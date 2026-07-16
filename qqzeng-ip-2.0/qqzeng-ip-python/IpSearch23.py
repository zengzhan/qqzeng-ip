# -*- coding: utf-8 -*-
#IP数据库格式详解 qqzeng-ip.dat
#编码：UTF8  字节序：Little-Endian
#返回多个字段信息（如：亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115）

#兼容 python2,3 by https://github.com/yxy/qqzeng-ip/blob/master/qqzeng-ip-python/IpSearch.py  2018-04-23

import mmap, struct, socket, os


class IPInfo:
    """
    亚洲|中国|广东|广州|天河|电信|440106|China|CN|113.36112|23.12467
    """

    def __init__(self, text, sep="|", **kwargs):
        _raw = text.split(sep)
        self._fields = [
            'continent',
            'country',
            'province',
            'city',
            'region',
            'carrier',
            'division',
            'en_country',
            'en_short_code',
            'longitude',
            'latitude',
        ]
        if len(_raw) != len(self._fields):
            raise ValueError("Invalid ip data: {0}".format(text))
        for index, field in enumerate(self._fields):
            setattr(self, field, _raw[index])
        self._raw = _raw

    def __repr__(self):
        return str(self)

    def __str__(self):
        return '|'.join(self._raw)

    def to_dict(self):
        return {f: getattr(self, f, None) for f in self._fields}


class IpSearch:
    def __init__(self, file_name):
        self._handle = open(file_name, "rb")
        self.data = mmap.mmap(
            self._handle.fileno(), 0, access=mmap.ACCESS_READ)
        self.dict = {}
        self.start = self.int_from_4byte(0)
        # index_last_offset = self.int_from_4byte(4)
        prefix_start_offset = self.int_from_4byte(8)
        prefix_end_offset = self.int_from_4byte(12)
        i = prefix_start_offset
        while i <= prefix_end_offset:
            prefix = self.int_from_1byte(i)
            map_dict = {
                'prefix': prefix,
                'start_index': self.int_from_4byte(i + 1),
                'end_index': self.int_from_4byte(i + 5)
            }
            self.dict[prefix] = map_dict
            i += 9

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_value, exc_tb):
        self.close()

    def close(self):
        self._handle.close()

    def lookup(self, ip):
        ipdot = ip.split('.')
        prefix = int(ipdot[0])
        if prefix < 0 or prefix > 255 or len(ipdot) != 4:
            raise ValueError("invalid ip address")
        intIP = self.ip2long(ip)
        high = 0
        low = 0
        startIp = 0
        endIp = 0
        local_offset = 0
        local_length = 0
        if prefix in self.dict:
            low = self.dict.get(prefix)['start_index']
            high = self.dict.get(prefix)['end_index']
        else:
            return None
        my_index = self.binary_search(low, high, intIP) if low != high else low
        start_num, end_num, local_offset, local_length = self.get_index(
            my_index)
        if start_num <= intIP and end_num >= intIP:
            return IPInfo(self.get_local(local_offset, local_length))
        return None

    def get_index(self, left):
        i = self.start + (left * 12)
        start_num = self.int_from_4byte(i)
        end_num = self.int_from_4byte(4 + i)
        local_offset = self.int_from_3byte(8 + i)
        local_length = self.int_from_1byte(11 + i)
        return start_num, end_num, local_offset, local_length

    def get_local(self, offset, size):
        return self.data[offset:offset + size].decode('utf-8')

    def binary_search(self, low, high, k):
        M = 0
        while low <= high:
            mid = (low + high) // 2
            end_ip_num = self.get_end_ip_num(mid)
            if end_ip_num >= k:
                M = mid
                if mid == 0:
                    break
                high = mid - 1
            else:
                low = mid + 1
        return M

    def get_end_ip_num(self, left):
        left_offset = self.start + (left * 12)
        return self.int_from_4byte(left_offset + 4)

    def ip2long(self, ip):
        _ip = socket.inet_aton(ip)
        return struct.unpack("!L", _ip)[0]

    def int_from_4byte(self, offset):
        return struct.unpack('<L', self.data[offset:offset + 4])[0]

    def int_from_3byte(self, offset):
        return struct.unpack('<L', self.data[offset:offset + 3] + b'\x00')[0]

    def int_from_1byte(self, offset):
        return struct.unpack('B', self.data[offset:offset + 1])[0]
