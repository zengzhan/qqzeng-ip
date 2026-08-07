"""Tier 1 unit tests for the QZDB Python SDK (API contract §10).

No external database required for the pure-logic assertions; a few tests load
the bundled sample DBs under ``multi-lang/data`` when present. Run with:

    python3 test_tier1.py
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import qzdb
from qzdb import QzdbReader, QzdbError, QzdbRegistry, ChainedReader, BatchResult, UsageType

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
    print(f'[Tier1] {name}')


# ── 1. Strict IPv4/IPv6 parsing (SEC-05) ─────────────────────────────
section('strict IP parsing')
check(qzdb._fast_parse_ip('1.2.3.4') == (16909060, None), 'valid ipv4')
check(qzdb._fast_parse_ip('256.1.1.1') is None, 'octet >255 rejected')
check(qzdb._fast_parse_ip('1.2.3') is None, '3 segments rejected')
check(qzdb._fast_parse_ip('01.2.3.4') is None, 'leading zero rejected')
check(qzdb._fast_parse_ip('1.2.3.4.') is None, 'trailing dot rejected')
check(qzdb._fast_parse_ip('1.2.3.4.5') is None, '5 segments rejected')
check(qzdb._fast_parse_ip('1.2.3.4 ') is None, 'trailing space rejected (SSRF-safe)')
check(qzdb._fast_parse_ip('1.2.3.256') is None, 'overflow rejected')
check(qzdb._fast_parse_ip('::1') is not None, 'ipv6 loopback parsed')
check(qzdb._fast_parse_ip('2001:db8::1') is not None, 'ipv6 parsed')
check(qzdb._fast_parse_ip('1.2.3.4/24') is None, 'cidr form rejected')
check(qzdb._fast_parse_ip('fe80::1%eth0') is None, 'zone-id rejected')
check(qzdb._fast_parse_ip('') is None, 'empty rejected')
check(qzdb._fast_parse_ip('abc') is None, 'garbage rejected')

# ── 2. IPv4-Mapped IPv6 downgrade ──────────────────────────────────
section('mapped downgrade')
v4, v6 = qzdb._fast_parse_ip('::ffff:1.2.3.4')
check(v4 == 16909060, '::ffff:a.b.c.d downgrades to v4 int')
check(v6 is None, 'mapped form yields no v6 bytes')

# ── 3. Field name normalization (case/_/- insensitive) ──────────────
section('field normalization')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    gi = r.find('223.5.5.5')
    if gi is not None:
        check(gi.get('country') == gi.get('COUNTRY'), 'country == COUNTRY')
        check(gi.get('country_code') == gi.get('countryCode'), 'country_code == countryCode')
        check(gi.get('country-code') == gi.get('Country_Code'), 'hyphen-insensitive')
        check(gi.get('nonexistent_field') == '', 'missing field -> "" not KeyError')
        r.close()

# ── 4. UsageType 21 scenarios + unknown fallback ───────────────────
section('UsageType 21 + fallback')
known = ['AICrawler', 'Backbone', 'Broadband', 'Business', 'CDN', 'Cloud', 'DNS',
          'DataCenter', 'Education', 'Finance', 'Government', 'ISP', 'IXP', 'IoT',
          'Mobile', 'Reserved', 'Satellite', 'Spider', 'Streaming', 'Unknown', 'VPN']
check(len(UsageType._KNOWN) == 21, f'21 known scenarios ({len(UsageType._KNOWN)})')
for raw in known:
    ut = UsageType.from_string(raw)
    check(ut.is_known(), f'{raw} is known')
unknown = UsageType.from_string('TotallyMadeUp')
check(not unknown.is_known(), 'unknown raw -> fallback (not known)')
check(UsageType.from_string('') is not None, 'empty -> safe Unknown')
check(UsageType.from_string(None) is not None, 'None -> safe Unknown')

# ── 5. Corrupted file Fail-Closed ──────────────────────────────────
section('Fail-Closed on corrupt/missing')
raised = False
try:
    QzdbReader('/nonexistent/path.qzdb')
except QzdbError as e:
    raised = (e.code == QzdbError.NOT_FOUND)
check(raised, 'missing file raises NOT_FOUND')
# truncated file
bad = os.path.join(DATA_DIR, '.tier1_bad.qzdb')
with open(bad, 'wb') as f:
    f.write(b'QZDB\x01' + b'\x00' * 40)
raised = False
try:
    QzdbReader(bad)
except QzdbError as e:
    raised = True
check(raised, 'truncated file raises QzdbError')
os.remove(bad)
# bad magic
bad2 = os.path.join(DATA_DIR, '.tier1_badmagic.qzdb')
with open(bad2, 'wb') as f:
    f.write(b'XXXX' + b'\x00' * 200)
raised = False
try:
    QzdbReader(bad2)
except QzdbError as e:
    raised = (e.code == QzdbError.BAD_MAGIC)
check(raised, 'bad magic raises BAD_MAGIC')
os.remove(bad2)

# ── 6. CRC verification enforced ───────────────────────────────────
section('CRC enforcement')
if os.path.exists(STD):
    r = QzdbReader(STD)
    check(r.verify_crc() is True, 'valid db CRC passes')
    # flip one data byte, reload copy with crc on -> must reject
    with open(STD, 'rb') as f:
        data = bytearray(f.read())
    data[300] ^= 0xFF
    corrupt = os.path.join(DATA_DIR, '.tier1_corrupt.qzdb')
    with open(corrupt, 'wb') as f:
        f.write(data)
    raised = False
    try:
        QzdbReader(corrupt)
    except QzdbError as e:
        raised = (e.code == QzdbError.CORRUPTED)
    check(raised, 'corrupted CRC rejected (CORRUPTED)')
    os.remove(corrupt)
    r.close()

# ── 7. Lock-free atomic reload ────────────────────────────────────
section('atomic reload')
if os.path.exists(STD) and os.path.exists(ULT):
    r = QzdbReader(STD)
    before = r.find('223.5.5.5')
    r.reload(ULT)  # swap to a different db
    after = r.find('223.5.5.5')
    check(after is not None, 'reload serves new snapshot')
    r.close()

# ── 8. CIDR reverse lookup ─────────────────────────────────────────
section('CIDR reverse lookup')
if os.path.exists(ULT):
    r = QzdbReader(ULT)
    c = r.lookup_cidr('223.5.5.5')
    check(c is not None and c.endswith('/32') or c is None, 'cidr returned or None')
    check(r.lookup_cidr('not-an-ip') is None, 'invalid ip -> None (not raise)')
    check(r.lookup_cidr_uint(16909060) == r.lookup_cidr('1.2.3.4'), 'lookup_cidr_uint consistent')
    r.close()

# ── 9. Resource release ───────────────────────────────────────────
section('resource release')
if os.path.exists(STD):
    r = QzdbReader(STD)
    r.close()
    check(r.find('223.5.5.5') is None, 'query after close is safe (None)')
    r.close()  # idempotent

# ── extra: batch / stream / registry / chained ────────────────────
section('batch / stream / registry / chained')
if os.path.exists(STD):
    r = QzdbReader(STD)
    br = r.find_batch(['223.5.5.5', 'xyz'])
    check(isinstance(br[0], BatchResult) and br[0].geo_info is not None, 'find_batch hit')
    check(br[1].geo_info is None, 'find_batch miss -> None')
    st = list(r.find_stream(['223.5.5.5']))
    check(len(st) == 1 and st[0] is not None, 'find_stream yields one')
    reg = QzdbRegistry()
    reg.register('std', r)
    check(reg.find('223.5.5.5') is not None, 'registry find hit')
    ch = ChainedReader([r])
    check(ch.find('223.5.5.5') is not None, 'chained find hit')
    r.close()


print()
if _fails == 0:
    print(f'TIER1_PASS: {_asserts} assertions, 0 failures')
    sys.exit(0)
else:
    print(f'TIER1_FAIL: {_asserts} assertions, {_fails} failures')
    sys.exit(1)
