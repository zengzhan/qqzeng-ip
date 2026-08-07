'use strict';

/*
 * QZDB Node.js SDK — Tier2 地面真值逐字段校验器
 * 依据 docs/QZDB_TEST_SPECIFICATION.md §三 (Tier2)
 *
 * 用法：
 *   node tier2_csv_verify.js                          # 自动扫描 test_data_202608 全 10 版本（抽样）
 *   node tier2_csv_verify.js full std china            # 单库全量
 *   node tier2_csv_verify.js sampled std global        # 单库抽样
 */
const fs = require('fs');
const path = require('path');
const readline = require('readline');
const QzdbReader = require('./qzdb');

const TEST_DATA = path.join(__dirname, '..', 'test_data_202608');
const ALL_VERSIONS = ['std', 'pro', 'max', 'ult', 'asn'];
const ALL_SCOPES = ['china', 'global'];

function parseCsvLine(line) {
  const out = [];
  let cur = '', inQuote = false;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (inQuote) {
      if (c === '"') { if (line[i + 1] === '"') { cur += '"'; i++; } else inQuote = false; }
      else cur += c;
    } else if (c === '"') inQuote = true;
    else if (c === ',') { out.push(cur); cur = ''; }
    else cur += c;
  }
  out.push(cur);
  return out;
}

function networkIpFromCidr(cidr) {
  const slash = cidr.lastIndexOf('/');
  return slash > 0 ? cidr.slice(0, slash) : cidr;
}

function isExcludedMappedV6(networkIp) {
  if (networkIp.indexOf(':') < 0) return false;
  const r = QzdbReader.parseIp(networkIp);
  if (!r || r.v4 !== null) return false;
  if (!r.v6) return false;
  const b = r.v6;
  for (let i = 0; i < 10; i++) if (b[i] !== 0) return false;
  return (b[10] & 0xFF) === 0xFF && (b[11] & 0xFF) === 0xFF;
}

async function verifyDb(qzdbPath, csvPath, opts) {
  const reader = new QzdbReader(qzdbPath, 0, false);
  const full = opts.full !== false;
  const stride = opts.stride || (full ? 1 : 97);
  const fileSize = fs.statSync(csvPath).size;
  const useStream = fileSize > 400 * 1024 * 1024;

  let header = null;
  let fieldCount = 0;
  const linesBuf = [];

  let total = 0, matches = 0, mismatch = 0;
  let v4Total = 0, v6Total = 0, excluded = 0;
  const diffs = [];
  const t0 = process.hrtime.bigint();

  if (!useStream) {
    const lines = fs.readFileSync(csvPath, 'utf8').split('\n');
    header = parseCsvLine(lines[0]);
    fieldCount = header.length - 1;
    for (let i = 1, lineNo = 1; i < lines.length; i += stride) {
      const line = lines[i];
      processCsvLine(line, i + 1, fieldCount);
    }
  } else {
    const rl = readline.createInterface({
      input: fs.createReadStream(csvPath),
      crlfDelay: Infinity,
    });
    let lineNo = 0;
    for await (const line of rl) {
      lineNo++;
      if (lineNo === 1) { header = parseCsvLine(line); fieldCount = header.length - 1; continue; }
      if ((lineNo - 2) % stride !== 0) continue;
      processCsvLine(line, lineNo, fieldCount);
    }
  }

  function processCsvLine(line, csvLineNo, fc) {
    if (!line) return;
    total++;
    const cols = parseCsvLine(line);
    if (cols.length < fc + 1) return;
    const cidr = cols[0];
    const expected = cols.slice(1, fc + 1);
    const networkIp = networkIpFromCidr(cidr);
    const isV6 = networkIp.indexOf(':') >= 0;
    if (isV6) v6Total++; else v4Total++;
    if (isExcludedMappedV6(networkIp)) { excluded++; return; }

    let info;
    try { info = reader.find(networkIp); } catch (e) { info = null; }
    if (info === null) {
      mismatch++;
      if (diffs.length < 20) diffs.push({ csv: csvLine, ip: networkIp, cidr, expected: expected.join('|'), got: '(NOT_FOUND)' });
      return;
    }
    const got = info.toPipe();
    const expPipe = expected.join('|');
    if (got !== expPipe) {
      mismatch++;
      if (diffs.length < 20) diffs.push({ csv: csvLine, ip: networkIp, cidr, expected: expPipe, got });
    } else matches++;
  }

  const elapsed = Number(process.hrtime.bigint() - t0) / 1e6;
  reader.close();
  return { total, matches, mismatch, v4Total, v6Total, excluded, elapsed, diffs, stride, tag: useStream ? 'stream' : (full ? 'full' : 'sample') };
}

async function main() {
  console.log('=== QZDB Node.js Tier2 地面真值校验 ===\n');

  const args = process.argv.slice(2);
  let targets;
  if (args.length >= 3) {
    const mode = args[0], ver = args[1], scope = args[2];
    const qzdb = path.join(TEST_DATA, ver, scope, `qqzeng_ip_${ver}_${scope}.qzdb`);
    const csv = path.join(TEST_DATA, ver, scope, `qqzeng_ip_${ver}_${scope}.csv`);
    targets = [{ ver, scope, qzdb, csv, full: mode === 'full' }];
  } else {
    targets = [];
    for (const ver of ALL_VERSIONS)
      for (const scope of ALL_SCOPES) {
        const qzdb = path.join(TEST_DATA, ver, scope, `qqzeng_ip_${ver}_${scope}.qzdb`);
        const csv = path.join(TEST_DATA, ver, scope, `qqzeng_ip_${ver}_${scope}.csv`);
        if (fs.existsSync(qzdb) && fs.existsSync(csv))
          targets.push({ ver, scope, qzdb, csv, full: false });
      }
  }

  let grandMismatch = 0, grandTotal = 0, grandExcluded = 0;
  for (const t of targets) {
    const r = await verifyDb(t.qzdb, t.csv, { full: t.full });
    grandMismatch += r.mismatch;
    grandTotal += r.total;
    grandExcluded += r.excluded;
    const tag = r.tag;
    console.log(`[${t.ver}/${t.scope}] ${tag} 节点=${r.total} V4=${r.v4Total} V6=${r.v6Total} ` +
      `匹配=${r.matches} 偏差=${r.mismatch} 排除(mapped)=${r.excluded} 耗时=${r.elapsed.toFixed(1)}ms`);
    if (r.diffs.length) {
      console.log(`  首 ${r.diffs.length} 条差异：`);
      for (const d of r.diffs)
        console.log(`    #${d.csv} ${d.cidr} ip=${d.ip}\n      expected=${JSON.stringify(d.expected)}\n      got     =${JSON.stringify(d.got)}`);
    }
  }

  console.log(`\n[Tier2 汇总] 总节点=${grandTotal} 总偏差=${grandMismatch} 排除=${grandExcluded}`);
  console.log(grandMismatch === 0 ? '[ALL PASS] Tier2 地面真值 0 偏差' : `[FAIL] Tier2 偏差 ${grandMismatch} 条`);
  if (grandMismatch !== 0) process.exit(1);
}

main().catch(e => { console.error(e); process.exit(2); });
