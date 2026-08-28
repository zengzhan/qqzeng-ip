use std::path::Path;

fn find_db() -> String {
    for c in [
        "qqzeng_ip_std_china.qzdb",
        "../data/qqzeng_ip_std_china.qzdb",
        "data/qqzeng_ip_std_china.qzdb",
    ] {
        if Path::new(c).exists() {
            return c.to_string();
        }
    }
    String::new()
}

fn main() {
    let db = find_db();
    if db.is_empty() {
        println!("Database file not found");
        std::process::exit(1);
    }
    let searcher = match qzdb::QzdbReader::from_file(&db) {
        Ok(s) => s,
        Err(e) => {
            println!("Failed to load database: {:?}", e);
            std::process::exit(1);
        }
    };
    println!("Fields ({}): {:?}", searcher.get_field_names().len(), searcher.get_field_names());
    for ip in ["114.114.114.114", "223.5.5.5", "8.8.8.8"] {
        println!("find(\"{:<16}\") => {}", ip, searcher.find_str(ip));
    }
    println!("find(\"2408:8000:9000::1\") => {}", searcher.find_str("2408:8000:9000::1"));
    println!("TEST_PASS");
}
