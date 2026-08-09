<?php
/**
 * Batch IP query runner for PHP
 * Usage: php batch_query.php <database_path> <v4_test> <v4_output> <v6_test> <v6_output>
 * 
 * Reads test IPs from files, queries the QZDB database, writes results.
 * Test file format: one IP per line (uint32 for V4, "high:low" for V6)
 * Output format: ip_key|pipe_separated_geo_string
 */

ini_set('memory_limit', '512M');

if ($argc < 5) {
    fwrite(STDERR, "Usage: php batch_query.php <db_path> <v4_test> <v4_out> <v6_test> <v6_out>\n");
    exit(1);
}

$dbPath = $argv[1];
$v4Test = $argv[2];
$v4Out = $argv[3];
$v6Test = $argv[4];
$v6Out = $argv[5] ?? '';

require_once __DIR__ . '/../php/QzdbReader.php';
use Qqzeng\Ip\QzdbReader;

// v2.4 起 getInstance() 已移除；PHP 侧统一用构造函数
$searcher = new QzdbReader($dbPath);

function geoToPipe($r, $searcher) {
    // Use GeoInfo::toPipe() so output byte-matches Python to_pipe()
    if (!$r) return '';
    return $r->toPipe();
}

function parseV4Key(string $s): ?int {
    $parts = explode('.', $s);
    if (count($parts) === 4) {
        $v = 0;
        foreach ($parts as $p) {
            if (!ctype_digit($p) || (int)$p > 255) return null;
            $v = ($v << 8) | (int)$p;
        }
        return $v;
    }
    if (!ctype_digit($s) || strlen($s) > 10) return null;
    $v = (int)$s;
    return ($v < 0 || $v > 4294967295) ? null : $v;
}

// V4
$lines = file($v4Test, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
$results = [];
foreach ($lines as $line) {
    $ip = parseV4Key(trim($line));
    if ($ip === null) {
        $results[] = $line . '|';
        continue;
    }
    $r = $searcher->findUint($ip);
    $results[] = $line . '|' . geoToPipe($r, $searcher);
}
file_put_contents($v4Out, implode("\n", $results) . "\n");
fwrite(STDERR, "  PHP V4: " . count($results) . " queries\n");

// V6 - convert high:low decimal to 16-byte binary via GMP, use findV6Bytes
if ($v6Out && file_exists($v6Test)) {
    $lines6 = file($v6Test, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
    $results6 = [];
    foreach ($lines6 as $line) {
        $parts = explode(':', trim($line));
        $key16 = str_pad(gmp_export(gmp_init($parts[0], 10), 8, GMP_BIG_ENDIAN | GMP_MSW_FIRST), 8, "\0", STR_PAD_LEFT)
               . str_pad(gmp_export(gmp_init($parts[1], 10), 8, GMP_BIG_ENDIAN | GMP_MSW_FIRST), 8, "\0", STR_PAD_LEFT);
        $r = $searcher->findV6Bin($key16);
        $results6[] = $line . '|' . geoToPipe($r, $searcher);
    }
    file_put_contents($v6Out, implode("\n", $results6) . "\n");
    fwrite(STDERR, "  PHP V6: " . count($results6) . " queries\n");
}

fwrite(STDERR, "  PHP DONE\n");