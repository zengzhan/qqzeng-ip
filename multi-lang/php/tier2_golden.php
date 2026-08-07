<?php
/**
 * Tier2 黄金校验（强制 0 失败，契约 §10）：读取 golden_vectors.json，
 * 对 std_china 与 ult_china 加载对应数据库，断言 find(ip)->toPipe() === expected。
 * 未命中 / 非法 IP 统一映射为 ""。
 *
 * 运行：php tier2_golden.php
 */
require_once __DIR__ . '/QzdbReader.php';
use Qqzeng\Ip\QzdbReader;

$dataDir = __DIR__ . '/../data';
$golden = json_decode(file_get_contents(__DIR__ . '/../tools/golden_vectors.json'), true);

$dbMap = [
    'std_china' => $dataDir . '/qqzeng_ip_std_china.qzdb',
    'ult_china' => $dataDir . '/qqzeng_ip_ult_china.qzdb',
];

$readers = [];
foreach ($dbMap as $key => $file) {
    if (!file_exists($file)) {
        echo "SKIP {$key}: file not found: {$file}\n";
        continue;
    }
    $readers[$key] = new QzdbReader($file, 0);
}

$total = 0;
$fail = 0;
$failSamples = [];
foreach ($golden as $dbKey => $sec) {
    if (!isset($readers[$dbKey])) continue;
    $reader = $readers[$dbKey];
    foreach (['random_v4', 'random_v6', 'boundary_v4', 'boundary_v6', 'invalid'] as $kind) {
        if (!isset($sec[$kind])) continue;
        foreach ($sec[$kind] as $rec) {
            $ip = $rec['ip'];
            $exp = $rec['expected'] ?? '';
            $r = $reader->find($ip);
            $got = ($r === null) ? '' : $r->toPipe();
            $total++;
            if ($got !== $exp) {
                $fail++;
                if ($fail <= 30) {
                    $failSamples[] = "[{$dbKey}/{$kind}] ip={$ip}\n   exp=" . var_export($exp, true) . "\n   got=" . var_export($got, true);
                }
            }
        }
    }
}

foreach ($failSamples as $s) {
    echo $s . "\n";
}
echo "\nTier2 GOLDEN: TOTAL={$total} FAIL={$fail}\n";
if ($fail === 0) {
    echo "TIER2_OK (0 failures)\n";
    exit(0);
}
exit(1);
