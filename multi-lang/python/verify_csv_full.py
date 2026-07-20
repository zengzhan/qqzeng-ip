"""
CSV 源数据全量边界验证工具

遍历 CSV 每一行（CIDR 区间），测试：
  1. 区间边界 IP：start, start+1, end-1, end
  2. 区间内随机 IP（3-5 个）
  3. 区间内一致性：所有 IP 应返回相同地理结果 → 检测 off-by-one bug
  4. CSV 数据对比：.qzdb 结果 vs CSV 源 → 正常的数据库更新差异

注意：
  - CSV 与 .qzdb 可能存在数据更新不同步，CSV 不等于 "绝对正确"
  - 区间内一致性才是真正检测算法正确性的指标
"""
import csv
import ipaddress
import os
import sys
import argparse

sys.path.insert(0, os.path.dirname(__file__))
from qzdb import QzdbSearcher, GeoInfo

BASE_DIR = os.path.join(os.path.dirname(__file__), '..', '..')

GEO_FIELDS = ['continent', 'country', 'province', 'city', 'district', 'isp',
              'area_code', 'country_english']


def parse_cidr(cidr):
    """Return (start_ip, end_ip, num_addresses) or raise."""
    if ':' in cidr:
        net = ipaddress.IPv6Network(cidr, strict=False)
    else:
        net = ipaddress.IPv4Network(cidr, strict=False)
    n = net.num_addresses
    if n == 0:
        return None, None, 0
    s = int(net.network_address)
    e = int(net.broadcast_address) if n > 1 else s
    return s, e, n


def geo_val(g, field):
    """Get field from GeoInfo object."""
    if g is None:
        return ''
    d = {'continent': g.continent, 'country': g.country, 'province': g.province,
         'city': g.city, 'district': g.district, 'isp': g.isp,
         'area_code': g.area_code, 'country_english': g.country_english}
    return d.get(field, '')


def run(name, csv_rel, sample=1):
    csv_path = os.path.join(BASE_DIR, csv_rel)
    qzdb_path = os.path.join(BASE_DIR, f'qqzeng_ip_{name}.qzdb')

    if not os.path.exists(csv_path):
        print(f'  [{name}] SKIP: CSV not found')
        return
    if not os.path.exists(qzdb_path):
        print(f'  [{name}] SKIP: qzdb not found')
        return

    print(f'  [{name}] Loading...')
    s = QzdbSearcher()
    s.load(qzdb_path)

    rng = __import__('random').Random(42)

    total = 0
    inconsistent = 0   # 算法 bug
    csv_diff = 0       # 数据差异
    csv_diff_detail = []  # 记录 CSV 差异的 examples

    with open(csv_path, 'r', encoding='utf-8') as f:
        reader = csv.reader(f)
        header = next(reader)
        print(f'  [{name}] {len(header)} fields, sample={sample}')

        for i, row in enumerate(reader):
            if sample > 1 and i % sample != 0:
                continue
            total += 1

            cidr = row[0]
            expected = {f: row[j + 1] for j, f in enumerate(GEO_FIELDS)}

            try:
                start_ip, end_ip, count = parse_cidr(cidr)
            except Exception:
                continue
            if start_ip is None or count == 0:
                continue

            is_v6 = ':' in cidr
            results = []  # [(label, geo_dict), ...]

            def query(ip, label):
                if is_v6:
                    g = s._find_v6((ip >> 64) & 0xFFFFFFFFFFFFFFFF,
                                   ip & 0xFFFFFFFFFFFFFFFF)
                else:
                    g = s.find_uint(ip)
                results.append((label, ip, g))

            # 边界 IP
            if count >= 1:
                query(start_ip, 'start')
            if count >= 2:
                query(start_ip + 1, 'start+1')
            if count >= 3:
                query(end_ip - 1, 'end-1')
                query(end_ip, 'end')

            # 随机 IP
            for _ in range(min(5, max(0, count - 4))):
                offset = rng.randint(0, count - 1)
                query(start_ip + offset, f'rnd+{offset}')

            # --- 区间内一致性检查 ---
            found = [g for _, _, g in results if g is not None]
            if len(found) >= 2:
                first = found[0]
                if any(geo_val(first, f) != geo_val(g, f) for g in found[1:] for f in GEO_FIELDS):
                    inconsistent += 1
                    if inconsistent <= 5:
                        print(f'  ❌ INCONSISTENT row {i} {cidr}: 区间内返回不同!')
                        for label, ip, g in results[:6]:
                            if g:
                                vals = ' | '.join(geo_val(g, f) for f in ['province', 'city', 'district'])
                                print(f'      {label} IP={ip}: {vals}')
                    continue  # 不一致的不参与 CSV 对比

            # --- CSV 对比（仅对区间内一致的）--- 
            if found:
                first = found[0]
                mismatches = [f for f in GEO_FIELDS if expected[f] != geo_val(first, f)]
                if mismatches:
                    csv_diff += 1
                    if csv_diff <= 5:
                        sample_vals = ' | '.join(f'{f}: CSV="{expected[f]}" DB="{geo_val(first, f)}"'
                                                  for f in mismatches[:3])
                        print(f'  ℹ CSV-DIFF row {i} {cidr}: {sample_vals}')

            if total % 10000 == 0:
                print(f'  [{name}] ... {total} rows')

    # === 报告 ===
    print(f'\n  [{name}] ===== 结果 =====')
    print(f'  检查行数:     {total}')
    print(f'  算法不一致:    {inconsistent}')
    print(f'  CSV 数据差异:  {csv_diff}')
    if total > 0:
        print(f'  CSV 差异率:    {csv_diff / total * 100:.1f}%')

    if inconsistent == 0:
        print(f'  ✅ 所有区间内一致 → 算法正确')
    else:
        print(f'  ❌ {inconsistent} 行区间内不一致 → 需检查算法!')

    return {'total': total, 'inconsistent': inconsistent, 'csv_diff': csv_diff}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--db', choices=['max_china', 'max_global', 'all'], default='all')
    parser.add_argument('--sample', type=int, default=1)
    args = parser.parse_args()

    print('=' * 60)
    print('CSV 全量边界验证')
    print('=" 区间内一致性 → 检测 off-by-one / 算法 bug')
    print('=" CSV 差异 → 正常数据更新')
    print('=' * 60)

    dbs = []
    if args.db in ('max_china', 'all'):
        dbs.append(('max_china', 'qqzeng_ip_max_china.csv'))
    if args.db in ('max_global', 'all'):
        dbs.append(('max_global', 'qqzeng_ip_max_global.csv'))

    for db_name, csv_rel in dbs:
        print(f'\n--- {db_name} ---')
        run(db_name, csv_rel, args.sample)

    print('\n' + '=' * 60)
    print('结论判断:')
    print('  - 区间内不一致 > 0 → 算法有 bug（同一 CIDR 不同 IP 返回不同结果）')
    print('  - CSV 差异 > 0 → qzdb 数据与 CSV 源有差异，不影响算法正确性')
    print('=' * 60)


if __name__ == '__main__':
    main()
