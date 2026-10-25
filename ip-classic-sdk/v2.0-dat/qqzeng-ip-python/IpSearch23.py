# -*- coding: utf-8 -*-
"""兼容层: 保留历史 IpSearch23.py 的 IPInfo 结构化结果与 lookup() 接口。

核心实现已统一到 IpSearch.py,本文件仅做薄包装:
    from IpSearch23 import IpSearch, IPInfo
    info = IpSearch('qqzeng-ip.dat').lookup('8.8.8.8')   # -> IPInfo 或 None
"""

from IpSearch import IpSearch as _Core


class IPInfo(object):
    """单条归属地信息(11 字段)。"""

    __slots__ = ('continent', 'country', 'province', 'city', 'district', 'isp',
                 'dma_code', 'country_en', 'country_code', 'longitude', 'latitude',
                 'raw')

    _FIELDS = ('continent', 'country', 'province', 'city', 'district', 'isp',
               'dma_code', 'country_en', 'country_code', 'longitude', 'latitude')

    def __init__(self, text, sep='|'):
        parts = text.split(sep)
        parts += [''] * (11 - len(parts))
        self.raw = text
        for name, v in zip(self._FIELDS, parts):
            setattr(self, name, v)
        try:
            self.longitude = float(self.longitude) if self.longitude else 0.0
            self.latitude = float(self.latitude) if self.latitude else 0.0
        except ValueError:
            pass

    def to_dict(self):
        return {k: getattr(self, k) for k in self._FIELDS}

    def __repr__(self):
        return 'IPInfo(%r)' % (self.raw,)

    def __str__(self):
        return self.raw


class IpSearch(_Core):
    """在统一实现之上提供 lookup() -> IPInfo | None(未命中 None)。"""

    def lookup(self, ip):
        raw = self.find(ip)
        if not raw:
            return None
        return IPInfo(raw)


if __name__ == '__main__':
    s = IpSearch()
    print(s.lookup('58.213.198.68'))
