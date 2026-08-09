#!/usr/bin/env node
/**
 * Batch IP query runner for Node.js
 * Usage: node batch_query.js <database_path> <v4_test> <v4_output> <v6_test> <v6_output>
 * 
 * Reads test IPs from files, queries the QZDB database, writes results.
 * Test file format: one IP per line (uint32 for V4, "high:low" for V6)
 * Output format: ip_key|pipe_separated_geo_string
 */
const fs = require('fs');
const path = require('path');

function main() {
    const args = process.argv.slice(2);
    if (args.length < 5) {
        console.error('Usage: node batch_query.js <db_path> <v4_test> <v4_out> <v6_test> <v6_out>');
        process.exit(1);
    }
    const [dbPath, v4Test, v4Out, v6Test, v6Out] = args;

    // Load SDK resolved relative to this script (tools/), NOT cwd, so it works
    // regardless of where cross_verify.py is invoked from.
    const QzdbReader = require(path.join(__dirname, '..', 'nodejs', 'qzdb'));
    // v2.4 起 getInstance() 已移除，统一用 QzdbReader.open()
    const searcher = QzdbReader.open(dbPath);

    // Helper: format geo info to pipe string matching Python reference to_pipe()
    function geoToPipe(r) {
        return r ? r.toPipe() : '';
    }

    // Process V4
    if (fs.existsSync(v4Test)) {
        const v4Ips = fs.readFileSync(v4Test, 'utf8').trim().split('\n').filter(l => l.trim());
        const v4Results = v4Ips.map(ipStr => {
            const ip = parseInt(ipStr);
            const info = searcher.findUint(ip >>> 0);
            return `${ipStr}|${geoToPipe(info)}`;
        });
        fs.writeFileSync(v4Out, v4Results.join('\n') + '\n');
        console.error(`  Node.js V4: ${v4Results.length} queries`);
    }

    // Process V6
    if (fs.existsSync(v6Test)) {
        const v6Ips = fs.readFileSync(v6Test, 'utf8').trim().split('\n').filter(l => l.trim());
        const v6Results = v6Ips.map(line => {
            const [high, low] = line.split(':').map(BigInt);
            const info = searcher.findV6(high, low);
            return `${line}|${geoToPipe(info)}`;
        });
        fs.writeFileSync(v6Out, v6Results.join('\n') + '\n');
        console.error(`  Node.js V6: ${v6Results.length} queries`);
    }

    console.error('  Node.js DONE');
}

main();