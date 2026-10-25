# -*- coding: utf-8 -*-
"""qqzeng-ip 2.0 (.dat) Python 版冒烟测试。"""

from IpSearch import IpSearch
from IpSearch23 import IpSearch as IpSearch23, IPInfo


def main():
    path = IpSearch.find_db_path()
    if path is None:
        print('no .dat found, skip')
        return
    with IpSearch(path) as s:
        print(s.Find('58.213.198.68'))
        assert s.Find('8.8.8.8') in ('', s.Find('8.8.8.8'))
        assert s.find('abc.def.ghi.jkl') == ''
        assert s.find_uint(0x01020304) == s.find('1.2.3.4')
    info = IpSearch23(path).lookup('58.213.198.68')
    assert isinstance(info, IPInfo)
    print('ok:', info)


if __name__ == '__main__':
    main()
