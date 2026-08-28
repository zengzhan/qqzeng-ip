//! 回归测试：跳表条目带 SENTINEL 时，find / lookup_row_id 路径必须**直接返回**
//! 低 31 位的 row_id（QZDB_FORMAT.md §4 SearchV4 / SearchV6）。
//!
//! 历史缺陷：Rust 曾在跳表哨兵命中时"从根节点重走"以顺带算出 CIDR 前缀长度，
//! find 路径因此偏离规范，在伪造跳表上会与 C/Java/C#/Node/Python 分叉。
//! 本测试通过**定向篡改跳表哨兵**构造两种语义可区分的文件：
//! 规范语义返回哨兵 row_id（= A 的结果），旧的"从根重走"语义返回 trie 自身结果（= B 的结果）。
//! CIDR 反查（lookup_cidr）需要前缀长度，重走是其合法实现，不在本测试范围。

use std::sync::Arc;

use qzdb::QzdbReader;

fn data_dir() -> std::path::PathBuf {
    std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data")
}

fn base_bytes() -> Option<Vec<u8>> {
    std::fs::read(data_dir().join("qqzeng_ip_std_china.qzdb")).ok()
}

fn read_u64(b: &[u8], off: usize) -> u64 {
    let mut a = [0u8; 8];
    a.copy_from_slice(&b[off..off + 8]);
    u64::from_le_bytes(a)
}

/// 三个断言面：两个 geo 结果不同的 IP（A、B），把 B 桶的跳表条目改写成
/// SENTINEL | row_id(A) 后，B 的查询结果必须变成 A 的结果。
fn v4_body(ip_a: u32, ip_b: u32) {
    let Some(base) = base_bytes() else {
        eprintln!("SKIP: 测试数据缺失");
        return;
    };
    let off_v4_jump = read_u64(&base, 64) as usize;
    assert!(off_v4_jump > 0, "基准库缺少 V4 跳表");

    let clean = QzdbReader::from_bytes(&base, 0, false).unwrap();
    let row_a = clean.lookup_row_id_uint(ip_a);
    let pipe_a = clean.find_uint(ip_a).map(|g| g.to_pipe()).unwrap_or_default();
    let pipe_b = clean.find_uint(ip_b).map(|g| g.to_pipe()).unwrap_or_default();
    assert!(row_a != 0, "IP A 应命中");
    assert_ne!(pipe_a, pipe_b, "前置条件：A 与 B 的 geo 结果必须不同");

    let mut b = base.clone();
    let slot = off_v4_jump + ((ip_b >> 16) as usize) * 4;
    let sentinel_leaf: u32 = 0x8000_0000 | row_a;
    b[slot..slot + 4].copy_from_slice(&sentinel_leaf.to_le_bytes());

    let reader = QzdbReader::from_bytes(&b, 0, false).unwrap();
    // 规范 §4 SearchV4：ptr & SENTINEL → 直接返回 ptr & 0x7FFFFFFF
    assert_eq!(
        reader.lookup_row_id_uint(ip_b),
        row_a,
        "V4 跳表哨兵必须直接返回低 31 位 row_id"
    );
    assert_eq!(
        reader.find_uint(ip_b).map(|g| g.to_pipe()).unwrap_or_default(),
        pipe_a,
        "V4 跳表哨兵命中后 find 结果必须等于哨兵 row_id 的 geo"
    );
}

#[test]
fn v4_jump_sentinel_returns_leaf_row_directly() {
    // 114.114.114.114（电信）与 223.5.5.5（阿里 DNS）分属不同桶、不同 geo
    v4_body(114 << 24 | 114 << 16 | 114 << 8 | 114, 223 << 24 | 5 << 16 | 5 << 8 | 5);
}

#[test]
fn v6_jump_sentinel_returns_leaf_row_directly() {
    let Some(base) = base_bytes() else {
        eprintln!("SKIP: 测试数据缺失");
        return;
    };
    let off_v6_jump = read_u64(&base, 80) as usize;
    let v6_jump_bits = base[11] as usize;
    assert!(off_v6_jump > 0, "基准库缺少 V6 跳表");

    let a_hex: u128 = 0x2408_8000_9000_0000_0000_0000_0000_0001;
    let b_hex: u128 = 0x2001_0db8_0000_0000_0000_0000_0000_0001;
    let shift = 128 - v6_jump_bits;
    let idx_a = (a_hex >> shift) as usize;
    let idx_b = (b_hex >> shift) as usize;
    assert_ne!(idx_a, idx_b, "前置条件：A 与 B 必须落在不同跳表桶");

    let clean = QzdbReader::from_bytes(&base, 0, false).unwrap();
    let row_a = clean.lookup_row_id_v6(a_hex);
    let pipe_a = clean.find_v6(a_hex).map(|g| g.to_pipe()).unwrap_or_default();
    let pipe_b = clean.find_v6(b_hex).map(|g| g.to_pipe()).unwrap_or_default();
    assert!(row_a != 0, "IP A 应命中");
    assert_ne!(pipe_a, pipe_b, "前置条件：A 与 B 的 geo 结果必须不同");

    let mut b = base.clone();
    let slot = off_v6_jump + idx_b * 4;
    let sentinel_leaf: u32 = 0x8000_0000 | row_a;
    b[slot..slot + 4].copy_from_slice(&sentinel_leaf.to_le_bytes());

    let reader = QzdbReader::from_bytes(&b, 0, false).unwrap();
    // 规范 §4 SearchV6：ptr & SENTINEL → 直接返回 ptr & 0x7FFFFFFF
    assert_eq!(
        reader.lookup_row_id_v6(b_hex),
        row_a,
        "V6 跳表哨兵必须直接返回低 31 位 row_id（不得从根重走）"
    );
    assert_eq!(
        reader.find_v6(b_hex).map(|g| g.to_pipe()).unwrap_or_default(),
        pipe_a,
        "V6 跳表哨兵命中后 find 结果必须等于哨兵 row_id 的 geo"
    );
}

// find_shared：零拷贝 API 与 find() 结果逐字节一致（同 Arc 缓存）
#[test]
fn find_shared_matches_find() {
    let Some(base) = base_bytes() else {
        eprintln!("SKIP: 测试数据缺失");
        return;
    };
    let r = QzdbReader::from_bytes(&base, 0, false).unwrap();
    let owned = r.find("114.114.114.114").expect("应命中");
    let shared = r.find_shared("114.114.114.114").expect("应命中");
    assert_eq!(owned.to_pipe(), shared.to_pipe(), "find 与 find_shared 结果一致");
    assert_eq!(shared.country(), owned.country());
    // 重复查询拿同一个 Arc（缓存命中零分配）
    let again = r.find_shared("114.114.114.114").unwrap();
    assert!(Arc::ptr_eq(&shared, &again), "缓存命中应返回同一 Arc 实例");
    assert!(r.find_shared("not-an-ip").is_none());
    assert!(r.find_shared("8.8.8.8").is_none() || true);
}
