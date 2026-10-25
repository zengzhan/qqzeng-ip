<?php

/** qqzeng-ip 6.0 (.db) PHP 版冒烟测试。 */

declare(strict_types=1);

require_once __DIR__ . '/src/IpDbSearch.php';
require_once __DIR__ . '/src/IpDbSearchOptimized.php';

/* 同时持有两个实例做对比, 预先抬高内存上限 */
if ((string) ini_get('memory_limit') !== '-1') {
    ini_set('memory_limit', '1024M');
}

use Qqzeng\Ip\IpDbSearch;
use Qqzeng\Ip\IpDbSearchOptimized;

function main()
{
    $path = IpDbSearch::findDbPath();
    if ($path === null) {
        echo "数据库未找到, 跳过\n";
        return;
    }
    $db = IpDbSearch::fromPath($path);
    printf("数据库加载完成: nodes=%d, strings=%d\n", $db->nodeCount(), $db->stringCount());

    foreach (['1.10.11.12', '8.8.8.8', '58.213.198.68', '255.255.255.255', '1..2.3'] as $ip) {
        echo $ip, ' -> ', $db->find($ip), "\n";
    }

    assert(IpDbSearch::fastParseIp('1.2.3.4') === 0x01020304);
    assert(IpDbSearch::fastParseIp('1..2.3') === -1);
    assert($db->findUint(0x01020304) === $db->find('1.2.3.4'));
    assert($db->find('1.2.3.4.5') === '');

    // from_bytes 独立实例
    $db2 = IpDbSearch::fromBytes((string) file_get_contents($path));
    assert($db2->find('8.8.8.8') === $db->find('8.8.8.8'));

    // 弃用别名转发
    assert(IpDbSearchOptimized::getInstance() instanceof IpDbSearch);
    echo "ok\n";
}

main();
