<?php
require_once __DIR__ . '/QzdbSearcher.php';

if ($argc < 2) exit(1);
$dbPath = $argv[1];

$searcher = new \Qqzeng\Ip\QzdbSearcher();
$searcher->load($dbPath);

$handle = fopen("php://stdin", "r");
if ($handle) {
    while (($line = fgets($handle)) !== false) {
        $ip = trim($line);
        if ($ip === '') continue;
        $res = $searcher->findStr($ip);
        echo ($res !== null ? $res : '') . "\n";
    }
    fclose($handle);
}
