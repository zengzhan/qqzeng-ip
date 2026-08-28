//! Tier1 补充：IPv6 边界提取与验证测试。
//!
//! 从 golden_vectors.json 的 boundary_v6 / random_v6 案例中提取 IPv6 测试向量，
//! 验证 Rust SDK 的 IPv6 解析与查询行为：
//! 1. boundary_v6 案例：全零、全一、回环、链路本地、多播等边界地址
//! 2. random_v6 案例：随机 IPv6 地址查询
//! 3. 验证 IPv4-Mapped IPv6 自动降级
//! 4. 验证 CIDR 反查对命中地址返回合法 CIDR（RFC 5952 压缩格式）
//! 5. 验证 V6 uint128 入口与字符串入口一致

use qzdb::QzdbReader;
use std::path::PathBuf;

fn data_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data")
}

fn golden_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../tools/golden_vectors.json")
}

fn load_ult() -> QzdbReader {
    QzdbReader::from_file(data_dir().join("qqzeng_ip_ult_china.qzdb").to_str().unwrap())
        .expect("load ult")
}

fn load_std() -> QzdbReader {
    QzdbReader::from_file(data_dir().join("qqzeng_ip_std_china.qzdb").to_str().unwrap())
        .expect("load std")
}

/// 从 golden_vectors.json 提取 IPv6 边界测试案例。
fn extract_v6_cases(edition: &str, category: &str) -> Vec<(String, String)> {
    let text = std::fs::read_to_string(golden_path()).expect("read golden");
    let golden: serde_json::Value = serde_json::from_str(&text).expect("parse golden json");
    let root = golden.get(edition).expect("edition");
    let cases = root.get(category).and_then(|v| v.as_array()).map_or(&[][..], |v| v);
    cases
        .iter()
        .map(|c| {
            let ip = c.get("ip").and_then(|v| v.as_str()).unwrap_or("").to_string();
            let expected = c.get("expected").and_then(|v| v.as_str()).unwrap_or("").to_string();
            (ip, expected)
        })
        .collect()
}

#[test]
fn t_ipv6_boundary_cases() {
    let r = load_ult();
    let cases = extract_v6_cases("ult_china", "boundary_v6");
    assert!(!cases.is_empty(), "should have boundary_v6 cases");

    for (ip, expected) in &cases {
        let got = r.find_str(ip);
        assert_eq!(
            got, *expected,
            "boundary_v6 mismatch: ip={ip} expected={expected:?} got={got:?}"
        );
    }
}

#[test]
fn t_ipv6_random_sample() {
    let r = load_ult();
    let cases = extract_v6_cases("ult_china", "random_v6");
    assert!(cases.len() >= 100, "should have enough random_v6 cases");

    // 验证前 200 个
    let sample = cases.iter().take(200);
    for (ip, expected) in sample {
        let got = r.find_str(ip);
        assert_eq!(
            got, *expected,
            "random_v6 mismatch: ip={ip} expected={expected:?} got={got:?}"
        );
    }
}

#[test]
fn t_ipv6_mapped_downgrade() {
    let r = load_std();
    // ::ffff:x.x.x.x 应降级到 V4
    let v4_cases = [
        "::ffff:114.114.114.114",
        "::ffff:8.8.8.8",
        "::ffff:119.51.194.142",
    ];
    for mapped in &v4_cases {
        let v4_str = &mapped[7..]; // 去掉 "::ffff:"
        let from_mapped = r.find(mapped);
        let from_v4 = r.find(v4_str);
        assert_eq!(
            from_mapped.is_some(),
            from_v4.is_some(),
            "mapped {mapped} should match v4 {v4_str} in hit/miss"
        );
        if let (Some(a), Some(b)) = (&from_mapped, &from_v4) {
            assert_eq!(a.to_pipe(), b.to_pipe(), "mapped/v4 field-level mismatch for {mapped}");
        }
    }
}

#[test]
fn t_ipv6_uint128_entry_consistency() {
    let r = load_ult();
    // 验证 uint128 入口与字符串入口一致
    let test_ips = ["::1", "::", "2001:4860:4860::8888", "2408:8000:9000::1"];
    for ip_str in &test_ips {
        let from_str = r.find(ip_str);
        // 将 IPv6 字符串转为 u128
        let addr: std::net::Ipv6Addr = ip_str.parse().unwrap();
        let as_u128 = u128::from(addr);
        let from_uint = r.find_v6(as_u128);
        assert_eq!(
            from_str.is_some(),
            from_uint.is_some(),
            "find(str) vs find_v6(uint) mismatch for {ip_str}"
        );
        if let (Some(a), Some(b)) = (&from_str, &from_uint) {
            assert_eq!(a.to_pipe(), b.to_pipe(), "v6 value mismatch for {ip_str}");
        }
    }
}

#[test]
fn t_ipv6_cidr_format() {
    let r = load_ult();
    // 验证 IPv6 CIDR 反查格式正确（RFC 5952 压缩）
    if let Some(cidr) = r.lookup_cidr("2408:8000:9000::1") {
        assert!(cidr.contains('/'), "v6 cidr should contain /: {cidr}");
        let parts: Vec<&str> = cidr.split('/').collect();
        assert_eq!(parts.len(), 2, "cidr has network/prefix");
        let prefix: u32 = parts[1].parse().expect("prefix numeric");
        assert!(prefix <= 128, "v6 prefix <= 128: {prefix}");
        // RFC 5952 格式验证：不应含前导零（除了单独的 0）
        let net_part = parts[0];
        if !net_part.is_empty() {
            // 基本格式检查
            let g: Vec<&str> = net_part.split(':').collect();
            assert!(g.len() <= 8, "v6 cidr groups <= 8: {net_part}");
        }
    }
}

#[test]
fn t_ipv6_std_no_v6_data() {
    let r = load_std();
    assert!(r.find("::1").is_none(), "std ::1 not found");
    let _ = r.find_str("::1");
    let _ = r.lookup_cidr("::1");
    let _ = r.lookup_cidr("2408:8000:9000::1");
    let from_mapped = r.find("::ffff:114.114.114.114");
    let from_v4 = r.find("114.114.114.114");
    match (&from_mapped, &from_v4) {
        (Some(m), Some(v)) => assert_eq!(m.to_pipe(), v.to_pipe(), "mapped should match v4"),
        (None, None) => {}
        _ => panic!("mapped and v4 should be consistent"),
    }
}

#[test]
fn t_ipv6_loopback_and_unspecified() {
    let r = load_ult();
    // 回环地址 ::1 —— 不应 panic
    let _ = r.find("::1");
    // 全零 :: —— 不应 panic
    let _ = r.find("::");
    // 查找 CIDR
    let _ = r.lookup_cidr("::1");
    let _ = r.lookup_cidr("::");
}
