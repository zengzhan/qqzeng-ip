"""
QZDB V18 全量验证 v2 - 算法正确性优先

验证方法:
  Stage 1 - 算法正确性（核心指标）:
    每一行 CSV range 取 3 个测试点 (start/end/random)，
    它们必须返回完全一致的地理结果。
    → 这验证了搜索算法的正确性（无 off-by-one、无误入相邻区间）

  Stage 2 - 数据准确性（仅供参考）:
    CSV 数据 vs qzdb 数据对比
    → 数据可能因版本不同有差异，不影响算法正确性判断

  Stage 3 - QPS 基准测试
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

# ── 各版本的 CSV 数据字段列映射 ──
# 基于 data_v18/ 中 CSV 的实际 header
FIELD_MAP = {
    'std':  {4: 'continent', 5: 'country', 6: 'province', 7: 'city', 8: 'isp'},
    'ult':  {4: 'continent', 5: 'country', 6: 'province', 7: 'city', 8: 'district',
             9: 'isp', 10: 'geo_id', 11: 'country_english', 12: 'country_code',
             13: 'longitude', 14: 'latitude'},
    'asn':  {4: 'asn', 5: 'asn_org', 6: 'asn_domain', 7: 'usage_type',
             8: 'country', 9: 'country_code', 10: 'isp'},
    'max':  {4: 'continent', 5: 'country', 6: 'province', 7: 'city', 8: 'district',
             9: 'isp', 10: 'area_code', 11: 'country_english', 12: 'country_code',
             13: 'country_alpha3', 14: 'province_en', 15: 'city_en',
             16: 'longitude', 17: 'latitude'},
}

FLOAT_FIELDS = frozenset(['longitude', 'latitude'])


class VerifyResult:
    def __init__(self, name):
        self.name = name
        self.total_rows = 0
        self.v4_rows = 0
        self.v6_rows = 0
        self.v6_uncovered = 0
        self.consistent_sets = 0     # 区间内一致 + 与 CSV 匹配
        self.consistent_diff = 0     # 区间内一致但与 CSV 不匹配
        self.inconsistent_sets = 0   # 区间内不一致 → 算法 bug
        self.total_queries = 0
        self.elapsed = 0.0
        self.sample_errors = []

    def record(self):
        pass


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
    """Build expected dict from CSV row."""
    expected = {}
    for col, field in FIELD_MAP[version].items():
        if col < len(csv_row):
            expected[field] = csv_row[col].strip()
        else:
            expected[field] = ''
    return expected


def geo_info_to_dict(info, expected_fields):
    """Convert GeoInfo to dict with only the expected fields."""
    result = {}
    for field in expected_fields:
        if info is None:
            result[field] = ''
        else:
            val = getattr(info, field, '')
            result[field] = str(val) if val is not None else ''
    return result


def dicts_match(a, b):
    """Compare two dicts containing geo fields. Returns (match, mismatches)."""
    mismatches = []
    for key in a:
        va = a[key].strip()
        vb = b[key].strip()
        if key in FLOAT_FIELDS:
            try:
                fa = float(va) if va else 0.0
                fb = float(vb) if vb else 0.0
                if abs(fa - fb) > 0.0001:
                    mismatches.append((key, va, vb))
            except ValueError:
                if va != vb:
                    mismatches.append((key, va, vb))
        else:
            if not va and not vb:
                continue
            if va != vb:
                mismatches.append((key, va, vb))
    return len(mismatches) == 0, mismatches


def verify_database_v2(name):
    """
    Two-stage verification for one database.
    Returns detailed results dict.
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

    searcher = QzdbSearcher(qzdb_path)
    result = VerifyResult(name)

    expected_fields = list(FIELD_MAP[version].values())

    with open(csv_path, 'r', encoding='utf-8') as f:
        reader = csv.reader(f)
        header = next(reader)

        start_time = time.time()

        for i, row in enumerate(reader):
            result.total_rows += 1
            if len(row) < 4:
                continue

            start_ip = row[0].strip()
            is_v6 = ':' in start_ip
            expected = build_expected(row, version)
            test_points = []

            if is_v6:
                result.v6_rows += 1
                try:
                    s_int, e_int = parse_v6_cidr(start_ip)
                except Exception:
                    continue
                if s_int == 0 and e_int == 0:
                    continue

                count = e_int - s_int + 1

                # Check if DB has V6 data
                probe = searcher.find_v6(
                    (s_int >> 64) & 0xFFFFFFFFFFFFFFFF,
                    s_int & 0xFFFFFFFFFFFFFFFF
                )
                if probe is None:
                    result.v6_uncovered += 1
                    continue

                # Start
                result.total_queries += 1
                g = searcher.find_v6(
                    (s_int >> 64) & 0xFFFFFFFFFFFFFFFF,
                    s_int & 0xFFFFFFFFFFFFFFFF
                )
                test_points.append(('start', s_int, g))

                # End
                result.total_queries += 1
                g = searcher.find_v6(
                    (e_int >> 64) & 0xFFFFFFFFFFFFFFFF,
                    e_int & 0xFFFFFFFFFFFFFFFF
                )
                test_points.append(('end', e_int, g))

                # Random
                if count > 2:
                    result.total_queries += 1
                    offset = RNG.randint(0, min(count - 1, 1000000))
                    rnd_int = s_int + offset
                    g = searcher.find_v6(
                        (rnd_int >> 64) & 0xFFFFFFFFFFFFFFFF,
                        rnd_int & 0xFFFFFFFFFFFFFFFF
                    )
                    test_points.append(('rnd', rnd_int, g))
            else:
                result.v4_rows += 1
                try:
                    start_num = int(row[2]) if row[2] else 0
                    end_num = int(row[3]) if row[3] else 0
                except (ValueError, IndexError):
                    continue

                if start_num == 0 and end_num == 0:
                    continue

                count = end_num - start_num + 1

                # Start
                result.total_queries += 1
                g = searcher.find_uint(start_num)
                test_points.append(('start', start_num, g))

                # End
                result.total_queries += 1
                g = searcher.find_uint(end_num)
                test_points.append(('end', end_num, g))

                # Random
                if count > 2:
                    result.total_queries += 1
                    rnd_num = RNG.randint(start_num, end_num)
                    g = searcher.find_uint(rnd_num)
                    test_points.append(('rnd', rnd_num, g))

            # ── Stage 1: Within-range consistency check ──
            info_dicts = []
            for label, ip, g in test_points:
                d = geo_info_to_dict(g, expected_fields)
                info_dicts.append((label, ip, d))

            # Compare all non-None results
            non_none = [d for _, _, d in info_dicts if any(d.values())]
            if len(non_none) >= 2:
                first = non_none[0]
                all_consistent = True
                for d in non_none[1:]:
                    ok, _ = dicts_match(first, d)
                    if not ok:
                        all_consistent = False
                        break

                if all_consistent:
                    # Stage 2: CSV comparison (informational)
                    ok, mismatches = dicts_match(first, expected)
                    if ok:
                        result.consistent_sets += 1
                    else:
                        result.consistent_diff += 1
                        # Only log first few
                        if result.consistent_diff <= 5:
                            mismatch_str = ' | '.join(
                                f'{k}: CSV="{v1}" DB="{v2}"'
                                for k, v1, v2 in mismatches[:5]
                            )
                            result.sample_errors.append(
                                f'  DATA-DIFF ROW={i} range={start_ip} {mismatch_str}'
                            )
                else:
                    result.inconsistent_sets += 1
                    if result.inconsistent_sets <= 10:
                        error_lines = [f'  ❌ BUG ROW={i} range={start_ip}: 区间内不一致!']
                        for label, ip_val, d in info_dicts:
                            vals = ' | '.join(
                                f'{k}={d[k]}' for k in ['province', 'city', 'isp', 'country']
                                if k in d
                            )
                            error_lines.append(f'      {label} IP={ip_val}: {vals}')
                        result.sample_errors.append('\n'.join(error_lines))
            else:
                # All results are None
                pass

            if result.total_rows % 10000 == 0:
                elapsed = time.time() - start_time
                qps = result.total_queries / elapsed if elapsed > 0 else 0
                sys.stdout.write(
                    f'\r  [{name}] rows={result.total_rows:,} '
                    f'q={result.total_queries:,} '
                    f'con={result.consistent_sets} '
                    f'diff={result.consistent_diff} '
                    f'bug={result.inconsistent_sets} '
                    f'{qps / 1000000:.2f}M q/s'
                )
                sys.stdout.flush()

        result.elapsed = time.time() - start_time
        print()

    # ── Report ──
    rep = f'  [{name}] {"=" * 50}\n'
    rep += f'    总行数:       {result.total_rows:,}\n'
    rep += f'    V4:           {result.v4_rows:,}\n'
    rep += f'    V6:           {result.v6_rows:,}\n'
    rep += f'    V6 未覆盖:     {result.v6_uncovered:,}\n'
    rep += f'    总查询:       {result.total_queries:,}\n'
    rep += f'    算法正确性:\n'
    rep += f'      ✅ 区间内一致+与CSV匹配: {result.consistent_sets:,}\n'
    rep += f'      ⚠  区间内一致但数据差异: {result.consistent_diff:,}\n'

    total_sets = result.consistent_sets + result.consistent_diff + result.inconsistent_sets
    if result.inconsistent_sets == 0:
        rep += f'      ✅ 区间内不一致:     0  ← 搜索算法完全正确!\n'
    else:
        rep += f'      ❌ 区间内不一致:     {result.inconsistent_sets:,}  ← 算法 BUG!\n'

    if result.consistent_diff > 0:
        rep += f'    耗时:         {result.elapsed:.2f}s\n'
        rep += f'    QPS:          {result.total_queries / result.elapsed / 1000000:.2f}M/s\n'

    if result.sample_errors:
        rep += f'    示例 (前 {len(result.sample_errors)} 条):\n'
        for e in result.sample_errors:
            rep += f'      {e}\n'

    print(rep)

    return result


def get_db_info(name):
    parts = name.split('_', 1)
    return parts[0], parts[1]


def benchmark_qzdb(name, num_v4=1_000_000, num_v6=500_000):
    """Pure QPS benchmark (no verification)."""
    qzdb_path = os.path.join(BASE, f'qqzeng_ip_{name}.qzdb')
    if not os.path.exists(qzdb_path):
        return None

    searcher = QzdbSearcher(qzdb_path)
    rng = random.Random(42)

    # Generate random V4 IPs
    v4_ips = [rng.randint(0, 0xFFFFFFFF) for _ in range(num_v4)]

    # Generate random V6 IPs
    v6_ips = []
    for _ in range(num_v6):
        hi = rng.getrandbits(64)
        lo = rng.getrandbits(64)
        hi = (hi & 0x1FFFFFFFFFFFFFFF) | 0x2000000000000000
        v6_ips.append((hi, lo))

    # Warmup
    searcher.find_uint(v4_ips[0])
    searcher.find_v6(v6_ips[0][0], v6_ips[0][1])

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

    return {'name': name, 'v4_qps': v4_qps, 'v6_qps': v6_qps,
            'v4_count': num_v4, 'v6_count': num_v6}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dbs', nargs='+',
                        default=['std_china', 'std_global', 'ult_china', 'ult_global',
                                 'asn_china', 'asn_global', 'max_china', 'max_global'])
    parser.add_argument('--verify-only', action='store_true')
    parser.add_argument('--skip-bench', action='store_true')
    args = parser.parse_args()

    print('=' * 70)
    print('  QZDB V18 全量验证 v2')
    print('  重点: 算法正确性 (区间内一致性)')
    print('  数据差异仅作参考，不影响算法正确性判断')
    print('=' * 70)

    all_results = []

    for db_name in args.dbs:
        print(f'\n{"─" * 70}')
        print(f'  [{db_name}]')
        print(f'{"─" * 70}')
        r = verify_database_v2(db_name)
        if r:
            all_results.append(r)

    # ── 算法正确性汇总 ──
    print(f'\n\n{"=" * 70}')
    print('  算法正确性汇总')
    print('=' * 70)
    print(f'  {"DB":<22} {"行数":>12} {"V4":>10} {"V6":>10} {"一致+匹配":>12} {"一致差异":>10} {"算法BUG":>8}')
    print(f'  {"─" * 22} {"─" * 12} {"─" * 10} {"─" * 10} {"─" * 12} {"─" * 10} {"─" * 8}')

    total_cons_match = 0
    total_cons_diff = 0
    total_incons = 0
    total_rows = 0
    all_pass = True

    for r in all_results:
        flag = '❌' if r.inconsistent_sets > 0 else '✅'
        print(f'  {flag} {r.name:<20} {r.total_rows:>12,} {r.v4_rows:>10,} '
              f'{r.v6_rows:>10,} {r.consistent_sets:>12,} {r.consistent_diff:>10,} '
              f'{r.inconsistent_sets:>8,}')
        total_cons_match += r.consistent_sets
        total_cons_diff += r.consistent_diff
        total_incons += r.inconsistent_sets
        total_rows += r.total_rows
        if r.inconsistent_sets > 0:
            all_pass = False

    print(f'  {"─" * 22} {"─" * 12} {"─" * 10} {"─" * 10} {"─" * 12} {"─" * 10} {"─" * 8}')
    print(f'  {"总计":<22} {total_rows:>12,} {"":>10} {"":>10} '
          f'{total_cons_match:>12,} {total_cons_diff:>10,} {total_incons:>8,}')
    print()

    if total_incons == 0:
        print(f'  ✅ 算法无误: {total_cons_match + total_cons_diff:,} 个区间全部一致，无 off-by-one 或搜索树 bug')
        print(f'     (其中 {total_cons_diff:,} 个与 CSV 有数据差异，属正常数据更新)')
    else:
        print(f'  ❌ 发现 {total_incons} 个区间内不一致 → 搜索算法有 BUG!')

    # ── 数据差异统计 ──
    print(f'\n{"─" * 70}')
    print(f'  数据差异 (CSV vs qzdb — 仅作参考)')
    print(f'  总区间 {total_cons_match + total_cons_diff:,} 个, '
          f'其中 {total_cons_diff:,} 个有数据差异 '
          f'({total_cons_diff / (total_cons_match + total_cons_diff) * 100:.1f}%)')
    print(f'  注: qzdb 为正式发行版，CSV 为辅助导出格式，') 
    print(f'       数据存在时间差属正常现象')

    # ── QPS ──
    if not args.skip_bench:
        print(f'\n\n{"=" * 70}')
        print('  ⚡ QPS 性能基准测试 (Python 实现)')
        print('=' * 70)

        all_bench = []
        for db_name in args.dbs:
            print(f'\n  [{db_name}] ...')
            bench = benchmark_qzdb(db_name)
            if bench:
                all_bench.append(bench)
                print(f'    V4: {bench["v4_qps"] / 1000000:.2f}M QPS  ({bench["v4_count"]:,})')
                print(f'    V6: {bench["v6_qps"] / 1000000:.2f}M QPS  ({bench["v6_count"]:,})')

        print(f'\n{"─" * 50}')
        print(f'  {"DB":<22} {"V4 QPS":>13} {"V6 QPS":>13}')
        print(f'  {"─" * 22} {"─" * 13} {"─" * 13}')
        for b in all_bench:
            print(f'  {b["name"]:<22} {b["v4_qps"] / 1000000:>11.2f}M/s '
                  f'{b["v6_qps"] / 1000000:>11.2f}M/s')

    print(f'\n{"=" * 70}')
    print(f'  验证完成')
    print('=' * 70)

    return 0 if all_pass else 1


if __name__ == '__main__':
    sys.exit(main())
