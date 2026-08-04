#!/usr/bin/env python3
"""
gen_synthetic_db.py — Generate a minimal but fully valid QZDB file.

Purpose
-------
The purchased .qzdb files are gitignored (see .gitignore: "Purchased IP
database files (must be obtained separately)"), so a fresh CI checkout has
no data and run_all_tests.sh would fail before even testing the SDKs.

This script writes a tiny QZDB file whose layout mirrors the real
qqzeng_ip_asn_china.qzdb structure (verified loadable by all 8 SDKs),
but with a handful of synthetic IP ranges. It lets CI exercise the full
load path (header parse, ROW_SCHEMA, GROUP_SCHEMA, GroupMetadataTable,
string pools, metadata, CRC32) without purchased data.

Usage
-----
    python3 tools/gen_synthetic_db.py [output_path]

    Default output: multi-lang/data/qqzeng_ip_std_china.qzdb
    (the exact name every language test script's find_db() looks for)

The output file is deterministic (no randomness), so CI results are stable.
"""

import struct
import sys
import zlib
import os

# ---------------------------------------------------------------------------
# Synthetic dataset
# ---------------------------------------------------------------------------
# Fields mirror the real ASN edition (8 dims). Pool index 0 is always "".
FIELDS = ["continent", "country_code", "country", "isp",
          "asn", "as_name", "as_domain", "usage_type"]

# One pool per field. Index 0 is the reserved empty string.
POOLS = [
    ["", "亚洲"],                                  # continent
    ["", "CN"],                                    # country_code
    ["", "中国"],                                  # country
    ["", "中国电信"],                              # isp
    ["", "137702", "4134", "15169"],               # asn
    ["", "CHINANET", "Chinanet", "Google"],        # as_name
    ["", "chinatelecom.cn", "google.com"],         # as_domain
    ["", "isp"],                                   # usage_type
]

# IPRange -> (asn_pool_index)  (asn dimension; see dimMask below)
# The high 16 bits of each IP become a jump-table leaf pointing at a row_id.
V4_RANGES = [
    ("114.114.0.0", "114.114.255.255", 1),  # -> "137702" (CHINANET)
    ("223.5.0.0", "223.5.255.255", 2),      # -> "4134"
    ("8.8.0.0", "8.8.255.255", 3),          # -> "15169" (Google)
]

# V6: single /128-ish entry used by the smoke test IP 2408:8000:9000::1
V6_RANGES = [
    ("2408:8000:9000::", 4),
]

# GeoEntry rows (asn dimension). entry[0] is reserved (all zero).
# Each row: 8 fields x poolIdxSize(2) = 16 bytes.
GEO_ENTRIES = [
    [0, 0, 0, 0, 0, 0, 0, 0],      # entry 0: reserved
    [1, 1, 1, 1, 1, 1, 1, 1],      # entry 1: continent=亚洲 code=CN ... asn=137702
    [1, 1, 1, 1, 2, 2, 2, 1],      # entry 2: asn=4134
    [1, 1, 1, 1, 3, 3, 3, 1],      # entry 3: asn=15169
]

# IPRow layout mirrors the real ASN file: ROW_SCHEMA geo(2B) + asn(2B).
# row[0] is the reserved empty row (all zero).
# row[0] = (0,0), row[1] = (0,1) etc: asn dimension drives GeoEntry lookup.
IP_ROWS = [
    (0, 0),  # row 0: reserved
    (0, 1),  # row 1 -> asn entry 1
    (0, 2),  # row 2 -> asn entry 2
    (0, 3),  # row 3 -> asn entry 3
    (0, 1),  # row 4 -> asn entry 1 (V6 range)
]

META_VERSION_LIST = "asn"
META_FIELD_NAMES = "|".join(FIELDS)
META_DESCRIPTION = "synthetic test database (gen_synthetic_db.py)"
META_PRIMARY = "asn"

VERSION_MASK = 4          # bit2 = asn edition (matches real file)
FLAGS = 0x37              # V4 | V6 | meta | v4_node_24 | v6_node_24 (matches real file)
V4_JUMP_BITS = 16
V6_JUMP_BITS = 16
POOL_IDX_SIZE = 2
HEADER_SIZE = 192
IP_ROW_SIZE = 4           # geo(2) + asn(2); matches real ASN file ROW_SCHEMA
GEO_ENTRY_GROUP_COUNT = 1

# ROW_SCHEMA byte layout (canonical, matches real file + all 8 SDKs):
#   byte[0] = fieldCount(2), byte[1] = stride(4),
#   bytes[2..3] = reserved, then fieldCount x 4B {fid, width, offset, flags}
ROW_SCHEMA = bytes([
    2, 4, 0, 0,          # fieldCount=2, stride=4
    0, 2, 0, 0,          # fid=0 (geo), width=2, offset=0, flags=0
    1, 2, 2, 0,          # fid=1 (asn), width=2, offset=2, flags=0
])

# GROUP_SCHEMA byte layout (matches real file):
#   ushort groupSchemaCount, then per group:
#     ushort groupId, ushort fieldCount, uint32 entryCount,
#     uint32 stride, uint32 flags, then fieldCount x {ushort fid, byte width,
#     byte fieldFlags, uint32 offset, uint32 poolSectionId}
# Uniform slot width = PoolIdxSize: mixed-width layouts (as in the real file)
# can be mis-read by readers that fall back to PoolIdxSize per field.
_ASN_WIDTHS = [2] * len(FIELDS)


def build_group_schema():
    out = struct.pack("<H", GEO_ENTRY_GROUP_COUNT)
    out += struct.pack("<HHII", 4, len(FIELDS), len(GEO_ENTRIES), sum(_ASN_WIDTHS))
    out += struct.pack("<I", 0)  # flags
    offset = 0
    for i, w in enumerate(_ASN_WIDTHS):
        out += struct.pack("<HBBII", i, w, 0, offset, 0)
        offset += w
    return out


def align64(n):
    return (n + 63) & ~63


def build_pool(field_idx):
    """One DimensionPool. Matches all 8 SDKs: when off_row_schema > 0, each
    pool carries an extra 4-byte string-total-length field right after count."""
    strings = POOLS[field_idx]
    data = "".join(strings).encode("utf-8")
    offsets = [0]
    for s in strings:
        offsets.append(offsets[-1] + len(s.encode("utf-8")))
    out = struct.pack("<I", len(strings))
    out += struct.pack("<I", len(data))          # extra field (ROW_SCHEMA present)
    out += b"".join(struct.pack("<I", o) for o in offsets)
    out += data
    return out


def ipv4_to_uint(ip):
    parts = [int(x) for x in ip.split(".")]
    return (parts[0] << 24) | (parts[1] << 16) | (parts[2] << 8) | parts[3]


def ipv6_to_uint128(ip):
    head, _, tail = ip.partition("::")
    hextets = []
    for part in (head, tail):
        if part:
            hextets += [int(x, 16) for x in part.split(":")]
    missing = 8 - len(hextets)
    full = []
    if head:
        full += [int(x, 16) for x in head.split(":")]
    full += [0] * missing
    if tail:
        full += [int(x, 16) for x in tail.split(":")]
    value = 0
    for h in full:
        value = (value << 16) | h
    return value


def build_header(offsets, row_count, v4_rec_count, v6_rec_count,
                 geo_count, v4_node_count=0, v6_node_count=0):
    h = bytearray(HEADER_SIZE)
    h[0:4] = b"QZDB"
    h[4] = 1                                # HeaderVersion (unified v1)
    struct.pack_into("<H", h, 6, VERSION_MASK)
    struct.pack_into("<H", h, 8, FLAGS)
    h[10] = V4_JUMP_BITS
    h[11] = V6_JUMP_BITS
    h[12] = len(FIELDS)                     # PoolCount
    h[13] = POOL_IDX_SIZE
    struct.pack_into("<H", h, 14, geo_count)
    # 16..19: CRC32 slot (filled last, computed with zeros here)
    struct.pack_into("<I", h, 20, row_count)
    struct.pack_into("<I", h, 24, v4_rec_count)
    struct.pack_into("<I", h, 28, v6_rec_count)
    struct.pack_into("<I", h, 32, 20260805)  # BuildDate yyyyMMdd
    struct.pack_into("<I", h, 36, HEADER_SIZE)
    struct.pack_into("<Q", h, 40, offsets["row_schema"])
    struct.pack_into("<Q", h, 48, offsets["group_schema"])
    struct.pack_into("<Q", h, 64, offsets["v4_jump"])
    struct.pack_into("<Q", h, 72, 0)        # offV4Nodes (none; jump has leaves)
    struct.pack_into("<Q", h, 80, offsets["v6_jump"])
    struct.pack_into("<Q", h, 88, 0)        # offV6Nodes
    struct.pack_into("<Q", h, 96, offsets["ip_row"])
    struct.pack_into("<Q", h, 104, offsets["geo_entries"])
    struct.pack_into("<Q", h, 136, offsets["pools"])
    struct.pack_into("<Q", h, 144, offsets["meta"])
    struct.pack_into("<I", h, 152, v4_node_count)
    struct.pack_into("<I", h, 156, v6_node_count)
    struct.pack_into("<I", h, 160, IP_ROW_SIZE)
    struct.pack_into("<I", h, 164, GEO_ENTRY_GROUP_COUNT)
    # GeoEntryOffsets[0] = 64 (after GroupMetadataTable, aligned)
    struct.pack_into("<Q", h, 168, 64)
    return h


def build_meta():
    entries = [
        (1, META_VERSION_LIST.encode("utf-8")),
        (2, META_FIELD_NAMES.encode("utf-8")),
        (3, META_DESCRIPTION.encode("utf-8")),
        (4, META_PRIMARY.encode("utf-8")),
    ]
    out = b""
    for t, val in entries:
        out += struct.pack("<BBH", t, 0, len(val))
        out += val
    return out


def generate(path):
    # ---- Section 1: ROW_SCHEMA right after header ----
    off_row_schema = HEADER_SIZE
    cursor = off_row_schema + len(ROW_SCHEMA)

    # ---- GROUP_SCHEMA (64-aligned) ----
    cursor = align64(cursor)
    off_group_schema = cursor
    gs = build_group_schema()
    cursor += len(gs)

    # ---- V4 Jump Table (64-aligned) ----
    cursor = align64(cursor)
    off_v4_jump = cursor
    v4_jump = bytearray(65536 * 4)
    for lo, hi, row_id in V4_RANGES:
        start = ipv4_to_uint(lo)
        hi16 = (start >> 16) & 0xFFFF
        # leaf value: row_id | SENTINEL(0x80000000)
        struct.pack_into("<I", v4_jump, hi16 * 4, row_id | 0x80000000)
    cursor += len(v4_jump)

    # ---- V6 Jump Table (64-aligned) ----
    cursor = align64(cursor)
    off_v6_jump = cursor
    v6_jump = bytearray((1 << V6_JUMP_BITS) * 4)
    for ip, row_id in V6_RANGES:
        value = ipv6_to_uint128(ip)
        hi = (value >> (128 - V6_JUMP_BITS)) & ((1 << V6_JUMP_BITS) - 1)
        struct.pack_into("<I", v6_jump, hi * 4, row_id | 0x80000000)
    cursor += len(v6_jump)

    # ---- IPRow Array (64-aligned) ----
    cursor = align64(cursor)
    off_ip_row = cursor
    ip_row = bytearray()
    for geo_id, asn_id in IP_ROWS:
        ip_row += struct.pack("<H", geo_id) + struct.pack("<H", asn_id)
    cursor += len(ip_row)

    # ---- GeoEntry Section (64-aligned) ----
    cursor = align64(cursor)
    off_geo_entries = cursor
    # GroupMetadataTable (matches real file + all SDKs):
    #   byte groupCount, per group: byte fieldCount, uint32 entryCount,
    #   uint16 dimensionMask
    meta_tbl = struct.pack("<B", GEO_ENTRY_GROUP_COUNT)
    meta_tbl += struct.pack("<BIH", len(FIELDS), len(GEO_ENTRIES), 0x2)  # dimMask=asn
    cursor += len(meta_tbl)
    # GeoEntry group data (at GeoEntryOffsets[0] = 64, i.e. aligned after table)
    cursor = off_geo_entries + 64
    geo_data = bytearray()
    for entry in GEO_ENTRIES:
        for idx in entry:
            geo_data += struct.pack("<H", idx)
    # pad group data to next section boundary (64-aligned)
    while len(geo_data) % 64 != 0:
        geo_data += b"\x00"
    cursor += len(geo_data)

    # ---- String Pools (64-aligned) ----
    cursor = align64(cursor)
    off_pools = cursor
    pools = b"".join(build_pool(i) for i in range(len(FIELDS)))
    cursor += len(pools)

    # ---- Metadata (64-aligned) ----
    cursor = align64(cursor)
    off_meta = cursor
    meta = build_meta()
    cursor += len(meta)

    offsets = {
        "row_schema": off_row_schema,
        "group_schema": off_group_schema,
        "v4_jump": off_v4_jump,
        "v6_jump": off_v6_jump,
        "ip_row": off_ip_row,
        "geo_entries": off_geo_entries,
        "pools": off_pools,
        "meta": off_meta,
    }

    header = build_header(offsets, row_count=len(IP_ROWS),
                          v4_rec_count=len(V4_RANGES),
                          v6_rec_count=len(V6_RANGES),
                          geo_count=len(GEO_ENTRIES))

    # Assemble in fixed section order: Header -> ROW_SCHEMA -> GROUP_SCHEMA ->
    # V4 Jump -> V6 Jump -> IPRow -> GeoEntry -> Pools -> Meta
    blob = bytearray()
    blob += header
    blob += b"\x00" * (off_row_schema - len(blob))
    blob += ROW_SCHEMA
    blob += b"\x00" * (off_group_schema - len(blob))
    blob += gs
    blob += b"\x00" * (off_v4_jump - len(blob))
    blob += v4_jump
    blob += b"\x00" * (off_v6_jump - len(blob))
    blob += v6_jump
    blob += b"\x00" * (off_ip_row - len(blob))
    blob += ip_row
    blob += b"\x00" * (off_geo_entries - len(blob))
    blob += meta_tbl
    blob += b"\x00" * (64 - len(meta_tbl))  # GeoEntryOffsets[0]=64
    blob += geo_data
    blob += b"\x00" * (off_pools - len(blob))
    blob += pools
    blob += b"\x00" * (off_meta - len(blob))
    blob += meta

    # ---- CRC32 (slot at 16..20 computed as zero) ----
    crc = zlib.crc32(bytes(blob)) & 0xFFFFFFFF
    struct.pack_into("<I", blob, 16, crc)

    with open(path, "wb") as f:
        f.write(bytes(blob))

    return len(blob)


def main():
    default = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "..", "data", "qqzeng_ip_std_china.qzdb")
    path = sys.argv[1] if len(sys.argv) > 1 else default
    path = os.path.abspath(path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    size = generate(path)
    print(f"Wrote synthetic QZDB: {path} ({size} bytes)")


if __name__ == "__main__":
    main()
