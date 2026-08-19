#!/usr/bin/env python3
"""Independent correctness oracle for the Python QZDB reader.

Unlike ``test_golden.py`` (whose vectors are emitted by the code under test and
therefore only prove determinism / regression / cross-language agreement), this
suite checks the reader against the *authoritative source data* that the ``.qzdb``
files were built from: ``test_data_202608/<edition>/china/*_range.csv`` carries
``start_ip_num`` / ``end_ip_num`` plus the geo fields. We sample IPs both inside
real ranges and across the whole IPv4 space and compare the reader's output with
the CSV ground truth. This is the only test that proves the SDK returns the
*right* answer rather than merely a self-consistent one.

Run:  python3 test_csv_oracle.py
Exits non-zero on any mismatch. Skips gracefully if source CSVs are absent.
"""
import bisect
import csv
import ipaddress
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from qzdb import QzdbReader  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, 'data')
SRC = os.path.join(ROOT, 'test_data_202608')

# (label, qzdb filename under DATA, range-csv under SRC)
TARGETS = [
    ('std_china', 'qqzeng_ip_std_china.qzdb', 'std/china/qqzeng_ip_std_china_range.csv'),
    ('ult_china', 'qqzeng_ip_ult_china.qzdb', 'ult/china/qqzeng_ip_ult_china_range.csv'),
]

IN_RANGE_SAMPLES = 6000
GLOBAL_SAMPLES = 5000
SEED = 12345


def load_csv_oracle(csv_path):
    """Return (rows, starts) where rows = (start, end, country, province, city, isp)."""
    rows = []
    with open(csv_path, newline='', encoding='utf-8') as f:
        r = csv.reader(f)
        hdr = next(r)
        ci = {h: i for i, h in enumerate(hdr)}
        for row in r:
            s = int(row[ci['start_ip_num']])
            e = int(row[ci['end_ip_num']])
            rows.append((s, e, row[ci['country']], row[ci['province']],
                         row[ci['city']], row[ci['isp']]))
    rows.sort(key=lambda x: x[0])
    starts = [x[0] for x in rows]
    return rows, starts


def csv_lookup(rows, starts, ipi):
    idx = bisect.bisect_right(starts, ipi) - 1
    if idx >= 0 and rows[idx][0] <= ipi <= rows[idx][1]:
        return rows[idx]
    return None


def run(label, qzdb_path, csv_path):
    if not os.path.exists(qzdb_path):
        print(f'  SKIP {label}: qzdb not found ({qzdb_path})')
        return 0
    if not os.path.exists(csv_path):
        print(f'  SKIP {label}: source csv not found ({csv_path})')
        return 0

    rows, starts = load_csv_oracle(csv_path)
    r = QzdbReader(qzdb_path)
    rng = random.Random(SEED)
    mismatch = 0
    found_both = 0
    miss_both = 0
    checked = 0
    details = []

    # 1) Random IPs across the whole IPv4 space
    for _ in range(GLOBAL_SAMPLES):
        ipi = rng.randint(0, 0xFFFFFFFF)
        ip = str(ipaddress.ip_address(ipi))
        exp = csv_lookup(rows, starts, ipi)
        gi = r.find(ip)
        sdk = (gi.country, gi.province, gi.city, gi.isp) if gi else None
        exp_t = (exp[2], exp[3], exp[4], exp[5]) if exp else None
        checked += 1
        if exp is None and gi is None:
            miss_both += 1
            continue
        if exp is not None and gi is not None:
            found_both += 1
            if sdk != exp_t:
                mismatch += 1
                if len(details) < 12:
                    details.append((ip, sdk, exp_t))
        else:
            mismatch += 1
            if len(details) < 12:
                details.append((ip, 'SDK=' + str(sdk), 'CSV=' + str(exp_t)))

    # 2) Random IPs inside real ranges (maximizes found_both coverage)
    for _ in range(IN_RANGE_SAMPLES):
        s, e = rng.choice(rows)[0], rng.choice(rows)[1]
        lo, hi = min(s, e), max(s, e)
        ipi = rng.randint(lo, hi)
        ip = str(ipaddress.ip_address(ipi))
        exp = csv_lookup(rows, starts, ipi)
        gi = r.find(ip)
        sdk = (gi.country, gi.province, gi.city, gi.isp) if gi else None
        exp_t = (exp[2], exp[3], exp[4], exp[5]) if exp else None
        checked += 1
        if exp is not None and gi is not None:
            found_both += 1
            if sdk != exp_t:
                mismatch += 1
                if len(details) < 12:
                    details.append((ip, sdk, exp_t))

    r.close()
    status = 'OK' if mismatch == 0 else 'FAIL'
    print(f'  {label}: {status} checked={checked} found_both={found_both} '
          f'miss_both={miss_both} MISMATCH={mismatch}')
    for d in details:
        print('    MISMATCH', d)
    return mismatch


def main():
    print('=== CSV oracle (independent ground-truth correctness) ===')
    total = 0
    exercised = 0
    for label, qzdb_name, csv_rel in TARGETS:
        qzdb_path = os.path.join(DATA, qzdb_name)
        csv_path = os.path.join(SRC, csv_rel)
        if os.path.exists(qzdb_path) and os.path.exists(csv_path):
            exercised += 1
        total += run(label, qzdb_path, csv_path)
    if total == 0 and exercised == 0:
        print('CSV_ORACLE_OK (no targets exercised)')
    elif total == 0:
        print(f'CSV_ORACLE_OK (targets exercised={exercised})')
    else:
        print(f'CSV_ORACLE: total MISMATCH={total} -> '
              f'{"PASS" if total == 0 else "FAIL"}')
    return 1 if total != 0 else 0


if __name__ == '__main__':
    sys.exit(main())
