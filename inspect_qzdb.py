"""Inspect existing qzdb files to check pool data"""
import sys
import os
sys.path.insert(0, 'multi-lang/python')
from qzdb import QzdbSearcher

data_dir = 'multi-lang/data'
for f in sorted(os.listdir(data_dir)):
    if not f.endswith('.qzdb'):
        continue
    path = os.path.join(data_dir, f)
    ipdb = QzdbSearcher(path)
    version = 'std' if 'std' in f else 'max' if 'max' in f else 'unknown'
    print(f'=== {f} (version={version}) ===')
    print(f'  geos: {ipdb._geo_count}')

    for pi in range(8):
        pool = ipdb._pools[pi]
        empty = all(s == '' for s in pool)
        samples = [x for x in pool[:5] if x]
        print(f'  pool[{pi}]: len={len(pool)}, empty={empty}, samples={samples}')

    # Check pool[6] for "0.0"
    p6_has_00 = any(x == '0.0' for x in ipdb._pools[6])
    p6_has_countrycode = any(x in ('CN','US','GB','JP','KR','RU') for x in ipdb._pools[6])
    print(f'  pool[6] has "0.0"? {p6_has_00} (bug indicator: gParts[8] leaked into pool[6])')
    print(f'  pool[6] has country_code? {p6_has_countrycode} (another gParts[8] leak indicator)')

    # Sample some lookups
    if 'std' in f:
        import random
        rng = random.Random(42)
        samples = []
        for _ in range(3):
            ip_int = rng.randint(0, 0xFFFFFFFF)
            info = ipdb.find_uint(ip_int)
            if info and info.country:
                samples.append(f'{info.code}')
        print(f'  sample pool[6] ("code") values: {samples}')
    print()
