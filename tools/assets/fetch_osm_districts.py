#!/usr/bin/env python3
"""
Выгружает границы районов/муниципальных и городских округов (OSM admin_level=6, ODbL) через Overpass API.

Использование:
    python3 tools/fetch_osm_districts.py <выходная папка>
Результат: <папка>/<region_id>.json — [{"name": ..., "rings": [[[lat, lon], ...], ...]}, ...]
"""
import json
import os
import sys
import time
import urllib.parse
import urllib.request

OUT = sys.argv[1] if len(sys.argv) > 1 else "/tmp/osm_d"
os.makedirs(OUT, exist_ok=True)

REGIONS = {
    "belgorod": "Белгородская область", "kursk": "Курская область", "bryansk": "Брянская область",
    "orel": "Орловская область", "voronezh": "Воронежская область", "rostov": "Ростовская область",
    "krasnodar": "Краснодарский край", "crimea": "Республика Крым", "sevastopol": "Севастополь",
    "kherson": "Херсонская область", "zaporozhye": "Запорожская область", "lnr": "Луганская область",
    "dnr": "Донецкая область", "kharkov": "Харьковская область",
}
# Тыловые регионы (сообщения @kupolrussia и др.): привязка по коду ISO 3166-2 — надёжнее названий.
REAR_ISO = {
    "lipetsk": "RU-LIP", "tambov": "RU-TAM", "tula": "RU-TUL", "kaluga": "RU-KLU", "smolensk": "RU-SMO",
    "moscow_obl": "RU-MOS", "ryazan": "RU-RYA", "penza": "RU-PNZ", "saratov": "RU-SAR", "volgograd": "RU-VGG",
    "astrakhan": "RU-AST", "kalmykia": "RU-KL", "samara": "RU-SAM", "ulyanovsk": "RU-ULY", "mordovia": "RU-MO",
    "nizhny": "RU-NIZ", "vladimir": "RU-VLA", "ivanovo": "RU-IVA", "yaroslavl": "RU-YAR", "tver": "RU-TVE",
    "pskov": "RU-PSK", "novgorod": "RU-NGR", "leningrad": "RU-LEN", "kostroma": "RU-KOS", "vologda": "RU-VLG",
    "kaliningrad": "RU-KGD", "chuvashia": "RU-CU", "tatarstan": "RU-TA", "bashkiria": "RU-BA", "orenburg": "RU-ORE",
    "udmurtia": "RU-UD", "mari": "RU-ME", "perm": "RU-PER", "kirov": "RU-KIR", "stavropol": "RU-STA",
    "adygea": "RU-AD", "kchr": "RU-KC", "kbr": "RU-KB", "ossetia": "RU-SE", "ingushetia": "RU-IN",
    "chechnya": "RU-CE", "dagestan": "RU-DA", "chelyabinsk": "RU-CHE", "sverdlovsk": "RU-SVE",
    "murmansk": "RU-MUR", "karelia": "RU-KR", "arkhangelsk": "RU-ARK", "komi": "RU-KO", "tyumen": "RU-TYU",
    "kurgan": "RU-KGN", "nao": "RU-NEN", "khmao": "RU-KHM", "yanao": "RU-YAN", "omsk": "RU-OMS",
    "novosibirsk": "RU-NVS", "tomsk": "RU-TOM", "kemerovo": "RU-KEM", "altai_krai": "RU-ALT",
    "krasnoyarsk": "RU-KYA", "irkutsk": "RU-IRK", "khakassia": "RU-KK", "tuva": "RU-TY", "altai_rep": "RU-AL",
    "buryatia": "RU-BU", "zabaykalsky": "RU-ZAB", "amur": "RU-AMU", "jao": "RU-YEV", "khabarovsk": "RU-KHA",
    "primorye": "RU-PRI", "magadan": "RU-MAG", "sakhalin": "RU-SAK", "chukotka": "RU-CHU", "yakutia": "RU-SA",
    "kamchatka": "RU-KAM",
}
# Города федерального значения: районы — другой admin_level (Москва: 125 районов и поселения — 8; СПб: 18 районов — 5)
CITY_ISO = {"moscow": ("RU-MOW", "8"), "spb": ("RU-SPE", "5")}
QUERY_ISO = """[out:json][timeout:900];
area["ISO3166-2"="{iso}"]["boundary"="administrative"]->.a;
relation(area.a)["boundary"="administrative"]["admin_level"="{level}"];
out geom;"""

SERVERS = ["https://overpass-api.de/api/interpreter", "https://overpass.private.coffee/api/interpreter",
           "https://maps.mail.ru/osm/tools/overpass/api/interpreter"]
QUERY = """[out:json][timeout:600];
area["name:ru"="{name}"]["boundary"="administrative"]["admin_level"="4"]->.a;
relation(area.a)["boundary"="administrative"]["admin_level"="6"];
out geom;"""


def fetch(name):
    if name.startswith("ISO:"):
        iso, _, level = name[4:].partition("@")
        q = QUERY_ISO.replace("{iso}", iso).replace("{level}", level or "6")
    else:
        q = QUERY.replace("{name}", name)
    data = urllib.parse.urlencode({"data": q}).encode()
    for attempt in range(8):
        url = SERVERS[attempt % len(SERVERS)]
        try:
            req = urllib.request.Request(url, data=data, headers={"User-Agent": "Radar/1.0 (build script)"})
            body = urllib.request.urlopen(req, timeout=660).read()
            return json.loads(body)
        except Exception as e:  # noqa: BLE001
            print("  retry", name, url, e, file=sys.stderr)
            time.sleep(15 + attempt * 15)
    return None


def assemble(ways):
    """Склейка outer-путей в замкнутые кольца."""
    segs = [w[:] for w in ways if len(w) >= 2]
    rings = []
    while segs:
        ring = segs.pop(0)
        changed = True
        while ring[0] != ring[-1] and changed:
            changed = False
            for i, s in enumerate(segs):
                if s[0] == ring[-1]:
                    ring += s[1:]
                elif s[-1] == ring[-1]:
                    ring += s[::-1][1:]
                elif s[-1] == ring[0]:
                    ring = s[:-1] + ring
                elif s[0] == ring[0]:
                    ring = s[::-1][:-1] + ring
                else:
                    continue
                segs.pop(i)
                changed = True
                break
        if len(ring) >= 4:
            rings.append(ring)
    return rings


TODO = dict(REGIONS)
TODO.update({rid: "ISO:" + iso for rid, iso in REAR_ISO.items()})
TODO.update({rid: f"ISO:{iso}@{lvl}" for rid, (iso, lvl) in CITY_ISO.items()})
ONLY = set(sys.argv[2].split(",")) if len(sys.argv) > 2 else None

for rid, name in TODO.items():
    if ONLY and rid not in ONLY:
        continue
    path = os.path.join(OUT, rid + ".json")
    if os.path.exists(path) and os.path.getsize(path) > 50:
        continue
    js = fetch(name)
    if js is None:
        print(rid, "FAILED", file=sys.stderr)
        continue
    result = []
    for el in js.get("elements", []):
        tags = el.get("tags", {})
        nm = tags.get("name:ru") or tags.get("name")
        if not nm:
            continue
        ways = []
        for m in el.get("members", []):
            if m.get("type") == "way" and m.get("role") in ("outer", "") and "geometry" in m:
                ways.append([(round(p["lat"], 4), round(p["lon"], 4)) for p in m["geometry"]])
        rings = assemble(ways)
        if rings:
            result.append({"name": nm, "rings": rings})
    with open(path, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False)
    print(rid, len(result), file=sys.stderr)
    time.sleep(5)
