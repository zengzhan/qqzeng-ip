# -*- coding: utf-8 -*-
"""Metadata TLV type=5/6（data_month/scope）权威语义回归（FORMAT §8.2 / ROADMAP T7）。

不依赖外部数据库文件：在内存中构造最小合法 .qzdb（256 字节 header + Metadata 段），
经 open_buffer 加载（强制 CRC，故内嵌按 SDK 规则计算的 canonical CRC32）。

覆盖：
  1. 带 type=5/6 条目：get_data_month()/get_scope() 取 TLV 值（权威）。
  2. 无条目（旧文件）：scope 为 ""，data_month 回落 Header BuildDate。
  3. buildTime 始终取自 Header BuildDate，与 TLV 无关。

运行：python3 test_tlv_meta.py
"""
import os
import struct
import sys
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from qzdb import QzdbReader  # noqa: E402

HEADER_SIZE = 256  # 预留到 256：Metadata 段紧随其后（offMeta = 256）


def _build_db(meta_tlvs=b'', build_date=0):
    """最小合法库：无 V4/V6 覆盖（查询返回 None），仅元信息路径可观测。"""
    buf = bytearray(HEADER_SIZE)
    buf[0:4] = b'QZDB'
    buf[4] = 1                                   # format version
    struct.pack_into('<H', buf, 8, 0x0004 if meta_tlvs else 0x0000)  # flags bit2=hasMeta
    buf[13] = 2                                  # poolIdxSize（校验要求 ∈ {2,3}）
    struct.pack_into('<I', buf, 32, build_date)  # BuildDate yyyyMMdd
    struct.pack_into('<I', buf, 36, 192)         # headerSize
    struct.pack_into('<I', buf, 160, 6)          # ipRowSize
    struct.pack_into('<I', buf, 164, 1)          # geoEntryGroupCount
    body = b''
    if meta_tlvs:
        struct.pack_into('<Q', buf, 144, HEADER_SIZE)  # offMeta
        body = meta_tlvs
    blob = bytearray(bytes(buf) + body)
    # canonical CRC（与 SDK verify_crc 一致）：@16 填 4 个 0 字节参与全文件计算
    crc = zlib.crc32(blob[0:16])
    crc = zlib.crc32(b'\x00' * 4, crc)
    crc = zlib.crc32(blob[20:], crc)
    struct.pack_into('<I', blob, 16, crc & 0xFFFFFFFF)
    return bytes(blob)


def tlv(t, val):
    b = val.encode('utf-8')
    return struct.pack('<BBH', t, 0, len(b)) + b


passed = failed = 0


def check(cond, msg):
    global passed, failed
    if cond:
        passed += 1
    else:
        failed += 1
        print('  FAIL:', msg)


# --- 1) 带 type=5/6：TLV 权威 ------------------------------------------------
db = _build_db(tlv(5, '2026-07') + tlv(6, 'global'))
r = QzdbReader.open_buffer(db)
check(r.get_data_month() == '2026-07', f'TLV type=5 权威 dataMonth（got {r.get_data_month()!r}）')
check(r.get_scope() == 'global', f'TLV type=6 权威 scope（got {r.get_scope()!r}）')
check(r.get_build_time() == '', 'buildTime 与 TLV 无关（无 BuildDate 时为 ""）')
r.close()

# --- 2) 无条目：回落路径（旧行为零变化） --------------------------------------
db2 = _build_db(build_date=20260805)
r2 = QzdbReader.open_buffer(db2)
check(r2.get_data_month() == '2026-08', f'dataMonth 回落 BuildDate（got {r2.get_data_month()!r}）')
check(r2.get_scope() == '', f'旧文件 scope 为 ""（got {r2.get_scope()!r}）')
check(r2.get_build_time() == '2026-08-05', f'buildTime 取自 BuildDate（got {r2.get_build_time()!r}）')
r2.close()

print(f'TLV meta: {passed} passed, {failed} failed')
sys.exit(1 if failed else 0)
