#!/usr/bin/env python3
"""Пакетный архив (artifacts/archive-pack, по manifest.json) → JSON по месяцам для `Radar --engine-audit`.

Каталог назначения очищается от *.json перед записью: в аудит не попадают старые выгрузки.
Использование: audit_export.py [каталог_назначения]   (по умолчанию $RADAR_AUDIT_DIR или /tmp/months)
"""
import datetime
import gzip
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACK = os.path.join(ROOT, 'artifacts', 'archive-pack')


def unescape(s):
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == '\\' and i + 1 < len(s):
            out.append({'n': '\n', 't': '\t', 'r': '\r', '\\': '\\'}.get(s[i + 1], s[i + 1]))
            i += 2
        else:
            out.append(c)
            i += 1
    return ''.join(out)


def main():
    dest = sys.argv[1] if len(sys.argv) > 1 else os.environ.get('RADAR_AUDIT_DIR', '/tmp/months')
    os.makedirs(dest, exist_ok=True)
    for f in os.listdir(dest):
        if f.endswith('.json'):
            os.remove(os.path.join(dest, f))
    manifest = json.load(open(os.path.join(PACK, 'manifest.json'), encoding='utf-8'))
    total = 0
    for m in manifest['months']:
        out = []
        with gzip.open(os.path.join(PACK, m['file']), 'rt', encoding='utf-8') as fh:
            for line in fh:
                p = line.rstrip('\n').split('\t', 3)
                # как MessageCache.Parse: строка без канала или с нечисловыми id/временем пропускается
                if len(p) < 4 or not p[0]:
                    continue
                try:
                    mid, ts = int(p[1]), int(p[2])
                except ValueError:
                    continue
                out.append({'ch': p[0], 'id': mid,
                            'time': datetime.datetime.fromtimestamp(ts, datetime.timezone.utc).isoformat(),
                            'text': unescape(p[3])})
        if len(out) != m['messages']:
            sys.exit(f"{m['month']}: {len(out)} сообщений, в манифесте {m['messages']}")
        total += len(out)
        json.dump(out, open(os.path.join(dest, f"{m['month']}.json"), 'w', encoding='utf-8'), ensure_ascii=False)
    print(f'месяцев {len(manifest["months"])}, сообщений {total} → {dest}', file=sys.stderr)


if __name__ == '__main__':
    main()
