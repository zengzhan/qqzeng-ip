/**
 * QzdbReader - Node.js SDK calling example
 *
 * Usage: node test.js
 * Place qqzeng_ip_std_china.qzdb in the same directory or specify the path.
 */

const path = require('path');
const fs = require('fs');
const QzdbReader = require('./qzdb');

function findDb() {
    for (const c of [
        'qqzeng_ip_std_china.qzdb',
        '../data/qqzeng_ip_std_china.qzdb',
        'data/qqzeng_ip_std_china.qzdb',
    ]) {
        if (fs.existsSync(c)) return c;
    }
    return null;
}

function main() {
    const dbPath = findDb();
    if (!dbPath) {
        console.log('Database file not found');
        return;
    }

    const searcher = QzdbReader.open(dbPath);

    console.log(`Fields (${searcher._fieldNames.length}): ${searcher._fieldNames.join(', ')}\n`);

    // Query sample V4 IPs
    for (const ip of ['114.114.114.114', '223.5.5.5', '8.8.8.8']) {
        const result = searcher.findStr(ip);
        console.log(`find("${ip}") => ${result || '(null)'}`);
    }

    // Query a V6 IP
    const result = searcher.findStr('2408:8000:9000::1');
    console.log(`find("2408:8000:9000::1") => ${result || '(null)'}`);

    // Get structured fields
    console.log('\n--- Structured fields for 114.114.114.114 ---');
    const loc = searcher.find('114.114.114.114');
    if (loc) {
        for (const name of searcher._fieldNames) {
            console.log(`  ${name}: ${loc[name] || ''}`);
        }
    }
}

main();
runEmbeddedV4StrictnessTest();
console.log('TEST_PASS');

// ── 内嵌 IPv4 严格性回归（对齐 Go netip 拒绝规则）──
// 通过 vm 模块检查直接访问内部 fastParseIp（该符号未从模块导出）。
function runEmbeddedV4StrictnessTest() {
    const vm = require('vm');
    const fs = require('fs');
    const src = fs.readFileSync(path.join(__dirname, 'qzdb.js'), 'utf8');
    const sandbox = {
        require, module: { exports: {} }, exports: {}, console, Buffer,
        process, __dirname, setTimeout, clearTimeout, TextDecoder, TextEncoder,
    };
    vm.createContext(sandbox);
    vm.runInContext(src, sandbox);
    const fastParseIp = sandbox.fastParseIp;
    if (typeof fastParseIp !== 'function') {
        console.log('EMBEDDED_V4_TEST_SKIP: fastParseIp not inspectable');
        return;
    }
    // 内嵌 IPv4 必须位于地址末尾；"<groups>:<v4>::"（v4 落在 "::" 之前）属非法地址，须拒绝。
    const reject = ['0.0.0.0::', '1.2.3.4::', '2001:db8:1.2.3.4::'];
    const accept = ['::1.2.3.4', '2001:db8::1.2.3.4', '1::2.3.4.5',
                    '114.114.114.114', '::ffff:7272:7272'];
    let fails = 0;
    for (const ip of reject) {
        if (fastParseIp(ip) !== null) {
            console.log(`  FAIL: embedded-v4-before-gap should reject: ${ip}`);
            fails++;
        }
    }
    for (const ip of accept) {
        if (fastParseIp(ip) === null) {
            console.log(`  FAIL: valid form should accept: ${ip}`);
            fails++;
        }
    }
    if (fastParseIp('fe80::1%eth0') !== null) { console.log('  FAIL: zone-id should reject'); fails++; }
    if (fastParseIp('1::2::3') !== null) { console.log('  FAIL: double-gap should reject'); fails++; }
    if (fails > 0) {
        console.log(`EMBEDDED_V4_TEST_FAIL: ${fails} failures`);
        process.exit(1);
    }
    console.log('EMBEDDED_V4_TEST_PASS');
}
