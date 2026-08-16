#!/usr/bin/env node
'use strict';
/*
 * QZDB reference-compliant benchmark (Node.js) — docs/BENCH_CONTRACT.md
 * Ports gen_stream() from tools/bench_gen.py and SELF-CHECKS FNV-1a parity
 * against bench_vectors.json. Template for the other languages.
 */
const fs = require('fs');
const path = require('path');
const os = require('os');
const { Worker, isMainThread } = require('worker_threads');
const QzdbReader = require('./qzdb.js');

const MASK = (1n << 64n) - 1n;
const MASTER_SEED = 20260807n;
const OPS_FULL = 2000000;
const POOL_V4 = 4096, POOL_V6 = 1024;
const FINGERPRINT_N = 1024;
const LAT_EVERY = 20;

function fnv1a(buf, h = 0n) {
  if (h === 0n) h = 0xCBF29CE484222325n;
  for (let i = 0; i < buf.length; i++) {
    h ^= BigInt(buf[i]);
    h = (h * 0x100000001B3n) & MASK;
  }
  return h;
}

class SM {
  constructor(seed) { this.s = BigInt(seed) & MASK; }
  next() {
    this.s = (this.s + 0x9E3779B97F4A7C15n) & MASK;
    let z = this.s;
    z = ((z ^ (z >> 30n)) * 0xBF58476D1CE4E5B9n) & MASK;
    z = ((z ^ (z >> 27n)) * 0x94D049BB133111EBn) & MASK;
    return z ^ (z >> 31n);
  }
  u32() { return this.next() & 0xFFFFFFFFn; }
  u128() { return ((this.next() & MASK) << 64n) | (this.next() & MASK); }
}

function buildPools() {
  const p4 = new SM(MASTER_SEED + 1n), pool_v4 = [];
  for (let i = 0; i < POOL_V4; i++) pool_v4.push(p4.u32());
  const p6 = new SM(MASTER_SEED + 2n), pool_v6 = [];
  for (let i = 0; i < POOL_V6; i++) pool_v6.push(p6.u128());
  return [pool_v4, pool_v6];
}

function genV4(dist, rng, pool_v4, base4, i) {
  if (dist === 'random') return rng.u32();
  if (dist === 'hot') return pool_v4[Number(rng.u32() % 4096n)];
  if (dist === 'sequential') return (base4 + BigInt(i)) & 0xFFFFFFFFn;
  const r = rng.u32() % 10n;
  if (r < 6n) return pool_v4[Number(rng.u32() % 4096n)];
  if (r < 9n) return rng.u32();
  return (base4 + BigInt(i)) & 0xFFFFFFFFn;
}
function genV6(dist, rng, pool_v6, base6, i) {
  if (dist === 'random') { const v = rng.u128(); return [v >> 64n, v & MASK]; }
  if (dist === 'hot') { const v = pool_v6[Number(rng.u32() % 1024n)]; return [v >> 64n, v & MASK]; }
  if (dist === 'sequential') { const v = (base6 + BigInt(i)) & ((1n << 128n) - 1n); return [v >> 64n, v & MASK]; }
  const r = rng.u32() % 10n;
  if (r < 6n) { const v = pool_v6[Number(rng.u32() % 1024n)]; return [v >> 64n, v & MASK]; }
  if (r < 9n) { const v = rng.u128(); return [v >> 64n, v & MASK]; }
  const v = (base6 + BigInt(i)) & ((1n << 128n) - 1n); return [v >> 64n, v & MASK];
}
function mapped(ip) {
  const v = (0xFFFFn << 32n | (ip & 0xFFFFFFFFn)) & ((1n << 128n) - 1n);
  return [v >> 64n, v & MASK];
}
function encV4(ip) { const b = Buffer.alloc(8); b.writeBigUInt64LE(ip & 0xFFFFFFFFn, 0); return b; }
function encV6(h, l) { const b = Buffer.alloc(16); b.writeBigUInt64LE(h & MASK, 0); b.writeBigUInt64LE(l & MASK, 8); return b; }

function* genStream(dist, mode, seed, pool_v4, pool_v6) {
  const rng = new SM(seed);
  const base4 = rng.u32();
  const base6 = rng.u128();
  for (let i = 0; i < OPS_FULL; i++) {
    if (mode === 'v4') yield [0, genV4(dist, rng, pool_v4, base4, i), 0n];
    else if (mode === 'v6') {
      if (rng.u32() % 5n === 0n) { const [h, l] = mapped(genV4(dist, rng, pool_v4, base4, i)); yield [2, h, l]; }
      else { const [h, l] = genV6(dist, rng, pool_v6, base6, i); yield [1, h, l]; }
    } else {
      const m = i % 10;
      if (m < 5) yield [0, genV4(dist, rng, pool_v4, base4, i), 0n];
      else if (m < 9) { const [h, l] = genV6(dist, rng, pool_v6, base6, i); yield [1, h, l]; }
      else { const [h, l] = mapped(genV4(dist, rng, pool_v4, base4, i)); yield [2, h, l]; }
    }
  }
}
function encQuery(q) { return q[0] === 0 ? encV4(q[1]) : encV6(q[1], q[2]); }

function percentile(arr, p) {
  if (!arr.length) return 0;
  const a = arr.slice().sort((x, y) => x - y);
  const idx = Math.min(a.length - 1, Math.max(0, Math.ceil(p * a.length) - 1));
  return a[idx];
}

function findDb(edition, reg, region, fn) {
  const roots = [
    path.join(__dirname, '..', 'test_data_202608'),
    path.join(__dirname, 'test_data_202608'),
    path.join(__dirname, '..', '..', 'test_data_202608'),
  ];
  for (const r of roots) { const p = path.join(r, reg, region, fn); if (fs.existsSync(p)) return p; }
  return null;
}

// Route each query kind to the entry point a real caller would use, and return
// whether it hit. kind 2 is IPv4-mapped (::ffff:w.x.y.z) and MUST use
// findBytes(), which performs the mapped downgrade; findV6() is a pure v6 trie
// walk that never downgrades, so it would miss on every mapped query and the
// bench would time the early-exit miss path instead of a real lookup.
// `buf` is a caller-owned scratch Buffer — findBytes() does not retain it.
function dispatch(reader, q, buf) {
  if (q[0] === 0) return reader.findUint(Number(q[1])) !== null;
  if (q[0] === 2) {
    buf.writeBigUInt64BE(q[1], 0);
    buf.writeBigUInt64BE(q[2], 8);
    return reader.findBytes(buf) !== null;
  }
  return reader.findV6(q[1], q[2]) !== null;
}

function runSingle(reader, dist, mode, seed, pool_v4, pool_v6, ops, sample) {
  const lat = [];
  const buf = Buffer.allocUnsafe(16);
  let hits = 0;
  const t0 = process.hrtime.bigint();
  let i = 0;
  for (const q of genStream(dist, mode, seed, pool_v4, pool_v6)) {
    let found;
    if (sample && i % LAT_EVERY === 0) {
      const s = process.hrtime.bigint();
      found = dispatch(reader, q, buf);
      lat.push(Number(process.hrtime.bigint() - s));
    } else {
      found = dispatch(reader, q, buf);
    }
    if (found) hits++;
    if (++i >= ops) break;
  }
  const elapsed = Number(process.hrtime.bigint() - t0) / 1e9;
  // hit_rate is reported because a QPS number without it is uninterpretable:
  // a high QPS may just mean every query took the early-exit miss path.
  return { ops, qps: Math.round(ops / elapsed), avg_ns: Math.round(elapsed * 1e9 / ops),
    p50_ns: percentile(lat, 0.5), p95_ns: percentile(lat, 0.95), p99_ns: percentile(lat, 0.99),
    errors: 0, hits, hit_rate: hits / ops };
}

function parityCheck(manifest, pool_v4, pool_v6) {
  let bad = 0;
  for (const dist of Object.keys(manifest.streams)) {
    for (const mode of Object.keys(manifest.streams[dist])) {
      const info = manifest.streams[dist][mode];
      let fnv = 0n;
      let i = 0;
      for (const q of genStream(dist, mode, info.seed, pool_v4, pool_v6)) {
        if (i++ >= FINGERPRINT_N) break;
        fnv = fnv1a(encQuery(q), fnv);
      }
      if (fnv !== BigInt(String(info.first1024_fnv1a))) { bad++; console.log('  MISMATCH', dist, mode); }
    }
  }
  return bad === 0;
}

function runWorkerQueries(db, dist, mode, seed, count) {
  const reader = new QzdbReader(db, 0, false);
  const [pool_v4, pool_v6] = buildPools();
  const buf = Buffer.allocUnsafe(16);
  let done = 0, err = 0;
  for (const q of genStream(dist, mode, seed, pool_v4, pool_v6)) {
    try { dispatch(reader, q, buf); done++; }   // a miss is expected, not a failure
    catch (e) { err++; break; }
    if (done >= count) break;
  }
  return { done, err };
}

const { spawn } = require('child_process');
function concurrencySafe(db, dist, mode, seed) {
  const n = 16, per = 100000 / n;
  return new Promise((resolve) => {
    let pending = n, errors = 0, done = 0;
    const self = path.join(__dirname, 'bench_contract.js');
    for (let t = 0; t < n; t++) {
      const cp = spawn(process.execPath, ['-e',
        `process.env.BENCH_WORKER='1';` +
        `try{const r=require(${JSON.stringify(self)}).runWorkerQueries(${JSON.stringify(db)},${JSON.stringify(dist)},${JSON.stringify(mode)},${seed},${per});console.log(JSON.stringify(r));process.exit(0)}catch(e){console.error(String(e&&e.stack||e));process.exit(1)}`
      ], { stdio: ['ignore', 'pipe', 'inherit'] });
      let buf = '';
      cp.stdout.on('data', (d) => buf += d);
      cp.on('close', (code) => {
        if (code === 0) { const r = JSON.parse(buf); done += r.done; errors += r.err; }
        else errors++;
        if (--pending === 0) resolve(errors === 0 && done === n * per);
      });
    }
  });
}

async function main() {
  const ops = parseInt(process.env.BENCH_OPS || OPS_FULL, 10);
  const editions = (process.env.BENCH_EDITIONS || 'std_china,max_global').split(',');
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'tools', 'bench_vectors.json'), 'utf8'));
  const [pool_v4, pool_v6] = buildPools();
  process.stdout.write('parity self-check ... ');
  if (!parityCheck(manifest, pool_v4, pool_v6)) { console.log('FAILED'); process.exit(1); }
  console.log('OK (12/12)');

  const reportsDir = path.join(__dirname, '..', 'bench_reports');
  fs.mkdirSync(reportsDir, { recursive: true });
  const editionsMap = { std_china: ['std', 'china', 'qqzeng_ip_std_china.qzdb'], max_global: ['max', 'global', 'qqzeng_ip_max_global.qzdb'] };

  for (const edition of editions) {
    const [reg, region, fn] = editionsMap[edition];
    const db = findDb(edition, reg, region, fn);
    if (!db) { console.log(`[SKIP] ${edition}: not found`); continue; }
    const reader = new QzdbReader(db, 0, false);
    console.log(`\nedition ${edition}: ${db} (${fs.statSync(db).size} bytes)`);

    const safe = await concurrencySafe(db, 'hot', 'mixed', manifest.streams.hot.mixed.seed, pool_v4, pool_v6);
    console.log(`  concurrency_safe(16x100k): ${safe}`);

    const distOut = {};
    for (const dist of Object.keys(manifest.streams)) {
      distOut[dist] = {};
      for (const mode of Object.keys(manifest.streams[dist])) {
        const seed = manifest.streams[dist][mode].seed;
        const cold = runSingle(reader, dist, mode, seed, pool_v4, pool_v6, Math.min(ops, 200000), true); cold.warm = 'cold';
        runSingle(reader, dist, mode, seed, pool_v4, pool_v6, Math.min(ops, 1000000), false);
        const hot = runSingle(reader, dist, mode, seed, pool_v4, pool_v6, ops, true); hot.warm = 'hot';
        distOut[dist][mode] = { cold, hot, threads: { '1': hot } };
        console.log(`  ${dist.padEnd(11)}.${mode.padEnd(6)} hot QPS=${String(hot.qps).padStart(12)} p50=${String(hot.p50_ns).padStart(6)}ns p99=${String(hot.p99_ns).padStart(7)}ns err=${hot.errors} hit=${(hot.hit_rate * 100).toFixed(1)}%`);
      }
    }

    // string round-trip on hot.mixed
    const seed = manifest.streams.hot.mixed.seed;
    const lat = []; let n = 0; const t0 = process.hrtime.bigint();
    for (const q of genStream('hot', 'mixed', seed, pool_v4, pool_v6)) {
      const s = q[0] === 0 ? `${(Number(q[1]) >> 24) & 255}.${(Number(q[1]) >> 16) & 255}.${(Number(q[1]) >> 8) & 255}.${Number(q[1]) & 255}`
        : (() => { const v = (q[1] << 64n) | q[2]; const g = []; for (let k = 0; k < 8; k++) g.push(((v >> (112n - 16n * BigInt(k))) & 0xFFFFn).toString(16)); return g.join(':'); })();
      if (n % LAT_EVERY === 0) { const ss = process.hrtime.bigint(); reader.find(s); lat.push(Number(process.hrtime.bigint() - ss)); }
      else reader.find(s);
      if (++n >= ops) break;
    }
    const el = Number(process.hrtime.bigint() - t0) / 1e9;
    const string_rt = { api: 'string', ops: n, qps: Math.round(n / el), avg_ns: Math.round(el * 1e9 / n), p50_ns: percentile(lat, 0.5), p95_ns: percentile(lat, 0.95), p99_ns: percentile(lat, 0.99), errors: 0, warm: 'hot' };
    console.log(`  ${'hot'.padEnd(11)}.${'mixed'.padEnd(6)} STRING round-trip QPS=${String(string_rt.qps).padStart(12)} p99=${String(string_rt.p99_ns).padStart(7)}ns`);

    const report = {
      contract: 'QZDB_BENCH_CONTRACT v1.0', language: 'nodejs', sdk_version: 'multi-lang/nodejs',
      timestamp: new Date().toISOString(), seed: Number(MASTER_SEED),
      db: { path: db, edition, bytes: fs.statSync(db).size, hash: 'crc32:n/a' },
      environment: { cpu: os.cpus()[0] ? os.cpus()[0].model : '?', cores: os.cpus().length, ram_gb: Math.round(os.totalmem() / 1e9), os: `${os.type()} ${os.release()}`, runtime: `Node ${process.version}`, compiler: 'V8', bench_contract: 'v1.0', note: 'Node is single-threaded; concurrency_safety uses worker_threads (per-worker reader).' },
      distributions: distOut, string_roundtrip: { hot: { mixed: string_rt } }, concurrency_safe: safe,
    };
    const out = path.join(reportsDir, `nodejs_${edition}.json`);
    fs.writeFileSync(out, JSON.stringify(report, null, 2));
    console.log(`  wrote ${out}`);
  }
}
const isEntry = (typeof require !== 'undefined') && require.main === module;
if (isEntry && !process.env.BENCH_WORKER) main();

module.exports = { runWorkerQueries, genStream, buildPools, parityCheck };
