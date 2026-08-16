<?php
// QZDB reference-compliant benchmark for PHP  (docs/BENCH_CONTRACT.md v1.0)
//
// Implements splitmix64 reference RNG, four distributions, dual-stack tri-mode,
// QPS/P50/P95/P99 cold vs hot, FNV-1a parity guard against bench_vectors.json,
// string round-trip, and a fork-based concurrency gate (PHP CLI is
// single-threaded, so the §8 "16 threads x 100k shared reader" is approximated
// with 16 forked children each owning a reader — see environment.note).
//
// All 64-bit arithmetic uses the GMP extension (PHP native ints become floats
// past 2^63, which would corrupt splitmix64 / FNV). 64-bit multipliers that
// overflow 32-bit (e.g. 0x9E3779B97F4A7C15) are mandatory here.
//
// Env overrides:  BENCH_OPS=200000   BENCH_EDITIONS=std_china

const MASTER_SEED   = 20260807;
const POOL_HOT_V4   = 4096;
const POOL_HOT_V6   = 1024;
const FINGERPRINT_N = 1024;
const MAPPED_PREFIX = "0x0000FFFF00000000"; // ::ffff:0:0
const COLD_OPS      = 200000;
const WARMUP_OPS    = 1000000;
const LAT_EVERY     = 20;
const CONC_THREADS  = 16;
const CONC_OPS      = 100000;

const DIST_NAMES = ["random", "hot", "sequential", "real_world"];
const MODE_NAMES = ["v4", "v6", "mixed"];

$MASK64 = gmp_init("18446744073709551615", 10);
$MASK32 = gmp_init("4294967295", 10);
$MASK16 = gmp_init("65535", 10);

function g($s, $base = 0) { return gmp_init($s, 0); } // base 0 auto-detects 0x/0b/0 prefixes
// PHP GMP has no shift op; emulate via divide/multiply by 2^n.
function gmp_shr($x, $n) { return gmp_div_q($x, gmp_pow("2", $n)); }

// ----------------------------------------------------------- splitmix64
class SplitMix64 {
    public $s;
    public $mask64;
    public function __construct($seed, $mask64) {
        $this->s = gmp_and(g($seed, 10), $mask64);
        $this->mask64 = $mask64;
    }
    public function next() {
        $this->s = gmp_and(gmp_add($this->s, g("0x9E3779B97F4A7C15", 16)), $this->mask64);
        $z = $this->s;
        $z = gmp_and(gmp_mul(gmp_xor($z, gmp_shr($z, 30)), g("0xBF58476D1CE4E5B9", 16)), $this->mask64);
        $z = gmp_and(gmp_mul(gmp_xor($z, gmp_shr($z, 27)), g("0x94D049BB133111EB", 16)), $this->mask64);
        return gmp_xor($z, gmp_shr($z, 31));
    }
    public function u32() {
        return gmp_and($this->next(), g("0xFFFFFFFF", 16));
    }
}

function fnv1a($bytes, $h, $mask64) {
    if (gmp_cmp($h, g("0", 10)) == 0) $h = g("0xCBF29CE484222325", 16);
    for ($i = 0; $i < strlen($bytes); $i++) {
        $h = gmp_xor($h, g((string) ord($bytes[$i]), 10));
        $h = gmp_and(gmp_mul($h, g("0x100000001B3", 16)), $mask64);
    }
    return $h;
}

// ----------------------------------------------------------- encoding
function u64_le_bytes($x) {
    $b = "";
    for ($k = 0; $k < 8; $k++) {
        $b .= chr(gmp_intval(gmp_and(gmp_shr($x, $k * 8), g("0xFF", 16))));
    }
    return $b;
}
function u64_be_bytes($x) {
    $b = "";
    for ($k = 7; $k >= 0; $k--) {
        $b .= chr(gmp_intval(gmp_and(gmp_shr($x, $k * 8), g("0xFF", 16))));
    }
    return $b;
}
function enc_query($kind, $hi, $lo) {
    if ($kind == 0) {
        // u32 zero-extended to u64, LE == 4 bytes LE + 4 zero bytes (8 total)
        return substr(u64_le_bytes($hi), 0, 4) . "\0\0\0\0";
    }
    return u64_le_bytes($hi) . u64_le_bytes($lo); // u128 = hi||lo, LE (16 bytes)
}

// ----------------------------------------------------------- stream
class Stream {
    public $rng;
    public $dist;
    public $mode;
    public $pool_v4;
    public $pool_v6;
    public $base4;
    public $base6_hi;
    public $base6_lo;
    public $i = 0;
    public $mask64;
    public function __construct($dist, $mode, $seed, $pool_v4, $pool_v6, $mask64) {
        $this->dist = $dist; $this->mode = $mode;
        $this->pool_v4 = $pool_v4; $this->pool_v6 = $pool_v6;
        $this->mask64 = $mask64;
        $this->rng = new SplitMix64($seed, $mask64);
        $this->base4 = $this->rng->u32();
        list($this->base6_hi, $this->base6_lo) = [$this->rng->next(), $this->rng->next()];
    }
    public function gen_v4() {
        switch ($this->dist) {
            case "random": return $this->rng->u32();
            case "hot":    return $this->pool_v4[gmp_intval($this->rng->u32()) % POOL_HOT_V4];
            case "sequential": return gmp_and(gmp_add($this->base4, g((string)$this->i, 10)), g("0xFFFFFFFF", 16));
            default:
                $r = gmp_intval($this->rng->u32()) % 10;
                if ($r < 6) return $this->pool_v4[gmp_intval($this->rng->u32()) % POOL_HOT_V4];
                if ($r < 9) return $this->rng->u32();
                return gmp_and(gmp_add($this->base4, g((string)$this->i, 10)), g("0xFFFFFFFF", 16));
        }
    }
    public function gen_v6() {
        switch ($this->dist) {
            case "random":
                return [$this->rng->next(), $this->rng->next()];
            case "hot":
                $p = $this->pool_v6[gmp_intval($this->rng->u32()) % POOL_HOT_V6];
                return [$p[0], $p[1]];
            case "sequential":
                list($lo, $carry) = bi_add($this->base6_lo, g((string)$this->i, 10));
                $hi = gmp_and(gmp_add($this->base6_hi, $carry), $this->mask64);
                return [$hi, $lo];
            default:
                $r = gmp_intval($this->rng->u32()) % 10;
                if ($r < 6) {
                    $p = $this->pool_v6[gmp_intval($this->rng->u32()) % POOL_HOT_V6];
                    return [$p[0], $p[1]];
                }
                if ($r < 9) return [$this->rng->next(), $this->rng->next()];
                list($lo, $carry) = bi_add($this->base6_lo, g((string)$this->i, 10));
                $hi = gmp_and(gmp_add($this->base6_hi, $carry), $this->mask64);
                return [$hi, $lo];
        }
    }
    public function next() {
        // Mirrors bench_gen.py gen_stream(): `i` indexes the CURRENT query and
        // is advanced only after it has been produced. Missing this increment
        // pins mixed-mode to the v4 branch and kills the sequential offset.
        $mapped = g(MAPPED_PREFIX, 16);
        $out = null;
        switch ($this->mode) {
            case "v4":
                $out = [0, $this->gen_v4(), g("0", 10)];
                break;
            case "v6":
                if (gmp_intval($this->rng->u32()) % 5 == 0) {
                    $ip = $this->gen_v4();
                    $out = [2, g("0", 10), gmp_or($mapped, $ip)];
                } else {
                    list($hi, $lo) = $this->gen_v6();
                    $out = [1, $hi, $lo];
                }
                break;
            default:
                $m = $this->i % 10;
                if ($m < 5) {
                    $out = [0, $this->gen_v4(), g("0", 10)];
                } elseif ($m < 9) {
                    list($hi, $lo) = $this->gen_v6();
                    $out = [1, $hi, $lo];
                } else {
                    $ip = $this->gen_v4();
                    $out = [2, g("0", 10), gmp_or($mapped, $ip)];
                }
                break;
        }
        $this->i++;
        return $out;
    }
}

function bi_add($a, $b) {
    $lo = gmp_and(gmp_add($a, $b), g("18446744073709551615", 10));
    $carry = gmp_cmp(gmp_add($a, $b), g("18446744073709551615", 10)) > 0 ? g("1", 10) : g("0", 10);
    return [$lo, $carry];
}

function build_pools($mask64) {
    $p4 = new SplitMix64(MASTER_SEED + 1, $mask64);
    $pool_v4 = [];
    for ($i = 0; $i < POOL_HOT_V4; $i++) $pool_v4[] = gmp_intval($p4->u32());
    $p6 = new SplitMix64(MASTER_SEED + 2, $mask64);
    $pool_v6 = [];
    for ($i = 0; $i < POOL_HOT_V6; $i++) $pool_v6[] = [$p6->next(), $p6->next()];
    return [$pool_v4, $pool_v6];
}

// ----------------------------------------------------------- SDK dispatch
// Each kind goes to the entry point a real caller would use. kind 2 is an
// IPv4-mapped address (::ffff:w.x.y.z) and MUST use findBytes(), which
// performs the mapped downgrade; findV6Bin() is a pure v6 trie walk that never
// downgrades, so it would miss on every mapped query and the bench would time
// the early-exit miss path instead of a real lookup.
function dispatch($reader, $kind, $hi, $lo, &$hit) {
    if ($kind == 0) {
        $g = $reader->findUint(gmp_intval($hi));
    } else {
        $bin = u64_be_bytes($hi) . u64_be_bytes($lo);
        $g = ($kind == 2) ? $reader->findBytes($bin) : $reader->findV6Bin($bin);
    }
    $hit = ($g !== null);
    return $g;
}

// ----------------------------------------------------------- metrics
function pct($arr, $p) {
    if (count($arr) == 0) return 0;
    sort($arr, SORT_NUMERIC);
    $idx = min(count($arr) - 1, max(0, (int)(floor(count($arr) * $p + 0.9999) - 1)));
    return $arr[$idx];
}

function run_single($reader, $dist, $mode, $seed, $pool_v4, $pool_v6, $mask64, $ops, $sample) {
    $st = new Stream($dist, $mode, $seed, $pool_v4, $pool_v6, $mask64);
    $lat = [];
    $errs = 0; $hits = 0;
    $t0 = hrtime(true);
    for ($i = 0; $i < $ops; $i++) {
        list($kind, $hi, $lo) = $st->next();
        if ($sample && $i % LAT_EVERY == 0) {
            $a = hrtime(true);
            dispatch($reader, $kind, $hi, $lo, $f);
            $lat[] = hrtime(true) - $a;
            if ($f) $hits++;
        } else {
            dispatch($reader, $kind, $hi, $lo, $f);
            if ($f) $hits++;
        }
    }
    $el = (hrtime(true) - $t0) / 1e9;
    return [
        "ops" => $ops,
        "qps" => $ops / $el,
        "avg_ns" => $el * 1e9 / $ops,
        "p50_ns" => pct($lat, 0.50),
        "p95_ns" => pct($lat, 0.95),
        "p99_ns" => pct($lat, 0.99),
        "errors" => $errs,
        "hits" => $hits,
        "hit_rate" => $hits / $ops,
        "warm" => "",
        "api" => "uint",
    ];
}

function concurrency_safe_fork($dbPath, $dist, $mode, $seed, $pool_v4, $pool_v6, $mask64) {
    // PHP has no threads in the default CLI SAPI, so §8's concurrency gate is
    // approximated with 16 forked children, each holding its OWN reader. This
    // proves reader construction + hot-path are re-entrant across processes;
    // it does NOT prove shared-reader thread safety (nothing is shared here).
    if (!function_exists("pcntl_fork")) {
        return [null, 0];   // null => "not applicable", reported as such
    }
    $pids = [];
    $ok = 0;
    for ($t = 0; $t < CONC_THREADS; $t++) {
        $pid = pcntl_fork();
        if ($pid == -1) { continue; }
        if ($pid == 0) {
            // child: own reader, 100k hot.mixed queries
            $r = new \Qqzeng\Ip\QzdbReader($dbPath, 0, false);
            $st = new Stream($dist, $mode, $seed, $pool_v4, $pool_v6, $mask64);
            $done = 0;
            for ($i = 0; $i < CONC_OPS; $i++) {
                list($kind, $hi, $lo) = $st->next();
                dispatch($r, $kind, $hi, $lo, $f);
                $done++;
            }
            exit($done == CONC_OPS ? 0 : 1);
        }
        $pids[] = $pid;
    }
    foreach ($pids as $pid) {
        $st = null;
        pcntl_waitpid($pid, $st);
        // pcntl_wexitstatus() is only meaningful for normally-exited children;
        // a child killed by a signal (crash) must count as a failure.
        if (pcntl_wifexited($st) && pcntl_wexitstatus($st) === 0) $ok++;
    }
    return [count($pids) === CONC_THREADS && $ok === CONC_THREADS, $ok * CONC_OPS];
}

// ----------------------------------------------------------- parity
function parity_self_check($manifest, $pool_v4, $pool_v6, $mask64) {
    echo "parity self-check ... ";
    $bad = 0;
    foreach (DIST_NAMES as $dn) {
        foreach (MODE_NAMES as $mn) {
            $info = $manifest["streams"][$dn][$mn];
            $want = gmp_init($info["first1024_fnv1a"], 10);
            $seed = gmp_intval(g($info["seed"], 10));
            $st = new Stream($dn, $mn, $seed, $pool_v4, $pool_v6, $mask64);
            $h = g("0", 10);
            for ($i = 0; $i < FINGERPRINT_N; $i++) {
                list($kind, $hi, $lo) = $st->next();
                $h = fnv1a(enc_query($kind, $hi, $lo), $h, $mask64);
            }
            if (gmp_cmp($h, $want) != 0) {
                echo "\n  MISMATCH $dn . $mn got=" . gmp_strval($h) . " want=" . gmp_strval($want);
                $bad++;
            }
        }
    }
    if ($bad != 0) { echo "\nFAILED\n"; return false; }
    echo "OK (12/12 streams match bench_vectors.json)\n";
    return true;
}

// ----------------------------------------------------------- helpers
function repo_root() {
    $d = getcwd();
    for ($i = 0; $i < 8; $i++) {
        if (file_exists($d . "/multi-lang/tools/bench_vectors.json")) return $d;
        $p = dirname($d);
        if ($p == $d) break;
        $d = $p;
    }
    return "";
}
function find_db($root, $edition) {
    $map = ["std_china" => ["std", "china"], "max_global" => ["max", "global"]];
    if (!isset($map[$edition])) return null;
    list($tier, $region) = $map[$edition];
    foreach (["multi-lang/test_data_202608", "test_data_202608"] as $base) {
        $p = "$root/$base/$tier/$region/qqzeng_ip_$edition.qzdb";
        if (file_exists($p)) return $p;
    }
    return null;
}
function fmt_v4($ip) { // $ip gmp u32
    $b0 = gmp_intval(gmp_and(gmp_shr($ip, 24), g("0xFF", 16)));
    $b1 = gmp_intval(gmp_and(gmp_shr($ip, 16), g("0xFF", 16)));
    $b2 = gmp_intval(gmp_and(gmp_shr($ip, 8), g("0xFF", 16)));
    $b3 = gmp_intval(gmp_and($ip, g("0xFF", 16)));
    return "$b0.$b1.$b2.$b3";
}
function fmt_v6($hi, $lo) {
    $g = function($v, $k) { return dechex(gmp_intval(gmp_and(gmp_shr($v, 48 - 16 * $k), g("0xFFFF", 16)))); };
    $parts = [];
    for ($k = 0; $k < 4; $k++) $parts[] = $g($hi, $k);
    for ($k = 0; $k < 4; $k++) $parts[] = $g($lo, $k);
    return implode(":", $parts);
}
function cpu_model() {
    if (function_exists("shell_exec")) {
        $o = trim(@shell_exec("sysctl -n machdep.cpu.brand_string 2>/dev/null"));
        if ($o) return $o;
    }
    return "unknown";
}

// ----------------------------------------------------------- main
require_once __DIR__ . "/QzdbReader.php";

// Guard: only run the benchmark when executed directly, not when required
// (e.g. by a debug script). This lets the helpers above be unit-tested.
if (basename(__FILE__) !== basename($_SERVER["PHP_SELF"] ?? "x")) {
    return;
}

$ops = isset($_ENV["BENCH_OPS"]) ? (int)$_ENV["BENCH_OPS"] : 2000000;
if (getenv("BENCH_OPS") !== false) $ops = (int)getenv("BENCH_OPS");
if ($ops <= 0) $ops = 2000000;

$editions = ["std_china", "max_global"];
if (getenv("BENCH_EDITIONS") !== false) {
    $editions = explode(",", getenv("BENCH_EDITIONS"));
}

$root = repo_root();
if ($root === "") { fwrite(STDERR, "cannot locate repo root\n"); exit(1); }
$manifest = json_decode(file_get_contents("$root/multi-lang/tools/bench_vectors.json"), true);

list($pool_v4, $pool_v6) = build_pools($MASK64);
if (!parity_self_check($manifest, $pool_v4, $pool_v6, $MASK64)) exit(1);

$repdir = "$root/multi-lang/bench_reports";
@mkdir($repdir, 0755, true);

$ts = date("Y-m-d\TH:i:sP");
$cpu = cpu_model();
$cores = function_exists("shell_exec") ? (int)trim(@shell_exec("sysctl -n hw.ncpu 2>/dev/null") ?: "0") : 0;

foreach ($editions as $edition) {
    $db = find_db($root, $edition);
    if ($db === null) { echo "[SKIP] $edition: db not found\n"; continue; }
    try {
        $reader = new \Qqzeng\Ip\QzdbReader($db, 0, false);
    } catch (\Throwable $e) {
        echo "[SKIP] $edition: open failed: " . $e->getMessage() . "\n";
        continue;
    }
    $bytes = filesize($db);
    echo "\nedition $edition: $db ($bytes bytes)\n";

    $seedHM = gmp_intval(g($manifest["streams"]["hot"]["mixed"]["seed"], 10));
    list($safe, $cdone) = concurrency_safe_fork($db, "hot", "mixed", $seedHM, $pool_v4, $pool_v6, $MASK64);
    $safeStr = $safe === null ? "n/a (ext-pcntl missing)" : ($safe ? "true" : "false");
    echo "  concurrency_safe(" . CONC_THREADS . "x" . (CONC_OPS / 1000) . "k): $safeStr"
       . " (done=$cdone via " . CONC_THREADS . " forked children, own reader each)\n";

    $distOut = [];
    foreach (DIST_NAMES as $dn) {
        $modeOut = [];
        foreach (MODE_NAMES as $mn) {
            $seed = gmp_intval(g($manifest["streams"][$dn][$mn]["seed"], 10));
            $coldOps = min($ops, COLD_OPS);
            $cold = run_single($reader, $dn, $mn, $seed, $pool_v4, $pool_v6, $MASK64, $coldOps, true);
            $cold["warm"] = "cold";
            run_single($reader, $dn, $mn, $seed, $pool_v4, $pool_v6, $MASK64, min($ops, WARMUP_OPS), false);
            $hot = run_single($reader, $dn, $mn, $seed, $pool_v4, $pool_v6, $MASK64, $ops, true);
            $hot["warm"] = "hot";
            // PHP is single-threaded: report the only available degree
            $th = ["1" => $hot];
            $distOut[$dn][$mn] = ["cold" => $cold, "hot" => $hot, "threads" => $th];
            printf("  %-11s.%-6s hot QPS=%12.0f p50=%6dns p99=%7dns hit=%.1f%%\n",
                $dn, $mn, $hot["qps"], $hot["p50_ns"], $hot["p99_ns"], $hot["hit_rate"] * 100.0);
        }
    }

    // string round-trip on hot.mixed
    $st = new Stream("hot", "mixed", $seedHM, $pool_v4, $pool_v6, $MASK64);
    $lat = [];
    $t0 = hrtime(true);
    for ($i = 0; $i < $ops; $i++) {
        list($kind, $hi, $lo) = $st->next();
        $s = ($kind == 0) ? fmt_v4($hi) : fmt_v6($hi, $lo);
        if ($i % LAT_EVERY == 0) {
            $a = hrtime(true);
            $reader->find($s);
            $lat[] = hrtime(true) - $a;
        } else {
            $reader->find($s);
        }
    }
    $el = (hrtime(true) - $t0) / 1e9;
    $srt = [
        "ops" => $ops, "qps" => $ops / $el, "avg_ns" => $el * 1e9 / $ops,
        "p50_ns" => pct($lat, 0.50), "p95_ns" => pct($lat, 0.95), "p99_ns" => pct($lat, 0.99),
        "errors" => 0, "hits" => 0, "hit_rate" => 0.0, "warm" => "hot", "api" => "string",
    ];
    printf("  %-11s.%-6s STRING round-trip QPS=%12.0f p99=%7dns\n", "hot", "mixed", $srt["qps"], $srt["p99_ns"]);

    $report = [
        "contract" => "QZDB_BENCH_CONTRACT v1.0",
        "language" => "php",
        "sdk_version" => "multi-lang/php (QzdbReader)",
        "timestamp" => $ts,
        "seed" => MASTER_SEED,
        "db" => ["path" => $db, "edition" => $edition, "bytes" => $bytes, "hash" => "crc32:n/a"],
        "environment" => [
            "cpu" => $cpu, "cores" => $cores, "os" => "darwin arm64",
            "runtime" => "php " . PHP_VERSION, "compiler" => "php " . PHP_VERSION,
            "bench_contract" => "v1.0",
            "note" => "PHP CLI is single-threaded; 64-bit math via GMP. Thread scaling reports only degree 1. The §8 concurrency gate is approximated with 16 pcntl_fork children (each owns a reader).",
        ],
        "distributions" => $distOut,
        "string_roundtrip" => ["hot" => ["mixed" => $srt]],
        "concurrency_safe" => $safe,
        "concurrency_done" => $cdone,
        "concurrency_spec" => CONC_THREADS . " forked children x " . CONC_OPS . " ops (own reader each)",
    ];
    $out = "$repdir/php_$edition.json";
    file_put_contents($out, json_encode($report, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES));
    echo "  wrote $out\n";
}
