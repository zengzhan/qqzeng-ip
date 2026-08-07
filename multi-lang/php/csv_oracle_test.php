<?php
/**
 * Independent correctness oracle for the PHP QZDB reader.
 *
 * 与 Python test_csv_oracle.py / Go csv_oracle_test.go 完全镜像：
 * 以 .qzdb 的 *源数据* test_data_202608/<edition>/china/*_range.csv 为裁判
 * （带 start_ip_num/end_ip_num + 地理字段），在全局随机 + 区间内随机抽样，
 * 比对 country/province/city/isp。
 *
 * 不同于 tier2_golden（向量由被测代码自身生成，只证跨语言一致），本测试证明
 * SDK 对*真值*答得对。
 *
 * 运行：php csv_oracle_test.php
 * 任何失配即以非 0 退出；源 CSV 缺失时优雅跳过。
 */

require_once __DIR__ . '/QzdbReader.php';
use Qqzeng\Ip\QzdbReader;

// 源 CSV 较大（ult_china ~47 万行），开发期验证脚本调高内存上限（非生产运行时）。
ini_set('memory_limit', '768M');

const ROOT = __DIR__ . '/..';
const DATA_DIR = ROOT . '/data';
const SRC_DIR = ROOT . '/test_data_202608';

const TARGETS = [
    ['std_china', 'qqzeng_ip_std_china.qzdb', 'std/china/qqzeng_ip_std_china_range.csv'],
    ['ult_china', 'qqzeng_ip_ult_china.qzdb', 'ult/china/qqzeng_ip_ult_china_range.csv'],
];

const IN_RANGE_SAMPLES = 6000;
const GLOBAL_SAMPLES = 5000;
const SEED = 12345;

/** 加载源 CSV，返回按 start_ip_num 升序的 (start,end,country,province,city,isp) 行。 */
function loadCsvOracle(string $csvPath): array
{
    $rows = [];
    $fh = fopen($csvPath, 'r');
    if ($fh === false) {
        return $rows;
    }
    $hdr = fgetcsv($fh, 0, ',', '"', '\\');
    $ci = array_flip($hdr);
    while (($row = fgetcsv($fh, 0, ',', '"', '\\')) !== false) {
        $s = (int)$row[$ci['start_ip_num']];
        $e = (int)$row[$ci['end_ip_num']];
        $rows[] = [
            $s, $e,
            $row[$ci['country']], $row[$ci['province']],
            $row[$ci['city']], $row[$ci['isp']],
        ];
    }
    fclose($fh);
    usort($rows, fn($a, $b) => $a[0] <=> $b[0]);
    return $rows;
}

/** 在按 start 升序的行中查找覆盖 ipi 的区间（非重叠，返回最右匹配）。 */
function csvLookup(array $rows, int $ipi): ?array
{
    $lo = 0;
    $hi = count($rows) - 1;
    $idx = -1;
    while ($lo <= $hi) {
        $mid = intdiv($lo + $hi, 2);
        if ($rows[$mid][0] <= $ipi) {
            $idx = $mid;
            $lo = $mid + 1;
        } else {
            $hi = $mid - 1;
        }
    }
    if ($idx >= 0 && $ipi <= $rows[$idx][1]) {
        return $rows[$idx];
    }
    return null;
}

function runTarget(string $label, string $qzdbName, string $csvRel): int
{
    $qzdbPath = DATA_DIR . '/' . $qzdbName;
    $csvPath = SRC_DIR . '/' . $csvRel;

    if (!file_exists($qzdbPath)) {
        echo "  SKIP {$label}: qzdb not found ({$qzdbPath})\n";
        return 0;
    }
    if (!file_exists($csvPath)) {
        echo "  SKIP {$label}: source csv not found ({$csvPath})\n";
        return 0;
    }

    $rows = loadCsvOracle($csvPath);
    if (count($rows) === 0) {
        echo "  SKIP {$label}: empty csv\n";
        return 0;
    }

    $reader = new QzdbReader($qzdbPath, 0);
    mt_srand(SEED);

    $mismatch = 0;
    $foundBoth = 0;
    $missBoth = 0;
    $checked = 0;
    $details = [];

    // 1) 全局随机 IP
    for ($i = 0; $i < GLOBAL_SAMPLES; $i++) {
        $ipi = mt_rand(0, 0xFFFFFFFF);
        $ip = long2ip($ipi);
        $exp = csvLookup($rows, $ipi);
        $gi = $reader->find($ip);
        $sdk = $gi === null ? null : [$gi->getCountry(), $gi->getProvince(), $gi->getCity(), $gi->getIsp()];
        $expT = $exp === null ? null : [$exp[2], $exp[3], $exp[4], $exp[5]];
        $checked++;
        if ($exp === null && $gi === null) {
            $missBoth++;
            continue;
        }
        if ($exp !== null && $gi !== null) {
            $foundBoth++;
            if ($sdk !== $expT) {
                $mismatch++;
                if (count($details) < 12) {
                    $details[] = "ip={$ip} sdk=" . json_encode($sdk) . " csv=" . json_encode($expT);
                }
            }
        } else {
            $mismatch++;
            if (count($details) < 12) {
                $details[] = "ip={$ip} sdk=" . json_encode($sdk) . " csv=" . json_encode($expT);
            }
        }
    }

    // 2) 区间内随机 IP（最大化 found_both 覆盖）
    $nRows = count($rows);
    for ($i = 0; $i < IN_RANGE_SAMPLES; $i++) {
        $a = $rows[mt_rand(0, $nRows - 1)];
        $b = $rows[mt_rand(0, $nRows - 1)];
        $lo = min($a[0], $b[0]);
        $hi = max($a[1], $b[1]);
        $ipi = mt_rand($lo, $hi);
        $ip = long2ip($ipi);
        $exp = csvLookup($rows, $ipi);
        $gi = $reader->find($ip);
        $sdk = $gi === null ? null : [$gi->getCountry(), $gi->getProvince(), $gi->getCity(), $gi->getIsp()];
        $expT = $exp === null ? null : [$exp[2], $exp[3], $exp[4], $exp[5]];
        $checked++;
        if ($exp !== null && $gi !== null) {
            $foundBoth++;
            if ($sdk !== $expT) {
                $mismatch++;
                if (count($details) < 12) {
                    $details[] = "ip={$ip} sdk=" . json_encode($sdk) . " csv=" . json_encode($expT);
                }
            }
        }
    }

    $reader->close();
    $status = $mismatch === 0 ? 'OK' : 'FAIL';
    echo "  {$label}: {$status} checked={$checked} found_both={$foundBoth} "
        . "miss_both={$missBoth} MISMATCH={$mismatch}\n";
    foreach ($details as $d) {
        echo "    MISMATCH {$d}\n";
    }
    return $mismatch;
}

function main(): int
{
    echo "=== CSV oracle (independent ground-truth correctness) ===\n";
    $total = 0;
    foreach (TARGETS as [$label, $qzdbName, $csvRel]) {
        $total += runTarget($label, $qzdbName, $csvRel);
    }
    if ($total === 0) {
        echo "CSV_ORACLE_OK (all targets 0 mismatch)\n";
    } else {
        echo "CSV_ORACLE: total MISMATCH={$total} -> FAIL\n";
    }
    return $total !== 0 ? 1 : 0;
}

exit(main());
