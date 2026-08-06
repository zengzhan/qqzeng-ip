use std::time::Instant;

fn bench(db_name: &str, db_path: &str) {
   if !std::path::Path::new(db_path).exists() {
       println!("  {db_name}: not found");
       return;
   }
    let searcher = match qzdb_reader::from_file(db_path) {
        Ok(s) => s,
        Err(_) => { println!("  {db_name}: load failed"); return; }
    };

    // V4
    let count = 3_000_000;
    let mut seed: u32 = 123;
    let mut ips = Vec::with_capacity(count);
    for _ in 0..count {
        seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
        ips.push(seed);
    }
    let start = Instant::now();
    for &ip in &ips { searcher.find_uint(ip); }
    let v4_qps = count as f64 / start.elapsed().as_secs_f64();

    // V6
    let v6_count = 1_000_000;
    let mut v6_seed: u64 = 456;
    let v6_start = Instant::now();
    for _ in 0..v6_count {
        v6_seed = v6_seed.wrapping_mul(1664525).wrapping_add(1013904223);
        let high = (v6_seed << 32) | (v6_seed ^ 0xDEADBEEF);
        let low = (v6_seed ^ 0xCAFEBABE) << 32;
        let ip_v6 = ((high as u128) << 64) | (low as u128);
        searcher.find_v6(ip_v6);
    }
    let v6_qps = v6_count as f64 / v6_start.elapsed().as_secs_f64();
    println!("  {db_name:12} V4 QPS: {v4_qps:.0}  V6 QPS: {v6_qps:.0}");
}

fn main() {
    println!("Rust QPS Benchmarks (M4 Pro)");
    bench("std_china", "../test_data_202608/std/china/qqzeng_ip_std_china.qzdb");
    bench("max_china", "../test_data_202608/max/china/qqzeng_ip_max_china.qzdb");
    bench("max_global", "../test_data_202608/max/global/qqzeng_ip_max_global.qzdb");
}
