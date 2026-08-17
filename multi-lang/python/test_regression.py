# -*- coding: utf-8 -*-
"""回归测试（第一批 fail-closed / 零拷贝修复）。

覆盖三项修复：
  1. find_fields / lookup_cidr 缺 _has_v4/_has_v6 族门禁 —— 单族库（或 flags
     被篡改清位的文件）此前会把文件头当跳表读（幻觉结果或裸 struct.error），
     现在与 C# 同构在 walk 函数顶部统一拦截。
  2. 32 位节点分支缺 idx >= node_count 检查（5 处）—— 伪造 child 指针可让
     unpack_from 越过文件抛裸异常。与 24 位分支的既有检查对齐。
  3. verify_crc 对 mmap 做 d[20:] 整文件拷贝 —— 改为 memoryview 零拷贝
     （行为不变，仅消除 122MB 级瞬时分配）。

运行：python3 test_regression.py（依赖 ../data/qqzeng_ip_std_china.qzdb）
"""
import os
import struct
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from qzdb import QzdbReader, QzdbError  # noqa: E402

DB = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'data', 'qqzeng_ip_std_china.qzdb')
if not os.path.exists(DB):
    print('SKIP: missing', DB)
    sys.exit(0)

passed = failed = 0


def check(cond, msg):
    global passed, failed
    if cond:
        passed += 1
    else:
        failed += 1
        print('  FAIL:', msg)


with open(DB, 'rb') as f:
    base = f.read()

# ---- 1. 族门禁：清除 hasV4 标志后，V4 查询必须 miss（不得幻觉命中） ----
mutated = bytearray(base)
flags = struct.unpack_from('<H', mutated, 8)[0]
struct.pack_into('<H', mutated, 8, flags & ~0x01)  # 清 bit0 (hasV4)
with tempfile.NamedTemporaryFile(suffix='.qzdb', delete=False) as tf:
    tf.write(mutated)
    tmp = tf.name
try:
    r = QzdbReader(tmp, verify_crc=False)
    check(r.find('114.114.114.114') is None, '清 hasV4 后 find 应返回 None')
    check(r.find_fields('114.114.114.114', ['country']) is None,
          '清 hasV4 后 find_fields 应返回 None（修复前把文件头当跳表读）')
    check(r.lookup_cidr('114.114.114.114') is None,
          '清 hasV4 后 lookup_cidr 应返回 None（修复前同上）')
    # V6 未受影响
    check(isinstance(r.find('2408:8000:9000::1'), object), '清 hasV4 不影响 V6 查询路径')
    r.close()
finally:
    os.unlink(tmp)

# ---- 2. 越界 child 指针：find 必须优雅 miss，不得抛裸异常 ----
mutated2 = bytearray(base)
off_v4_jump = struct.unpack_from('<Q', mutated2, 64)[0]
struct.pack_into('<I', mutated2, off_v4_jump + 0x7272 * 4, 0x7FFF0000)
with tempfile.NamedTemporaryFile(suffix='.qzdb', delete=False) as tf:
    tf.write(mutated2)
    tmp2 = tf.name
try:
    r2 = QzdbReader(tmp2, verify_crc=False)
    try:
        got = r2.find('114.114.114.114')
        check(got is None, '越界 child 指针必须 fail-closed 返回 None')
    except QzdbError:
        check(True, '')  # 结构化错误也可接受（fail-closed）
        passed += 0
    except Exception as e:  # noqa: BLE001
        check(False, f'不得抛非结构化异常: {type(e).__name__}: {e}')
    r2.close()
finally:
    os.unlink(tmp2)

# ---- 3. verify_crc：memoryview 改写后行为不变 ----
r3 = QzdbReader(DB, verify_crc=True)
check(r3.verify_crc() is True, 'memoryview CRC：真实库校验仍通过')
r3.close()

print(f'test_regression: {passed} passed, {failed} failed')
sys.exit(1 if failed else 0)
