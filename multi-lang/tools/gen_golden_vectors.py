#!/usr/bin/env python3
"""
Generate golden_vectors.json for cross-language regression testing.

Uses Python reference implementation to generate deterministic test vectors:
- seed=42 for reproducible random IPs
- ≥1000 random IPs per database
- All boundary/edge cases included

Output: golden_vectors.json
  { db_name: { "random_v4": [...], "random_v6": [...], "boundary_v4": [...], "boundary_v6": [...] } }
  Each entry: { "ip": "...", "expected": "pipe_string_or_empty" }
"""
import json, os, random, sys

SRC_DIR = os.path.join(os.path.dirname(__file__), '..')
sys.path.insert(0, os.path.join(SRC_DIR, 'python'))
from qzdb import QzdbSearcher

DATA_DIR = os.path.join(SRC_DIR, 'data')
OUT_FILE = os.path.join(os.path.dirname(__file__), 'golden_vectors.json')

SEED = 42
RANDOM_COUNT = 1000  # per IP version per database

# Boundary IPs from edge_test.py
BOUNDARY_V4 = [
    ("V4-normal", "114.114.114.114"),
    ("V4-normal2", "223.5.5.5"),
    ("V4-google", "8.8.8.8"),
    ("V4-cloudflare", "1.1.1.1"),
    ("V4-zero", "0.0.0.0"),
    ("V4-max", "255.255.255.255"),
    ("V4-private-a", "10.0.0.1"),
    ("V4-private-b", "172.16.0.1"),
    ("V4-private-c", "192.168.1.1"),
    ("V4-loopback", "127.0.0.1"),
    ("V4-broadcast", "255.255.255.255"),
    ("V4-class-a-start", "1.0.0.0"),
    ("V4-class-a-end", "126.255.255.255"),
    ("V4-class-b-start", "128.0.0.0"),
    ("V4-class-b-end", "191.255.255.255"),
    ("V4-class-c-start", "192.0.0.0"),
    ("V4-class-c-end", "223.255.255.255"),
    ("V4-each-octet-max", "255.255.255.255"),
    ("V4-single-1", "1.1.1.1"),
    ("V4-single-254", "254.254.254.254"),
]

BOUNDARY_V6 = [
    ("V6-loopback", "::1"),
    ("V6-unspecified", "::"),
    ("V6-google", "2001:4860:4860::8888"),
    ("V6-china-unicom", "2408:8000:9000::1"),
    ("V6-cloudflare", "2606:4700:4700::1111"),
    ("V6-linklocal", "fe80::1"),
    ("V6-multicast", "ff02::1"),
    ("V6-6to4", "2002::1"),
    ("V6-ula", "fd00::1"),
    ("V6-doc", "2001:db8::1"),
    ("V6-mixed-case", "2001:DB8::1"),
    ("V6-mapped-v4", "::ffff:114.114.114.114"),
    ("V6-mapped-google", "::ffff:8.8.8.8"),
    ("V6-full-zero", "0000:0000:0000:0000:0000:0000:0000:0000"),
    ("V6-full-one", "0000:0000:0000:0000:0000:0000:0000:0001"),
    ("V6-max", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"),
]

# Invalid IPs (expected: empty string in output)
INVALID_IPS = [
    "", "1.2.3", "1.2.3.4.5", "114..114.114.114",
    ".1.2.3.4", "1.2.3.4.", "256.1.2.3", "-1.2.3.4",
    "a.b.c.d", "1.2.3.4:80", "fe80::1%eth0", "1::2::3",
    "g::1", "[::1]", "2001:gggg::1",
]


def gen_random_ips(rng, count, version):
    """Generate random IPs."""
    ips = []
    for _ in range(count):
        if version == 4:
            ip = f"{rng.randint(0,255)}.{rng.randint(0,255)}.{rng.randint(0,255)}.{rng.randint(0,255)}"
        else:
            parts = [f"{rng.randint(0, 0xFFFF):04x}" for _ in range(8)]
            ip = ":".join(parts)
        ips.append(ip)
    return ips


def lookup(searcher, ip):
    """Lookup IP and return pipe string or empty. Handles parse errors gracefully."""
    try:
        r = searcher.find(ip)
        if r is None:
            return ""
        return r.to_pipe()
    except (KeyError, ValueError, IndexError):
        return ""


def generate_for_db(db_name, db_path):
    """Generate golden vectors for one database."""
    if not os.path.exists(db_path):
        print(f"  SKIP {db_name}: {db_path} not found")
        return None

    searcher = QzdbSearcher(db_path)
    rng_v4 = random.Random(SEED)
    rng_v6 = random.Random(SEED + 1)

    result = {
        "db": db_name,
        "seed": SEED,
        "random_v4": [],
        "random_v6": [],
        "boundary_v4": [],
        "boundary_v6": [],
        "invalid": [],
    }

    # Random V4
    for ip in gen_random_ips(rng_v4, RANDOM_COUNT, 4):
        result["random_v4"].append({"ip": ip, "expected": lookup(searcher, ip)})

    # Random V6
    for ip in gen_random_ips(rng_v6, RANDOM_COUNT, 6):
        result["random_v6"].append({"ip": ip, "expected": lookup(searcher, ip)})

    # Boundary V4
    for label, ip in BOUNDARY_V4:
        result["boundary_v4"].append({"ip": ip, "label": label, "expected": lookup(searcher, ip)})

    # Boundary V6
    for label, ip in BOUNDARY_V6:
        result["boundary_v6"].append({"ip": ip, "label": label, "expected": lookup(searcher, ip)})

    # Invalid IPs (all should return empty)
    for ip in INVALID_IPS:
        result["invalid"].append({"ip": ip, "expected": ""})

    return result


def main():
    databases = [
        ("std_china", "qqzeng_ip_std_china.qzdb"),
        ("max_china", "qqzeng_ip_max_china.qzdb"),
        ("std_global", "qqzeng_ip_std_global.qzdb"),
        ("max_global", "qqzeng_ip_max_global.qzdb"),
        ("ult_china", "qqzeng_ip_ult_china.qzdb"),
        ("ult_global", "qqzeng_ip_ult_global.qzdb"),
    ]

    all_vectors = {}
    for db_name, fname in databases:
        db_path = os.path.join(DATA_DIR, fname)
        print(f"Generating for {db_name}...")
        vectors = generate_for_db(db_name, db_path)
        if vectors:
            all_vectors[db_name] = vectors
            total = (len(vectors["random_v4"]) + len(vectors["random_v6"]) +
                     len(vectors["boundary_v4"]) + len(vectors["boundary_v6"]) +
                     len(vectors["invalid"]))
            print(f"  {total} vectors generated")

    with open(OUT_FILE, 'w', encoding='utf-8') as f:
        json.dump(all_vectors, f, ensure_ascii=False, indent=2)

    print(f"\nWrote {OUT_FILE}")
    print(f"Databases: {len(all_vectors)}")


if __name__ == '__main__':
    main()
