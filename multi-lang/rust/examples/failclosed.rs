//! Fail-Closed 验证：畸形 / 截断 / 位翻转的 .qzdb 必须返回 Err，绝不 panic。
use qzdb::QzdbReader;

fn probe(label: &str, bytes: &[u8]) {
    let r = std::panic::catch_unwind(|| QzdbReader::from_bytes(bytes, 0, false));
    match r {
        Ok(Ok(rd)) => {
            // 打开成功也要保证查询不 panic
            let q = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                rd.find("114.114.114.114").map(|g| g.to_string())
            }));
            match q {
                Ok(v) => println!("  [OK-open ] {label}: opened, query={:?}", v.is_some()),
                Err(_) => println!("  [PANIC!! ] {label}: query panicked"),
            }
        }
        Ok(Err(e)) => println!("  [OK-err  ] {label}: {}", e),
        Err(_) => println!("  [PANIC!! ] {label}: open panicked"),
    }
}

fn main() {
    std::panic::set_hook(Box::new(|_| {})); // 静音 panic 回溯，只看结论
    let src = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "../test_data_202608/ult/china/qqzeng_ip_ult_china.qzdb".into());
    let full = std::fs::read(&src).expect("read source db");

    println!("== 截断测试 ==");
    for n in [0usize, 3, 100, 191, 192, 200, 250, 500, 5000, 100_000, 1_000_000] {
        if n > full.len() {
            continue;
        }
        probe(&format!("truncate {n:>9} B"), &full[..n]);
    }

    println!("== 头部偏移伪造测试 ==");
    // 把各 section 偏移改成极端值，检验 checked_add / 边界收口
    let offsets: [(usize, &str); 8] = [
        (40, "off_row_schema"),
        (48, "off_group_schema"),
        (96, "off_ip_row"),
        (104, "off_geo_entries"),
        (136, "off_pools"),
        (144, "off_meta"),
        (64, "off_v4_jump"),
        (72, "off_v4_nodes"),
    ];
    for (pos, name) in offsets {
        for (pat, tag) in [
            (u64::MAX, "u64::MAX"),
            (u64::MAX - 7, "MAX-7"),
            (0x7FFF_FFFF_FFFF_FFFFu64, "i64::MAX"),
            (full.len() as u64 - 1, "len-1"),
        ] {
            let mut m = full.clone();
            m[pos..pos + 8].copy_from_slice(&pat.to_le_bytes());
            probe(&format!("{name}={tag}"), &m);
        }
    }

    println!("== 计数字段伪造测试 ==");
    for (pos, width, name) in [
        (14usize, 2usize, "geo_count"),
        (20, 4, "row_count"),
        (152, 4, "v4_node_count"),
        (156, 4, "v6_node_count"),
        (160, 4, "ip_row_size"),
        (164, 4, "geo_entry_group_count"),
    ] {
        let mut m = full.clone();
        for i in 0..width {
            m[pos + i] = 0xFF;
        }
        probe(&format!("{name}=ALL_FF"), &m);
    }

    println!("== 头部 192 字节全域穷举（每字节 × 4 种模式）==");
    let mut hdr_panics = 0;
    let mut hdr_cases = 0;
    for pos in 4..192usize {
        for pat in [0x00u8, 0xFF, 0x7F, 0x80] {
            let mut m = full.clone();
            m[pos] = pat;
            hdr_cases += 1;
            let r = std::panic::catch_unwind(|| {
                QzdbReader::from_bytes(&m, 0, false).map(|rd| rd.find("114.114.114.114").is_some())
            });
            if r.is_err() {
                hdr_panics += 1;
                println!("  [PANIC!! ] header byte {pos} = 0x{pat:02X}");
            }
        }
    }
    println!("  头部穷举 {hdr_cases} 例，panic 次数 = {hdr_panics}");

    println!("== 字节洪泛（随机位翻转）==");
    let mut seed = 0x9E3779B97F4A7C15u64;
    let mut panics = 0;
    let n_iter = 2000;
    for i in 0..n_iter {
        seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        // 前 512KB 命中 header / schema / jump / node 等结构区，最易触发解析分支
        let pos = (seed >> 16) as usize % full.len().min(512 * 1024);
        let mut m = full.clone();
        m[pos] ^= 0xFF;
        let r = std::panic::catch_unwind(|| {
            QzdbReader::from_bytes(&m, 0, false).map(|rd| rd.find("114.114.114.114").is_some())
        });
        if r.is_err() {
            panics += 1;
            println!("  [PANIC!! ] flip #{i} @ byte {pos}");
        }
    }
    println!("  位翻转 {n_iter} 次，panic 次数 = {panics}");

    println!("== 尾部随机截断 ==");
    let mut tpanics = 0;
    for i in 0..500 {
        seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        let n = (seed >> 16) as usize % full.len();
        let r = std::panic::catch_unwind(|| {
            QzdbReader::from_bytes(&full[..n], 0, false).map(|rd| rd.find("8.8.8.8").is_some())
        });
        if r.is_err() {
            tpanics += 1;
            println!("  [PANIC!! ] truncate #{i} @ {n} B");
        }
    }
    println!("  随机截断 500 次，panic 次数 = {tpanics}");

    let total = hdr_panics + panics + tpanics;
    println!("\n==== 总 panic 数 = {total} ({}) ====", if total == 0 { "PASS" } else { "FAIL" });
    if total != 0 {
        std::process::exit(1);
    }
}
