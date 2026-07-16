/**
 * QzdbSearcher - Node.js SDK calling example
 *
 * Usage: node test.js
 * Place qqzeng_ip_std_china.qzdb in the same directory or specify the path.
 */

const path = require('path');
const fs = require('fs');
const QzdbSearcher = require('./qzdb');

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

    const searcher = QzdbSearcher.getInstance(dbPath);

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
console.log('TEST_PASS');
