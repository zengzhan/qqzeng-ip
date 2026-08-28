//! Fail-Closed 模糊验证：畸形 .qzdb 必须被受控拒绝，不得 panic / 不得 OOM。
//! 与 Python/Node/PHP/Go/Java/C 的同名 harness 共用同一套 4 类用例。
//! 运行：cargo run --release --example fcprobe -- <db 路径>

use std::panic;

fn probe(buf: &[u8]) -> bool {
    let r = panic::catch_unwind(|| {
        if let Ok(rd) = qzdb::QzdbReader::from_bytes(buf, 0, false) {
            let _ = rd.find("114.114.114.114");
            let _ = rd.find("8.8.8.8");
            let _ = rd.find("2400:3200::1");
            let _ = rd.find_str("1.2.3.4");
            let _ = rd.lookup_row_id("223.5.5.5");
            let _ = rd.get_field_names().to_vec();
        }
    });
    r.is_err()
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let path = args
        .get(1)
        .cloned()
        .unwrap_or_else(|| "../test_data_202608/ult/china/qqzeng_ip_ult_china.qzdb".to_string());
    let base = std::fs::read(&path).expect("读取基准库失败");
    println!("基准库: {} ({} bytes)", path, base.len());
    panic::set_hook(Box::new(|_| {})); // 静默 panic 输出，只统计次数

    let mut bad = 0usize;
    let mut cases = 0usize;

    println!("\n== 截断测试 ==");
    for &c in &[
        0usize, 1, 3, 4, 8, 15, 16, 32, 63, 64, 100, 127, 128, 160, 191, 192, 193, 200, 256, 512,
        1024, 4096, 65535, 65536, 1 << 20,
    ] {
        if c > base.len() {
            continue;
        }
        cases += 1;
        if probe(&base[..c]) {
            bad += 1;
            println!("  [FAIL] truncate@{c}");
        }
    }
    println!("  panic = {bad}");

    println!("\n== 头部 192 字节全域穷举（每字节 × 4 模式）==");
    for i in 0..192.min(base.len()) {
        for &p in &[0x00u8, 0x01, 0x7F, 0xFF] {
            let mut b = base.clone();
            b[i] = p;
            cases += 1;
            if probe(&b) {
                bad += 1;
                println!("  [FAIL] hdr[{i}]=0x{p:02X}");
            }
        }
    }
    println!("  {} 例, panic = {bad}", 192.min(base.len()) * 4);

    println!("\n== 字节洪泛（随机位翻转 2000 次）==");
    let mut seed: u64 = 0x5EED_1234;
    let span = base.len().min(512 * 1024);
    for n in 0..2000 {
        seed = seed
            .wrapping_mul(6364136223846793005)
            .wrapping_add(1442695040888963407);
        let pos = ((seed >> 17) as usize) % span;
        let bit = ((seed >> 5) as usize) % 8;
        let mut b = base.clone();
        b[pos] ^= 1 << bit;
        cases += 1;
        if probe(&b) {
            bad += 1;
            println!("  [FAIL] flip#{n}@{pos}:{bit}");
        }
    }
    println!("  panic = {bad}");

    println!("\n== 尾部随机截断 500 次 ==");
    for n in 0..500 {
        seed = seed
            .wrapping_mul(6364136223846793005)
            .wrapping_add(1442695040888963407);
        let len = ((seed >> 13) as usize) % base.len();
        cases += 1;
        if probe(&base[..len]) {
            bad += 1;
            println!("  [FAIL] rndcut#{n}@{len}");
        }
    }
    println!("  panic = {bad}");

    println!("\n==== 用例总数 = {cases} ====");
    println!(
        "==== 总 panic 数 = {bad} ({}) ====",
        if bad == 0 { "PASS" } else { "FAIL" }
    );
    std::process::exit(if bad == 0 { 0 } else { 1 });
}
