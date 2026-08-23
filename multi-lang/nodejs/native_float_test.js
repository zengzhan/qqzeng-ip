/**
 * 原生浮点格式化统一契约边界回归（FORMAT §10.5 / ROADMAP P0-2）。
 * 与 python/test_native_float.py、go/qzdb/native_float_test.go 用例逐字同源。
 * 运行：node native_float_test.js
 */
'use strict';
const QzdbReader = require('./qzdb');
const _formatNativeFloat = QzdbReader._formatNativeFloat;

let passed = 0, failed = 0;
function check(cond, msg) {
  if (cond) { passed++; } else { failed++; console.log('  FAIL:', msg); }
}

function fmtBuf(value, fw) {
  const b = Buffer.alloc(8);
  if (fw === 4) b.writeFloatLE(value, 0); else b.writeDoubleLE(value, 0);
  return _formatNativeFloat(fw, b, 0);
}

// double(1e300) 精确十进制展开（str(int(1e300)) 一次性导出后固化的字面量，
// 不用被测函数或同算法解码器生成，避免循环论证）
const E300_LITERAL =
  '10000000000000000525047602552044202487044685811081591549158541155118024579889' +
  '08195786371375080447864043704443832883878176942523235360430575644792184786706' +
  '98284838720092657580373783023379478809005936895323497079994508111903896764088' +
  '0074652742780142494579258788820056842838115669472196386865459400540160';

// float64 边界
const f64 = [
  [116.0, '116', '整值无小数点'],
  [-3.0, '-3', '负整值'],
  [0.0, '0', '零'],
  [-0.0, '0', '负零归一为 "0"'],
  [116.4, '116.400000', '非整数固定 6 位'],
  [-3.5, '-3.500000', '负非整数'],
  [NaN, '', 'NaN -> ""'],
  [Infinity, '', '+Inf -> ""'],
  [-Infinity, '', '-Inf -> ""'],
  [1e16, '10000000000000000', '2^53 整值'],
  [9.2e18, '9200000000000000000', 'int64 上界内大整值'],
  [9223372036854774784.0, '9223372036854774784', '< 2^63 最大可表示偶数整值'],
  [9223372036854775808.0, '9223372036854775808', '恰为 2^63 走定点分支'],
  [-9223372036854775808.0, '-9223372036854775808', '恰为 -2^63'],
  [1e20, '100000000000000000000', '> 2^63 定点整数位'],
  [1e300, E300_LITERAL, '1e300 定点展开精确值（非精确可表示）'],
  [-1e300, '-' + E300_LITERAL, '-1e300'],
];
for (const [val, want, label] of f64) {
  const got = fmtBuf(val, 8);
  check(got === want, `f64 ${label}: got ${String(got).slice(0, 44)} want ${String(want).slice(0, 44)}`);
}

// float32 边界（float32 116.4 的精确 double 值 = 116.40000152587890625）
check(fmtBuf(116.4, 4) === '116.400002', `f32 按精确 double 值舍入（got ${fmtBuf(116.4, 4)}）`);
check(fmtBuf(116.0, 4) === '116', 'f32 整值无小数点');
check(fmtBuf(-3.0, 4) === '-3', 'f32 负整值');
check(fmtBuf(Infinity, 4) === '', 'f32 Inf -> ""');

console.log(`NativeFloat: ${passed} passed, ${failed} failed`);
if (failed === 0) console.log('NATIVE_FLOAT_OK'); else console.log('NATIVE_FLOAT_FAIL');
process.exit(failed ? 1 : 0);
