<?php
/**
 * QzdbSearcher - PHP SDK calling example
 *
 * Usage: php test.php
 * Place qqzeng_ip_std_china.qzdb in the same directory or specify the path.
 */

require_once __DIR__ . '/QzdbSearcher.php';
use Qqzeng\Ip\QzdbSearcher;

function findDb() {
    $candidates = [
        'qqzeng_ip_std_china.qzdb',
        '../data/qqzeng_ip_std_china.qzdb',
        'data/qqzeng_ip_std_china.qzdb',
    ];
    foreach ($candidates as $c) {
        if (file_exists($c)) return $c;
    }
    return null;
}

ini_set('memory_limit', '256M');
$dbPath = findDb();
if (!$dbPath) {
    echo "Database file not found\n";
    exit(1);
}

$searcher = QzdbSearcher::getInstance($dbPath);
$fields = $searcher->getFieldNames();
echo "Fields (" . count($fields) . "): " . implode(', ', $fields) . "\n\n";

// Query sample V4 IPs
foreach (['114.114.114.114', '223.5.5.5', '8.8.8.8'] as $ip) {
    $result = $searcher->findStr($ip);
    echo "find(\"{$ip}\") => " . ($result ?: '(null)') . "\n";
}

// Query a V6 IP
$result = $searcher->findStr('2408:8000:9000::1');
echo "find(\"2408:8000:9000::1\") => " . ($result ?: '(null)') . "\n";

// Get structured fields
echo "\n--- Structured fields for 114.114.114.114 ---\n";
$loc = $searcher->find('114.114.114.114');
if ($loc) {
    foreach ($fields as $name) {
        echo "  {$name}: {$loc[$name]}\n";
    }
}
echo "TEST_PASS\n";
