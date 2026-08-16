#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
QZDB reference-compliant benchmark (Python) — implements docs/BENCH_CONTRACT.md.

This is the TEMPLATE the other 5 non-compliant languages (C/Go/Rust/Node/PHP)
replicate. It:
  1. imports gen_stream()/enc_query() from tools/bench_gen.py (single source
     of truth) and SELF-CHECKS parity: the first-1024 SHA of every stream it
     generates must equal bench_vectors.json's fingerprint, else it aborts.
  2. runs 4 distributions x 3 dual-stack modes with QPS / avg / P50 / P95 / P99,
     cold vs hot, and 1/8/16-thread scaling (Python is GIL-bound, so thread
     QPS is reported best-effort + a concurrency-safety assertion).
  3. adds a `hot.mixed` string round-trip to isolate parse vs decode cost.
  4. writes the canonical JSON to multi-lang/bench_reports/python_<edition>.json.

Env overrides (for quick local runs):
    BENCH_OPS=200000          scale OPS down
    BENCH_EDITIONS=std_china  comma list of editions to run
"""

import hashlib
import json
import os
import platform
import statistics
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
TOOLS = os.path.join(HERE, "..", "tools")
sys.path.insert(0, TOOLS)
REPORTS = os.path.join(HERE, "..", "bench_reports")

import bench_gen as bg
from qzdb import QzdbReader  # reference SDK under test

LAT_SAMPLE_EVERY = 20
THREAD_CONFIGS = [1, 8, 16]
CONCURRENCY_THREADS = 16
CONCURRENCY_OPS = 100_000

EDITIONS = {
    "std_china": ("std", "china", "qqzeng_ip_std_china.qzdb"),
    "max_global": ("max", "global", "qqzeng_ip_max_global.qzdb"),
}

DB_ROOT_CANDIDATES = [
    os.path.join(HERE, "..", "test_data_202608"),
    os.path.join(HERE, "test_data_202608"),
    os.path.join(HERE, "..", "..", "test_data_202608"),
    os.path.join(HERE, "data"),
]


def find_db(edition: str) -> str:
    reg, region, fn = EDITIONS[edition]
    for root in DB_ROOT_CANDIDATES:
        p = os.path.join(root, reg, region, fn)
        if os.path.exists(p):
            return p
    return None


def fmt_v4(ip: int) -> str:
    return f"{(ip >> 24) & 255}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}"


def fmt_v6(h: int, l: int) -> str:
    v = (h << 64) | l
    return ":".join(f"{(v >> (112 - 16 * k)) & 0xFFFF:x}" for k in range(8))


def dispatch(reader, q):
    """Route each query kind to the entry point a real caller would use.

    'm' (IPv4-mapped, ::ffff:w.x.y.z) MUST go through the mapped-aware bytes
    API: find_v6_uint() is a pure v6 trie walk that never downgrades, so it
    reports a miss for every mapped query and the bench ends up timing the
    early-exit miss path instead of a real lookup. Keeping the three kinds on
    three distinct entry points is what makes the numbers comparable across
    languages -- see docs/BENCH_CONTRACT.md.
    """
    if q[0] == "v4":
        return reader.find_uint(q[1])
    if q[0] == "m":
        return reader.find_bytes(((q[1] << 64) | q[2]).to_bytes(16, "big"))
    return reader.find_v6_uint((q[1] << 64) | q[2])


def percentile(ns_list, p):
    if not ns_list:
        return 0
    ns_list.sort()
    idx = min(len(ns_list) - 1, max(0, int(__import__("math").ceil(p * len(ns_list))) - 1))
    return ns_list[idx]


def run_single(reader, dist, mode, seed, pool_v4, pool_v6, ops, sample=True):
    lat = []
    hits = 0
    start = time.perf_counter()
    for i, q in enumerate(bg.gen_stream(dist, mode, seed, pool_v4, pool_v6)):
        if sample and i % LAT_SAMPLE_EVERY == 0:
            t0 = time.perf_counter()
            found = dispatch(reader, q)
            lat.append(int((time.perf_counter() - t0) * 1e9))
        else:
            found = dispatch(reader, q)
        if found is not None:
            hits += 1
        if i + 1 >= ops:
            break
    elapsed = time.perf_counter() - start
    qps = ops / elapsed
    return {
        "ops": ops,
        "qps": round(qps, 1),
        "avg_ns": round(elapsed * 1e9 / ops, 1),
        "p50_ns": int(percentile(lat, 0.50)) if lat else 0,
        "p95_ns": int(percentile(lat, 0.95)) if lat else 0,
        "p99_ns": int(percentile(lat, 0.99)) if lat else 0,
        "errors": 0,
        # hit_rate is mandatory: a QPS number without it is uninterpretable,
        # because a fast run may simply be one where every query missed and
        # took the early-exit path.
        "hits": hits,
        "hit_rate": hits / ops,
    }


def run_multi(reader, dist, mode, seed, pool_v4, pool_v6, threads, ops):
    per = ops // threads
    errs = [0]
    done = [0]
    latch = threading.Barrier(threads)
    start = [0.0]

    def worker(base):
        try:
            latch.wait()
            if base == 0:
                start[0] = time.perf_counter()
            for i, q in enumerate(bg.gen_stream(dist, mode, seed, pool_v4, pool_v6)):
                dispatch(reader, q)
                done[0] += 1
                if i + 1 >= per:
                    break
        except Exception:
            errs[0] += 1

    ts = [threading.Thread(target=worker, args=(t * per,)) for t in range(threads)]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    elapsed = time.perf_counter() - start[0]
    return {
        "ops": done[0],
        "qps": round(done[0] / elapsed, 1),
        "avg_ns": round(elapsed * 1e9 / max(1, done[0]), 1),
        "p50_ns": 0, "p95_ns": 0, "p99_ns": 0,
        "errors": errs[0],
    }


def concurrency_safe(reader, dist, mode, seed, pool_v4, pool_v6):
    per = CONCURRENCY_OPS // CONCURRENCY_THREADS
    errs = [0]
    done = [0]

    def worker(base):
        try:
            for i, q in enumerate(bg.gen_stream(dist, mode, seed, pool_v4, pool_v6)):
                dispatch(reader, q)
                done[0] += 1
                if i + 1 >= per:
                    break
        except Exception:
            errs[0] += 1

    ts = [threading.Thread(target=worker, args=(t * per,)) for t in range(CONCURRENCY_THREADS)]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    return errs[0] == 0 and done[0] == CONCURRENCY_THREADS * per


def parity_selfcheck(manifest):
    print("parity self-check ...", end=" ", flush=True)
    pool_v4, pool_v6 = bg.build_pools()
    bad = 0
    for dist, modes in manifest["streams"].items():
        for mode, info in modes.items():
            h = hashlib.sha256()
            for i, q in enumerate(bg.gen_stream(dist, mode, info["seed"], pool_v4, pool_v6)):
                if i >= bg.FINGERPRINT_N:
                    break
                h.update(bg.enc_query(q))
            if h.hexdigest() != info["first1024_sha256"]:
                bad += 1
                print(f"\n  MISMATCH {dist}.{mode}!")
    if bad:
        print("FAILED")
        sys.exit(1)
    print("OK (12/12 streams match bench_vectors.json)")


def main():
    ops = int(os.environ.get("BENCH_OPS", bg.OPS))
    editions = os.environ.get("BENCH_EDITIONS", "std_china,max_global").split(",")
    manifest = json.load(open(os.path.join(TOOLS, "bench_vectors.json"), encoding="utf-8"))
    parity_selfcheck(manifest)
    pool_v4, pool_v6 = bg.build_pools()

    os.makedirs(REPORTS, exist_ok=True)

    for edition in editions:
        db = find_db(edition)
        if not db:
            print(f"[SKIP] {edition}: db not found")
            continue
        reader = QzdbReader(db)
        print(f"\nedition {edition}: {db} ({os.path.getsize(db):,} bytes)")

        # concurrency safety on hot.mixed (most representative)
        safe = concurrency_safe(reader, "hot", "mixed",
                                manifest["streams"]["hot"]["mixed"]["seed"], pool_v4, pool_v6)
        print(f"  concurrency_safe(16x{CONCURRENCY_OPS//1000}k): {safe}")

        dist_out = {}
        for dist, modes in manifest["streams"].items():
            dist_out[dist] = {}
            for mode, info in modes.items():
                seed = info["seed"]

                # cold: first COLD_OPS right after load (cache cold)
                cold = run_single(reader, dist, mode, seed, pool_v4, pool_v6,
                                  min(ops, bg.COLD_OPS), sample=True)
                cold["warm"] = "cold"

                # warmup
                run_single(reader, dist, mode, seed, pool_v4, pool_v6,
                           min(ops, bg.WARMUP_OPS), sample=False)

                # hot: single-thread full ops
                hot = run_single(reader, dist, mode, seed, pool_v4, pool_v6, ops, sample=True)
                hot["warm"] = "hot"

                threads = {}
                for tc in THREAD_CONFIGS:
                    r = run_multi(reader, dist, mode, seed, pool_v4, pool_v6, tc, ops)
                    r["warm"] = "hot"
                    threads[str(tc)] = r

                dist_out[dist][mode] = {"cold": cold, "hot": hot, "threads": threads}

                print(f"  {dist:11s}.{mode:6s}  hot QPS={hot['qps']:>12,.0f}  "
                      f"p50={hot['p50_ns']:>6d}ns  p99={hot['p99_ns']:>7d}ns  "
                      f"8T={threads['8']['qps']:>12,.0f}  err={hot['errors']}  "
                      f"hit={hot['hit_rate'] * 100:.1f}%")

        # string round-trip on hot.mixed
        seed = manifest["streams"]["hot"]["mixed"]["seed"]
        lat = []
        start = time.perf_counter()
        n = 0
        for i, q in enumerate(bg.gen_stream("hot", "mixed", seed, pool_v4, pool_v6)):
            s = fmt_v4(q[1]) if q[0] == "v4" else fmt_v6(q[1], q[2])
            if i % LAT_SAMPLE_EVERY == 0:
                t0 = time.perf_counter()
                reader.find(s)
                lat.append(int((time.perf_counter() - t0) * 1e9))
            else:
                reader.find(s)
            n += 1
            if n >= ops:
                break
        elapsed = time.perf_counter() - start
        string_rt = {
            "api": "string", "ops": n, "qps": round(n / elapsed, 1),
            "avg_ns": round(elapsed * 1e9 / n, 1),
            "p50_ns": int(percentile(lat, 0.50)) if lat else 0,
            "p95_ns": int(percentile(lat, 0.95)) if lat else 0,
            "p99_ns": int(percentile(lat, 0.99)) if lat else 0, "errors": 0, "warm": "hot",
        }
        print(f"  {'hot':11s}.{'mixed':6s}  STRING round-trip QPS={string_rt['qps']:>12,.0f}  "
              f"p99={string_rt['p99_ns']:>7d}ns")

        report = {
            "contract": "QZDB_BENCH_CONTRACT v1.0",
            "language": "python",
            "sdk_version": "multi-lang/python (reference)",
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
            "seed": bg.MASTER_SEED,
            "db": {"path": db, "edition": edition,
                   "bytes": os.path.getsize(db),
                   "hash": f"crc32:n/a"},  # Python SDK exposes getFileHash on reader if available
            "environment": {
                "cpu": platform.processor() or platform.machine(),
                "cores": os.cpu_count(),
                "ram_gb": None,
                "os": f"{platform.system()} {platform.release()} {platform.machine()}",
                "runtime": f"Python {platform.python_version()}",
                "compiler": "CPython",
                "bench_contract": "v1.0",
                "note": "Python is GIL-bound; multi-thread QPS is best-effort, "
                        "concurrency_safety is the meaningful thread assertion.",
            },
            "distributions": dist_out,
            "string_roundtrip": {"hot": {"mixed": string_rt}},
            "concurrency_safe": safe,
        }
        out_path = os.path.join(REPORTS, f"python_{edition}.json")
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(report, f, indent=2, ensure_ascii=False)
        print(f"  wrote {out_path}")


if __name__ == "__main__":
    main()
