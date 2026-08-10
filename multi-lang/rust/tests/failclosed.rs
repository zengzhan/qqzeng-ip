//! Fail-Closed 安全测试（此前 8 种语言里 Rust 是唯一缺这一层的）。
//!
//! 威胁模型：`.qzdb` 文件内容**完全由攻击者控制**。
//! 注意 CRC32 **不是**安全边界——它是纠错码不是 MAC，攻击者改完数据顺手改 CRC 即可，
//! 所以本测试一律用 `verify_crc = false` 加载，模拟"CRC 已被重算过"的真实攻击面。
//!
//! 判定标准（与 C/Go 的 failclosed 一致）：
//!   畸形文件只允许 ① 返回结构化 Err ② 加载成功但查询返回空/降级结果。
//!   **绝不允许 panic / 崩溃 / OOM。**
//!
//! Rust 的类型系统已经排除了内存破坏（全 crate 零 `unsafe`，见 `#![forbid(unsafe_code)]`），
//! 因此本测试的靶子是剩余的两类风险：
//!   A. panic 型 DoS——攻击者可控 offset 参与算术，debug 构建下溢出直接 panic；
//!   B. 静默错值——release 构建下同一处算术回绕，读到错误位置却照常返回。

use std::panic;
use std::path::PathBuf;

use qzdb_reader::QzdbReader;

fn data_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data")
}

fn base_bytes() -> Option<Vec<u8>> {
    let p = data_dir().join("qqzeng_ip_std_china.qzdb");
    std::fs::read(p).ok()
}

fn put_u32(b: &mut [u8], off: usize, v: u32) {
    b[off..off + 4].copy_from_slice(&v.to_le_bytes());
}
fn put_u48(b: &mut [u8], off: usize, v: u64) {
    b[off..off + 6].copy_from_slice(&v.to_le_bytes()[..6]);
}
fn put_u64(b: &mut [u8], off: usize, v: u64) {
    b[off..off + 8].copy_from_slice(&v.to_le_bytes());
}

/// 加载 + 全量查询一遍。返回 Err(描述) 表示被拒绝；Ok(()) 表示优雅降级。
/// **本函数内部若 panic，测试即失败**——这正是我们要抓的东西。
fn load_and_hammer(bytes: Vec<u8>) -> Result<(), String> {
    let reader = QzdbReader::from_bytes(&bytes, 0, false).map_err(|e| format!("{:?}", e))?;
    // 走遍所有对外查询入口，尽量把攻击者可控的 offset 送进算术路径
    for ip in [
        "1.0.1.1",
        "114.114.114.114",
        "223.5.5.5",
        "8.8.8.8",
        "255.255.255.255",
        "0.0.0.0",
        "240e:390:1:1::1",
        "::1",
    ] {
        let _ = reader.find(ip);
        let _ = reader.find_str(ip);
        let _ = reader.lookup_cidr(ip);
        let _ = reader.lookup_row_id(ip);
        let _ = reader.find_fields(ip, &["country", "city", "asn", "longitude"]);
    }
    let _ = reader.get_edition();
    let _ = reader.get_field_names();
    let _ = reader.get_group_count();
    Ok(())
}

/// 把 `f` 跑在 catch_unwind 里，panic 视为**测试失败**并给出可读信息。
fn must_not_panic(name: &str, bytes: Vec<u8>) {
    let res = panic::catch_unwind(panic::AssertUnwindSafe(|| load_and_hammer(bytes)));
    match res {
        Ok(Ok(())) => {}                     // 优雅降级
        Ok(Err(_e)) => {}                    // 结构化拒绝
        Err(p) => {
            let msg = p
                .downcast_ref::<String>()
                .cloned()
                .or_else(|| p.downcast_ref::<&str>().map(|s| s.to_string()))
                .unwrap_or_else(|| "<non-string panic>".into());
            panic!("FAIL-CLOSED 违规：畸形输入 `{}` 触发 panic：{}", name, msg);
        }
    }
}

// ---------------------------------------------------------------------------
// 1. 截断扫描：从完整文件一路砍到 0 字节
// ---------------------------------------------------------------------------
#[test]
fn truncation_scan_never_panics() {
    let Some(base) = base_bytes() else {
        eprintln!("SKIP: 测试数据缺失");
        return;
    };
    // 头部附近逐字节砍（最容易踩解析边界），之后按几何级数抽样
    let mut lens: Vec<usize> = (0..=260.min(base.len())).collect();
    let mut n = 512;
    while n < base.len() {
        lens.push(n);
        n = n + n / 3 + 1;
    }
    lens.push(base.len());
    for len in lens {
        must_not_panic(&format!("truncate@{}", len), base[..len].to_vec());
    }
}

// ---------------------------------------------------------------------------
// 2. 192 字节头逐字节位模式穷举
// ---------------------------------------------------------------------------
#[test]
fn header_bit_patterns_never_panic() {
    let Some(base) = base_bytes() else {
        eprintln!("SKIP: 测试数据缺失");
        return;
    };
    // magic(0..4) 不动，否则一律在第一道门就被拒，测不到深层逻辑
    for off in 4..192usize {
        for pat in [0x00u8, 0x01, 0x7F, 0x80, 0xFF] {
            let mut b = base.clone();
            b[off] = pat;
            must_not_panic(&format!("header[{}]={:#04x}", off, pat), b);
        }
    }
}

// ---------------------------------------------------------------------------
// 3. 定向攻击：把攻击者可控的 offset 字段拉到极值，专打整数溢出
//
// 这一组是本文件的核心。build_geo() / resolve_fields() 里的
//   entry_off = off_geo_entries + group_entry_offsets[gi] + entry_id * group_strides[gi]
//   fo        = entry_off + offsets[i]
// 三个加数与一个乘数**全部来自文件**，且此前只校验了各自的可读性，
// 没有校验它们的**和**是否回绕。debug 构建下溢出即 panic。
// ---------------------------------------------------------------------------
#[test]
fn offset_arithmetic_overflow_never_panics() {
    let Some(base) = base_bytes() else {
        eprintln!("SKIP: 测试数据缺失");
        return;
    };

    // Header 字段偏移（FORMAT §3.1）
    const OFF_ROW_SCHEMA: usize = 40;
    const OFF_GROUP_SCHEMA: usize = 48;
    const OFF_IP_ROW: usize = 96;
    const OFF_GEO_ENTRIES: usize = 104;
    const OFF_POOLS: usize = 136;
    const IP_ROW_SIZE: usize = 160;
    const GROUP_ENTRY_OFFSETS: usize = 168; // u48 x4

    let extremes_u64 = [
        0u64,
        1,
        u32::MAX as u64,
        u64::MAX / 2,
        u64::MAX - 1,
        u64::MAX,
        0x7FFF_FFFF_FFFF_FFFF,
    ];

    for &v in &extremes_u64 {
        for &field in &[
            OFF_ROW_SCHEMA,
            OFF_GROUP_SCHEMA,
            OFF_IP_ROW,
            OFF_GEO_ENTRIES,
            OFF_POOLS,
        ] {
            let mut b = base.clone();
            put_u64(&mut b, field, v);
            must_not_panic(&format!("header_u64@{}={:#x}", field, v), b);
        }
    }

    // group_entry_offsets 是 u48，单独拉极值
    for &v in &[0u64, 1, u32::MAX as u64, 0xFFFF_FFFF_FFFFu64] {
        for i in 0..4usize {
            let mut b = base.clone();
            put_u48(&mut b, GROUP_ENTRY_OFFSETS + i * 6, v);
            must_not_panic(&format!("group_entry_offset[{}]={:#x}", i, v), b);
        }
    }

    // ip_row_size 极值（合法区间 [1,64]，越界应被头部校验拒绝）
    for v in [0u32, 1, 63, 64, 65, u32::MAX] {
        let mut b = base.clone();
        put_u32(&mut b, IP_ROW_SIZE, v);
        must_not_panic(&format!("ip_row_size={}", v), b);
    }

    // 组合拳：同时把 off_geo_entries 与 group_entry_offsets[0] 顶到高位，
    // 让二者之和在 usize 上回绕。
    for &(a, c) in &[
        (u64::MAX, 0xFFFF_FFFF_FFFFu64),
        (u64::MAX - 4096, 0xFFFF_FFFF_FFFFu64),
        (0xFFFF_FFFF_FFFF_0000u64, 0xFFFF_FFFF_FFFFu64),
    ] {
        let mut b = base.clone();
        put_u64(&mut b, OFF_GEO_ENTRIES, a);
        put_u48(&mut b, GROUP_ENTRY_OFFSETS, c);
        must_not_panic(&format!("combo geo={:#x} entoff={:#x}", a, c), b);
    }
}

// ---------------------------------------------------------------------------
// 3b. 精确构造的整数溢出攻击（随机变异打不中，必须手工协同 6 个字段）
//
//   entry_off = off_geo_entries + group_entry_offsets[0] + entry_id * group_strides[0]
//
// 取 entry_id = 0xFFFF_FFFE、group_strides[0] = 0xFFFF_FFFF，乘积 ≈ 1.8446744061e19
// 仍在 usize 内；再加上 group_entry_offsets[0] = 0xFFFF_FFFF_FFFF 就越过
// usize::MAX = 1.8446744074e19 —— debug 构建下这一步直接 panic。
//
// 为了让 entry_id 能取到 u32 级别，必须先把 ROW_SCHEMA 改成"单字段、宽度 4"，
// 并让 ip_row_size 与之一致（解析器要求 total == ip_row_size 且 width ∈ [1,4]）。
// ---------------------------------------------------------------------------
#[test]
fn crafted_entry_offset_overflow_never_panics() {
    let Some(base) = base_bytes() else {
        eprintln!("SKIP: 测试数据缺失");
        return;
    };
    let read_u64 = |b: &[u8], o: usize| {
        let mut a = [0u8; 8];
        a.copy_from_slice(&b[o..o + 8]);
        u64::from_le_bytes(a)
    };

    let mut b = base.clone();
    let off_row_schema = read_u64(&b, 40) as usize;
    let off_group_schema = read_u64(&b, 48) as usize;
    let off_ip_row = read_u64(&b, 96) as usize;
    let off_geo_entries = read_u64(&b, 104) as usize;
    if off_row_schema == 0 || off_group_schema == 0 || off_ip_row == 0 || off_geo_entries == 0 {
        eprintln!("SKIP: 基准库缺少必要 section");
        return;
    }

    // ① ip_row_size = 4，令 geo 字段能吃满 u32
    put_u32(&mut b, 160, 4);
    // ② ROW_SCHEMA：field_count=1, stride=4, 唯一字段 fid=0(geo) width=4
    b[off_row_schema] = 1;
    b[off_row_schema + 1] = 4;
    b[off_row_schema + 4] = 0;
    b[off_row_schema + 5] = 4;
    // ③ GROUP_SCHEMA.stride[0] 顶满。
    //    段内游标：+2 group_count，+2 groupId，+2 fld_count，+4 保留 → stride 落在 +10。
    put_u32(&mut b, off_group_schema + 10, u32::MAX);
    // ④ GEO_ENTRIES.group_entry_count[0] 顶满，放行超大 entry_id
    put_u32(&mut b, off_geo_entries + 2, u32::MAX);
    // ⑤ group_entry_offsets[0] 顶满（u48）
    put_u48(&mut b, 168, 0xFFFF_FFFF_FFFF);
    // ⑥ 整个 IP_ROW 段填成 geo_id = 0xFFFF_FFFE（须 < entry_count 才不会被提前挡掉）
    let ip_row_end = off_geo_entries.min(b.len());
    let mut p = off_ip_row;
    while p + 4 <= ip_row_end {
        b[p..p + 4].copy_from_slice(&0xFFFF_FFFEu32.to_le_bytes());
        p += 4;
    }

    must_not_panic("crafted: entry_off 整数溢出", b);
}

// ---------------------------------------------------------------------------
// 4. GEO_ENTRIES / GROUP_SCHEMA 内部字段的定向变异
//
// 这些字段位于 header 之后，是"结构基本合法、但内部被恶意构造"的那一类，
// 风险高于 header 阶段就被拒的输入——因为它们会一路走到 pool / trie 解析。
// ---------------------------------------------------------------------------
#[test]
fn section_internal_mutation_never_panics() {
    let Some(base) = base_bytes() else {
        eprintln!("SKIP: 测试数据缺失");
        return;
    };
    let read_u64 = |b: &[u8], o: usize| {
        let mut a = [0u8; 8];
        a.copy_from_slice(&b[o..o + 8]);
        u64::from_le_bytes(a)
    };

    for &hdr_off in &[48usize /* GROUP_SCHEMA */, 104 /* GEO_ENTRIES */, 136 /* POOLS */] {
        let sec = read_u64(&base, hdr_off) as usize;
        if sec == 0 || sec >= base.len() {
            continue;
        }
        // 段头 64 字节逐字节位模式穷举
        let end = (sec + 64).min(base.len());
        for p in sec..end {
            for pat in [0x00u8, 0xFF, 0x80, 0x7F] {
                let mut b = base.clone();
                b[p] = pat;
                must_not_panic(&format!("section@{}[{}]={:#04x}", hdr_off, p - sec, pat), b);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// 5. 随机翻位（确定性 LCG，失败可复现）
// ---------------------------------------------------------------------------
#[test]
fn random_bitflips_never_panic() {
    let Some(base) = base_bytes() else {
        eprintln!("SKIP: 测试数据缺失");
        return;
    };
    let span = base.len().min(512 * 1024);
    let mut s: u64 = 0x2026_0810;
    let mut next = move || {
        s = s.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        s >> 33
    };
    for round in 0..2000 {
        let mut b = base.clone();
        let flips = 1 + (next() as usize % 4);
        for _ in 0..flips {
            let pos = next() as usize % span;
            let bit = next() as u32 % 8;
            b[pos] ^= 1u8 << bit;
        }
        must_not_panic(&format!("bitflip round {}", round), b);
    }
}

// ---------------------------------------------------------------------------
// 6. 全零 / 全 FF / 只有 magic 的退化输入
// ---------------------------------------------------------------------------
#[test]
fn degenerate_inputs_never_panic() {
    for (name, bytes) in [
        ("empty", Vec::new()),
        ("magic_only", b"QZDB".to_vec()),
        ("zeros_192", vec![0u8; 192]),
        ("ff_192", vec![0xFFu8; 192]),
        ("magic_then_zeros", {
            let mut v = b"QZDB".to_vec();
            v.resize(v.len() + 1024, 0u8);
            v
        }),
        ("magic_then_ff", {
            let mut v = b"QZDB".to_vec();
            v.push(1); // format version = 1，越过版本门
            v.resize(v.len() + 4096, 0xFFu8);
            v
        }),
    ] {
        must_not_panic(name, bytes);
    }
}
