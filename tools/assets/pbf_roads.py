#!/usr/bin/env python3
"""
Извлекает дороги из региональных выгрузок OSM (Geofabrik .osm.pbf, ODbL) в кэш tools/build_roadgraph.py
в том же формате, что отдаёт Overpass (way + nodes + geometry) — когда публичные серверы Overpass перегружены.

Использование:
    python3 tools/pbf_roads.py <кэш> <файл.osm.pbf> [ещё .pbf …]
Требуется pyosmium (pip install osmium). Тайлы и классы дорог берутся из TILES в build_roadgraph.py;
обрабатываются только тайлы, которых ещё нет в кэше.
"""
import importlib.util
import json
import os
import sys

import osmium

CACHE = sys.argv[1]
PBFS = sys.argv[2:]
here = os.path.dirname(os.path.abspath(__file__))

# TILES из build_roadgraph.py без выполнения сборки
src = open(os.path.join(here, "build_roadgraph.py"), encoding="utf-8").read()
ns = {}
exec(src[src.index("FULL ="):src.index("CLASSES =")], ns)  # noqa: S102 — локальный скрипт сборки
TILES = ns["TILES"]
todo = {name: spec for name, spec in TILES.items() if not os.path.exists(os.path.join(CACHE, name + ".json"))}
if not todo:
    print("all tiles cached")
    sys.exit(0)
print("tiles to extract:", ", ".join(todo))


def classes_of(spec):
    return set(spec[1].split("|"))


ALL_CLASSES = set()
for spec in todo.values():
    ALL_CLASSES |= classes_of(spec)


class Roads(osmium.SimpleHandler):
    def __init__(self):
        super().__init__()
        self.out = {name: [] for name in todo}

    def way(self, w):
        hw = w.tags.get("highway")
        if not hw:
            return
        base = hw.replace("_link", "")
        if base not in ALL_CLASSES:
            return
        try:
            geom = [(n.lat, n.lon) for n in w.nodes]
        except osmium.InvalidLocationError:
            return
        if len(geom) < 2:
            return
        lats = [g[0] for g in geom]
        lons = [g[1] for g in geom]
        mn_la, mx_la, mn_lo, mx_lo = min(lats), max(lats), min(lons), max(lons)
        for name, ((s, wl, n, e), cls) in todo.items():
            if base not in classes_of((None, cls)):
                continue
            if mx_la < s or mn_la > n or mx_lo < wl or mn_lo > e:
                continue
            self.out[name].append({
                "type": "way", "id": w.id,
                "nodes": [nd.ref for nd in w.nodes],
                "geometry": [{"lat": la, "lon": lo} for la, lo in geom],
                "tags": {"highway": hw, **({"ref": w.tags["ref"]} if "ref" in w.tags else {})},
            })


h = Roads()
for pbf in PBFS:
    print("reading", pbf)
    h.apply_file(pbf, locations=True, idx="flex_mem")

# тайл сохраняется, только если в нём есть данные (тайл мог попасть в другую выгрузку)
for name, ways in h.out.items():
    if not ways:
        print("  empty", name)
        continue
    seen = set()
    uniq = [w for w in ways if not (w["id"] in seen or seen.add(w["id"]))]
    json.dump({"elements": uniq}, open(os.path.join(CACHE, name + ".json"), "w"))
    print("  wrote", name, len(uniq), "ways")
