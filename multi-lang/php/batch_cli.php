<?php
/**
 * 批量查询 CLI：从标准输入逐行读取 IP，输出 toPipe() 结果（未命中输出空行）。
 *
 * 用法：cat ips.txt | php batch_cli.php /path/to/db.qzdb
 */
require_once __DIR__ . '/QzdbReader.php';
use Qqzeng\Ip\QzdbReader;
use Qqzeng\Ip\QzdbBuilder;

if ($argc < 2) {
    fwrite(STDERR, "usage: php batch_cli.php <db.qzdb>\n");
    exit(1);
}
$dbPath = $argv[1];

$searcher = QzdbBuilder::path($dbPath)->build();

$handle = fopen('php://stdin', 'r');
if ($handle) {
    while (($line = fgets($handle)) !== false) {
        $ip = trim($line);
        if ($ip === '') continue;
        $res = $searcher->findStr($ip);
        echo $res . "\n";
    }
    fclose($handle);
}
