<?php
/**
 * QZDB PHP 性能基准
 *
 * 用法：php bench_all.php
 * 说明：V4 走 findUint；V6 走 findV6Bin（16 字节原始二进制）。
 *       随机 IP 大多未命中，更接近真实"缓存最不利"场景。
 */
require_once __DIR__ . '/QzdbReader.php';
use Qqzeng\Ip\QzdbReader;

function bench(string $name, string $dbPath, int $count, int $v6count): void
{
    if (!file_exists($dbPath)) {
        echo "  {$name}: not found ({$dbPath})\n";
        return;
    }
    $s = new QzdbReader($dbPath, 0);
    echo "--- {$name} ---\n";
    echo "  fields: " . count($s->getFieldNames()) . ", edition: " . $s->getEdition() . "\n";

    // V4
    $t0 = microtime(true);
    $hit = 0;
    for ($i = 0; $i < $count; $i++) {
        $ip = rand(0, 0x7FFFFFFF) | (rand(0, 0x7FFFFFFF) << 31 & 0xFFFFFFFF);
        $ip = $ip & 0xFFFFFFFF;
        $r = $s->findUint($ip);
        if ($r !== null) $hit++;
    }
    $v4qps = (int)($count / (microtime(true) - $t0));
    echo "  V4: " . number_format($v4qps) . " QPS  (命中 {$hit}/{$count})\n";

    // V6（16 字节原始二进制）
    $t1 = microtime(true);
    $hit6 = 0;
    for ($i = 0; $i < $v6count; $i++) {
        $b = '';
        for ($j = 0; $j < 16; $j++) {
            $b .= chr(rand(0, 255));
        }
        $r = $s->findV6Bin($b);
        if ($r !== null) $hit6++;
    }
    $v6qps = (int)($v6count / (microtime(true) - $t1));
    echo "  V6: " . number_format($v6qps) . " QPS  (命中 {$hit6}/{$v6count})\n";
}

$count = 3000000;
$v6count = 1000000;
echo "PHP QZDB Benchmarks\n";
bench('std_china', __DIR__ . '/../data/qqzeng_ip_std_china.qzdb', $count, $v6count);
bench('ult_china', __DIR__ . '/../data/qqzeng_ip_ult_china.qzdb', $count, $v6count);
