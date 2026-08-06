const fs = require('fs');
const QzdbReader = require('./qzdb');

function bench(name, dbPath, count, v6count, first) {
  if (!fs.existsSync(dbPath)) { console.log(`  ${name}: not found`); return; }
  let s;
  if (first) s = QzdbReader.getInstance(dbPath);
  else { s = QzdbReader.getInstance(); s.load(dbPath); }

  const ips = new Uint32Array(count);
  let seed = 123;
  for (let i = 0; i < count; i++) {
    seed = (seed * 1664525 + 1013904223) >>> 0;
    ips[i] = seed;
  }
  let start = process.hrtime.bigint();
  for (let i = 0; i < count; i++) s.findUint(ips[i]);
  const v4qps = Math.floor(count / (Number(process.hrtime.bigint() - start) / 1e9));

  function nxt(s) { return (Math.imul(s, 1664525) + 1013904223) >>> 0; }
  let vs = 456;
  start = Date.now();
  for (let i = 0; i < v6count; i++) {
    vs = nxt(vs);
    const high = (BigInt(vs) << 32n) | BigInt(nxt(vs = nxt(vs)));
    vs = nxt(vs);
    const low = (BigInt(vs) << 32n) | BigInt(nxt(vs = nxt(vs)));
    s.findV6(high, low);
  }
  const v6qps = Math.floor(v6count / ((Date.now() - start) / 1000));
  console.log(`  ${name.padEnd(12)} V4 QPS: ${v4qps}  V6 QPS: ${v6qps}`);
}

const count = 3000000, v6count = 1000000;
console.log('Node.js QPS Benchmarks (M4 Pro)');
bench('std_china', '../data/qqzeng_ip_std_china.qzdb', count, v6count, true);
bench('max_china', '../data/qqzeng_ip_max_china.qzdb', count, v6count, false);
bench('max_global', '../data/qqzeng_ip_max_global.qzdb', count, v6count, false);
