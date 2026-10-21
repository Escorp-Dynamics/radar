#!/usr/bin/env python3
"""
Извлекает трассы по номерам («М-4», «Р-22», «А-290» …) из региональных выгрузок OSM (Geofabrik .osm.pbf, ODbL)
в кэш tools/build_routes.py — в том же формате, что отдаёт Overpass (way + geometry), когда серверы перегружены.

Трасса собирается из путей (way) с этим номером в теге ref (в т. ч. «М-4;Е115») и из участников отношений
route=road с этим ref.

Использование:
    python3 tools/pbf_route_refs.py <кэш> <файл.osm.pbf> [ещё .pbf …]
Требуется pyosmium. Список номеров — ROADS из build_routes.py; обрабатываются только номера без кэша.
"""
import json
import os
import re
import sys

import osmium

CACHE = sys.argv[1]
PBFS = sys.argv[2:]
here = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(here, "build_routes.py"), encoding="utf-8").read()
ns = {}
exec(src[src.index("ROADS = {"):src.index("SERVERS =")], ns)  # noqa: S102 — локальный скрипт сборки
ROADS = ns["ROADS"]
todo = [r for r in ROADS if not os.path.exists(os.path.join(CACHE, r + ".json"))]
if not todo:
    print("all roads cached")
    sys.exit(0)
print("roads to extract:", ", ".join(todo))


def norm_ref(r):
    r = r.strip().upper().replace(" ", "").replace("M", "М").replace("P", "Р").replace("A", "А")
    m = re.match(r"^([МРА])-?(\d+)$", r)
    return f"{m.group(1)}-{m.group(2)}" if m else None


WANT = set(todo)


class RelPass(osmium.SimpleHandler):
    """Первый проход: участники отношений route=road с нужными номерами."""

    def __init__(self):
        super().__init__()
        self.member_ref = {}

    def relation(self, r):
        if r.tags.get("route") != "road":
            return
        refs = {norm_ref(x) for x in r.tags.get("ref", "").split(";")}
        hit = refs & WANT
        if not hit:
            return
        ref = next(iter(hit))
        for m in r.members:
            if m.type == "w":
                self.member_ref.setdefault(m.ref, ref)


class WayPass(osmium.SimpleHandler):
    def __init__(self, member_ref):
        super().__init__()
        self.member_ref = member_ref
        self.out = {r: {} for r in WANT}

    def way(self, w):
        refs = set()
        if "ref" in w.tags:
            refs = {norm_ref(x) for x in w.tags["ref"].replace(",", ";").split(";")} & WANT
        if w.id in self.member_ref:
            refs.add(self.member_ref[w.id])
        if not refs or "highway" not in w.tags:
            return
        try:
            geom = [{"lat": n.lat, "lon": n.lon} for n in w.nodes]
        except osmium.InvalidLocationError:
            return
        if len(geom) < 2:
            return
        for r in refs:
            self.out[r][w.id] = {"type": "way", "id": w.id, "geometry": geom, "tags": {"highway": w.tags["highway"], "ref": w.tags.get("ref", r)}}


for pbf in PBFS:
    print("reading", pbf)
rp = RelPass()
for pbf in PBFS:
    rp.apply_file(pbf)
print("relation members:", len(rp.member_ref))
wp = WayPass(rp.member_ref)
for pbf in PBFS:
    wp.apply_file(pbf, locations=True, idx="flex_mem")
for r, ways in wp.out.items():
    if not ways:
        print("  empty", r)
        continue
    json.dump({"elements": list(ways.values())}, open(os.path.join(CACHE, r + ".json"), "w"))
    print("  wrote", r, len(ways), "ways")
