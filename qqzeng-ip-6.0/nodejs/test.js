const IpDbSearch = require('./lib/IpDbSearch');
const fs = require('fs');
const path = require('path');

function main() {
    console.log("正在初始化 qqzeng-ip 数据库...");
    let searcher;
    try {
        searcher = IpDbSearch.getInstance();
    } catch (e) {
        console.warn("⚠️ 数据库加载失败，跳过测试 (CI环境下正常): " + e.message);
        return;
    }
    console.log(`数据库加载完成`);

    const testFile = findTestFile();
    if (testFile) {
        verifyWithFile(searcher, testFile);
    }

    // --- 随机压测 ---
    const totalCount = 3000000;
    console.log(`\n生成 ${totalCount} 个随机 IP (UInt32)...`);
    const randomIps = generateRandomIps(totalCount);
    console.log("生成完成，开始压测 (findUint)...");

    // GC? Node没有强制GC，只能依靠V8自己
    const benchStart = process.hrtime();

    for (let i = 0; i < totalCount; i++) {
        searcher.findUint(randomIps[i]);
    }

    const [bSeconds, bNanoseconds] = process.hrtime(benchStart);
    const benchElapsedMs = bSeconds * 1000 + bNanoseconds / 1000000;
    const benchSecondsVal = benchElapsedMs / 1000;

    console.log(`${totalCount} 次随机查询耗时: ${benchElapsedMs.toFixed(2)} ms`);
    console.log(`QPS: ${(totalCount / benchSecondsVal).toFixed(2)}`);
}

function generateRandomIps(count) {
    // 使用 TypedArray 节省内存
    const ips = new Uint32Array(count);
    let seed = 123;
    for (let i = 0; i < count; i++) {
        // LCG
        seed = Math.imul(seed, 1664525) + 1013904223;
        ips[i] = seed >>> 0;
    }
    return ips;
}

function verifyWithFile(searcher, path) {
    console.log(`正在读取测试文件: ${path}`);
    const content = fs.readFileSync(path, 'utf8');
    const lines = content.split('\n').filter(line => line.trim().length > 0);
    let passed = 0;
    for (const line of lines) {
        const parts = line.split('\t');
        if (parts.length < 3) continue;
        if (searcher.find(parts[0]) === parts[2] && searcher.find(parts[1]) === parts[2]) {
            passed++;
        }
    }
    console.log(`验证完成: ${passed}/${lines.length} 通过`);
}

function findTestFile() {
    const attempts = [
        path.join(__dirname, '../data/test.txt'),
        path.join(__dirname, '../../data/test.txt'),
        '../data/test.txt'
    ];
    for (const p of attempts) {
        if (fs.existsSync(p)) return p;
    }
    return null;
}

main();
