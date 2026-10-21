#!/usr/bin/env python3
"""Тест разбора логов `--engine-audit` в tools/audit_summary.py: каждое число — в своём столбце, итог — сумма.

Запуск: python3 -m unittest discover -s tools -p 'test_*.py'
"""
import os
import subprocess
import sys
import tempfile
import unittest

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'audit_summary.py')

# строки в том виде, в каком их печатает EngineAudit; все числа разные — перепутанный столбец сразу виден
LOG = """\
duplicates: {dup} overlapping groups in 7229 samples
repost duplicates: {repost}
point duplicates: {point}
tracks: {tracks}; along corridor 24, forecast along corridor/via 100, via 62; launch explicit 131, inferred 2007
track quality ({q3} tracks with 3+ points): reversals {rev}, loops {loops}, towards Ukraine {ukr}, launch ahead {ahead}, towards city near line {city} → aud_x.tracks.tsv
two-point tracks: 4321, towards Ukraine {two}, shown without heading {nohd}
forecast back towards launch side: {back} of {fc} (+36 to the target stated in the latest message)
messages {msgs}, all-clears applied {clears}: idle {idle}, stuck {stuck}, wide {wide}; dropped {dropped} → aud_x.tsv
"""

A = dict(msgs=72294, tracks=9335, q3=1129, rev=1, loops=2, ukr=3, ahead=4, back=5, fc=3972,
         clears=2686, idle=1643, stuck=6, wide=7, dropped=8, dup=9, repost=10, point=11, city=12, two=13, nohd=14)
B = {k: v * 10 + 1 for k, v in A.items()}
ORDER = ['msgs', 'tracks', 'q3', 'rev', 'loops', 'ukr', 'ahead', 'back', 'fc',
         'clears', 'idle', 'stuck', 'wide', 'dropped', 'dup', 'repost', 'point', 'city', 'two', 'nohd']


def run(files):
    with tempfile.TemporaryDirectory() as d:
        for name, text in files.items():
            with open(os.path.join(d, name), 'w', encoding='utf-8') as f:
                f.write(text)
        return subprocess.run([sys.executable, SCRIPT, d], capture_output=True, text=True)


class AuditSummaryTest(unittest.TestCase):
    def test_columns_and_total(self):
        r = run({'2026-06.log': LOG.format(**A), '2026-07.log': LOG.format(**B)})
        self.assertEqual(r.returncode, 0, r.stderr)
        rows = [line.split('\t') for line in r.stdout.strip().split('\n')]
        self.assertEqual(len(rows[0]), 21)
        self.assertEqual(rows[1], ['2026-06'] + [str(A[k]) for k in ORDER])
        self.assertEqual(rows[2], ['2026-07'] + [str(B[k]) for k in ORDER])
        self.assertEqual(rows[3], ['ИТОГО'] + [str(A[k] + B[k]) for k in ORDER])

    def test_unfinished_month_fails(self):
        # аудит месяца оборвался (нет итоговой строки messages) — сводка не выдаёт нули за него молча
        r = run({'2026-06.log': LOG.format(**A), '2026-07.log': 'tracks: 5; along corridor 0\n'})
        self.assertNotEqual(r.returncode, 0)
        self.assertIn('2026-07', r.stderr)
        self.assertNotIn('2026-07\t', r.stdout)


if __name__ == '__main__':
    unittest.main()
