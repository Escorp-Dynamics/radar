#!/usr/bin/env python3
"""
Акватории, которых нет в Natural Earth ocean: Сиваш отделён от Азовского моря Арабатской стрелкой, а в NE
его площадь целиком покрыта полигонами регионов. Контур — из отношения OSM natural=bay (Overpass, ODbL);
результат кладётся в кэш, который читает build_routes.py.

Использование (нужен shapely):
    python3 tools/fetch_water.py [/tmp/osm_routes/_water.json]
Формат: {"sea:sivash": [[[lat, lon], ...], ...]} — внешние контуры полигонов.
"""
import json
import sys
import urllib.parse
import urllib.request

from shapely.geometry import LineString
from shapely.ops import polygonize, unary_union

TARGETS = {5925937: "sea:sivash"}  # OSM relation natural=bay → id в слое ориентиров
SERVERS = ["https://overpass.private.coffee/api/interpreter", "https://overpass-api.de/api/interpreter",
           "https://maps.mail.ru/osm/tools/overpass/api/interpreter"]

dst = sys.argv[1] if len(sys.argv) > 1 else "/tmp/osm_routes/_water.json"
res = {}
for rid, fid in TARGETS.items():
    q = urllib.parse.urlencode({"data": f"[out:json][timeout:120];relation({rid});out geom;"}).encode()
    data = None
    for s in SERVERS:
        try:
            data = json.load(urllib.request.urlopen(s, q, timeout=180))
            break
        except Exception as e:  # перегруженное зеркало — следующее
            print("  ", s, e, file=sys.stderr)
    if data is None:
        sys.exit(f"не удалось загрузить relation {rid}")
    rel = data["elements"][0]
    lines = [LineString([(g["lon"], g["lat"]) for g in m["geometry"]]) for m in rel["members"]
             if m["type"] == "way" and m.get("role") in ("outer", "") and len(m.get("geometry", [])) >= 2]
    polys = [p for p in polygonize(unary_union(lines)) if p.area > 1e-3]
    res[fid] = [[[round(y, 5), round(x, 5)] for x, y in p.exterior.coords] for p in polys]
    print(fid, "rings", len(polys), "pts", sum(len(r) for r in res[fid]))
json.dump(res, open(dst, "w"))
