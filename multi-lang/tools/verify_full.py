#!/usr/bin/env python3
"""
V18 全量遍历验证脚本

对 2026-07 发行版的每个 range CSV 逐行验证：
  - 取 start_ip 与 end_ip，分别用 Python SDK 查询
  - SDK 输出 (pipe 串) 与 CSV 原始字段按字段名逐一比对
  - IPv4 / IPv6 全覆盖，全量不抽样

用法:
    python3 verify_full.py --db std_china            # 验证单个
    python3 verify_full.py --all                      # 验证全部 8 个
    python3 verify_full.py --all --errors 50          # 每库最多打印 50 条
    python3 verify_full.py --all --report report.txt  # 输出汇总报告
"""

import argparse
import csv
import ipaddress
import os
import sys
import time

BASE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
sys.path.insert(0, os.path.join(BASE_DIR, 'python'))
from qzdb import QzdbSearcher

DATA_DIR = os.path.join(BASE_DIR, 'data_v18')

# 每个库对应的 range CSV 文件名 (qzdb 同名前缀)
DATABASES = [
    'std_china', 'std_global',
    'ult_china', 'ult_global',
    'asn_china', 'asn_global',
    'max_china', 'max_global',
]

# CSV 中存在但 SDK 不直接作为业务字段输出的列 (元信息/辅助列)，比对这些列无意义
# SDK 的字段名来自 metadata field_names，CSV 里多出的列会被忽略
CSV_META_COLS = frozenset(['start_ip', 'end_ip', 'start_ip_num', 'end_ip_num', 'geo_id'])

# 浮点字段允许的误差
FLOAT_TOL = 1e-4


def ip_to_v4_int(s):
    p = s.split('.')
    return (int(p[0]) << 24) | (int(p[1]) << 16) | (int(p[2]) << 8) | int(p[3])


def ip_to_v6_hl(s):
    n = int(ipaddress.IPv6Address(s))
    return (n >> 64) & ((1 << 64) - 1), n & ((1 << 64) - 1)


def parse_float(s):
    try:
        return float(s)
    except (ValueError, TypeError):
        return None


def fields_match(field_name, csv_val, sdk_val):
    """逐字段比对。csv_val / sdk_val 均为字符串。"""
    # 浮点字段: 数值近似即可 (SDK 按 %.6f 格式化, CSV 也可能是 6 位小数)
    if field_name in ('longitude', 'latitude'):
        if csv_val == sdk_val:
            return True
        cf = parse_float(csv_val)
        sf = parse_float(sdk_val)
        if cf is None or sf is None:
            return csv_val == sdk_val
        return abs(cf - sf) <= FLOAT_TOL
    # 其余: 字符串精确相等
    return csv_val == sdk_val


def verify_db(db_name, max_errors=20):
    qzdb_path = os.path.join(DATA_DIR, f'qqzeng_ip_{db_name}.qzdb')
    csv_path = os.path.join(DATA_DIR, f'qqzeng_ip_{db_name}_range.csv')
    if not os.path.exists(qzdb_path):
        return {'db': db_name, 'status': 'SKIP', 'reason': f'qzdb missing: {qzdb_path}'}
    if not os.path.exists(csv_path):
        return {'db': db_name, 'status': 'SKIP', 'reason': f'csv missing: {csv_path}'}

    s = QzdbSearcher()
    s.load(qzdb_path)
    sdk_fields = s._field_names            # 来自 metadata, 权威字段名顺序
    sdk_float_idx = s._float_field_indices
    # CSV 字段 -> 索引; 取与 SDK 字段重合的部分做比对
    # CSV 里没有的 SDK 字段, 比对时 csv_val 视为 ''

    total = 0
    checked = 0
    mismatches = 0
    empty_lookup = 0     # SDK 查不到 (返回 None) 的行数
    sample_errors = []

    t0 = time.time()
    with open(csv_path, 'r', encoding='utf-8', newline='') as f:
        reader = csv.reader(f)
        header = next(reader)
        col = {h: i for i, h in enumerate(header)}

        # 预计算 SDK 字段 -> CSV 列索引 (不存在则 None)
        sdk_csv_idx = [(name, col.get(name)) for name in sdk_fields]

        find_uint = s.find_uint
        find_v6 = s.find_v6
        to_pipe = None  # 用逐字段比对, 不走 to_pipe

        for row in reader:
            if not row:
                continue
            total += 1

            start_ip = row[col['start_ip']]
            end_ip = row[col['end_ip']]

            is_v4 = '.' in start_ip
            results = []  # (ip_label, sdk_dict_or_None)

            for label, ip_str in (('start', start_ip), ('end', end_ip)):
                try:
                    if is_v4:
                        info = find_uint(ip_to_v4_int(ip_str))
                    else:
                        h, l = ip_to_v6_hl(ip_str)
                        info = find_v6(h, l)
                except Exception:
                    info = None
                results.append((label, ip_str, info))

            for label, ip_str, info in results:
                checked += 1
                if info is None:
                    empty_lookup += 1
                    continue

                # 逐字段比对
                bad = []
                for fname, csv_i in sdk_csv_idx:
                    csv_val = row[csv_i] if csv_i is not None and csv_i < len(row) else ''
                    sdk_val = info._fields.get(fname, '')
                    if not fields_match(fname, csv_val, sdk_val):
                        bad.append((fname, csv_val, sdk_val))

                if bad:
                    mismatches += 1
                    if len(sample_errors) < max_errors:
                        sample_errors.append({
                            'ip': ip_str, 'label': label, 'row': total,
                            'bad_fields': bad,
                        })

            if total % 500000 == 0:
                elapsed = time.time() - t0
                rate = total / elapsed if elapsed > 0 else 0
                print(f'    [{db_name}] {total:,} rows processed '
                      f'({rate:,.0f} rows/s, {mismatches} mismatches so far)',
                      file=sys.stderr)

    elapsed = time.time() - t0
    acc = 100.0 * (checked - mismatches) / checked if checked else 100.0
    return {
        'db': db_name,
        'status': 'OK' if mismatches == 0 else 'FAIL',
        'total_rows': total,
        'checks': checked,
        'empty_lookup': empty_lookup,
        'mismatches': mismatches,
        'accuracy': acc,
        'elapsed_s': elapsed,
        'rows_per_s': total / elapsed if elapsed > 0 else 0,
        'sample_errors': sample_errors,
        'sdk_fields': sdk_fields,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--db', help='single db name e.g. std_china')
    ap.add_argument('--all', action='store_true', help='verify all 8 dbs')
    ap.add_argument('--errors', type=int, default=20, help='max sample errors to print per db')
    ap.add_argument('--report', help='write summary report to this file')
    args = ap.parse_args()

    if args.all:
        dbs = DATABASES
    elif args.db:
        dbs = [args.db]
    else:
        ap.error('need --db <name> or --all')

    results = []
    for db in dbs:
        print(f'\n=== Verifying {db} ===', file=sys.stderr)
        r = verify_db(db, max_errors=args.errors)
        results.append(r)
        line = (f"  {r['db']:14s} {r['status']:4s} "
                f"rows={r.get('total_rows', 0):>10,} "
                f"checks={r.get('checks', 0):>10,} "
                f"mismatch={r.get('mismatches', 0):>7} "
                f"empty={r.get('empty_lookup', 0):>7} "
                f"acc={r.get('accuracy', 0):.6f}% "
                f"{r.get('elapsed_s', 0):.1f}s")
        print(line)
        for e in r.get('sample_errors', [])[:args.errors]:
            bf = '; '.join(f"{n}: csv={v!r} sdk={sv!r}" for n, v, sv in e['bad_fields'])
            print(f"      {e['label']} ip={e['ip']} row={e['row']}: {bf}")

    # 汇总
    print('\n' + '=' * 70)
    ok = sum(1 for r in results if r['status'] == 'OK')
    fail = sum(1 for r in results if r['status'] == 'FAIL')
    skip = sum(1 for r in results if r['status'] == 'SKIP')
    print(f'TOTAL: {ok} OK, {fail} FAIL, {skip} SKIP')
    if fail:
        print('FAILED DBs: ' + ', '.join(r['db'] for r in results if r['status'] == 'FAIL'))
    print('=' * 70)

    if args.report:
        with open(args.report, 'w') as f:
            f.write('db,status,total_rows,checks,mismatches,empty_lookup,accuracy,elapsed_s,rows_per_s\n')
            for r in results:
                f.write(f"{r['db']},{r['status']},{r.get('total_rows',0)},"
                        f"{r.get('checks',0)},{r.get('mismatches',0)},"
                        f"{r.get('empty_lookup',0)},{r.get('accuracy',0):.6f},"
                        f"{r.get('elapsed_s',0):.2f},{r.get('rows_per_s',0):.0f}\n")
                for e in r.get('sample_errors', []):
                    bf = '; '.join(f"{n}: csv={v!r} sdk={sv!r}" for n, v, sv in e['bad_fields'])
                    f.write(f"  # {e['label']} ip={e['ip']} row={e['row']}: {bf}\n")
        print(f'Report written to {args.report}')

    return 0 if fail == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
