<?php

require_once __DIR__ . '/src/IpDbSearch.php';

use Qqzeng\Ip\IpDbSearch;

function main() {
    echo "正在初始化 qqzeng-ip 数据库...\n";
    $start = microtime(true);
    try {
        $searcher = IpDbSearch::getInstance();
    } catch (Exception $e) {
        echo "Error: " . $e->getMessage() . "\n";
        return;
    }
    $elapsed = (microtime(true) - $start) * 1000;
    echo sprintf("数据库加载完成，耗时: %.2f ms\n", $elapsed);

    $testFile = findTestFile();
    if (!$testFile) {
        echo "无法找到测试文件\n";
        return;
    }

    echo "正在读取测试文件: $testFile\n";
    $lines = file($testFile, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
    echo "共有 " . count($lines) . " 条测试记录\n";

    $passed = 0;
    $failed = 0;

    // 预热
    $searcher->find("8.8.8.8");

    $testStart = microtime(true);
    foreach ($lines as $line) {
        $parts = explode("\t", $line);
        if (count($parts) < 3) continue;

        $startIp = $parts[0];
        $endIp = $parts[1];
        $expected = $parts[2];

        if (!verify($searcher, $startIp, $expected)) {
            $failed++;
            continue;
        }
        if (!verify($searcher, $endIp, $expected)) {
            $failed++;
            continue;
        }
        
        $midIp = getMidIp($startIp, $endIp);
        if (!verify($searcher, $midIp, $expected)) {
            $failed++;
            continue;
        }

        $passed++;
    }
    $testElapsed = (microtime(true) - $testStart) * 1000;

    echo "\n-------------------------------------------\n";
    echo "测试完成!\n";
    echo "总记录数: " . count($lines) . "\n";
    echo "通过: $passed\n";
    echo "失败: $failed\n";
    echo sprintf("总耗时: %.2f ms\n", $testElapsed);
    if (count($lines) > 0) {
        echo sprintf("平均耗时: %.4f ms/query\n", $testElapsed / (count($lines) * 3));
    }
    echo "-------------------------------------------\n";

    // 压测
    echo "\n开始性能压测 (1,000,000 次查询)...\n";
    $benchStart = microtime(true);
    for ($i = 0; $i < 1000000; $i++) {
        $searcher->find("1.0.0.1");
        $searcher->find("255.255.255.255");
        $searcher->find("114.114.114.114");
        $searcher->find("8.8.8.8");
    }
    $benchElapsed = microtime(true) - $benchStart;
    
    echo sprintf("4,000,000 次查询耗时: %.2f ms\n", $benchElapsed * 1000);
    echo sprintf("QPS: %.2f\n", 4000000 / $benchElapsed);
}

function verify($searcher, $ip, $expected) {
    // 预期结果可能包含Unicode字符，需要确保终端输出正确
    $result = $searcher->find($ip);
    if ($result !== $expected) {
        echo "[Fail] IP: $ip\n";
        echo "  期望: $expected\n";
        echo "  实际: $result\n";
        return false;
    }
    return true;
}

function findTestFile() {
    $attempts = [
        __DIR__ . '/../data/test.txt',
        __DIR__ . '/../../data/test.txt',
        __DIR__ . '/../../../data/test.txt',
        '../data/test.txt'
    ];
    foreach ($attempts as $path) {
        if (file_exists($path)) {
            return $path;
        }
    }
    return null;
}

function getMidIp($start, $end) {
    $sStart = ip2long($start);
    $eStart = ip2long($end);
    $s = ($sStart < 0) ? ($sStart + 4294967296) : $sStart;
    $e = ($eStart < 0) ? ($eStart + 4294967296) : $eStart;
    if ($s == $e) return $start;
    $mid = $s + floor(($e - $s) / 2);
    return long2ip((int)$mid);
}

main();
