'use strict';
/*
 * QZDB Node.js Tier3 并发安全测试
 * 16 Worker 线程 × 100,000 次查询 = 160 万 op
 * 验证无锁快照架构的线程安全性
 */

const { Worker, isMainThread, parentPort, workerData } = require('worker_threads');
const path = require('path');

if (isMainThread) {
  const QzdbReader = require('./qzdb');
  const STD_DB = path.join(__dirname, 'qqzeng_ip_std_china.qzdb');
  const r = new QzdbReader(STD_DB);
  const expected = r.find('223.5.5.5').toPipe();
  const THREADS = 16;
  const OPS = 100000;
  let completed = 0;
  let totalErrors = 0;
  let totalOps = 0;
  const { performance } = require('perf_hooks');

  console.log('  [Tier3] 启动 ' + THREADS + ' worker x ' + OPS + ' ops...');
  const t0 = performance.now();

  for (let t = 0; t < THREADS; t++) {
    const worker = new Worker(__filename, {
      workerData: { dbPath: STD_DB, ops: OPS, tid: t }
    });
    worker.on('message', (msg) => {
      totalErrors += msg.errors;
      totalOps += msg.ops;
      completed++;
      if (completed === THREADS) {
        const elapsed = performance.now() - t0;
        const qps = (totalOps / elapsed * 1000).toFixed(0);
        console.log('  [Tier3] 完成: ' + totalOps + ' ops, ' + totalErrors + ' errors, ' + qps + ' QPS (' + elapsed.toFixed(0) + 'ms)');
        if (totalErrors === 0) {
          console.log('  [Tier3 PASS] 16 线程并发 0 错误');
        } else {
          console.error('  [Tier3 FAIL] ' + totalErrors + ' errors');
          process.exit(1);
        }
        r.close();
      }
    });
    worker.on('error', (e) => {
      console.error('  [Tier3] Worker error: ' + e);
      process.exit(1);
    });
  }
} else {
  // Worker thread
  const QzdbReader = require('./qzdb');
  const r = new QzdbReader(workerData.dbPath);
  let errors = 0;
  for (let i = 0; i < workerData.ops; i++) {
    const ip = (i % 256) + '.' + ((i * 17 + workerData.tid) % 256) + '.' + ((i * 131) % 256) + '.1';
    try {
      r.find(ip);
    } catch (e) {
      if (e.code !== 'NOT_FOUND') errors++;
    }
  }
  parentPort.postMessage({ errors, ops: workerData.ops });
  r.close();
}
