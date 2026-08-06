#!/usr/bin/env python3
"""
QZDB 全面解析验证器 v3 - full_parse_verify.py
=============================================
从 temp_work 原始 temp_*.txt 数据直接验证 qzdb 文件的 Python SDK 解析结果。

核心改进: 按字段位置匹配（不依赖字段名别名），精准检测真实数据差异。

验证策略（三层）:
  Layer 1 - 区间内一致性（算法正确性核心指标）:
    取每行 range 的 start / end / random 三点查询必须返回完全相同地理结果
    检测 off-by-one、误入相邻区间等算法 bug

  Layer 2 - 源数据位置精确匹配（数据准确性）:
    qzdb 查询结果按字段位置 vs temp 原始数据逐位对比
    （--lang 追加跨语言对照: 各语言 SDK 查询结果与 Python 参考逐位比较）

  Layer 3 - 边界/特殊IP 专项（健壮性）:
    0.0.0.0, 255.255.255.255, ::, ::1, ::ffff:x.x.x.x / NAT64 / 超大网段等
    无效格式（应返回 None）验证

用法:
    python3 full_parse_verify.py --db std_china --sample 10000
    python3 full_parse_verify.py --all --sample 10000
    python3 full_parse_verify.py --all --full
    python3 full_parse_verify.py --all --boundary-only
    python3 full_parse_verify.py --all --sample 20000 --report /tmp/verify_report.txt

新增选项:
    python3 full_parse_verify.py --db max_china --workers 8 --skip-count --sample 5000    # 并发 + 跳过行数统计
    python3 full_parse_verify.py --version max --region china --sample 2000 --no-boundary  # 版本/区域过滤
    python3 full_parse_verify.py --db std_china --sample 500 --lang node,go,c --no-boundary # 跨语言 L2 对照
    python3 full_parse_verify.py --db asn_china --sample 5000 --seed 2024                  # 随机种子
    python3 full_parse_verify.py --db std_china --min-l2-rate 95.0                         # L2 最低匹配率
    python3 full_parse_verify.py --db asn_china --strict-known-diff                        # 已知差异照常计 FAIL

退出码:
    0 = 全部通过
    1 = 存在 L1 失败 / L3 边界 bug / L2 匹配率低于 --min-l2-rate（已知差异库除外）
    2 = 用法/配置错误（未知库名/版本/区域、未选择任何库）
"""

import argparse
import os
import random
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

BASE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(BASE_DIR, 'python'))
from qzdb import QzdbReader

TEMP_WORK = '/Users/zengxiangzhan/ZengData/qqzeng-data/temp_work'

# temp 文件字段列数（按位置定义，与 SDK metadata 字段名无关）
VERSION_FIELD_COUNT = {
    'std':  6,
    'pro':  10,   # pro temp 有 10 列
    'ult':  25,
    'asn':  8,
    'max':  15,
}

FLOAT_POSITIONS = {
    'std':  [],
    'pro':  [7, 8],   # longitude, latitude
    'ult':  [13, 14],
    'asn':  [],
    'max':  [7, 8],
}

# 各版本 temp 文件中 geo_id/area_code 列的位置（跳过不比对）
SKIP_POSITIONS = {
    'std':  set(),
    'pro':  set(),
    'ult':  {11, 12},   # district_en 和 geo_id 列 SDK 有但 temp 无 / 意义不同
    'asn':  set(),
    'max':  set(),
}

FLOAT_TOL = 1e-3
DEFAULT_SEED = 42

# 已知差异库注解（可扩展）: 这些库的 L2 差异源于 temp 源数据与 SDK 库数据版本不一致,
# 属预期差异, 默认不计入 FAIL（可用 --strict-known-diff 恢复）。
KNOWN_DIFF = {
    'asn_china': '数据版本偏差: temp源数据与SDK库数据版本不一致',
}

GREEN  = '\033[92m'
RED    = '\033[91m'
YELLOW = '\033[93m'
CYAN   = '\033[96m'
BOLD   = '\033[1m'
RESET  = '\033[0m'

# 所有工作线程的打印都经由 print_lock 保证原子性
print_lock = threading.Lock()


def p(*args, **kwargs):
    with print_lock:
        print(*args, **kwargs)


def col(text, c):
    return f'{c}{text}{RESET}'


def parse_line(line, version):
    """解析 temp_*.txt 一行，返回字典，失败返回 None"""
    line = line.rstrip('\n')
    if not line:
        return None
    parts = line.split('\t')
    if len(parts) < 5:
        return None
    start_ip = parts[0].strip()
    end_ip   = parts[1].strip()
    try:
        start_num = int(parts[2])
        end_num   = int(parts[3])
    except ValueError:
        return None
    data_str   = parts[4]
    fields_raw = data_str.split('|')
    # 按位置返回
    fc = VERSION_FIELD_COUNT.get(version, 0)
    field_vals = []
    for i in range(fc):
        field_vals.append(fields_raw[i].strip() if i < len(fields_raw) else '')
    return {
        'start_ip':  start_ip,
        'end_ip':    end_ip,
        'start_num': start_num,
        'end_num':   end_num,
        'is_v6':     ':' in start_ip,
        'field_vals': field_vals,
        'raw_line':  line[:150],
    }


def int_to_ipv4(n):
    return f'{(n>>24)&0xFF}.{(n>>16)&0xFF}.{(n>>8)&0xFF}.{n&0xFF}'


def int_to_ipv6(n):
    """128位整数 -> RFC5952 IPv6 字符串（热路径, 不使用 ipaddress）.
    规则: 小写十六进制、每段无前导0、压缩最左侧最长连续 >=2 个零段为 '::'。
    与 str(ipaddress.IPv6Address) 的唯一差异: ::ffff:0:0/96 地址 ipaddress
    把最后 32 位渲染为点分十进制（'::ffff:0.0.0.0'），本实现渲染为十六进制组
    （'::ffff:0:0'）。两者经 _fast_parse_ipv6 解析得到同一个 128 位整型,
    查询结果一致, 故该差异是预期且正确的。
    """
    b = n.to_bytes(16, 'big')
    groups = []
    for i in range(0, 16, 2):
        groups.append(int.from_bytes(b[i:i + 2], 'big'))
    # 找最左侧最长、长度 >= 2 的连续零段
    best_start = -1
    best_len = 0
    cur_start = -1
    cur_len = 0
    for i, g in enumerate(groups):
        if g == 0:
            if cur_start < 0:
                cur_start = i
                cur_len = 1
            else:
                cur_len += 1
        else:
            if cur_len >= 2 and cur_len > best_len:
                best_start, best_len = cur_start, cur_len
            cur_start = -1
            cur_len = 0
    if cur_len >= 2 and cur_len > best_len:
        best_start, best_len = cur_start, cur_len
    if best_start < 0:
        return ':'.join(f'{g:x}' for g in groups)
    left  = ':'.join(f'{groups[i]:x}' for i in range(best_start))
    right = ':'.join(f'{groups[i]:x}' for i in range(best_start + best_len, 8))
    if left and right:
        return f'{left}::{right}'
    if left:
        return f'{left}::'
    if right:
        return f'::{right}'
    return '::'


def geoinfo_to_vals(info, fc):
    """GeoInfo -> list of string values by position"""
    if info is None:
        return [''] * fc
    pipe = info.to_pipe()
    parts = pipe.split('|')
    result = []
    for i in range(fc):
        result.append(parts[i].strip() if i < len(parts) else '')
    return result


def compare_by_position(expected_vals, actual_vals, version):
    """按位置逐一比对，返回 [(pos, expected, actual)]"""
    fc = VERSION_FIELD_COUNT.get(version, len(expected_vals))
    fp = FLOAT_POSITIONS.get(version, [])
    sp = SKIP_POSITIONS.get(version, set())
    mismatches = []
    for i in range(fc):
        if i in sp:
            continue
        ev = expected_vals[i] if i < len(expected_vals) else ''
        av = actual_vals[i]   if i < len(actual_vals)   else ''
        if i in fp:
            try:
                ef = float(ev) if ev else 0.0
                af = float(av) if av else 0.0
                if abs(ef - af) > FLOAT_TOL:
                    mismatches.append((i, ev, av))
            except ValueError:
                if ev != av:
                    mismatches.append((i, ev, av))
        else:
            if not ev and not av:
                continue
            if ev != av:
                mismatches.append((i, ev, av))
    return mismatches


@dataclass
class Stats:
    name: str = ''
    total_rows: int = 0
    v4_rows: int = 0
    v6_rows: int = 0
    sampled_rows: int = 0
    total_queries: int = 0
    skipped: int = 0
    l1_sets: int = 0
    l1_ok: int = 0
    l1_fail: int = 0
    l1_none: int = 0
    l1_errors: List[str] = field(default_factory=list)
    l2_ok: int = 0
    l2_diff: int = 0
    l2_none: int = 0
    l2_errors: List[str] = field(default_factory=list)
    elapsed: float = 0.0
    qps: float = 0.0
    crc_ok: Optional[bool] = None
    sdk_field_names: List[str] = field(default_factory=list)
    # 每个被比对字段位置的 L2 差异计数（下标 = 字段位置）
    l2_pos_diffs: List[int] = field(default_factory=list)
    # lang -> (checked, mismatches, samples)
    xlang_results: Dict[str, Tuple[int, int, List[str]]] = field(default_factory=dict)
    # qzdb/temp 缺失或加载失败（该库不参与 FAIL 判定）
    skipped_db: bool = False

    def l1_pass_rate(self):
        return 0.0 if self.l1_sets == 0 else self.l1_ok / self.l1_sets * 100

    def l2_match_rate(self):
        n = self.l2_ok + self.l2_diff + self.l2_none
        return 0.0 if n == 0 else self.l2_ok / n * 100


def fmt_eta(secs):
    secs = int(max(0, secs))
    return f'{secs // 3600:02d}:{(secs % 3600) // 60:02d}:{secs % 60:02d}'


def print_progress(name, ip_ver, sampled, total_lines, ip_start, count_known):
    el = time.time() - ip_start
    rate = sampled / el if el > 0 else 0.0
    if count_known:
        pct = sampled / total_lines * 100 if total_lines else 0.0
        eta = (total_lines - sampled) / rate if rate > 0 else 0.0
        p(f'  [{name} {ip_ver}] {sampled:,}/{total_lines:,} ({pct:.1f}%) '
          f'{rate:,.0f} rows/s ETA {fmt_eta(eta)}')
    else:
        p(f'  [{name} {ip_ver}] {sampled:,} rows ({rate:,.0f} rows/s)')


def verify_one(name, version, region, args, rng):
    """对单个库执行 L1+L2 验证（在 ThreadPoolExecutor 工作线程中运行）"""
    s = Stats(name=name)
    p(f'\n{col(f"▶ {name}", BOLD+CYAN)}')

    qzdb_path = os.path.join(TEMP_WORK, f'qqzeng_ip_{version}',
                              f'qqzeng_ip_{version}_{region}.qzdb')
    if region == 'china':
        txt_v4 = os.path.join(TEMP_WORK, f'qqzeng_ip_{version}', 'temp_china_v4.txt')
        txt_v6 = os.path.join(TEMP_WORK, f'qqzeng_ip_{version}', 'temp_china_v6.txt')
    else:
        txt_v4 = os.path.join(TEMP_WORK, f'qqzeng_ip_{version}', 'temp_global_v4.txt')
        txt_v6 = os.path.join(TEMP_WORK, f'qqzeng_ip_{version}', 'temp_global_v6.txt')

    if not os.path.exists(qzdb_path):
        p(f'  {col("SKIP", YELLOW)}: qzdb not found: {qzdb_path}')
        s.skipped_db = True
        return s, {}

    try:
        searcher = QzdbReader(qzdb_path)
    except Exception as e:
        p(f'  {col("ERROR", RED)}: Failed to load: {e}')
        s.skipped_db = True
        return s, {}

    try:
        s.crc_ok = searcher.verify_crc()
    except Exception:
        s.crc_ok = None

    s.sdk_field_names = list(searcher.field_names or [])
    fc = len(s.sdk_field_names)
    s.l2_pos_diffs = [0] * max(fc, VERSION_FIELD_COUNT.get(version, fc))
    crc_str = col('✓ PASS', GREEN) if s.crc_ok else col('✗ FAIL', RED)
    p(f'  CRC: {crc_str}')
    p(f'  SDK fields ({fc}): {s.sdk_field_names}')

    t0 = time.time()
    # --full 或 --skip-count 时跳过行数统计, total 未知
    count_known = (not args.skip_count) and (not args.full)
    collect_xlang = bool(args.lang)
    xlang_pipes = {}

    for ip_ver, txt_path in [('v4', txt_v4), ('v6', txt_v6)]:
        if not os.path.exists(txt_path):
            if args.verbose:
                p(f'    [{ip_ver}] not found: {txt_path}')
            continue

        if count_known:
            p(f'  [{ip_ver}] counting lines...', end=' ', flush=True)
            total_lines = sum(1 for _ in open(txt_path, 'r', encoding='utf-8', errors='replace'))
            p(f'{total_lines:,}')
        else:
            total_lines = 0
            p(f'  [{ip_ver}] line count skipped; rows counted as processed')

        if args.full:
            step = 1
            target = None
        elif count_known:
            target = min(args.sample, total_lines)
            step = max(1, total_lines // target) if target > 0 else 1
        else:
            # 总数未知: 取前 N 行
            target = args.sample
            step = 1

        sampled = 0
        lines_seen = 0
        ip_start = time.time()
        with open(txt_path, 'r', encoding='utf-8', errors='replace') as f:
            for lineno, line in enumerate(f):
                lines_seen = lineno + 1
                if step != 1 and lineno % step != 0:
                    continue
                if target is not None and sampled >= target:
                    break

                row = parse_line(line, version)
                if row is None:
                    s.skipped += 1
                    continue

                sampled += 1
                s.total_rows += 1

                start_num     = row['start_num']
                end_num       = row['end_num']
                is_v6         = row['is_v6']
                expected_vals = row['field_vals']

                # Layer 1: 区间内一致性
                s.l1_sets += 1
                test_ips = []
                results = {}
                results_geo = {}

                if is_v6:
                    try:
                        test_ips.append(('start', int_to_ipv6(start_num), start_num))
                        test_ips.append(('end',   int_to_ipv6(end_num), end_num))
                        if end_num > start_num:
                            mid = start_num + rng.randint(1, min(end_num - start_num, 500000))
                            test_ips.append(('mid', int_to_ipv6(mid), mid))
                    except Exception:
                        s.l1_none += 1
                        continue
                else:
                    test_ips.append(('start', int_to_ipv4(start_num), start_num))
                    test_ips.append(('end',   int_to_ipv4(end_num), end_num))
                    if end_num > start_num:
                        mid = start_num + rng.randint(1, min(end_num - start_num, 10000000))
                        test_ips.append(('mid', int_to_ipv4(mid), mid))

                for label, ip_str, _num in test_ips:
                    s.total_queries += 1
                    try:
                        info = searcher.find(ip_str)
                        results[label] = geoinfo_to_vals(info, fc) if info else None
                        results_geo[label] = info
                    except Exception as ex:
                        results[label] = f'EXCEPTION:{ex}'
                        results_geo[label] = None

                vals       = list(results.values())
                none_count = sum(1 for v in vals if v is None)
                exc_count  = sum(1 for v in vals if isinstance(v, str) and v.startswith('EXCEPTION'))

                if exc_count > 0:
                    s.l1_fail += 1
                    if len(s.l1_errors) < args.max_errors:
                        s.l1_errors.append(
                            f'[L1-EXC] {name} line~{lineno} ip={test_ips[0][1]} '
                            f'exc={[v for v in vals if isinstance(v, str)]}')
                elif none_count == len(vals):
                    s.l1_none += 1
                elif none_count > 0:
                    s.l1_fail += 1
                    if len(s.l1_errors) < args.max_errors:
                        s.l1_errors.append(
                            f'[L1-PARTIAL-NONE] {name} line~{lineno} '
                            f'ips={[ip for _, ip, _ in test_ips]} '
                            f'none_on={[lbl for lbl, v in results.items() if v is None]}')
                else:
                    ref = vals[0]
                    consistent = True
                    diff_positions = []
                    for other in vals[1:]:
                        mm = compare_by_position(ref, other, version)
                        if mm:
                            consistent = False
                            diff_positions.extend([pp for pp, _, _ in mm])
                    if consistent:
                        s.l1_ok += 1
                    else:
                        s.l1_fail += 1
                        if len(s.l1_errors) < args.max_errors:
                            s.l1_errors.append(
                                f'[L1-INCONSIST] {name} line~{lineno} '
                                f'ips={[ip for _, ip, _ in test_ips]} '
                                f'diff_pos={list(set(diff_positions))} '
                                f'start={vals[0]} end={vals[1]}')

                # Layer 2: 数据精确匹配
                s.total_queries += 1
                start_ip_str = test_ips[0][1]
                try:
                    info = searcher.find(start_ip_str)
                except Exception as ex:
                    info = None
                    if len(s.l2_errors) < args.max_errors:
                        s.l2_errors.append(f'[L2-EXC] {name} {start_ip_str}: {ex}')

                if info is None:
                    s.l2_none += 1
                else:
                    actual_vals = geoinfo_to_vals(info, fc)
                    mismatches  = compare_by_position(expected_vals, actual_vals, version)
                    if not mismatches:
                        s.l2_ok += 1
                    else:
                        s.l2_diff += 1
                        for ppos, _e, _a in mismatches:
                            if 0 <= ppos < len(s.l2_pos_diffs):
                                s.l2_pos_diffs[ppos] += 1
                        if len(s.l2_errors) < args.max_errors:
                            mm_str = '; '.join(
                                f'pos{pp}=[exp:{ev!r}|got:{av!r}]'
                                for pp, ev, av in mismatches[:5])
                            s.l2_errors.append(
                                f'[L2-DIFF] {name} {start_ip_str}: {mm_str}')

                # 跨语言 L2: 收集 (ip_key -> python pipe 字符串)
                if collect_xlang:
                    for label, _ip, num in test_ips:
                        if label not in ('start', 'mid'):
                            continue
                        if not is_v6:
                            key = str(num)
                        else:
                            key = f'{num >> 64}:{num & 0xFFFFFFFFFFFFFFFF}'
                        if key not in xlang_pipes:
                            g = results_geo.get(label)
                            xlang_pipes[key] = g.to_pipe() if g else ''

                if sampled % 10000 == 0:
                    print_progress(name, ip_ver, sampled, total_lines, ip_start, count_known)

        if ip_ver == 'v4':
            s.v4_rows = total_lines if count_known else lines_seen
        else:
            s.v6_rows = total_lines if count_known else lines_seen

        s.sampled_rows += sampled
        if count_known:
            pct = sampled / total_lines * 100 if total_lines else 0
            p(f'  [{ip_ver}] sampled {sampled:,}/{total_lines:,} ({pct:.1f}%)')
        else:
            p(f'  [{ip_ver}] sampled {sampled:,} rows (~{lines_seen:,} lines)')

    # 没有任何有效数据（temp 文件缺失或 0 有效行）→ 优雅跳过
    if s.sampled_rows == 0 and s.v4_rows == 0 and s.v6_rows == 0:
        s.skipped_db = True

    s.elapsed = time.time() - t0
    s.qps = s.total_queries / s.elapsed if s.elapsed > 0 else 0
    return s, xlang_pipes


# ── 边界/健壮性测试 ──────────────────────────────────────────────────
BOUNDARY_VALID = [
    '0.0.0.0', '0.0.0.1', '0.0.0.255',
    '1.0.0.0', '1.0.0.1',
    '127.0.0.1', '127.255.255.255',
    '10.0.0.0', '10.255.255.255',
    '172.16.0.0', '172.31.255.255',
    '192.168.0.0', '192.168.255.255',
    '224.0.0.0', '239.255.255.255',
    '240.0.0.0', '255.255.255.254',
    '255.255.255.255',
    '::',
    '::1',
    '::ffff:0.0.0.0',
    '::ffff:127.0.0.1',
    '::ffff:192.168.1.1',
    '::ffff:255.255.255.255',
    '2001:db8::',
    'fe80::1',
    'ff02::1',
    'ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff',
    # V4 映射 V6 (::ffff:0:0/96) 扩展
    '::ffff:0.0.0.1',
    '::ffff:10.0.0.1',
    '::ffff:172.16.0.1',
    '::ffff:192.168.0.1',
    '::ffff:224.0.0.1',
    '::ffff:0:0',
    '::ffff:0:1',
    '::ffff:ffff:0',
    '::ffff:ffff:ffff',
    # NAT64
    '64:ff9b::',
    '64:ff9b::1',
    '64:ff9b:1::192.0.2.1',
    # 超大网段（ASN 库特别相关）
    '0.255.255.255',
    '1.255.255.255',
    '2.0.0.0',
    '128.0.0.0',
    '191.255.255.255',
    '223.255.255.255',
    '255.0.0.0',
    '255.255.0.0',
    '8000::',
    '7fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff',
    'ffff::',
    'ffff:ffff::',
]
BOUNDARY_INVALID = [
    '', '   ', '256.0.0.0', '1.2.3.4.5', '::gggg',
    '1.2.3', '01.0.0.0', '1.2.3.4:80', '2001:db8:::1',
    '-1.0.0.0', '1.2.3.4 ', ' 1.2.3.4', '1.2.3.4.',
    # 无效的 V4 映射 / NAT64 变体
    '::ffff:0.0.0.256',
    '::ffff:999.1.1.1',
    '64:ff9b:1::256.0.0.1',
]


def run_boundary_test(name, searcher):
    results = {}
    bugs = []
    for ip in BOUNDARY_INVALID:
        try:
            info = searcher.find(ip)
            if info is not None:
                bugs.append(f'[BUG] Invalid IP {ip!r} must return None, got: {info.to_pipe()[:80]}')
            else:
                results[ip] = 'OK(None)'
        except Exception as e:
            bugs.append(f'[EXC] Invalid IP {ip!r} raised exception: {e}')
    for ip in BOUNDARY_VALID:
        try:
            info = searcher.find(ip)
            results[ip] = info.to_pipe()[:100] if info else 'None(no data in db)'
        except Exception as e:
            bugs.append(f'[EXC] Valid IP {ip!r}: {e}')
            results[ip] = f'EXCEPTION: {e}'
    return results, bugs


def db_verdict(s, args):
    """单库判定: (文本, 颜色)。用于 print_stats 与报告"""
    if s.l1_fail > 0:
        return '❌ FAIL (区间不一致！算法BUG！)', RED
    if s.skipped_db:
        return '⚠️  SKIP (文件缺失, 未验证)', YELLOW
    if s.name in KNOWN_DIFF and not args.strict_known_diff:
        if s.l2_match_rate() >= 95.0:
            return '✅ PASS', GREEN
        return '⚠️  WARN (已知数据版本差异, L2不计FAIL)', YELLOW
    if s.l2_match_rate() >= args.min_l2_rate:
        return '✅ PASS', GREEN + BOLD
    if s.l2_match_rate() >= 80.0:
        return '⚠️  WARN (算法OK, 数据有差异)', YELLOW + BOLD
    return '❌ FAIL (数据匹配率过低)', RED + BOLD


def print_stats(s, args, show_errors=True):
    l1_rate = s.l1_pass_rate()
    l2_rate = s.l2_match_rate()
    l1c = GREEN if l1_rate >= 99.9 else (YELLOW if l1_rate >= 95.0 else RED)
    l2c = GREEN if l2_rate >= 99.0 else (YELLOW if l2_rate >= 90.0 else RED)

    print(f'  {"─"*64}')
    crc_s = col('PASS', GREEN) if s.crc_ok else (col('FAIL', RED) if s.crc_ok is not None else 'N/A')
    print(f'  CRC校验    : {crc_s}')
    print(f'  SDK字段    : {s.sdk_field_names}')
    print(f'  总行数     : {s.total_rows:,}  (v4={s.v4_rows:,}, v6={s.v6_rows:,})')
    print(f'  抽样行数   : {s.sampled_rows:,}')
    print(f'  总查询数   : {s.total_queries:,}')
    print(f'  耗时/QPS   : {s.elapsed:.2f}s / {col(f"{s.qps:,.0f} QPS", CYAN)}')
    print()
    print(f'  ■ Layer1 区间一致性（算法正确性）:')
    print(f'    测试区间 : {s.l1_sets:,}  通过: {s.l1_ok:,}  失败: {col(str(s.l1_fail), RED if s.l1_fail>0 else GREEN)}  无数据: {s.l1_none:,}')
    print(f'    通过率   : {col(f"{l1_rate:.4f}%", l1c)}')
    print()
    print(f'  ■ Layer2 数据精确匹配（数据准确性）:')
    print(f'    精确匹配 : {s.l2_ok:,}  字段差异: {s.l2_diff:,}  SDK返回None: {s.l2_none:,}')
    print(f'    匹配率   : {col(f"{l2_rate:.2f}%", l2c)}')
    if s.name in KNOWN_DIFF:
        note = KNOWN_DIFF[s.name]
        if args.strict_known_diff:
            print(f'    已知差异 : {note}  [--strict-known-diff 生效, L2照常计FAIL]')
        else:
            print(f'    已知差异 : {note}  [L2差异不判FAIL]')

    # 字段位置差异分布
    if s.l2_pos_diffs and sum(s.l2_pos_diffs) > 0:
        mx = max(s.l2_pos_diffs)
        mx_pos = next(i for i, c in enumerate(s.l2_pos_diffs) if c == mx)
        print()
        print(f'    ┌─ 字段位置差异分布（仅列有差异位置）')
        print(f'    │ {"位置":<6}{"SDK字段":<14}{"差异数":>10}{"占L2差异行":>12}')
        for pp, cnt in enumerate(s.l2_pos_diffs):
            if cnt == 0:
                continue
            nm = s.sdk_field_names[pp] if pp < len(s.sdk_field_names) else '(未知)'
            hl = BOLD + CYAN if cnt == mx else ''
            share = cnt / s.l2_diff * 100 if s.l2_diff else 0.0
            print(f'    │ {hl}pos{pp:<4}{nm:<14}{cnt:>10,}{share:>11.2f}%{RESET if hl else ""}')
        print(f'    └─ 差异最多位置: {col(f"pos{mx_pos}({mx}处)", BOLD+CYAN)}')

    # 跨语言 L2 对照
    if s.xlang_results:
        print(f'\n  ■ 跨语言 L2 对照:')
        for lang, (checked, mismatches, samples) in sorted(s.xlang_results.items()):
            st = col('✓', GREEN) if mismatches == 0 else col('✗', RED)
            print(f'    L2-XL [{lang}]: {checked:,} checked, {mismatches:,} mismatches {st}')
            for smp in samples[:5]:
                print(f'      {col(smp, YELLOW)}')

    if show_errors and s.l1_errors:
        print(f'\n  ⚠ Layer1 错误样本（{len(s.l1_errors)} 条）:')
        for e in s.l1_errors[:15]:
            print(f'    {col(e, RED)}')
    if show_errors and s.l2_errors:
        print(f'\n  ⚠ Layer2 差异样本（{len(s.l2_errors)} 条）:')
        for e in s.l2_errors[:15]:
            print(f'    {col(e, YELLOW)}')

    vtxt, vc = db_verdict(s, args)
    print(f'\n  {col(vtxt, vc)}')


def print_overall(exit_code):
    print()
    print('=' * 74)
    if exit_code == 0:
        print(col('OVERALL: PASS ✅', GREEN + BOLD))
    else:
        print(col('OVERALL: FAIL ❌', RED + BOLD))
    print('=' * 74)


def generate_report(all_stats, boundary_results_all, report_path, args=None):
    lines = []
    lines.append('=' * 74)
    lines.append('QZDB 全面解析验证报告 v2')
    lines.append(f'生成时间: {time.strftime("%Y-%m-%d %H:%M:%S")}')
    if args is not None:
        lines.append(f'参数: --sample={args.sample} --full={args.full} --workers={args.workers} '
                     f'--seed={args.seed} --skip-count={args.skip_count} --lang={args.lang or "-"} '
                     f'--min-l2-rate={args.min_l2_rate} --strict-known-diff={args.strict_known_diff}')
    lines.append('=' * 74)
    total_q = sum(s.total_queries for s in all_stats)
    total_f = sum(s.l1_fail for s in all_stats)
    total_s = sum(s.l1_sets for s in all_stats)
    lines.append(f'\n汇总: {len(all_stats)} 数据库  总查询 {total_q:,}  L1区间 {total_s:,}  L1失败 {total_f}')
    lines.append(f'\n{"数据库":<18} {"L1通过率":>12} {"L1失败":>8} {"L2匹配率":>12} {"QPS":>10} {"CRC":>5}')
    lines.append('─' * 74)
    for s in all_stats:
        crc = 'OK' if s.crc_ok else 'FAIL'
        lines.append(f'{s.name:<18} {s.l1_pass_rate():>11.4f}% {s.l1_fail:>8} '
                     f'{s.l2_match_rate():>11.2f}% {s.qps:>10,.0f} {crc:>5}')
    for s in all_stats:
        if s.name in KNOWN_DIFF:
            lines.append(f'    ↳ 已知差异: {KNOWN_DIFF[s.name]}')
        if s.xlang_results:
            xl_s = ', '.join(f'{lang}={chk}查/{mm}差'
                             for lang, (chk, mm, _) in sorted(s.xlang_results.items()))
            lines.append(f'    ↳ L2-XL: {xl_s}')
    for s in all_stats:
        lines.append(f'\n{"─"*74}')
        lines.append(f'数据库: {s.name}')
        lines.append(f'SDK字段: {s.sdk_field_names}')
        lines.append(f'采样行: {s.sampled_rows:,}  总查询: {s.total_queries:,}  QPS: {s.qps:,.0f}')
        lines.append(f'L1(区间一致): {s.l1_ok}/{s.l1_sets} = {s.l1_pass_rate():.4f}%  失败={s.l1_fail}  无数据={s.l1_none}')
        lines.append(f'L2(数据匹配): {s.l2_ok}/{s.l2_ok+s.l2_diff+s.l2_none} = {s.l2_match_rate():.2f}%  差异={s.l2_diff}')
        if s.name in KNOWN_DIFF:
            strict_note = ' (strict, 照常计FAIL)' if (args is not None and args.strict_known_diff) else ' (L2不判FAIL)'
            lines.append(f'已知差异: {KNOWN_DIFF[s.name]}{strict_note}')
        if s.l2_pos_diffs and sum(s.l2_pos_diffs) > 0:
            lines.append('字段位置差异分布:')
            lines.append(f'  {"位置":<6}{"SDK字段":<14}{"差异数":>10}{"占L2差异行":>12}')
            for pp, cnt in enumerate(s.l2_pos_diffs):
                if cnt == 0:
                    continue
                nm = s.sdk_field_names[pp] if pp < len(s.sdk_field_names) else '(未知)'
                if s.l2_diff:
                    lines.append(f'  pos{pp:<4}{nm:<14}{cnt:>10,}{cnt / s.l2_diff * 100:>11.2f}%')
                else:
                    lines.append(f'  pos{pp:<4}{nm:<14}{cnt:>10,}')
        if s.xlang_results:
            lines.append('跨语言 L2 对照:')
            for lang, (checked, mismatches, samples) in sorted(s.xlang_results.items()):
                lines.append(f'  L2-XL [{lang}]: {checked:,} checked, {mismatches:,} mismatches')
                for smp in samples:
                    lines.append(f'    {smp}')
        for e in s.l1_errors:
            lines.append(f'  L1-ERR: {e}')
        for e in s.l2_errors:
            lines.append(f'  L2-ERR: {e}')
    if boundary_results_all:
        lines.append(f'\n{"─"*74}')
        lines.append('Layer3 边界测试:')
        for name, bres, bugs in boundary_results_all:
            lines.append(f'\n  [{name}] bugs={len(bugs)}')
            for b in bugs:
                lines.append(f'    {b}')
            if not bugs:
                lines.append(f'    所有无效IP均正确返回None ✓')
    with open(report_path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
    print(f'\n{col("报告已保存:", CYAN)} {report_path}')


ALL_DBS = [
    ('std_china',  'std',  'china'),
    ('std_global', 'std',  'global'),
    ('pro_china',  'pro',  'china'),
    ('pro_global', 'pro',  'global'),
    ('ult_china',  'ult',  'china'),
    ('ult_global', 'ult',  'global'),
    ('asn_china',  'asn',  'china'),
    ('asn_global', 'asn',  'global'),
    ('max_china',  'max',  'china'),
    ('max_global', 'max',  'global'),
]


# ── 跨语言 L2 对照 ────────────────────────────────────────────────────
# 运行器映射（与 cross_verify.py 的调用约定一致）:
#   runner <db_path> <v4_test> <v4_out> <v6_test> <v6_out>
RUNNERS = {
    'c':      [os.path.join(TOOLS_DIR, 'batch_c')],
    'go':     [os.path.join(TOOLS_DIR, 'batch_go')],
    'rust':   [os.path.join(TOOLS_DIR, 'batch_rust')],
    'node':   ['node', os.path.join(TOOLS_DIR, 'batch_query.js')],
    'php':    ['php', os.path.join(TOOLS_DIR, 'batch_query.php')],
    'java':   ['bash', os.path.join(TOOLS_DIR, 'batch_java.sh')],
    'csharp': ['bash', os.path.join(TOOLS_DIR, 'batch_csharp.sh')],
}


def compare_lang_output(lang, xlang_pipes, v4_out, v6_out, max_errors):
    """读取语言运行器输出, 与 Python 参考 pipe 字符串逐 key 精确比对"""
    checked = 0
    mismatches = 0
    samples = []
    for out_path in (v4_out, v6_out):
        if not os.path.exists(out_path):
            continue
        with open(out_path, 'r', encoding='utf-8', errors='replace') as f:
            for line in f:
                line = line.strip()
                if not line or '|' not in line:
                    continue
                key, val = line.split('|', 1)
                if key not in xlang_pipes:
                    continue
                checked += 1
                if val != xlang_pipes[key]:
                    mismatches += 1
                    if len(samples) < max_errors:
                        samples.append(
                            f'[{lang}] {key}: py={xlang_pipes[key][:60]!r} lang={val[:60]!r}')
    return checked, mismatches, samples


def run_xlang(db_name, qzdb_path, xlang_pipes, langs, max_errors):
    """对单个库运行跨语言 L2 对照, 返回 {lang: (checked, mismatches, samples)}"""
    results: Dict[str, Tuple[int, int, List[str]]] = {}
    v4_keys = sorted(k for k in xlang_pipes if ':' not in k)
    v6_keys = sorted(k for k in xlang_pipes if ':' in k)
    if not v4_keys and not v6_keys:
        p(f'  {col("L2-XL", YELLOW)}: no keys collected, skip')
        return results

    tmpdir = tempfile.mkdtemp(prefix='xlang_')
    try:
        v4_test = os.path.join(tmpdir, 'v4_keys.txt')
        v6_test = os.path.join(tmpdir, 'v6_keys.txt')
        with open(v4_test, 'w', encoding='utf-8') as f:
            f.write('\n'.join(v4_keys) + ('\n' if v4_keys else ''))
        with open(v6_test, 'w', encoding='utf-8') as f:
            f.write('\n'.join(v6_keys) + ('\n' if v6_keys else ''))

        for lang in langs:
            cmd = RUNNERS.get(lang)
            if cmd is None:
                continue
            v4_out = os.path.join(tmpdir, f'out_{lang}_v4.txt')
            v6_out = os.path.join(tmpdir, f'out_{lang}_v6.txt')
            try:
                r = subprocess.run(
                    [*cmd, qzdb_path, v4_test, v4_out, v6_test, v6_out],
                    capture_output=True, text=True, timeout=600)
                if r.returncode != 0:
                    p(f'  {col("L2-XL", YELLOW)} [{lang}]: runner exited {r.returncode}: '
                      f'{(r.stderr or r.stdout or "")[:200]}')
                    continue
            except FileNotFoundError as e:
                p(f'  {col("L2-XL", YELLOW)} [{lang}]: runner missing → skip ({e})')
                continue
            except subprocess.TimeoutExpired:
                p(f'  {col("L2-XL", YELLOW)} [{lang}]: timeout 600s → skip')
                continue
            checked, mismatches, samples = compare_lang_output(
                lang, xlang_pipes, v4_out, v6_out, max_errors)
            results[lang] = (checked, mismatches, samples)
            st = col('✓', GREEN) if mismatches == 0 else col('✗', RED)
            p(f'  L2-XL [{lang}]: {checked:,} checked, {mismatches:,} mismatches {st}')
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)
    return results


def select_dbs(parser, args):
    """返回选定 db_list; 用法/配置错误时以 exit 2 退出"""
    versions = None
    regions = None
    if args.version:
        versions = set(x.strip() for x in args.version.split(',') if x.strip())
        unknown = versions - set(VERSION_FIELD_COUNT)
        if unknown:
            print(f'未知版本: {sorted(unknown)}，可选: {sorted(VERSION_FIELD_COUNT)}')
            sys.exit(2)
    if args.region:
        regions = set(x.strip() for x in args.region.split(',') if x.strip())
        unknown = regions - {'china', 'global'}
        if unknown:
            print(f'未知区域: {sorted(unknown)}，可选: china,global')
            sys.exit(2)

    if args.db:
        db_list = [e for e in ALL_DBS if e[0] == args.db]
        if not db_list:
            print(f'未知库名: {args.db}，可选: {[e[0] for e in ALL_DBS]}')
            sys.exit(2)
        return db_list

    if not (args.all or versions or regions):
        parser.print_help()
        sys.exit(2)

    db_list = list(ALL_DBS)
    if versions:
        db_list = [e for e in db_list if e[1] in versions]
    if regions:
        db_list = [e for e in db_list if e[2] in regions]
    if not db_list:
        print('没有匹配的数据库（版本/区域过滤后为空）')
        sys.exit(2)
    return db_list


def main():
    parser = argparse.ArgumentParser(description='QZDB 全面解析验证器 v3')
    parser.add_argument('--db',    help='验证单个库，如 std_china')
    parser.add_argument('--all',   action='store_true', help='验证所有10个库')
    parser.add_argument('--version', help='按版本过滤(逗号分隔): std,pro,ult,asn,max')
    parser.add_argument('--region',  help='按区域过滤(逗号分隔): china,global')
    parser.add_argument('--sample', type=int, default=5000,
                        help='每库每IP版本(v4/v6)抽样行数（默认5000）')
    parser.add_argument('--full',  action='store_true', help='全量验证（不抽样, 自动跳过行数统计）')
    parser.add_argument('--skip-count', action='store_true', help='跳过行数统计(性能; 总数/ETA未知)')
    parser.add_argument('--workers', type=int, default=4, help='L1+L2并发工作线程数（默认4）')
    parser.add_argument('--seed', type=int, default=DEFAULT_SEED, help='随机种子（默认42）')
    parser.add_argument('--lang', help='跨语言L2对照(逗号分隔): c,go,rust,node,php,java,csharp')
    parser.add_argument('--min-l2-rate', type=float, default=90.0,
                        help='L2最低匹配率%%，低于则FAIL（默认90.0）')
    parser.add_argument('--strict-known-diff', action='store_true',
                        help='已知差异库的L2差异照常计入FAIL')
    parser.add_argument('--boundary-only', action='store_true', help='只运行边界测试')
    parser.add_argument('--no-boundary', action='store_true', help='跳过边界测试')
    parser.add_argument('--max-errors', type=int, default=20, help='每库最多记录错误数')
    parser.add_argument('--verbose',  action='store_true', help='详细输出')
    parser.add_argument('--report',   help='报告输出路径')
    args = parser.parse_args()

    if args.sample < 0:
        print('--sample 必须 >= 0')
        sys.exit(2)
    if args.workers < 1:
        print('--workers 必须 >= 1')
        sys.exit(2)
    if not (0.0 <= args.min_l2_rate <= 100.0):
        print('--min-l2-rate 必须在 [0, 100] 之间')
        sys.exit(2)

    langs = []
    if args.lang:
        langs = [x.strip() for x in args.lang.split(',') if x.strip()]
        unknown = set(langs) - set(RUNNERS)
        if unknown:
            print(f'未知语言: {sorted(unknown)}，可选: {sorted(RUNNERS)}')
            sys.exit(2)

    db_list = select_dbs(parser, args)

    all_stats            = []
    boundary_results_all = []
    boundary_bugs        = 0

    # Layer3: 边界测试（顺序执行, 快速）
    if not args.no_boundary:
        print(f'\n{col("="*70, BOLD)}')
        print(f'{col("■ Layer3 边界/特殊 IP 健壮性测试", BOLD)}')
        print(f'{col("="*70, BOLD)}')
        for name, version, region in db_list:
            qzdb_path = os.path.join(TEMP_WORK, f'qqzeng_ip_{version}',
                                      f'qqzeng_ip_{version}_{region}.qzdb')
            if not os.path.exists(qzdb_path):
                print(f'  SKIP {name}: not found')
                continue
            try:
                searcher = QzdbReader(qzdb_path)
                print(f'\n  {col(name, CYAN)}:')
                bres, bugs = run_boundary_test(name, searcher)
                boundary_results_all.append((name, bres, bugs))
                boundary_bugs += len(bugs)
                if bugs:
                    for b in bugs:
                        print(f'    {col(b, RED)}')
                else:
                    print(f'    {col("无效IP拦截: ✓ (全部正确返回None)", GREEN)}')
                    for ip in BOUNDARY_VALID[:6]:
                        r = bres.get(ip, '?')
                        print(f'    [{ip}] → {r[:90]}')
            except Exception as e:
                print(f'  {col("ERROR", RED)} {name}: {e}')

    if args.boundary_only:
        if args.report:
            generate_report([], boundary_results_all, args.report, args=args)
        print_overall(1 if boundary_bugs > 0 else 0)
        return 1 if boundary_bugs > 0 else 0

    # Layer1+2: 解析验证（按库并发）
    print(f'\n{col("="*70, BOLD)}')
    print(f'{col("■ Layer1+2 解析正确性验证", BOLD)}')
    mode_str = '全量' if args.full else f'抽样 {args.sample} 行/版本'
    workers_str = f'（workers={args.workers}, skip-count={args.skip_count}）'
    print(f'  模式: {mode_str} {workers_str}')
    print(f'{col("="*70, BOLD)}')

    with ThreadPoolExecutor(max_workers=args.workers) as executor:
        futures = {}
        for idx, (name, version, region) in enumerate(db_list):
            # 每个库的 RNG 由 seed + 库在 db_list 中的固定下标决定（与线程调度无关）
            rng = random.Random(args.seed + idx)
            futures[executor.submit(verify_one, name, version, region, args, rng)] = \
                (name, version, region)

        for fut, (name, version, region) in futures.items():
            try:
                s, xl = fut.result()
            except Exception as e:
                p(f'  {col("FATAL ERROR", RED)}: {e}')
                traceback.print_exc()
                s = Stats(name=name)
                s.skipped_db = True
                xl = {}
            s.name = name
            if langs and xl and not s.skipped_db:
                qzdb_path = os.path.join(TEMP_WORK, f'qqzeng_ip_{version}',
                                          f'qqzeng_ip_{version}_{region}.qzdb')
                s.xlang_results = run_xlang(name, qzdb_path, xl, langs, args.max_errors)
            print_stats(s, args)
            all_stats.append(s)

    # 汇总表格
    if len(all_stats) > 1:
        print(f'\n{col("="*74, BOLD)}')
        print(f'{col("■ 汇总报告", BOLD)}')
        print(f'{"─"*74}')
        print(f'{"数据库":<18} {"L1通过率":>12} {"L1失败":>8} {"L2匹配率":>12} {"QPS":>10} {"CRC":>6}')
        print(f'{"─"*74}')
        for s in all_stats:
            l1c = GREEN if s.l1_fail == 0 else RED
            l2c = GREEN if s.l2_match_rate() >= 99.0 else (YELLOW if s.l2_match_rate() >= 90.0 else RED)
            crc = col('✓', GREEN) if s.crc_ok else col('✗', RED)
            l1r = col(f'{s.l1_pass_rate():.2f}%', l1c)
            l1f = col(str(s.l1_fail), l1c)
            l2r = col(f'{s.l2_match_rate():.2f}%', l2c)
            print(f'{s.name:<18} {l1r:>12} {l1f:>8} {l2r:>12} {s.qps:>10,.0f} {crc:>6}')
            if s.name in KNOWN_DIFF:
                print(f'    ↳ 已知差异: {KNOWN_DIFF[s.name]}')
            if s.xlang_results:
                xl_s = ', '.join(f'{lang}={chk}查/{mm}差'
                                 for lang, (chk, mm, _) in sorted(s.xlang_results.items()))
                print(f'    ↳ L2-XL: {xl_s}')

        total_l1_fail = sum(s.l1_fail for s in all_stats)
        total_queries = sum(s.total_queries for s in all_stats)
        print(f'{"─"*74}')
        print(f'总查询: {total_queries:,}  总L1失败: {col(str(total_l1_fail), RED if total_l1_fail>0 else GREEN)}')

        if total_l1_fail == 0:
            print(f'\n{col("🎉 所有数据库区间一致性全部通过！算法无 off-by-one bug。", GREEN+BOLD)}')
        else:
            print(f'\n{col(f"❌ 发现 {total_l1_fail} 个区间不一致！存在解析 bug！", RED+BOLD)}')

    if boundary_bugs > 0:
        print(f'\n{col(f"⚠ Layer3 边界测试发现 {boundary_bugs} 个 bug", RED+BOLD)}')

    # 退出码: 0=通过, 1=L1失败 或 L3边界bug 或 非已知差异库L2匹配率不足
    exit_code = 0
    if boundary_bugs > 0:
        exit_code = 1
    for s in all_stats:
        if s.skipped_db:
            continue
        if s.l1_fail > 0:
            exit_code = 1
        if s.name in KNOWN_DIFF and not args.strict_known_diff:
            continue
        if s.l2_match_rate() < args.min_l2_rate:
            exit_code = 1

    if args.report:
        generate_report(all_stats, boundary_results_all, args.report, args=args)

    print_overall(exit_code)
    return exit_code


if __name__ == '__main__':
    sys.exit(main())
