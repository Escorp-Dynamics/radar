#!/usr/bin/env python3
"""
Слой подписей населённых пунктов для карты: Radar.Core/Assets/labels.bin.gz (GeoNames, CC-BY 4.0 + OSM, ODbL).

Одна запись на пункт (в отличие от places.tsv, где у пункта много вариантов названия для поиска):
русское название (предпочтительное из alternateNames), координаты, население, класс важности.

Использование:
    python3 tools/build_labels.py <папка GeoNames: RU.txt, UA.txt, alt/ru/RU.txt, alt/ua/UA.txt> [папка OSM-выгрузки]

Формат RLB1: "RLB1", uint32 count, затем записи по убыванию важности:
    float32 lat, float32 lon, int32 population, byte class (0 — центр региона/столица, 1 — центр района, 2 — прочие),
    uint16 длина + UTF-8 название.
"""
import gzip
import math
import os
import re
import struct
import sys

SRC = sys.argv[1] if len(sys.argv) > 1 else "/tmp/gn"
OSM = sys.argv[2] if len(sys.argv) > 2 else None
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "Radar.Core", "Assets", "labels.bin.gz")

CYR = re.compile(r"^[А-Яа-яЁёІіЇїЄєҐґ][А-Яа-яЁёІіЇїЄєҐґ\- .'’()0-9]+$")
UKR_ONLY = re.compile(r"[ІіЇїЄєҐґ]")
# украинские формы без особых букв: «Васютинське», «Бердянська Коса»
UKR_FORM = re.compile(r"(ськ|цьк|зьк|ння$|ддя$|ття$|лля$| Дил$)")
SKIP_CODES = {"PPLH", "PPLQ", "PPLW", "PPLX", "PPLCH"}


def load_alt(path):
    """geonameid → лучшее русское название (предпочтительное, не историческое/разговорное)."""
    best = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) < 8 or p[2] != "ru":
                continue
            gid, name = int(p[1]), p[3].strip()
            if not name or not CYR.match(name) or UKR_ONLY.search(name) or UKR_FORM.search(name):
                continue
            preferred, short, colloq, hist = p[4] == "1", p[5] == "1", p[6] == "1", p[7] == "1"
            rank = (not hist, not colloq, preferred, not short)
            old = best.get(gid)
            if old is None or rank > old[0]:
                best[gid] = (rank, name)
    return {k: v[1] for k, v in best.items()}


def klass(code):
    if code in ("PPLC", "PPLA"):
        return 0
    if code in ("PPLA2", "PPLA3"):
        return 1
    return 2


items = []  # (lat, lon, pop, class, name)
for country in ("RU", "UA"):
    alt = load_alt(os.path.join(SRC, "alt", country.lower(), f"{country}.txt"))
    with open(os.path.join(SRC, f"{country}.txt"), encoding="utf-8") as f:
        for line in f:
            p = line.rstrip("\n").split("\t")
            if p[6] != "P" or p[7] in SKIP_CODES:
                continue
            gid = int(p[0])
            name = alt.get(gid)
            if name is None:
                # без русского варианта: кириллица из списка названий (русская форма предпочтительнее украинской)
                cands = [n.strip() for n in p[3].split(",") if CYR.match(n.strip())]
                ru = [n for n in cands if not UKR_ONLY.search(n) and not (country == "UA" and UKR_FORM.search(n))]
                name = (ru or [None])[0]
            if name is None:
                continue
            items.append([float(p[4]), float(p[5]), int(p[14] or 0), klass(p[7]), name])
print("geonames", len(items))


# Официальные названия в РФ для пунктов, переименованных Украиной (каналы пишут российские названия)
RENAMES = {
    # ЛНР
    "Кадиевка": "Стаханов", "Сорокино": "Краснодон", "Хрустальный": "Красный Луч", "Довжанск": "Свердловск",
    "Голубовка": "Кировск", "Должанск": "Свердловск", "Красный луч": "Красный Луч", "Крестовка": "Кировское", "Боково-Хрустальное": "Боково-Антрацит", "Сокологорск": "Горское",
    "Ирмино": "Ирмино", "Вахрушево": "Вахрушево", "Петрово-Красноселье": "Петровское",
    # ДНР
    "Бахмут": "Артёмовск", "Торецк": "Дзержинск", "Покровск": "Красноармейск", "Мирноград": "Димитров",
    "Лиман": "Красный Лиман", "Кальмиусское": "Комсомольское", "Мангуш": "Первомайское", "Никольское": "Володарское",
    "Бойковское": "Тельманово", "Новоэкономическое": "Димитрово", "Светлодарск": "Светлодарск",
}
# Прежние названия крупных городов Украины — как в сообщениях каналов (только для крупных: тёзки-сёла не трогаем)
RENAMES_UA = {"Днепр": "Днепропетровск", "Кропивницкий": "Кировоград", "Каменское": "Днепродзержинск"}
# переименования применяются только в пределах Донбасса (одноимённые пункты есть по всей стране)
DONBAS = (46.8, 50.2, 36.4, 40.4)
RENAMES_RU = set(RENAMES.values())


def in_donbas(lat, lon):
    return DONBAS[0] <= lat <= DONBAS[1] and DONBAS[2] <= lon <= DONBAS[3]


def norm(s):
    return s.lower().replace("ё", "е").replace("-", " ").replace("  ", " ").strip()


# OSM (прифронтовые регионы): уточняет население/название совпадающих пунктов и добавляет недостающие
if OSM and os.path.isdir(OSM):
    grid = {}

    def cell(lat, lon):
        return (int(lat * 20), int(lon * 20))

    for i, it in enumerate(items):
        grid.setdefault(cell(it[0], it[1]), []).append(i)
    added = updated = 0
    for fn in sorted(os.listdir(OSM)):
        if not fn.endswith(".tsv"):
            continue
        for line in open(os.path.join(OSM, fn), encoding="utf-8"):
            p = line.rstrip("\n").split("\t")
            if len(p) < 6 or p[4] != "P" or not CYR.match(p[0]):
                continue
            name, lat, lon, pop = p[0], float(p[2]), float(p[3]), int(p[5] or 0)
            for pref in ("пгт ", "село ", "посёлок ", "поселок "):
                if name.lower().startswith(pref):
                    name = name[len(pref):]
            c = cell(lat, lon)
            match = None
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for i in grid.get((c[0] + dx, c[1] + dy), []):
                        it = items[i]
                        d = math.hypot(it[0] - lat, (it[1] - lon) * math.cos(math.radians(lat)))
                        if d < 0.04 and norm(it[4]) == norm(name):
                            match = i
            if match is not None:
                if pop > items[match][2]:
                    items[match][2] = pop
                    updated += 1
                continue
            items.append([lat, lon, pop, 2, name])
            grid.setdefault(c, []).append(len(items) - 1)
            added += 1
    print("osm added", added, "updated", updated)

renamed = 0
for it in items:
    if in_donbas(it[0], it[1]) and it[4] in RENAMES and RENAMES[it[4]] != it[4]:
        it[4] = RENAMES[it[4]]
        renamed += 1
for it in items:
    if it[4] in RENAMES_UA and it[2] >= 100000 and 44.0 <= it[0] <= 53.0 and 22.0 <= it[1] <= 40.5:
        it[4] = RENAMES_UA[it[4]]
        renamed += 1
print("renamed", renamed)

# дубли одного пункта (записи GeoNames/OSM рядом с одинаковым названием) — оставляем крупнейший,
# население берём максимальное из дублей
items.sort(key=lambda it: (it[3], -it[2], it[4]))
grid = {}
uniq = []
for it in items:
    c = (int(it[0] * 25), int(it[1] * 25))
    nm = norm(it[4])
    dup = None
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for u in grid.get((c[0] + dx, c[1] + dy), []):
                if norm(u[4]) == nm and math.hypot(u[0] - it[0], (u[1] - it[1]) * math.cos(math.radians(it[0]))) < 0.03:
                    dup = u
    # одна точка под разными названиями (старое/новое) — первое по важности
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for u in grid.get((c[0] + dx, c[1] + dy), []):
                if dup is None and it[2] > 0 and math.hypot(u[0] - it[0], (u[1] - it[1]) * math.cos(math.radians(it[0]))) < 0.004:
                    dup = u
    if dup is not None:
        dup[2] = max(dup[2], it[2])
        dup[3] = min(dup[3], it[3])
        continue
    grid.setdefault(c, []).append(it)
    uniq.append(it)
# порядок важности: население, затем класс (центры районов без данных о населении — раньше сёл)
uniq.sort(key=lambda it: (-max(it[2], 15000 if it[3] == 0 else 3000 if it[3] == 1 else 0), it[3], it[4]))

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with gzip.open(OUT, "wb", compresslevel=9) as g:
    g.write(b"RLB1")
    g.write(struct.pack("<I", len(uniq)))
    for lat, lon, pop, cls, name in uniq:
        b = name.encode("utf-8")
        g.write(struct.pack("<ffiBH", lat, lon, pop, cls, len(b)))
        g.write(b)
print("labels", len(uniq), "size", os.path.getsize(OUT))
