#!/usr/bin/env python3
"""
Cross-Language IP Database Verification Tool

Reads range CSV files, generates test cases (start_ip, end_ip, random_ip),
runs Python reference to get expected results, then runs all other language
SDKs and compares outputs.

Usage:
    # Run all databases
    python3 cross_verify.py --all

    # Run specific databases
    python3 cross_verify.py --databases std_china,max_china

    # Run specific languages only (skip slow ones)
    python3 cross_verify.py --skip-langs php,java

    # Exhaustive mode (no sampling)
    python3 cross_verify.py --exhaustive --databases std_china
"""

import argparse
import csv
import hashlib
import ipaddress
import os
import random
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed

BASE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
REPO_DIR = os.path.abspath(os.path.join(BASE_DIR, '..'))
TOOLS_DIR = os.path.dirname(__file__)
TEST_CASES_DIR = os.path.join(TOOLS_DIR, 'test_cases')
RESULTS_DIR = os.path.join(TOOLS_DIR, 'results')

# Python reference
sys.path.insert(0, os.path.join(BASE_DIR, 'python'))
from qzdb import QzdbSearcher


# ── Database configuration ──────────────────────────────────────────────
# Each entry: (db_name, csv_path_rel, field_count_in_csv, sample_rate)
# sample_rate: every Nth row to test (1 = exhaustive)
DATABASES = [
    {
        'name': 'std_china',
        'csv_rel': 'qqzeng_ip_std_china_range.csv',
        'qzdb_rel': 'multi-lang/data/qqzeng_ip_std_china.qzdb',
        'sample_v4': 1,    # exhaustive for small DB
        'sample_v6': 1,
    },
    {
        'name': 'max_china',
        'csv_rel': 'qqzeng_ip_max_china_range.csv',
        'qzdb_rel': 'multi-lang/data/qqzeng_ip_max_china.qzdb',
        'sample_v4': 2,    # every 2nd row
        'sample_v6': 2,
    },
    {
        'name': 'max_global',
        'csv_rel': 'qqzeng_ip_max_global_range.csv',
        'qzdb_rel': 'multi-lang/data/qqzeng_ip_max_global.qzdb',
        'sample_v4': 20,   # every 20th row (2.8M rows → ~140K)
        'sample_v6': 20,
    },
    {
        'name': 'std_global',
        'csv_rel': 'qqzeng_ip_std_global_range.csv',
        'qzdb_rel': 'multi-lang/data/qqzeng_ip_std_global.qzdb',
        'sample_v4': 10,
        'sample_v6': 10,
    },
    {
        'name': 'ult_china',
        'csv_rel': 'qqzeng_ip_ult_china_range.csv',
        'qzdb_rel': 'multi-lang/data/qqzeng_ip_ult_china.qzdb',
        'sample_v4': 5,
        'sample_v6': 5,
    },
    {
        'name': 'ult_global',
        'csv_rel': 'qqzeng_ip_ult_global_range.csv',
        'qzdb_rel': 'multi-lang/data/qqzeng_ip_ult_global.qzdb',
        'sample_v4': 20,
        'sample_v6': 20,
    },
]


def make_pipe_string(info):
    """Format GeoInfo to pipe-separated string using the SDK's own to_pipe()."""
    if info is None:
        return ''
    return info.to_pipe()


def is_v4_ip(ip_str):
    return '.' in ip_str


def parse_csv_row(row, header):
    """Parse a CSV row into a dict."""
    d = {}
    for i, h in enumerate(header):
        val = row[i].strip() if i < len(row) else ''
        d[h] = val
    return d


def ip_str_to_uint32(ip_str):
    """Convert IPv4 string to uint32."""
    parts = ip_str.split('.')
    return (int(parts[0]) << 24) | (int(parts[1]) << 16) | (int(parts[2]) << 8) | int(parts[3])


def ip_str_to_uint128(ip_str):
    """Convert IPv6 string to (high, low) uint64 pair."""
    ip = ipaddress.IPv6Address(ip_str)
    ip_int = int(ip)
    high = (ip_int >> 64) & 0xFFFFFFFFFFFFFFFF
    low = ip_int & 0xFFFFFFFFFFFFFFFF
    return high, low


def random_ip_in_v4_range(start_num, end_num, rng):
    """Generate a random IP uint32 within [start_num, end_num]."""
    if start_num >= end_num:
        return start_num
    return rng.randint(start_num, end_num)


def random_ip_in_v6_range(start_str, end_str, rng):
    """Generate a random IPv6 (high, low) within the range."""
    start_ip = ipaddress.IPv6Address(start_str)
    end_ip = ipaddress.IPv6Address(end_str)
    start_int = int(start_ip)
    end_int = int(end_ip)
    if start_int >= end_int:
        return (start_int >> 64) & 0xFFFFFFFFFFFFFFFF, start_int & 0xFFFFFFFFFFFFFFFF
    rand_int = rng.randint(start_int, end_int)
    high = (rand_int >> 64) & 0xFFFFFFFFFFFFFFFF
    low = rand_int & 0xFFFFFFFFFFFFFFFF
    return high, low


def generate_test_cases(db_config, rng):
    """Generate test cases from a range CSV file.
    
    Returns: list of tuples (ip_key, ip_type, ip_str, row_number, expected_pipe)
      - for V4: ip_key = uint32 string
      - for V6: ip_key = "high:low"
    """
    csv_path = os.path.join(REPO_DIR, db_config['csv_rel'])
    if not os.path.exists(csv_path):
        print(f"  ⚠ CSV not found: {csv_path}")
        return []
    
    qzdb_path = os.path.join(REPO_DIR, db_config['qzdb_rel'])
    if not os.path.exists(qzdb_path):
        print(f"  ⚠ QZDB not found: {qzdb_path}")
        return []
    
    # Load Python reference
    searcher = QzdbSearcher()
    searcher.load(qzdb_path)
    field_names = searcher._field_names
    float_indices = searcher._float_field_indices
    
    cases = []
    
    with open(csv_path, 'r', encoding='utf-8') as f:
        reader = csv.reader(f)
        header = next(reader)
        
        # Determine column indices
        col_start_ip = header.index('start_ip')
        col_end_ip = header.index('end_ip')
        
        for row_idx, row in enumerate(reader):
            if not row or len(row) < 2:
                continue
            
            start_ip = row[col_start_ip].strip()
            end_ip = row[col_end_ip].strip()
            
            is_v4 = '.' in start_ip
            
            # Sampling
            if is_v4:
                if row_idx % db_config['sample_v4'] != 0:
                    continue
            else:
                if row_idx % db_config['sample_v6'] != 0:
                    continue
            
            try:
                if is_v4:
                    start_num = ip_str_to_uint32(start_ip)
                    end_num = ip_str_to_uint32(end_ip)
                    
                    cases.append((str(start_num), 'start', start_ip, row_idx, ''))
                    if end_num != start_num:
                        cases.append((str(end_num), 'end', end_ip, row_idx, ''))
                    
                    # Random IP
                    rand_ip = random_ip_in_v4_range(start_num, end_num, rng)
                    if rand_ip != start_num and rand_ip != end_num:
                        cases.append((str(rand_ip), 'rand', f'random/{row_idx}', row_idx, ''))
                else:
                    start_h, start_l = ip_str_to_uint128(start_ip)
                    end_h, end_l = ip_str_to_uint128(end_ip)
                    
                    key_start = f'{start_h}:{start_l}'
                    key_end = f'{end_h}:{end_l}'
                    
                    cases.append((key_start, 'start', start_ip, row_idx, ''))
                    if (end_h, end_l) != (start_h, start_l):
                        cases.append((key_end, 'end', end_ip, row_idx, ''))
                    
                    # Random IP
                    rand_h, rand_l = random_ip_in_v6_range(start_ip, end_ip, rng)
                    key_rand = f'{rand_h}:{rand_l}'
                    if key_rand != key_start and key_rand != key_end:
                        cases.append((key_rand, 'rand', f'random/{row_idx}', row_idx, ''))
            except Exception as e:
                print(f"  ⚠ Error processing row {row_idx}: {e}")
                continue
    
    # Deduplicate by ip_key
    seen = set()
    unique_cases = []
    for c in cases:
        if c[0] not in seen:
            seen.add(c[0])
            unique_cases.append(c)
    
    print(f"  Generated {len(unique_cases)} test cases ({len(seen)} unique IPs)")
    return unique_cases


def generate_expected(cases, qzdb_path):
    """Query Python reference for each test case to get expected results."""
    import sys
    sys.path.insert(0, os.path.join(BASE_DIR, 'python'))
    from qzdb import QzdbSearcher
    
    searcher = QzdbSearcher()
    searcher.load(qzdb_path)
    
    results = []
    for ip_key, ip_type, ip_str, row_idx, _ in cases:
        try:
            if ':' in ip_key:
                # V6
                high, low = ip_key.split(':')
                high = int(high)
                low = int(low)
                info = searcher.find_v6_uint((high << 64) | low)
            else:
                # V4
                ip_int = int(ip_key)
                info = searcher.find_uint(ip_int)
            
            pipe_str = make_pipe_string(info)
            results.append((ip_key, ip_type, ip_str, row_idx, pipe_str))
        except Exception as e:
            results.append((ip_key, ip_type, ip_str, row_idx, f'__ERROR__:{e}'))
    
    return results


def save_test_cases(results, db_name):
    """Save test cases and expected results to files."""
    os.makedirs(TEST_CASES_DIR, exist_ok=True)
    
    # Full test file with expected results
    v4_path = os.path.join(TEST_CASES_DIR, f'{db_name}_v4.txt')
    v6_path = os.path.join(TEST_CASES_DIR, f'{db_name}_v6.txt')
    
    v4_expected_path = os.path.join(TEST_CASES_DIR, f'{db_name}_v4_expected.txt')
    v6_expected_path = os.path.join(TEST_CASES_DIR, f'{db_name}_v6_expected.txt')
    
    v4_ips = []
    v4_expected = []
    v6_ips = []
    v6_expected = []
    
    for ip_key, ip_type, ip_str, row_idx, pipe_str in results:
        if ':' in ip_key:
            v6_ips.append(f'{ip_key}')
            v6_expected.append(f'{ip_key}|{pipe_str}')
        else:
            v4_ips.append(f'{ip_key}')
            v4_expected.append(f'{ip_key}|{pipe_str}')
    
    with open(v4_path, 'w') as f:
        f.write('\n'.join(v4_ips) + '\n')
    with open(v4_expected_path, 'w') as f:
        f.write('\n'.join(v4_expected) + '\n')
    with open(v6_path, 'w') as f:
        f.write('\n'.join(v6_ips) + '\n')
    with open(v6_expected_path, 'w') as f:
        f.write('\n'.join(v6_expected) + '\n')
    
    print(f"  Saved {len(v4_ips)} V4 + {len(v6_ips)} V6 test cases")
    return v4_path, v6_path, v4_expected_path, v6_expected_path


# ── Language Runners ────────────────────────────────────────────────────

LANGUAGES = {}

def register_language(name, runner_func):
    LANGUAGES[name] = runner_func

# Python runner
def run_python(db_name, qzdb_path, v4_test, v6_test, v4_out, v6_out):
    sys.path.insert(0, os.path.join(BASE_DIR, 'python'))
    from qzdb import QzdbSearcher
    
    searcher = QzdbSearcher()
    searcher.load(qzdb_path)
    
    # V4
    v4_results = []
    with open(v4_test) as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            ip_int = int(line)
            info = searcher.find_uint(ip_int)
            pipe_str = make_pipe_string(info)
            v4_results.append(f'{line}|{pipe_str}')
    with open(v4_out, 'w') as f:
        f.write('\n'.join(v4_results) + '\n')
    
    # V6
    v6_results = []
    try:
        with open(v6_test) as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                high, low = line.split(':')
                high = int(high)
                low = int(low)
                info = searcher.find_v6_uint((high << 64) | low)
                pipe_str = make_pipe_string(info)
                v6_results.append(f'{line}|{pipe_str}')
    except FileNotFoundError:
        pass
    
    with open(v6_out, 'w') as f:
        f.write('\n'.join(v6_results) + '\n')
    
    return True

register_language('python', run_python)


def run_language_subprocess(cmd_parts, v4_test, v6_test, v4_out, v6_out, timeout=300):
    """Run a language batch query via subprocess."""
    try:
        result = subprocess.run(
            cmd_parts,
            capture_output=True,
            text=True,
            timeout=timeout
        )
        return result.returncode == 0, result.stdout, result.stderr
    except subprocess.TimeoutExpired:
        return False, '', f'TIMEOUT after {timeout}s'
    except FileNotFoundError as e:
        return False, '', f'Not found: {e}'


# ── Comparison ──────────────────────────────────────────────────────────

def compare_results(db_name, results_dir):
    """Compare all language results for a database."""
    import glob
    
    # Find all result files
    v4_files = {}
    v6_files = {}
    
    for f in os.listdir(results_dir):
        if f.startswith(f'{db_name}_') and f.endswith('_v4.txt'):
            lang = f.replace(f'{db_name}_', '').replace('_v4.txt', '')
            v4_files[lang] = os.path.join(results_dir, f)
        if f.startswith(f'{db_name}_') and f.endswith('_v6.txt'):
            lang = f.replace(f'{db_name}_', '').replace('_v6.txt', '')
            v6_files[lang] = os.path.join(results_dir, f)
    
    ref_lang = 'python'
    all_pass = True
    
    # Compare V4
    if v4_files:
        ref_file = v4_files.get(ref_lang)
        if not ref_file:
            print(f"  ⚠ No reference (python) result for {db_name} V4")
            all_pass = False
        else:
            with open(ref_file) as f:
                ref_lines = [l.strip() for l in f if l.strip()]
            ref_dict = {}
            for line in ref_lines:
                if '|' in line:
                    ip_key, val = line.split('|', 1)
                    ref_dict[ip_key] = val
            
            for lang, fpath in v4_files.items():
                if lang == ref_lang:
                    continue
                with open(fpath) as f:
                    lang_lines = [l.strip() for l in f if l.strip()]
                mismatches = 0
                checked = 0
                for line in lang_lines:
                    if '|' not in line:
                        continue
                    ip_key, val = line.split('|', 1)
                    if ip_key in ref_dict:
                        checked += 1
                        if val != ref_dict[ip_key]:
                            mismatches += 1
                            if mismatches <= 5:
                                print(f"    V4 MISMATCH [{lang}] IP={ip_key}")
                                print(f"      python: {ref_dict[ip_key][:60]}...")
                                print(f"      {lang}:  {val[:60]}...")
                
                status = '✓' if mismatches == 0 else '✗'
                print(f"  V4 [{lang}]: {status} {checked} checked, {mismatches} mismatches")
                if mismatches > 0:
                    all_pass = False
    
    # Compare V6
    if v6_files:
        ref_file = v6_files.get(ref_lang)
        if not ref_file:
            print(f"  ⚠ No reference (python) result for {db_name} V6")
            all_pass = False
        else:
            with open(ref_file) as f:
                ref_lines = [l.strip() for l in f if l.strip()]
            ref_dict = {}
            for line in ref_lines:
                if '|' in line:
                    ip_key, val = line.split('|', 1)
                    ref_dict[ip_key] = val
            
            for lang, fpath in v6_files.items():
                if lang == ref_lang:
                    continue
                with open(fpath) as f:
                    lang_lines = [l.strip() for l in f if l.strip()]
                mismatches = 0
                checked = 0
                for line in lang_lines:
                    if '|' not in line:
                        continue
                    ip_key, val = line.split('|', 1)
                    if ip_key in ref_dict:
                        checked += 1
                        if val != ref_dict[ip_key]:
                            mismatches += 1
                            if mismatches <= 5:
                                print(f"    V6 MISMATCH [{lang}] IP={ip_key}")
                                print(f"      python: {ref_dict[ip_key][:60]}...")
                                print(f"      {lang}:  {val[:60]}...")
                
                status = '✓' if mismatches == 0 else '✗'
                print(f"  V6 [{lang}]: {status} {checked} checked, {mismatches} mismatches")
                if mismatches > 0:
                    all_pass = False
    
    return all_pass


# ── Main ────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description='Cross-language IP database verifier')
    parser.add_argument('--all', action='store_true', help='Test all databases')
    parser.add_argument('--databases', type=str, default='', help='Comma-separated list of databases to test')
    parser.add_argument('--skip-langs', type=str, default='', help='Comma-separated languages to skip')
    parser.add_argument('--generate-only', action='store_true', help='Only generate test cases, do not run')
    parser.add_argument('--compare-only', action='store_true', help='Only compare existing results')
    parser.add_argument('--exhaustive', action='store_true', help='Exhaustive mode (no sampling)')
    args = parser.parse_args()
    
    skip_langs = set(args.skip_langs.split(',')) if args.skip_langs else set()
    
    # Select databases
    if args.all:
        db_list = DATABASES
    elif args.databases:
        db_names = set(args.databases.split(','))
        db_list = [d for d in DATABASES if d['name'] in db_names]
    else:
        # Default: test small/medium databases
        db_list = [d for d in DATABASES if d['name'] in ('std_china', 'max_china')]
    
    if args.exhaustive:
        for db in db_list:
            db['sample_v4'] = 1
            db['sample_v6'] = 1
    
    if not db_list:
        print("No databases selected")
        return
    
    os.makedirs(TEST_CASES_DIR, exist_ok=True)
    os.makedirs(RESULTS_DIR, exist_ok=True)
    
    overall_pass = True
    
    for db_config in db_list:
        db_name = db_config['name']
        qzdb_path = os.path.join(REPO_DIR, db_config['qzdb_rel'])
        csv_path = os.path.join(REPO_DIR, db_config['csv_rel'])
        
        print(f"\n{'='*60}")
        print(f"  Database: {db_name}")
        print(f"{'='*60}")
        
        if not os.path.exists(qzdb_path):
            print(f"  ⚠ SKIP: {qzdb_path} not found")
            continue
        if not os.path.exists(csv_path):
            print(f"  ⚠ SKIP: {csv_path} not found")
            continue
        
        # Phase 1: Generate test cases
        print(f"\n  ── Generating test cases ──")
        rng = random.Random(42)
        cases = generate_test_cases(db_config, rng)
        if not cases:
            print(f"  ⚠ No test cases generated")
            continue
        
        results = generate_expected(cases, qzdb_path)
        v4_test, v6_test, v4_exp, v6_exp = save_test_cases(results, db_name)
        
        if args.generate_only:
            continue
        
        # Phase 2: Run all languages
        print(f"\n  ── Running language SDKs ──")
        
        lang_results_dir = RESULTS_DIR
        
        # Python reference
        v4_py = os.path.join(lang_results_dir, f'{db_name}_python_v4.txt')
        v6_py = os.path.join(lang_results_dir, f'{db_name}_python_v6.txt')
        if not args.compare_only:
            print(f"  Running: python")
            run_python(db_name, qzdb_path, v4_test, v6_test, v4_py, v6_py)
        
        # C
        if 'c' not in skip_langs and not args.compare_only:
            v4_c = os.path.join(lang_results_dir, f'{db_name}_c_v4.txt')
            v6_c = os.path.join(lang_results_dir, f'{db_name}_c_v6.txt')
            c_runner = os.path.join(TOOLS_DIR, 'batch_c')
            if os.path.exists(c_runner):
                print(f"  Running: C")
                ok, out, err = run_language_subprocess(
                    [c_runner, qzdb_path, v4_test, v4_c, v6_test, v6_c],
                    v4_test, v6_test, v4_c, v6_c
                )
                if not ok:
                    print(f"    C FAILED: {err[:200]}")
            else:
                print(f"  Skipping: C (binary not found, build with tools/build_all.sh)")
        
        # Go
        if 'go' not in skip_langs and not args.compare_only:
            v4_go = os.path.join(lang_results_dir, f'{db_name}_go_v4.txt')
            v6_go = os.path.join(lang_results_dir, f'{db_name}_go_v6.txt')
            go_runner = os.path.join(TOOLS_DIR, 'batch_go')
            if os.path.exists(go_runner):
                print(f"  Running: Go")
                ok, out, err = run_language_subprocess(
                    [go_runner, qzdb_path, v4_test, v4_go, v6_test, v6_go],
                    v4_test, v6_test, v4_go, v6_go
                )
                if not ok:
                    print(f"    Go FAILED: {err[:200]}")
            else:
                print(f"  Skipping: Go (binary not found)")
        
        # Rust
        if 'rust' not in skip_langs and not args.compare_only:
            v4_rs = os.path.join(lang_results_dir, f'{db_name}_rust_v4.txt')
            v6_rs = os.path.join(lang_results_dir, f'{db_name}_rust_v6.txt')
            rs_runner = os.path.join(TOOLS_DIR, 'batch_rust')
            if os.path.exists(rs_runner):
                print(f"  Running: Rust")
                ok, out, err = run_language_subprocess(
                    [rs_runner, qzdb_path, v4_test, v4_rs, v6_test, v6_rs],
                    v4_test, v6_test, v4_rs, v6_rs
                )
                if not ok:
                    print(f"    Rust FAILED: {err[:200]}")
            else:
                print(f"  Skipping: Rust (binary not found)")
        
        # Node.js
        if 'node' not in skip_langs and 'nodejs' not in skip_langs and not args.compare_only:
            v4_js = os.path.join(lang_results_dir, f'{db_name}_nodejs_v4.txt')
            v6_js = os.path.join(lang_results_dir, f'{db_name}_nodejs_v6.txt')
            js_runner = os.path.join(TOOLS_DIR, 'batch_query.js')
            if os.path.exists(js_runner):
                print(f"  Running: Node.js")
                ok, out, err = run_language_subprocess(
                    ['node', js_runner, qzdb_path, v4_test, v4_js, v6_test, v6_js],
                    v4_test, v6_test, v4_js, v6_js
                )
                if not ok:
                    print(f"    Node.js FAILED: {err[:200]}")
            else:
                print(f"  Skipping: Node.js (script not found)")
        
        # PHP
        if 'php' not in skip_langs and not args.compare_only:
            v4_php = os.path.join(lang_results_dir, f'{db_name}_php_v4.txt')
            v6_php = os.path.join(lang_results_dir, f'{db_name}_php_v6.txt')
            php_runner = os.path.join(TOOLS_DIR, 'batch_query.php')
            if os.path.exists(php_runner):
                print(f"  Running: PHP")
                ok, out, err = run_language_subprocess(
                    ['php', php_runner, qzdb_path, v4_test, v4_php, v6_test, v6_php],
                    v4_test, v6_test, v4_php, v6_php
                )
                if not ok:
                    print(f"    PHP FAILED: {err[:200]}")
            else:
                print(f"  Skipping: PHP (script not found)")
        
        # Java
        if 'java' not in skip_langs and not args.compare_only:
            v4_java = os.path.join(lang_results_dir, f'{db_name}_java_v4.txt')
            v6_java = os.path.join(lang_results_dir, f'{db_name}_java_v6.txt')
            java_runner = os.path.join(TOOLS_DIR, 'batch_java.sh')
            if os.path.exists(java_runner):
                print(f"  Running: Java")
                ok, out, err = run_language_subprocess(
                    ['bash', java_runner, qzdb_path, v4_test, v4_java, v6_test, v6_java],
                    v4_test, v6_test, v4_java, v6_java
                )
                if not ok:
                    print(f"    Java FAILED: {err[:200]}")
            else:
                print(f"  Skipping: Java (script not found)")
        
        # C#
        if 'csharp' not in skip_langs and 'c#' not in skip_langs and 'netcore' not in skip_langs and not args.compare_only:
            v4_cs = os.path.join(lang_results_dir, f'{db_name}_csharp_v4.txt')
            v6_cs = os.path.join(lang_results_dir, f'{db_name}_csharp_v6.txt')
            cs_runner = os.path.join(TOOLS_DIR, 'batch_csharp.sh')
            cs_project = os.path.join(TOOLS_DIR, 'batch_csharp')
            if 'csharp' in skip_langs:
                print(f"  Skipping: C# (requested)")
            elif not (os.path.exists(cs_runner) and os.path.isdir(cs_project)):
                print(f"  Skipping: C# (project not found)")
            else:
                print(f"  Running: C#")
                ok, out, err = run_language_subprocess(
                    ['bash', cs_runner, qzdb_path, v4_test, v4_cs, v6_test, v6_cs],
                    v4_test, v6_test, v4_cs, v6_cs
                )
                if not ok:
                    print(f"    C# FAILED: {err[:200]}")
        
        # Phase 3: Compare
        print(f"\n  ── Comparing results ──")
        db_pass = compare_results(db_name, lang_results_dir)
        if db_pass:
            print(f"  ★ {db_name}: ALL LANGUAGES MATCH")
        else:
            print(f"  ✗ {db_name}: MISMATCHES DETECTED")
            overall_pass = False
    
    print(f"\n{'='*60}")
    if overall_pass:
        print(f"  ★ OVERALL: ALL TESTS PASSED")
    else:
        print(f"  ✗ OVERALL: SOME TESTS FAILED")
    print(f"{'='*60}")
    return 0 if overall_pass else 1


if __name__ == '__main__':
    sys.exit(main())
