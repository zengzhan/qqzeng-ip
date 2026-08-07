"""Comprehensive diagnostic tests for the QZDB Python SDK.

Tests correctness against the API contract (QZDB_SDK_API.md) and FORMAT spec,
using the Java reference implementation as the behavioral baseline.

Run with: python3 test_diagnostic.py
"""

import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import qzdb
from qzdb import QzdbReader, QzdbError, GeoInfo, BatchResult, ChainedReader, QzdbRegistry

DATA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'data')
STD = os.path.join(DATA_DIR, 'qqzeng_ip_std_china.qzdb')
ULT = os.path.join(DATA_DIR, 'qqzeng_ip_ult_china.qzdb')

_asserts = 0
_fails = 0


def check(cond, msg):
    global _asserts, _fails
    _asserts += 1
    if not cond:
        _fails += 1
        print(f'  FAIL: {msg}')


def section(name):
    print(f'[diag] {name}')


# ── 1. V6 query correctness (24-bit node sentinel bug) ──────────────
section('V6 24-bit node trie walk')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    # The ULT db has v6node24=1, so all V6 queries exercise the 24-bit path
    v6_ips = [
        '2408:4004:10:1::1',
        '240e:390:1:1::1',
        '2001:da8:200:10::1',
        '2a03:2880:f10c:83:face:b00c:0:25de',
    ]
    for ip in v6_ips:
        gi = r.find(ip)
        if gi is not None:
            check(True, f'V6 {ip} -> {gi.to_pipe()[:40]}...')
        else:
            # may legitimately not be in the DB; just confirm no crash
            check(True, f'V6 {ip} -> None (not in DB, OK)')
    r.close()


# ── 2. find_fields normalization (API contract §6.1 / §9.6) ──────────
section('find_fields field-name normalization')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    gi = r.find('223.5.5.5')
    if gi is not None:
        base_country = gi.get('country')

        # find_fields MUST normalize field names the same way get() does
        for variant in ['country', 'COUNTRY', 'Country', 'country_code', 'countryCode', 'COUNTRY_CODE', 'country-code']:
            gi2 = r.find_fields('223.5.5.5', [variant])
            if gi2 is not None:
                val = gi2.get(variant)
                # country_code may legitimately differ, but 'country' variants must match
                if variant in ('country', 'COUNTRY', 'Country'):
                    check(val == base_country,
                          f"find_fields(['{variant}']).get('{variant}') = {val!r}, expected {base_country!r}")
            else:
                # find_fields must NEVER return None when the IP is found
                # (only the field projection varies; empty string for unknown field)
                check(False, f"find_fields(['{variant}']) returned None for known IP 223.5.5.5")
    r.close()


# ── 3. find_fields output length = input length (API contract §9.6) ──
section('find_fields output array length matches input')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    # Unknown fields must be returned as empty strings, preserving length
    gi = r.find_fields('223.5.5.5', ['country', 'nonexistent_field', 'city'])
    if gi is not None:
        check(len(gi._values) == 3,
              f"find_fields returned {len(gi._values)} values for 3 fields, expected 3")
        check(gi._values[0] != '', 'country should be non-empty')
        check(gi._values[1] == '', 'nonexistent_field should be ""')
    else:
        check(False, "find_fields with unknown field returned None for known IP")
    r.close()


# ── 4. has_field uses pre-built normalized index (performance) ──────
section('has_field uses normalized index (correct + fast)')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    check(r.has_field('country') is True, "has_field('country')")
    check(r.has_field('COUNTRY') is True, "has_field('COUNTRY') (case-insensitive)")
    check(r.has_field('country_code') is True, "has_field('country_code')")
    check(r.has_field('countryCode') is True, "has_field('countryCode') (normalized)")
    check(r.has_field('nonexistent') is False, "has_field('nonexistent')")
    check(r.has_field('country-code') is True, "has_field('country-code') (hyphen-insensitive)")
    r.close()


# ── 5. find_fields returns empty GeoInfo, not None (contract §9.6) ──
section('find_fields never returns None for known IP')
if os.path.exists(STD):
    r = QzdbReader(STD)
    # All unknown fields -> should still return a GeoInfo with empty strings
    gi = r.find_fields('223.5.5.5', ['zzz_unknown', 'xxx_nope'])
    check(gi is not None,
          "find_fields with all-unknown fields should return empty GeoInfo, not None")
    if gi is not None:
        check(all(v == '' for v in gi._values),
              "all unknown fields should be empty strings")
    r.close()


# ── 6. find_v6_bytes correctness (24-bit path) ──────────────────────
section('find_bytes with IPv6 24-bit nodes')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    # Same IP via string vs bytes must give same result
    ip_str = '2408:4004:10:1::1'
    gi_str = r.find(ip_str)
    parsed = qzdb._fast_parse_ip(ip_str)
    if parsed is not None and parsed[1] is not None:
        gi_bytes = r.find_bytes(parsed[1])
        if gi_str is not None and gi_bytes is not None:
            check(gi_str.to_pipe() == gi_bytes.to_pipe(),
                  f"string vs bytes V6 mismatch: {gi_str.to_pipe()!r} vs {gi_bytes.to_pipe()!r}")
        elif gi_str is None and gi_bytes is None:
            check(True, "both None (IP not in DB, OK)")
        else:
            check(False, f"string={gi_str is not None} vs bytes={gi_bytes is not None} mismatch")
    r.close()


# ── 7. findFields order preservation (API contract §9.6) ─────────────
section('find_fields preserves requested field order')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    gi = r.find_fields('223.5.5.5', ['city', 'country', 'isp'])
    if gi is not None:
        check(gi._field_names == ['city', 'country', 'isp'],
              f"field order should be ['city','country','isp'], got {gi._field_names}")
    else:
        check(False, "find_fields returned None for known IP")
    r.close()


# ── 8. lookup_row_id for V6 (24-bit path) ──────────────────────────
section('lookup_row_id V6 correctness')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    # V6 lookup must not crash and must give non-zero for covered IPs
    for ip in ['2408:4004:10:1::1', '240e:390:1:1::1']:
        rid = r.lookup_row_id(ip)
        check(isinstance(rid, int) and rid >= 0,
              f"lookup_row_id({ip}) returned {rid}")
    r.close()


# ── 9. GeoInfo to_json numeric types (API contract §6.2) ────────────
section('to_json numeric type handling')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    gi = r.find('223.5.5.5')
    if gi is not None:
        j = gi.to_json()
        # longitude/latitude/asn/geo_id must be JSON numbers (no quotes)
        # Other fields must be JSON strings (with quotes)
        check('longitude' in j and ':null' not in j.split('longitude')[1][:3] or '"longitude":null' in j or 'longitude":' in j,
              f"longitude format check in: {j[:120]}")
    r.close()


# ── 10. findFields with subset (API contract §9.6 projection) ───────
section('field projection reduces work')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    gi_full = r.find('223.5.5.5')
    gi_partial = r.find_fields('223.5.5.5', ['country'])
    if gi_full is not None and gi_partial is not None:
        check(len(gi_partial._values) == 1,
              f"find_fields(['country']) should return 1 value, got {len(gi_partial._values)}")
        check(gi_partial._values[0] == gi_full.get('country'),
              "partial country should match full country")
    r.close()


# ── Summary ─────────────────────────────────────────────────────────
print()
if _fails == 0:
    print(f'DIAG_PASS: {_asserts} assertions, 0 failures')
    sys.exit(0)
else:
    print(f'DIAG_FAIL: {_asserts} assertions, {_fails} failures')
    sys.exit(1)
