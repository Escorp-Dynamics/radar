#!/usr/bin/env python3
"""Сводка логов `Radar --engine-audit` по месяцам в TSV (формат artifacts/validation/engine-audit-months.tsv).

Использование: audit_summary.py [каталог_с_логами]   (по умолчанию $RADAR_AUDIT_DIR или /tmp/months)
"""
import glob
import os
import re
import sys

HEAD = ['месяц', 'сообщ', 'треков', '3+т', 'развор', 'петли', 'к_Укр', 'пуск_впер', 'прогн_назад', 'прогнозов',
        'отбоев', 'холост', 'завис', 'широк', 'отброш', 'дубли', 'репост', 'точк_дубл', 'к_городу', 'к_Укр_2т', 'без_курса']


def grab(pattern, text):
    m = re.search(pattern, text)
    return [int(x) for x in m.groups()] if m else [0] * re.compile(pattern).groups


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else os.environ.get('RADAR_AUDIT_DIR', '/tmp/months')
    rows, failed = [], []
    for f in sorted(glob.glob(os.path.join(src, '20*.log'))):
        s = open(f, encoding='utf-8').read()
        month = os.path.basename(f)[:7]
        # месяц засчитывается, только если аудит дошёл до конца (последняя строка отчёта — прогнозы);
        # иначе метрики упавшего на середине прогона были бы нулями
        if 'messages ' not in s or 'forecast back towards launch side' not in s:
            failed.append(month)
            continue
        tracks, = grab(r'tracks: (\d+);', s)
        quality = grab(r'\((\d+) tracks with 3\+ points\): reversals (\d+), loops (\d+), towards Ukraine (\d+), launch ahead (\d+)', s)
        forecast = grab(r'forecast back towards launch side: (\d+) of (\d+)', s)
        clears = grab(r'messages (\d+), all-clears applied (\d+): idle (\d+), stuck (\d+), wide (\d+); dropped (\d+)', s)
        dup, = grab(r'duplicates: (\d+) overlapping', s)
        repost, = grab(r'repost duplicates: (\d+)', s)
        point, = grab(r'point duplicates: (\d+)', s)
        city, = grab(r'towards city near line (\d+)', s)
        two, = grab(r'two-point tracks: \d+, towards Ukraine (\d+)', s)
        noheading, = grab(r'shown without heading (\d+)', s)
        rows.append([month, clears[0], tracks] + quality + forecast + clears[1:] + [dup, repost, point, city, two, noheading])
    print('\t'.join(HEAD))
    for r in rows:
        print('\t'.join(map(str, r)))
    print('\t'.join(map(str, ['ИТОГО'] + [sum(r[i] for r in rows) for i in range(1, len(HEAD))])))
    if failed:
        sys.exit('аудит не завершился: ' + ', '.join(failed))


if __name__ == '__main__':
    main()
