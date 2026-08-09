use qzdb_reader::QzdbReader;
use std::io::{BufRead, BufReader};
use std::fs::File;

fn main() {
    let db = std::env::args().nth(1).expect("db arg");
    let ipf = std::env::args().nth(2).expect("ipfile arg");
    let s = QzdbReader::from_file(&db).expect("load failed");
    let fns: Vec<String> = s.get_field_names();
    let f = File::open(&ipf).expect("open ipfile");
    for line in BufReader::new(f).lines() {
        let ip = match line {
            Ok(l) => l.trim().to_string(),
            Err(_) => continue,
        };
        if ip.is_empty() {
            continue;
        }
        match s.find(&ip) {
            Some(info) => {
                let parts: Vec<String> = fns
                    .iter()
                    .map(|n| format!("{}={}", n, info.get(n)))
                    .collect();
                println!("{}\t{}", ip, parts.join("\t"));
            }
            None => println!("{}\t__NOTFOUND__", ip),
        }
    }
}
