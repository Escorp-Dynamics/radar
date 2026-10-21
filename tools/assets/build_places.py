#!/usr/bin/env python3
"""
Собирает офлайн-справочник населённых пунктов Radar.Core/Assets/places.tsv.gz из GeoNames (CC-BY 4.0).

Использование:
    python3 tools/build_places.py <папка с RU.txt, UA.txt из https://download.geonames.org/export/dump/> [папка OSM-выгрузки]

Формат строки: norm_name \t region_id \t lat \t lon \t kind \t pop \t display_name
kind: P — населённый пункт, A — район/округ, T — мыс/гора/полуостров, H — водный объект, L — местность, S — объект
"""
import gzip
import os
import re
import sys

SRC = sys.argv[1] if len(sys.argv) > 1 else "/tmp/gn"
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "Radar.Core", "Assets", "places.tsv.gz")

# GeoNames admin1 → id региона приложения
RU = {
    "09": "belgorod", "41": "kursk", "10": "bryansk", "56": "orel", "86": "voronezh", "38": "krasnodar", "61": "rostov",
    "43": "lipetsk", "72": "tambov", "76": "tula", "25": "kaluga", "69": "smolensk", "47": "moscow_obl", "48": "moscow",
    "62": "ryazan", "57": "penza", "67": "saratov", "84": "volgograd", "07": "astrakhan", "24": "kalmykia", "65": "samara",
    "81": "ulyanovsk", "46": "mordovia", "51": "nizhny", "83": "vladimir", "21": "ivanovo", "88": "yaroslavl", "77": "tver",
    "60": "pskov", "52": "novgorod", "42": "leningrad", "66": "spb", "37": "kostroma", "85": "vologda", "23": "kaliningrad",
    "16": "chuvashia", "73": "tatarstan", "08": "bashkiria", "55": "orenburg", "80": "udmurtia", "45": "mari", "90": "perm",
    "33": "kirov", "70": "stavropol", "01": "adygea", "27": "kchr", "22": "kbr", "68": "ossetia", "19": "ingushetia",
    "12": "chechnya", "17": "dagestan", "13": "chelyabinsk", "71": "sverdlovsk", "49": "murmansk", "28": "karelia",
    "06": "arkhangelsk", "34": "komi", "78": "tyumen", "32": "khmao", "87": "yanao", "50": "nao",
    "40": "kurgan", "54": "omsk", "53": "novosibirsk", "75": "tomsk", "29": "kemerovo", "04": "altai_krai",
    "91": "krasnoyarsk", "20": "irkutsk", "31": "khakassia", "79": "tuva", "03": "altai_rep",
    "11": "buryatia", "93": "zabaykalsky", "05": "amur", "89": "jao", "30": "khabarovsk", "59": "primorye",
    "44": "magadan", "64": "sakhalin", "15": "chukotka", "63": "yakutia", "92": "kamchatka",
}
UA = {"11": "crimea", "20": "sevastopol", "08": "kherson", "26": "zaporozhye", "14": "lnr", "05": "dnr", "07": "kharkov",
      # соседние области Украины — для направлений «от Одессы», «в сторону Николаева» и т. п.
      "17": "ua_odesa", "16": "ua_mykolaiv", "04": "ua_dnipro", "21": "ua_sumy", "02": "ua_chernihiv", "18": "ua_poltava"}

CODES = {"P": None, "A": {"ADM2", "ADM3", "ADM2H"}, "T": {"CAPE", "PEN", "MT", "HLL", "PT", "SPIT", "ISTH", "PASS"},
         "H": {"BAY", "LGN", "LK", "STRT", "INLT", "RSV"}, "L": {"AREA", "RGN", "PRT", "INDS"}, "S": {"AIRP", "AIRF", "PS", "PSTP", "OILR", "DAM", "BDG", "RSTN"}}
CYR = re.compile(r"^[А-Яа-яЁё][А-Яа-яЁё\- .'’()]+$")


def norm(s):
    return s.lower().replace("ё", "е").replace("’", "").replace("'", "").strip()


rows = {}
for fn, mapping in (("RU.txt", RU), ("UA.txt", UA)):
    with open(os.path.join(SRC, fn), encoding="utf-8") as f:
        for line in f:
            p = line.rstrip("\n").split("\t")
            cls, code, adm1 = p[6], p[7], p[10]
            region = mapping.get(adm1)
            if region is None or cls not in CODES:
                continue
            allowed = CODES[cls]
            if allowed is not None and code not in allowed:
                continue
            if cls == "P" and code in ("PPLH", "PPLQ", "PPLW"):
                continue  # исторические/разрушенные/заброшенные
            lat, lon = float(p[4]), float(p[5])
            pop = int(p[14] or 0)
            names = {n.strip() for n in p[3].split(",") if CYR.match(n.strip())}
            if not names:
                continue
            # детерминированный порядок (set в Python перемешивается): вариант с «ё» — первым, он и станет подписью
            for n in sorted(names, key=lambda x: ("ё" not in x.lower(), x)):
                key = (norm(n), region)
                # одна запись на имя в регионе: предпочитаем крупный пункт, затем населённый пункт перед прочими
                rank = (cls == "P", pop, code.startswith("PPLA"))
                old = rows.get(key)
                if old is None or rank > old[0]:
                    rows[key] = (rank, lat, lon, cls, pop, n)

# Дополнение из OpenStreetMap (tools/fetch_osm_places.py): приоритет над GeoNames для одноимённых записей
OSM = sys.argv[2] if len(sys.argv) > 2 else None
if OSM and os.path.isdir(OSM):
    added = 0
    for fn in sorted(os.listdir(OSM)):
        if not fn.endswith(".tsv"):
            continue
        for line in open(os.path.join(OSM, fn), encoding="utf-8"):
            p = line.rstrip("\n").split("\t")
            if len(p) < 6 or not CYR.match(p[0]):
                continue
            name, region, lat, lon, kind, pop = p[0], p[1], float(p[2]), float(p[3]), p[4], int(p[5] or 0)
            # «мыс Чауда» → индексируем и «чауда»
            names = {name}
            for pref in ("мыс ", "бухта ", "коса ", "полуостров ", "гора ", "озеро ", "пгт ", "село ", "посёлок ", "поселок "):
                if name.lower().startswith(pref):
                    names.add(name[len(pref):])
            if name.endswith(" сельское поселение") or name.endswith(" городское поселение"):
                continue
            for n in sorted(names):
                key = (norm(n), region)
                rank = (kind == "P", pop, True)
                old = rows.get(key)
                if old is None or rank >= old[0]:
                    if old is None:
                        added += 1
                    rows[key] = (rank, lat, lon, kind, pop, n)
    print("osm added", added)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with gzip.open(OUT, "wt", encoding="utf-8", compresslevel=9) as g:
    for (nm, region), (_, lat, lon, cls, pop, disp) in sorted(rows.items()):
        g.write(f"{nm}\t{region}\t{lat:.4f}\t{lon:.4f}\t{cls}\t{pop}\t{disp}\n")
print("entries", len(rows), "size", os.path.getsize(OUT))
