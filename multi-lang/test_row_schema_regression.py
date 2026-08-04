#!/usr/bin/env python3
"""
ROW_SCHEMA 解析回归测试 (Bug1 守卫)

目的:
  证明 QZDB 的 ROW_SCHEMA 字节偏移修复(从错误的 "sp+5/sp+9 Java-compatible"
  布局改为规范的 "sp+0/sp+1/sp+4" 布局)是必要的,且修复后的真实 SDK 行为正确。

为什么需要它:
  asn_china 这种 2 字段文件(fid 顺序 geo,asn),新旧偏移"巧合"算出相同宽度(2,2),
  所以 Bug1 一直潜伏没触发。但一旦字段顺序/数量/stride 不同,旧偏移就会算错宽度 ->
  IP-Row 读取错位 -> ASN 崩到默认 56554。本测试用"字段顺序打乱"和"3 字段"两种布局
  触发差异,确保修复不会被回退。

测试内容:
  A. 真实 SDK 加载: 对补丁过的真实 qzdb(ROW_SCHEMA 字段顺序打乱),
     断言 row_geo_width/row_asn_width 仍为正确的 (2, 2) — 真实 SDK 代码路径验证。
  B. 双公式对比: 在同一组字节上跑 NEW(规范)与 OLD(错误)解析公式,
     断言它们在"打乱顺序/3字段"布局上 DIVERGE(OLD 给出错误宽度),
     在 asn_china 原布局上 COINCIDE(解释为何原文件不触发)。
"""
import importlib.util
import os
import shutil
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REAL_QZDB = "/tmp/real_asn_china/qzdb/qqzeng_ip_asn_china.qzdb"
PATCHED_QZDB = "/tmp/patched_row_schema.qzdb"
SDK_PATH = os.path.join(HERE, "python", "qzdb.py")
DEFAULT_ASN = 56554


def load_sdk():
    spec = importlib.util.spec_from_file_location("qzdb_regress", SDK_PATH)
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


def new_parse(d, sp):
    """规范布局: sp+0=fieldCount, sp+1=stride, sp+4 起 4 字节/字段 {fid,width,off,flags}"""
    if sp + 4 > len(d):
        return None
    fcount = d[sp]
    stride = d[sp + 1]
    if not (1 <= fcount <= 8) or sp + 4 + fcount * 4 > len(d):
        return None
    geo = asn = usage = 0
    total = 0
    ok = True
    wp = sp + 4
    for _ in range(fcount):
        fid = d[wp]
        w = d[wp + 1]
        if fid == 0:
            geo = w
        elif fid == 1:
            asn = w
        elif fid == 2:
            usage = w
        wp += 4
        total += w
        if not (1 <= w <= 4):
            ok = False
    if not ok or total != stride:
        return None
    return (geo, asn, usage)


def old_parse(d, sp):
    """错误布局: sp+5=fieldCount, sp+9+i=widths"""
    if sp + 10 > len(d):
        return None
    if d[sp] != 2:
        return None
    schema_row_size = d[sp + 1]
    fcount = d[sp + 5]
    if not (1 <= fcount <= 8) or sp + 9 + fcount > len(d):
        return None
    widths = [d[sp + 9 + i] for i in range(fcount)]
    if sum(widths) != schema_row_size:
        return None
    asn = widths[0] if fcount >= 1 else 0
    geo = widths[1] if fcount >= 2 else 0
    usage = widths[2] if fcount >= 3 else 0
    return (geo, asn, usage)


def make_schema_3field():
    """3 字段 geo(2)+asn(2)+usage(2), stride=6, 规范顺序."""
    b = bytearray([3, 6, 0, 0])                       # fcount=3, stride=6, reserved
    b += bytes([0, 2, 0, 0])                           # fid=0 geo w=2
    b += bytes([1, 2, 2, 0])                           # fid=1 asn w=2
    b += bytes([2, 2, 4, 0])                           # fid=2 usage w=2
    return bytes(b)


def make_schema_swapped_2field():
    """2 字段但顺序打乱: sp+4=fid1(asn,w2), sp+8=fid0(geo,w2). stride=4."""
    b = bytearray([2, 4, 0, 0])                        # fcount=2, stride=4, reserved
    b += bytes([1, 2, 0, 0])                           # fid=1 asn w=2  (原为 fid0)
    b += bytes([0, 2, 0, 0])                           # fid=0 geo w=2   (原为 fid1)
    return bytes(b)


def main():
    failures = []

    # ---- B. 双公式对比(不依赖文件) ----
    s3 = make_schema_3field()
    n3 = new_parse(s3, 0)
    o3 = old_parse(s3, 0)
    print("[B1] 3-field schema (geo2+asn2+usage2, stride6):")
    print("     NEW ->", n3, " OLD ->", o3)
    if n3 != (2, 2, 2):
        failures.append("NEW parser wrong on 3-field schema: %s" % str(n3))
    # OLD 要么拒绝(None->默认 3,3,0),要么给出错误宽度;只要 != (2,2,2) 即证明差异
    if o3 == (2, 2, 2):
        failures.append("OLD parser unexpectedly matched NEW on 3-field schema")

    # asn_china 原始布局(规范顺序 2 字段) -> 新旧应一致
    orig = bytes([2, 4, 0, 0, 0, 2, 0, 0, 1, 2, 2, 0, 0, 0, 0, 0])
    no = new_parse(orig, 0)
    oo = old_parse(orig, 0)
    print("[B2] asn_china original 2-field schema:")
    print("     NEW ->", no, " OLD ->", oo)
    if no != (2, 2, 0):
        failures.append("NEW parser wrong on original schema: %s" % str(no))
    if no != oo:
        failures.append("original schema diverged (should coincide): NEW=%s OLD=%s" % (no, oo))

    # 打乱顺序 2 字段 -> 新旧应 DIVERGE
    sw = make_schema_swapped_2field()
    nw = new_parse(sw, 0)
    ow = old_parse(sw, 0)
    print("[B3] swapped-order 2-field schema:")
    print("     NEW ->", nw, " OLD ->", ow)
    if nw != (2, 2, 0):
        failures.append("NEW parser wrong on swapped schema: %s" % str(nw))
    if nw == ow:
        failures.append("swapped schema coincided (should diverge): both=%s" % str(nw))

    # ---- A. 真实 SDK 加载补丁文件 ----
    shutil.copy(REAL_QZDB, PATCHED_QZDB)
    sw_bytes = make_schema_swapped_2field()
    with open(PATCHED_QZDB, "r+b") as f:
        data = bytearray(f.read())
        sp = struct.unpack("<Q", data[40:48])[0]
        print("[A] patching real qzdb ROW_SCHEMA at off=%d with swapped-order schema" % sp)
        data[sp:sp + len(sw_bytes)] = sw_bytes
        f.seek(0)
        f.write(data)
    # (不重算 CRC: 测试只验证解析器宽度, CRC 不参与 init)

    m = load_sdk()
    s = m.QzdbSearcher(db_path=PATCHED_QZDB)
    print("     real SDK loaded: row_geo_width=%d row_asn_width=%d row_usage_width=%d"
          % (s._row_geo_width, s._row_asn_width, s._row_usage_width))
    if (s._row_geo_width, s._row_asn_width, s._row_usage_width) != (2, 2, 0):
        failures.append("REAL SDK parsed wrong widths on swapped schema: (%d,%d,%d)"
                        % (s._row_geo_width, s._row_asn_width, s._row_usage_width))

    print()
    if failures:
        print(">>> FAIL (%d):" % len(failures))
        for f in failures:
            print("   -", f)
        return 1
    print(">>> PASS: ROW_SCHEMA 修复正确且必要 (真实 SDK 验证 + 双公式对比)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
