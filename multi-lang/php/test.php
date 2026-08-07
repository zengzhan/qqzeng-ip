<?php
/**
 * QzdbReader — PHP SDK 调用示例
 *
 * 用法：php test.php
 * 把 qqzeng_ip_std_china.qzdb 放到本目录或 ../data 下。
 */
require_once __DIR__ . '/QzdbReader.php';
use Qqzeng\Ip\QzdbReader;

function findDb(): ?string
{
    $candidates = [
        'qqzeng_ip_std_china.qzdb',
        '../data/qqzeng_ip_std_china.qzdb',
        __DIR__ . '/../data/qqzeng_ip_std_china.qzdb',
    ];
    foreach ($candidates as $c) {
        if (file_exists($c)) return $c;
    }
    return null;
}

$dbPath = findDb();
if (!$dbPath) {
    echo "Database file not found\n";
    exit(1);
}

// 推荐：Builder 模式加载
$searcher = Qqzeng\Ip\QzdbBuilder::path($dbPath)->build();

echo "Edition: " . $searcher->getEdition() . "\n";
echo "DataMonth: " . $searcher->getDataMonth() . "\n";
echo "Fields (" . count($searcher->getFieldNames()) . "): " . implode(', ', $searcher->getFieldNames()) . "\n\n";

// 单次查询（命中返回 GeoInfo，未命中返回 null）
foreach (['114.114.114.114', '223.5.5.5', '8.8.8.8'] as $ip) {
    $result = $searcher->find($ip);
    echo "find(\"{$ip}\") => " . ($result ? $result->toPipe() : '(null)') . "\n";
}

// 管道符格式（未命中返回 ""，适合落库 / 日志）
echo "findStr(\"240e:390:1:1::1\") => " . $searcher->findStr('240e:390:1:1::1') . "\n";

// 结构化取值 + 语义 Getter
echo "\n--- Structured for 114.114.114.114 ---\n";
$loc = $searcher->find('114.114.114.114');
if ($loc) {
    echo "  country=" . $loc->getCountry() . ", province=" . $loc->getProvince() . ", city=" . $loc->getCity() . "\n";
    echo "  usage=" . $loc->getUsageType()->getDisplayZh() . "\n";
}

// CIDR 反查
echo "\nlookupCidr(114.114.114.114) => " . var_export($searcher->lookupCidr('114.114.114.114'), true) . "\n";

// 元信息
echo "fileHash=" . $searcher->getFileHash() . ", verifyCrc=" . var_export($searcher->verifyCrc(), true) . "\n";

echo "\nTEST_PASS\n";
