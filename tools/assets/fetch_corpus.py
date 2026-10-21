#!/usr/bin/env python3
"""Скачивает историю публичных каналов через веб-превью t.me/s/ в corpus.json для проверки парсера.
Использование: python3 tools/fetch_corpus.py [страниц на канал] [файл] [канал1,канал2,...]  (≈20 сообщений на страницу)"""
import re, html, json, sys, time, urllib.request, concurrent.futures as cf
CH = ["LPRalarm","kupolrussia","lpr1_Crimea_Alarm","lpr1_Krasnodar_alarm","lpr1_Rostov_alarm","lpr1_Belgorod_alarm",
      "lpr1_Kursk_alarm","lpr1_Bryansk_alarm","lpr1_Orlov_alarm","lpr1_Voronezh_alarm","lpr1_Kherson_alarm","lpr1_LDPR_alarm","lpr1_Kharkov_alarm",
      "radarrussiia","radar_moscoww","rada_saratov","vrv_radar","radar_samaraaaa","radar_rschs","nabludatel_rf","locatorru"]
if len(sys.argv) > 3: CH = sys.argv[3].split(",")
PAGES = int(sys.argv[1]) if len(sys.argv) > 1 else 60
def get(url):
    for i in range(4):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
            return urllib.request.urlopen(req, timeout=20).read().decode("utf-8", "replace")
        except Exception as e:
            time.sleep(2 + i * 2)
    return ""
def parse(ch, s):
    out = []
    starts = [m for m in re.finditer(r'<div class="tgme_widget_message (?:text_not_supported_wrap )?js-widget_message"[^>]*data-post="([^"]+)"', s)]
    for i, m in enumerate(starts):
        blk = s[m.start(): starts[i+1].start() if i+1 < len(starts) else len(s)]
        mid = int(m.group(1).split("/")[-1])
        t = re.search(r'<div class="tgme_widget_message_text js-message_text"[^>]*>(.*?)</div>', blk, re.S)
        d = re.search(r'<time[^>]*datetime="([^"]+)"', blk)
        text = ""
        if t:
            x = re.sub(r'<br\s*/?>', '\n', t.group(1)); x = re.sub(r'<[^>]+>', '', x); text = html.unescape(x)
            text = "\n".join(l.strip() for l in text.split("\n") if l.strip())
        out.append({"ch": ch, "id": mid, "time": d.group(1) if d else "", "text": text,
                    "photo": "tgme_widget_message_photo" in blk, "fwd": "tgme_widget_message_forwarded_from" in blk})
    return out
def crawl(ch):
    msgs = {}; before = None
    for p in range(PAGES):
        s = get(f"https://t.me/s/{ch}" + (f"?before={before}" if before else ""))
        page = parse(ch, s)
        if not page:
            print(ch, "empty page at", before, file=sys.stderr); break
        for m in page: msgs[m["id"]] = m
        mn = min(m["id"] for m in page)
        if before is not None and mn >= before: break
        before = mn
        time.sleep(0.4)
    return ch, list(msgs.values())
allm = []
with cf.ThreadPoolExecutor(4) as ex:
    for ch, ms in ex.map(crawl, CH):
        print(ch, len(ms), min((m["time"] for m in ms), default="-"), file=sys.stderr)
        allm += ms
json.dump(allm, open(sys.argv[2] if len(sys.argv) > 2 else "corpus.json", "w"), ensure_ascii=False)
print("total", len(allm), file=sys.stderr)
