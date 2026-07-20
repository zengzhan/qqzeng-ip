/// Batch IP query runner for Rust
/// Build: cargo build --release --bin batch_rust
/// Usage: ./batch_rust <database_path> <v4_test> <v4_output> <v6_test> <v6_output>
use std::env;
use std::fs;
use std::process;

fn geo_to_pipe(searcher: &qzdb_searcher::QzdbSearcher, info: &qzdb_searcher::GeoInfo) -> String {
    let len = searcher.field_names.len().min(info.values.len());
    let mut parts = Vec::with_capacity(len);
    for i in 0..len {
        let val = &info.values[i];
        if searcher.float_indices.contains(&i) && !val.is_empty() {
            if let Ok(v) = val.parse::<f64>() {
                parts.push(format!("{:.6}", v));
            } else {
                parts.push(val.clone());
            }
        } else {
            parts.push(val.clone());
        }
    }
    parts.join("|")
}

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 5 {
        eprintln!("Usage: {} <db_path> <v4_test> <v4_out> <v6_test> <v6_out>", args[0]);
        process::exit(1);
    }

    let db_path = &args[1];
    let v4_test = &args[2];
    let v4_out = &args[3];
    let v6_test = &args[4];
    let v6_out = &args[5];

    let searcher = qzdb_searcher::instance(db_path);

    // V4
    let v4_data = fs::read_to_string(v4_test).unwrap_or_default();
    let v4_lines: Vec<&str> = v4_data.lines().filter(|l| !l.trim().is_empty()).collect();
    let mut v4_results = Vec::with_capacity(v4_lines.len());
    for line in &v4_lines {
        let ip: u32 = line.trim().parse().unwrap_or(0);
        let s = match searcher.find_uint(ip) {
            Some(ref info) => geo_to_pipe(searcher, info),
            None => String::new(),
        };
        v4_results.push(format!("{}|{}", line, s));
    }
    fs::write(v4_out, v4_results.join("\n") + "\n").ok();
    eprintln!("  Rust V4: {} queries", v4_results.len());

    // V6
    let v6_data = fs::read_to_string(v6_test).unwrap_or_default();
    let v6_lines: Vec<&str> = v6_data.lines().filter(|l| !l.trim().is_empty()).collect();
    let mut v6_results = Vec::with_capacity(v6_lines.len());
    for line in &v6_lines {
        let parts: Vec<&str> = line.trim().split(':').collect();
        if parts.len() == 2 {
            let high: u64 = parts[0].parse().unwrap_or(0);
            let low: u64 = parts[1].parse().unwrap_or(0);
            let s = match searcher.find_v6(high, low) {
                Some(ref info) => geo_to_pipe(searcher, info),
                None => String::new(),
            };
            v6_results.push(format!("{}|{}", line, s));
        }
    }
    fs::write(v6_out, v6_results.join("\n") + "\n").ok();
    eprintln!("  Rust V6: {} queries", v6_results.len());

    eprintln!("  Rust DONE");
}
