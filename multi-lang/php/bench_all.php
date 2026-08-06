<?php
require_once __DIR__ . '/QzdbReader.php';
use Qqzeng\Ip\QzdbReader;

function bench($name, $dbPath, $count, $v6count, $first) {
    if (!file_exists($dbPath)) { echo "  $name: not found\n"; return; }
    if ($first) $s = QzdbReader::getInstance($dbPath);
    else { $s = QzdbReader::getInstance(); $s->load($dbPath); }

    srand(123);
    $start = microtime(true);
    for ($i = 0; $i < $count; $i++) { $s->findUint(rand(0, 4294967295)); }
    $v4qps = floor($count / (microtime(true) - $start));

    srand(456);
    $v6start = microtime(true);
    for ($i = 0; $i < $v6count; $i++) {
        $high = (rand() & 0x7FFFFFFF) << 32 | (rand() & 0xFFFFFFFF);
        $low = (rand() & 0xFFFFFFFF) << 32 | (rand() & 0xFFFFFFFF);
        $s->findV6($high, $low);
    }
    $v6qps = floor($v6count / (microtime(true) - $v6start));
    printf("  %-12s V4 QPS: %d  V6 QPS: %d\n", $name, $v4qps, $v6qps);
}

$count = 3000000; $v6count = 1000000;
echo "PHP QPS Benchmarks (M4 Pro)\n";
bench('std_china', '../data/qqzeng_ip_std_china.qzdb', $count, $v6count, true);
bench('max_china', '../data/qqzeng_ip_max_china.qzdb', $count, $v6count, false);
bench('max_global', '../data/qqzeng_ip_max_global.qzdb', $count, $v6count, false);
