"""Vector-driven fail-closed hostile-file test for the QZDB Python SDK.

Consumes the shared, language-agnostic fixture ``tools/hostile_vectors.json``
(29 cases, self-documented in its ``_doc`` key). For every case it:

  1. loads a real ``.qzdb`` into bytes (READ-ONLY; never mutates the file on disk),
  2. resolves byte offsets from its OWN parsed 192-byte header (no baked-in absolutes),
  3. applies the mutation recipe (sweeps expand to many mutated copies),
  4. feeds each mutated copy to ``QzdbReader`` in BOTH modes
       - strict  = CRC on  (verify_crc=True)  -- the CRC gate
       - lenient = CRC off (verify_crc=False) -- the deeper attacker-recomputed-CRC path
  5. asserts the fail-closed contract: the SDK must NOT crash, must NOT hang, and must
     NOT return plausibly-correct-but-WRONG data. A rejection (any error code), a
     graceful empty result, or lenient-but-correct data all satisfy fail-closed.

This mirrors ``FailClosedHostileTest.java`` (dual-mode evaluation, divergence
reporting, ``group_index_invalid`` craftInvalidEntryRow special-case).

Conventions follow the existing ``multi-lang/python/test_*.py`` files: runnable
standalone (``python3 test_hostile_vectors.py``) and importable as a module.

    python3 test_hostile_vectors.py

Exit codes: 0 = all cases fail-closed (HOSTILE_VECTORS_OK); 1 = at least one
genuine SDK anomaly (HOSTILE_VECTORS_FAIL). If the base DB is absent, prints a
notice and exits 0 (graceful skip).

JSON stdlib only; zero new dependencies.
"""

import json
import os
import sys
import threading
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import qzdb
from qzdb import QzdbReader, QzdbError

# IPs exercised against every mutated copy. Mix of V4 (in-DB and out-of-DB) and V6.
TEST_IPS = [
    "223.5.5.5", "114.114.114.114", "1.0.1.0", "8.8.8.8",
    "0.0.0.0", "255.255.255.255", "240e:390:1:1::1", "::ffff:223.5.5.5",
]

# Per-evaluation timeout (seconds). Mirrors Java's TIMEOUT_MS = 15000.
TIMEOUT_S = 15

# Candidate locations for the base DB and the vector JSON. CWD is multi-lang/python
# when run via run_all_tests.sh, but also support running from this directory or the
# repo root.
_BASE_DB_CANDIDATES = [
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "data", "qqzeng_ip_std_china.qzdb"),
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "qqzeng_ip_std_china.qzdb"),
    "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/data/qqzeng_ip_std_china.qzdb",
]

_VECTOR_CANDIDATES = [
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools", "hostile_vectors.json"),
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "tools", "hostile_vectors.json"),
    "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/tools/hostile_vectors.json",
]

# ---------------------------------------------------------------------------
# Little-endian readers / writers (mirror Java's ru*/writeLE)
# ---------------------------------------------------------------------------

def _ru16(b, off):
    return b[off] & 0xFF | ((b[off + 1] & 0xFF) << 8)


def _ru32(b, off):
    return (b[off] & 0xFF | ((b[off + 1] & 0xFF) << 8)
            | ((b[off + 2] & 0xFF) << 16) | ((b[off + 3] & 0xFF) << 24))


def _ru48(b, off):
    v = 0
    for k in range(6):
        v |= (b[off + k] & 0xFF) << (8 * k)
    return v


def _ru64(b, off):
    v = 0
    for k in range(8):
        v |= (b[off + k] & 0xFF) << (8 * k)
    return v


def _write_le(b, off, width, value):
    """Write ``value`` as ``width`` little-endian bytes into bytearray ``b``."""
    for k in range(width):
        b[off + k] = (value >> (8 * k)) & 0xFF


def _crc32_buf(buf):
    """CRC32 over the whole buffer with bytes[16:20] treated as zero.

    Matches the SDK's ``verify_crc`` (segmented CRC: crc32(prefix) then
    crc32(4 zero bytes) then crc32(suffix)) and Java's ``CRC32.update(zeroed)``.
    """
    mv = memoryview(buf)
    crc = zlib.crc32(mv[:16])
    crc = zlib.crc32(b"\x00" * 4, crc)
    if len(buf) > 20:
        crc = zlib.crc32(mv[20:], crc)
    return crc & 0xFFFFFFFF


# ---------------------------------------------------------------------------
# Header offset resolution (consumer parses its OWN header)
# ---------------------------------------------------------------------------

def parse_header_offsets(buf):
    m = {}
    m["row_schema_start"] = _ru64(buf, 40)
    m["group_schema_start"] = _ru64(buf, 48)
    m["trie_v4_jump_start"] = _ru64(buf, 64)
    m["trie_v4_nodes_start"] = _ru64(buf, 72)
    m["trie_v6_jump_start"] = _ru64(buf, 80)
    m["trie_v6_nodes_start"] = _ru64(buf, 88)
    m["iprow_start"] = _ru64(buf, 96)
    m["geo_entries_start"] = _ru64(buf, 104)
    m["pools_start"] = _ru64(buf, 136)
    m["meta_start"] = _ru64(buf, 144)
    m["flags"] = _ru16(buf, 8)
    m["v4_node_count"] = _ru32(buf, 152)
    m["v6_node_count"] = _ru32(buf, 156)
    return m


# ---------------------------------------------------------------------------
# Bytes-buffer reader builder that HONORS the verify_crc flag.
#
# NOTE: QzdbReader.open_buffer()/reload_buffer() force CRC verification
# regardless of the verify_crc argument (reload always re-checks CRC). To mirror
# Java's Builder(copy).verifyCrc(false) -- the genuine CRC-OFF attacker path -- we
# drive the SDK's own shadow-build machinery directly with the requested flag.
# This does NOT modify qzdb.py; it exercises the same internal path reload uses,
# only without the forced-CRC override. This is what makes lenient mode actually
# skip CRC (so corrupt-but-CRC-valid copies load and are tested for crash/hang).
# ---------------------------------------------------------------------------

def build_from_bytes(buffer, verify_crc):
    r = QzdbReader(group_index=0, verify_crc=verify_crc)
    data = bytes(buffer)  # copy protection, mirrors open_buffer
    shadow = r._build_shadow(data, False)
    r._publish(shadow)
    return r


# ---------------------------------------------------------------------------
# Evaluation (mirrors Java's evaluate())
# ---------------------------------------------------------------------------

def evaluate(copy, verify_crc, baseline):
    res = {
        "opened": False,
        "code": None,       # QzdbError code name, if rejected
        "crashed": False,
        "hang": False,
        "wrong_data": False,
        "detail": "",
        "wrong_example": None,
    }

    def worker():
        try:
            r = build_from_bytes(copy, verify_crc)
            res["opened"] = True
            any_non_empty = False
            any_wrong = False
            for ip in TEST_IPS:
                # find_str never raises (returns '' on miss / invalid / error).
                got = r.find_str(ip)
                if got is None:
                    got = ""
                exp = baseline.get(ip, "")
                if got != "":
                    any_non_empty = True
                    if got != exp:
                        any_wrong = True
                        if res["wrong_example"] is None:
                            res["wrong_example"] = "ip=%s base=[%s] got=[%s]" % (ip, exp, got)
            res["wrong_data"] = any_wrong
            if res["wrong_data"]:
                res["detail"] = "WRONG-DATA"
            elif not any_non_empty:
                res["detail"] = "graceful-empty"
            else:
                res["detail"] = "correct(lenient)"
        except QzdbError as e:
            res["code"] = e.code
            res["detail"] = "rejected:" + e.code
        except Exception:
            res["crashed"] = True
            res["detail"] = "CRASH"

    t = threading.Thread(target=worker, daemon=True)
    t.start()
    t.join(TIMEOUT_S)
    if t.is_alive():
        res["hang"] = True
        res["detail"] = "HANG"
    return res


def _norm(s):
    if s is None:
        return ""
    return "".join(c.lower() for c in s if c.isalnum())


def describe_obs(obs_codes, saw_graceful, saw_correct):
    parts = []
    if obs_codes:
        parts.append("rejected:" + "/".join(sorted(obs_codes)))
    if saw_graceful:
        parts.append("graceful-empty")
    if saw_correct:
        parts.append("correct(lenient)")
    if not parts:
        parts.append("?")
    return " | ".join(parts)

# ---------------------------------------------------------------------------
# Mutation engine (mirrors Java's applyMutation)
# ---------------------------------------------------------------------------

def _as_long(o):
    if isinstance(o, bool):
        return 1 if o else 0
    if isinstance(o, int):
        return o
    if isinstance(o, float):
        return int(o)
    raise ValueError("expected number, got: %r" % (o,))


def apply_mutation(base, mut, anchors, log, sink):
    mtype = mut.get("type")
    if mtype == "header_field":
        sink(apply_header_field(base, mut, log))
    elif mtype == "header_byte_sweep":
        start = int(_as_long(mut.get("start")))
        end = int(_as_long(mut.get("end")))
        for po in mut.get("patterns", []):
            pat = int(_as_long(po)) & 0xFF
            for off in range(start, end):
                if off < 0 or off >= len(base):
                    continue
                cp = bytearray(base)
                cp[off] = pat
                sink(cp)
    elif mtype == "header_field_sweep":
        width = int(_as_long(mut.get("width")))
        value = _as_long(mut.get("value"))
        for oo in mut.get("offsets", []):
            off = int(_as_long(oo))
            cp = bytearray(base)
            if off + width > len(cp):
                log.append("skip header_field_sweep off=%d oob\n" % off)
                continue
            _write_le(cp, off, width, value)
            sink(cp)
    elif mtype == "truncate":
        if "bytes" in mut:
            length = int(_as_long(mut.get("bytes")))
            if 0 <= length < len(base):
                sink(bytearray(base[:length]))
        else:
            mode = mut.get("mode")
            if mode == "to_zero":
                lengths = [0]
            elif mode == "below_header":
                lengths = [100]
            elif mode == "at_header":
                lengths = [191]
            else:  # sweep
                lengths = [0]
                l = 1
                while l <= len(base):
                    lengths.append(l)
                    if l == len(base):
                        break
                    l *= 2
            for length in lengths:
                if 0 <= length <= len(base):
                    sink(bytearray(base[:length]))
    elif mtype == "append_junk":
        length = int(_as_long(mut.get("length")))
        fill = mut.get("fill")
        cp = bytearray(base)
        cp.extend(b"\x00" * length)
        if fill == "0xFF":
            for k in range(len(base), len(cp)):
                cp[k] = 0xFF
        elif fill == "zeros":
            pass  # already zero
        else:  # random (deterministic seed for reproducibility)
            state = 0x1234ABCD
            for k in range(len(base), len(cp)):
                state = (state * 1103515245 + 12345) & 0x7FFFFFFF
                cp[k] = state & 0xFF
        sink(cp)
    elif mtype == "section_mutate":
        anchor = mut.get("anchor")
        span = int(_as_long(mut.get("span")))
        aoff = anchors.get(anchor)
        if aoff is None or aoff < 0 or aoff >= len(base):
            log.append("skip section_mutate anchor=%s unresolved\n" % anchor)
            return
        for po in mut.get("patterns", []):
            pat = int(_as_long(po)) & 0xFF
            cp = bytearray(base)
            limit = min(span, len(base) - int(aoff))
            for k in range(limit):
                cp[int(aoff) + k] = pat
            sink(cp)
    elif mtype == "trie_nodes_fill":
        anchor = mut.get("anchor")
        count_field = mut.get("count_field")
        value = _as_long(mut.get("value"))
        write_width = int(_as_long(mut.get("write_width")))
        aoff = anchors.get(anchor)
        node_count = anchors.get(count_field)
        if aoff is None or node_count is None:
            log.append("skip trie_nodes_fill unresolved\n")
            return
        flags = int(anchors.get("flags", 0))
        if anchor == "trie_v4_nodes_start":
            stride = 6 if (flags & 0x10) else 8
        else:
            stride = 6 if (flags & 0x20) else 8
        cp = bytearray(base)
        n = min(node_count, len(cp) // stride + 1)
        for i in range(n):
            bo = int(aoff) + i * stride
            if bo + write_width + 4 > len(cp):
                break  # bounds-checked, never AIOOBE
            _write_le(cp, bo, 4, value)
            _write_le(cp, bo + write_width, 4, value)
        sink(cp)
    elif mtype == "random_bitflips":
        seed = _as_long(mut.get("seed"))
        rounds = int(_as_long(mut.get("rounds")))
        max_flips = int(_as_long(mut.get("max_flips")))
        span_obj = mut.get("span")
        span = len(base) if isinstance(span_obj, str) else int(_as_long(span_obj))
        if span > len(base):
            span = len(base)
        cp = bytearray(base)
        state = seed & 0xFFFFFFFF
        for _ in range(rounds):
            for _ in range(max_flips):
                state = (state * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
                pos = state % span
                bit = (state >> 32) % 8
                if 0 <= pos < len(cp):
                    cp[pos] ^= (1 << bit)
        sink(cp)
    elif mtype == "crc_field_corrupt":
        cp = bytearray(base)
        zeroed = bytearray(cp)
        zeroed[16] = 0
        zeroed[17] = 0
        zeroed[18] = 0
        zeroed[19] = 0
        calc = _crc32_buf(bytes(zeroed))
        bad = calc ^ 0xFFFFFFFF
        _write_le(cp, 16, 4, bad)
        sink(cp)
    elif mtype == "compound":
        cur = bytearray(base)
        for so in mut.get("steps", []):
            step_out = []
            apply_mutation(cur, so, anchors, log, step_out.append)
            if step_out:
                cur = step_out[0]
        sink(cur)
    else:
        log.append("unknown mutation type: %s\n" % mtype)


def apply_header_field(base, mut, log):
    off = int(_as_long(mut.get("offset")))
    width = int(_as_long(mut.get("width")))
    value = _as_long(mut.get("value"))
    mask = mut.get("mask")
    mask = _as_long(mask) if mask is not None else None
    cp = bytearray(base)
    if width == 48:
        if off + 6 > len(cp):
            log.append("skip header_field width48 oob\n")
            return cp
        cur = _ru48(cp, off)
        nv = (cur ^ mask) if mask is not None else value
        for k in range(6):
            cp[off + k] = (nv >> (8 * k)) & 0xFF
    else:
        if off + width > len(cp):
            log.append("skip header_field oob\n")
            return cp
        cur = 0
        if width == 1:
            cur = cp[off] & 0xFF
        elif width == 2:
            cur = _ru16(cp, off)
        elif width == 4:
            cur = _ru32(cp, off)
        elif width == 8:
            cur = _ru64(cp, off)
        else:
            log.append("bad width %d\n" % width)
            return cp
        nv = (cur ^ mask) if mask is not None else value
        _write_le(cp, off, width, nv)
    return cp

def craft_invalid_entry_row(base, anchors):
    """group_index_invalid real row-level attack (mirrors Java's craftInvalidEntryRow).

    The literal recipe writes values identical to std_china's current header
    (byte-level no-op), so instead fill the IPRow section with 0xFF (entryId
    inevitably out of bounds) and rewrite the canonical CRC32 so both modes load
    successfully -- pushing the test from load-time to query-time fail-closed
    validation. The SDK must then degrade gracefully or raise a column-level
    error, never crash / hang / return wrong data.
    """
    cp = bytearray(base)
    iprow_off = anchors.get("iprow_start", -1)
    row_count = _ru32(cp, 20)
    row_size = _ru32(cp, 160)
    if (iprow_off <= 0 or row_count <= 1 or row_size <= 0 or row_size > 64
            or int(iprow_off) + row_count * row_size > len(cp)):
        return cp
    r_off = int(iprow_off)
    span = row_count * row_size
    end = r_off + min(span, len(cp) - r_off)
    for k in range(r_off, end):
        cp[k] = 0xFF
    zeroed = bytearray(cp)
    zeroed[16] = 0
    zeroed[17] = 0
    zeroed[18] = 0
    zeroed[19] = 0
    crc = _crc32_buf(bytes(zeroed))
    _write_le(cp, 16, 4, crc)
    return cp


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def _locate(paths):
    for p in paths:
        if os.path.isfile(p) and os.access(p, os.R_OK):
            return p
    return None


def main():
    base_path = _locate(_BASE_DB_CANDIDATES)
    if base_path is None:
        print("NOTICE: base DB qqzeng_ip_std_china.qzdb not found -- skipping hostile vector suite (graceful skip).")
        print("HOSTILE_VECTORS_OK")
        return 0

    vector_path = _locate(_VECTOR_CANDIDATES)
    if vector_path is None:
        print("NOTICE: hostile_vectors.json not found -- skipping hostile vector suite (graceful skip).")
        print("HOSTILE_VECTORS_OK")
        return 0

    with open(base_path, "rb") as f:
        base = f.read()
    with open(vector_path, "r", encoding="utf-8") as f:
        doc = json.load(f)

    cases = doc.get("cases", [])
    anchors = parse_header_offsets(base)

    # Baseline: query the UNMUTATED file so we can detect wrong (non-empty, differing) data.
    baseline = {}
    try:
        br = build_from_bytes(base, True)
        for ip in TEST_IPS:
            try:
                baseline[ip] = br.find_str(ip)
            except QzdbError:
                baseline[ip] = ""
        br.close()
    except QzdbError as e:
        print("FAIL: baseline load of healthy DB failed: %s" % e.code)
        return 2

    print("=== Python Fail-Closed Hostile Test (consuming hostile_vectors.json) ===")
    print("Base DB: %d bytes; baseline queries: %d" % (len(base), len(TEST_IPS)))
    print()

    passed = 0
    failed = 0
    anomaly_report = []
    divergence_report = []

    for c in cases:
        cid = c.get("id")
        mut = c.get("mutation", {})
        exp = c.get("expected_outcome", {})
        exp_codes = exp.get("error_code_any", [])

        log = []
        acc = {
            "fail_closed": True,
            "obs_codes": set(),
            "saw_graceful": False,
            "saw_correct": False,
            "saw_wrong": False,
            "saw_crash": False,
            "saw_hang": False,
            "saw_lenient_wrong": False,
            "first_wrong_example": None,
            "copy_count": 0,
        }

        def sink(cp):
            acc["copy_count"] += 1
            m1 = evaluate(cp, False, baseline)  # lenient: CRC off
            m2 = evaluate(cp, True, baseline)   # strict: CRC on
            strict_ok = (not m2["crashed"]) and (not m2["hang"]) and (not m2["wrong_data"])
            lenient_ok = (not m1["crashed"]) and (not m1["hang"])
            if not strict_ok or not lenient_ok:
                acc["fail_closed"] = False
            if m1["code"] is not None:
                acc["obs_codes"].add(m1["code"])
            if m2["code"] is not None:
                acc["obs_codes"].add(m2["code"])
            if m2["wrong_data"]:
                acc["saw_wrong"] = True
                if acc["first_wrong_example"] is None:
                    acc["first_wrong_example"] = "STRICT " + m2["wrong_example"]
            if m1["crashed"] or m2["crashed"]:
                acc["saw_crash"] = True
            if m1["hang"] or m2["hang"]:
                acc["saw_hang"] = True
            if m1["wrong_data"]:
                acc["saw_lenient_wrong"] = True
            if (m1["opened"] and m1["detail"].startswith("graceful")) or \
               (m2["opened"] and m2["detail"].startswith("graceful")):
                acc["saw_graceful"] = True
            if (m1["opened"] and m1["detail"].startswith("correct")) or \
               (m2["opened"] and m2["detail"].startswith("correct")):
                acc["saw_correct"] = True

        if cid == "group_index_invalid":
            # Literal recipe is a byte-level no-op on std_china; craft a concrete
            # row-level attack instead (see craft_invalid_entry_row).
            sink(craft_invalid_entry_row(base, anchors))
        else:
            apply_mutation(base, mut, anchors, log, sink)

        fail_closed = acc["fail_closed"]
        obs_codes = acc["obs_codes"]
        saw_graceful = acc["saw_graceful"]
        saw_correct = acc["saw_correct"]
        first_wrong_example = acc["first_wrong_example"]

        if acc["copy_count"] == 0:
            fail_closed = False
            first_wrong_example = "NO COPIES GENERATED (mutation entirely out of bounds - test gap)"

        exp_norm = {_norm(o) for o in exp_codes}

        divergent = False
        if fail_closed:
            for oc in obs_codes:
                if _norm(oc) not in exp_norm:
                    divergent = True
                    break
            if not divergent and saw_graceful and "gracefulnull" not in exp_norm:
                divergent = True
            if not divergent and saw_correct and "gracefulnull" not in exp_norm:
                divergent = True

        if not fail_closed:
            status = "FAIL"
            failed += 1
            reason = "WRONG-DATA" if acc["saw_wrong"] else (
                "CRASH" if acc["saw_crash"] else ("HANG" if acc["saw_hang"] else "NO-COPIES"))
            anomaly_report.append(
                "ANOMALY  %s  [%s]\n    mutation=%s\n    example=%s"
                % (cid, reason, mut, first_wrong_example))
        else:
            passed += 1
            status = "PASS*" if divergent else "PASS"
            if divergent:
                divergence_report.append(
                    "DIVERGENT  %s  observed=%s expected=%s"
                    % (cid, describe_obs(obs_codes, saw_graceful, saw_correct), exp_codes))

        print("  [%-6s] %-32s copies=%-4d %s"
              % (status, cid, acc["copy_count"],
                 describe_obs(obs_codes, saw_graceful, saw_correct)))

    print()
    print("HostileVectors: %d/%d passed" % (passed, len(cases)))

    if divergence_report:
        print()
        print("--- Divergences (fail-closed holds, but observed family != expected) ---")
        for d in divergence_report:
            print(d)

    if failed > 0:
        print()
        print("--- SDK Anomaly Report (genuine fail-closed violations) ---")
        for a in anomaly_report:
            print(a)
        print()
        print("HOSTILE_VECTORS_FAIL")
        return 1

    print()
    print("HOSTILE_VECTORS_OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
