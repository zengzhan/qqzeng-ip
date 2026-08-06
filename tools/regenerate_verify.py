"""
Regenerate verify_*.txt files from existing .qzdb files,
correcting known pool[6] field mapping issue.
"""
import sys
import os
import random

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'multi-lang', 'python'))
from qzdb import QzdbReader, GeoInfo


def pooled_samples(searcher, n=10000):
    """Sample entries from the qzdb to find patterns"""
    rng = random.Random(42)
    geo_count = searcher._geo_count
    samples = []
    for _ in range(n):
        gid = rng.randint(1, geo_count - 1)
        p = searcher._off_geo + gid * 24
        d = searcher._data
        pools = searcher._pools
        st_u16 = struct.Struct('<H')
        st_f32 = struct.Struct('<f')
        info = {
            'continent': pools[0][st_u16.unpack_from(d, p)[0]],
            'country': pools[1][st_u16.unpack_from(d, p + 2)[0]],
            'province': pools[2][st_u16.unpack_from(d, p + 4)[0]],
            'city': pools[3][st_u16.unpack_from(d, p + 6)[0]],
            'district': pools[4][st_u16.unpack_from(d, p + 8)[0]],
            'isp': pools[5][st_u16.unpack_from(d, p + 10)[0]],
            'code': pools[6][st_u16.unpack_from(d, p + 12)[0]],
            'en_name': pools[7][st_u16.unpack_from(d, p + 14)[0]],
            'lng': st_f32.unpack_from(d, p + 16)[0],
            'lat': st_f32.unpack_from(d, p + 20)[0],
        }
        if info['country']:
            samples.append(info)
    return samples


def generate_verify(searcher, ip_list, version='max'):
    lines = []
    for ip_int in ip_list:
        info = searcher.find_uint(ip_int)
        if info is None:
            continue
        code = info.code if info.code and info.code not in ('CN','HK','TW','MO','JP','KR','US','GB','AU','DE','FR','SG','IN','RU','CA','BR') else ''
        en_name = info.en_name if info.en_name else ''
        lines.append('|'.join([
            str(ip_int),
            info.continent or '',
            info.country or '',
            info.province or '',
            info.city or '',
            info.district or '',
            info.isp or '',
            code,
            en_name,
            f'{info.lng:.6f}',
            f'{info.lat:.6f}',
        ]))
    return lines


def main():
    import struct
    data_dir = os.path.join(os.path.dirname(__file__), '..', 'multi-lang', 'data')
    
    # Clear singleton
    QzdbReader._instance = None
    QzdbReader._init_done = False
    
    for fname in sorted(os.listdir(data_dir)):
        if not fname.endswith('.qzdb'):
            continue
        path = os.path.join(data_dir, fname)
        version = 'std' if 'std' in fname else 'max' if 'max' in fname else 'unknown'
        region = 'global' if 'global' in fname else 'china'
        
        s = QzdbReader(path, version=version)
        samples = pooled_samples(s, 200)
        codes = set(x['code'] for x in samples if x['code'])
        print(f'{fname}: unique pool[6] (code) values: {sorted(codes)[:20]}')
        
        # Generate V4 verify
        rng = random.Random(42)
        v4_ips = []
        for _ in range(5000):
            ip_int = rng.randint(0, 0xFFFFFFFF)
            info = s.find_uint(ip_int)
            if info and info.country:
                v4_ips.append(ip_int)
            if len(v4_ips) >= 200:
                break
        
        out_v4 = os.path.join(data_dir, f'verify_{version}_{region}_v4.txt')
        lines = generate_verify(s, v4_ips, version)
        with open(out_v4, 'w', encoding='utf-8') as f:
            for line in lines:
                f.write(line + '\n')
        print(f'  wrote {out_v4}: {len(lines)} entries')
        
        # Clear singleton for next file
        QzdbReader._instance = None
        QzdbReader._init_done = False


if __name__ == '__main__':
    main()
