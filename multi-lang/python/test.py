"""
QzdbSearcher - Python SDK calling example

Usage: python test.py
Place qqzeng_ip_std_china.qzdb in the same directory or specify the path.
"""

import os
from qzdb import QzdbSearcher


def find_db():
    for candidate in [
        'qqzeng_ip_std_china.qzdb',
        '../data/qqzeng_ip_std_china.qzdb',
        'data/qqzeng_ip_std_china.qzdb',
    ]:
        if os.path.exists(candidate):
            return candidate
    return None


def main():
    db_path = find_db()
    if not db_path:
        print('Database file not found')
        return

    ipdb = QzdbSearcher.get_instance(db_path)
    print(f'Version code: {ipdb.version_code}, pools: {ipdb.pool_count}')
    print(f'Fields ({len(ipdb.field_names)}): {", ".join(ipdb.field_names)}\n')

    # Query sample V4 IPs
    for ip in ['114.114.114.114', '223.5.5.5', '8.8.8.8']:
        result = ipdb.find(ip)
        pipe = result.to_pipe() if result else '(null)'
        print(f'find("{ip}") => {pipe}')

    # Query a V6 IP
    result = ipdb.find('2408:8000:9000::1')
    pipe = result.to_pipe() if result else '(null)'
    print(f'find("2408:8000:9000::1") => {pipe}')

    # Get structured fields
    print('\n--- Structured fields for 114.114.114.114 ---')
    loc = ipdb.find('114.114.114.114')
    if loc:
        for name in ipdb.field_names:
            print(f'  {name}: {getattr(loc, name, "")}')
    print("TEST_PASS")


if __name__ == '__main__':
    main()
