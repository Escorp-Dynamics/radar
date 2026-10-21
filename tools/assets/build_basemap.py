#!/usr/bin/env python3
"""
Собирает встроенную векторную подложку Radar.Core/Assets/basemap.bin.gz из данных Natural Earth 1:10m (public domain).

Использование:
    python3 tools/build_basemap.py <папка с geojson Natural Earth> [папка с районами OSM]

Нужные файлы (https://github.com/nvkelso/natural-earth-vector/tree/master/geojson):
    ne_10m_admin_0_countries_rus.geojson, ne_10m_admin_1_states_provinces.geojson,
    ne_10m_lakes.geojson, ne_10m_rivers_lake_centerlines.geojson, ne_10m_populated_places.geojson

Формат (little-endian):
    "RBM2", затем записи: u8 kind, u8 lod, str id, str name, f32 labelLat, f32 labelLon, i32 rank,
    u32 parts, для каждой части: u32 n, i32 lat·1e5, i32 lon·1e5, затем (n−1) пар varint-zigzag дельт. kind 255 — конец.
    str = u16 длина + UTF-8.
"""
import gzip
import json
import os
import struct
import sys
import time

_T0 = time.time()


def _tick(what):
    print(f"[{time.time() - _T0:6.0f} s] {what}", flush=True)

SRC = sys.argv[1] if len(sys.argv) > 1 else "/tmp/ne"
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "Radar.Core", "Assets", "basemap.bin.gz")

BBOX = (10.0, 34.0, 180.0, 82.0)  # lon0, lat0, lon1, lat1 — вся Россия до Чукотки
LODS = [(0, 0.03), (1, 0.0025)]   # (уровень, допуск упрощения в градусах)

KIND_LAND, KIND_COUNTRY_BORDER_RU, KIND_REGION, KIND_FOREIGN_REGION, KIND_LAKE, KIND_RIVER, KIND_CITY, KIND_RU_OUTLINE, KIND_DISTRICT = 0, 1, 2, 3, 4, 5, 6, 7, 8

# ISO 3166-2 Natural Earth → id региона в Gazetteer.cs
REGION_IDS = {
    "RU-BEL": "belgorod", "RU-KRS": "kursk", "RU-BRY": "bryansk", "RU-ORL": "orel", "RU-VOR": "voronezh",
    "RU-KDA": "krasnodar", "RU-ROS": "rostov", "UA-43": "crimea", "UA-40": "sevastopol", "UA-65": "kherson",
    "UA-23": "zaporozhye", "UA-09": "lnr", "UA-14": "dnr", "RU-LIP": "lipetsk", "RU-TAM": "tambov", "RU-TUL": "tula",
    "RU-KLU": "kaluga", "RU-SMO": "smolensk", "RU-MOW": "moscow_obl", "RU-MOS": "moscow", "RU-RYA": "ryazan",
    "RU-PNZ": "penza", "RU-SAR": "saratov", "RU-VGG": "volgograd", "RU-AST": "astrakhan", "RU-KL": "kalmykia",
    "RU-SAM": "samara", "RU-ULY": "ulyanovsk", "RU-MO": "mordovia", "RU-NIZ": "nizhny", "RU-VLA": "vladimir",
    "RU-IVA": "ivanovo", "RU-YAR": "yaroslavl", "RU-TVE": "tver", "RU-PSK": "pskov", "RU-NGR": "novgorod",
    "RU-LEN": "leningrad", "RU-SPE": "spb", "RU-KOS": "kostroma", "RU-VLG": "vologda", "RU-KGD": "kaliningrad",
    "RU-CU": "chuvashia", "RU-TA": "tatarstan", "RU-BA": "bashkiria", "RU-ORE": "orenburg", "RU-UD": "udmurtia",
    "RU-ME": "mari", "RU-PER": "perm", "RU-KIR": "kirov", "RU-STA": "stavropol", "RU-AD": "adygea",
    "RU-KC": "kchr", "RU-KB": "kbr", "RU-SE": "ossetia", "RU-IN": "ingushetia", "RU-CE": "chechnya",
    "RU-DA": "dagestan", "RU-CHE": "chelyabinsk", "RU-SVE": "sverdlovsk", "RU-MUR": "murmansk", "RU-KR": "karelia",
    "RU-ARK": "arkhangelsk", "RU-KO": "komi", "RU-TYU": "tyumen", "RU-KHM": "khmao", "RU-YAN": "yanao", "RU-NEN": "nao",
    "RU-KGN": "kurgan", "RU-OMS": "omsk", "RU-NVS": "novosibirsk", "RU-TOM": "tomsk", "RU-KEM": "kemerovo", "RU-ALT": "altai_krai",
    "RU-KYA": "krasnoyarsk", "RU-IRK": "irkutsk", "RU-KK": "khakassia", "RU-TY": "tuva", "RU-AL": "altai_rep",
    "RU-BU": "buryatia", "RU-ZAB": "zabaykalsky", "RU-AMU": "amur", "RU-YEV": "jao", "RU-KHA": "khabarovsk",
    "RU-PRI": "primorye", "RU-MAG": "magadan", "RU-SAK": "sakhalin", "RU-CHU": "chukotka", "RU-SA": "yakutia",
    "RU-KAM": "kamchatka",
}
# Новые регионы, которые в NE числятся за Украиной
RU_ADMIN_FROM_UA = {"UA-43", "UA-40", "UA-65", "UA-23", "UA-09", "UA-14"}


def polys_of(geom):
    if geom is None:
        return []
    if geom["type"] == "Polygon":
        return [geom["coordinates"]]
    if geom["type"] == "MultiPolygon":
        return geom["coordinates"]
    if geom["type"] == "LineString":
        return [[geom["coordinates"]]]
    if geom["type"] == "MultiLineString":
        return [[l] for l in geom["coordinates"]]
    return []


def bbox_hit(ring):
    xs = [p[0] for p in ring]
    ys = [p[1] for p in ring]
    return max(xs) >= BBOX[0] and min(xs) <= BBOX[2] and max(ys) >= BBOX[1] and min(ys) <= BBOX[3]


try:
    from shapely.geometry import LineString as _SL
except ImportError:
    _SL = None


def simplify(pts, tol):
    """Дуглас — Пекер (GEOS, если есть shapely; иначе итеративно на Python — в десятки раз медленнее)."""
    if len(pts) < 4 or tol <= 0:
        return pts
    if _SL is not None:
        return list(_SL(pts).simplify(tol, preserve_topology=False).coords)
    keep = [False] * len(pts)
    keep[0] = keep[-1] = True
    stack = [(0, len(pts) - 1)]
    t2 = tol * tol
    while stack:
        a, b = stack.pop()
        ax, ay = pts[a]
        bx, by = pts[b]
        dx, dy = bx - ax, by - ay
        L = dx * dx + dy * dy
        best, bi = -1.0, -1
        for i in range(a + 1, b):
            px, py = pts[i]
            if L == 0:
                d = (px - ax) ** 2 + (py - ay) ** 2
            else:
                t = ((px - ax) * dx + (py - ay) * dy) / L
                t = 0 if t < 0 else 1 if t > 1 else t
                d = (px - ax - t * dx) ** 2 + (py - ay - t * dy) ** 2
            if d > best:
                best, bi = d, i
        if best > t2:
            keep[bi] = True
            stack.append((a, bi))
            stack.append((bi, b))
    return [p for p, k in zip(pts, keep) if k]


# RBM2: координаты — целые в единицах 1e-5° (~1 м), первая точка части абсолютно (2×i32), остальные — дельты
# varint-zigzag: вдвое компактнее float32 и лучше сжимается gzip
out = bytearray(b"RBM2")
Q = 1e5


def _varint(v):
    v = (v << 1) ^ (v >> 63)  # zigzag
    while v >= 0x80:
        out.append((v & 0x7F) | 0x80)
        v >>= 7
    out.append(v)
stats = {}


def wstr(s):
    b = (s or "").encode("utf-8")
    out.extend(struct.pack("<H", len(b)))
    out.extend(b)


def emit(kind, lod, fid, name, label, rank, parts, closed):
    parts2 = []
    for ring in parts:
        ring = [(p[0], p[1]) for p in ring]
        if kind != KIND_CITY:
            # Чукотка пересекает 180-й меридиан — отсекаем отрицательные долготы
            if all(x < 0 for x, _ in ring):
                continue
            ring = [(x if x >= 0 else 180.0, y) for x, y in ring]
            ring = simplify(ring, LODS[lod][1])
            if len(ring) < (3 if closed else 2):
                continue
        parts2.append(ring)
    if not parts2:
        return
    out.extend(struct.pack("<BB", kind, lod))
    wstr(fid)
    wstr(name)
    out.extend(struct.pack("<ffi", label[0], label[1], int(rank)))
    out.extend(struct.pack("<I", len(parts2)))
    for ring in parts2:
        q = [(int(round(y * Q)), int(round(x * Q))) for x, y in ring]
        # совпавшие после округления соседние точки — лишние
        q = [pt for i, pt in enumerate(q) if i == 0 or pt != q[i - 1]]
        out.extend(struct.pack("<I", len(q)))
        out.extend(struct.pack("<ii", q[0][0], q[0][1]))
        for (la0, lo0), (la1, lo1) in zip(q, q[1:]):
            _varint(la1 - la0)
            _varint(lo1 - lo0)
    stats[kind] = stats.get(kind, 0) + sum(len(r) for r in parts2)


def load(name):
    with open(os.path.join(SRC, name), encoding="utf-8") as f:
        return json.load(f)["features"]


# ---- уточнённый берег (OSM, tools/osm_land.py): в прибрежной рамке суша и регионы обрезаются по берегу OSM ----
# BASEMAP_COAST=/tmp/osm_land_coast.json; без файла или без shapely подложка строится по NE как прежде
COAST = None
_coast_fn = os.environ.get("BASEMAP_COAST")
if _coast_fn and os.path.exists(_coast_fn):
    try:
        from shapely.geometry import Polygon as _Poly, box as _box
        from shapely.ops import unary_union as _union
        _c = json.load(open(_coast_fn))
        COAST = (_union([_box(*f) for f in _c["frames"]]), _union([_Poly(p[0], p[1:]) for p in _c["polygons"]]).buffer(0))
        from shapely import prepare as _prepare
        _prepare(COAST[0])
        print("coast: OSM land in", _c["frames"])
    except ImportError:
        print("coast: shapely не установлен — берег NE")


# суша соседних стран NE (для «добора» берега страны из OSM без захвата чужой территории): код → геометрия
_OTHERS = {}
_LAND_IN_FRAME = [None]


def coastal(rings, holes_ok=False, grow=None):
    """
    Контуры полигона (внешние кольца, lon/lat) с уточнённым берегом: внутри прибрежной рамки — пересечение с сушей
    OSM, вне рамки — как есть. Возвращает список колец; дыры (лиманы внутри суши) — отдельными кольцами,
    подложка рисуется с правилом EvenOdd.
    grow — код страны (для слоя суши): берег NE бывает и глубже настоящего (Север) — тогда суша OSM в полосе ~15 км
    вокруг контура страны добавляется к ней (кроме территории соседних стран), берег берётся целиком из OSM.
    """
    if COAST is None:
        return rings
    frame, land = COAST
    out_rings = []
    for ring in rings:
        if len(ring) < 4:
            continue
        g = _Poly(ring).buffer(0)
        if not g.intersects(frame):
            out_rings.append(ring)
            continue
        inside = g.intersection(frame).intersection(land)
        if grow is not None:
            if _LAND_IN_FRAME[0] is None:
                _LAND_IN_FRAME[0] = land.intersection(frame)
            near = _LAND_IN_FRAME[0].intersection(g.simplify(0.01).buffer(0.15, quad_segs=2))
            others = _OTHERS.get(grow)
            if others is not None:
                near = near.difference(others)
            inside = _union([inside, near])
        outside = g.difference(frame)
        res = _union([inside, outside]).buffer(0)
        for p in getattr(res, "geoms", [res]):
            if p.geom_type != "Polygon" or p.area < 1e-6:
                continue
            out_rings.append(list(p.exterior.coords))
            if holes_ok:
                out_rings.extend(list(i.coords) for i in p.interiors if _Poly(i).area > 1e-5)
    return out_rings


_tick("start land")
# 1. Суша (страны) и граница России
# В NE части ЛНР/ДНР в границах 2014 года — отдельные безымянные полигоны: присоединяем их к Украине, чтобы на суше
# не оставалось «дыр» с линиями старого разграничения (регионы ЛНР/ДНР рисуются сверху целиком).
DONBASS_2014 = (37.4, 46.9, 40.3, 48.8)
countries = load("ne_10m_admin_0_countries_rus.geojson")
for f in countries:
    p = f["properties"]
    if p.get("ADM0_A3") is None:
        xs = [pt[0] for poly in polys_of(f["geometry"]) for pt in poly[0]]
        ys = [pt[1] for poly in polys_of(f["geometry"]) for pt in poly[0]]
        if xs and DONBASS_2014[0] <= min(xs) and max(xs) <= DONBASS_2014[2] and DONBASS_2014[1] <= min(ys) and max(ys) <= DONBASS_2014[3]:
            p["ADM0_A3"], p["NAME"], p["_merge"] = "UKR", "Ukraine", True
try:
    from shapely.geometry import shape as _shape, mapping as _mapping
    from shapely.ops import unary_union as _uu
    ukr = [f for f in countries if f["properties"].get("ADM0_A3") == "UKR"]
    if len(ukr) > 1:
        merged = _uu([_shape(f["geometry"]).buffer(0.0005) for f in ukr]).buffer(-0.0005)
        base = next(f for f in ukr if not f["properties"].get("_merge"))
        base["geometry"] = _mapping(merged)
        countries = [f for f in countries if not f["properties"].get("_merge")]
        print("land: Donbass 2014 pieces merged into UKR")
except ImportError:
    countries = [f for f in countries if not f["properties"].get("_merge")]
# суша записывается после районов (раздел 1a): на Севере её берег берётся из контуров районов


# Районы, которые выгрузка Overpass вернула в файлах двух регионов (общая граница): настоящий регион.
# Остальные дубли решаются по большинству площади в контуре NE.
DISTRICT_OWNER = {
    "городской округ саров": "nizhny",
    "александровский муниципальный округ": "vladimir",
    "юрьев-польский муниципальный округ": "vladimir",
    "переславль-залесский муниципальный округ": "yaroslavl",
}


def load_districts():
    """
    Районы OSM по регионам без дублей: {rid: [(name, rings, shapely)]}. Район, попавший в файлы двух регионов
    (почти та же геометрия), остаётся только у настоящего региона. Районы, которые NE относит к соседу
    (Сокольский округ передан из Ивановской в Нижегородскую область в 2010 г.), не трогаем — OSM актуальнее.
    """
    d = sys.argv[2] if len(sys.argv) > 2 else None
    if not d or not os.path.isdir(d):
        return {}, {}
    try:
        from shapely.geometry import Polygon as P, shape as S
        from shapely.ops import unary_union as U
        from shapely.strtree import STRtree
    except ImportError:
        return {}, {}
    ne = {}
    for f in load("ne_10m_admin_1_states_provinces.geojson"):
        iso = f["properties"]["iso_3166_2"]
        if iso in REGION_IDS:
            ne[REGION_IDS[iso]] = S(f["geometry"]).buffer(0)
    # суша NE: границы островных районов OSM (Курилы, Командоры, Арктика) проходят по морю — обрезаем их по суше
    # (только регионы с океанским побережьем; суша — все страны, чтобы не терять приграничные земли)
    SEA_CLIP = {"sakhalin", "kamchatka", "chukotka", "magadan", "khabarovsk", "primorye", "yakutia", "krasnoyarsk",
                "arkhangelsk", "nao", "yanao", "murmansk"}
    land_ne = U([S(f["geometry"]).buffer(0) for f in load("ne_10m_admin_0_countries_rus.geojson")])
    land_ne_wide = None
    items = []
    for fn in sorted(os.listdir(d)):
        if not fn.endswith(".json"):
            continue
        rid = fn[:-5]
        for dd in json.load(open(os.path.join(d, fn), encoding="utf-8")):
            rings = [r for r in dd["rings"] if len(r) >= 4]
            g = U([P([(lon, lat) for lat, lon in r]).buffer(0) for r in rings]) if rings else None
            raw = dd["rings"]
            if rid in SEA_CLIP and g is not None and not g.is_empty and g.area > 0.5:
                if land_ne_wide is None:
                    # буфер суши всей Евразии дорогой — один раз на сборку, а не на каждый район
                    land_ne_wide = land_ne.buffer(0.02)
                on_land = g.intersection(land_ne_wide)
                if on_land.area < g.area * 0.6:
                    g = on_land.buffer(0)
                    raw = [[(y, x) for x, y in pp.exterior.coords] for pp in getattr(g, "geoms", [g])
                           if pp.geom_type == "Polygon" and pp.area > 1e-5]
                    print("district clipped to land:", dd["name"], rid)
            items.append([rid, dd["name"], raw, g, True])
    geoms = [it[3] for it in items if it[3] is not None and not it[3].is_empty]
    idx = [i for i, it in enumerate(items) if it[3] is not None and not it[3].is_empty]
    tree = STRtree(geoms)
    for k, i in enumerate(idx):
        gi = items[i][3]
        for kj in tree.query(gi):
            j = idx[kj]
            if j <= i or items[j][0] == items[i][0]:
                continue
            gj = items[j][3]
            inter = gi.intersection(gj).area
            if inter / (gi.area + gj.area - inter) < 0.9:
                continue
            name = items[i][1].lower().replace("ё", "е")
            owner = DISTRICT_OWNER.get(name)
            if owner is None:
                def share(r):
                    return gi.intersection(ne[r]).area if r in ne else 0
                owner = max((items[i][0], items[j][0]), key=share)
            loser = j if items[i][0] == owner else i
            items[loser][4] = False
            print("district duplicate:", items[i][1], items[i][0], "/", items[j][0], "→", owner)
    res = {}
    for rid, name, rings, g, keep in items:
        if keep:
            res.setdefault(rid, []).append((name, rings, g))
    return res, ne


_tick("load_districts")
DISTRICTS_BY_REGION, NE_REGIONS = load_districts()


def district_regions():
    """
    Контуры регионов как объединение их районов OSM (tools/fetch_osm_districts.py): границы регионов совпадают
    с границами районов. Регион берётся из районов, если районы (всех регионов вместе) покрывают его контур NE
    почти полностью — иначе данных о районах не хватает и остаётся контур NE. id → shapely-геометрия.
    """
    if not DISTRICTS_BY_REGION:
        return {}
    from shapely.ops import unary_union as U
    ne = NE_REGIONS
    unions = {}
    for rid, ds in DISTRICTS_BY_REGION.items():
        gs = [g for _, _, g in ds if g is not None and not g.is_empty and g.area > 0]
        if gs:
            # мелкие щели между районами (разная точность контуров OSM) закрываем буфером туда-обратно
            # контуры OSM подробнее нужного (метры) — упрощаем до ~50 м: буфер и объединение в разы быстрее,
            # а итог всё равно упрощается до 250 м при записи
            unions[rid] = U([g.simplify(0.0005, preserve_topology=True).buffer(0.002, quad_segs=2) for g in gs]).buffer(-0.002, quad_segs=2)
    every = U(list(unions.values()))
    land = COAST[1] if COAST else None
    frame = COAST[0] if COAST else None
    res = {}
    for rid, u in unions.items():
        if rid not in ne:
            continue
        g = ne[rid]
        miss = g.difference(every)
        if land is not None:
            miss = miss.difference(miss.intersection(frame).difference(land))  # вода в прибрежной рамке — не пропуск
        ratio = u.area / g.area
        if miss.area / g.area > 0.03 or not 0.6 < ratio < 1.7:
            print("region from districts: skip", rid, "missing", round(miss.area / g.area, 3), "ratio", round(ratio, 2))
            continue
        res[rid] = u
    # Севастополь без районов в выгрузке: его часть полуострова — всё, что не покрыто районами Крыма
    if "crimea" in res and "sevastopol" in ne and "sevastopol" not in res:
        sev = ne["sevastopol"].union(ne["crimea"]).difference(res["crimea"]).intersection(ne["sevastopol"].buffer(0.05))
        sev = U([p for p in getattr(sev, "geoms", [sev]) if p.geom_type == "Polygon" and p.area > 1e-4])
        if not sev.is_empty:
            res["sevastopol"] = sev
    print("regions from districts:", len(res))
    # те же контуры — для слоя ориентиров (общие границы регионов в build_routes.py)
    out_fn = os.environ.get("REGIONS_OUT")
    if out_fn:
        from shapely.geometry import mapping as M
        json.dump({k: M(v) for k, v in res.items()}, open(out_fn, "w"))
    return res


_tick("district_regions")
FROM_DISTRICTS = district_regions()

# ---- 1a. Суша (после районов: порядок записей в файле не важен, но так суша видит уточнённый берег)
if COAST is not None:
    from shapely.geometry import shape as _cs
    _frame = COAST[0]
    _cg = {}
    for f in countries:
        code = f["properties"].get("ADM0_A3")
        if code is None:
            continue
        g = _cs(f["geometry"]).buffer(0)
        if g.intersects(_frame.buffer(0.3)):
            _cg[code] = _union([_cg[code], g]) if code in _cg else g
    for code in _cg:
        _OTHERS[code] = _union([g for c, g in _cg.items() if c != code]).intersection(_frame) if len(_cg) > 1 else None
for f in countries:
    p = f["properties"]
    if p.get("ADM0_A3") is None:
        continue
    polys = [poly for poly in polys_of(f["geometry"]) if bbox_hit(poly[0])]
    if not polys:
        continue
    is_ru = p["ADM0_A3"] == "RUS"
    rings = coastal([poly[0] for poly in polys], holes_ok=True, grow=p["ADM0_A3"])
    for lod, _ in LODS:
        emit(KIND_LAND, lod, p["ADM0_A3"], p.get("NAME_RU") or p["NAME"], (0, 0), 1 if is_ru else 0, rings, True)

def _signed_area(coords):
    c = list(coords)
    return sum(x0 * y1 - x1 * y0 for (x0, y0), (x1, y1) in zip(c, c[1:])) / 2


_tick("land emitted")
# 2. Регионы
ru_rings = []
for f in load("ne_10m_admin_1_states_provinces.geojson"):
    p = f["properties"]
    iso = p["iso_3166_2"]
    if p["adm0_a3"] not in ("RUS", "UKR", "BLR", "KAZ", "GEO") and iso not in RU_ADMIN_FROM_UA:
        continue
    polys = [poly for poly in polys_of(f["geometry"]) if bbox_hit(poly[0])]
    if not polys:
        continue
    russian = p["adm0_a3"] == "RUS" or iso in RU_ADMIN_FROM_UA
    kind = KIND_REGION if russian else KIND_FOREIGN_REGION
    rid = REGION_IDS.get(iso, iso)
    name = p.get("name_ru") or p["name"]
    if rid in FROM_DISTRICTS:
        g = FROM_DISTRICTS[rid]
        polys = [[list(pp.exterior.coords)] for pp in getattr(g, "geoms", [g])]
        # анклавы (Адыгея в Краснодарском крае, Москва в области) — дырами, заливка EvenOdd
        polys += [[list(i.coords)] for pp in getattr(g, "geoms", [g]) for i in pp.interiors if abs(_signed_area(i.coords)) > 1e-3]
    rings = coastal([poly[0] for poly in polys], holes_ok=True)
    if russian:
        ru_rings.extend(rings)
    for lod, _ in LODS:
        emit(kind, lod, rid, name, (p["latitude"], p["longitude"]), p.get("labelrank") or 5, rings, True)

_tick("regions emitted")
# 2a. Внешний контур России (с новыми регионами)
OUTLINE_DONE = False
try:
    from shapely.geometry import Polygon as _OP, LineString as _OL
    from shapely.ops import unary_union as _OU, linemerge as _OM
    _polys = []
    for r in ru_rings:
        if len(r) >= 4:
            g = _OP(r).buffer(0)
            if not g.is_empty:
                # контур страны пишется с допуском ≥ 250 м — исходная детализация в метрах тут только замедляет
                _polys.append(g.simplify(0.0008, preserve_topology=True).buffer(0.003, quad_segs=2))
    _ru = _OU(_polys).buffer(-0.003, quad_segs=2)
    _lines = []
    for pp in getattr(_ru, "geoms", [_ru]):
        if pp.area < 1e-4:
            continue
        ext = list(pp.exterior.coords)
        # участок по 180-му меридиану (Чукотка обрезана по нему) — не граница страны
        cur = []
        for pt in ext:
            if pt[0] >= 179.99:
                if len(cur) >= 2:
                    _lines.append(cur)
                cur = []
                continue
            cur.append(pt)
        if len(cur) >= 2:
            _lines.append(cur)
    for lod, _ in LODS:
        emit(KIND_RU_OUTLINE, lod, "RU", "", (0, 0), 0, _lines, False)
    print("outline: union contour parts", len(_lines))
    OUTLINE_DONE = True
except ImportError:
    pass


def key(pt):
    return (round(pt[0], 5), round(pt[1], 5))

edge_count = {}
for ring in ru_rings:
    for a, b in zip(ring, ring[1:]):
        ka, kb = key(a), key(b)
        if ka == kb:
            continue
        e = (ka, kb) if ka < kb else (kb, ka)
        edge_count[e] = edge_count.get(e, 0) + 1
boundary = [e for e, c in edge_count.items() if c == 1 and max(e[0][0], e[1][0]) < 98]
adj = {}
for a, b in boundary:
    adj.setdefault(a, []).append(b)
    adj.setdefault(b, []).append(a)
used = set()
chains = []
for a, b in boundary:
    e = (a, b)
    if e in used:
        continue
    used.add(e)
    chain = [a, b]
    for forward in (True, False):
        while True:
            tail = chain[-1] if forward else chain[0]
            prev = chain[-2] if forward else chain[1]
            nbrs = [n for n in adj[tail] if n != prev]
            if len(adj[tail]) != 2 or not nbrs:
                break
            n = nbrs[0]
            ee = (tail, n) if tail < n else (n, tail)
            if ee in used:
                break
            used.add(ee)
            if forward:
                chain.append(n)
            else:
                chain.insert(0, n)
    chains.append(chain)
if not OUTLINE_DONE:
    print("outline: boundary edges", len(boundary), "of", len(edge_count), "chains", len(chains))
    for lod, _ in LODS:
        emit(KIND_RU_OUTLINE, lod, "RU", "", (0, 0), 0, [c for c in chains if len(c) >= 2], False)

_tick("outline")
# 3. Озёра
for f in load("ne_10m_lakes.geojson"):
    p = f["properties"]
    if (p.get("scalerank") or 10) > 8:
        continue
    polys = [poly for poly in polys_of(f["geometry"]) if bbox_hit(poly[0])]
    for lod, _ in LODS:
        emit(KIND_LAKE, lod, "", p.get("name") or "", (0, 0), p.get("scalerank") or 0, [poly[0] for poly in polys], True)

# 4. Реки
for f in load("ne_10m_rivers_lake_centerlines.geojson"):
    p = f["properties"]
    sr = p.get("scalerank") or 10
    if sr > 7 or p.get("featurecla") != "River":
        continue
    lines = [l[0] for l in polys_of(f["geometry"]) if bbox_hit(l[0])]
    for lod, _ in LODS:
        if lod == 0 and sr > 4:
            continue
        emit(KIND_RIVER, lod, "", p.get("name") or "", (0, 0), sr, lines, False)

# 5. Города
for f in load("ne_10m_populated_places.geojson"):
    p = f["properties"]
    lat, lon = p["LATITUDE"], p["LONGITUDE"]
    if not (BBOX[0] <= lon <= BBOX[2] and BBOX[1] <= lat <= BBOX[3]):
        continue
    if p["ADM0_A3"] not in ("RUS", "UKR", "BLR", "KAZ", "GEO", "AZE", "ARM", "TUR", "POL", "LTU", "LVA", "EST", "FIN",
                            "MDA", "ROU"):
        continue
    sr = p.get("SCALERANK") or 10
    if sr > 9:
        continue
    name = p.get("NAME_RU") or p["NAME"]
    # прежние названия городов Украины — как в сообщениях каналов
    if p["ADM0_A3"] == "UKR":
        name = {"Днепр": "Днепропетровск", "Кропивницкий": "Кировоград", "Каменское": "Днепродзержинск"}.get(name, name)
    cap = 1 if p.get("ADM0CAP") else 0
    emit(KIND_CITY, 0, p["ADM0_A3"], name, (lat, lon), sr - cap * 3, [[(lon, lat)]], False)

_tick("cities")
# 6. Районы/округа (OSM admin_level=6, tools/fetch_osm_districts.py) — для точной подсветки тревог по районам
DISTRICTS = sys.argv[2] if len(sys.argv) > 2 else None


def district_key(name):
    """Ключ привязки: «Валуйский район», «Валуйский муниципальный округ» → «валуйский»."""
    n = name.lower().replace("ё", "е").strip()
    for suf in (" муниципальный район", " муниципальный округ", " городской округ", " район", " округ", " городской"):
        if n.endswith(suf):
            n = n[: -len(suf)]
            break
    for pre in ("муниципальный округ ", "муниципальный район ", "поселение ", "район ", "округ ", "городской округ ", "город ", "муниципальное образование ", "го "):
        if n.startswith(pre):
            n = n[len(pre):]
    return n.strip()


if DISTRICTS_BY_REGION:
    cnt = 0
    for rid in sorted(DISTRICTS_BY_REGION):
        for name, raw_rings, _ in DISTRICTS_BY_REGION[rid]:
            rings = [[(lon, lat) for lat, lon in r] for r in raw_rings]
            rings = coastal([r for r in rings if len(r) >= 4])
            if not rings:
                continue
            # центр подписи — центр масс самого большого кольца
            big = max(rings, key=len)
            cx = sum(x for x, _ in big) / len(big)
            cy = sum(y for _, y in big) / len(big)
            for lod, _ in LODS:
                emit(KIND_DISTRICT, lod, rid + "|" + district_key(name), name, (cy, cx), 0, rings, True)
            cnt += 1
    print("districts", cnt)

_tick("districts emitted")
out.extend(struct.pack("<B", 255))
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with gzip.open(OUT, "wb", compresslevel=9) as g:
    g.write(bytes(out))
print("raw", len(out), "gz", os.path.getsize(OUT), "points by kind", stats)
