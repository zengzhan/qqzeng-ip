use qzdb_reader::QzdbReader;
use std::env;
use std::fs::File;
use std::io::{BufRead, BufReader};

// Real-data regression for the QZDB ASN fix (mirrors Go/Java/PHP drivers).
// Usage: regress_rust <db.qzdb> <truth.tsv>
fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 3 {
        eprintln!("usage: regress_rust <db.qzdb> <truth.tsv>");
        std::process::exit(2);
    }
    let db_path = &args[1];
    let truth_path = &args[2];

    // QzdbReader::from_file already verifies CRC (default-on).
    let searcher = match QzdbReader::from_file(db_path) {
        Ok(s) => s,
        Err(e) => {
            eprintln!("load error: {:?}", e);
            std::process::exit(2);
        }
    };

    let f = match File::open(truth_path) {
        Ok(f) => f,
        Err(e) => {
            eprintln!("open truth error: {}", e);
            std::process::exit(2);
        }
    };
    let reader = BufReader::new(f);

    let mut checked: u64 = 0;
    let mut exact: u64 = 0;
    let mut notfound: u64 = 0;
    let mut mismatch: u64 = 0;
    let mut sample: u64 = 0;

    for line in reader.lines() {
        let line = match line {
            Ok(l) => l,
            Err(_) => continue,
        };
        if line.is_empty() {
            continue;
        }
        let parts: Vec<&str> = line.split('\t').collect();
        if parts.len() < 2 {
            continue;
        }
        let ip = parts[0];
        let truth_asn = parts[1].trim();
        checked += 1;
        match searcher.find(ip) {
            None => {
                notfound += 1;
                if sample < 10 {
                    eprintln!("NOTFOUND ip={} truth={}", ip, truth_asn);
                    sample += 1;
                }
            }
            Some(geo) => {
                let got = geo.get("asn").trim();
                if got == truth_asn {
                    exact += 1;
                } else {
                    mismatch += 1;
                    if sample < 10 {
                        eprintln!("MISMATCH ip={} truth={} got={}", ip, truth_asn, got);
                        sample += 1;
                    }
                }
            }
        }
    }

    println!("RUST REAL-DATA REGRESSION");
    println!(
        "checked={} EXACT={} NOTFOUND={} MISMATCH={}",
        checked, exact, notfound, mismatch
    );
    let pass = exact == checked && notfound == 0 && mismatch == 0;
    println!("{}", if pass { ">>> PASS 100%" } else { ">>> FAIL" });
    std::process::exit(if pass { 0 } else { 1 });
}
