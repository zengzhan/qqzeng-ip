mod lib;
use lib::IpDbSearch;
use std::fs::File;
use std::io::{BufRead, BufReader};
use std::path::PathBuf;
use std::time::Instant;

fn main() {
    println!("正在初始化 qqzeng-ip 数据库...");
    let _ = IpDbSearch::instance();
    println!("数据库加载完成");

    // 验证逻辑
    if let Some(path) = find_test_file() {
        verify_file(path);
    }

    // --- 随机压测 ---
    let total_count = 3_000_000;
    println!("\n生成 {} 个随机 IP (UInt32)...", total_count);
    let random_ips = generate_random_ips(total_count);
    println!("生成完成，开始压测 (find_uint)...");

    let searcher = IpDbSearch::instance();
    let bench_start = Instant::now();
    
    for &ip in &random_ips {
        // Rust 接口：prefix u16, suffix u16
        let prefix = (ip >> 16) as u16;
        let suffix = (ip & 0xFFFF) as u16;
        searcher.find_uint(prefix, suffix);
    }
    
    let bench_duration = bench_start.elapsed();
    
    println!("{} 次随机查询耗时: {:.2?}", total_count, bench_duration);
    let qps = total_count as f64 / bench_duration.as_secs_f64();
    println!("QPS: {:.2}", qps);
}

fn generate_random_ips(count: usize) -> Vec<u32> {
    let mut ips = Vec::with_capacity(count);
    let mut seed: u32 = 123;
    for _ in 0..count {
        seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
        ips.push(seed);
    }
    ips
}

fn verify_file(path: PathBuf) {
    println!("正在读取测试文件: {:?}", path);
    let file = File::open(path).unwrap();
    let reader = BufReader::new(file);
    let searcher = IpDbSearch::instance();
    let mut passed = 0;
    let mut total = 0;
    
    for line in reader.lines() {
        let l = line.unwrap();
        let parts: Vec<&str> = l.split('\t').collect();
        if parts.len() < 3 { continue; }
        if searcher.find(parts[0]) == parts[2] && searcher.find(parts[1]) == parts[2] {
            passed += 1;
        }
        total += 1;
    }
    println!("验证完成: {}/{} 通过", passed, total);
}

fn find_test_file() -> Option<PathBuf> {
    let attempts = [
        PathBuf::from("../data/test.txt"),
        PathBuf::from("../../data/test.txt"),
    ];
    for p in &attempts {
        if p.exists() { return Some(p.clone()); }
    }
    None
}
