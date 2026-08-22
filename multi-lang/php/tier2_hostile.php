<?php
/**
 * Tier2 敌对文件 fail-closed 校验（契约：tools/hostile_vectors.json）。
 *
 * 消费语言无关、单一事实来源的 hostile_vectors.json（29 个 case，自文档于其 _doc 键），
 * 对每个 case：
 *   1) 把真实 .qzdb 读入字节（只读；绝不修改磁盘上的文件）；
 *   2) 从【自身】解析出的 192 字节头解析锚点（不写死绝对偏移，因为 .qzdb 布局随文件变化）；
 *   3) 应用 mutation 配方（sweep 展开为多个变异副本）；
 *   4) 把每个变异副本喂给 SDK 的「从字节加载」入口，做【双模式】评估：
 *        - lenient  = verifyCrc=false（攻击者重算 CRC 的更深路径，类似 Rust 威胁模型）
 *        - strict   = verifyCrc=true（CRC 闸门，默认模式）
 *   5) 断言 fail-closed 契约：SDK 绝不能崩溃、绝不能挂起、绝不能返回「看似正确实则错误」的数据。
 *      拒绝（任意错误码）、优雅空结果、或 lenient 下虽错但正确的数据，都满足 fail-closed。
 *
 * 参考语义严格对齐 Java FailClosedHostileTest（双模式评估、分歧上报、group_index_invalid
 * 特殊行级攻击 craftInvalidEntryRow）。
 *
 * 本测试【绝不】修改生产 SDK 代码、其它 php 文件、run_all_tests.sh、hostile_vectors.json
 * 或 data/*.qzdb。
 *
 * 运行：php tier2_hostile.php
 * 退出：0 = HOSTILE_VECTORS_OK（全部 PASS/PASS*）；1 = HOSTILE_VECTORS_FAIL（存在 SDK 异常）。
 * 若基础库缺失：打印 notice 并 exit 0（优雅跳过）。
 */

declare(strict_types=1);

require_once __DIR__ . '/QzdbReader.php';
use Qqzeng\Ip\QzdbReader;
use Qqzeng\Ip\QzdbBuilder;
use Qqzeng\Ip\QzdbException;

// ---------------------------------------------------------------------------
// 全局时间 / 内存护栏：病态输入必须快速失败，而非挂起或 OOM。
// ---------------------------------------------------------------------------
ini_set('memory_limit', '1024M');
set_time_limit(600);

// 单副本评估超时（秒）。超过即判定为 HANG（借助 pcntl_alarm 实现，无 pcntl 时退化为全局护栏）。
const EVAL_TIMEOUT = 15;

// 每个变异副本都针对这些 IP 做查询，用于检测「错误数据」（与基线不一致的非空结果）。
const TEST_IPS = [
    '223.5.5.5', '114.114.114.114', '1.0.1.0', '8.8.8.8',
    '0.0.0.0', '255.255.255.255', '240e:390:1:1::1', '::ffff:223.5.5.5',
];

// ---------------------------------------------------------------------------
// 挂起检测：pcntl_alarm + 异步信号，在超时处抛出，标记 HANG 而非无限等待。
// ---------------------------------------------------------------------------
class HostileHangException extends \Exception {}

$hasPcntl = function_exists('pcntl_signal') && function_exists('pcntl_alarm') && function_exists('pcntl_async_signals');
if ($hasPcntl) {
    pcntl_async_signals(true);
    pcntl_signal(SIGALRM, static function (): void {
        throw new HostileHangException('evaluation exceeded timeout (possible hang)');
    });
}

// ---------------------------------------------------------------------------
// 小端读取 / 写入原语（与 Java 解析器逐字对齐）
// ---------------------------------------------------------------------------
function ru16(string $b, int $off): int
{
    if ($off < 0 || $off + 2 > strlen($b)) return 0;
    return unpack('v', $b, $off)[1];
}

function ru32(string $b, int $off): int
{
    if ($off < 0 || $off + 4 > strlen($b)) return 0;
    return unpack('V', $b, $off)[1];
}

function ru48(string $b, int $off): int
{
    $v = 0;
    for ($k = 0; $k < 6; $k++) {
        $v |= (ord($b[$off + $k]) & 0xFF) << (8 * $k);
    }
    return $v;
}

function ru64(string $b, int $off): int
{
    $v = 0;
    for ($k = 0; $k < 8; $k++) {
        $v |= (ord($b[$off + $k]) & 0xFF) << (8 * $k);
    }
    return $v;
}

function writeLE(string &$b, int $off, int $width, int $value): void
{
    for ($k = 0; $k < $width; $k++) {
        $b[$off + $k] = chr(($value >> (8 * $k)) & 0xFF);
    }
}

// ---------------------------------------------------------------------------
// 错误码映射：PHP 原生码 -> 规范跨语言族名
// ---------------------------------------------------------------------------
function mapCode(int $c): string
{
    switch ($c) {
        case QzdbReader::ERROR_BAD_MAGIC:      return 'BadMagic';
        case QzdbReader::ERROR_BAD_HEADER:     return 'BadHeader';
        case QzdbReader::ERROR_UNSUPPORTED:    return 'Unsupported';
        case QzdbReader::ERROR_CORRUPTED:      return 'Corrupted';
        case QzdbReader::ERROR_INVALID_PARAM:  return 'InvalidParam';
        case QzdbReader::ERROR_OUT_OF_BOUNDS:  return 'OutOfBounds';
        case QzdbReader::ERROR_NOT_FOUND:      return 'NotFound';
        default:                               return 'Code' . $c;
    }
}

function norm(string $s): string
{
    $s = strtolower($s);
    return preg_replace('/[^a-z0-9]/', '', $s);
}

function describeObs(array $obsCodes, bool $sawGraceful, bool $sawCorrect): string
{
    $parts = [];
    if (!empty($obsCodes)) {
        $parts[] = 'rejected:' . implode('/', $obsCodes);
    }
    if ($sawGraceful) {
        $parts[] = 'graceful-empty';
    }
    if ($sawCorrect) {
        $parts[] = 'correct(lenient)';
    }
    if (empty($parts)) {
        $parts[] = '?';
    }
    return implode(' | ', $parts);
}

// ---------------------------------------------------------------------------
// 规范 CRC32 重算（与 SDK 的 crc32bComputeFile 同一算法：CRC-32/ISO-HDLC）。
// 偏移 16..20 按规范计为零。用于 group_index_invalid 特殊攻击，使双模式都能加载。
// ---------------------------------------------------------------------------
function recomputeCrc(string $buf): int
{
    $zeroed = $buf;
    $zeroed[16] = "\0";
    $zeroed[17] = "\0";
    $zeroed[18] = "\0";
    $zeroed[19] = "\0";
    return crc32($zeroed) & 0xFFFFFFFF;
}

// ---------------------------------------------------------------------------
// 单副本评估（双模式各调一次）。返回结构化结果。
// ---------------------------------------------------------------------------
function evaluate(string $copy, bool $verifyCrc, array $baseline): array
{
    global $hasPcntl;
    $res = [
        'opened'        => false,
        'code'          => null,
        'crashed'       => false,
        'hang'          => false,
        'wrongData'     => false,
        'detail'        => '',
        'wrongExample'  => null,
    ];

    if ($hasPcntl) {
        pcntl_alarm(EVAL_TIMEOUT);
    }
    try {
        $reader = QzdbBuilder::bytes($copy)->verifyCrc($verifyCrc)->build();
        $res['opened'] = true;

        $anyNonEmpty = false;
        $anyWrong = false;
        foreach (TEST_IPS as $ip) {
            try {
                $got = $reader->findStr($ip);
            } catch (HostileHangException $e) {
                throw $e; // 挂起必须冒泡到外层，不能当 $got='' 继续
            } catch (\Throwable $e) {
                $got = '';
            }
            if ($got === null) {
                $got = '';
            }
            $exp = $baseline[$ip] ?? '';
            if ($got !== '') {
                $anyNonEmpty = true;
                if ($got !== $exp) {
                    $anyWrong = true;
                    if ($res['wrongExample'] === null) {
                        $res['wrongExample'] = "ip={$ip} base=[" . substr($exp, 0, 80) . "] got=[" . substr($got, 0, 80) . "]";
                    }
                }
            }
        }

        $res['wrongData'] = $anyWrong;
        if ($res['wrongData']) {
            $res['detail'] = 'WRONG-DATA';
        } elseif (!$anyNonEmpty) {
            $res['detail'] = 'graceful-empty';
        } else {
            $res['detail'] = 'correct(lenient)';
        }

        try {
            $reader->close();
        } catch (\Throwable $e) {
            // 忽略关闭期异常
        }
        unset($reader);
    } catch (QzdbException $e) {
        $res['code'] = mapCode($e->getCode());
        $res['detail'] = 'rejected:' . $res['code'];
    } catch (HostileHangException $e) {
        $res['hang'] = true;
        $res['detail'] = 'HANG';
    } catch (\Throwable $e) {
        $res['crashed'] = true;
        $res['detail'] = 'CRASH:' . get_class($e);
    } finally {
        if ($hasPcntl) {
            pcntl_alarm(0);
        }
    }

    return $res;
}

// ---------------------------------------------------------------------------
// 变异引擎：每个生成的副本通过 sink 回调【立即】评估并丢弃（单副本在内存，避免 752 份克隆爆内存）。
// ---------------------------------------------------------------------------
function applyHeaderField(string $base, array $mut): string
{
    $off = (int) $mut['offset'];
    $width = (int) $mut['width'];
    $value = (int) $mut['value'];
    $mask = isset($mut['mask']) ? (int) $mut['mask'] : null;
    $cp = $base; // 写时复制，仅在首次修改时真正克隆
    $len = strlen($cp);

    if ($width === 48) {
        if ($off < 0 || $off + 6 > $len) {
            return $cp; // 越界跳过
        }
        $cur = ru48($cp, $off);
        $nv = ($mask !== null) ? ($cur ^ $mask) : $value;
        for ($k = 0; $k < 6; $k++) {
            $cp[$off + $k] = chr(($nv >> (8 * $k)) & 0xFF);
        }
        return $cp;
    }

    if ($off < 0 || $off + $width > $len) {
        return $cp; // 越界跳过
    }
    $cur = 0;
    switch ($width) {
        case 1: $cur = ord($cp[$off]); break;
        case 2: $cur = ru16($cp, $off); break;
        case 4: $cur = ru32($cp, $off); break;
        case 8: $cur = ru64($cp, $off); break;
        default: return $cp;
    }
    $nv = ($mask !== null) ? ($cur ^ $mask) : $value;
    writeLE($cp, $off, $width, $nv);
    return $cp;
}

// 32-bit-safe LCG (glibc constants). Product of two 32-bit values with this
// multiplier stays within PHP's 64-bit int range, so no float-overflow warning.
function lcg32(int $state): int
{
    return (((int) ($state & 0xFFFFFFFF)) * 1103515245 + 12345) & 0xFFFFFFFF;
}

function fillRandom(string $cp, int $start, int $len, int $seed): string
{
    $state = $seed & 0xFFFFFFFF;
    for ($k = $start; $k < $start + $len; $k++) {
        $state = lcg32($state);
        $cp[$k] = chr($state & 0xFF);
    }
    return $cp;
}

function applyMutation(string $base, array $mut, array $anchors, string &$log, callable $sink): void
{
    $type = $mut['type'];
    $len = strlen($base);

    switch ($type) {
        case 'header_field':
            $sink(applyHeaderField($base, $mut));
            break;

        case 'header_byte_sweep': {
            $start = (int) $mut['start'];
            $end = (int) $mut['end'];
            foreach ($mut['patterns'] as $po) {
                $pat = (int) ($po & 0xFF);
                for ($off = $start; $off < $end; $off++) {
                    if ($off < 0 || $off >= $len) {
                        continue;
                    }
                    $cp = $base;
                    $cp[$off] = chr($pat);
                    $sink($cp);
                }
            }
            break;
        }

        case 'header_field_sweep': {
            $width = (int) $mut['width'];
            $value = (int) $mut['value'];
            foreach ($mut['offsets'] as $oo) {
                $off = (int) $oo;
                if ($off < 0 || $off + $width > $len) {
                    $log .= "skip header_field_sweep off={$off} oob\n";
                    continue;
                }
                $cp = $base;
                writeLE($cp, $off, $width, $value);
                $sink($cp);
            }
            break;
        }

        case 'truncate': {
            if (isset($mut['bytes'])) {
                $l = (int) $mut['bytes'];
                if ($l >= 0 && $l < $len) {
                    $sink(substr($base, 0, $l));
                }
            } else {
                $mode = $mut['mode'];
                if ($mode === 'to_zero') {
                    $lengths = [0];
                } elseif ($mode === 'below_header') {
                    $lengths = [100];
                } elseif ($mode === 'at_header') {
                    $lengths = [191];
                } else { // sweep：几何长度 [0,1,2,4,...,file_size]
                    $lengths = [0];
                    $l = 1;
                    while ($l <= $len) {
                        $lengths[] = $l;
                        if ($l === $len) {
                            break;
                        }
                        $l *= 2;
                    }
                }
                foreach ($lengths as $l) {
                    if ($l >= 0 && $l <= $len) {
                        $sink(substr($base, 0, $l));
                    }
                }
            }
            break;
        }

        case 'append_junk': {
            $length = (int) $mut['length'];
            $fill = $mut['fill'];
            $cp = $base . str_repeat("\0", $length);
            if ($fill === '0xFF') {
                for ($k = $len; $k < $len + $length; $k++) {
                    $cp[$k] = "\xFF";
                }
            } elseif ($fill === 'zeros') {
                // 已用 \0 填充，无需处理
            } else { // random（确定性种子，可复现）
                $cp = fillRandom($cp, $len, $length, 0x1234ABCD);
            }
            $sink($cp);
            break;
        }

        case 'section_mutate': {
            $anchor = $mut['anchor'];
            $span = (int) $mut['span'];
            if (!isset($anchors[$anchor])) {
                $log .= "skip section_mutate anchor={$anchor} unresolved\n";
                break;
            }
            $aoff = (int) $anchors[$anchor];
            if ($aoff < 0 || $aoff >= $len) {
                $log .= "skip section_mutate anchor={$anchor} out of range\n";
                break;
            }
            foreach ($mut['patterns'] as $po) {
                $pat = (int) ($po & 0xFF);
                $cp = $base;
                $limit = min($span, $len - $aoff);
                for ($k = 0; $k < $limit; $k++) {
                    $cp[$aoff + $k] = chr($pat);
                }
                $sink($cp);
            }
            break;
        }

        case 'trie_nodes_fill': {
            $anchor = $mut['anchor'];
            $countField = $mut['count_field'];
            $value = (int) $mut['value'];
            $writeWidth = (int) $mut['write_width'];
            if (!isset($anchors[$anchor]) || !isset($anchors[$countField])) {
                $log .= "skip trie_nodes_fill unresolved\n";
                break;
            }
            $aoff = (int) $anchors[$anchor];
            $nodeCount = (int) $anchors[$countField];
            $flags = (int) $anchors['flags'];
            $stride = ($anchor === 'trie_v4_nodes_start')
                ? (($flags & 0x10) !== 0 ? 6 : 8)
                : (($flags & 0x20) !== 0 ? 6 : 8);
            $cp = $base;
            $n = min($nodeCount, intdiv($len, $stride) + 1);
            for ($i = 0; $i < $n; $i++) {
                $bo = $aoff + $i * $stride;
                if ($bo < 0 || $bo + $writeWidth + 4 > $len) {
                    break; // 有界，绝不越界写
                }
                writeLE($cp, $bo, 4, $value);
                writeLE($cp, $bo + $writeWidth, 4, $value);
            }
            $sink($cp);
            break;
        }

        case 'random_bitflips': {
            $seed = (int) $mut['seed'];
            $rounds = (int) $mut['rounds'];
            $maxFlips = (int) $mut['max_flips'];
            $spanObj = $mut['span'];
            $span = is_string($spanObj) ? $len : (int) $spanObj;
            if ($span > $len) {
                $span = $len;
            }
            $cp = $base;
            $state = $seed & 0xFFFFFFFF;
            for ($r = 0; $r < $rounds; $r++) {
                for ($f = 0; $f < $maxFlips; $f++) {
                    $state = lcg32($state);
                    $pos = $state % $span;
                    $bit = ($state >> 8) % 8;
                    if ($pos >= 0 && $pos < strlen($cp)) {
                        $cp[$pos] = chr(ord($cp[$pos]) ^ (1 << $bit));
                    }
                }
            }
            $sink($cp);
            break;
        }

        case 'crc_field_corrupt': {
            $cp = $base;
            $zeroed = $cp;
            $zeroed[16] = "\0";
            $zeroed[17] = "\0";
            $zeroed[18] = "\0";
            $zeroed[19] = "\0";
            $calc = crc32($zeroed) & 0xFFFFFFFF;
            $bad = ($calc ^ 0xFFFFFFFF) & 0xFFFFFFFF;
            writeLE($cp, 16, 4, $bad);
            $sink($cp);
            break;
        }

        case 'compound': {
            $cur = $base;
            foreach ($mut['steps'] as $so) {
                $stepOut = [];
                applyMutation($cur, $so, $anchors, $log, static function (string $c) use (&$stepOut): void {
                    $stepOut[] = $c;
                });
                if (!empty($stepOut)) {
                    $cur = $stepOut[0];
                }
            }
            $sink($cur);
            break;
        }

        default:
            $log .= "unknown mutation type: {$type}\n";
    }
}

/**
 * group_index_invalid 的真实行级攻击（对齐 Java craftInvalidEntryRow）：
 * 把首个 IPRow 区段整段置 0xFF（entryId 必然越界），并重算规范 CRC32 写回偏移 16，
 * 使 verifyCrc=true 也能加载成功——从而把考验从加载期推到查询期：SDK 必须优雅空或抛
 * 列内错误码，绝不允许崩溃、挂起或返回错误数据。
 */
function craftInvalidEntryRow(string $base, array $anchors): string
{
    $cp = $base;
    $iprowOff = (int) ($anchors['iprow_start'] ?? -1);
    $rowCount = ru32($cp, 20);
    $rowSize = ru32($cp, 160);
    $len = strlen($cp);
    if ($iprowOff <= 0 || $rowCount <= 1 || $rowSize <= 0 || $rowSize > 64
        || $iprowOff + (int) ($rowCount * $rowSize) > $len) {
        return $cp;
    }
    $rOff = $iprowOff;
    $span = (int) ($rowCount * $rowSize);
    $limit = min($span, $len - $rOff);
    for ($k = 0; $k < $limit; $k++) {
        $cp[$rOff + $k] = "\xFF";
    }
    $crc = recomputeCrc($cp);
    $cp[16] = chr($crc & 0xFF);
    $cp[17] = chr(($crc >> 8) & 0xFF);
    $cp[18] = chr(($crc >> 16) & 0xFF);
    $cp[19] = chr(($crc >> 24) & 0xFF);
    return $cp;
}

// ---------------------------------------------------------------------------
// 锚点解析（消费方解析【自身】头）
// ---------------------------------------------------------------------------
function parseHeaderOffsets(string $buf): array
{
    return [
        'row_schema_start'     => ru64($buf, 40),
        'group_schema_start'   => ru64($buf, 48),
        'trie_v4_jump_start'   => ru64($buf, 64),
        'trie_v4_nodes_start'  => ru64($buf, 72),
        'trie_v6_jump_start'   => ru64($buf, 80),
        'trie_v6_nodes_start'  => ru64($buf, 88),
        'iprow_start'          => ru64($buf, 96),
        'geo_entries_start'    => ru64($buf, 104),
        'pools_start'          => ru64($buf, 136),
        'meta_start'           => ru64($buf, 144),
        'flags'                => ru16($buf, 8),
        'v4_node_count'        => ru32($buf, 152),
        'v6_node_count'        => ru32($buf, 156),
    ];
}

// ---------------------------------------------------------------------------
// 资源定位（CWD 为 multi-lang/php 时由 run_all_tests.sh 驱动）
// ---------------------------------------------------------------------------
function locateBaseDb(): ?string
{
    $candidates = [
        __DIR__ . '/../data/qqzeng_ip_std_china.qzdb',
        __DIR__ . '/data/qqzeng_ip_std_china.qzdb',
        'data/qqzeng_ip_std_china.qzdb',
        '/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/data/qqzeng_ip_std_china.qzdb',
    ];
    foreach ($candidates as $c) {
        if (is_readable($c)) {
            return $c;
        }
    }
    return null;
}

function loadVector(): ?array
{
    $candidates = [
        __DIR__ . '/../tools/hostile_vectors.json',
        __DIR__ . '/tools/hostile_vectors.json',
        'tools/hostile_vectors.json',
        '/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/tools/hostile_vectors.json',
    ];
    foreach ($candidates as $c) {
        if (is_readable($c)) {
            $txt = file_get_contents($c);
            $dec = json_decode($txt, true);
            if (is_array($dec)) {
                return $dec;
            }
        }
    }
    return null;
}

// ---------------------------------------------------------------------------
// 主流程
// ---------------------------------------------------------------------------
$dbPath = locateBaseDb();
if ($dbPath === null) {
    echo "NOTICE: base database not found (expected multi-lang/data/qqzeng_ip_std_china.qzdb). Skipping.\n";
    echo "HOSTILE_VECTORS_SKIP\n";
    exit(0);
}

$base = file_get_contents($dbPath);
if ($base === false || $base === '') {
    echo "NOTICE: cannot read base database: {$dbPath}. Skipping.\n";
    echo "HOSTILE_VECTORS_SKIP\n";
    exit(0);
}

$doc = loadVector();
if ($doc === null) {
    echo "FAIL: cannot locate hostile_vectors.json\n";
    exit(2);
}
$cases = $doc['cases'] ?? [];
if (!is_array($cases) || count($cases) === 0) {
    echo "FAIL: hostile_vectors.json has no cases\n";
    exit(2);
}

// 基线：对【未变异】文件查询，记录每个测试 IP 的健康结果，用于检测「错误数据」。
$baseline = [];
try {
    $healthy = QzdbBuilder::bytes($base)->verifyCrc(true)->build();
    foreach (TEST_IPS as $ip) {
        try {
            $v = $healthy->findStr($ip);
        } catch (\Throwable $e) {
            $v = '';
        }
        $baseline[$ip] = ($v === null) ? '' : $v;
    }
    unset($healthy);
} catch (QzdbException $e) {
    echo "FAIL: baseline load of healthy DB failed: " . $e->getCode() . "\n";
    exit(2);
}

$anchors = parseHeaderOffsets($base);

$passed = 0;
$failed = 0;
$anomalyReport = [];
$divergenceReport = [];

echo "=== PHP Fail-Closed Hostile Test (consuming hostile_vectors.json) ===\n";
echo "Base DB: " . strlen($base) . " bytes; baseline queries: " . count(TEST_IPS) . "\n\n";

foreach ($cases as $c) {
    $id = $c['id'] ?? '(no-id)';
    $mut = $c['mutation'] ?? [];
    $exp = $c['expected_outcome'] ?? [];
    $expCodes = $exp['error_code_any'] ?? [];

    $acc = [
        'failClosed' => true,
        'obsCodes'   => [],
        'sawGraceful' => false,
        'sawCorrect' => false,
        'sawWrong'   => false,
        'sawCrash'   => false,
        'sawHang'    => false,
        'firstWrongExample' => null,
        'copyCount'  => 0,
    ];

    $sink = function (string $cp) use (&$acc, $baseline): void {
        $acc['copyCount']++;
        $m1 = evaluate($cp, false, $baseline); // lenient
        $m2 = evaluate($cp, true, $baseline);  // strict
        // 安全不变量：fail-closed 来自 strict（默认 verifyCrc=true）模式，
        // 它绝不能崩溃/挂起/返回错误数据。lenient 是 CRC 的文档化退出项，
        // 那里允许错误数据，但绝不能崩溃或挂起。
        $strictOk = !$m2['crashed'] && !$m2['hang'] && !$m2['wrongData'];
        $lenientOk = !$m1['crashed'] && !$m1['hang'];
        if (!$strictOk || !$lenientOk) {
            $acc['failClosed'] = false;
        }
        if ($m1['code'] !== null && !in_array($m1['code'], $acc['obsCodes'], true)) {
            $acc['obsCodes'][] = $m1['code'];
        }
        if ($m2['code'] !== null && !in_array($m2['code'], $acc['obsCodes'], true)) {
            $acc['obsCodes'][] = $m2['code'];
        }
        if ($m2['wrongData']) {
            $acc['sawWrong'] = true;
            if ($acc['firstWrongExample'] === null) {
                $acc['firstWrongExample'] = 'STRICT ' . $m2['wrongExample'];
            }
        }
        if ($m1['crashed'] || $m2['crashed']) {
            $acc['sawCrash'] = true;
        }
        if ($m1['hang'] || $m2['hang']) {
            $acc['sawHang'] = true;
        }
        if (($m1['opened'] && strpos($m1['detail'], 'graceful') === 0)
            || ($m2['opened'] && strpos($m2['detail'], 'graceful') === 0)) {
            $acc['sawGraceful'] = true;
        }
        if (($m1['opened'] && strpos($m1['detail'], 'correct') === 0)
            || ($m2['opened'] && strpos($m2['detail'], 'correct') === 0)) {
            $acc['sawCorrect'] = true;
        }
    };

    $log = '';
    if ($id === 'group_index_invalid') {
        // 字面配方在 std_china 上是零字节空操作（现值即 1/3）；向量 notes 授权
        // consumer craft a concrete row，故此处改用真实行级攻击。
        $sink(craftInvalidEntryRow($base, $anchors));
    } else {
        applyMutation($base, $mut, $anchors, $log, $sink);
    }

    if ($acc['copyCount'] === 0) {
        $acc['failClosed'] = false;
        $acc['firstWrongExample'] = 'NO COPIES GENERATED (mutation entirely out of bounds - test gap)';
    }

    $expNorm = [];
    foreach ($expCodes as $ec) {
        $expNorm[] = norm((string) $ec);
    }

    $divergent = false;
    if ($acc['failClosed']) {
        foreach ($acc['obsCodes'] as $oc) {
            if (!in_array(norm($oc), $expNorm, true)) {
                $divergent = true;
                break;
            }
        }
        if (!$divergent && $acc['sawGraceful'] && !in_array('gracefulnull', $expNorm, true)) {
            $divergent = true;
        }
        if (!$divergent && $acc['sawCorrect'] && !in_array('gracefulnull', $expNorm, true)) {
            $divergent = true;
        }
    }

    if (!$acc['failClosed']) {
        $status = 'FAIL';
        $failed++;
        $reason = $acc['sawWrong'] ? 'WRONG-DATA' : ($acc['sawCrash'] ? 'CRASH' : ($acc['sawHang'] ? 'HANG' : 'NO-COPIES'));
        $anomalyReport[] = sprintf(
            "ANOMALY  %s  [%s]\n    mutation=%s\n    example=%s",
            $id, $reason, json_encode($mut, JSON_UNESCAPED_SLASHES), $acc['firstWrongExample']
        );
    } else {
        $passed++;
        $status = $divergent ? 'PASS*' : 'PASS';
        if ($divergent) {
            $divergenceReport[] = sprintf(
                "DIVERGENT  %s  observed=%s expected=%s",
                $id, describeObs($acc['obsCodes'], $acc['sawGraceful'], $acc['sawCorrect']), json_encode($expCodes)
            );
        }
    }

    printf("  [%-6s] %-32s copies=%-4d %s\n", $status, $id, $acc['copyCount'], describeObs($acc['obsCodes'], $acc['sawGraceful'], $acc['sawCorrect']));
}

echo "\n";
echo "HostileVectors: {$passed}/" . count($cases) . " passed"
    . ($failed > 0 ? "  ({$failed} FAILED - SDK anomalies)" : "") . "\n";

if (!empty($divergenceReport)) {
    echo "\n--- Divergences (fail-closed holds, but observed family != expected) ---\n";
    foreach ($divergenceReport as $d) {
        echo $d . "\n";
    }
}

if ($failed > 0) {
    echo "\n--- SDK Anomaly Report (genuine fail-closed violations) ---\n";
    foreach ($anomalyReport as $a) {
        echo $a . "\n";
    }
    echo "\nHOSTILE_VECTORS_FAIL\n";
    exit(1);
}

echo "\nHOSTILE_VECTORS_OK\n";
exit(0);
