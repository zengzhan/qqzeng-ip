#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
QZDB Benchmark Reference Generator
==================================

Implements the deterministic sample generator mandated by
`docs/BENCH_CONTRACT.md` (v1.0), §3 / §4 — the reference instantiation of the
splitmix64 RNG and the four-distribution workload.

DESIGN
------
Rather than shipping a 24M-element shared file, this tool emits a compact
manifest (`bench_vectors.json`) carrying, for every (distribution x mode)
stream, the exact 64-bit seed + a SHA-256 fingerprint over the first 1024
queries (canonical encoding). Each language's bench imports `gen_stream()` /
`enc_query()` from THIS module, generates its OPS-long stream inline from the
manifest seed, and asserts its own first-1024 SHA equals the fingerprint.
Match => the full stream is byte-identical by construction. Single source of
truth, zero divergence risk.

Canonical query encoding (used ONLY for the fingerprint; must match per lang):
    v4        : u32 zero-extended to u64, little-endian, 8 bytes
    v6 / mapped: u128 as two u64 (high then low), little-endian, 16 bytes
    mapped v6 : ::ffff:w.x.y.z  =>  u128 = (0xFFFF << 32) | ipv4_u32

No third-party dependencies. Python 3.8+.

Usage:
    python3 bench_gen.py                 # write bench_vectors.json
    python3 bench_gen.py --out /tmp/x.json
"""

import hashlib
import json
import os
import struct
import sys

MASK64 = (1 << 64) - 1
MASTER_SEED = 20260807          # = 0x0134F107, matches BENCH_CONTRACT.md §3
OPS = 2_000_000                 # per (distribution, mode) — contract §4
WARMUP_OPS = 1_000_000
COLD_OPS = 200_000
POOL_HOT_V4 = 4096
POOL_HOT_V6 = 1024
FINGERPRINT_N = 1024

# IPv4-Mapped prefix: ::ffff:w.x.y.z  =>  u128 = (0xFFFF << 32) | ipv4_u32
MAPPED_PREFIX = 0xFFFF << 32    # 0x0000FFFF00000000


class SplitMix64:
    def __init__(self, seed: int):
        self.state = seed & MASK64

    def next(self) -> int:
        self.state = (self.state + 0x9E3779B97F4A7C15) & MASK64
        z = self.state
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK64
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK64
        return z ^ (z >> 31)

    def u32(self) -> int:
        return self.next() & 0xFFFFFFFF

    def u128(self) -> int:
        return ((self.next() & MASK64) << 64) | (self.next() & MASK64)


def enc_v4(ip: int) -> bytes:
    return struct.pack("<Q", ip & 0xFFFFFFFF)          # u32 zero-ext to u64, LE


def enc_v6(high: int, low: int) -> bytes:
    return struct.pack("<QQ", high & MASK64, low & MASK64)


def enc_query(q) -> bytes:
    """Canonical fingerprint encoding of a query yielded by gen_stream."""
    if q[0] == "v4":
        return enc_v4(q[1])
    return enc_v6(q[1], q[2])                            # 'v6' and 'm' both 16 bytes


def fnv1a(data: bytes, h: int = 0) -> int:
    """FNV-1a 64 — trivial to port, used as the cross-language parity guard
    inside every language's bench (no SHA256 needed in C/Go/Rust/etc)."""
    if h == 0:
        h = 0xCBF29CE484222325
    for b in data:
        h ^= b
        h = (h * 0x100000001B3) & MASK64
    return h


def build_pools():
    pv4 = SplitMix64(MASTER_SEED + 1)
    pool_v4 = [pv4.u32() for _ in range(POOL_HOT_V4)]
    pv6 = SplitMix64(MASTER_SEED + 2)
    pool_v6 = [pv6.u128() for _ in range(POOL_HOT_V6)]
    return pool_v4, pool_v6


def gen_v4(dist: str, rng: SplitMix64, pool_v4, base4: int, i: int) -> int:
    if dist == "random":
        return rng.u32()
    if dist == "hot":
        return pool_v4[rng.u32() % POOL_HOT_V4]
    if dist == "sequential":
        return (base4 + i) & 0xFFFFFFFF
    # real_world: 60% hot / 30% random / 10% sequential
    r = rng.u32() % 10
    if r < 6:
        return pool_v4[rng.u32() % POOL_HOT_V4]
    if r < 9:
        return rng.u32()
    return (base4 + i) & 0xFFFFFFFF


def gen_v6(dist: str, rng: SplitMix64, pool_v6, base6: int, i: int):
    """Returns (high, low) of a pure v6 query (mapped handled by caller)."""
    if dist == "random":
        v = rng.u128()
        return (v >> 64) & MASK64, v & MASK64
    if dist == "hot":
        v = pool_v6[rng.u32() % POOL_HOT_V6]
        return (v >> 64) & MASK64, v & MASK64
    if dist == "sequential":
        v = (base6 + i) & ((1 << 128) - 1)
        return (v >> 64) & MASK64, v & MASK64
    r = rng.u32() % 10
    if r < 6:
        v = pool_v6[rng.u32() % POOL_HOT_V6]
        return (v >> 64) & MASK64, v & MASK64
    if r < 9:
        v = rng.u128()
        return (v >> 64) & MASK64, v & MASK64
    v = (base6 + i) & ((1 << 128) - 1)
    return (v >> 64) & MASK64, v & MASK64


def mapped_from_v4(ip: int):
    v = (MAPPED_PREFIX | (ip & 0xFFFFFFFF)) & ((1 << 128) - 1)
    return (v >> 64) & MASK64, v & MASK64


def gen_stream(dist: str, mode: str, seed: int, pool_v4, pool_v6):
    """Yield queries for (dist, mode) from `seed`. Each query is one of:
        ('v4', ip_u32)
        ('v6', high_u64, low_u64)
        ('m',  high_u64, low_u64)   # IPv4-mapped, the '::ffff:' form
    Consumed by both the fingerprint and by every language's bench.
    """
    rng = SplitMix64(seed)
    base4 = rng.u32()                       # fixed per stream (sequential base)
    base6 = rng.u128()                      # fixed per stream
    for i in range(OPS):
        if mode == "v4":
            yield ("v4", gen_v4(dist, rng, pool_v4, base4, i))
        elif mode == "v6":
            if rng.u32() % 5 == 0:          # 80% pure / 20% mapped
                ip = gen_v4(dist, rng, pool_v4, base4, i)
                h, l = mapped_from_v4(ip)
                yield ("m", h, l)
            else:
                h, l = gen_v6(dist, rng, pool_v6, base6, i)
                yield ("v6", h, l)
        else:                               # mixed 50/40/10
            m = i % 10
            if m < 5:
                yield ("v4", gen_v4(dist, rng, pool_v4, base4, i))
            elif m < 9:
                h, l = gen_v6(dist, rng, pool_v6, base6, i)
                yield ("v6", h, l)
            else:
                ip = gen_v4(dist, rng, pool_v4, base4, i)
                h, l = mapped_from_v4(ip)
                yield ("m", h, l)


def stream_fingerprint(dist: str, mode: str, seed: int, pool_v4, pool_v6):
    h = hashlib.sha256()
    fnv = 0
    sample = []
    for i, q in enumerate(gen_stream(dist, mode, seed, pool_v4, pool_v6)):
        if i < FINGERPRINT_N:
            b = enc_query(q)
            h.update(b)
            fnv = fnv1a(b, fnv)
        if i < 8:
            sample.append(q)
    return {
        "seed": seed,
        "ops": OPS,
        "first1024_sha256": h.hexdigest(),
        "first1024_fnv1a": str(fnv),   # string: 64-bit, >2^53, JSON number loses precision
        "sample_first8": sample,
    }


def main():
    _default = os.path.join(os.path.dirname(os.path.abspath(__file__)), "bench_vectors.json")
    out = sys.argv[sys.argv.index("--out") + 1] if "--out" in sys.argv else _default

    pool_v4, pool_v6 = build_pools()

    def pool_fp(arr, width):
        h = hashlib.sha256()
        fnv = 0
        for x in arr[:FINGERPRINT_N]:
            b = struct.pack("<Q", x & MASK64) if width == 4 \
                else struct.pack("<QQ", (x >> 64) & MASK64, x & MASK64)
            h.update(b)
            fnv = fnv1a(b, fnv)
        return h.hexdigest(), fnv

    distributions = ["random", "hot", "sequential", "real_world"]
    modes = ["v4", "v6", "mixed"]
    out_obj = {
        "contract": "QZDB_BENCH_CONTRACT v1.0",
        "generator": "bench_gen.py",
        "master_seed": MASTER_SEED,
        "ops": OPS,
        "warmup_ops": WARMUP_OPS,
        "cold_ops": COLD_OPS,
        "pool_hot_v4": POOL_HOT_V4,
        "pool_hot_v6": POOL_HOT_V6,
        "encoding": {
            "v4": "u32 zero-extended to u64, little-endian, 8 bytes",
            "v6": "u128 as two u64 (high then low), little-endian, 16 bytes",
            "fingerprint": "sha256 over first 1024 query encodings, concatenated",
            "mapped_v6": "::ffff:w.x.y.z => u128 = (0xFFFF << 32) | ipv4_u32",
        },
        "pools": {
            "hot_v4_seed": MASTER_SEED + 1,
            "hot_v6_seed": MASTER_SEED + 2,
            "hot_v4_first1024_sha256": pool_fp(pool_v4, 4)[0],
            "hot_v4_first1024_fnv1a": str(pool_fp(pool_v4, 4)[1]),
            "hot_v6_first1024_sha256": pool_fp(pool_v6, 16)[0],
            "hot_v6_first1024_fnv1a": str(pool_fp(pool_v6, 16)[1]),
        },
        "streams": {},
    }

    order = 0
    for dist in distributions:
        out_obj["streams"][dist] = {}
        for mode in modes:
            seed = MASTER_SEED + 100 + order
            order += 1
            out_obj["streams"][dist][mode] = stream_fingerprint(dist, mode, seed, pool_v4, pool_v6)

    with open(out, "w", encoding="utf-8") as f:
        json.dump(out_obj, f, indent=2, ensure_ascii=False)

    # Emit a C header twin of the manifest. C has no decent JSON parser in the
    # SDK-free path, but the FNV-1a parity guard and the 12 stream seeds are all
    # it needs — so generate a tiny header from the same source of truth.
    h_out = os.path.join(os.path.dirname(os.path.abspath(out)), "bench_vectors.h")
    lines = []
    lines.append("/* Generated by bench_gen.py — DO NOT EDIT BY HAND. */")
    lines.append("/* Single source of truth: multi-lang/tools/bench_vectors.json */")
    lines.append("#ifndef QZDB_BENCH_VECTORS_H")
    lines.append("#define QZDB_BENCH_VECTORS_H")
    lines.append("#include <stdint.h>")
    lines.append("")
    lines.append(f"#define QZDB_BENCH_MASTER_SEED ((uint64_t){MASTER_SEED}ULL)")
    lines.append(f"#define QZDB_BENCH_OPS          ((uint64_t){OPS}ULL)")
    lines.append(f"#define QZDB_BENCH_POOL_V4     {POOL_HOT_V4}")
    lines.append(f"#define QZDB_BENCH_POOL_V6     {POOL_HOT_V6}")
    lines.append(f"#define QZDB_BENCH_FINGERPRINT {FINGERPRINT_N}")
    lines.append("")
    lines.append("typedef struct {")
    lines.append("    uint64_t seed;            /* per-stream splitmix64 seed */")
    lines.append("    uint64_t first1024_fnv1a; /* expected FNV-1a over first 1024 queries */")
    lines.append("} qzdb_bench_stream_t;")
    lines.append("")
    lines.append("/* indexed [dist][mode]: dist in {random,hot,sequential,real_world},")
    lines.append("   mode in {v4,v6,mixed} */")
    lines.append("static const qzdb_bench_stream_t qzdb_bench_streams[4][3] = {")
    for di, dist in enumerate(distributions):
        lines.append(f"  /* {dist} */")
        row = []
        for mi, mode in enumerate(modes):
            info = out_obj["streams"][dist][mode]
            seed = info["seed"]
            fnv = info["first1024_fnv1a"]
            row.append(f"{{ (uint64_t){seed}ULL, (uint64_t){fnv}ULL }}")
        lines.append("  { " + ", ".join(row) + " },")
    lines.append("};")
    lines.append("")
    lines.append("#endif /* QZDB_BENCH_VECTORS_H */")
    with open(h_out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    print(f"wrote {out}")
    print(f"wrote {h_out}")
    print(f"  master_seed = {MASTER_SEED}")
    print(f"  streams = {len(distributions) * len(modes)} (4 dist x 3 modes), ops/stream = {OPS:,}")


if __name__ == "__main__":
    main()
