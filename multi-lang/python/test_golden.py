"""Tier 2 golden verification for the QZDB Python SDK (API contract §10).

Asserts ``find(ip).to_pipe()`` matches the language-agnostic golden vectors for
every entry in ``tools/golden_vectors.json``. Must finish with 0 failures.

    python3 test_golden.py
"""

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import qzdb
from qzdb import QzdbReader

ROOT = os.path.dirname(os.path.abspath(__file__))
GOLDEN = os.path.join(ROOT, '..', 'tools', 'golden_vectors.json')
DATA_DIR = os.path.join(ROOT, '..', 'data')
STD = os.path.join(DATA_DIR, 'qqzeng_ip_std_china.qzdb')
ULT = os.path.join(DATA_DIR, 'qqzeng_ip_ult_china.qzdb')


def main():
    with open(GOLDEN, 'r', encoding='utf-8') as f:
        g = json.load(f)

    r_std = QzdbReader(STD)
    r_ult = QzdbReader(ULT)
    readers = {'std_china': r_std, 'ult_china': r_ult}

    total = 0
    fail = 0
    for dbk, reader in readers.items():
        for cat in ('random_v4', 'random_v6', 'boundary_v4', 'boundary_v6', 'invalid'):
            if cat not in g[dbk]:
                continue
            for e in g[dbk][cat]:
                ip = e['ip']
                exp = e.get('expected', '')
                gi = reader.find(ip)
                got = gi.to_pipe() if gi is not None else ''
                total += 1
                if got != exp:
                    fail += 1
                    if fail <= 8:
                        print(f'  MISMATCH {dbk}/{cat} {ip!r}: got={got!r} exp={exp!r}')

    r_std.close()
    r_ult.close()

    print(f'Tier2 golden: total={total} fail={fail}')
    if fail == 0:
        print('TIER2_OK')
        sys.exit(0)
    else:
        print('TIER2_FAIL')
        sys.exit(1)


if __name__ == '__main__':
    main()
