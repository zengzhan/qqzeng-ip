# -*- coding: utf-8 -*-
"""原生浮点格式化统一契约边界回归（FORMAT §10.5 / ROADMAP P0-2）。

不依赖外部数据库：直接以最小桩实例驱动 QzdbReader._decode_native，
对 float32/float64 两条解码路径断言 §10.5 全部边界行为：
  - 整数值且 |v| < 2^63 → 无小数点整数字面量
  - 整数值且 |v| ≥ 2^63 → 定点整数位（无小数点、无科学计数法）
  - 非整数 → 固定 6 位小数
  - NaN / ±Inf → ""
与 Go/Node/C/Rust/PHP 的 native_float 边界用例逐字同源。

运行：python3 test_native_float.py
"""
import math
import struct
import sys
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from qzdb import QzdbReader  # noqa: E402

# double(1e300) 的精确十进制展开（str(int(1e300)) 一次性导出后固化，非被测路径生成）
E300_LITERAL = (
    '10000000000000000525047602552044202487044685811081591549158541155118024579889'
    '08195786371375080447864043704443832883878176942523235360430575644792184786706'
    '98284838720092657580373783023379478809005936895323497079994508111903896764088'
    '0074652742780142494579258788820056842838115669472196386865459400540160'
)

passed = failed = 0


def check(cond, msg):
    global passed, failed
    if cond:
        passed += 1
    else:
        failed += 1
        print('  FAIL:', msg)


def fmt64(x):
    r = object.__new__(QzdbReader)
    r._data = struct.pack('<d', x)
    return QzdbReader._decode_native(r, 1, 8, 0)


def fmt32(x):
    r = object.__new__(QzdbReader)
    r._data = struct.pack('<f', x)
    return QzdbReader._decode_native(r, 1, 4, 0)


# --- float64 边界 ------------------------------------------------------------
f64_cases = [
    (116.0, '116', '整值无小数点'),
    (-3.0, '-3', '负整值'),
    (0.0, '0', '零'),
    (-0.0, '0', '负零归一为 "0"'),
    (116.4, '116.400000', '非整数固定 6 位'),
    (-3.5, '-3.500000', '负非整数'),
    (float('nan'), '', 'NaN -> ""'),
    (float('inf'), '', '+Inf -> ""'),
    (float('-inf'), '', '-Inf -> ""'),
    (1e16, '10000000000000000', '2^53 整值'),
    (9.2e18, '9200000000000000000', 'int64 上界内大整值'),
    (9223372036854774784.0, '9223372036854774784', '< 2^63 最大可表示偶数整值'),
    (9223372036854775808.0, '9223372036854775808', '恰为 2^63 走定点分支'),
    (-9223372036854775808.0, '-9223372036854775808', '恰为 -2^63'),
    (1e20, '100000000000000000000', '> 2^63 定点整数位'),
    # 1e300 非精确可表示：契约输出的是该 double 的精确整数值（各语言定点
    # 格式化均展开精确二进制值）。期望值硬编码（str(int(1e300)) 导出一次后
    # 固化），避免与实现同为 str(int(v)) 造成循环论证。
    (1e300, E300_LITERAL, '1e300 定点展开精确值'),
    (-1e300, '-' + E300_LITERAL, '-1e300'),
]
for val, want, label in f64_cases:
    got = fmt64(val)
    check(got == want, f'f64 {label}: got {got[:40]!r} want {want[:40]!r}')

# --- float32 边界 ------------------------------------------------------------
# float32 116.4 的精确 double 值为 116.40000152587890625，6 位舍入 = 116.400002
check(fmt32(116.4) == '116.400002', f'f32 非整数按 double 精确值舍入（got {fmt32(116.4)!r}）')
check(fmt32(116.0) == '116', 'f32 整值无小数点')
check(fmt32(float('inf')) == '', 'f32 Inf -> ""')

print(f'NativeFloat: {passed} passed, {failed} failed')
print('NATIVE_FLOAT_OK' if failed == 0 else 'NATIVE_FLOAT_FAIL')
sys.exit(1 if failed else 0)
