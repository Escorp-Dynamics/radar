#!/usr/bin/env python3
"""
Собирает офлайн-граф автодорог приграничных регионов Radar.Core/Assets/roadgraph.bin.gz (OSM, ODbL):
магистрали, региональные и межмуниципальные дороги (motorway … tertiary). По графу строится путь между
пунктами («по трассе Рыльск — Хомутовка», «по дороге на Геройское»).

Использование:
    python3 tools/build_roadgraph.py [кэш OSM]          (ROADGRAPH_OFFLINE=1 — только из кэша)

Формат (little-endian): "RRG1", u32 nv, nv*(f32 lat, f32 lon), u32 ne,
    для каждого ребра: u32 a, u32 b, u8 class (0 motorway … 4 tertiary), u16 n, n*(f32 lat, f32 lon) — промежуточные точки.
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

CACHE = sys.argv[1] if len(sys.argv) > 1 else "/tmp/osm_roadgraph"
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "Radar.Core", "Assets", "roadgraph.bin.gz")
os.makedirs(CACHE, exist_ok=True)
TOL = 0.0015  # упрощение геометрии ребра, градусы (~100–150 м)

FULL = "motorway|trunk|primary|secondary|tertiary"   # приграничье: вплоть до межмуниципальных дорог
MAIN = "motorway|trunk|primary"                       # тыл: магистрали и региональные дороги

# (s, w, n, e), классы дорог. Приграничные участки — подробно, тыловые — основная сеть.
TILES = {
    "bryansk_w": ((51.9, 32.2, 53.5, 34.6), FULL),
    "kursk_w": ((51.0, 34.0, 52.6, 36.0), FULL),
    "kursk_e": ((51.0, 36.0, 52.6, 38.4), FULL),
    "belgorod": ((49.8, 35.3, 51.5, 39.0), FULL),
    "lnr": ((48.0, 38.0, 50.1, 40.3), FULL),
    "dnr": ((46.9, 37.0, 48.6, 38.9), FULL),
    "zap_kherson": ((46.0, 32.6, 47.6, 37.2), FULL),
    "crimea": ((44.3, 32.4, 46.3, 36.7), FULL),
    # Ростовская, Воронежская области, Краснодарский край — подробно
    "rostov_w": ((46.0, 38.2, 48.2, 41.0), FULL),
    "rostov_e": ((46.0, 41.0, 48.2, 44.4), FULL),
    "rostov_n": ((48.2, 40.3, 50.3, 44.4), FULL),
    "voronezh_w": ((49.5, 38.0, 52.1, 40.4), FULL),
    "voronezh_e": ((50.3, 40.4, 52.1, 42.9), FULL),
    "krasnodar_w": ((44.3, 36.5, 46.9, 39.0), FULL),
    "krasnodar_e": ((43.4, 39.0, 46.9, 41.8), FULL),
    "bryansk_e_orel": ((52.0, 34.6, 53.9, 38.0), FULL),
    # тыловые области европейской части — основная сеть дорог
    "rear_50_42": ((49.5, 42.9, 53.0, 48.0), MAIN),
    "rear_52_38": ((52.1, 38.0, 55.0, 42.9), MAIN),
    "rear_53_31": ((53.5, 30.5, 56.5, 35.0), MAIN),
    "rear_53_35": ((53.9, 35.0, 56.8, 38.0), MAIN),
    "rear_55_38": ((55.0, 38.0, 58.0, 43.0), MAIN),
    "rear_53_43": ((53.0, 43.0, 56.5, 49.5), MAIN),
    "rear_56_31": ((56.5, 28.0, 60.5, 38.0), MAIN),
    "rear_south": ((43.0, 41.8, 46.0, 48.0), MAIN),
    "volgograd": ((47.4, 41.8, 51.3, 47.2), MAIN),
    "stavropol": ((43.6, 40.5, 46.2, 45.5), MAIN),
}
CLASSES = {"motorway": 0, "trunk": 1, "primary": 2, "secondary": 3, "tertiary": 4}

SERVERS = ["https://overpass.private.coffee/api/interpreter", "https://overpass-api.de/api/interpreter",
           "https://maps.mail.ru/osm/tools/overpass/api/interpreter"]
QUERY = """[out:json][timeout:900];
way["highway"~"^({classes})(_link)?$"]({s},{w},{n},{e});
out geom;"""


def overpass(q, tries=10):
    data = urllib.parse.urlencode({"data": q}).encode()
    for attempt in range(tries):
        url = SERVERS[attempt % len(SERVERS)]
        try:
            req = urllib.request.Request(url, data=data, headers={"User-Agent": "Radar/1.0 (build script)"})
            body = urllib.request.urlopen(req, timeout=960).read()
            if body.lstrip()[:1] != b"{":
                raise RuntimeError(body[:200])
            return json.loads(body)
        except Exception as e:  # noqa: BLE001
            print("  retry", url, str(e)[:120], file=sys.stderr)
            time.sleep(10 + attempt * 10)
    return None


def simplify(pts, tol):
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
            t = 0 if L == 0 else max(0, min(1, ((px - ax) * dx + (py - ay) * dy) / L))
            d = (px - ax - t * dx) ** 2 + (py - ay - t * dy) ** 2
            if d > best:
                best, bi = d, i
        if bi >= 0 and best > tol * tol:
            keep[bi] = True
            stack.append((a, bi))
            stack.append((bi, b))
    return [p for p, k in zip(pts, keep) if k]


# ---------- загрузка ----------
ways = {}
for name, ((s, w, n, e), classes) in TILES.items():
    fn = os.path.join(CACHE, name + ".json")
    if not os.path.exists(fn):
        if os.environ.get("ROADGRAPH_OFFLINE"):
            print("  skip (no cache)", name, file=sys.stderr)
            continue
        print("fetch", name, file=sys.stderr)
        d = overpass(QUERY.replace("{classes}", classes).format(s=s, w=w, n=n, e=e))
        if d is None:
            print("  FAILED", name, file=sys.stderr)
            continue
        json.dump(d, open(fn, "w"))
        time.sleep(3)
    for el in json.load(open(fn)).get("elements", []):
        if el.get("type") == "way" and el.get("geometry") and el.get("nodes"):
            hw = el.get("tags", {}).get("highway", "").replace("_link", "")
            if hw in CLASSES:
                ways[el["id"]] = (el["nodes"], [(g["lat"], g["lon"]) for g in el["geometry"]], CLASSES[hw])
print("ways", len(ways), file=sys.stderr)

# ---------- граф ----------
# Вершины — узлы OSM, общие для нескольких путей (перекрёстки), и концы путей. Рёбра — участки путей между вершинами.
# Затем цепочки через вершины степени 2 (дорога продолжается без перекрёстка) сливаются в одно ребро.
use = {}
for nodes, _, _ in ways.values():
    for i, nid in enumerate(nodes):
        use[nid] = use.get(nid, 0) + (2 if i in (0, len(nodes) - 1) else 1)
is_vertex = {nid for nid, c in use.items() if c >= 2}

pos_of = {}
raw_edges = []  # (nodeA, nodeB, cls, pts)
for nodes, geom, cls in ways.values():
    start = 0
    for i in range(1, len(nodes)):
        if nodes[i] in is_vertex or i == len(nodes) - 1:
            a, b = nodes[start], nodes[i]
            if a != b:
                pos_of[a] = geom[start]
                pos_of[b] = geom[i]
                raw_edges.append([a, b, cls, geom[start:i + 1]])
            start = i

# дубли (две проезжие части, повторяющиеся пути) — оставляем по одному ребру на пару вершин
best = {}
for e in raw_edges:
    k = (min(e[0], e[1]), max(e[0], e[1]))
    if k not in best or e[2] < best[k][2]:
        best[k] = e
edges = list(best.values())

adj = {}
for i, e in enumerate(edges):
    adj.setdefault(e[0], set()).add(i)
    adj.setdefault(e[1], set()).add(i)
alive = [True] * len(edges)

changed = True
while changed:
    changed = False
    for v in list(adj.keys()):
        es = [e for e in adj.get(v, ()) if alive[e]]
        if len(es) != 2:
            continue
        i1, i2 = es
        E1, E2 = edges[i1], edges[i2]
        a = E1[0] if E1[1] == v else E1[1]
        b = E2[1] if E2[0] == v else E2[0]
        if a == v or b == v or a == b:
            continue
        p1 = E1[3] if E1[1] == v else E1[3][::-1]
        p2 = E2[3] if E2[0] == v else E2[3][::-1]
        alive[i1] = alive[i2] = False
        ni = len(edges)
        edges.append([a, b, min(E1[2], E2[2]), p1 + p2[1:]])
        alive.append(True)
        adj[a].discard(i1); adj[a].discard(i2); adj[a].add(ni)
        adj[b].discard(i1); adj[b].discard(i2); adj[b].add(ni)
        adj[v] = set()
        changed = True

live = [e for i, e in enumerate(edges) if alive[i]]
used_v = sorted({e[0] for e in live} | {e[1] for e in live})
remap = {v: i for i, v in enumerate(used_v)}
vpos = [pos_of[v] for v in used_v]
live = [(remap[a], remap[b], cls, simplify(pts, TOL), True) for a, b, cls, pts in live]
used_v = list(range(len(vpos)))
remap = {v: v for v in used_v}

out = bytearray(b"RRG1")
out.extend(struct.pack("<I", len(used_v)))
for v in used_v:
    out.extend(struct.pack("<ff", *vpos[v]))
out.extend(struct.pack("<I", len(live)))
npts = 0
for a, b, cls, pts, _ in live:
    mid = pts[1:-1]
    out.extend(struct.pack("<IIBH", remap[a], remap[b], cls, len(mid)))
    for p in mid:
        out.extend(struct.pack("<ff", *p))
    npts += len(mid)
with gzip.open(OUT, "wb", compresslevel=9) as fo:
    fo.write(bytes(out))
print("written", OUT, "vertices", len(used_v), "edges", len(live), "mid points", npts, "raw bytes", len(out))
