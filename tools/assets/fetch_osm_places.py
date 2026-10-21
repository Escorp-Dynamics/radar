#!/usr/bin/env python3
"""
Выгружает топонимы OpenStreetMap (ODbL) по регионам через Overpass API в osm_places.tsv
(дополнение к GeoNames — для Крыма, Севастополя, новых регионов и приграничья кириллических названий в GeoNames мало).

Использование:
    python3 tools/fetch_osm_places.py <выходная папка>
Формат: name \t region_id \t lat \t lon \t kind \t pop
"""
import os
import sys
import time
import urllib.parse
import urllib.request

OUT = sys.argv[1] if len(sys.argv) > 1 else "/tmp/osm"
os.makedirs(OUT, exist_ok=True)

REGIONS = {
    "crimea": "Республика Крым", "sevastopol": "Севастополь", "kherson": "Херсонская область",
    "zaporozhye": "Запорожская область", "lnr": "Луганская область", "dnr": "Донецкая область",
    "belgorod": "Белгородская область", "kursk": "Курская область", "bryansk": "Брянская область",
    "orel": "Орловская область", "voronezh": "Воронежская область", "rostov": "Ростовская область",
    "krasnodar": "Краснодарский край", "kharkov": "Харьковская область",
}
SERVERS = ["https://overpass-api.de/api/interpreter", "https://overpass.private.coffee/api/interpreter",
           "https://maps.mail.ru/osm/tools/overpass/api/interpreter"]

QUERY = """[out:csv(name,"name:ru",::lat,::lon,place,boundary,admin_level,natural,population;false;"\\t")][timeout:300];
area["name:ru"="{name}"]["boundary"="administrative"]["admin_level"="4"]->.a;
(
  node(area.a)[place~"^(city|town|village|hamlet|suburb|quarter|neighbourhood|locality|isolated_dwelling|allotments)$"][name];
  node(area.a)[natural~"^(cape|peninsula|bay|strait|spit|peak|beach)$"][name];
  relation(area.a)[boundary=administrative][admin_level~"^(6|8)$"][name];
);
out center;"""


def fetch(name):
    q = QUERY.replace("{name}", name)
    data = urllib.parse.urlencode({"data": q}).encode()
    for attempt in range(6):
        url = SERVERS[attempt % len(SERVERS)]
        try:
            req = urllib.request.Request(url, data=data, headers={"User-Agent": "Radar/1.0 (build script)"})
            body = urllib.request.urlopen(req, timeout=330).read().decode("utf-8", "replace")
            if body.lstrip().startswith("<"):
                raise RuntimeError("busy")
            return body
        except Exception as e:  # noqa: BLE001
            print("  retry", name, url, e, file=sys.stderr)
            time.sleep(10 + attempt * 10)
    return ""


for rid, name in REGIONS.items():
    path = os.path.join(OUT, rid + ".tsv")
    if os.path.exists(path) and os.path.getsize(path) > 100:
        continue
    body = fetch(name)
    rows = []
    for line in body.splitlines():
        p = line.split("\t")
        if len(p) < 9:
            continue
        nm, nm_ru, lat, lon, place, boundary, lvl, natural, pop = p[:9]
        n = nm_ru or nm
        if not n or not lat:
            continue
        kind = "P" if place else ("A" if boundary else "T")
        pop = "".join(ch for ch in pop if ch.isdigit()) or "0"
        rows.append(f"{n}\t{rid}\t{float(lat):.4f}\t{float(lon):.4f}\t{kind}\t{pop}")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(rows) + "\n")
    print(rid, len(rows), file=sys.stderr)
    time.sleep(5)
