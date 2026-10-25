# -*- coding: utf-8 -*-
"""
qqzeng-ip 2.0 (.dat) 高性能解析 - Python 版 (Python 3.8 ~ 3.14+)

文件结构 (UTF-8, Little-Endian):
    文件头  16 字节 (4-4-4-4): [索引区首条偏移][索引区末条偏移][前缀区首偏移][前缀区末偏移]
    内容区  唯一不重复的地区字符串,按 (offset,len) 被索引区引用
    索引区  12 字节/条 (4-4-3-1): [起始IP][结束IP][地区偏移24b][长度8b]
    前缀区   9 字节/条 (1-4-4): [前缀0-255][索引区start][索引区end]

用法:
    with IpSearch('qqzeng-ip.dat') as s:       # 指定路径(可多实例)
        s.Find('1.2.3.4')
    s = IpSearch()                              # 自动探测默认路径
    s = IpSearch.from_bytes(buf)                # 从内存字节流加载
    s.find_uint(0x01020304)                     # 整数查询

未命中/非法 IP 一律返回 ''(历史版本返回 'N/A',已统一为 '')。

相比历史版本的修复与优化:
    1. 修复 __exit__ 签名缺参 / close() 引用不存在的 self._handle / exit(0)。
    2. 前缀 dict 改为 [None]*256 列表,缺失前缀以 None 标记
       (修复 0.0.0.0/8 前缀 low=high=0 被判未命中的 bug)。
    3. inet_aton(接受 "1.2.3" 等残缺 IP)改为严格 fast_parse_ip 校验。
    4. 索引区经 array.frombytes + 切片一步预解析为 array('I')(C 级速度),
       二分查找走紧凑数组,配合 bisect 模块的 C 实现。
    5. 字符串池去重常驻;加载时完整校验文件结构,fail-closed 抛 RuntimeError。
"""

import os
import struct
from array import array
from bisect import bisect_left

_DEFAULT_NAMES = ('qqzeng-ip-utf8.dat', 'qqzeng-ip.dat',
                  'qqzeng-ip-china-utf8.dat', 'qqzeng-ip-global-utf8.dat')
_DEFAULT_DIRS = ('.', 'dada/国内精华版-202610', 'dada/全球旗舰版-202610',
                 '../dada/国内精华版-202610', '../dada/全球旗舰版-202610',
                 '../../dada/国内精华版-202610', '../../dada/全球旗舰版-202610')

_le32 = struct.Struct('<I').unpack_from


class IpSearch(object):
    """qqzeng-ip 2.0 (.dat) 查询器(初始化后只读,线程安全)。"""

    def __init__(self, file_name=None):
        if file_name is None:
            file_name = self.find_db_path()
            if file_name is None:
                raise RuntimeError('IpSearch: no loadable .dat found')
        path = file_name
        if not os.path.isfile(path):
            # 兼容旧版: 相对模块所在目录解析
            cand = os.path.join(os.path.dirname(os.path.abspath(__file__)), file_name)
            path = cand if os.path.isfile(cand) else file_name
        with open(path, 'rb') as f:
            data = f.read()
        self._load(data)

    @classmethod
    def from_bytes(cls, buf):
        """从内存字节流加载(bytes / bytearray / memoryview)。"""
        obj = cls.__new__(cls)
        obj._load(bytes(buf))
        return obj

    @staticmethod
    def find_db_path():
        for d in _DEFAULT_DIRS:
            for n in _DEFAULT_NAMES:
                p = n if d == '.' else os.path.join(d, n)
                if os.path.isfile(p):
                    return p
        return None

    # -- 加载 --

    def _load(self, data):
        n = len(data)
        if n < 16:
            raise RuntimeError('database too small')

        first_index = _le32(data, 0)[0]
        last_index = _le32(data, 4)[0]
        pref_start = _le32(data, 8)[0]
        pref_end = _le32(data, 12)[0]

        if (first_index < 16 or last_index < first_index
                or (last_index - first_index) % 12 != 0
                or pref_end < pref_start or pref_start < 16
                or (pref_end - pref_start) % 9 != 0):
            raise RuntimeError('bad header')

        record_count = (last_index - first_index) // 12 + 1
        prefix_count = (pref_end - pref_start) // 9 + 1
        if first_index + 12 * record_count > n or pref_start + 9 * prefix_count > n:
            raise RuntimeError('header exceeds file size')

        pref = [None] * 256
        for k in range(prefix_count):
            o = pref_start + k * 9
            p = data[o]
            lo = _le32(data, o + 1)[0]
            hi = _le32(data, o + 5)[0]
            if lo > hi or hi >= record_count:
                raise RuntimeError('bad prefix range')
            pref[p] = (lo, hi)
        self._pref = pref

        # 索引区一次 frombytes,再按 stride-3 切出三列紧凑 array('I')
        region = data[first_index:first_index + 12 * record_count]
        rec = array('I')
        rec.frombytes(region)
        self._starts = rec[0::3]
        self._ends = rec[1::3]
        packed = rec[2::3]  # offset24 | len<<24
        del rec, region

        # 字符串池去重 + 记录 -> 字符串下标
        seen = {}
        strings = []
        str_idx = array('I', bytes(4 * record_count))
        for i in range(record_count):
            pk = packed[i]
            off = pk & 0xFFFFFF
            slen = pk >> 24
            if off + slen > n:
                raise RuntimeError('string region out of range')
            key = off * 256 + slen
            sid = seen.get(key)
            if sid is None:
                sid = len(strings)
                strings.append(data[off:off + slen].decode('utf-8'))
                seen[key] = sid
            str_idx[i] = sid
        self._str_idx = str_idx
        self._strings = strings

    # -- 上下文管理 --

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        return False

    def close(self):
        """兼容旧接口;解析结果为纯内存结构,无需显式释放。"""
        pass

    # -- 查询 --

    @staticmethod
    def fast_parse_ip(ip):
        """严格 IPv4 解析: 每段 1~3 位数字且 <=255,恰好 4 段。非法返回 -1。"""
        if not isinstance(ip, str):
            return -1
        val = 0
        result = 0
        shift = 24
        digits = 0
        for c in ip:
            if '0' <= c <= '9':
                val = val * 10 + (ord(c) - 48)
                if val > 255:
                    return -1
                digits += 1
                if digits > 3:
                    return -1  # 拒绝 "0008.8.8.8" 这类超长前导零
            elif c == '.':
                if digits == 0 or shift == 0:
                    return -1  # 空段或第 5 段
                result |= val << shift
                val = 0
                digits = 0
                shift -= 8
            else:
                return -1
        if digits == 0 or shift != 0:
            return -1
        return result | val

    def Find(self, ip):
        """兼容旧版方法名: 未命中/非法返回 ''。"""
        return self.find(ip)

    def find(self, ip):
        v = self.fast_parse_ip(ip)
        if v < 0:
            return ''
        return self.find_uint(v)

    def find_uint(self, val):
        """整数查询(跳过字符串解析,高性能场景)。"""
        rng = self._pref[(val >> 24) & 0xFF]
        if rng is None:
            return ''  # 前缀不存在(不能以 high==0 判未命中)
        lo, hi = rng
        if lo == hi:
            idx = lo
        else:
            # bisect_left 在 ends 中找第一个 >= val 的下标 (C 实现)
            idx = bisect_left(self._ends, val, lo, hi + 1)
            if idx > hi:
                idx = hi
        if self._starts[idx] <= val <= self._ends[idx]:
            return self._strings[self._str_idx[idx]]
        return ''


if __name__ == '__main__':
    s = IpSearch()
    print(s.Find('58.213.198.68'))
