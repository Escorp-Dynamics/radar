#!/usr/bin/env python3
"""
Собирает офлайн-слой «линейных ориентиров» для построения маршрутов Radar.Core/Assets/routes.bin.gz:
  • федеральные и региональные трассы (OSM route=road, ODbL) — по номеру («Р-280», «М-4») и названию («Таврида», «Дон»);
  • железные дороги (Natural Earth 10m railroads, public domain);
  • реки (Natural Earth 10m rivers, public domain) — с русскими названиями;
  • береговая линия морей (Natural Earth 10m coastline, public domain);
  • общие границы регионов и акватории Азовского/Чёрного морей (Natural Earth admin-1 и ocean; нужен shapely).

Использование:
    python3 tools/build_routes.py [папка Natural Earth] [кэш OSM]

Формат (little-endian): "RRT1", затем записи:
    u8 kind (1 трасса, 2 ж/д, 3 река, 4 берег, 5 общая граница регионов «border:a+b», 6 акватория — замкнутые
    контуры «sea:…»), str id, str names ('|'-разделённые, нижний регистр),
    u32 parts, для каждой части: u32 n, n*(f32 lat, f32 lon). kind 255 — конец. str = u16 длина + UTF-8.
"""
import gzip
import json
import math
import os
import struct
import sys
import time
import urllib.parse
import urllib.request

NE = sys.argv[1] if len(sys.argv) > 1 else "/tmp/ne"
CACHE = sys.argv[2] if len(sys.argv) > 2 else "/tmp/osm_routes"
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "Radar.Core", "Assets", "routes.bin.gz")
os.makedirs(CACHE, exist_ok=True)

# зона интереса: европейская часть РФ, новые регионы, Украина (источники пусков), Черное/Азовское/Каспийское моря
BBOX = (22.0, 40.5, 62.0, 62.0)  # lon0, lat0, lon1, lat1
TOL = 0.004                      # допуск упрощения, градусы (~300–400 м)

KIND_ROAD, KIND_RAIL, KIND_RIVER, KIND_COAST, KIND_BORDER, KIND_SEA = 1, 2, 3, 4, 5, 6

# Трассы: номер → дополнительные названия (как их пишут каналы)
ROADS = {
    # трассы, которые упоминают каналы тревог (по архиву сообщений), и магистрали европейской части
    "Р-280": ["новороссия", "а-280", "а280"],  # А-280 Ростов–Таганрог — прежний номер участка
    "А-240": [],
    "М-4": ["дон", "трасса дон"],
    "М-3": ["украина", "киевское шоссе", "киевская трасса"],
    "М-2": ["крым", "симферопольское шоссе", "симферопольская трасса"],
    "А-291": ["таврида", "трасса таврида"],
    "Р-150": [],
    "Р-260": [],
    "А-290": ["новороссийск керчь"],
    "Р-22": ["каспий"],
    "Р-217": ["кавказ"],
    "Р-120": ["а-141", "а141"],  # прежний номер Орёл–Брянск–Смоленск
    "Р-298": ["а-144", "а144"],  # прежний номер Курск–Воронеж–Борисоглебск
    "М-1": ["беларусь", "минское шоссе", "минка"],
    "М-11": ["нева"],
    "М-5": ["урал"],
    "М-7": ["волга"],
    "М-8": ["холмогоры"],
    "М-9": ["балтия", "новорижское шоссе"],
    "М-10": ["россия", "ленинградское шоссе"],
    "М-12": ["восток"],
    "Р-119": [],
    "Р-132": [],
    "Р-158": [],
    "Р-178": [],
    "Р-193": [],
    "Р-208": [],
    "Р-215": [],
    "Р-228": [],
    "А-108": ["мбк", "большое кольцо"],
    "А-113": ["цкад"],
    "А-130": ["варшавское шоссе"],
    "А-146": [],
    "А-147": [],
    "А-260": [],
    "А-270": [],
    "А-289": [],
    "А-300": [],
}

SERVERS = ["https://overpass.private.coffee/api/interpreter", "https://overpass-api.de/api/interpreter",
           "https://maps.mail.ru/osm/tools/overpass/api/interpreter"]
QUERY = """[out:json][timeout:600];
relation["route"="road"]["ref"="{ref}"];
way(r)["highway"];
out geom;"""


def overpass(q, tries=10):
    data = urllib.parse.urlencode({"data": q}).encode()
    for attempt in range(tries):
        url = SERVERS[attempt % len(SERVERS)]
        try:
            req = urllib.request.Request(url, data=data, headers={"User-Agent": "Radar/1.0 (build script)"})
            body = urllib.request.urlopen(req, timeout=660).read()
            if body.lstrip()[:1] != b"{":
                raise RuntimeError(body[:200])
            return json.loads(body)
        except Exception as e:  # noqa: BLE001
            print("  retry", url, str(e)[:120], file=sys.stderr)
            time.sleep(10 + attempt * 10)
    return None


def inb(lat, lon):
    return BBOX[0] <= lon <= BBOX[2] and BBOX[1] <= lat <= BBOX[3]


def simplify(pts, tol):
    """Дуглас–Пекер (итеративно)."""
    if len(pts) < 3:
        return pts
    keep = [False] * len(pts)
    keep[0] = keep[-1] = True
    stack = [(0, len(pts) - 1)]
    while stack:
        a, b = stack.pop()
        (ay, ax), (by, bx) = pts[a], pts[b]
        dx, dy = bx - ax, by - ay
        L = dx * dx + dy * dy
        best, bi = 0.0, -1
        for i in range(a + 1, b):
            py, px = pts[i]
            if L == 0:
                d = (px - ax) ** 2 + (py - ay) ** 2
            else:
                t = max(0, min(1, ((px - ax) * dx + (py - ay) * dy) / L))
                d = (px - ax - t * dx) ** 2 + (py - ay - t * dy) ** 2
            if d > best:
                best, bi = d, i
        if bi >= 0 and best > tol * tol:
            keep[bi] = True
            stack.append((a, bi))
            stack.append((bi, b))
    return [p for p, k in zip(pts, keep) if k]


def join_lines(lines, gap_km=0.0):
    """
    Склейка отрезков в длинные полилинии. Концы, совпадающие точно, склеиваются всегда;
    при gap_km > 0 — и близкие концы (раздельные проезжие части, развязки, разрывы данных).
    Вершины — (lat, lon).
    """
    def key(p):
        return (round(p[0], 5), round(p[1], 5))

    def km(a, b):
        return math.hypot((a[0] - b[0]) * 111.0, (a[1] - b[1]) * 111.0 * math.cos(math.radians(a[0])))

    segs = [l[:] for l in lines if len(l) >= 2]
    # грубая сетка концов для поиска ближайших (~2 км ячейка)
    def cell(p):
        return (int(p[0] / 0.02), int(p[1] / 0.03))
    used = [False] * len(segs)
    grid = {}
    for i, sg in enumerate(segs):
        for end in (sg[0], sg[-1]):
            grid.setdefault(cell(end), []).append(i)

    def find(pt):
        best, bi, rev = None, None, False
        cx, cy = cell(pt)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for j in grid.get((cx + dx, cy + dy), []):
                    if used[j]:
                        continue
                    sg = segs[j]
                    for end, r in ((sg[0], False), (sg[-1], True)):
                        d = 0.0 if key(end) == key(pt) else km(end, pt)
                        if (d == 0.0 or d <= gap_km) and (best is None or d < best):
                            best, bi, rev = d, j, r
        return bi, rev

    out = []
    for i in range(len(segs)):
        if used[i]:
            continue
        used[i] = True
        chain = segs[i][:]
        while True:
            j, rev = find(chain[-1])
            if j is None:
                break
            used[j] = True
            sg = segs[j][::-1] if rev else segs[j]
            chain += sg[1:] if key(sg[0]) == key(chain[-1]) else sg
        while True:
            j, rev = find(chain[0])
            if j is None:
                break
            used[j] = True
            sg = segs[j] if rev else segs[j][::-1]
            chain = (sg[:-1] if key(sg[-1]) == key(chain[0]) else sg) + chain
        out.append(chain)
    return out


def clip(lines):
    """Оставляет части линий внутри зоны интереса."""
    res = []
    for l in lines:
        cur = []
        for p in l:
            if inb(*p):
                cur.append(p)
            elif cur:
                if len(cur) >= 2:
                    res.append(cur)
                cur = []
        if len(cur) >= 2:
            res.append(cur)
    return res


out = bytearray(b"RRT1")
stats = {}


def wstr(s):
    b = s.encode("utf-8")
    out.extend(struct.pack("<H", len(b)))
    out.extend(b)


pending = {}


def emit(kind, fid, names, lines):
    """Копит линии по (вид, id): участки одной трассы из разных источников склеиваются вместе."""
    k = (kind, fid)
    if k in pending:
        pending[k][0].update(names)
        pending[k][1].extend(lines)
    else:
        pending[k] = (set(names), list(lines))


def flush():
    for (kind, fid), (names, lines) in pending.items():
        gap = 3.0 if kind == KIND_ROAD else 1.0 if kind == KIND_RIVER else 0.0
        write(kind, fid, names, join_lines(lines, gap) if gap > 0 else lines)


def write(kind, fid, names, lines):
    lines = [simplify(l, TOL) for l in clip(lines)]
    lines = [l for l in lines if len(l) >= 2]
    if not lines:
        return
    out.extend(struct.pack("<B", kind))
    wstr(fid)
    wstr("|".join(sorted({n.lower() for n in names if n})))
    out.extend(struct.pack("<I", len(lines)))
    for l in lines:
        out.extend(struct.pack("<I", len(l)))
        for lat, lon in l:
            out.extend(struct.pack("<ff", lat, lon))
    stats[kind] = stats.get(kind, 0) + sum(len(l) for l in lines)


def geo_lines(geom):
    t = geom["type"]
    cs = [geom["coordinates"]] if t == "LineString" else geom["coordinates"] if t == "MultiLineString" else []
    return [[(c[1], c[0]) for c in l] for l in cs]


# ---------- трассы (OSM) ----------
for ref, alt in ROADS.items():
    fn = os.path.join(CACHE, ref + ".json")
    if not os.path.exists(fn) and os.environ.get("ROUTES_OFFLINE"):
        print("  skip (no cache)", ref, file=sys.stderr)
        continue
    if not os.path.exists(fn):
        print("fetch", ref, file=sys.stderr)
        d = overpass(QUERY.replace("{ref}", ref))
        if d is None:
            print("  FAILED", ref, file=sys.stderr)
            continue
        json.dump(d, open(fn, "w"))
        time.sleep(3)
    d = json.load(open(fn))
    lines = [[(g["lat"], g["lon"]) for g in w["geometry"]] for w in d.get("elements", []) if w.get("geometry")]
    if not lines:
        continue
    names = [ref, ref.replace("-", ""), ref.replace("-", " ")] + alt
    emit(KIND_ROAD, ref, names, join_lines(lines))

# ---------- участки трасс в новых регионах: в OSM ещё под украинскими номерами ----------
# Р-280 «Новороссия» в Запорожской/Херсонской областях = М-14; Джанкой–Мелитополь (А-291/Р-280 в сводках) = М-18
UA_ALIAS = {"М-14": ("Р-280", ["новороссия"]), "М-18": ("М-18", ["симферополь мелитополь", "р-280"])}
ua_fn = os.path.join(CACHE, "_ua_sections.json")
if os.path.exists(ua_fn):
    groups = {}
    for w in json.load(open(ua_fn)).get("elements", []):
        refs = [r.strip() for r in w.get("tags", {}).get("ref", "").replace(",", ";").split(";")]
        for r in refs:
            if r in UA_ALIAS and w.get("geometry"):
                groups.setdefault(r, []).append([(g["lat"], g["lon"]) for g in w["geometry"]])
    for r, lines in groups.items():
        target, alt = UA_ALIAS[r]
        names = [target, target.replace("-", ""), target.replace("-", " "), r, r.replace("-", "")] + alt
        emit(KIND_ROAD, target, names, join_lines(lines))

# ---------- ж/д (NE) ----------
rail = []
for f in json.load(open(os.path.join(NE, "ne_10m_railroads.geojson")))["features"]:
    for l in geo_lines(f["geometry"]):
        if any(inb(*p) for p in l):
            rail.append(l)
emit(KIND_RAIL, "rail", ["жд", "железная дорога"], join_lines(rail))

# ---------- реки (NE) ----------
RIVER_RU = {
    "Volga": ["волга", "волге", "волгу"], "Don": ["дон", "дону"], "Dnieper": ["днепр", "днепру"], "Dnipro": ["днепр", "днепру"],
    "Desna": ["десна", "десне", "десны", "десну"], "Oka": ["ока", "оке", "оки", "оку"], "Seym": ["сейм", "сейму", "сейма"],
    "Donets": ["северский донец", "донец", "донцу", "донца"], "Severskiy Donets": ["северский донец", "донец", "донцу"],
    "Kuban": ["кубань", "кубани"], "Khoper": ["хопер", "хопру", "хопёр"], "Medveditsa": ["медведица", "медведице"],
    "Oskol": ["оскол", "осколу"], "Psel": ["псел", "пслу"], "Vorskla": ["ворскла", "ворскле"], "Sula": ["сула", "суле"],
    "Ipuť": ["ипуть", "ипути"], "Iput": ["ипуть", "ипути"], "Bolva": ["болва", "болве"], "Sozh": ["сож", "сожу"],
    "Pripyat": ["припять", "припяти"], "Southern Bug": ["южный буг"], "Kama": ["кама", "каме"], "Sura": ["сура", "суре"],
    "Tsna": ["цна", "цне"], "Voronezh": ["воронеж река"], "Bityug": ["битюг"], "Sal": ["сал"], "Manych": ["маныч"],
    "Mius": ["миус", "миусу"], "Kalmius": ["кальмиус"], "Ural": ["урал река"], "Moskva": ["москва-река", "москве-реке"],
    "Ugra": ["угра", "угре"], "Zhizdra": ["жиздра"], "Sejm": ["сейм", "сейму"], "Bug": ["южный буг"], "Ingulets": ["ингулец"],
}
rivers = {}
for f in json.load(open(os.path.join(NE, "ne_10m_rivers_lake_centerlines.geojson")))["features"]:
    p = f["properties"]
    if p.get("featurecla") != "River" or (p.get("scalerank") or 10) > 9:
        continue
    lines = [l for l in geo_lines(f["geometry"]) if any(inb(*q) for q in l)]
    if not lines:
        continue
    name = p.get("name") or p.get("name_en") or ""
    rivers.setdefault(name, []).extend(lines)
for name, lines in rivers.items():
    names = RIVER_RU.get(name, []) + ([name.lower()] if name else [])
    emit(KIND_RIVER, "river:" + (name or "?"), names, join_lines(lines))

# ---------- береговая линия (NE) ----------
coast = []
for f in json.load(open(os.path.join(NE, "ne_10m_coastline.geojson")))["features"]:
    for l in geo_lines(f["geometry"]):
        if any(inb(*p) for p in l):
            coast.append(l)
emit(KIND_COAST, "coast", ["берег", "побережье"], join_lines(coast))

# ---------- общие границы регионов и акватории (NE, нужен shapely) ----------
try:
    from shapely.geometry import shape, box
    from shapely.ops import linemerge
except ImportError:
    shape = None
    print("  shapely не установлен — границы и акватории пропущены", file=sys.stderr)

if shape is not None:
    # build_basemap.py — скрипт (импорт запустил бы сборку подложки): берём только словари соответствия id
    import ast
    _src = open(os.path.join(os.path.dirname(__file__), "build_basemap.py"), encoding="utf-8").read()
    _consts = {}
    for node in ast.parse(_src).body:
        if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name) and node.targets[0].id in ("REGION_IDS", "RU_ADMIN_FROM_UA"):
            _consts[node.targets[0].id] = ast.literal_eval(node.value)
    REGION_IDS, RU_ADMIN_FROM_UA = _consts["REGION_IDS"], _consts["RU_ADMIN_FROM_UA"]
    # области Украины — id из Gazetteer.cs (источники и направления); Харьковская — общий регион «kharkov»
    UA_IDS = {"UA-59": "ua_sumy", "UA-74": "ua_chernihiv", "UA-53": "ua_poltava", "UA-12": "ua_dnipro",
              "UA-51": "ua_odesa", "UA-48": "ua_mykolaiv", "UA-63": "kharkov"}
    regs = {}
    for f in json.load(open(os.path.join(NE, "ne_10m_admin_1_states_provinces.geojson")))["features"]:
        p = f["properties"]
        iso = p["iso_3166_2"]
        rid = REGION_IDS.get(iso) or UA_IDS.get(iso)
        if rid is None or not (p["adm0_a3"] in ("RUS", "UKR") or iso in RU_ADMIN_FROM_UA):
            continue
        g = shape(f["geometry"]).buffer(0)
        if g.intersects(box(BBOX[0], BBOX[1], BBOX[2], BBOX[3])):
            regs[rid] = g
    # контуры регионов из районов OSM (как в подложке, build_basemap.py REGIONS_OUT) — границы совпадают с картой
    _rf = os.environ.get("REGIONS_GEOJSON")
    if _rf and os.path.exists(_rf):
        for rid, gj in json.load(open(_rf)).items():
            if rid in regs:
                regs[rid] = shape(gj).buffer(0)
        print("  borders: regions from districts", _rf, file=sys.stderr)
    ids = sorted(regs)
    nb = 0
    for i, a in enumerate(ids):
        for b in ids[i + 1:]:
            ga, gb = regs[a], regs[b]
            if not ga.envelope.intersects(gb.envelope):
                continue
            # общие вершины NE совпадают не всегда точно — берём границу A внутри узкого буфера B
            shared = ga.boundary.intersection(gb.buffer(0.003))
            if shared.is_empty:
                continue
            if shared.geom_type not in ("LineString", "MultiLineString"):
                shared = linemerge([g for g in getattr(shared, "geoms", []) if g.geom_type == "LineString"]) if hasattr(shared, "geoms") else None
                if shared is None or shared.is_empty:
                    continue
            shared = linemerge(shared) if shared.geom_type == "MultiLineString" else shared
            parts = list(getattr(shared, "geoms", [shared]))
            lines = [[(y, x) for x, y in l.coords] for l in parts if l.length > 0.02]
            if not lines:
                continue
            emit(KIND_BORDER, f"border:{a}+{b}", [f"{a}+{b}"], join_lines(lines, 1.0))
            nb += 1
    print("  borders:", nb, file=sys.stderr)

    ocean = shape(json.load(open(os.path.join(NE, "ne_10m_ocean.geojson")))["features"][0]["geometry"])
    azov = ocean.intersection(box(34.6, 45.28, 39.6, 47.4))
    taganrog = azov.intersection(box(37.3, 46.55, 39.6, 47.4))
    black = ocean.intersection(box(27.0, 40.8, 42.0, 46.8)).difference(azov.buffer(0.01))
    # уточнённый берег OSM (tools/osm_land.py) — тот же, что у подложки: акватории не заходят на косы и лиманы
    coast_fn = os.environ.get("BASEMAP_COAST")
    if coast_fn and os.path.exists(coast_fn):
        from shapely.geometry import Polygon
        from shapely.ops import unary_union
        cj = json.load(open(coast_fn))
        cframe = unary_union([box(*f) for f in cj["frames"]])
        cland = unary_union([Polygon(p[0], p[1:]) for p in cj["polygons"]]).buffer(0)

        def refine(g):
            # в рамке: вода = рамка минус суша OSM (включая лиманы, отрезанные от моря в NE); вне рамки — NE
            near = g.buffer(0.15).intersection(cframe)
            return g.difference(cframe).union(near.difference(cland)).buffer(0)
        azov, taganrog, black = refine(azov), refine(taganrog), refine(black)
        # Каспий: в NE ocean его нет (это «озеро») — вода в каспийской рамке по берегу OSM
        casp_frame = box(46.4, 41.0, 54.0, 47.3)
        caspian = casp_frame.difference(cland).buffer(0)
        emit(KIND_SEA, "sea:caspian", ["каспийское море", "каспийского моря", "каспии", "каспий"],
             [[(y, x) for x, y in g.exterior.coords] for g in getattr(caspian, "geoms", [caspian])
              if g.geom_type == "Polygon" and g.area > 0.5])
        print("  seas: coast refined by OSM", file=sys.stderr)
    for fid, names, geom in (("sea:azov", ["азовское море", "азовского моря", "азовском море"], azov),
                             ("sea:taganrog", ["таганрогский залив"], taganrog),
                             ("sea:black", ["черное море", "черного моря", "черном море"], black)):
        polys = [g for g in getattr(geom, "geoms", [geom]) if g.geom_type == "Polygon" and g.area > 0.01]
        rings = [[(y, x) for x, y in g.exterior.coords] for g in polys]
        emit(KIND_SEA, fid, names, rings)
    # Сиваш — из OSM (tools/pbf_water.py или Overpass relation 5925937): в NE его нет
    water_fn = os.path.join(CACHE, "_water.json")
    if os.path.exists(water_fn):
        for fid, rings in json.load(open(water_fn)).items():
            emit(KIND_SEA, fid, ["сиваш", "залив сиваш", "гнилое море"], [[(la, lo) for la, lo in r] for r in rings])

flush()
out.extend(struct.pack("<B", 255))
with gzip.open(OUT, "wb", compresslevel=9) as fo:
    fo.write(bytes(out))
print("written", OUT, len(out), "bytes raw; vertices by kind:", stats)
