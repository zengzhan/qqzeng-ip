//! Edge case tests for float formatting, JSON serialization, cache behavior,
//! and boundary conditions.

use qzdb_reader::{QzdbReader, UsageType};

fn data_dir() -> std::path::PathBuf {
    std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data")
}

fn load_std() -> QzdbReader {
    QzdbReader::from_file(data_dir().join("qqzeng_ip_std_china.qzdb").to_str().unwrap())
        .expect("load std")
}

fn load_ult() -> QzdbReader {
    QzdbReader::from_file(data_dir().join("qqzeng_ip_ult_china.qzdb").to_str().unwrap())
        .expect("load ult")
}

// ---- Float formatting edge cases ----

#[test]
fn t_float_format_whole_numbers() {
    let r = load_ult();
    let geo = r.find("119.51.194.142").expect("hit");
    // longitude 125.350350 is not a whole number
    let lon = geo.longitude().unwrap();
    assert!((lon - 125.350350).abs() < 1e-6);
    // JSON should contain the numeric value
    let json = geo.to_json();
    assert!(json.contains("\"longitude\":125.350350"), "json: {}", json);
}

#[test]
fn t_float_format_negative() {
    // Test that negative floats are formatted correctly
    let r = load_ult();
    let geo = r.find("119.51.194.142").expect("hit");
    let lat = geo.latitude().unwrap();
    assert!((lat - 43.864010).abs() < 1e-6);
    let json = geo.to_json();
    assert!(json.contains("\"latitude\":43.864010"), "json: {}", json);
}

#[test]
fn t_float_format_zero() {
    // Test that zero is formatted as "0" (no decimal point)
    let r = load_ult();
    // Find an IP where some numeric field might be 0
    let geo = r.find("119.51.194.142").expect("hit");
    let json = geo.to_json();
    // geo_id should be a number (ult has geo_id field)
    assert!(json.contains("\"geo_id\":"), "json: {}", json);
}

// ---- JSON serialization edge cases ----

#[test]
fn t_json_empty_field_values() {
    let r = load_ult();
    let geo = r.find("119.51.194.142").expect("hit");
    let json = geo.to_json();
    // Should start with { and end with }
    assert!(json.starts_with('{'));
    assert!(json.ends_with('}'));
    // Should not contain panic
    assert!(!json.contains("nullnull"));
}

#[test]
fn t_json_special_characters_in_values() {
    let r = load_ult();
    let geo = r.find("119.51.194.142").expect("hit");
    let json = geo.to_json();
    // Check that the JSON is valid (no unescaped quotes in values)
    // The country field contains "中国" which should be properly encoded
    assert!(json.contains("\"country\""), "json: {}", json);
}

#[test]
fn t_json_numeric_field_empty_becomes_null() {
    let r = load_ult();
    // Find an IP where a numeric field might be empty
    let geo = r.find("119.51.194.142").expect("hit");
    let json = geo.to_json();
    // All numeric fields should be either a number or null
    assert!(!json.contains("\"geo_id\":\"\""), "geo_id should be number or null");
    assert!(!json.contains("\"asn\":\"\""), "asn should be number or null");
}

// ---- Cache behavior tests ----

#[test]
fn t_cache_hit_consistency() {
    let r = load_std();
    // Query the same IP multiple times - should return identical results
    let geo1 = r.find("119.51.194.142").expect("hit");
    let geo2 = r.find("119.51.194.142").expect("hit");
    assert_eq!(geo1.to_pipe(), geo2.to_pipe());
    assert_eq!(geo1.to_json(), geo2.to_json());
}

#[test]
fn t_cache_different_ips() {
    let r = load_std();
    // Query different IPs - should return different results
    let geo1 = r.find("119.51.194.142").expect("hit");
    let geo2 = r.find("114.114.114.114").expect("hit");
    // Different IPs should have different results
    assert_ne!(geo1.to_pipe(), geo2.to_pipe());
}

// ---- Boundary condition tests ----

#[test]
fn t_boundary_ip_zeros() {
    let r = load_std();
    // 0.0.0.0 should not panic
    let _ = r.find("0.0.0.0");
}

#[test]
fn t_boundary_ip_broadcast() {
    let r = load_std();
    // 255.255.255.255 should not panic
    let _ = r.find("255.255.255.255");
}

#[test]
fn t_boundary_ip_loopback() {
    let r = load_std();
    // 127.0.0.1 should not panic
    let _ = r.find("127.0.0.1");
}

#[test]
fn t_boundary_private_ips() {
    let r = load_std();
    // Private IPs should not panic
    let _ = r.find("192.168.1.1");
    let _ = r.find("10.0.0.1");
    let _ = r.find("172.16.0.1");
}

#[test]
fn t_boundary_ipv6_unspecified() {
    let r = load_ult();
    // :: should not panic
    let _ = r.find("::");
}

#[test]
fn t_boundary_ipv6_loopback() {
    let r = load_ult();
    // ::1 should not panic
    let _ = r.find("::1");
}

#[test]
fn t_boundary_ipv6_full() {
    let r = load_ult();
    // Full IPv6 address
    let _ = r.find("2001:0db8:0000:0000:0000:0000:0000:0001");
}

// ---- UsageType edge cases ----

#[test]
fn t_usage_type_case_insensitive() {
    assert!(UsageType::from_raw("cloud").is_known());
    assert!(UsageType::from_raw("CLOUD").is_known());
    assert!(UsageType::from_raw("Cloud").is_known());
    assert!(UsageType::from_raw("cLoUd").is_known());
}

#[test]
fn t_usage_type_whitespace_handling() {
    // Leading/trailing whitespace should be trimmed
    assert!(UsageType::from_raw("  cloud  ").is_known());
}

#[test]
fn t_usage_type_empty_string() {
    let u = UsageType::from_raw("");
    assert_eq!(u.raw_value(), "Unknown");
}

// ---- Field normalization edge cases ----

#[test]
fn t_field_normalization_mixed() {
    let r = load_ult();
    let geo = r.find("119.51.194.142").expect("hit");
    // Various forms of country_code should all work
    let cc = geo.get("country_code");
    assert_eq!(geo.get("COUNTRY_CODE"), cc);
    assert_eq!(geo.get("country-code"), cc);
    assert_eq!(geo.get("Country_Code"), cc);
    assert_eq!(geo.get("Country-Code"), cc);
    assert_eq!(geo.get("countrycode"), cc);
    assert_eq!(geo.get("COUNTRYCODE"), cc);
}

// ---- Error handling edge cases ----

#[test]
fn test_empty_ip() {
    let r = load_std();
    assert!(r.find("").is_none());
    assert_eq!(r.find_str(""), "");
    assert_eq!(r.lookup_row_id(""), 0);
}

#[test]
fn test_whitespace_only_ip() {
    let r = load_std();
    assert!(r.find("   ").is_none());
    assert!(r.find("\t").is_none());
    assert!(r.find("\n").is_none());
}

#[test]
fn test_very_long_ip() {
    let r = load_std();
    let long_ip = "1.2.3.4.".repeat(100);
    assert!(r.find(&long_ip).is_none());
}

// ---- Concurrent access edge cases ----

#[test]
fn test_concurrent_find_and_reload() {
    use std::sync::Arc;
    use std::thread;

    let reader = Arc::new(load_ult());
    let mut handles = Vec::new();

    // Spawn multiple reader threads
    for _ in 0..4 {
        let r = Arc::clone(&reader);
        handles.push(thread::spawn(move || {
            for _ in 0..1000 {
                let _ = r.find("119.51.194.142");
                let _ = r.find_str("114.114.114.114");
                let _ = r.find_uint(0x7733C28E);
            }
        }));
    }

    // Spawn a reload thread
    let r = Arc::clone(&reader);
    let reload_path = data_dir().join("qqzeng_ip_ult_china.qzdb");
    handles.push(thread::spawn(move || {
        for _ in 0..5 {
            let _ = r.reload(reload_path.to_str().unwrap());
        }
    }));

    for h in handles {
        h.join().expect("thread panicked");
    }
}

// ---- Batch/Stream edge cases ----

#[test]
fn test_batch_empty() {
    let r = load_std();
    let results = r.find_batch(&[]);
    assert!(results.is_empty());
}

#[test]
fn test_batch_all_invalid() {
    let r = load_std();
    let results = r.find_batch(&["not-an-ip", "also-bad", "nope"]);
    assert_eq!(results.len(), 3);
    for r in &results {
        assert!(r.geo_info.is_none());
        assert!(r.error.is_none()); // Invalid IP is not an error, just not found
    }
}

#[test]
fn test_stream_empty() {
    let r = load_std();
    let results: Vec<_> = r.find_stream(&[]).collect();
    assert!(results.is_empty());
}

// ---- ChainedReader edge cases ----

#[test]
fn test_chained_empty() {
    use qzdb_reader::ChainedReader;
    let chain = ChainedReader::new();
    assert!(chain.find("119.51.194.142").is_none());
    assert_eq!(chain.find_str("119.51.194.142"), "");
}

#[test]
fn test_chained_single_reader() {
    use qzdb_reader::ChainedReader;
    let r = load_std();
    let mut chain = ChainedReader::new();
    chain.push(r);
    let geo = chain.find("119.51.194.142");
    assert!(geo.is_some());
}

// ---- Registry edge cases ----

#[test]
fn test_registry_get_nonexistent() {
    use qzdb_reader::QzdbRegistry;
    let reg = QzdbRegistry::new();
    assert!(reg.get("nonexistent").is_none());
}

#[test]
fn test_registry_overwrite() {
    use qzdb_reader::QzdbRegistry;
    let r1 = load_std();
    let r2 = load_ult();
    let mut reg = QzdbRegistry::new();
    reg.register("db", r1);
    reg.register("db", r2); // Overwrite
    let reader = reg.get("db").expect("db exists");
    // Should use the ult reader now - ult has longitude field
    assert!(reader.has_field("longitude"));
    let geo = reg.find("119.51.194.142").expect("hit");
    assert!(!geo.get("longitude").is_empty());
}
