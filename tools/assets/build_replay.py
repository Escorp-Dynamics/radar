#!/usr/bin/env python3
"""Собирает недельный срез корпуса сообщений для демо-воспроизведения: Radar.Core/Assets/replay.tsv.gz.
Формат строки: channel \t id \t unix_seconds \t text (переводы строк -> \\n, табуляции -> пробел).
Использование: build_replay.py /tmp/corpus/corpus.json [days]"""
import gzip, json, os, sys, datetime as D

src = sys.argv[1]
days = float(sys.argv[2]) if len(sys.argv) > 2 else 7
d = json.load(open(src))
d = [x for x in d if x.get('text', '').strip()]
end = max(D.datetime.fromisoformat(x['time']) for x in d)
start = end - D.timedelta(days=days)
rows = sorted((x for x in d if D.datetime.fromisoformat(x['time']) >= start), key=lambda x: (x['time'], x['ch'], x['id']))
with gzip.open(os.path.join(os.path.dirname(__file__), '..', '..', 'Radar.Core', 'Assets', 'replay.tsv.gz'), 'wt', encoding='utf-8', compresslevel=9) as f:
    for x in rows:
        t = int(D.datetime.fromisoformat(x['time']).timestamp())
        text = x['text'].replace('\\', '\\\\').replace('\t', ' ').replace('\r', '').replace('\n', '\\n')
        f.write(f"{x['ch']}\t{x['id']}\t{t}\t{text}\n")
print(len(rows), start.isoformat(), end.isoformat())
