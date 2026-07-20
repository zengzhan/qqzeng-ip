'use strict';
// Direct Node-vs-Python correctness comparison. Writes IP lists to temp files,
// invokes a Python script (file, not inline) that reads them and emits pipe
// strings as a JSON file; Node computes its own and we diff in-process.
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const DB = process.argv[2] || '../data/qqzeng_ip_max_global.qzdb';
const N = parseInt(process.argv[3] || '200000', 10);

function nxt(x){ return (Math.imul(x, 1664525) + 1013904223) >>> 0; }
const v4 = [], v6 = [];
let s = 123;
for (let i = 0; i < N; i++) { s = nxt(s); v4.push(s); }
s = 456;
for (let i = 0; i < N; i++) {
  s = nxt(s); const h = (BigInt(s) << 32n) | BigInt(nxt(s = nxt(s)));
  s = nxt(s); const l = (BigInt(s) << 32n) | BigInt(nxt(s = nxt(s)));
  v6.push(`${h}:${l}`);
}

const tmpV4 = path.join(os.tmpdir(), 'cmp_v4.txt');
const tmpV6 = path.join(os.tmpdir(), 'cmp_v6.txt');
const tmpOut = path.join(os.tmpdir(), 'cmp_py_out.json');
fs.writeFileSync(tmpV4, v4.join('\n'));
fs.writeFileSync(tmpV6, v6.join('\n'));

const pyScript = `
import sys, json
sys.path.insert(0, ${JSON.stringify(path.join(__dirname, '..', 'python'))})
from qzdb import QzdbSearcher
db = ${JSON.stringify(DB)}
s = QzdbSearcher(db)
out = {'v4': [], 'v6': []}
with open(${JSON.stringify(tmpV4)}) as f:
    for line in f:
        line = line.strip()
        if not line: continue
        info = s.find_uint(int(line))
        out['v4'].append(info.to_pipe() if info else '')
with open(${JSON.stringify(tmpV6)}) as f:
    for line in f:
        line = line.strip()
        if not line: continue
        h, l = line.split(':')
        info = s.find_v6_uint((int(h) << 64) | int(l))
        out['v6'].append(info.to_pipe() if info else '')
with open(${JSON.stringify(tmpOut)}, 'w') as fo:
    json.dump(out, fo)
`;
const pyFile = path.join(os.tmpdir(), 'cmp_py.py');
fs.writeFileSync(pyFile, pyScript);
execFileSync('python3', [pyFile], { stdio: 'ignore' });
const py = JSON.parse(fs.readFileSync(tmpOut, 'utf8'));

const Q = require('./qzdb');
const sn = Q.getInstance(DB);
const nv4 = v4.map(ip => { const r = sn.findUint(ip); return r ? r.toPipe() : ''; });
const nv6 = v6.map(pair => { const [h, l] = pair.split(':'); const r = sn.findV6(BigInt(h), BigInt(l)); return r ? r.toPipe() : ''; });

let d4 = 0, d6 = 0;
for (let i = 0; i < N; i++) { if (nv4[i] !== py.v4[i]) d4++; if (nv6[i] !== py.v6[i]) d6++; }
console.log(`DB=${DB} N=${N}`);
console.log(`  V4: Node vs Python diff = ${d4} / ${N}`);
console.log(`  V6: Node vs Python diff = ${d6} / ${N}`);
console.log(d4 === 0 && d6 === 0 ? '  RESULT: PASS (identical)' : '  RESULT: FAIL');
