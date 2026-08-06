"""
CSV 源数据 → qzdb 一致性验证

以 CSV 为权威源，验证 qzdb 数据库是否构建正确。
对每行 CIDR，测试 start/start+1/end-1/end + 随机 IP，
全部必须匹配 CSV 字段。

用法:
  python3 verify_csv_source.py --csv qqzeng_ip_max_china.csv --db qqzeng_ip_max_china.qzdb
  python3 verify_csv_source.py --csv qqzeng_ip_max_china.csv --db qqzeng_ip_max_global.qzdb
"""
import csv
import os
import sys
import ipaddress
import argparse

sys.path.insert(0, os.path.dirname(__file__))
from qzdb import QzdbReader


def verify(csv_rel, qzdb_rel, sample=1, max_fails=50):
    base = os.path.join(os.path.dirname(__file__), '..', '..')
    csv_path = os.path.join(base, csv_rel)
    qzdb_path = os.path.join(base, qzdb_rel)

    if not os.path.exists(csv_path):
        print(f'CSV not found: {csv_path}')
        return False
    if not os.path.exists(qzdb_path):
        print(f'QZDB not found: {qzdb_path}')
        return False

    print(f'CSV:  {csv_rel}  ({os.path.getsize(csv_path)/1024/1024:.0f} MB)')
    print(f'QZDB: {qzdb_rel}  ({os.path.getsize(qzdb_path)/1024/1024:.0f} MB)')

    s = QzdbReader()
    s.load(qzdb_path)

    # 读取 CSV header
    with open(csv_path, 'r', encoding='utf-8') as f:
        csv_header = next(csv.reader(f))

    # qzdb 字段名
    g = s.find_uint(16777472)
    if g is None:
        g = s._find_v6(0x2001021860020000, 1)
    qzdb_fields = list(g.to_dict().keys()) if g else []
    print(f'QZDB fields: {len(qzdb_fields)}')

    # 对齐: CSV 第 1 列是 cidr，之后列与 qzdb_fields 一一对应
    csv_fields = csv_header[1:]  # 去掉 cidr
    common_idx = []  # [(csv_idx, qzdb_field), ...]
    for i, cf in enumerate(csv_fields):
        if i < len(qzdb_fields) and cf == qzdb_fields[i]:
            common_idx.append((i, qzdb_fields[i]))
        # 即使名称不同也尝试位置匹配 (max_china.csv 和 max_china.qzdb 理应顺序一致)
        elif i < len(qzdb_fields):
            common_idx.append((i, qzdb_fields[i]))
    print(f'Matching fields: {len(common_idx)}')
    print(f'Fields: {", ".join(f for _, f in common_idx[:10])}{"..." if len(common_idx) > 10 else ""}')

    rng = __import__('random').Random(42)
    total = 0
    fail_rows = 0
    fail_details = []
    fallos_total = 0

    with open(csv_path, 'r', encoding='utf-8') as f:
        reader = csv.reader(f)
        next(reader)  # skip header

        for i, row in enumerate(reader):
            if sample > 1 and i % sample != 0:
                continue
            total += 1

            cidr = row[0]
            try:
                if ':' in cidr:
                    net = ipaddress.IPv6Network(cidr, strict=False)
                else:
                    net = ipaddress.IPv4Network(cidr, strict=False)
            except Exception:
                continue

            cnt = net.num_addresses
            if cnt == 0:
                continue
            start_ip = int(net.network_address)
            end_ip = int(net.broadcast_address) if cnt > 1 else start_ip

            row_fails = []

            def test(ip_val, label):
                nonlocal row_fails, fallos_total
                try:
                    if ':' in cidr:
                        g2 = s._find_v6((ip_val >> 64) & 0xFFFFFFFFFFFFFFFF,
                                        ip_val & 0xFFFFFFFFFFFFFFFF)
                    else:
                        g2 = s.find_uint(ip_val)
                except Exception as e:
                    row_fails.append(f'{label} IP={ip_val}: exception {e}')
                    fallos_total += 1
                    return

                if g2 is None:
                    row_fails.append(f'{label} IP={ip_val}: not found (expected data)')
                    fallos_total += 1
                    return

                d = g2.to_dict()
                for csv_idx, qf in common_idx:
                    csv_val = row[csv_idx + 1].strip() if csv_idx + 1 < len(row) else ''
                    qzdb_val = str(d.get(qf, '')).strip()

                    if csv_val == qzdb_val:
                        continue

                    # 浮点数容差比较
                    if qf in ('longitude', 'latitude'):
                        try:
                            if abs(float(csv_val) - float(qzdb_val)) < 0.00005:
                                continue
                        except (ValueError, TypeError):
                            pass

                    fallos_total += 1
                    row_fails.append(
                        f'{label} IP={ip_val} {qf}: CSV="{csv_val}" vs QZDB="{qzdb_val}"'
                    )

            # 测试 7 个 IP
            test(start_ip, 'start')
            if cnt >= 2:
                test(start_ip + 1, 'start+1')
            if cnt >= 3:
                test(end_ip - 1, 'end-1')
                test(end_ip, 'end')
            for _ in range(min(3, max(0, cnt - 4))):
                test(start_ip + rng.randint(0, cnt - 1), 'rnd')

            if row_fails:
                fail_rows += 1
                if len(fail_details) < max_fails:
                    fail_details.append((i, cidr, row_fails))

            if total % 10000 == 0:
                print(f'  ... {total} rows, {fail_rows} failed, {fallos_total} field mismatches')

    # === 报告 ===
    pct = fail_rows / max(total, 1) * 100
    print(f'\n{"=" * 60}')
    print(f'  检查行数:           {total}')
    print(f'  失败行数:           {fail_rows} ({pct:.1f}%)')
    print(f'  字段不匹配总数:     {fallos_total}')
    print(f'{"=" * 60}')

    if fail_details:
        print(f'\n首批失败 (最多 {max_fails} 行):')
        for row_idx, cidr, flist in fail_details[:10]:
            print(f'\n  row {row_idx} {cidr}:')
            for f in flist[:8]:
                print(f'    {f}')
            if len(flist) > 8:
                print(f'    ... and {len(flist) - 8} more')

    return fail_rows == 0


def main():
    parser = argparse.ArgumentParser(description='CSV → QZDB 源数据一致性验证')
    parser.add_argument('--csv', default='qqzeng_ip_max_china.csv')
    parser.add_argument('--db', default='qqzeng_ip_max_china.qzdb')
    parser.add_argument('--sample', type=int, default=1)
    args = parser.parse_args()

    print('=' * 60)
    print('CSV → QZDB 源数据一致性验证')
    print('以 CSV 为权威源，验证 qzdb 构建正确性')
    print('=' * 60)
    print()

    ok = verify(args.csv, args.db, args.sample)
    print()
    if ok:
        print('✅ 完全一致: qzdb 与 CSV 源数据匹配')
        print('TEST_PASS')
    else:
        print('❌ 存在不一致: qzdb 与 CSV 源数据有差异')
        print('TEST_FAIL')
        sys.exit(1)


if __name__ == '__main__':
    main()
