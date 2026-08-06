"""Re-generate verify_*.txt files from .qzdb files"""
import random
import struct
import sys
import os

sys.path.insert(0, os.path.dirname(__file__))
from qzdb import QzdbReader


def generate(data_dir, qzdb_name, version):
    path = os.path.join(data_dir, qzdb_name)
    if not os.path.exists(path):
        print(f'  {qzdb_name}: 文件不存在，跳过')
        return

    from qzdb import QzdbReader
    QzdbReader._instance = None
    QzdbReader._init_done = False
    s = QzdbReader(path, version=version)

    rng = random.Random(42)
    region = 'global' if 'global' in qzdb_name else 'china'

    # V4: 采样
    v4_ips = set()
    for _ in range(3000):
        ip = rng.randint(0, 0xFFFFFFFF)
        g = s.find_uint(ip)
        if g and g.country:
            v4_ips.add(ip)
    for _ in range(200):
        high = rng.randint(0, 65535)
        low = rng.randint(0, 65535)
        ip = (high << 16) | low
        g = s.find_uint(ip)
        if g and g.country:
            v4_ips.add(ip)

    out_v4 = os.path.join(data_dir, f'verify_{version}_{region}_v4.txt')
    with open(out_v4, 'w', encoding='utf-8') as f:
        for ip in sorted(v4_ips):
            g = s.find_uint(ip)
            if g:
                f.write(f'{ip}|{g.to_pipe()}\n')
    print(f'  {version} V4: wrote {len(v4_ips)} 条 -> {os.path.basename(out_v4)}')

    # V6: 采样
    d = s._data
    off = s._off_v6data
    if off < len(d):
        count = struct.unpack_from('<I', d, off)[0]
        entry_size = 32 + s._geo_id_size
        data_start = off + 4

        v6_cases = set()
        for _ in range(500):
            high = rng.getrandbits(64)
            low = rng.getrandbits(64)
            g = s._find_v6(high, low)
            if g and g.country:
                v6_cases.add((high, low))

        for i in range(min(count, 200)):
            p = data_start + i * entry_size
            s_hi = struct.unpack_from('>Q', d, p)[0]
            s_lo = struct.unpack_from('>Q', d, p + 8)[0]
            g = s._find_v6(s_hi, s_lo)
            if g and g.country:
                v6_cases.add((s_hi, s_lo))

        out_v6 = os.path.join(data_dir, f'verify_{version}_{region}_v6.txt')
        with open(out_v6, 'w', encoding='utf-8') as f:
            for high, low in sorted(v6_cases):
                g = s._find_v6(high, low)
                if g:
                    f.write(f'{high}:{low}|{g.to_pipe()}\n')
        print(f'  {version} V6: wrote {len(v6_cases)} 条 -> {os.path.basename(out_v6)}')


def main():
    data_dir = os.path.join(os.path.dirname(__file__), '..', 'data')

    files = [
        ('qqzeng_ip_std_china.qzdb', 'std'),
        ('qqzeng_ip_max_china.qzdb', 'max'),
        ('qqzeng_ip_max_global.qzdb', 'max'),
    ]
    for qzdb_name, version in files:
        generate(data_dir, qzdb_name, version)


if __name__ == '__main__':
    main()
