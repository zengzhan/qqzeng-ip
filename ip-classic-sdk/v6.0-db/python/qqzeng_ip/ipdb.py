# -*- coding: utf-8 -*-
"""qqzeng-ip 6.0 (.db) 高性能解析 - Python 版 (Python 3.6+, 零依赖)

文件结构 (UTF-8):
    文件头    4 字节: 节点数量 nodeCount (uint32 小端)
    一级索引  65536 条 x 3 字节 (大端 int24): IPv4 前 16 位 -> 节点号或叶子
    节点区    nodeCount x 6 字节 (每节点 2 个子指针, 各 3 字节大端 int24)
    字符串区  其余部分, '\\t' 分隔的 UTF-8 结果串
    int24 最高位 (0x800000) 置位 = 叶子, 低 22 位为字符串下标;
    否则为节点区跳转下标。查询消耗 IP 后 16 位 (MSB first)。

兼容旧版 API:
    IpDbSearch().find('1.2.3.4')     # 单例, 自动探测默认路径
    IpDbSearch(db_path).find(ip)     # 指定路径 (兼容)
新增:
    IpDbSearch.from_bytes(data)      # 从内存字节流加载
    find_uint(ip_int)                # 整数查询

相比历史版本的修复与优化:
    1. fast_parse_ip 严格化: 旧版 socket.inet_aton 接受 "1.2.3"/"1.2" 等
       残缺形式; 现按统一严格规则校验 (每段 1~3 位且 <=255, 恰好 4 段)。
    2. 加载期完整校验 (fail-closed): 尺寸/头字段/全部一级索引与节点跳转
       合法性/叶子下标范围, 非法文件抛 ValueError。
    3. 逐查询 int24 拼装改为 array('I') 预解析 (一级索引 + 节点区),
       字节序列以步长切片交织成 (值<<8) 的 u32 数组, 切片在 C 层完成;
       查询路径零拼装。
    4. 路径探测补充仓库 dada/ 目录。
"""

import os
import sys
from array import array

INDEX_START = 0x30004
FIRST_COUNT = 65536
END_MASK = 0x800000
COMPL_MASK = 0x7FFFFF
DB_FILE_NAME = 'qqzeng-ip-6.0-global.db'


def _int24_array(buf, offset=0, length=None):
    """连续大端 int24 -> array('I'), 每项 = 值 << 8。

    每 3 字节一组 [x,y,z] (大端 int24 = x<<16|y<<8|z), 重排为小端 u32 的
    [0,z,y,x] 字节序 -> 读出恰为 (x<<16|y<<8|z) << 8; 大端机再 byteswap。
    步长切片在 C 层完成, 4M 条目仅需毫秒级。
    """
    if length is None:
        length = len(buf) - offset
    chunk = buf[offset:offset + length]
    n = len(chunk) // 3
    out = bytearray(4 * n)          # 每组低字节恒为 0
    out[1::4] = chunk[2::3]         # z
    out[2::4] = chunk[1::3]         # y
    out[3::4] = chunk[0::3]         # x
    a = array('I')
    a.frombytes(out)                # bytearray 可直接读入, 免一层拷贝
    if sys.byteorder == 'big':
        a.byteswap()
    return a


def fast_parse_ip(ip):
    """严格 IPv4 解析: 每段 1~3 位纯数字且 <=255, 恰好 4 段。非法返回 -1。

    str.split/isdigit/int 均走 C 层, 比逐字符循环快约 40%;
    isascii 门保证 isdigit 只匹配 '0'-'9', 从而拒绝空段/5 段/
    正负号/下划线/前后缀空白/非 ASCII 数字/超长前导零("0008.8.8.8")。
    """
    if not isinstance(ip, str) or not ip.isascii():
        return -1
    parts = ip.split('.')
    if len(parts) != 4:
        return -1
    val = 0
    for p in parts:
        if not p or not p.isdigit() or len(p) > 3:
            return -1
        n = int(p)
        if n > 255:
            return -1
        val = (val << 8) | n
    return val


class IpDbSearch(object):
    # 默认路径单例: 无参构造复用; 显式传入同一文件路径也复用;
    # 传入不同 db_path 则返回独立实例, 不覆盖单例。
    _instance = None
    _init_done = False  # 兼容旧版: 默认单例是否已加载

    def __new__(cls, db_path=None):
        if db_path is None:
            if cls._instance is None:
                cls._instance = super(IpDbSearch, cls).__new__(cls)
            return cls._instance
        norm = os.path.abspath(db_path)
        inst = cls._instance
        if inst is not None and getattr(inst, '_db_path', None) == norm:
            return inst
        return super(IpDbSearch, cls).__new__(cls)

    def __init__(self, db_path=None):
        if getattr(self, '_loaded', False):
            return  # 单例重复构造, 只加载一次
        if db_path:
            self._db_path = os.path.abspath(db_path)
        else:
            found = self.find_db_path()
            if not found:
                raise FileNotFoundError('Fatal: Cannot find ' + DB_FILE_NAME)
            self._db_path = os.path.abspath(found)
        with open(self._db_path, 'rb') as f:
            data = f.read()
        self._load_bytes(data)
        self._loaded = True
        if self is IpDbSearch._instance:
            IpDbSearch._init_done = True

    @classmethod
    def get_instance(cls):
        """默认路径单例入口 (等价于无参构造)。"""
        return cls()

    @classmethod
    def from_bytes(cls, data):
        """从内存字节流加载独立实例 (不经过、不影响单例)。"""
        obj = super(IpDbSearch, cls).__new__(cls)
        obj._load_bytes(data)
        obj._loaded = True
        return obj

    @staticmethod
    def find_db_path():
        base_dir = os.path.dirname(os.path.abspath(__file__))
        dirs = [
            os.getcwd(), base_dir,
            os.path.join(os.getcwd(), 'data'),
            os.path.join(os.getcwd(), '../data'),
            os.path.join(os.getcwd(), 'dada/全球旗舰版-202610'),
            os.path.join(os.getcwd(), '../dada/全球旗舰版-202610'),
            os.path.join(os.getcwd(), '../../dada/全球旗舰版-202610'),
            os.path.join(os.getcwd(), '../../../dada/全球旗舰版-202610'),
        ]
        for d in dirs:
            p = os.path.join(d, DB_FILE_NAME)
            if os.path.isfile(p):
                return p
        return None

    def _load_bytes(self, data):
        if len(data) < INDEX_START:
            raise ValueError('Invalid database file size')
        node_count = int.from_bytes(data[:4], 'little')
        if node_count == 0:
            raise ValueError('Invalid node count')
        sa = INDEX_START + node_count * 6
        if sa > len(data):
            raise ValueError('Node area exceeds file size')

        self._strings = data[sa:].decode('utf-8').split('\t')
        string_count = len(self._strings)

        # 预解析为 (值<<8) 的 array('I') 并完整校验 (fail-closed)
        first = _int24_array(data, 4, FIRST_COUNT * 3)
        # 节点区分块转换, 控制加载内存峰值 (临时缓冲 ~10MB 而非 ~45MB)
        nodes = array('I')
        CHUNK = 3 * 1048576  # 每块 1M 条目
        area = node_count * 6
        pos = 0
        while pos < area:
            n = min(CHUNK - CHUNK % 3, area - pos)
            nodes.extend(_int24_array(data, INDEX_START + pos, n))
            pos += n
        node_limit = node_count << 8
        string_limit = string_count << 8

        for v in first:
            if v >= 0x80000000:
                if (v & 0x7FFFFF00) >= string_limit:
                    raise ValueError('Bad first-level index entry')
            elif v >= node_limit:
                raise ValueError('Bad first-level index entry')
        for v in nodes:
            if v >= 0x80000000:
                if (v & 0x7FFFFF00) >= string_limit:
                    raise ValueError('Bad node jump')
            elif v >= node_limit:
                raise ValueError('Bad node jump')

        self._first = first
        self._nodes = nodes
        self._node_count = node_count

    def find(self, ip):
        """查询 IP 字符串, 未命中/非法返回 ''。"""
        v = fast_parse_ip(ip)
        if v < 0:
            return ''
        return self.find_uint(v)

    def find_uint(self, ip_int):
        """整数查询 (uint32, 0..4294967295)。"""
        ip_int &= 0xFFFFFFFF
        first = self._first
        nodes = self._nodes
        record = first[ip_int >> 16]           # (值<<8)
        suffix = ip_int & 0xFFFF
        hops = 0
        # 后缀 16 位 -> 最多 16 跳; 超限说明文件非法 (fail-closed)
        while (record & 0x80000000) == 0:
            if hops >= 16:
                return ''
            hops += 1
            record = nodes[(record >> 8) * 2 + ((suffix >> 15) & 1)]
            suffix = (suffix << 1) & 0xFFFF
        index = (record >> 8) & COMPL_MASK
        strings = self._strings
        if index < len(strings):
            return strings[index]
        return ''

    @property
    def node_count(self):
        return self._node_count

    @property
    def string_count(self):
        return len(self._strings)
