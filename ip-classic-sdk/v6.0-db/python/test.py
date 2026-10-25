# -*- coding: utf-8 -*-
"""qqzeng-ip 6.0 (.db) Python 版冒烟测试。"""

from qqzeng_ip.ipdb import IpDbSearch, fast_parse_ip


def main():
    path = IpDbSearch.find_db_path()
    if not path:
        print('数据库未找到, 跳过')
        return
    db = IpDbSearch(path)
    print('数据库加载完成: nodes=%d, strings=%d' % (db.node_count, db.string_count))

    for ip in ['1.10.11.12', '8.8.8.8', '58.213.198.68', '255.255.255.255', '1..2.3']:
        print('%s -> %s' % (ip, db.find(ip)))

    assert fast_parse_ip('1.2.3.4') == 0x01020304
    assert fast_parse_ip('1..2.3') == -1
    assert db.find_uint(0x01020304) == db.find('1.2.3.4')
    assert db.find('1.2.3.4.5') == ''

    # from_bytes 独立实例
    with open(path, 'rb') as f:
        db2 = IpDbSearch.from_bytes(f.read())
    assert db2.find('8.8.8.8') == db.find('8.8.8.8')
    print('ok')


if __name__ == '__main__':
    main()
