use std::path::Path;

const DATA: &str = "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/data";

fn family(ip: &str) -> &'static str {
    if ip.contains(':') { "V6" }
    else if regex_lite::Regex::new(r"^\d+\.\d+\.\d+\.\d+$").map(|r| r.is_match(ip)).unwrap_or(false) { "V4" }
    else { "?" }
}

fn test(db: &str, label: &str, ip: &str) {
    let path = format!("{}/{}", DATA, db);
    if !Path::new(&path).exists() {
        println!("  ⚠ {} not found", db);
        return;
    }
    let searcher = qzdb_searcher::from_file(&path);
    let s = searcher.find_str(ip);
    if !s.is_empty() {
        println!("  ✅ {} {:<42} → {}", family(ip), label, s);
    } else {
        println!("  ⬜ {} {:<42} → (None)", family(ip), label);
    }
}

fn main() {
    println!("{}", "=".repeat(90));
    println!("【Rust】");
    println!("{}", "=".repeat(90));
    test("qqzeng_ip_std_china.qzdb", "114.114.114.114 (安徽)", "114.114.114.114");
    test("qqzeng_ip_std_china.qzdb", "223.5.5.5 (阿里DNS)", "223.5.5.5");
    test("qqzeng_ip_std_china.qzdb", "8.8.8.8 (Google 国外)", "8.8.8.8");
    test("qqzeng_ip_std_china.qzdb", "2408:8000:9000::1 (联通V6)", "2408:8000:9000::1");
    test("qqzeng_ip_std_china.qzdb", "2001:4860:4860::8888 (GoogleV6)", "2001:4860:4860::8888");
    test("qqzeng_ip_std_china.qzdb", "127.0.0.1 (回环)", "127.0.0.1");
    test("qqzeng_ip_std_china.qzdb", "192.168.1.1 (私网)", "192.168.1.1");
    test("qqzeng_ip_std_china.qzdb", "not-an-ip (非法)", "not-an-ip");
    test("qqzeng_ip_max_china.qzdb", "114.114.114.114", "114.114.114.114");
    test("qqzeng_ip_max_china.qzdb", "8.8.8.8 (国外)", "8.8.8.8");
    test("qqzeng_ip_max_global.qzdb", "8.8.8.8 (Google)", "8.8.8.8");
    test("qqzeng_ip_max_global.qzdb", "1.1.1.1 (Cloudflare)", "1.1.1.1");
    test("qqzeng_ip_max_global.qzdb", "114.114.114.114 (中国)", "114.114.114.114");
    test("qqzeng_ip_max_global.qzdb", "2001:4860:4860::8888 (GoogleV6)", "2001:4860:4860::8888");
    test("qqzeng_ip_max_global.qzdb", "not-an-ip", "not-an-ip");
    println!("TEST_PASS");
}
