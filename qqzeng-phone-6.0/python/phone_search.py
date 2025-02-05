import os
import struct
from typing import Optional

class PhoneSearch6Db:
    _instance = None

    # 常量定义
    HEADER_SIZE = 32  # 8个uint的头部
    PREFIX_COUNT = 200  # 电话号码前缀总数（0-199）
    BITMAP_POP_COUNT_OFFSET = 0x4E2  # 位图统计信息偏移量

    def __init__(self):
        self.data = bytes()
        self.region_isps = []
        self.index = [{'bitmap_offset': 0, 'data_offset': 0} for _ in range(self.PREFIX_COUNT)]
        self.load_database()

    def __new__(cls):
        if cls._instance is None:
            cls._instance = super().__new__(cls)
        return cls._instance

    def load_database(self):
        file_path = os.path.join(os.path.dirname(__file__), "qqzeng-phone-6.0.db")
        if not os.path.exists(file_path):
            raise FileNotFoundError(f"Database file not found: {file_path}")

        with open(file_path, "rb") as f:
            self.data = f.read()

        # 解析头部（小端序）
        header = struct.unpack_from('<8I', self.data, 0)

        # 解析地区与运营商表
        regions_start = self.HEADER_SIZE
        isps_start = regions_start + header[1]
        index_start = isps_start + header[2]

        regions = self.data[regions_start:regions_start + header[1]].decode('utf-8').split('&')
        isps = self.data[isps_start:isps_start + header[2]].decode('utf-8').split('&')

        # 构建地区-运营商组合
        entry_offset = header[3]
        self.region_isps = []
        for i in range(header[4]):
            entry = struct.unpack_from('<H', self.data, entry_offset + i * 2)[0]
            region_idx = entry >> 5
            isp_idx = entry & 0x1F
            self.region_isps.append(f"{regions[region_idx]}|{isps[isp_idx]}")

        # 构建前缀索引表
        pos = index_start
        for i in range(self.PREFIX_COUNT):
            if pos + 12 > len(self.data):
                break

            prefix = struct.unpack_from('<I', self.data, pos)[0]
            if prefix == i:
                bitmap_offset = struct.unpack_from('<i', self.data, pos + 4)[0]
                data_offset = struct.unpack_from('<i', self.data, pos + 8)[0]
                self.index[i] = {'bitmap_offset': bitmap_offset, 'data_offset': data_offset}
                pos += 12
            else:
                self.index[i] = {'bitmap_offset': 0, 'data_offset': 0}

    def query(self, phone: str) -> Optional[str]:
        if len(phone) != 7 or not phone.isdigit():
            raise ValueError("Invalid phone number format")

        prefix = int(phone[:3])
        sub_num = int(phone[3:])

        if prefix < 0 or prefix >= self.PREFIX_COUNT:
            return None

        index_entry = self.index[prefix]
        if index_entry['bitmap_offset'] == 0 or index_entry['data_offset'] == 0:
            return None

        byte_index = sub_num >> 3
        bit_index = sub_num & 0b0111

        if index_entry['bitmap_offset'] + byte_index >= len(self.data):
            return None

        bitmap = self.data[index_entry['bitmap_offset'] + byte_index]
        if (bitmap & (1 << bit_index)) == 0:
            return None

        # 计算有效数据位置
        pop_count_offset = index_entry['bitmap_offset'] + self.BITMAP_POP_COUNT_OFFSET + (byte_index << 1)
        pre_count = struct.unpack_from('<H', self.data, pop_count_offset)[0]

        # 计算本地位数
        mask = (1 << bit_index) - 1
        local_count = bin(bitmap & mask).count('1')

        data_pos = index_entry['data_offset'] + ((pre_count + local_count) << 1)
        if data_pos + 2 > len(self.data):
            return None

        entry = struct.unpack_from('<H', self.data, data_pos)[0]
        return self.region_isps[entry] if entry < len(self.region_isps) else None