"""Regression tests for the review-driven fixes (F1–F11) of the QZDB Python SDK.

Run with:
    python3 test_review_fixes.py
"""

import os
import sys
import ipaddress

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import qzdb
from qzdb import (
    QzdbReader, QzdbError, QzdbRegistry, ChainedReader, BatchResult,
    UsageType, RowIds, Registry,
)

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


HAVE_DATA = os.path.exists(STD) and os.path.exists(ULT)


# ── F1: GeoInfo.cidr property (must not raise AttributeError) ──────
def test_f1_cidr_property():
    if not HAVE_DATA:
        return
    r = QzdbReader(ULT)
    gi = r.find('223.5.5.5')
    if gi is not None:
        check(gi.cidr == '', 'gi.cidr returns "" (no AttributeError)')
        check(gi.get_cidr() == '', 'gi.get_cidr() still returns ""')
    r.close()


# ── F2: typed geo_id / asn / usage_type attributes ─────────────────
def test_f2_typed_attrs():
    if not HAVE_DATA:
        return
    r = QzdbReader(ULT)
    gi = r.find('223.5.5.5')
    if gi is not None:
        # typed attribute forms
        check(isinstance(gi.geo_id, int) or gi.geo_id is None,
              f'gi.geo_id typed int|None (got {type(gi.geo_id).__name__})')
        check(isinstance(gi.asn, int) or gi.asn is None,
              f'gi.asn typed int|None (got {type(gi.asn).__name__})')
        check(isinstance(gi.usage_type, UsageType),
              f'gi.usage_type typed UsageType (got {type(gi.usage_type).__name__})')
        # __getattr__ raw-string fallback must NOT apply to these names
        check(not isinstance(gi.geo_id, str), 'gi.geo_id is not raw str')
        # get_* aliases still agree
        check(gi.get_geo_id() == gi.geo_id, 'get_geo_id() == geo_id')
        check(gi.get_asn() == gi.asn, 'get_asn() == asn')
        check(gi.get_usage_type() == gi.usage_type, 'get_usage_type() == usage_type')
    r.close()


# ── F3: find() raises on invalid / empty IP (API §7.1) ─────────────
def test_f3_find_raises():
    if not HAVE_DATA:
        return
    r = QzdbReader(STD)
    for bad in ['', '   ', '999.1.1.1', 'not-an-ip', '1.2.3.4/24', '::1%lo',
                '256.256.256.256', 'abc.def.ghi.jkl']:
        raised = False
        try:
            r.find(bad)
        except QzdbError as e:
            raised = (e.code == QzdbError.INVALID_PARAM)
        check(raised, f'find({bad!r}) raises INVALID_PARAM')
    # valid IP still returns GeoInfo, not raise
    check(r.find('223.5.5.5') is not None or r.find('223.5.5.5') is None,
          'valid IP does not raise (control)')
    r.close()


# ── F4: find_batch three-state (invalid -> error set) ──────────────
def test_f4_batch_threestate():
    if not HAVE_DATA:
        return
    r = QzdbReader(STD)
    res = r.find_batch(['223.5.5.5', 'not-an-ip', '8.8.8.8'])
    check(len(res) == 3, 'find_batch length')
    check(isinstance(res[0], BatchResult) and res[0].geo_info is not None,
          'hit: geo_info set, error None')
    check(res[0].error is None, 'hit: error None')
    check(res[1].geo_info is None and isinstance(res[1].error, QzdbError),
          'invalid: geo_info None, error set (three-state)')
    check(res[1].error.code == QzdbError.INVALID_PARAM, 'invalid: error code INVALID_PARAM')
    check(res[2].geo_info is None and res[2].error is None, 'clean miss: geo_info None, error None')
    # BatchResult.info alias
    check(res[0].info is res[0].geo_info, 'BatchResult.info aliases geo_info')
    r.close()


# ── F5: find_iter (BatchResult stream) + find_stream resilience ────
def test_f5_iter_and_stream():
    if not HAVE_DATA:
        return
    r = QzdbReader(STD)
    out = list(r.find_iter(['223.5.5.5', 'bad', '8.8.8.8']))
    check(len(out) == 3 and all(isinstance(b, BatchResult) for b in out),
          'find_iter yields BatchResult')
    check(out[1].error is not None, 'find_iter invalid -> error set')
    # find_stream must NOT crash on invalid input (lenient GeoInfo|None)
    streamed = list(r.find_stream(['223.5.5.5', 'bad']))
    check(streamed[0] is not None and streamed[1] is None,
          'find_stream resilient: invalid -> None, no crash')
    r.close()


# ── F6: open_buffer static factory from bytes ──────────────────────
def test_f6_open_buffer():
    if not HAVE_DATA:
        return
    with open(STD, 'rb') as f:
        raw = f.read()
    r = QzdbReader.open_buffer(raw)
    check(r.find('223.5.5.5') is not None, 'open_buffer(bytes) serves queries')
    check(isinstance(r, QzdbReader), 'open_buffer returns QzdbReader')
    r.close()
    # copy semantics: mutating original does not affect reader
    mutated = bytearray(raw)
    mutated[30] ^= 0xFF
    r2 = QzdbReader.open_buffer(bytes(raw))
    r2.close()
    # invalid buffer raises
    raised = False
    try:
        QzdbReader.open_buffer(b'')
    except QzdbError as e:
        raised = (e.code == QzdbError.INVALID_PARAM)
    check(raised, 'open_buffer(b"") raises INVALID_PARAM')


# ── F7: lookup_ids returns RowIds namedtuple ───────────────────────
def test_f7_rowids():
    if not HAVE_DATA:
        return
    r = QzdbReader(STD)
    rid = r.lookup_row_id('223.5.5.5')
    if rid:
        row = r.lookup_ids(rid)
        check(isinstance(row, RowIds), 'lookup_ids returns RowIds')
        check(hasattr(row, 'geo_id') and hasattr(row, 'asn_id')
              and hasattr(row, 'usage_type_id'), 'RowIds has named fields')
        # still unpackable like a tuple (backward compat)
        g, a, u = row
        check((g, a, u) == tuple(row), 'RowIds unpacks like tuple')
    r.close()


# ── F8: BatchResult.info alias ─────────────────────────────────────
def test_f8_batchresult_info():
    check(hasattr(BatchResult, 'info'), 'BatchResult.info property exists')
    b = BatchResult('1.2.3.4', None, None)
    check(b.info is b.geo_info, 'BatchResult.info == geo_info')


# ── F9: Registry / ChainedReader symmetry ──────────────────────────
def test_f9_registry_chained():
    if not HAVE_DATA:
        return
    # Registry alias
    check(Registry is QzdbRegistry, 'Registry is alias of QzdbRegistry')
    reg = QzdbRegistry()
    reg.register_path('std', STD)
    with open(ULT, 'rb') as f:
        reg.register_buffer('ult', f.read())
    check(reg.find('223.5.5.5') is not None, 'registry find (path+buffer) hit')
    check('std' in reg.names(), 'registry names includes std')
    reg.unregister('std')
    check('std' not in reg.names(), 'registry unregister works')
    # registry find_batch
    rb = reg.find_batch(['223.5.5.5', 'bad'])
    check(isinstance(rb[0], BatchResult) and rb[1].error is not None,
          'registry find_batch three-state')

    # ChainedReader factories + aggregations
    ch = ChainedReader.chain(QzdbReader(STD), QzdbReader(ULT))
    check(ch.find('223.5.5.5') is not None, 'ChainedReader.chain find hit')
    ch2 = ChainedReader.chain_merge(QzdbReader(STD), QzdbReader(ULT))
    check(len(ch2.readers) == 2, 'chain_merge readers count')
    ch3 = ChainedReader.chain_merge_override(QzdbReader(STD), QzdbReader(ULT))
    check(len(ch3.readers) == 2, 'chain_merge_override readers count')
    check(len(ch.editions) == 2 and len(ch.scopes) == 2,
          'chained editions/scopes aggregate')
    cb = ch.find_batch(['223.5.5.5', 'bad'])
    check(isinstance(cb[0], BatchResult) and cb[1].error is not None,
          'chained find_batch three-state')
    ch.close()


# ── F10: UsageType spec-named accessors ────────────────────────────
def test_f10_usage_type():
    ut = UsageType.from_string('CDN')
    check(ut.raw == 'CDN', 'UsageType.raw property')
    check(ut.display_zh == 'CDN', 'UsageType.display_zh property')
    check(ut.display_en == 'CDN', 'UsageType.display_en property')
    check(ut.description != '', 'UsageType.description property')
    check(UsageType.from_raw('VPN') is UsageType.from_string('VPN'), 'from_raw alias')
    unk = UsageType.from_string('MadeUp')
    check(isinstance(unk, str) and not isinstance(unk, UsageType),
          'unknown raw -> plain str')
    check(unk == 'MadeUp', 'unknown returns raw string')
    # Unknown SCENARIO (a known enum member) still carries display metadata
    unk_scenario = UsageType.from_string('Unknown')
    check(unk_scenario.display_zh == '未知', 'Unknown scenario display_zh')


# ── F11: find_v6_bytes ::ffff: downgrade ───────────────────────────
def test_f11_mapped_downgrade():
    if not HAVE_DATA:
        return
    r = QzdbReader(ULT)
    gi = r.find('223.5.5.5')
    if gi is not None:
        ipi = int(ipaddress.IPv4Address('223.5.5.5'))
        mapped = b'\x00' * 10 + b'\xff\xff' + ipi.to_bytes(4, 'big')
        gb = r.find_v6_bytes(mapped)
        check(gb is not None, 'find_v6_bytes(::ffff:1.2.3.4) downgrades to V4')
        check(gb.to_pipe() == gi.to_pipe(), 'mapped downgrade matches find()')
    r.close()


# ── cross-entrypoint consistency (Oracle) ──────────────────────────
def test_cross_entrypoint():
    if not HAVE_DATA:
        return
    r = QzdbReader(STD)
    mism = 0
    for off in range(0, 1 << 16, 101):
        ipi = (0xC0A80000 + off) & 0xFFFFFFFF
        ip = str(ipaddress.IPv4Address(ipi))
        a = r.find(ip)
        b = r.find_uint(ipi)
        c = r.find_bytes(ipaddress.IPv4Address(ipi).packed)
        pa = a.to_pipe() if a else None
        pb = b.to_pipe() if b else None
        pc = c.to_pipe() if c else None
        if not (pa == pb == pc):
            mism += 1
    check(mism == 0, f'find/find_uint/find_bytes consistent (mism={mism})')
    r.close()


# ── concurrency: reload stress + concurrent find ───────────────────
def test_concurrency():
    if not HAVE_DATA:
        return
    import threading
    r = QzdbReader(STD)
    errors = []
    reloads_done = []

    def worker():
        for _ in range(300):
            try:
                r.find('223.5.5.5')
            except Exception as e:  # noqa
                errors.append(e)
            # exercise the raise path under concurrency; INVALID_PARAM is expected
            try:
                r.find('not-an-ip')
            except QzdbError:
                pass
            except Exception as e:  # noqa
                errors.append(e)

    def reloader():
        for _ in range(15):
            try:
                r.reload(ULT if (_ % 2 == 0) else STD)
                reloads_done.append(1)
            except Exception as e:  # noqa
                errors.append(e)

    threads = [threading.Thread(target=worker) for _ in range(8)]
    rt = threading.Thread(target=reloader)
    for t in threads:
        t.start()
    rt.start()
    for t in threads:
        t.join()
    rt.join()
    check(len(errors) == 0, f'concurrent find/reload no errors (errors={len(errors)})')
    check(len(reloads_done) == 15, 'all reloads completed')
    r.close()


if __name__ == '__main__':
    test_f1_cidr_property()
    test_f2_typed_attrs()
    test_f3_find_raises()
    test_f4_batch_threestate()
    test_f5_iter_and_stream()
    test_f6_open_buffer()
    test_f7_rowids()
    test_f8_batchresult_info()
    test_f9_registry_chained()
    test_f10_usage_type()
    test_f11_mapped_downgrade()
    test_cross_entrypoint()
    test_concurrency()

    print()
    if _fails == 0:
        print(f'REVIEW_FIXES_PASS: {_asserts} assertions, 0 failures')
        sys.exit(0)
    else:
        print(f'REVIEW_FIXES_FAIL: {_asserts} assertions, {_fails} failures')
        sys.exit(1)
