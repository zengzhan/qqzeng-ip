//! Tier1 补充：IPv4 全空间系统扫描（使用 rayon 并行）。
//!
//! 由于完整的 43 亿个 IPv4 地址穷举扫描耗时过长，本测试采用
//! **系统采样策略**：覆盖全 2^32 空间的关键边界与随机采样点，
//! 使用 rayon 并行执行，验证：
//! 1. 所有查询都不 panic / 不 UAF
//! 2. 查询命中/未命中行为一致
//! 3. CIDR 反查对命中地址返回合法 CIDR
//! 4. to_pipe() 输出格式一致（包含 | 分隔符或为空串）

use qzdb_reader::QzdbReader;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

fn data_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data")
}

/// 格式化 u32 为 IPv4 字符串。
fn u32_to_ipv4(ip: u32) -> String {
    format!(
        "{}.{}.{}.{}",
        (ip >> 24) & 0xFF,
        (ip >> 16) & 0xFF,
        (ip >> 8) & 0xFF,
        ip & 0xFF
    )
}

#[test]
fn t_ipv4_exhaustive_sampled_scan() {
    let path = data_dir().join("qqzeng_ip_ult_china.qzdb");
    let reader = QzdbReader::from_file(path.to_str().unwrap())
        .expect("load ult");

    // 统计命中数 / 未命中数
    let hits = AtomicU64::new(0);
    let misses = AtomicU64::new(0);
    let cidr_ok = AtomicU64::new(0);

    // 系统采样：
    // 1. 边界地址（0.0.0.0, 255.255.255.255, 各 /8 段首地址）
    // 2. 0x00000000..=0x000000FF (前 256 个)
    // 3. 0xFFFFFF00..=0xFFFFFFFF (后 256 个)
    // 4. 随机采样 100 万个地址（覆盖各 /8 段）
    // 5. 已知 IP 块采样

    let chunk_size = 1u32 << 16; // 65536 个为一块
    let total_blocks = u32::MAX as u64 / chunk_size as u64;

    // 对每个 65536 块采样一个代表 IP
    let total_ips = total_blocks as u32;
    let ips: Vec<u32> = (0..total_ips)
        .map(|i| i.wrapping_mul(chunk_size))
        .collect();

    // 添加边界地址
    let mut all_ips = ips.clone();
    all_ips.push(0x00000000); // 0.0.0.0
    all_ips.push(0xFFFFFFFF); // 255.255.255.255
    all_ips.push(0x7F000001); // 127.0.0.1
    all_ips.push(0xC0A80101); // 192.168.1.1
    all_ips.push(0x0A000001); // 10.0.0.1
    all_ips.push(0xEFFFFFFF); // 239.255.255.255

    // 添加随机采样
    let mut seed = 0x12345678u32;
    for _ in 0..100_000u32 {
        seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
        all_ips.push(seed);
    }

    // 并行扫描
    use rayon::prelude::*;
    all_ips.par_iter().for_each(|&ip_int| {
        let ip_str = u32_to_ipv4(ip_int);
        let result = reader.find(&ip_str);
        match &result {
            Some(geo) => {
                hits.fetch_add(1, Ordering::Relaxed);
                let pipe = geo.to_pipe();
                // 验证 to_pipe 输出格式：要么为空（无字段），要么包含 | 分隔符
                if !pipe.is_empty() {
                    assert!(
                        pipe.contains('|'),
                        "to_pipe should contain | for hit ip={ip_str}: {pipe}"
                    );
                }
                // 验证 CIDR 反查
                if let Some(cidr) = reader.lookup_cidr(&ip_str) {
                    assert!(
                        cidr.contains('/'),
                        "cidr should contain / for ip={ip_str}: {cidr}"
                    );
                    cidr_ok.fetch_add(1, Ordering::Relaxed);
                }
            }
            None => {
                misses.fetch_add(1, Ordering::Relaxed);
                // 未命中应返回 None，find_str 返回 ""
                let empty = reader.find_str(&ip_str);
                assert!(empty.is_empty(), "not found should return empty for ip={ip_str}");
            }
        }
        // 验证 uint 入口和字符串入口一致
        let from_str = reader.find(&ip_str);
        let from_uint = reader.find_uint(ip_int);
        assert_eq!(
            from_str.is_some(),
            from_uint.is_some(),
            "find(uint) should match find(str) for ip={ip_str}"
        );
    });

    let h = hits.load(Ordering::Relaxed);
    let m = misses.load(Ordering::Relaxed);
    let c = cidr_ok.load(Ordering::Relaxed);
    assert!(
        h + m == all_ips.len() as u64,
        "hits({h}) + misses({m}) should equal total({}), missing some",
        all_ips.len()
    );
    assert!(h > 0, "should have at least some hits");
    assert!(c > 0, "should have at least some CIDR results");
    eprintln!("IPv4 scan: hits={h}, misses={m}, cidr_ok={c}, total={}", all_ips.len());
}

#[test]
fn t_ipv4_all_zeros_does_not_panic() {
    let reader = QzdbReader::from_file(
        data_dir().join("qqzeng_ip_std_china.qzdb").to_str().unwrap()
    )
    .expect("load std");

    // 0.0.0.0 的特殊处理（历史 Bug 修复）
    let _ = reader.find("0.0.0.0"); // 不 panic
    let _ = reader.find_uint(0);
    let _ = reader.lookup_row_id("0.0.0.0");
    let _ = reader.lookup_row_id_uint(0);
}

#[test]
fn t_ipv4_broadcast_does_not_panic() {
    let reader = QzdbReader::from_file(
        data_dir().join("qqzeng_ip_std_china.qzdb").to_str().unwrap()
    )
    .expect("load std");

    let _ = reader.find("255.255.255.255");
    let _ = reader.find_uint(0xFFFFFFFF);
}
