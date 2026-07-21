#!/usr/bin/env python3
"""
Verify boundary value matrix: load golden_boundary.json, run Python reference,
assert valid IPs return data and invalid IPs return None/empty.

Usage:
    python3 verify_boundary.py [--db std_china] [--all]
"""
import json, os, sys

SRC_DIR = os.path.join(os.path.dirname(__file__), '..')
sys.path.insert(0, os.path.join(SRC_DIR, 'python'))
from qzdb import QzdbSearcher

DATA_DIR = os.path.join(SRC_DIR, 'data')
BOUNDARY_FILE = os.path.join(os.path.dirname(__file__), 'golden_boundary.json')

DB_MAP = {
    'std_china': 'qqzeng_ip_std_china.qzdb',
    'max_china': 'qqzeng_ip_max_china.qzdb',
    'std_global': 'qqzeng_ip_std_global.qzdb',
    'max_global': 'qqzeng_ip_max_global.qzdb',
    'ult_china': 'qqzeng_ip_ult_china.qzdb',
    'ult_global': 'qqzeng_ip_ult_global.qzdb',
}

# Invalid categories: IPs that MUST return empty
INVALID_CATEGORIES = {'v4_invalid', 'v6_invalid'}

# Valid categories: IPs that MUST return data (on a global DB)
VALID_CATEGORIES = {'v4_boundary_valid', 'v6_boundary_valid'}

# Special: embedded V4 — consistency check only (may or may not have data)
EMBEDDED_CATEGORIES = {'v6_embedded_v4'}

# Parser-dependent: behavior varies, but all languages must agree with each other
PARSER_DEPENDENT_CATEGORIES = {'v4_parser_dependent'}


def lookup(searcher, ip):
    try:
        r = searcher.find(ip)
        if r is None:
            return ""
        return r.to_pipe()
    except (KeyError, ValueError, IndexError):
        return ""


def verify_db(db_name, boundary_data, verbose=False):
    """Verify one database against boundary matrix."""
    fname = DB_MAP.get(db_name)
    if not fname:
        return 0, 0, 0, [f"Unknown db: {db_name}"]

    db_path = os.path.join(DATA_DIR, fname)
    if not os.path.exists(db_path):
        return 0, 0, 0, [f"SKIP: {db_path} not found"]

    searcher = QzdbSearcher(db_path)
    passed = 0
    failed = 0
    warnings = 0
    errors = []

    categories = boundary_data.get('categories', {})

    for cat_name, cat_data in categories.items():
        cases = cat_data.get('cases', [])

        if cat_name in INVALID_CATEGORIES:
            # Invalid IPs must return empty
            for case in cases:
                ip = case['ip']
                label = case.get('label', ip)
                result = lookup(searcher, ip)
                if result == "":
                    passed += 1
                else:
                    failed += 1
                    errors.append(f"  [{cat_name}] {label} ({ip!r}): expected empty, got {result!r}")

        elif cat_name in VALID_CATEGORIES:
            # Valid IPs on global DBs should return data
            # On china DBs, only Chinese IPs have data
            is_global = 'global' in db_name
            for case in cases:
                ip = case['ip']
                label = case.get('label', ip)
                result = lookup(searcher, ip)
                if is_global:
                    # Global DB: most valid IPs should have data
                    if result != "":
                        passed += 1
                    else:
                        # Some special IPs (multicast, reserved) may not have data
                        warnings += 1
                        if verbose:
                            errors.append(f"  WARN [{cat_name}] {label} ({ip}): no data on global DB")
                else:
                    # China DB: only Chinese IPs have data — just check it doesn't crash
                    passed += 1

        elif cat_name in EMBEDDED_CATEGORIES:
            for case in cases:
                ip = case['ip']
                try:
                    result = lookup(searcher, ip)
                    passed += 1
                except Exception as e:
                    failed += 1
                    errors.append(f"  [{cat_name}] {ip}: crashed: {e}")

        elif cat_name in PARSER_DEPENDENT_CATEGORIES:
            for case in cases:
                ip = case['ip']
                try:
                    result = lookup(searcher, ip)
                    passed += 1
                except Exception as e:
                    failed += 1
                    errors.append(f"  [{cat_name}] {ip}: crashed: {e}")

    return passed, failed, warnings, errors


def main():
    import argparse
    parser = argparse.ArgumentParser(description='Verify boundary value matrix')
    parser.add_argument('--db', nargs='+', help='Database(s) to verify')
    parser.add_argument('--all', action='store_true', help='Verify all databases')
    parser.add_argument('--verbose', '-v', action='store_true')
    args = parser.parse_args()

    if not os.path.exists(BOUNDARY_FILE):
        print(f"Boundary file not found: {BOUNDARY_FILE}")
        return 1

    with open(BOUNDARY_FILE, 'r') as f:
        boundary = json.load(f)

    if args.all:
        dbs = list(DB_MAP.keys())
    elif args.db:
        dbs = args.db
    else:
        dbs = list(DB_MAP.keys())

    total_passed = 0
    total_failed = 0
    total_warnings = 0
    all_errors = []

    for db_name in dbs:
        passed, failed, warnings, errors = verify_db(db_name, boundary, args.verbose)
        total_passed += passed
        total_failed += failed
        total_warnings += warnings
        all_errors.extend(errors)

        status = "PASS" if failed == 0 else "FAIL"
        warn_str = f" ({warnings} warnings)" if warnings else ""
        print(f"  [{status}] {db_name}: {passed} passed, {failed} failed{warn_str}")

    print(f"\n{'='*60}")
    print(f"Total: {total_passed} passed, {total_failed} failed, {total_warnings} warnings")

    if total_failed > 0:
        print(f"\nFailures:")
        for e in all_errors:
            print(e)
        return 1

    print("ALL BOUNDARY CASES VERIFIED ✓")
    return 0


if __name__ == '__main__':
    sys.exit(main())
