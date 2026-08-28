use std::path::Path;

fn family(ip: &str) -> &'static str {
    if ip.contains(':') {
        "V6"
    } else if ip.split('.').count() == 4 && ip.split('.').all(|p| p.parse::<u8>().is_ok()) {
        "V4"
    } else {
        "?"
    }
}

fn test(path: &str, ip: &str) {
    if !Path::new(path).exists() {
        println!("  ⚠ {} not found", path);
        return;
    }
    let searcher = match qzdb::QzdbReader::from_file(path) {
        Ok(s) => s,
        Err(e) => {
            println!("  ⚠ {} load error: {:?}", path, e);
            return;
        }
    };
    let s = searcher.find_str(ip);
    println!("Rust Output: {} ({})", s, family(ip));
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() > 2 {
        test(&args[1], &args[2]);
    } else {
        println!("Usage: cargo run -- <db_path> <ip_str>");
    }
}
