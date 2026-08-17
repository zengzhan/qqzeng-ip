<?php
/**
 * 回归测试：跳表条目带 SENTINEL 时，find / lookupRowId 路径必须**直接返回**
 * 低 31 位的 row_id（QZDB_FORMAT.md §4 SearchV4 / SearchV6）。
 *
 * 历史缺陷（cbd6e52 引入）：PHP 的 trieWalkV6 曾在跳表哨兵命中时"从根节点重走"，
 * 归因注释写的是 "matches Rust ref"——但 Rust 恰是离群实现，规范与多数派
 * （C/Java/C#/Node/Python）都是直接返回。本测试通过**定向篡改跳表哨兵**构造
 * 两种语义可区分的文件：规范语义返回哨兵 row_id（= A 的结果），
 * 旧的"从根重走"语义返回 trie 自身结果（= B 的结果）。
 * CIDR 反查（lookupCidr）需要前缀长度，从根重走是其合法实现，不在本测试范围。
 *
 * 运行：php jump_sentinel_test.php  （依赖 ../data/qqzeng_ip_std_china.qzdb）
 */
require_once __DIR__ . '/QzdbReader.php';
use Qqzeng\Ip\QzdbBuilder;

$passed = 0;
$failed = 0;
function check(bool $cond, string $msg): void
{
    global $passed, $failed;
    if ($cond) {
        $passed++;
    } else {
        $failed++;
        echo "  FAIL: {$msg}\n";
    }
}

function readU64(string $b, int $off): int
{
    $v = unpack('P', $b, $off)[1];
    return (int)$v;
}

function putU32Str(string $b, int $off, int $v): string
{
    $p = pack('V', $v);
    $b[$off] = $p[0]; $b[$off + 1] = $p[1]; $b[$off + 2] = $p[2]; $b[$off + 3] = $p[3];
    return $b;
}

$dbPath = __DIR__ . '/../data/qqzeng_ip_std_china.qzdb';
if (!is_file($dbPath)) {
    echo "SKIP: 缺少 {$dbPath}\n";
    exit(0);
}
$base = file_get_contents($dbPath);

$offV4Jump = readU64($base, 64);
$offV6Jump = readU64($base, 80);
$v6JumpBits = ord($base[11]);
check($offV4Jump > 0, '基准库应含 V4 跳表');
check($offV6Jump > 0, '基准库应含 V6 跳表');
if ($failed > 0) exit(1);

/* ---------------- V4：跳表哨兵直接返回叶子 row_id ---------------- */
$ipA = (114 << 24) | (114 << 16) | (114 << 8) | 114;  // 114.114.114.114
$ipB = (223 << 24) | (5 << 16) | (5 << 8) | 5;        // 223.5.5.5

$clean = QzdbBuilder::bytes($base)->verifyCrc(false)->build();
$rowA = $clean->lookupRowIdUint($ipA);
$pipeA = $clean->findUint($ipA)?->toPipe() ?? '';
$pipeB = $clean->findUint($ipB)?->toPipe() ?? '';
check($rowA !== 0, 'V4 前置：IP A 应命中');
check($pipeA !== $pipeB, 'V4 前置：A 与 B 的 geo 结果应不同');

$mutated = putU32Str($base, $offV4Jump + (($ipB >> 16) & 0xFFFF) * 4, 0x80000000 | $rowA);
$reader = QzdbBuilder::bytes($mutated)->verifyCrc(false)->build();
check(
    $reader->lookupRowIdUint($ipB) === $rowA,
    'V4 跳表哨兵必须直接返回低 31 位 row_id'
);
check(
    ($reader->findUint($ipB)?->toPipe() ?? '') === $pipeA,
    'V4 跳表哨兵命中后 find 结果必须等于哨兵 row_id 的 geo'
);

/* ---------------- V6：跳表哨兵直接返回叶子 row_id ---------------- */
$aBin = pack('H*', '24088000900000000000000000000001');  // 2408:8000:9000::1
$bBin = pack('H*', '20010db8000000000000000000000001');  // 2001:db8::1
$v6Prefix = static function (string $bin, int $bits): int {
    $val = 0;
    for ($i = 0; $i < $bits; $i++) {
        $val = ($val << 1) | ((ord($bin[$i >> 3]) >> (7 - ($i & 7))) & 1);
    }
    return $val;
};
$idxA = $v6Prefix($aBin, $v6JumpBits);
$idxB = $v6Prefix($bBin, $v6JumpBits);
check($idxA !== $idxB, 'V6 前置：A 与 B 应落在不同跳表桶');
if ($failed > 0) exit(1);

$rowA6 = $clean->lookupRowIdV6($aBin);
$pipeA6 = $clean->findV6Bin($aBin)?->toPipe() ?? '';
$pipeB6 = $clean->findV6Bin($bBin)?->toPipe() ?? '';
check($rowA6 !== 0, 'V6 前置：IP A 应命中');
check($pipeA6 !== $pipeB6, 'V6 前置：A 与 B 的 geo 结果应不同');

$mutated6 = putU32Str($base, $offV6Jump + $idxB * 4, 0x80000000 | $rowA6);
$reader6 = QzdbBuilder::bytes($mutated6)->verifyCrc(false)->build();
check(
    $reader6->lookupRowIdV6($bBin) === $rowA6,
    'V6 跳表哨兵必须直接返回低 31 位 row_id（不得从根重走）'
);
check(
    ($reader6->findV6Bin($bBin)?->toPipe() ?? '') === $pipeA6,
    'V6 跳表哨兵命中后 find 结果必须等于哨兵 row_id 的 geo'
);

echo "jump_sentinel_test: {$passed} passed, {$failed} failed\n";
exit($failed > 0 ? 1 : 0);
