//! Tier2 黄金校验（API_CONTRACT §10）：对 golden_vectors.json 0 偏差。
//! 加载 qqzeng_ip_std_china.qzdb / qqzeng_ip_ult_china.qzdb，
//! 对每个 IP 断言 find(ip).to_pipe() == expected（未命中/非法映射为 ""）。必须 0 失败。

use std::path::PathBuf;

use qzdb::QzdbReader;

fn data_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data")
}

fn golden_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../tools/golden_vectors.json")
}

fn run_golden(edition: &str, file: &str) {
    let db_path = data_dir().join(file);
    let reader = QzdbReader::from_file(db_path.to_str().unwrap())
        .unwrap_or_else(|e| panic!("failed to load {}: {:?}", db_path.display(), e));

    let golden_text = std::fs::read_to_string(golden_path()).expect("read golden");
    let golden: serde_json::Value =
        serde_json::from_str(&golden_text).expect("parse golden json");
    let root = golden
        .get(edition)
        .unwrap_or_else(|| panic!("edition {} not in golden", edition));

    let categories = ["random_v4", "random_v6", "boundary_v4", "boundary_v6", "invalid"];
    let mut total = 0usize;
    let mut failures = Vec::new();

    for cat in categories {
        let arr = match root.get(cat).and_then(|v| v.as_array()) {
            Some(a) => a,
            None => continue,
        };
        for entry in arr {
            let ip = entry.get("ip").and_then(|v| v.as_str()).unwrap_or("");
            let expected = entry.get("expected").and_then(|v| v.as_str()).unwrap_or("");
            let got = reader.find_str(ip);
            total += 1;
            if got != expected && failures.len() < 20 {
                failures.push(format!(
                    "[{}] ip={:?} expected={:?} got={:?}",
                    cat, ip, expected, got
                ));
            }
        }
    }

    if !failures.is_empty() {
        panic!(
            "Tier2 GOLDEN FAIL for {}: {}/{} mismatches.\nFirst failures:\n{}",
            edition,
            failures.len(),
            total,
            failures.join("\n")
        );
    }
    println!("Tier2 GOLDEN PASS for {}: {} vectors, 0 failures", edition, total);
}

#[test]
fn tier2_golden_std_china() {
    run_golden("std_china", "qqzeng_ip_std_china.qzdb");
}

#[test]
fn tier2_golden_ult_china() {
    run_golden("ult_china", "qqzeng_ip_ult_china.qzdb");
}
