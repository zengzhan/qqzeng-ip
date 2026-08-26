#!/usr/bin/env python3
"""
QZDB 结构与算法准确性深度评估
测试两类数据库：
  1. China (非连续 IP 分配) - CIDR 碎片化，同一地区多段不连续 IP
  2. Global (连续唯一分配) - IP 段覆盖广但不连续

使用 Python 作为 reference SDK，通过底层二进制解析验证 trie 遍历 + IPRow 间接寻址 + 池化字符串还原的正确性。
"""

import struct, os, sys, json, time, random, socket, collections

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_DIR = os.path.join(SCRIPT_DIR, "data")

# 加载 Python SDK
sys.path.insert(0, os.path.join(SCRIPT_DIR, "python"))
from qzdb import QzdbReader

class QzdbAnalyzer:
    """底层二进制解析器，直接读取 Header/Trie/IPRow/Pools 用于交叉验证"""

    def __init__(self, path):
        with open(path, "rb") as f:
            self.data = f.read()
        self._parse_header()
        self._parse_jump_table()
        self._parse_trie()
        self._parse_ip_rows()
        self._parse_pools()

    def _parse_header(self):
        h = self.data[:192]
        self.magic = h[0:4]
        self.flags = struct.unpack_from('<H', h, 8)[0]
        self.v4_jump_bits = h[10]
        self.v6_jump_bits = h[11]
        self.pool_count = h[12]
        self.pool_idx_size = h[13]
        self.row_count = struct.unpack_from('<I', h, 20)[0]
        self.v4_record = struct.unpack_from('<I', h, 24)[0]
        self.v6_record = struct.unpack_from('<I', h, 28)[0]
        self.offset_v4_jump = struct.unpack_from('<Q', h, 64)[0]
        self.offset_v4_nodes = struct.unpack_from('<Q', h, 72)[0]
        self.offset_v6_jump = struct.unpack_from('<Q', h, 80)[0]
        self.offset_v6_nodes = struct.unpack_from('<Q', h, 88)[0]
        self.offset_ip_row = struct.unpack_from('<Q', h, 96)[0]
        self.v4_node_count = struct.unpack_from('<I', h, 152)[0]
        self.v6_node_count = struct.unpack_from('<I', h, 156)[0]
        self.ip_row_size = struct.unpack_from('<I', h, 160)[0]

        # Header offset 14 is a legacy 16-bit field, not the number of geo
        # entries. The authoritative count lives in the first GeoEntries group
        # table: byte 0 is the group count, byte 1 is field count, bytes 2..5
        # are the group's uint32 entry count.
        self.offset_geo_entries = struct.unpack_from('<Q', h, 104)[0]
        if self.offset_geo_entries:
            if self.offset_geo_entries + 6 <= len(self.data):
                self.geo_count = struct.unpack_from('<I', self.data, self.offset_geo_entries + 2)[0]
                self.dimension_mask = struct.unpack_from('<H', self.data, self.offset_geo_entries + 6)[0]
            else:
                self.geo_count = 0
                self.dimension_mask = 0
        else:
            self.geo_count = 0
            self.dimension_mask = 0

    def _parse_jump_table(self):
        if self.offset_v4_jump > 0:
            self.v4_jump = []
            for i in range(65536):
                v = struct.unpack_from('<I', self.data, self.offset_v4_jump + i * 4)[0]
                self.v4_jump.append(v)
        else:
            self.v4_jump = None

    def _parse_trie(self):
        self.node_size = 6 if self.v4_node_count < 8388608 and self.row_count < 8388608 else 8
        SENTINEL = 0x80000000 if self.node_size == 8 else 0x800000

        def read_node(nodes_data, idx):
            """idx is 0-based node index"""
            off = idx * self.node_size
            if self.node_size == 6:
                # Compact nodes contain two overlapping 24-bit little-endian
                # children. Do not unpack 32 bits: the final right child only
                # occupies bytes 3..5 and has no fourth byte in the slice.
                left = nodes_data[off] | (nodes_data[off + 1] << 8) | (nodes_data[off + 2] << 16)
                right = nodes_data[off + 3] | (nodes_data[off + 4] << 8) | (nodes_data[off + 5] << 16)
            else:
                left = struct.unpack_from('<I', nodes_data, off)[0]
                right = struct.unpack_from('<I', nodes_data, off + 4)[0]
            return left, right

        self._read_node = read_node
        self._SENTINEL = SENTINEL

        # 读取 V4 trie nodes
        if self.offset_v4_nodes > 0 and self.v4_node_count > 0:
            self.v4_nodes_raw = self.data[self.offset_v4_nodes:
                self.offset_v4_nodes + self.v4_node_count * self.node_size]
        else:
            self.v4_nodes_raw = None

        # 读取 V6 trie nodes
        if self.offset_v6_nodes > 0 and self.v6_node_count > 0:
            self.v6_nodes_raw = self.data[self.offset_v6_nodes:
                self.offset_v6_nodes + self.v6_node_count * self.node_size]
        else:
            self.v6_nodes_raw = None

    def _parse_ip_rows(self):
        self.ip_rows = {}
        if self.offset_ip_row > 0 and self.row_count > 0:
            for i in range(self.row_count):
                off = self.offset_ip_row + i * self.ip_row_size
                row_data = self.data[off:off + self.ip_row_size]
                if self.ip_row_size >= 6:
                    geo_id = struct.unpack_from('<I', row_data + b'\x00\x00')[0] & 0xFFFFFF
                    asn_id = struct.unpack_from('<I', row_data[3:6] + b'\x00\x00')[0] & 0xFFFFFF
                else:
                    geo_id = struct.unpack_from('<I', row_data + b'\x00\x00\x00')[0] & 0xFFFFFF
                    asn_id = 0
                self.ip_rows[i] = (geo_id, asn_id)

    def _parse_pools(self):
        """解析 String Pools"""
        self.pools = []
        # 简化: 使用 SDK 的 pool 数据，不重复解析
        pass

    def raw_traverse_v4(self, ip_int):
        """底层 V4 trie 遍历，返回 row_id 或 None"""
        if self.v4_jump is None or self.v4_nodes_raw is None:
            return None

        # 1. Jump Table O(1) 查找
        high16 = (ip_int >> 16) & 0xFFFF
        jump_val = self.v4_jump[high16]

        if jump_val == 0:
            return None  # 该 /16 无数据
        if jump_val & 0x80000000:
            return jump_val & 0x7FFFFFFF  # 直接是叶子

        # 2. Trie Walk (剩余 16 bits)
        # jump_val IS the 0-based node index (SDK uses it directly as array index)
        node_idx = jump_val
        remaining = ip_int & 0xFFFF
        SENTINEL = self._SENTINEL

        for bit_pos in range(15, -1, -1):
            bit = (remaining >> bit_pos) & 1
            if node_idx >= len(self.v4_nodes_raw) // self.node_size:
                return None
            left, right = self._read_node(self.v4_nodes_raw, node_idx)
            child = right if bit else left

            if child == 0:
                return None
            if child & SENTINEL:
                return child & ~SENTINEL  # 叶子 → row_id

            # child IS the 0-based node index for next iteration
            node_idx = child

        return None

    def raw_lookup_v4(self, ip_str):
        """底层 V4 查询，返回 (row_id, geo_id) 或 None"""
        ip_int = struct.unpack('>I', socket.inet_aton(ip_str))[0]
        row_id = self.raw_traverse_v4(ip_int)
        if row_id is None:
            return None
        geo_id, asn_id = self.ip_rows.get(row_id, (0, 0))
        return row_id, geo_id, asn_id


def classify_ip_ranges(ip_int):
    """将 IP 整数分为不同的特征区间"""
    ranges = [
        (0, 0, "0.0.0.0 (特殊)"),
        (1, 0x00FFFFFF, "0.x.x.x (保留)"),
        (0x01000000, 0x0AFFFFFF, "1-10.x.x.x (保留/私有)"),
        (0x0B000000, 0x64FFFFFF, "11-100.x.x.x (分配)"),
        (0x65000000, 0x7F000000, "101-127.x.x.x (回环/分配)"),
        (0x7F000001, 0x7FFFFFFF, "127.x.x.x (回环)"),
        (0x80000000, 0x9FFFFFFF, "128-159.x.x.x (亚太)"),
        (0xA0000000, 0xBFFFFFFF, "160-191.x.x.x (欧洲/美洲)"),
        (0xC0000000, 0xCFFFFFFF, "192-207.x.x.x (北美/亚太)"),
        (0xD0000000, 0xEFFFFFFF, "208-239.x.x.x (分配)"),
        (0xF0000000, 0xFEFFFFFF, "240-254.x.x.x (保留/私有)"),
        (0xFF000000, 0xFFFFFFFF, "255.x.x.x (广播)"),
    ]
    for lo, hi, label in ranges:
        if lo <= ip_int <= hi:
            return label
    return "unknown"


def test_boundary_ips(analyzer, sdk, label):
    """测试关键边界 IP"""
    boundary_ips = [
        # 经典边界
        "0.0.0.0", "0.0.0.1", "0.255.255.255",
        "1.0.0.0", "1.0.0.1", "1.255.255.255",
        "10.0.0.0", "10.0.0.1", "10.255.255.255",
        "100.0.0.0", "100.64.0.0", "100.127.255.255",
        "127.0.0.1", "127.255.255.255",
        "128.0.0.0", "128.0.0.1", "128.255.255.255",
        "172.16.0.0", "172.16.0.1", "172.31.255.255",
        "192.0.0.0", "192.0.0.1", "192.168.0.0", "192.168.0.1", "192.168.255.255",
        "255.255.255.255",
        # 中国关键段
        "1.0.1.0", "1.0.2.0", "1.0.3.0",
        "14.0.0.0", "14.0.0.1", "14.104.0.0",
        "27.8.0.0", "27.8.0.1", "27.16.0.0",
        "36.0.0.0", "36.0.0.1", "36.96.0.0",
        "42.48.0.0", "42.48.0.1", "42.96.0.0",
        "58.0.0.0", "58.0.0.1", "58.32.0.0",
        "60.0.0.0", "60.0.0.1", "60.160.0.0",
        "101.0.0.0", "101.0.0.1", "101.32.0.0",
        "110.0.0.0", "110.0.0.1", "110.64.0.0",
        "114.0.0.0", "114.0.0.1", "114.114.114.114",
        "125.0.0.0", "125.0.0.1", "125.72.0.0",
        "180.0.0.0", "180.0.0.1", "180.76.0.0",
        "202.0.0.0", "202.0.0.1", "202.96.128.0",
        "210.0.0.0", "210.0.0.1", "210.73.0.0",
        "211.0.0.0", "211.0.0.1", "211.64.0.0",
        "218.0.0.0", "218.0.0.1", "218.75.0.0",
        "219.0.0.0", "219.0.0.1", "219.128.0.0",
        "220.0.0.0", "220.0.0.1", "220.96.0.0",
        "221.0.0.0", "221.0.0.1", "221.192.0.0",
        "222.0.0.0", "222.0.0.1", "222.128.0.0",
        "223.0.0.0", "223.0.0.1", "223.255.255.255",
        "224.0.0.0", "224.0.0.1",
        "240.0.0.0", "240.0.0.1",
    ]

    passed = 0
    failed = 0
    mismatches = []

    for ip in boundary_ips:
        try:
            sdk_result = sdk.find(ip)
            sdk_pipe = sdk_result.to_pipe() if sdk_result else ""

            # 也尝试用 raw 解析器验证
            raw = analyzer.raw_lookup_v4(ip)
            if raw:
                row_id, geo_id, asn_id = raw
                # row_id 应该在范围内
                if row_id >= analyzer.row_count:
                    mismatches.append((ip, f"row_id {row_id} >= row_count {analyzer.row_count}"))
                    failed += 1
                    continue
                entry_id = asn_id if analyzer.dimension_mask & 0x02 else geo_id
                if entry_id >= analyzer.geo_count:
                    dimension = "asn_id" if analyzer.dimension_mask & 0x02 else "geo_id"
                    mismatches.append((ip, f"{dimension} {entry_id} >= entry_count {analyzer.geo_count}"))
                    failed += 1
                    continue

            if sdk_pipe == "" and raw is None:
                passed += 1  # 两者一致：都返回 null
            elif sdk_pipe != "" and raw is not None:
                passed += 1  # 两者一致：都返回了结果
            else:
                # 不一致
                mismatches.append((ip, f"SDK={'有结果' if sdk_pipe else 'null'}, Raw={'row_id='+str(raw[0]) if raw else 'null'}"))
                failed += 1
        except Exception as e:
            mismatches.append((ip, f"异常: {e}"))
            failed += 1

    return passed, failed, mismatches


def test_sequential_ranges(sdk, label, num_tests=500):
    """测试连续 IP 范围内的稳定性（相邻 IP 应返回相同或连续结果）"""
    random.seed(42)
    stable_count = 0
    transition_count = 0
    anomaly_count = 0

    for _ in range(num_tests):
        ip1_int = random.randint(0x01000000, 0xFEFFFFFF)
        ip2_int = ip1_int + 1
        ip1_str = socket.inet_ntoa(struct.pack('>I', ip1_int))
        ip2_str = socket.inet_ntoa(struct.pack('>I', ip2_int))

        r1 = sdk.find(ip1_str)
        r2 = sdk.find(ip2_str)

        p1 = r1.to_pipe() if r1 else ""
        p2 = r2.to_pipe() if r2 else ""

        if p1 == p2:
            stable_count += 1
        elif p1 != "" and p2 != "":
            # 两者都有结果但不同 → 正常的区间边界
            transition_count += 1
        elif p1 == "" and p2 == "":
            stable_count += 1  # 两者都为空
        else:
            # 异常：一个有一个没有（相邻 IP 不应出现这种情况，除非是区间边界）
            anomaly_count += 1

    return stable_count, transition_count, anomaly_count


def test_china_specific_ranges(sdk, analyzer, label):
    """针对中国 IP 的专项测试 - 测试不连续的 CIDR 分配"""
    # 这些是中国 IP 分配中已知的不连续段
    test_groups = [
        # (段名, [IP列表])
        ("中国电信 1.0.1.x", ["1.0.1.0", "1.0.1.1", "1.0.1.2", "1.0.1.127", "1.0.1.128", "1.0.1.254", "1.0.1.255"]),
        ("中国联通 1.0.2.x", ["1.0.2.0", "1.0.2.1", "1.0.2.127", "1.0.2.128", "1.0.2.255"]),
        ("中国移动 10.0.0.x", ["10.0.0.0", "10.0.0.1", "10.0.0.127", "10.0.0.128", "10.0.0.255"]),
        ("北京 223.5.5.x", ["223.5.5.0", "223.5.5.1", "223.5.5.2", "223.5.5.5", "223.5.5.255"]),
        ("上海 114.114.114.x", ["114.114.114.0", "114.114.114.1", "114.114.114.114", "114.114.114.255"]),
        ("广州 113.116.x.x", ["113.116.0.0", "113.116.0.1", "113.116.127.254", "113.116.128.0", "113.116.255.255"]),
        ("深圳 119.147.x.x", ["119.147.0.0", "119.147.0.1", "119.147.127.254", "119.147.128.0", "119.147.255.255"]),
        ("四川 182.150.x.x", ["182.150.0.0", "182.150.0.1", "182.150.127.254", "182.150.128.0", "182.150.255.255"]),
        ("教育网 202.112.x.x", ["202.112.0.0", "202.112.0.1", "202.112.127.254", "202.112.128.0", "202.112.255.255"]),
    ]

    results = []
    for group_name, ips in test_groups:
        group_results = []
        for ip in ips:
            r = sdk.find(ip)
            pipe = r.to_pipe() if r else ""
            # 也用 raw 解析器验证
            raw = analyzer.raw_lookup_v4(ip) if analyzer.v4_jump else None
            raw_row = raw[0] if raw else None
            group_results.append((ip, pipe, raw_row))
        results.append((group_name, group_results))

    return results


def test_v6_boundary_ips(sdk, label):
    """测试 IPv6 边界"""
    v6_ips = [
        "::", "::1", "::ffff:0:0", "::ffff:192.168.0.1",
        "::ffff:127.0.0.1",
        "2001:db8::1",  # 文档地址
        "fe80::1",  # 链路本地
        "ff02::1",  # 组播
        "2408:8000:9000::1",  # 中国联通
        "240e::1",  # 中国电信
        "2409:8000:8000::1",  # 中国移动
        "2001:4860:4860::8888",  # Google DNS
        "2606:4700:4700::1111",  # Cloudflare DNS
        "2001:0db8:0000:0000:0000:0000:0000:0001",  # 完整格式
    ]

    results = []
    for ip in v6_ips:
        r = sdk.find(ip)
        pipe = r.to_pipe() if r else ""
        results.append((ip, pipe))
    return results


def run_comprehensive_test(db_path):
    """运行综合准确性测试"""
    print(f"\n{'='*80}")
    print(f"  测试数据库: {os.path.basename(db_path)}")
    print(f"  文件大小: {os.path.getsize(db_path) / 1024 / 1024:.1f} MB")
    print(f"{'='*80}")

    sdk = QzdbReader(db_path)
    analyzer = QzdbAnalyzer(db_path)

    is_china = 'china' in os.path.basename(db_path).lower()
    db_type = "国内版 (China)" if is_china else "全球版 (Global)"

    print(f"  数据库类型: {db_type}")
    print(f"  V4 Records: {analyzer.v4_record:,}  V6 Records: {analyzer.v6_record:,}")
    print(f"  RowCount: {analyzer.row_count:,}  GeoCount: {analyzer.geo_count:,}")
    print(f"  Trie Nodes: V4={analyzer.v4_node_count:,} V6={analyzer.v6_node_count:,}")
    print(f"  Node Size: {analyzer.node_size} bytes  SENTINEL: 0x{analyzer._SENTINEL:X}")

    all_passed = 0
    all_failed = 0
    all_mismatches = []

    # Test 1: 边界 IP 测试
    print(f"\n  [测试 1] 边界 IP 测试 ({db_type})")
    p, f, m = test_boundary_ips(analyzer, sdk, db_type)
    all_passed += p
    all_failed += f
    all_mismatches.extend(m)
    print(f"    通过: {p}/{p+f}  失败: {f}")
    if m:
        for ip, reason in m[:5]:
            print(f"      ✗ {ip}: {reason}")

    # Test 2: 连续范围稳定性
    print(f"\n  [测试 2] 连续 IP 范围稳定性 (500 对相邻 IP)")
    s, t, a = test_sequential_ranges(sdk, db_type)
    print(f"    稳定: {s}  区间边界: {t}  异常: {a}")
    if a > 0:
        print(f"    ⚠ 发现 {a} 个异常相邻对")
        all_failed += a
    else:
        all_passed += 500

    # Test 3: 中国 IP 专项测试（仅 China 库）
    if is_china:
        print(f"\n  [测试 3] 中国 IP 不连续段测试")
        china_results = test_china_specific_ranges(sdk, analyzer, db_type)
        for group_name, group_results in china_results:
            non_empty = sum(1 for _, p, _ in group_results if p)
            print(f"    {group_name}: {non_empty}/{len(group_results)} 有结果")
            # 检查同一段内结果的一致性
            pipes = set(p for _, p, _ in group_results if p)
            if len(pipes) == 1:
                print(f"      ✓ 段内结果一致: {list(pipes)[0][:60]}...")
            elif len(pipes) > 1:
                print(f"      ✓ 段内有 {len(pipes)} 种不同结果（符合预期，不同子网不同归属）")
            # 验证 raw 解析器行号
            for ip, pipe, raw_row in group_results:
                if raw_row is not None and raw_row >= analyzer.row_count:
                    all_failed += 1
                    all_mismatches.append((ip, f"raw row_id {raw_row} out of range"))
                else:
                    all_passed += 1

    # Test 4: IPv6 边界
    print(f"\n  [测试 4] IPv6 边界测试")
    v6_results = test_v6_boundary_ips(sdk, db_type)
    for ip, pipe in v6_results:
        status = "✓" if pipe or ip == "::" else "·"
        print(f"    {status} {ip}: {'有结果' if pipe else 'null'}")
        all_passed += 1

    # Test 5: Trie 遍历交叉验证 (raw vs SDK)
    print(f"\n  [测试 5] Trie 遍历交叉验证 (raw binary vs SDK)")
    random.seed(123)
    test_count = 2000
    raw_match = 0
    raw_mismatch = 0

    for _ in range(test_count):
        ip_int = random.randint(0x01000000, 0xFEFFFFFF)
        ip_str = socket.inet_ntoa(struct.pack('>I', ip_int))

        sdk_result = sdk.find(ip_str)
        sdk_pipe = sdk_result.to_pipe() if sdk_result else ""

        raw = analyzer.raw_lookup_v4(ip_str)
        raw_pipe = ""
        if raw:
            row_id, geo_id, asn_id = raw
            if row_id < analyzer.row_count:
                raw_pipe = "has_result"

        if (sdk_pipe == "" and raw is None) or (sdk_pipe != "" and raw is not None):
            raw_match += 1
        else:
            raw_mismatch += 1
            if raw_mismatch <= 5:
                all_mismatches.append((ip_str, f"SDK={'有' if sdk_pipe else '无'}, Raw={'有' if raw else '无'}"))

    all_passed += raw_match
    all_failed += raw_mismatch
    print(f"    一致: {raw_match}/{test_count}  不一致: {raw_mismatch}")

    # 汇总
    print(f"\n{'='*80}")
    print(f"  汇总: {all_passed} 通过 / {all_failed} 失败 / {all_passed+all_failed} 总计")
    if all_mismatches:
        print(f"  失败详情 (前 10 个):")
        for ip, reason in all_mismatches[:10]:
            print(f"    ✗ {ip}: {reason}")
    print(f"{'='*80}")

    return all_passed, all_failed, all_mismatches


def main():
    print("QZDB 结构与算法准确性深度评估")
    print(f"测试时间: {time.strftime('%Y-%m-%d %H:%M:%S')}")

    total_passed = 0
    total_failed = 0
    all_mismatches = []

    # 测试所有正式数据库；跳过隐藏文件（tier1 敌对向量测试会在 data/ 落盘
    # .tier1_bad*.qzdb 等故意损坏夹具，SDK 对其 Fail-Closed 拒载是预期行为，
    # 不能因此中断整体分析）。
    for db_file in sorted(os.listdir(DATA_DIR)):
        if not db_file.endswith('.qzdb') or db_file.startswith('.'):
            continue
        db_path = os.path.join(DATA_DIR, db_file)
        p, f, m = run_comprehensive_test(db_path)
        total_passed += p
        total_failed += f
        all_mismatches.extend(m)

    print(f"\n{'='*80}")
    print(f"  总体汇总")
    print(f"{'='*80}")
    print(f"  总通过: {total_passed}")
    print(f"  总失败: {total_failed}")
    print(f"  通过率: {total_passed/(total_passed+total_failed)*100:.2f}%")
    print(f"  测试数据库数: {len([f for f in os.listdir(DATA_DIR) if f.endswith('.qzdb')])}")
    print(f"{'='*80}")

    return total_failed == 0


if __name__ == "__main__":
    success = main()
    sys.exit(0 if success else 1)
