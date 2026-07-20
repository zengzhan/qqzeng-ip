import os
import sys
import time
import random

sys.path.insert(0, os.path.dirname(__file__))
from qzdb import QzdbSearcher

DATADIR = os.path.join(os.path.dirname(__file__), '..', 'data')

def bench(name, db_file, v4_count=3_000_000, v6_count=1_000_000):
    db_path = os.path.join(DATADIR, db_file)
    if not os.path.exists(db_path):
        print(f"  {name}: DB not found, skip")
        return

    s = QzdbSearcher(db_path)

    # V4 benchmark
    rng = random.Random(123)
    ips = [rng.randint(0, 0xFFFFFFFF) for _ in range(v4_count)]
    start = time.perf_counter()
    for ip in ips:
        s.find_uint(ip)
    v4_elapsed = time.perf_counter() - start
    v4_qps = v4_count / v4_elapsed

    # V6 benchmark
    v6_rng = random.Random(456)
    v6_high_low = []
    for _ in range(v6_count):
        hi = ((v6_rng.randint(0, 0xFFFFFFFF)) << 32) | (v6_rng.randint(0, 0xFFFFFFFF))
        lo = ((v6_rng.randint(0, 0xFFFFFFFF)) << 32) | (v6_rng.randint(0, 0xFFFFFFFF))
        v6_high_low.append((hi, lo))
    start = time.perf_counter()
    for hi, lo in v6_high_low:
        s.find_v6_uint((hi << 64) | lo)
    v6_elapsed = time.perf_counter() - start
    v6_qps = v6_count / v6_elapsed
    print(f"  {name:20s}  V4 QPS: {v4_qps:>10.0f}  V6 QPS: {v6_qps:>10.0f}")

def main():
    print("Python QPS Benchmarks (M4 Pro)")
    print(f"{'DB':20s}  {'V4 QPS':>10s}  {'V6 QPS':>10s}")
    print("-" * 50)
    bench("std_china", "qqzeng_ip_std_china.qzdb")
    bench("max_china", "qqzeng_ip_max_china.qzdb")
    bench("max_global", "qqzeng_ip_max_global.qzdb")

if __name__ == '__main__':
    main()
