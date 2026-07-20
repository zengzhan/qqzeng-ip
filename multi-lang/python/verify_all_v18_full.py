"""
QZDB V18 全量验证工具

遍历 data_v18/ 下所有 8 个数据库的 CSV range 文件（每一行），测试：
  1. 区间起始 IP  → 必须命中正确数据
  2. 区间结束 IP  → 必须命中正确数据
  3. 区间内随机 IP → 必须命中正确数据
  4. QPS 基准测试

IPv4 / IPv6 全覆盖，不抽样。
"""
import csv
import ipaddress
import os
import sys
import time
import random
import argparse

sys.path.insert(0, os.path.dirname(__file__))
from qzdb import QzdbSearcher

BASE = os.path.join(os.path.dirname(__file__), '..', 'data_v18')
RNG = random.Random(42)

# ── 各版本的 CSV 数据字段列映射 (列号 → GeoInfo 字段名) ──
# CSV 格式: start_ip(0), end_ip(1), start_ip_num(2), end_ip_num(3), 数据字段(4+)
FIELD_MAP = {
    'std':  {4: 'continent', 5: 'country', 6: 'province', 7: 'city', 8: 'isp'},
    'ult':  {4: 'continent', 5: 'country', 6: 'province', 7: 'city', 8: 'district',
             9: 'isp', 10: 'area_code', 11: 'country_english', 12: 'country_code',
             13: 'longitude', 14: 'latitude'},
    'asn':  {4: 'asn', 5: 'asn_org', 6: 'asn_domain', 7: 'usage_type',
             8: 'country', 9: 'country_code', 10: 'isp'},
    'max':  {4: 'continent', 5: 'country', 6: 'province', 7: 'city', 8: 'district',
             9: 'isp', 10: 'area_code', 11: 'country_english', 12: 'country_code',
             13: 'country_alpha3', 14: 'province_en', 15: 'city_en',
             16: 'longitude', 17: 'latitude'},
}

FLOAT_FIELDS = frozenset(['longitude', 'latitude'])


def get_db_info(name):
    """Parse name like 'std_china' or 'max_global' into (version, region)."""
    parts = name.split('_', 1)
    return parts[0], parts[1]


def parse_v6_cidr(cidr):
    """Parse IPv6 CIDR, return (start_int, end_int)."""
    net = ipaddress.IPv6Network(cidr, strict=False)
    n = net.num_addresses
    if n == 0:
        return 0, 0
    s = int(net.network_address)
    e = int(net.broadcast_address) if n > 1 else s
    return s, e


def build_expected(csv_row, version):
    """Build a dict of expected values from CSV row based on version field map."""
    expected = {}
    for col, field in FIELD_MAP[version].items():
        if col < len(csv_row):
            expected[field] = csv_row[col].strip()
        else:
            expected[field] = ''
    return expected


def geo_info_to_dict(info, version):
    """Convert GeoInfo object to dict using field map for this version."""
    result = {}
    if info is None:
        for col, field in FIELD_MAP[version].items():
            result[field] = ''
        return result

    # Build from geo info object attributes
    for col, field in FIELD_MAP[version].items():
        val = getattr(info, field, '')
        if val is None:
            val = ''
        result[field] = str(val)
    return result


def compare_expected(expected, actual, version):
    """Compare expected vs actual dicts. Returns (is_match, mismatches_list)."""
    mismatches = []
    for field in FIELD_MAP[version].values():
        e = expected.get(field, '').strip()
        a = actual.get(field, '').strip()
        if field in FLOAT_FIELDS:
            # Float fields: fuzzy compare
            try:
                ef = float(e) if e else 0.0
                af = float(a) if a else 0.0
                if abs(ef - af) > 0.0001:
                    mismatches.append((field, e, a))
            except ValueError:
                if e != a:
                    mismatches.append((field, e, a))
        else:
            # Empty vs empty → match
            if not e and not a:
                continue
            if e != a:
                mismatches.append((field, e, a))
    return len(mismatches) == 0, mismatches


def verify_database(name):
    """
    Full traversal verification for one database.
    Returns (total_rows, total_checks, pass_count, fail_count, elapsed, qps, v4_rows, v6_rows).
    """
    version, region = get_db_info(name)
    qzdb_path = os.path.join(BASE, f'qqzeng_ip_{name}.qzdb')
    csv_path = os.path.join(BASE, f'qqzeng_ip_{name}_range.csv')

    if not os.path.exists(qzdb_path):
        print(f'  [{name}] SKIP: qzdb not found: {qzdb_path}')
        return None
    if not os.path.exists(csv_path):
        print(f'  [{name}] SKIP: CSV not found: {csv_path}')
        return None

    # Load qzdb
    searcher = QzdbSearcher(qzdb_path)

    # Open CSV
    with open(csv_path, 'r', encoding='utf-8') as f:
        reader = csv.reader(f)
        header = next(reader)  # skip header

        total_rows = 0
        v4_rows = 0
        v6_rows = 0
        v6_uncovered = 0
        pass_count = 0
        fail_count = 0
        sample_fails = []
        total_queries = 0

        start_time = time.time()

        for i, row in enumerate(reader):
            total_rows += 1
            if len(row) < 4:
                continue

            start_ip = row[0].strip()
            is_v6 = ':' in start_ip

            expected = build_expected(row, version)

            if is_v6:
                v6_rows += 1
                try:
                    s_int, e_int = parse_v6_cidr(start_ip)
                except Exception:
                    continue
                if s_int == 0 and e_int == 0:
                    continue

                # Check if database has V6 data at all
                sample_addr_v6 = (s_int >> 64) & 0xFFFFFFFFFFFFFFFF, s_int & 0xFFFFFFFFFFFFFFFF
                probe = searcher.find_v6(sample_addr_v6[0], sample_addr_v6[1])
                if probe is None:
                    v6_uncovered += 1
                    continue

                count = e_int - s_int + 1

                # Test start
                total_queries += 1
                high = (s_int >> 64) & 0xFFFFFFFFFFFFFFFF
                low = s_int & 0xFFFFFFFFFFFFFFFF
                result = searcher.find_v6(high, low)
                actual = geo_info_to_dict(result, version)
                ok, fails = compare_expected(expected, actual, version)
                if ok:
                    pass_count += 1
                else:
                    fail_count += 1
                    if len(sample_fails) < 10:
                        sample_fails.append(f'  V6 ROW={i} CIDR={start_ip} test=start '
                                            f'FAILS={fails}')
                    continue

                # Test end
                total_queries += 1
                high = (e_int >> 64) & 0xFFFFFFFFFFFFFFFF
                low = e_int & 0xFFFFFFFFFFFFFFFF
                result = searcher.find_v6(high, low)
                actual = geo_info_to_dict(result, version)
                ok, fails = compare_expected(expected, actual, version)
                if ok:
                    pass_count += 1
                else:
                    fail_count += 1
                    if len(sample_fails) < 10:
                        sample_fails.append(f'  V6 ROW={i} CIDR={start_ip} test=end '
                                            f'FAILS={fails}')

                # Test random within range
                if count > 2:
                    total_queries += 1
                    offset = RNG.randint(0, min(count - 1, 0xFFFFFFFF))
                    rnd_int = s_int + offset
                    high = (rnd_int >> 64) & 0xFFFFFFFFFFFFFFFF
                    low = rnd_int & 0xFFFFFFFFFFFFFFFF
                    result = searcher.find_v6(high, low)
                    actual = geo_info_to_dict(result, version)
                    ok, fails = compare_expected(expected, actual, version)
                    if ok:
                        pass_count += 1
                    else:
                        fail_count += 1
                        if len(sample_fails) < 10:
                            sample_fails.append(f'  V6 ROW={i} CIDR={start_ip} test=random '
                                                f'FAILS={fails}')

            else:
                v4_rows += 1
                try:
                    start_num = int(row[2]) if row[2] else 0
                    end_num = int(row[3]) if row[3] else 0
                except (ValueError, IndexError):
                    continue

                if start_num == 0 and end_num == 0:
                    continue

                count = end_num - start_num + 1

                # Test start
                total_queries += 1
                result = searcher.find_uint(start_num)
                actual = geo_info_to_dict(result, version)
                ok, fails = compare_expected(expected, actual, version)
                if ok:
                    pass_count += 1
                else:
                    fail_count += 1
                    if len(sample_fails) < 10:
                        sample_fails.append(f'  V4 ROW={i} start={start_ip} '
                                            f'test=start FAILS={fails}')

                # Test end
                total_queries += 1
                result = searcher.find_uint(end_num)
                actual = geo_info_to_dict(result, version)
                ok, fails = compare_expected(expected, actual, version)
                if ok:
                    pass_count += 1
                else:
                    fail_count += 1
                    if len(sample_fails) < 10:
                        sample_fails.append(f'  V4 ROW={i} end={row[1]} '
                                            f'test=end FAILS={fails}')

                # Test random within range
                if count > 2:
                    total_queries += 1
                    rnd_num = RNG.randint(start_num, end_num)
                    result = searcher.find_uint(rnd_num)
                    actual = geo_info_to_dict(result, version)
                    ok, fails = compare_expected(expected, actual, version)
                    if ok:
                        pass_count += 1
                    else:
                        fail_count += 1
                        if len(sample_fails) < 10:
                            sample_fails.append(f'  V4 ROW={i} start={start_ip} '
                                                f'test=random FAILS={fails}')

            if total_rows % 10000 == 0:
                elapsed = time.time() - start_time
                qps = total_queries / elapsed if elapsed > 0 else 0
                sys.stdout.write(f'\r  [{name}] ... {total_rows:,} rows, '
                                 f'{total_queries:,} checks, '
                                 f'{fail_count} fails, '
                                 f'{qps / 1000000:.2f}M qps')
                sys.stdout.flush()

        elapsed = time.time() - start_time
        qps = total_queries / elapsed if elapsed > 0 else 0

        print()  # newline after progress
        print(f'  [{name}] ====== DONE ======')
        print(f'    Total rows:    {total_rows:,}')
        print(f'    V4 rows:       {v4_rows:,}')
        print(f'    V6 rows:       {v6_rows:,}')
        print(f'    V6 uncovered:  {v6_uncovered:,}')
        print(f'    Total checks:  {total_queries:,}')
        print(f'    Pass:          {pass_count:,}')
        print(f'    Fail:          {fail_count:,}')
        if total_queries > 0:
            acc = 100.0 * pass_count / total_queries
            print(f'    Accuracy:      {acc:.6f}%')
        print(f'    Elapsed:       {elapsed:.2f}s')
        print(f'    QPS:           {qps / 1000000:.2f}M/s')
        if sample_fails:
            print(f'    Sample failures (first {len(sample_fails)}):')
            for sf in sample_fails:
                print(f'      {sf}')

        return {
            'name': name,
            'total_rows': total_rows,
            'v4_rows': v4_rows,
            'v6_rows': v6_rows,
            'v6_uncovered': v6_uncovered,
            'total_checks': total_queries,
            'pass': pass_count,
            'fail': fail_count,
            'accuracy': 100.0 * pass_count / total_queries if total_queries > 0 else 0,
            'elapsed': elapsed,
            'qps': qps,
        }


def benchmark_qzdb(name, searcher=None, num_v4=1_000_000, num_v6=500_000):
    """
    Pure QPS benchmark (no verification, just random queries).
    Uses the same pattern as IPDBTestV18.cs.
    """
    qzdb_path = os.path.join(BASE, f'qqzeng_ip_{name}.qzdb')
    if not os.path.exists(qzdb_path):
        return None

    if searcher is None:
        searcher = QzdbSearcher(qzdb_path)

    rng = random.Random(42)

    # Generate random V4 IPs
    v4_ips = [rng.randint(0, 0xFFFFFFFF) for _ in range(num_v4)]

    # Generate random V6 IPs (with 2000::/3 prefix like global unicast)
    v6_ips = []
    for _ in range(num_v6):
        hi = rng.getrandbits(64)
        lo = rng.getrandbits(64)
        hi = (hi & 0x1FFFFFFFFFFFFFFF) | 0x2000000000000000  # 2000::/3
        v6_ips.append((hi, lo))

    # Warmup
    _ = searcher.find_uint(v4_ips[0])
    _ = searcher.find_v6(v6_ips[0][0], v6_ips[0][1])

    # V4 benchmark
    t0 = time.perf_counter()
    for ip in v4_ips:
        searcher.find_uint(ip)
    t1 = time.perf_counter()
    v4_qps = num_v4 / (t1 - t0)

    # V6 benchmark
    t0 = time.perf_counter()
    for hi, lo in v6_ips:
        searcher.find_v6(hi, lo)
    t1 = time.perf_counter()
    v6_qps = num_v6 / (t1 - t0)

    return {
        'name': name,
        'v4_qps': v4_qps,
        'v6_qps': v6_qps,
        'v4_count': num_v4,
        'v6_count': num_v6,
    }


def main():
    parser = argparse.ArgumentParser(description='QZDB V18 全量验证')
    parser.add_argument('--dbs', nargs='+',
                        default=['std_china', 'std_global', 'ult_china', 'ult_global',
                                 'asn_china', 'asn_global', 'max_china', 'max_global'],
                        help='Databases to verify')
    parser.add_argument('--benchmark', action='store_true', default=True,
                        help='Run QPS benchmark after verification')
    parser.add_argument('--verify-only', action='store_true',
                        help='Skip QPS benchmark')
    args = parser.parse_args()

    print('=' * 70)
    print('  QZDB V18 全量验证工具')
    print('  --- 完整遍历每一行 CSV range 数据 ---')
    print('  --- 测试: 起始IP | 结束IP | 区间内随机IP ---')
    print('=' * 70)

    all_results = []
    all_bench = []

    for db_name in args.dbs:
        print(f'\n{"─" * 70}')
        print(f'  [{db_name}] 开始全量验证...')
        print(f'{"─" * 70}')
        result = verify_database(db_name)
        if result:
            all_results.append(result)

    # ── 汇总 ──
    print(f'\n\n{"=" * 70}')
    print('  📊 全量验证汇总')
    print('=' * 70)
    print(f'  {"DB":<20} {"行数":>12} {"V4":>10} {"V6":>10} {"检查":>12} {"通过":>10} {"失败":>8} {"准确率":>10} {"耗时":>8} {"QPS":>10}')
    print(f'  {"─" * 20} {"─" * 12} {"─" * 10} {"─" * 10} {"─" * 12} {"─" * 10} {"─" * 8} {"─" * 10} {"─" * 8} {"─" * 10}')

    grand_rows = 0
    grand_checks = 0
    grand_pass = 0
    grand_fail = 0
    grand_elapsed = 0

    for r in all_results:
        acc_str = f'{r["accuracy"]:.4f}%' if r["accuracy"] < 100 else '100%'
        print(f'  {r["name"]:<20} {r["total_rows"]:>12,} {r["v4_rows"]:>10,} '
              f'{r["v6_rows"]:>10,} {r["total_checks"]:>12,} '
              f'{r["pass"]:>10,} {r["fail"]:>8,} '
              f'{acc_str:>10} {r["elapsed"]:>7.1f}s {r["qps"] / 1000000:.2f}M/s')
        grand_rows += r['total_rows']
        grand_checks += r['total_checks']
        grand_pass += r['pass']
        grand_fail += r['fail']
        grand_elapsed += r['elapsed']

    grand_acc = 100.0 * grand_pass / grand_checks if grand_checks > 0 else 0
    print(f'  {"─" * 20} {"─" * 12} {"─" * 10} {"─" * 10} {"─" * 12} {"─" * 10} {"─" * 8} {"─" * 10} {"─" * 8} {"─" * 10}')
    print(f'  {"总计":<20} {grand_rows:>12,} {"":>10} {"":>10} '
          f'{grand_checks:>12,} {grand_pass:>10,} {grand_fail:>8,} '
          f'{grand_acc:.4f}% {grand_elapsed:>7.1f}s')

    if grand_fail == 0:
        print(f'\n  ✅ 所有 {grand_checks:,} 次查询全部通过！解析完全准确。')
    else:
        print(f'\n  ❌ 发现 {grand_fail:,} 次失败，请检查上面的失败详情。')

    # ── QPS 基准测试 ──
    if not args.verify_only:
        print(f'\n\n{"=" * 70}')
        print('  ⚡ QPS 性能基准测试')
        print('=' * 70)

        for db_name in args.dbs:
            print(f'\n  [{db_name}] QPS 基准...')
            bench = benchmark_qzdb(db_name)
            if bench:
                all_bench.append(bench)
                print(f'    V4: {bench["v4_qps"] / 1000000:.2f}M QPS  '
                      f'({bench["v4_count"]:,} queries)')
                print(f'    V6: {bench["v6_qps"] / 1000000:.2f}M QPS  '
                      f'({bench["v6_count"]:,} queries)')

        print(f'\n{"─" * 70}')
        print(f'  {"DB":<20} {"V4 QPS":>15} {"V6 QPS":>15}')
        print(f'  {"─" * 20} {"─" * 15} {"─" * 15}')
        for b in all_bench:
            print(f'  {b["name"]:<20} {b["v4_qps"] / 1000000:>13.2f}M/s '
                  f'{b["v6_qps"] / 1000000:>13.2f}M/s')

    print(f'\n{"=" * 70}')
    print('  验证完成')
    print('=' * 70)

    return 0 if grand_fail == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
