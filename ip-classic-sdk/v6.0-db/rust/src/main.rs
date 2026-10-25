//! qqzeng-ip 6.0 (.db) Rust 版冒烟示例
//! 用法: cargo run --release --bin rust [db_path] [ip ...]

use rust::IpDbSearch;
use std::time::Instant;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let db = match args.get(1).map(|p| IpDbSearch::from_path(p)).unwrap_or_else(IpDbSearch::new) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("数据库加载失败: {e} (用法: rust [db_path] [ip ...])");
            return;
        }
    };
    println!("数据库加载完成: nodes={}, strings={}", db.node_count(), db.string_count());

    for ip in ["1.10.11.12", "8.8.8.8", "58.213.198.68", "255.255.255.255", "1..2.3"] {
        println!("{ip} -> {}", db.find(ip));
    }
    for ip in &args[2..] {
        println!("{ip} -> {}", db.find(ip));
    }

    // 快速压测
    let n = 1_000_000u64;
    let mut seed: u64 = 123_456_789;
    let mut checksum = 0u64;
    let t0 = Instant::now();
    for _ in 0..n {
        seed = seed.wrapping_mul(48_271) % 2_147_483_647;
        checksum += db.find_uint32(seed as u32).len() as u64;
    }
    let ms = t0.elapsed().as_secs_f64() * 1000.0;
    println!("bench: {n} queries, {:.0} qps (checksum {checksum})", n as f64 / (ms / 1000.0));
}
