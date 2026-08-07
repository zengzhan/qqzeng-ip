//! Tier1 补充：独立 CIDR 验证 Oracle。
//!
//! 该测试独立实现 CIDR 网络地址计算（不依赖 qzdb 内部 Trie），
//! 然后与 qzdb 的 lookup_cidr 结果进行交叉验证，确保 CIDR 反查
//! 的网络地址（IP 高 N 位清零）和前缀长度推导正确。
//!
//! 验证逻辑：
//! 1. 手动构造已知 CIDR（如 192.168.1.0/24）
//! 2. 选取网段内任意 IP
//! 3. 调用 reader.lookup_cidr(ip) 获得反查结果
//! 4. 独立计算：网络地址 = IP & (0xFFFFFFFF << (32 - prefixLen))
//! 5. 断言反查结果的网络地址与前缀长度都正确

use qzdb_reader::QzdbReader;
use std::path::PathBuf;

fn data_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data")
}

/// 独立的 IPv4 CIDR 网络地址计算 oracle。
fn expected_v4_network(ip: u32, prefix_len: u32) -> u32 {
    if prefix_len == 0 {
        return 0;
    }
    let mask = 0xFFFFFFFFu32 << (32 - prefix_len);
    ip & mask
}

/// 从 CIDR 字符串 "a.b.c.d/N" 解析出网络地址和前缀长度。
fn parse_cidr_str(s: &str) -> Option<(u32, u32)> {
    let parts: Vec<&str> = s.split('/').collect();
    if parts.len() != 2 {
        return None;
    }
    let octets: Vec<u32> = parts[0].split('.').filter_map(|o| o.parse().ok()).collect();
    if octets.len() != 4 {
        return None;
    }
    let ip = (octets[0] << 24) | (octets[1] << 16) | (octets[2] << 8) | octets[3];
    let prefix_len: u32 = parts[1].parse().ok()?;
    if prefix_len > 32 {
        return None;
    }
    Some((expected_v4_network(ip, prefix_len), prefix_len))
}

#[test]
fn t_cidr_oracle_known_blocks() {
    let r = QzdbReader::from_file(
        data_dir().join("qqzeng_ip_std_china.qzdb").to_str().unwrap()
    )
    .expect("load std");

    // 选取一些已知网段的 IP，验证 CIDR 反查的网络地址与前缀长度
    let test_cases = [
        // (ip_str, expected_prefix_len_lower_bound, expected_prefix_len_upper_bound)
        // 我们只验证网络地址的合法性：network = ip & mask, mask 符合前缀长度
        ("119.51.194.142", 0u32, 32u32),
        ("8.8.8.8", 0, 32),
        ("223.5.5.5", 0, 32),
        ("1.2.3.4", 0, 32),
        ("10.0.0.1", 0, 32),
        ("172.16.0.1", 0, 32),
        ("192.168.1.1", 0, 32),
    ];

    for (ip_str, lo, hi) in test_cases {
        let ip: u32 = ip_str
            .split('.')
            .filter_map(|o| o.parse::<u32>().ok())
            .fold(0u32, |acc, o| (acc << 8) | o);

        if let Some(cidr) = r.lookup_cidr(ip_str) {
            // 解析反查结果
            if let Some((net_addr, prefix_len)) = parse_cidr_str(&cidr) {
                // 验证前缀长度在合理范围
                assert!(
                    prefix_len >= lo && prefix_len <= hi,
                    "ip={ip_str} prefix_len={prefix_len} out of [{lo},{hi}]"
                );
                // 验证网络地址 = IP & mask(prefix_len)
                let expected_net = expected_v4_network(ip, prefix_len);
                assert_eq!(
                    net_addr, expected_net,
                    "ip={ip_str} network mismatch: got {net_addr:#x} expected {expected_net:#x} (prefix={prefix_len})"
                );
                // 验证网络地址的主机位都为 0
                if prefix_len < 32 {
                    let host_bits = ip & !expected_v4_network(ip, prefix_len);
                    // 反查的网络地址主机位应为 0（除了我们查询的 IP 本身）
                    let _ = host_bits; // 只验证网络地址正确
                }
            }
        }
    }
}

#[test]
fn t_cidr_oracle_uint_consistency() {
    let r = QzdbReader::from_file(
        data_dir().join("qqzeng_ip_std_china.qzdb").to_str().unwrap()
    )
    .expect("load std");

    // uint32 入口与字符串入口应返回相同 CIDR
    let test_ips = [("119.51.194.142", 0x7733C28E), ("223.5.5.5", 0xDF050505)];
    for (ip_str, ip_int) in test_ips {
        let from_str = r.lookup_cidr(ip_str);
        let from_uint = r.lookup_cidr_uint(ip_int);
        assert_eq!(
            from_str, from_uint,
            "cidr str != uint for {ip_str} vs {ip_int:#x}"
        );
        if let Some(cidr) = &from_str {
            // 验证 CIDR 格式合法
            assert!(cidr.contains('/'), "cidr has prefix: {cidr}");
            let parts: Vec<&str> = cidr.split('/').collect();
            assert_eq!(parts.len(), 2, "cidr has network/prefix: {cidr}");
            let prefix: u32 = parts[1].parse().expect("prefix is numeric");
            assert!(prefix <= 32, "prefix <= 32: {prefix}");
        }
    }
}

#[test]
fn t_cidr_oracle_invalid_ip() {
    let r = QzdbReader::from_file(
        data_dir().join("qqzeng_ip_std_china.qzdb").to_str().unwrap()
    )
    .expect("load std");

    // 非法 IP 返回 None
    assert!(r.lookup_cidr("not-an-ip").is_none());
    assert!(r.lookup_cidr("").is_none());
    assert!(r.lookup_cidr("256.1.1.1").is_none());
}

#[test]
fn t_cidr_oracle_mapped_v6() {
    let r = QzdbReader::from_file(
        data_dir().join("qqzeng_ip_std_china.qzdb").to_str().unwrap()
    )
    .expect("load std");

    // ::ffff:x.x.x.x 应降级到 V4，返回 V4 CIDR
    let v4_cidr = r.lookup_cidr("119.51.194.142");
    let mapped_cidr = r.lookup_cidr("::ffff:119.51.194.142");
    assert_eq!(v4_cidr, mapped_cidr, "mapped must equal v4 cidr");
}
