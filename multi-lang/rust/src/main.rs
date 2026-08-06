use std::path::Path;

const DATA: &str = "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/data";

fn family(ip: &str) -> &'static str {
    if ip.contains(':') { "V6" }
    else if regex_lite::Regex::new(r"^\d+\.\d+\.\d+\.\d+$").map(|r| r.is_match(ip)).unwrap_or(false) { "V4" }
    else { "?" }
}

fn test(path: &str, ip: &str) {
    if !Path::new(path).exists() {
        println!("  ⚠ {} not found", path);
        return;
    }
    let searcher = match qzdb_reader::from_file(path) {
        Ok(s) => s,
        Err(e) => {
            println!("  ⚠ {} load error: {:?}", path, e);
            return;
        }
    };
    let s = searcher.find_str(ip);
    println!("Rust Output: {}", s);
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() > 2 {
        test(&args[1], &args[2]);
    } else {
        println!("Usage: cargo run -- <db_path> <ip_str>");
    }
}
