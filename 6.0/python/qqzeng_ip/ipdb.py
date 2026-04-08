import struct
import os
import socket

class IpDbSearch:
    _instance = None
    _init_done = False

    def __new__(cls, *args, **kwargs):
        if not cls._instance:
            cls._instance = super(IpDbSearch, cls).__new__(cls)
        return cls._instance

    def __init__(self, db_path=None):
        if IpDbSearch._init_done:
            return
        
        self.data = None
        self.geoisp_arr = None
        self.index_start_index = 0x30004
        self.end_mask = 0x800000
        self.compl_mask = ~self.end_mask
        
        self._load_db(db_path)
        IpDbSearch._init_done = True

    def _load_db(self, db_path=None):
        if not db_path:
            db_path = self._find_db_path()
            if not db_path:
                raise FileNotFoundError("Fatal: Cannot find qqzeng-ip-6.0-global.db")
        
        with open(db_path, 'rb') as f:
            self.data = f.read()

        if len(self.data) < self.index_start_index:
             raise ValueError("Invalid database file size")

        # 读取节点数量 (小端序)
        node_count = struct.unpack('<I', self.data[:4])[0]
        
        string_area_offset = self.index_start_index + node_count * 6
        
        if string_area_offset > len(self.data):
             raise ValueError("Invalid metadata")

        # 解析字符串区
        content = self.data[string_area_offset:].decode('utf-8')
        self.geoisp_arr = content.split('\t')

    def find(self, ip: str) -> str:
        if not ip:
            return ""
        
        try:
            prefix, suffix = self._fast_parse_ip(ip)
        except:
            return ""

        # 一级索引
        # Python没有直接的 readInt24，我们需要模拟它
        # 索引区从4开始
        record = self._read_int24(4 + prefix * 3)

        while (record & self.end_mask) != self.end_mask:
            bit = (suffix >> 15) & 1
            offset = self.index_start_index + record * 6 + bit * 3
            record = self._read_int24(offset)
            suffix <<= 1
        
        index = record & self.compl_mask
        if index < len(self.geoisp_arr):
            return self.geoisp_arr[index]
        return ""

    def _read_int24(self, offset):
        # Python 优化: 直接从 bytes 切片和位运算
        # 大端序逻辑: high mid low
        b0 = self.data[offset]
        b1 = self.data[offset+1]
        b2 = self.data[offset+2]
        return (b0 << 16) | (b1 << 8) | b2

    def _fast_parse_ip(self, ip_str):
        # 这个版本尽量避免 split
        # 但在 Python 中, split+int 可能比纯循环快，因为是在 C 层面执行的
        # 为了保持逻辑一致性，使用位操作
        
        # 简单实现，Python中 struct.unpack 配合 inet_aton 可能最快
        # 但 inet_aton 仅支持 IPv4 且依赖 socket 库
        try:
            packed = socket.inet_aton(ip_str)
            # 大端序 packed -> 整数
            ip_int = struct.unpack('>I', packed)[0]
            prefix = ip_int >> 16
            suffix = ip_int & 0xFFFF
            return prefix, suffix
        except:
            raise ValueError("Invalid IP")

    def _find_db_path(self):
        # 尝试多个路径
        base_dir = os.path.dirname(os.path.abspath(__file__))
        attempts = [
            os.path.join(os.getcwd(), 'qqzeng-ip-6.0-global.db'),
            os.path.join(base_dir, 'qqzeng-ip-6.0-global.db'),
            os.path.join(base_dir, '../data/qqzeng-ip-6.0-global.db'),
            os.path.join(base_dir, '../../data/qqzeng-ip-6.0-global.db'),
            os.path.join(base_dir, '../../../data/qqzeng-ip-6.0-global.db'),
            '../data/qqzeng-ip-6.0-global.db'
        ]
        
        for path in attempts:
            if os.path.exists(path):
                return path
        return None
