'use strict';

/** qqzeng-ip 6.0 (.db) Node.js 版冒烟测试 (CJS + ESM 双入口)。 */

const IpDbSearch = require('./lib/IpDbSearch');

function main() {
  let db;
  try {
    db = IpDbSearch.getInstance();
  } catch (e) {
    console.log('数据库未找到, 跳过:', e.message);
    return;
  }
  console.log(`数据库加载完成: nodes=${db.nodeCount}, strings=${db.stringCount}`);

  for (const ip of ['1.10.11.12', '8.8.8.8', '58.213.198.68', '255.255.255.255', '1..2.3']) {
    console.log(`${ip} -> ${db.find(ip)}`);
  }
  const v = IpDbSearch.fastParseIp('1.2.3.4');
  if (v !== 0x01020304) throw new Error('fastParseIp failed');
  if (IpDbSearch.fastParseIp('1..2.3') !== -1) throw new Error('strict parse failed');

  // ESM 入口冒烟 (子进程, 避免与 CJS require 混用)
  require('child_process').execSync(
    `node --input-type=module -e "import m from './lib/IpDbSearch.mjs'; ` +
    `if (m.fastParseIp('8.8.8.8') !== 0x08080808) process.exit(1);"`, { stdio: 'inherit' });
  console.log('ESM entry ok');
}

main();
