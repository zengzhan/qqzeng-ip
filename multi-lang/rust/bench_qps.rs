// QZDB reference-compliant benchmark for Rust  (docs/BENCH_CONTRACT.md v1.0)
//
// Implements splitmix64 reference RNG (byte-identical to the other 7 benches),
// four distributions, dual-stack tri-mode, QPS/P50/P95/P99 cold vs hot,
// thread scaling 1/2/4/8/16 on a SHARED Arc<QzdbReader> + 16x100k concurrency
// gate, and canonical JSON to multi-lang/bench_reports/rust_<edition>.json.
//
// Parity guard: FNV-1a 64 over the first 1024 queries of every stream is
// compared against bench_vectors.json (same manifest the other languages read).
//
// Env overrides:  BENCH_OPS=200000   BENCH_EDITIONS=std_china

use qzdb::QzdbReader;
use serde_json::{json, Value};
use std::sync::atomic::{AtomicI64, AtomicU64, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::Instant;

const MASTER_SEED: u64 = 20260807;
const POOL_HOT_V4: usize = 4096;
const POOL_HOT_V6: usize = 1024;
const FINGERPRINT_N: usize = 1024;
const MAPPED_PREFIX: u128 = 0xFFFF_u128 << 32; // ::ffff:0:0
const COLD_OPS: usize = 200_000;
const WARMUP_OPS: usize = 1_000_000;
const LAT_EVERY: usize = 20;
const CONC_THREADS: usize = 16;
const CONC_OPS: usize = 100_000;

const DIST_NAMES: [&str; 4] = ["random", "hot", "sequential", "real_world"];
const MODE_NAMES: [&str; 3] = ["v4", "v6", "mixed"];

#[derive(Clone, Copy)]
enum Dist {
    Random,
    Hot,
    Sequential,
    RealWorld,
}
#[derive(Clone, Copy)]
enum Mode {
    V4,
    V6,
    Mixed,
}

// ----------------------------------------------------------- splitmix64

struct SplitMix64 {
    s: u64,
}
impl SplitMix64 {
    fn new(seed: u64) -> Self {
        Self { s: seed }
    }
    fn next(&mut self) -> u64 {
        self.s = self.s.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.s;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }
    fn u32(&mut self) -> u32 {
        self.next() as u32
    }
}

fn fnv1a(data: &[u8], mut h: u64) -> u64 {
    if h == 0 {
        h = 0xCBF2_9CE4_8422_2325;
    }
    for &b in data {
        h ^= b as u64;
        h = h.wrapping_mul(0x0000_0100_0000_01B3);
    }
    h
}

// ----------------------------------------------------------- pools + stream

struct Stream {
    rng: SplitMix64,
    dist: Dist,
    mode: Mode,
    pool_v4: Arc<Vec<u32>>,
    pool_v6: Arc<Vec<(u64, u64)>>,
    base4: u32,
    base6: (u64, u64),
    i: u64,
}

fn build_pools() -> (Vec<u32>, Vec<(u64, u64)>) {
    let mut p4 = SplitMix64::new(MASTER_SEED + 1);
    let pool_v4: Vec<u32> = (0..POOL_HOT_V4).map(|_| p4.u32()).collect();
    let mut p6 = SplitMix64::new(MASTER_SEED + 2);
    let pool_v6: Vec<(u64, u64)> = (0..POOL_HOT_V6)
        .map(|_| (p6.next(), p6.next()))
        .collect();
    (pool_v4, pool_v6)
}

impl Stream {
    fn new(dist: Dist, mode: Mode, seed: u64, pool_v4: Arc<Vec<u32>>, pool_v6: Arc<Vec<(u64, u64)>>) -> Self {
        let mut rng = SplitMix64::new(seed);
        let base4 = rng.u32();
        let base6 = (rng.next(), rng.next());
        Self {
            rng,
            dist,
            mode,
            pool_v4,
            pool_v6,
            base4,
            base6,
            i: 0,
        }
    }

    fn gen_v4(&mut self) -> u32 {
        match self.dist {
            Dist::Random => self.rng.u32(),
            Dist::Hot => self.pool_v4[self.rng.u32() as usize % POOL_HOT_V4],
            Dist::Sequential => self.base4.wrapping_add(self.i as u32),
            Dist::RealWorld => {
                let r = self.rng.u32() % 10;
                if r < 6 {
                    self.pool_v4[self.rng.u32() as usize % POOL_HOT_V4]
                } else if r < 9 {
                    self.rng.u32()
                } else {
                    self.base4.wrapping_add(self.i as u32)
                }
            }
        }
    }

    fn gen_v6(&mut self) -> (u64, u64) {
        match self.dist {
            Dist::Random => (self.rng.next(), self.rng.next()),
            Dist::Hot => {
                let p = self.pool_v6[self.rng.u32() as usize % POOL_HOT_V6];
                (p.0, p.1)
            }
            Dist::Sequential => {
                let (hi, lo) = self.base6;
                let (lo2, carry) = lo.overflowing_add(self.i);
                let hi2 = hi.wrapping_add(carry as u64);
                (hi2, lo2)
            }
            Dist::RealWorld => {
                let r = self.rng.u32() % 10;
                if r < 6 {
                    let p = self.pool_v6[self.rng.u32() as usize % POOL_HOT_V6];
                    (p.0, p.1)
                } else if r < 9 {
                    (self.rng.next(), self.rng.next())
                } else {
                    let (hi, lo) = self.base6;
                    let (lo2, carry) = lo.overflowing_add(self.i);
                    let hi2 = hi.wrapping_add(carry as u64);
                    (hi2, lo2)
                }
            }
        }
    }

    fn next(&mut self) -> (u8, u64, u64) {
        let (kind, hi, lo) = match self.mode {
            Mode::V4 => (0u8, self.gen_v4() as u64, 0u64),
            Mode::V6 => {
                if self.rng.u32() % 5 == 0 {
                    let ip = self.gen_v4();
                    (2u8, 0, (MAPPED_PREFIX | ip as u128) as u64)
                } else {
                    let (h, l) = self.gen_v6();
                    (1u8, h, l)
                }
            }
            Mode::Mixed => {
                let m = self.i % 10;
                if m < 5 {
                    (0u8, self.gen_v4() as u64, 0u64)
                } else if m < 9 {
                    let (h, l) = self.gen_v6();
                    (1u8, h, l)
                } else {
                    let ip = self.gen_v4();
                    (2u8, 0, (MAPPED_PREFIX | ip as u128) as u64)
                }
            }
        };
        self.i += 1;
        (kind, hi, lo)
    }
}

fn enc_query(kind: u8, hi: u64, lo: u64) -> Vec<u8> {
    if kind == 0 {
        hi.to_le_bytes().to_vec() // 8 bytes (u32 zero-ext)
    } else {
        let mut v = Vec::with_capacity(16);
        v.extend_from_slice(&hi.to_le_bytes());
        v.extend_from_slice(&lo.to_le_bytes());
        v
    }
}

// ----------------------------------------------------------- metrics

#[derive(Default, Clone)]
struct Metrics {
    ops: usize,
    qps: f64,
    avg_ns: f64,
    p50_ns: u64,
    p95_ns: u64,
    p99_ns: u64,
    errors: i64,
    hits: i64,
    hit_rate: f64,
    warm: String,
    api: String,
}

fn pct(v: &mut [u64], p: f64) -> u64 {
    if v.is_empty() {
        return 0;
    }
    v.sort_unstable();
    let idx = ((v.len() as f64 * p + 0.9999).floor() as usize).saturating_sub(1);
    v[idx.min(v.len() - 1)]
}

/// Route each query kind to the entry point a real caller would use.
///
/// kind 2 (IPv4-mapped, `::ffff:w.x.y.z`) MUST go through `find_bytes`, which
/// performs the mapped downgrade. `find_v6` is a *pure* v6 trie walk that never
/// downgrades, so it misses on every mapped query — the bench would then be
/// timing the early-exit miss path instead of a real lookup, and the hit rate
/// would silently diverge from the other languages.
fn dispatch(r: &Arc<QzdbReader>, kind: u8, hi: u64, lo: u64) -> bool {
    match kind {
        0 => r.find_uint(hi as u32).is_some(),
        2 => {
            let ip = ((hi as u128) << 64) | (lo as u128);
            r.find_bytes(&ip.to_be_bytes()).is_some()
        }
        _ => {
            let ip = ((hi as u128) << 64) | (lo as u128);
            r.find_v6(ip).is_some()
        }
    }
}

fn run_single(r: &Arc<QzdbReader>, dist: Dist, mode: Mode, seed: u64,
              pool_v4: &Arc<Vec<u32>>, pool_v6: &Arc<Vec<(u64, u64)>>,
              ops: usize, sample: bool) -> Metrics {
    let mut st = Stream::new(dist, mode, seed, pool_v4.clone(), pool_v6.clone());
    let mut lat: Vec<u64> = Vec::with_capacity(ops / LAT_EVERY + 1);
    let mut hits = 0i64;
    let t0 = Instant::now();
    for i in 0..ops {
        let (kind, hi, lo) = st.next();
        let found = if sample && i % LAT_EVERY == 0 {
            let a = Instant::now();
            let f = dispatch(r, kind, hi, lo);
            lat.push(a.elapsed().as_nanos() as u64);
            f
        } else {
            dispatch(r, kind, hi, lo)
        };
        if found {
            hits += 1;
        }
    }
    let el = t0.elapsed().as_secs_f64();
    let mut m = Metrics {
        ops,
        qps: ops as f64 / el,
        avg_ns: el * 1e9 / ops as f64,
        errors: 0,
        hits,
        hit_rate: hits as f64 / ops as f64,
        warm: String::new(),
        api: "uint".into(),
        ..Default::default()
    };
    m.p50_ns = pct(&mut lat, 0.50);
    m.p95_ns = pct(&mut lat, 0.95);
    m.p99_ns = pct(&mut lat, 0.99);
    m
}

fn run_multi(r: &Arc<QzdbReader>, dist: Dist, mode: Mode, seed: u64,
             pool_v4: &Arc<Vec<u32>>, pool_v6: &Arc<Vec<(u64, u64)>>,
             threads: usize, ops: usize) -> Metrics {
    let per = ops / threads;
    let done = Arc::new(AtomicU64::new(0));
    let hits = Arc::new(AtomicI64::new(0));
    let t0 = Instant::now();
    let handles: Vec<_> = (0..threads)
        .map(|_| {
            let r = r.clone();
            let done = done.clone();
            let hits = hits.clone();
            let pool_v4 = pool_v4.clone();
            let pool_v6 = pool_v6.clone();
            thread::spawn(move || {
                let mut st = Stream::new(dist, mode, seed, pool_v4, pool_v6);
                let mut local_hits = 0i64;
                for _ in 0..per {
                    let (kind, hi, lo) = st.next();
                    if dispatch(&r, kind, hi, lo) {
                        local_hits += 1;
                    }
                }
                done.fetch_add(per as u64, Ordering::Relaxed);
                hits.fetch_add(local_hits, Ordering::Relaxed);
            })
        })
        .collect();
    for h in handles {
        let _ = h.join();
    }
    let el = t0.elapsed().as_secs_f64();
    let d = done.load(Ordering::Relaxed);
    let h = hits.load(Ordering::Relaxed);
    Metrics {
        ops: d as usize,
        qps: d as f64 / el,
        avg_ns: el * 1e9 / d as f64,
        errors: 0,
        hits: h,
        hit_rate: h as f64 / d as f64,
        warm: "hot".into(),
        api: "uint".into(),
        ..Default::default()
    }
}

fn concurrency_safe(r: &Arc<QzdbReader>, seed: u64,
                     pool_v4: &Arc<Vec<u32>>, pool_v6: &Arc<Vec<(u64, u64)>>) -> (bool, u64) {
    let done = Arc::new(AtomicU64::new(0));
    let handles: Vec<_> = (0..CONC_THREADS)
        .map(|_| {
            let r = r.clone();
            let done = done.clone();
            let pool_v4 = pool_v4.clone();
            let pool_v6 = pool_v6.clone();
            thread::spawn(move || {
                let mut st = Stream::new(Dist::Hot, Mode::Mixed, seed, pool_v4, pool_v6);
                for _ in 0..CONC_OPS {
                    let (kind, hi, lo) = st.next();
                    let _ = dispatch(&r, kind, hi, lo);   // a miss is expected, not a failure
                }
                done.fetch_add(CONC_OPS as u64, Ordering::Relaxed);
            })
        })
        .collect();
    for h in handles {
        let _ = h.join();
    }
    let d = done.load(Ordering::Relaxed);
    (d == (CONC_THREADS * CONC_OPS) as u64, d)
}

// ----------------------------------------------------------- parity

fn parity_self_check(manifest: &Value, pool_v4: &Arc<Vec<u32>>, pool_v6: &Arc<Vec<(u64, u64)>>) -> bool {
    print!("parity self-check ... ");
    let streams = &manifest["streams"];
    let mut bad = 0;
    for &dn in DIST_NAMES.iter() {
        for &mn in MODE_NAMES.iter() {
            let info = &streams[dn][mn];
            let want: u64 = info["first1024_fnv1a"].as_str().unwrap().parse().unwrap();
            let seed: u64 = info["seed"].as_u64().unwrap();
            let mut st = Stream::new(dist_from(dn), mode_from(mn), seed, pool_v4.clone(), pool_v6.clone());
            let mut h = 0u64;
            for _ in 0..FINGERPRINT_N {
                let (kind, hi, lo) = st.next();
                let q = enc_query(kind, hi, lo);
                h = fnv1a(&q, h);
            }
            if h != want {
                println!("\n  MISMATCH {} . {} got={} want={}", dn, mn, h, want);
                bad += 1;
            }
        }
    }
    if bad != 0 {
        println!("\nFAILED");
        false
    } else {
        println!("OK (12/12 streams match bench_vectors.json)");
        true
    }
}

fn dist_from(s: &str) -> Dist {
    match s {
        "random" => Dist::Random,
        "hot" => Dist::Hot,
        "sequential" => Dist::Sequential,
        _ => Dist::RealWorld,
    }
}
fn mode_from(s: &str) -> Mode {
    match s {
        "v4" => Mode::V4,
        "v6" => Mode::V6,
        _ => Mode::Mixed,
    }
}

// ----------------------------------------------------------- helpers

fn repo_root() -> String {
    let mut d = std::env::current_dir().unwrap();
    for _ in 0..8 {
        if d.join("multi-lang/tools/bench_vectors.json").exists() {
            return d.to_string_lossy().to_string();
        }
        if !d.pop() {
            break;
        }
    }
    String::new()
}

fn find_db(root: &str, edition: &str) -> Option<String> {
    let (tier, region) = match edition {
        "std_china" => ("std", "china"),
        "max_global" => ("max", "global"),
        _ => return None,
    };
    for base in ["multi-lang/test_data_202608", "test_data_202608"] {
        let p = format!("{}/{}/{}/{}/qqzeng_ip_{}.qzdb", root, base, tier, region, edition);
        if std::path::Path::new(&p).exists() {
            return Some(p);
        }
    }
    None
}

fn fmt_v4(ip: u32) -> String {
    format!("{}.{}.{}.{}", (ip >> 24) & 255, (ip >> 16) & 255, (ip >> 8) & 255, ip & 255)
}
fn fmt_v6(hi: u64, lo: u64) -> String {
    let g = |v: u64, k: usize| -> String {
        let shift = 48 - 16 * k;
        format!("{:x}", (v >> shift) & 0xFFFF)
    };
    let mut s = String::new();
    for k in 0..4 {
        s.push_str(&g(hi, k));
        s.push(':');
    }
    for k in 0..4 {
        s.push_str(&g(lo, k));
        if k < 3 {
            s.push(':');
        }
    }
    s
}

fn cpu_model() -> String {
    if let Ok(out) = std::process::Command::new("sysctl")
        .args(["-n", "machdep.cpu.brand_string"])
        .output()
    {
        if out.status.success() {
            return String::from_utf8_lossy(&out.stdout).trim().to_string();
        }
    }
    "unknown".to_string()
}

fn metrics_to_json(m: &Metrics) -> Value {
    let mut v = json!({
        "ops": m.ops,
        "qps": m.qps as u64,
        "avg_ns": m.avg_ns,
        "p50_ns": m.p50_ns,
        "p95_ns": m.p95_ns,
        "p99_ns": m.p99_ns,
        "errors": m.errors,
        "hits": m.hits,
        "hit_rate": m.hit_rate,
    });
    if !m.warm.is_empty() {
        v["warm"] = json!(m.warm);
    }
    if !m.api.is_empty() {
        v["api"] = json!(m.api);
    }
    v
}

// ----------------------------------------------------------- main

fn main() {
    let ops: usize = std::env::var("BENCH_OPS")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(2_000_000);
    let editions: Vec<String> = std::env::var("BENCH_EDITIONS")
        .ok()
        .map(|v| v.split(',').map(|s| s.to_string()).collect())
        .unwrap_or_else(|| vec!["std_china".to_string(), "max_global".to_string()]);

    let root = repo_root();
    if root.is_empty() {
        eprintln!("cannot locate repo root");
        return;
    }
    let manifest_text = std::fs::read_to_string(format!("{}/multi-lang/tools/bench_vectors.json", root))
        .expect("read manifest");
    let manifest: Value = serde_json::from_str(&manifest_text).expect("parse manifest");

    let (pool_v4, pool_v6) = build_pools();
    let pool_v4 = Arc::new(pool_v4);
    let pool_v6 = Arc::new(pool_v6);

    if !parity_self_check(&manifest, &pool_v4, &pool_v6) {
        return;
    }

    let repdir = format!("{}/multi-lang/bench_reports", root);
    std::fs::create_dir_all(&repdir).ok();

    let ts = chrono_timestamp();
    let cpu = cpu_model();
    let cores = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(0);

    for edition in &editions {
        let db = match find_db(&root, edition) {
            Some(p) => p,
            None => {
                println!("[SKIP] {}: db not found", edition);
                continue;
            }
        };
        let reader = match QzdbReader::from_file(&db) {
            Ok(r) => Arc::new(r),
            Err(_) => {
                println!("[SKIP] {}: open failed", edition);
                continue;
            }
        };
        let bytes = std::fs::metadata(&db).map(|m| m.len()).unwrap_or(0);
        println!("\nedition {}: {} ({} bytes)", edition, db, bytes);

        let seed_hot_mixed: u64 = manifest["streams"]["hot"]["mixed"]["seed"].as_u64().unwrap();
        let (safe, cdone) = concurrency_safe(&reader, seed_hot_mixed, &pool_v4, &pool_v6);
        println!("  concurrency_safe({}x{}k): {} (done={})", CONC_THREADS, CONC_OPS / 1000, safe, cdone);

        let mut dist_out = serde_json::Map::new();
        let mut first_d = true;
        for &dn in DIST_NAMES.iter() {
            if !first_d {
                print!("\n");
            }
            first_d = false;
            let mut mode_out = serde_json::Map::new();
            for &mn in MODE_NAMES.iter() {
                let seed: u64 = manifest["streams"][dn][mn]["seed"].as_u64().unwrap();
                let cold_ops = ops.min(COLD_OPS);
                let mut cold = run_single(&reader, dist_from(dn), mode_from(mn), seed, &pool_v4, &pool_v6, cold_ops, true);
                cold.warm = "cold".into();
                let _ = run_single(&reader, dist_from(dn), mode_from(mn), seed, &pool_v4, &pool_v6, ops.min(WARMUP_OPS), false);
                let mut hot = run_single(&reader, dist_from(dn), mode_from(mn), seed, &pool_v4, &pool_v6, ops, true);
                hot.warm = "hot".into();

                let mut th = serde_json::Map::new();
                let tcfgs = [1usize, 2, 4, 8, 16];
                for &tc in tcfgs.iter() {
                    let mm = run_multi(&reader, dist_from(dn), mode_from(mn), seed, &pool_v4, &pool_v6, tc, ops);
                    th.insert(tc.to_string(), metrics_to_json(&mm));
                }
                let t1 = run_multi(&reader, dist_from(dn), mode_from(mn), seed, &pool_v4, &pool_v6, 1, ops);
                let t16 = run_multi(&reader, dist_from(dn), mode_from(mn), seed, &pool_v4, &pool_v6, 16, ops);
                let scl = t16.qps / (t1.qps + 1e-9);

                let mut mr = serde_json::Map::new();
                mr.insert("cold".into(), metrics_to_json(&cold));
                mr.insert("hot".into(), metrics_to_json(&hot));
                mr.insert("threads".into(), Value::Object(th));
                mode_out.insert(mn.to_string(), Value::Object(mr));

                println!(
                    "  {:<11}.{:<6} hot QPS={:>12.0} p50={:>6}ns p99={:>7}ns 1T={:>12.0} 16T={:>12.0} ({:.1}x) hit={:.1}%",
                    dn, mn, hot.qps, hot.p50_ns, hot.p99_ns, t1.qps, t16.qps, scl, hot.hit_rate * 100.0
                );
            }
            dist_out.insert(dn.to_string(), Value::Object(mode_out));
        }

        // string round-trip on hot.mixed
        let mut st = Stream::new(Dist::Hot, Mode::Mixed, seed_hot_mixed, pool_v4.clone(), pool_v6.clone());
        let mut lat: Vec<u64> = Vec::with_capacity(ops / LAT_EVERY + 1);
        let t0 = Instant::now();
        for i in 0..ops {
            let (kind, hi, lo) = st.next();
            let s = if kind == 0 {
                fmt_v4(hi as u32)
            } else {
                fmt_v6(hi, lo)
            };
            if i % LAT_EVERY == 0 {
                let a = Instant::now();
                let _ = reader.find(&s);
                lat.push(a.elapsed().as_nanos() as u64);
            } else {
                let _ = reader.find(&s);
            }
        }
        let el = t0.elapsed().as_secs_f64();
        let mut srt = Metrics {
            ops,
            qps: ops as f64 / el,
            avg_ns: el * 1e9 / ops as f64,
            errors: 0,
            hits: 0,
            hit_rate: 0.0,
            warm: "hot".into(),
            api: "string".into(),
            ..Default::default()
        };
        srt.p50_ns = pct(&mut lat, 0.50);
        srt.p95_ns = pct(&mut lat, 0.95);
        srt.p99_ns = pct(&mut lat, 0.99);
        println!("  {:<11}.{:<6} STRING round-trip QPS={:>12.0} p99={:>7}ns", "hot", "mixed", srt.qps, srt.p99_ns);

        let report = json!({
            "contract": "QZDB_BENCH_CONTRACT v1.0",
            "language": "rust",
            "sdk_version": "multi-lang/rust (crate qzdb)",
            "timestamp": ts,
            "seed": MASTER_SEED,
            "db": {"path": db, "edition": edition, "bytes": bytes, "hash": "crc32:n/a"},
            "environment": {
                "cpu": cpu,
                "cores": cores,
                "os": "darwin arm64",
                "runtime": "rustc",
                "compiler": "rustc",
                "bench_contract": "v1.0",
                "note": "Arc<QzdbReader> shared across std::thread workers; per-snapshot bounded lock-free cache."
            },
            "distributions": Value::Object(dist_out),
            "string_roundtrip": {"hot": {"mixed": metrics_to_json(&srt)}},
            "concurrency_safe": safe,
            "concurrency_done": cdone,
            "concurrency_spec": format!("{} threads x {} ops shared reader", CONC_THREADS, CONC_OPS),
        });
        let out = format!("{}/rust_{}.json", repdir, edition);
        std::fs::write(&out, serde_json::to_string_pretty(&report).unwrap()).ok();
        println!("  wrote {}", out);
    }
}

fn chrono_timestamp() -> String {
    if let Ok(out) = std::process::Command::new("date").args(["+%Y-%m-%dT%H:%M:%S%z"]).output() {
        if out.status.success() {
            return String::from_utf8_lossy(&out.stdout).trim().to_string();
        }
    }
    String::new()
}
