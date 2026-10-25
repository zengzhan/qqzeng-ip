<?php

/** qqzeng-ip 2.0 (.dat) PHP 版冒烟测试。 */

require __DIR__ . '/IpSearch.php';

$dbPath = IpSearch::findDbPath();
if ($dbPath === null) {
    echo "no .dat found, skip\n";
    exit(0);
}
$finder = new IpSearch($dbPath);

$ip = '58.213.198.68';
echo $ip, "\n", $finder->get($ip), "\n";
var_dump($finder->get('8.8.8.8') === '' || $finder->get('8.8.8.8') !== false);
var_dump($finder->get('abc.def') === '');
