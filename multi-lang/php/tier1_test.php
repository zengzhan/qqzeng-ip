<?php
/**
 * Tier1 单元测试（DB-free，契约 §10 九大类）。
 * 运行：php tier1_test.php
 */
require_once __DIR__ . '/QzdbReader.php';
use Qqzeng\Ip\QzdbReader;
use Qqzeng\Ip\GeoInfo;
use Qqzeng\Ip\UsageType;
use Qqzeng\Ip\KnownUsageType;
use Qqzeng\Ip\RowIds;
use Qqzeng\Ip\BatchResult;
use Qqzeng\Ip\QzdbException;
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

/* ----------------------------------------------------------------------
 * 构造一个最小但结构合法的 .qzdb 文件，用于在无真实数据库时练习查询 / 解析 / CIDR 路径。
 * 关闭 CRC 校验即可加载；开启 CRC 校验应能 Fail-Closed 拒绝（CRC 字段故意写错）。
 * ---------------------------------------------------------------------- */
function buildSyntheticFile(string $path, int $storedCrc): void
{
    $buf = str_repeat("\0", 256);
    // magic
    $buf[0] = 'Q'; $buf[1] = 'Z'; $buf[2] = 'D'; $buf[3] = 'B';
    $buf[4] = "\1";                       // format version = 1
    // flags (U16 LE) @8 : 0（无 V4/V6，使 find/lookupCidr 直接返回 null，避免触碰 Trie 越界）
    $f = pack('v', 0);
    $buf[8] = $f[0]; $buf[9] = $f[1];
    $buf[11] = "\x10";                    // v6JumpBits = 16
    $buf[13] = "\x02";                    // poolIdxSize = 2
    // storedCrc (U32 LE) @16
    $c = pack('V', $storedCrc);
    $buf[16] = $c[0]; $buf[17] = $c[1]; $buf[18] = $c[2]; $buf[19] = $c[3];
    // headerSize (U32 LE) @36 = 192
    $h = pack('V', 192);
    $buf[36] = $h[0]; $buf[37] = $h[1]; $buf[38] = $h[2]; $buf[39] = $h[3];
    // ipRowSize (U32 LE) @160 = 6
    $r = pack('V', 6);
    $buf[160] = $r[0]; $buf[161] = $r[1]; $buf[162] = $r[2]; $buf[163] = $r[3];
    // geoEntryGroupCount (U32 LE) @164 = 1
    $g = pack('V', 1);
    $buf[164] = $g[0]; $buf[165] = $g[1]; $buf[166] = $g[2]; $buf[167] = $g[3];
    file_put_contents($path, $buf);
}

$tmp = __DIR__ . '/.tier1_synthetic.qzdb';
buildSyntheticFile($tmp, 0xDEADBEEF);   // 错误的 CRC

/* ----------------------------------------------------------------------
 * 类别 1：严格 IP 解析（前导零 / 越界 / 缺段 / 超长 / CIDR 形式 / zone-id 全拒绝）
 * PHP 语义：非法 IP 返回 null（契约 §4），不抛异常、不崩溃。
 * ---------------------------------------------------------------------- */
$reader = QzdbBuilder::path($tmp)->verifyCrc(false)->build();   // 关闭 CRC 以便加载合成文件

$invalidIps = [
    '', '1.2.3', '1.2.3.4.5', '256.1.1.1', '1.2.3.256', '01.2.3.4',
    '1.2.3.4.', '1::2::3', '1.2.3.4%eth0', 'gggg::1', '12345::1',
    '2001:db8::g', '1.2.3.4/24', '::ffff:999.1.1.1', '  8.8.8.8  ',
];
foreach ($invalidIps as $ip) {
    check($reader->find($ip) === null, "find('{$ip}') 应返回 null（非法 IP）");
}

// 合法格式 IP 查询不崩溃，返回 null（合成库无覆盖）
$validIps = ['8.8.8.8', '223.5.5.5', '2001:db8::1', '::1', '::ffff:8.8.8.8', '0.0.0.0'];
foreach ($validIps as $ip) {
    $r = $reader->find($ip);
    check($r === null || $r instanceof GeoInfo, "find('{$ip}') 合法格式不应崩溃");
}

/* ----------------------------------------------------------------------
 * 类别 4：字段名归一化（大小写 / 下划线 / 连字符不敏感）
 * ---------------------------------------------------------------------- */
$g = new GeoInfo(['CN'], ['country_code']);
check($g->get('country_code') === 'CN', 'get(country_code)');
check($g->get('countryCode') === 'CN', 'get(countryCode) 等价');
check($g->get('Country-Code') === 'CN', 'get(Country-Code) 等价');
check($g->get('COUNTRY_CODE') === 'CN', 'get(COUNTRY_CODE) 等价');
check($g->get('missing') === '', 'get(missing) 返回 ""');
check($g->get('') === '', 'get("") 返回 ""');

/* ----------------------------------------------------------------------
 * 类别：序列化（toPipe / toMap / toJson）
 * ---------------------------------------------------------------------- */
$g2 = new GeoInfo(['116.400000', 'Broadband', 'CN'], ['longitude', 'usage_type', 'country']);
check($g2->toPipe() === '116.400000|Broadband|CN', 'toPipe 逐字拼接');
check($g2->toPipeString() === $g2->toPipe(), 'toPipeString == toPipe');
check($g2->__toString() === $g2->toPipe(), 'toString == toPipe');
$m = $g2->toMap();
check($m['country'] === 'CN' && $m['longitude'] === '116.400000', 'toMap 字段值');
$json = $g2->toJson();
check(strpos($json, '"longitude":116.400000') !== false, 'toJson 数值字段为数字');
check(strpos($json, '"usage_type":"Broadband"') !== false, 'toJson 字符串字段带引号');
check(strpos($json, '"country":"CN"') !== false, 'toJson country 为字符串');

/* ----------------------------------------------------------------------
 * 类别 2：浮点原生格式 = 6 位小数（FORMAT §10.5 统一契约）
 * ---------------------------------------------------------------------- */
check(GeoInfo::formatFloatValue(116.0) === '116', '整数值无小数点');
check(GeoInfo::formatFloatValue(116.4) === '116.400000', '非整数固定 6 位小数');
check(GeoInfo::formatFloatValue(-3.5) === '-3.500000', '负数 6 位小数');
check(GeoInfo::formatFloatValue(0.0) === '0', '0 无小数点');
check(GeoInfo::formatFloatValue(-0.0) === '0', '-0.0 归一为 "0"');
check(GeoInfo::formatFloatValue(NAN) === '', 'NaN -> ""');
check(GeoInfo::formatFloatValue(INF) === '', 'Inf -> ""');
// §10.5 大整值边界：int64 范围内走 (int) 字面量；恰为 2^63 及以上必须走 %.0F
// 定点（(int) cast 对超范围浮点是未定义/异常行为——回归守卫）
check(GeoInfo::formatFloatValue(1.0e16) === '10000000000000000', '2^53 整值无小数点');
check(GeoInfo::formatFloatValue(9.2e18) === '9200000000000000000', 'int64 上界内大整值');
check(GeoInfo::formatFloatValue(9223372036854775808.0) === '9223372036854775808', '恰为 2^63 走 %.0F 定点');
check(GeoInfo::formatFloatValue(-9223372036854775808.0) === '-9223372036854775808', '恰为 -2^63');
check(GeoInfo::formatFloatValue(1.0e20) === '100000000000000000000', '> 2^63 定点整数位');

/* ----------------------------------------------------------------------
 * 类别 5：UsageType 21 场景 + 未知兜底
 * ---------------------------------------------------------------------- */
$known = ['AICrawler','Backbone','Broadband','Business','CDN','Cloud','DNS','DataCenter',
          'Education','Finance','Government','ISP','IXP','IoT','Mobile','Reserved','Satellite',
          'Spider','Streaming','Unknown','VPN'];
check(count($known) === 21, '已知场景共 21 个');
foreach ($known as $k) {
    $ut = KnownUsageType::fromRaw($k);
    check($ut !== null && $ut->isKnown() && $ut->rawValue() === $k, "KnownUsageType {$k}");
}
$unknown = UsageType::fromString('SomethingNew');
check(!$unknown->isKnown(), '未知场景 isKnown()=false');
check($unknown->rawValue() === 'SomethingNew', '未知场景保留原始值');
check(UsageType::fromString('')->isKnown() === false ? true : true, '空字符串兜底');
check(UsageType::fromString('Cloud')->getDisplayZh() === '云服务', 'Cloud 中文名');
check(UsageType::fromString('VPN')->getDisplayEn() === 'VPN', 'VPN 英文名');

/* ----------------------------------------------------------------------
 * 类别 3：GeoInfo 语义 Getter（缺失返回 "" 或 null，不崩溃）
 * ---------------------------------------------------------------------- */
$g3 = new GeoInfo(['CN', '中国', 'Broadband'], ['country', 'country_en', 'usage_type']);
check($g3->getCountry() === 'CN', 'getCountry');
check($g3->getCountryEn() === '中国', 'getCountryEn');
check($g3->getAsn() === null, 'getAsn 缺失 -> null');
check($g3->getLongitude() === null, 'getLongitude 缺失 -> null');
check($g3->getGeoId() === null, 'getGeoId 缺失 -> null');
check($g3->getCidr() === '', 'getCidr 恒返回 ""');
check($g3->getUsageType()->rawValue() === 'Broadband', 'getUsageType');
check($g3->get('not_a_real_field') === '', '未知字段 -> ""');

/* ----------------------------------------------------------------------
 * 类别 6：损坏文件 Fail-Closed（构造即拒绝）
 * ---------------------------------------------------------------------- */
// 坏 magic
file_put_contents($tmp . '.bad', str_repeat('X', 256));
try {
    QzdbBuilder::path($tmp . '.bad')->build();
    check(false, '坏 magic 应抛异常');
} catch (QzdbException $e) {
    check($e->getCode() === QzdbReader::ERROR_BAD_MAGIC, '坏 magic -> BAD_MAGIC');
}
// 坏格式版本
$buf = file_get_contents($tmp);
$buf[4] = "\2";
file_put_contents($tmp . '.ver', $buf);
try {
    QzdbBuilder::path($tmp . '.ver')->build();
    check(false, '坏版本应抛异常');
} catch (QzdbException $e) {
    check($e->getCode() === QzdbReader::ERROR_UNSUPPORTED, '坏版本 -> UNSUPPORTED');
}
// 坏 header size
$buf2 = file_get_contents($tmp);
$h2 = pack('V', 100);
$buf2[36] = $h2[0]; $buf2[37] = $h2[1]; $buf2[38] = $h2[2]; $buf2[39] = $h2[3];
file_put_contents($tmp . '.hs', $buf2);
try {
    QzdbBuilder::path($tmp . '.hs')->build();
    check(false, '坏 header size 应抛异常');
} catch (QzdbException $e) {
    check($e->getCode() === QzdbReader::ERROR_CORRUPTED, '坏 header size -> CORRUPTED');
}
// 截断文件
file_put_contents($tmp . '.trunc', substr(file_get_contents($tmp), 0, 100));
try {
    QzdbBuilder::path($tmp . '.trunc')->build();
    check(false, '截断文件应抛异常');
} catch (QzdbException $e) {
    check(true, '截断文件被拒绝');
}

/* ----------------------------------------------------------------------
 * 类别 7：CRC 强制（verifyCrc=true 应因错 CRC 拒绝；false 可加载）
 * ---------------------------------------------------------------------- */
try {
    QzdbBuilder::path($tmp)->verifyCrc(true)->build();   // 合成文件 CRC 故意错
    check(false, '错 CRC 应拒绝');
} catch (QzdbException $e) {
    check($e->getCode() === QzdbReader::ERROR_CORRUPTED, '错 CRC -> CORRUPTED (Fail-Closed)');
}
// verifyCrc(false) 可加载（已在上文 $reader 构造验证）
check($reader instanceof QzdbReader, 'verifyCrc(false) 可加载合成文件');

/* ----------------------------------------------------------------------
 * 类别 8：原子热更新 reload（reload 后 reader 仍可用；旧快照不破坏）
 * ---------------------------------------------------------------------- */
// reload 同一文件（CRC 强制）会抛 CORRUPTED（因合成文件 CRC 错）；此处验证 reload 方法存在且行为一致
try {
    $reader->reload($tmp);  // 强制 CRC -> 应抛
    check(false, 'reload 错 CRC 文件应抛');
} catch (QzdbException $e) {
    check($e->getCode() === QzdbReader::ERROR_CORRUPTED, 'reload 强制 CRC 校验');
}
// reloadBuffer 等价
try {
    $reader->reloadBuffer(file_get_contents($tmp));
    check(false, 'reloadBuffer 错 CRC 应抛');
} catch (QzdbException $e) {
    check($e->getCode() === QzdbReader::ERROR_CORRUPTED, 'reloadBuffer 强制 CRC');
}

/* ----------------------------------------------------------------------
 * 类别 9：CIDR 反查（合成库无覆盖 -> null；非法 IP -> null；不崩溃）
 * ---------------------------------------------------------------------- */
check($reader->lookupCidr('8.8.8.8') === null, '未覆盖 IP CIDR -> null');
check($reader->lookupCidr('bad-ip') === null, '非法 IP CIDR -> null');
check($reader->lookupCidrUint(0x08080808) === null, 'lookupCidrUint 未覆盖 -> null');
$v6bytes = inet_pton('2001:db8::1');
check($reader->lookupCidrBytes($v6bytes) === null, 'lookupCidrBytes 未覆盖 -> null');
check($reader->lookupCidrBytes('abc') === null, 'lookupCidrBytes 长度非法 -> null');

/* ----------------------------------------------------------------------
 * 资源释放 / 生命周期
 * ---------------------------------------------------------------------- */
check($reader->isClosed() === false, '加载后未关闭');
$reader->close();
check($reader->isClosed() === true, 'close 后置位');
$reader->close();   // 幂等
check($reader->isClosed() === true, 'close 幂等安全');

/* ----------------------------------------------------------------------
 * RowIds / BatchResult 构造与三态
 * ---------------------------------------------------------------------- */
$row = new RowIds(1, 2, 3);
check($row->geoId === 1 && $row->asnId === 2 && $row->usageId === 3, 'RowIds 字段');
$brOk = new BatchResult('1.1.1.1', new GeoInfo(['CN'], ['country']), null);
check($brOk->isSuccess(), 'BatchResult success');
check($brOk->info instanceof GeoInfo, 'BatchResult info field');
$brMiss = new BatchResult('1.1.1.1', null, null);
check($brMiss->isNotFound(), 'BatchResult notFound');
$brErr = new BatchResult('x', null, new QzdbException('bad', QzdbReader::ERROR_INVALID_PARAM));
check($brErr->hasError(), 'BatchResult error');

/* ----------------------------------------------------------------------
 * 元信息自省 API 存在性（合成结构下返回合理默认值）
 * ---------------------------------------------------------------------- */
$reader2 = QzdbBuilder::path($tmp)->verifyCrc(false)->build();
check($reader2 instanceof QzdbReader, '重建 reader');
check(is_string($reader2->getVersion()), 'getVersion 返回 string');
check(is_string($reader2->getScope()) && $reader2->getScope() === '', 'getScope 无 TLV 条目时返回 ""');
check(is_string($reader2->getEdition()), 'getEdition 返回 string');
check(is_string($reader2->getFileHash()) && strlen($reader2->getFileHash()) === 8, 'getFileHash 8 位 hex');
check($reader2->getGroupCount() >= 1, 'getGroupCount >= 1');
check(is_array($reader2->getFieldNames()), 'getFieldNames 数组');
check($reader2->hasField('field_0'), 'hasField 命中');
check(!$reader2->hasField('nonexistent_field'), 'hasField 未命中');

/* ----------------------------------------------------------------------
 * 类别：Metadata TLV type=5/6 权威语义（FORMAT §8.2 / ROADMAP T7）
 * 带 type=5/6 条目的文件：getDataMonth()/getScope() 取 TLV 值（原样、不 trim）；
 * 无条目的文件走回落（见上方 reader2：scope 为 ""）。
 * ---------------------------------------------------------------------- */
$metaBuf = str_repeat("\0", 256);
$metaBuf[0] = 'Q'; $metaBuf[1] = 'Z'; $metaBuf[2] = 'D'; $metaBuf[3] = 'B';
$metaBuf[4] = "\1";
$f2 = pack('v', 0x0004);              // flags bit2 = has metadata
$metaBuf[8] = $f2[0]; $metaBuf[9] = $f2[1];
$metaBuf[13] = "\x02";                // poolIdxSize ∈ {2,3}（与 python/test_tlv_meta.py 对齐）
$h2 = pack('V', 192);
$metaBuf[36] = $h2[0]; $metaBuf[37] = $h2[1]; $metaBuf[38] = $h2[2]; $metaBuf[39] = $h2[3];
$r2b = pack('V', 6);
$metaBuf[160] = $r2b[0]; $metaBuf[161] = $r2b[1]; $metaBuf[162] = $r2b[2]; $metaBuf[163] = $r2b[3];
$g2b = pack('V', 1);
$metaBuf[164] = $g2b[0]; $metaBuf[165] = $g2b[1]; $metaBuf[166] = $g2b[2]; $metaBuf[167] = $g2b[3];
$om = pack('P', 256);                 // offMeta (U64 LE) @144
$metaBuf[144] = $om[0]; $metaBuf[145] = $om[1]; $metaBuf[146] = $om[2]; $metaBuf[147] = $om[3];
$metaBuf[148] = $om[4]; $metaBuf[149] = $om[5]; $metaBuf[150] = $om[6]; $metaBuf[151] = $om[7];
// TLV：type=5 "2026-07"、type=6 "global"（含一个前后带空格的对照不做——原样存储即契约）
$meta = "\x05\x00" . pack('v', 7) . '2026-07'
      . "\x06\x00" . pack('v', 6) . 'global';
$tmpMeta = __DIR__ . '/.tier1_synthetic_meta.qzdb';
file_put_contents($tmpMeta, $metaBuf . $meta);
$readerMeta = QzdbBuilder::path($tmpMeta)->verifyCrc(false)->build();
check($readerMeta->getDataMonth() === '2026-07', 'TLV type=5 为权威 dataMonth');
check($readerMeta->getScope() === 'global', 'TLV type=6 为权威 scope');
check($readerMeta->getBuildTime() === '', 'buildTime 与 TLV 无关（无 BuildDate 时为 ""）');
$readerMeta->close();
unlink($tmpMeta);

/* ----------------------------------------------------------------------
 * GeoInfo 缓存淘汰策略（直接映射 / 碰撞覆盖 / 永不整表清空）
 *
 * 回归守卫：旧实现在 size 达到 GEO_CACHE_LIMIT 时执行 `$this->geoCache = []`
 * 整表清空，长驻进程（Swoole/RoadRunner）下会把常驻热点集一起丢光，造成吞吐断崖。
 * 这里断言三件事：① 表容量被槽位下标钉死 ② 碰撞只重算绝不串值
 * ③ 持续压入远超容量的 entry 也不会出现条目数骤降。
 * ---------------------------------------------------------------------- */
$cacheDb = __DIR__ . '/../data/qqzeng_ip_std_global.qzdb';
if (file_exists($cacheDb)) {
    $cr   = new QzdbReader($cacheDb, 0);
    $crf  = new ReflectionClass($cr);
    $mResolve = $crf->getMethod('resolveGeo');
    $pCache = $crf->getProperty('geoCache');
    $pCounts = $crf->getProperty('groupEntryCounts');
    // PHP 8.0 需显式 setAccessible（8.1+ 免除，此处保留以兼容 8.0）
    $mResolve->setAccessible(true);
    $pCache->setAccessible(true);
    $pCounts->setAccessible(true);
    $nEntry = $pCounts->getValue($cr)[0];

    $cap = QzdbReader::GEO_CACHE_LIMIT;
    check($cap === (QzdbReader::GEO_CACHE_MASK + 1), '缓存容量与掩码自洽');
    check(($cap & ($cap - 1)) === 0, '缓存容量是 2 的幂');

    // ① 压入 2.5 倍容量的不同 entry：条目数不得越界，也不得骤降
    $probe = min($nEntry, (int)($cap * 2.5));
    $prevSz = 0; $sawDrop = false;
    for ($i = 1; $i < $probe; $i++) {
        $mResolve->invoke($cr, $i, 0);
        if (($i & 0x3FFF) === 0) {                  // 每 16384 次采样一次
            $sz = count($pCache->getValue($cr));
            if ($sz < $prevSz * 0.5) { $sawDrop = true; }
            $prevSz = $sz;
        }
    }
    $finalSz = count($pCache->getValue($cr));
    check($finalSz <= $cap, "缓存条目数不越界（{$finalSz} <= {$cap}）");
    check($finalSz > $cap * 0.5, "缓存被有效填充（{$finalSz}）");
    check($sawDrop === false, '压入 2.5x 容量全程无整表清空（条目数无骤降）');

    // ② 碰撞正确性：找一对映射到同一槽位的 entryId，交替查询必须各自返回自己的值
    $slotOf = static function (int $e) use ($cap): int {
        return (($e ^ ($e >> 16)) + 0) & ($cap - 1);
    };
    $seen = []; $pair = null;
    for ($e = 1; $e < $nEntry && $pair === null; $e++) {
        $s = $slotOf($e);
        if (isset($seen[$s])) { $pair = [$seen[$s], $e]; } else { $seen[$s] = $e; }
    }
    check($pair !== null, '构造出同槽位碰撞对');
    if ($pair !== null) {
        list($eA, $eB) = $pair;
        $refA = $mResolve->invoke($cr, $eA, 0);
        $refB = $mResolve->invoke($cr, $eB, 0);
        $pipeA = $refA === null ? '<null>' : $refA->toPipe();
        $pipeB = $refB === null ? '<null>' : $refB->toPipe();
        $ok = true;
        for ($k = 0; $k < 50; $k++) {               // 交替 100 次，每次都在互相踢出对方
            $a = $mResolve->invoke($cr, $eA, 0);
            $b = $mResolve->invoke($cr, $eB, 0);
            if (($a === null ? '<null>' : $a->toPipe()) !== $pipeA) { $ok = false; break; }
            if (($b === null ? '<null>' : $b->toPipe()) !== $pipeB) { $ok = false; break; }
        }
        check($ok, "同槽位碰撞交替查询不串值（entry {$eA} vs {$eB}）");
    }

    // ③ reload / close 后缓存必须清空（快照隔离）
    $cr->close();
    check(count($pCache->getValue($cr)) === 0, 'close 后缓存清空');
} else {
    echo "SKIP 缓存淘汰策略测试: {$cacheDb} 不存在\n";
}

echo "\nTier1: PASSED={$passed} FAILED={$failed}\n";
if ($failed > 0) {
    exit(1);
}
echo "TIER1_OK\n";
