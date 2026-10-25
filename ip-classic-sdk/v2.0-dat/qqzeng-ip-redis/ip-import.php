<?php

/**
 * qqzeng-ip 2.0 (.dat) -> Redis ZSET 批量导入工具。
 *
 * 用法:
 *   php ip-import.php <qqzeng-ip.dat> [redis_uri]
 *   php ip-import.php qqzeng-ip-global-utf8.dat   # 默认 127.0.0.1:6379
 *
 * 数据结构: ZSET key=qqzengip, score=区间结束IP(uint32), member=地区字符串。
 * 查询: zRangeByScore('qqzengip', $ip, '+inf', ['limit' => [0, 1]]) 取第一条。
 *
 * 相比历史版本的改进:
 *   1. 直接读取 .dat 二进制(旧版依赖外部明文 txt),与 SDK 同源同口径。
 *   2. 修复 32/64 位 ip2long 负数陷阱(直接使用 uint32 记录值)。
 *   3. 修正 pipeline 未生效问题(每 1 万条 exec 一次分批提交)。
 */

require __DIR__ . '/../qqzeng-ip-php/IpSearch.php';

if ($argc < 2) {
    fwrite(STDERR, "usage: php ip-import.php <qqzeng-ip.dat> [redis_uri]\n");
    exit(2);
}
if (!class_exists('Redis')) {
    fwrite(STDERR, "phpredis extension not loaded (php -m | grep redis); nothing to do\n");
    exit(77); /* graceful skip: no redis client in this environment */
}
$datPath = $argv[1];
$redisUri = $argv[2] ?? '127.0.0.1:6379';
$key = 'qqzengip';

list($host, $port) = array_pad(explode(':', $redisUri, 2), 2, 6379);
$redis = new Redis();
$redis->connect($host, (int)$port);

$searcher = new IpSearch($datPath);
echo 'records: ', $searcher->recordCount(), "\n";

$redis->multi(Redis::PIPELINE);
$redis->del([$key]);

$i = 0;
foreach ($searcher->records() as $start => [$end, $region]) {
    $redis->zAdd($key, (int)$end, $region); /* score = 区间结束 IP (uint32) */
    if (++$i % 10000 === 0) {
        $redis->exec();
        $redis->multi(Redis::PIPELINE);
        echo $i, "\n";
    }
}
$redis->exec();

echo 'count: ', $redis->zCount($key, '-inf', '+inf'), "\n";
$last = $redis->zRangeByScore($key, 4294967295, 4294967295, ['limit' => [0, 1]]);
echo 'last: ', $last[0] ?? '(none)', "\n";
$redis->close();
