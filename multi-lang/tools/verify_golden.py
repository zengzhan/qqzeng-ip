#!/usr/bin/env python3
"""
Verify golden vectors: load golden_vectors.json, re-run Python reference, assert byte-exact match.

Usage:
    python3 verify_golden.py [--db std_china] [--all]
"""
import json, os, sys

SRC_DIR = os.path.join(os.path.dirname(__file__), '..')
sys.path.insert(0, os.path.join(SRC_DIR, 'python'))
from qzdb import QzdbSearcher

DATA_DIR = os.path.join(SRC_DIR, 'data')
GOLDEN_FILE = os.path.join(os.path.dirname(__file__), 'golden_vectors.json')

DB_MAP = {
    'std_china': 'qqzeng_ip_std_china.qzdb',
    'max_china': 'qqzeng_ip_max_china.qzdb',
    'std_global': 'qqzeng_ip_std_global.qzdb',
    'max_global': 'qqzeng_ip_max_global.qzdb',
    'ult_china': 'qqzeng_ip_ult_china.qzdb',
    'ult_global': 'qqzeng_ip_ult_global.qzdb',
}


def lookup(searcher, ip):
    try:
        r = searcher.find(ip)
        if r is None:
            return ""
        return r.to_pipe()
    except (KeyError, ValueError, IndexError):
        return ""


def verify_db(db_name, golden_data, verbose=False):
    """Verify one database against golden vectors."""
    fname = DB_MAP.get(db_name)
    if not fname:
        return 0, 0, [f"Unknown db: {db_name}"]

    db_path = os.path.join(DATA_DIR, fname)
    if not os.path.exists(db_path):
        return 0, 0, [f"SKIP: {db_path} not found"]

    searcher = QzdbSearcher(db_path)
    passed = 0
    failed = 0
    errors = []

    for category in ['random_v4', 'random_v6', 'boundary_v4', 'boundary_v6', 'invalid']:
        cases = golden_data.get(category, [])
        for entry in cases:
            ip = entry['ip']
            expected = entry['expected']
            actual = lookup(searcher, ip)

            if actual == expected:
                passed += 1
            else:
                failed += 1
                label = entry.get('label', '')
                errors.append(f"  [{category}] {label or ip}: expected={expected!r} actual={actual!r}")
                if verbose and len(errors) <= 10:
                    print(f"  FAIL: {ip} expected={expected!r} actual={actual!r}")

    return passed, failed, errors


def main():
    import argparse
    parser = argparse.ArgumentParser(description='Verify golden vectors')
    parser.add_argument('--db', nargs='+', help='Database(s) to verify')
    parser.add_argument('--all', action='store_true', help='Verify all databases')
    parser.add_argument('--verbose', '-v', action='store_true')
    args = parser.parse_args()

    if not os.path.exists(GOLDEN_FILE):
        print(f"Golden file not found: {GOLDEN_FILE}")
        print("Run gen_golden_vectors.py first.")
        return 1

    with open(GOLDEN_FILE, 'r') as f:
        golden = json.load(f)

    if args.all:
        dbs = list(golden.keys())
    elif args.db:
        dbs = args.db
    else:
        dbs = list(golden.keys())

    total_passed = 0
    total_failed = 0
    all_errors = []

    for db_name in dbs:
        if db_name not in golden:
            print(f"  SKIP {db_name}: not in golden file")
            continue

        passed, failed, errors = verify_db(db_name, golden[db_name], args.verbose)
        total_passed += passed
        total_failed += failed
        all_errors.extend(errors)

        status = "PASS" if failed == 0 else "FAIL"
        print(f"  [{status}] {db_name}: {passed} passed, {failed} failed")

    print(f"\n{'='*60}")
    print(f"Total: {total_passed} passed, {total_failed} failed")

    if total_failed > 0:
        print(f"\nFirst 20 failures:")
        for e in all_errors[:20]:
            print(e)
        return 1

    print("ALL GOLDEN VECTORS VERIFIED ✓")
    return 0


if __name__ == '__main__':
    sys.exit(main())
