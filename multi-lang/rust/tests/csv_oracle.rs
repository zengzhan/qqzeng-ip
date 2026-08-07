//! Tier2 补充：独立 CSV 地面真值 Oracle（与 Python/Go/PHP 同标准）。
//!
//! 以 `.qzdb` 的源数据 `test_data_202608/{std,ult}/china/*_range.csv` 为裁判
//! （带 start_ip_num/end_ip_num + 地理字段），对 std_china / ult_china 两个库
//! 各自做：区间内随机 + 全局随机 共约 11000 样本，比对 SDK find() 的
//! country/province/city/isp 与 CSV 一致。证明 SDK "答得对"（非自洽）。
//!
//! 解析器逐字段处理双引号与内嵌逗号（ult 的 languages 字段即 `"a,b,c"`）。

use qzdb_reader::QzdbReader;
use std::collections::HashMap;
use std::path::PathBuf;

fn data_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data")
}
fn testdata_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../test_data_202608")
}

/// 确定性 LCG（可复现抽样）。
struct Lcg(u64);
impl Lcg {
    fn new(seed: u64) -> Self { Lcg(seed) }
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        self.0 >> 33
    }
    fn below(&mut self, n: u64) -> u64 {
        if n == 0 { 0 } else { self.next() % n }
    }
}

struct Range {
    start: u32,
    end: u32,
    country: String,
    province: String,
    city: String,
    isp: String,
}

/// 逐字段解析一行 CSV（按字节处理，正确保留 UTF-8 多字节字符如中文），
/// 正确处理双引号与内嵌逗号 / `""` 转义。
fn parse_csv_line(line: &[u8], out: &mut Vec<String>) {
    out.clear();
    let b = line;
    let n = b.len();
    let mut i = 0usize;
    let mut field: Vec<u8> = Vec::new();
    while i < n {
        if b[i] == b'"' {
            i += 1;
            while i < n {
                if b[i] == b'"' {
                    if i + 1 < n && b[i + 1] == b'"' {
                        field.push(b'"');
                        i += 2;
                    } else {
                        i += 1;
                        break;
                    }
                } else {
                    field.push(b[i]);
                    i += 1;
                }
            }
        } else if b[i] == b',' {
            out.push(String::from_utf8_lossy(&field).into_owned());
            field.clear();
            i += 1;
        } else {
            field.push(b[i]);
            i += 1;
        }
    }
    out.push(String::from_utf8_lossy(&field).into_owned());
}

fn load_ranges(path: &std::path::Path) -> Option<Vec<Range>> {
    let f = std::fs::File::open(path).ok()?;
    use std::io::BufRead;
    let reader = std::io::BufReader::new(f);
    let mut lines = reader.lines();
    let header = match lines.next() {
        Some(Ok(h)) => h,
        _ => return None,
    };
    let mut cols: Vec<String> = Vec::new();
    parse_csv_line(header.as_bytes(), &mut cols);
    let mut idx: HashMap<String, usize> = HashMap::new();
    for (i, c) in cols.iter().enumerate() {
        idx.insert(c.clone(), i);
    }
    let ci = |k: &str| idx.get(k).copied();
    let (si, ei, coi, pri, cii, isi) = (
        ci("start_ip_num"),
        ci("end_ip_num"),
        ci("country"),
        ci("province"),
        ci("city"),
        ci("isp"),
    );
    let (si, ei, coi, pri, cii, isi) = match (si, ei, coi, pri, cii, isi) {
        (Some(a), Some(b), Some(c), Some(d), Some(e), Some(f)) => (a, b, c, d, e, f),
        _ => return None,
    };
    let mut ranges = Vec::new();
    for line in lines {
        let line = match line {
            Ok(l) => l,
            Err(_) => continue,
        };
        if line.trim().is_empty() {
            continue;
        }
        let mut flds: Vec<String> = Vec::new();
        parse_csv_line(line.as_bytes(), &mut flds);
        let get = |i: usize| -> String { flds.get(i).cloned().unwrap_or_default() };
        let start: u64 = get(si).trim().parse().unwrap_or(0);
        let end: u64 = get(ei).trim().parse().unwrap_or(0);
        if start == 0 && end == 0 {
            continue;
        }
        if start > u32::MAX as u64 || end > u32::MAX as u64 {
            continue;
        }
        ranges.push(Range {
            start: start as u32,
            end: end as u32,
            country: get(coi),
            province: get(pri),
            city: get(cii),
            isp: get(isi),
        });
    }
    if ranges.is_empty() {
        None
    } else {
        Some(ranges)
    }
}

/// 二分：返回满足 start <= ip 的最大区间下标；若 ip 不在该区间内返回 -1。
fn find_range(ranges: &[Range], ip: u32) -> i64 {
    let (mut lo, mut hi) = (0i64, (ranges.len() as i64) - 1);
    let mut ans = -1i64;
    while lo <= hi {
        let mid = (lo + hi) / 2;
        if ranges[mid as usize].start <= ip {
            ans = mid;
            lo = mid + 1;
        } else {
            hi = mid - 1;
        }
    }
    if ans >= 0 && ranges[ans as usize].end >= ip {
        ans
    } else {
        -1
    }
}

fn eq(a: &str, b: &str) -> bool {
    a.trim() == b.trim()
}

#[test]
fn t_csv_oracle_std_china() {
    let db = data_dir().join("qqzeng_ip_std_china.qzdb");
    let csv = testdata_dir().join("std/china/qqzeng_ip_std_china_range.csv");
    let (checked, mism) = run_one(&db, &csv, 0x9E3779B97F4A7C15);
    assert_eq!(mism, 0, "std_china: {checked} checked, {mism} mismatched");
    println!("csv_oracle std_china: checked={checked} mism={mism}");
}

#[test]
fn t_csv_oracle_ult_china() {
    let db = data_dir().join("qqzeng_ip_ult_china.qzdb");
    let csv = testdata_dir().join("ult/china/qqzeng_ip_ult_china_range.csv");
    let (checked, mism) = run_one(&db, &csv, 0x123456789ABCDEFF);
    assert_eq!(mism, 0, "ult_china: {checked} checked, {mism} mismatched");
    println!("csv_oracle ult_china: checked={checked} mism={mism}");
}

fn run_one(db_path: &std::path::Path, csv_path: &std::path::Path, seed: u64) -> (u64, u64) {
    let ranges = load_ranges(csv_path)
        .unwrap_or_else(|| panic!("cannot load CSV oracle at {:?}", csv_path));
    let reader = QzdbReader::from_file(db_path.to_str().unwrap())
        .unwrap_or_else(|e| panic!("cannot load {:?}: {:?}", db_path, e));
    let nr = ranges.len() as u64;
    let mut rng = Lcg::new(seed);
    let mut mism = 0u64;
    let mut checked = 0u64;
    const IN_RANGE_N: u64 = 6000;
    const GLOBAL_N: u64 = 5000;

    let compare = |r: &QzdbReader, ip: u32, expect: &Range, where_: &str, mism: &mut u64, checked: &mut u64| {
        let ipstr = format!(
            "{}.{}.{}.{}",
            (ip >> 24) & 255,
            (ip >> 16) & 255,
            (ip >> 8) & 255,
            ip & 255
        );
        match r.find(&ipstr) {
            Some(g) => {
                *checked += 1;
                if !eq(g.country(), &expect.country)
                    || !eq(g.province(), &expect.province)
                    || !eq(g.city(), &expect.city)
                    || !eq(g.isp(), &expect.isp)
                {
                    *mism += 1;
                    if *mism <= 10 {
                        eprintln!(
                            "  MISMATCH [{where_}] ip={ipstr} expect(country={},prov={},city={},isp={}) got(country={},prov={},city={},isp={})",
                            expect.country, expect.province, expect.city, expect.isp,
                            g.country(), g.province(), g.city(), g.isp()
                        );
                    }
                }
            }
            None => {
                *mism += 1;
                if *mism <= 10 {
                    eprintln!("  MISMATCH [{where_}] ip={ipstr} expected geo but SDK returned None");
                }
            }
        }
    };

    // 区间内随机抽样（严格）
    for _ in 0..IN_RANGE_N {
        let ri = rng.below(nr) as usize;
        let rg = &ranges[ri];
        let len = rg.end as u64 - rg.start as u64 + 1;
        let off = if len > (1u64 << 20) {
            match rng.below(3) {
                0 => 0u64,
                1 => len / 2,
                _ => len - 1,
            }
        } else {
            rng.below(len)
        };
        let ip = rg.start as u64 + off;
        compare(&reader, ip as u32, rg, "in-range", &mut mism, &mut checked);
    }

    // 全局随机：命中区间必须匹配；未命中区间 SDK 必须返回 None（捕获假阳性）
    for _ in 0..GLOBAL_N {
        let ip = rng.below(1u64 << 32) as u32;
        let idx = find_range(&ranges, ip);
        if idx >= 0 {
            let rg = &ranges[idx as usize];
            compare(&reader, ip, rg, "global-hit", &mut mism, &mut checked);
        } else {
            let ipstr = format!(
                "{}.{}.{}.{}",
                (ip >> 24) & 255,
                (ip >> 16) & 255,
                (ip >> 8) & 255,
                ip & 255
            );
            if reader.find(&ipstr).is_some() {
                mism += 1;
                if mism <= 10 {
                    eprintln!("  MISMATCH [global-miss] ip={ipstr} SDK returned geo but CSV has none");
                }
            }
        }
    }

    (checked, mism)
}
