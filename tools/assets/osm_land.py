#!/usr/bin/env python3
"""
Суша по береговой линии OSM (ODbL) в прибрежной рамке Азовского/Чёрного морей — для уточнения берега подложки
(build_basemap.py) и акваторий (build_routes.py). У Natural Earth 1:10m берег грубый: лиманы, косы и заливы
смещены на километры.

Источник: https://osmdata.openstreetmap.de/download/land-polygons-split-4326.zip (распаковать).
Использование (нужны pyshp и shapely):
    python3 tools/osm_land.py <land_polygons.shp> [/tmp/osm_land_coast.json]
Формат: {"frames": [[lon0, lat0, lon1, lat1], ...], "polygons": [[[[lon, lat], ...] внешний, [...] дыры], ...]}
"""
import json
import sys

import shapefile
from shapely.geometry import box, shape, mapping
from shapely.ops import unary_union

# прибрежные рамки: Одесса — Крым — Азов — Черноморское побережье Кавказа до Батуми; Каспий (Дагестан, Калмыкия,
# дельта Волги, побережье Казахстана);
#   Балтика: Калининградская область (Калининградский и Куршский заливы, косы), Финский залив (Санкт-Петербург,
#   Кронштадт, Выборгский залив, Нарвский залив)
FRAMES = [(30.0, 41.3, 42.0, 47.6), (46.4, 41.0, 54.0, 47.3), (19.3, 54.2, 22.95, 55.45), (26.5, 59.2, 30.6, 60.8)]
TOL = 0.0006  # ~60 м: заметно точнее NE, но без лишних вершин
# Север и Дальний Восток (Кольский, Белое море, Карелия, Арктика, Камчатка, Охотское море, Приморье) — у NE берег там
# смещён на километры, а районы OSM уходят в море (морские части районов).
# Фьорды и шхеры очень изрезаны: допуск ~150 м, острова меньше ~0,2 км² не нужны
NORTH_FRAMES = [
    (28.0, 63.5, 46.0, 70.5), (46.0, 65.5, 62.0, 70.5), (62.0, 66.0, 80.0, 73.6),
    # Новая Земля, Земля Франца-Иосифа
    (44.0, 70.5, 70.0, 82.0),
    # Таймыр, Северная Земля, устье Енисея; море Лаптевых, Новосибирские острова; Восточно-Сибирское и Чукотское моря
    (80.0, 69.0, 115.0, 81.5), (115.0, 69.0, 150.0, 77.5), (150.0, 64.0, 180.0, 72.0),
    # острова Карского моря (Сибирякова, Уединения) и восточная часть Новосибирских островов
    (70.0, 73.6, 80.0, 81.5), (150.0, 72.0, 165.0, 77.5),
    # Берингово море, Камчатка, Охотское море, Сахалин, Курилы, Приморье
    (155.0, 50.0, 180.0, 64.0), (135.0, 52.0, 165.0, 62.5), (138.0, 43.0, 157.0, 55.0), (129.0, 42.0, 142.0, 49.0),
]
NORTH_TOL = 0.0015
NORTH_MIN_AREA = 5e-5

src = sys.argv[1]
dst = sys.argv[2] if len(sys.argv) > 2 else "/tmp/osm_land_coast.json"
polys = []
with shapefile.Reader(src) as r:
    for fr in FRAMES + NORTH_FRAMES:
        north = fr in NORTH_FRAMES
        frame = box(*fr)
        parts = []
        for sr in r.iterShapes(bbox=list(fr)):
            g = shape(sr.__geo_interface__)
            if g.intersects(frame):
                parts.append(g.intersection(frame))
        land = unary_union(parts).simplify(NORTH_TOL if north else TOL, preserve_topology=True)
        got = [p for p in getattr(land, "geoms", [land]) if p.geom_type == "Polygon" and p.area > (NORTH_MIN_AREA if north else 2e-6)]
        print("frame", fr, "polygons", len(got))
        polys.extend(got)
out = {"frames": [list(f) for f in FRAMES + NORTH_FRAMES], "polygons": [mapping(p)["coordinates"] for p in polys]}
json.dump(out, open(dst, "w"))
print("land polygons", len(polys), "vertices", sum(len(p.exterior.coords) + sum(len(i.coords) for i in p.interiors) for p in polys))
