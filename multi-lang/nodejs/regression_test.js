'use strict';

/*
 * 回归测试（第一批 fail-closed 修复）：
 *   1. Trie 子节点索引越界（伪造跳表/child 指针）时 find 必须优雅返回 null，
 *      不得抛 OUT_OF_BOUNDS、更不得把文件内其它 section 当节点读出垃圾 rowId。
 *      （修复前 _getV4Child/_getV6Child 不校验 nodeIdx >= nodeCount）
 *   2. getFileHash() 在空/已 close 的 reader 上返回 '00000000'，
 *      不得对空 Buffer 静默算出假 CRC。
 *
 * 运行：node regression_test.js（依赖 ../data/qqzeng_ip_std_china.qzdb）
 */

const assert = require('assert');
const fs = require('fs');
const path = require('path');

const QzdbReader = require('./qzdb');

const STD_DB = path.join(__dirname, '..', 'data', 'qqzeng_ip_std_china.qzdb');
if (!fs.existsSync(STD_DB)) {
  console.log('SKIP: missing ' + STD_DB);
  process.exit(0);
}

const base = fs.readFileSync(STD_DB);
let checks = 0;

/* --- 1. 越界 child 指针 → 优雅 miss，不抛异常 --- */
{
  const mutated = Buffer.from(base);
  const offV4Jump = Number(mutated.readBigUInt64LE(64));
  // 把 114.114.0.0/16 桶的跳表条目改成越界的内节点索引：
  // 非零、非哨兵、远超 v4NodeCount，且 0x7fff0000 * 8 远超文件长度。
  const slot = offV4Jump + 0x7272 * 4;
  mutated.writeUInt32LE(0x7fff0000, slot);
  const r = new QzdbReader.Builder(mutated).groupIndex(0).verifyCrc(false).build();
  let outcome;
  let val = 'unset';
  try {
    val = r.find('114.114.114.114');
    outcome = 'returned';
  } catch (e) {
    outcome = 'threw:' + (e && e.code ? e.code : e.message);
  }
  assert.strictEqual(outcome, 'returned', 'find 在 child 越界时不得抛异常，got=' + outcome);
  assert.strictEqual(val, null, '越界 child 必须 fail-closed 返回 null');
  checks++;
  r.close();
}

/* --- 2. getFileHash 空守卫 --- */
{
  const r = new QzdbReader.Builder(STD_DB).build();
  const hashBefore = r.getFileHash();
  assert.match(hashBefore, /^[0-9a-f]{8}$/, '正常 reader 的 hash 形态');
  r.close();
  assert.strictEqual(
    r.getFileHash(),
    '00000000',
    'close 后 getFileHash 应返回 00000000（修复前对空 Buffer 算出垃圾值）',
  );
  checks += 2;
}

console.log('regression_test: PASS (' + checks + ' checks)');
