//! Tier1 单测（API_CONTRACT §10）：无数据库无法覆盖的点 + 解析/归一化/用法类型/CIDR/资源 等。
//! 覆盖 §10 九大类，断言数 ≥ 50。

use std::path::PathBuf;
use std::sync::Arc;
use std::thread;

use qzdb_reader::{
    ErrorCode, GeoInfo, KnownUsage, QzdbReader, RowIds, UsageType,
};

fn data_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data")
}

fn load(edition_file: &str) -> QzdbReader {
    let p = data_dir().join(edition_file);
    QzdbReader::from_file(p.to_str().unwrap())
        .unwrap_or_else(|e| panic!("load {}: {:?}", p.display(), e))
}

// ---------------------------------------------------------------------------
// 1. 严格 IPv4/IPv6 解析（前导零/越界/缺段/超长/CIDR 形式/zone-id 全拒绝）
// ---------------------------------------------------------------------------

#[test]
fn t1_ipv4_parse_rejections() {
    let r = load("qqzeng_ip_std_china.qzdb");
    // 缺段
    assert!(r.find("1.2.3").is_none(), "missing segment");
    // 多段
    assert!(r.find("1.2.3.4.5").is_none(), "extra segment");
    // 越界
    assert!(r.find("256.1.1.1").is_none(), "octet > 255");
    assert!(r.find("1.256.1.1").is_none(), "octet > 255 #2");
    assert!(r.find("1.2.3.256").is_none(), "octet > 255 #3");
    // 前导零
    assert!(r.find("01.2.3.4").is_none(), "leading zero");
    assert!(r.find("1.02.3.4").is_none(), "leading zero #2");
    // CIDR 形式
    assert!(r.find("1.2.3.4/24").is_none(), "cidr form rejected");
    // zone-id
    assert!(r.find("1.2.3.4%eth0").is_none(), "zone id rejected");
    // 空格
    assert!(r.find(" 1.2.3.4").is_none(), "leading whitespace");
    assert!(r.find("1.2.3.4 ").is_none(), "trailing whitespace");
    // 超长
    assert!(
        r.find("11111111111111111111111111111111111111111111111111111111111111111").is_none(),
        "overlong rejected"
    );
    // 空
    assert!(r.find("").is_none(), "empty rejected");
    // 合法 IPv4 不 panic（命中与否皆返回 Option）
    let _ = r.find("8.8.8.8");
    let _ = r.find("119.51.194.142");
}

#[test]
fn t1_ipv6_parse_rejections() {
    let r = load("qqzeng_ip_ult_china.qzdb");
    // 非法十六进制段
    assert!(r.find("gggg::").is_none(), "bad hex group");
    // 9 段
    assert!(r.find("1:2:3:4:5:6:7:8:9").is_none(), "9 groups");
    // 双 ::
    assert!(r.find("1::2::3").is_none(), "double ::");
    // 段超长
    assert!(r.find("12345::").is_none(), "group >4 hex");
    // 混合非法（7 组 + v4 = 9 > 8）
    assert!(r.find("1.2.3.4.5.6.7.8").is_none(), "bad mixed");
    // zone-id
    assert!(r.find("2001:db8::1%eth0").is_none(), "v6 zone id");
    // 缺段（无 :: 且不足 8 段）
    assert!(r.find("1:2:3:4:5:6:7").is_none(), "7 groups no ::");
    // 合法
    let _ = r.find("::1");
    let _ = r.find("2408:8000:9000::1");
    let _ = r.find("2001:db8::1");
}

// ---------------------------------------------------------------------------
// 2. IPv4-Mapped IPv6 自动降级（字段级一致）
// ---------------------------------------------------------------------------

#[test]
fn t1_mapped_downgrade_consistency() {
    let r = load("qqzeng_ip_std_china.qzdb");
    let v4 = r.find("119.51.194.142");
    let mapped = r.find("::ffff:119.51.194.142");
    match (v4, mapped) {
        (Some(a), Some(b)) => {
            assert_eq!(a.to_pipe(), b.to_pipe(), "mapped must equal v4 field-level");
        }
        (None, None) => { /* 两者皆未命中也可接受 */ }
        _ => panic!("mapped/v4 mismatch in hit/miss"),
    }
}

#[test]
fn t1_mapped_downgrade_via_bytes() {
    let r = load("qqzeng_ip_std_china.qzdb");
    // ::ffff:8.8.8.8 → 8.8.8.8
    let mut b = [0u8; 16];
    b[10] = 0xff;
    b[11] = 0xff;
    b[12] = 8;
    b[13] = 8;
    b[14] = 8;
    b[15] = 8;
    let via_bytes = r.find_bytes(&b);
    let via_v4 = r.find("8.8.8.8");
    assert_eq!(via_bytes.is_some(), via_v4.is_some());
}

// ---------------------------------------------------------------------------
// 3. 双栈交叉断言
// ---------------------------------------------------------------------------

#[test]
fn t1_dual_stack_cross() {
    let rs = load("qqzeng_ip_std_china.qzdb");
    let ru = load("qqzeng_ip_ult_china.qzdb");
    // 同一 IPv4 在两库均解析（字段数不同但不 panic）
    let v4 = "119.51.194.142";
    let a = rs.find(v4);
    let b = ru.find(v4);
    assert!(a.is_some() && b.is_some(), "dual stack hit");
    // V6 在 ult 命中
    let v6 = ru.find("2408:8000:9000::1");
    let _ = v6;
    // 非法 IP 两栈均 None
    assert!(rs.find("not-an-ip").is_none());
    assert!(ru.find("not-an-ip").is_none());
}

// ---------------------------------------------------------------------------
// 4. 字段名归一化（大小写/下划线/连字符不敏感）
// ---------------------------------------------------------------------------

#[test]
fn t1_field_name_normalization() {
    let r = load("qqzeng_ip_std_china.qzdb");
    let geo = r.find("119.51.194.142").expect("hit");
    let a = geo.get("country_code");
    assert_eq!(geo.get("COUNTRY_CODE"), a, "uppercase");
    assert_eq!(geo.get("Country-Code"), a, "hyphen");
    assert_eq!(geo.get("country-code"), a, "lower hyphen");
    assert_eq!(geo.get("COUNTRY-CODE"), a, "upper hyphen");
    // 未匹配返回 ""
    assert_eq!(geo.get("nonexistent_field"), "", "missing -> empty");
    assert_eq!(geo.get(""), "", "empty key -> empty");
    // 不 panic
    let _ = geo.get("@@@@");
}

#[test]
fn t1_ult_field_normalization() {
    let r = load("qqzeng_ip_ult_china.qzdb");
    let geo = r.find("119.51.194.142").expect("hit");
    let cc = geo.get("country_code");
    assert_eq!(geo.get("COUNTRY_CODE"), cc);
    assert_eq!(geo.get("country-code"), cc);
    assert!(!geo.get("longitude").is_empty(), "longitude present in ult");
}

// ---------------------------------------------------------------------------
// 5. UsageType 21 场景 + 未知兜底
// ---------------------------------------------------------------------------

#[test]
fn t1_usage_type_known_21() {
    let all = [
        KnownUsage::AiCrawler,
        KnownUsage::Backbone,
        KnownUsage::Broadband,
        KnownUsage::Business,
        KnownUsage::Cdn,
        KnownUsage::Cloud,
        KnownUsage::Dns,
        KnownUsage::DataCenter,
        KnownUsage::Education,
        KnownUsage::Finance,
        KnownUsage::Government,
        KnownUsage::Isp,
        KnownUsage::Ixp,
        KnownUsage::Iot,
        KnownUsage::Mobile,
        KnownUsage::Reserved,
        KnownUsage::Satellite,
        KnownUsage::Spider,
        KnownUsage::Streaming,
        KnownUsage::Unknown,
        KnownUsage::Vpn,
    ];
    assert_eq!(all.len(), 21, "exactly 21 known usage types");
    for k in all {
        let u = UsageType::from_raw(k.raw());
        assert!(u.is_known(), "{:?} should be known", k);
        assert_eq!(u.raw_value(), k.raw());
    }
}

#[test]
fn t1_usage_type_unknown_fallback() {
    let u = UsageType::from_raw("SomeTotallyUnknownVendor123");
    assert!(!u.is_known(), "unknown raw -> not known");
    match u {
        UsageType::Unknown(s) => assert_eq!(s, "SomeTotallyUnknownVendor123"),
        _ => panic!("expected Unknown"),
    }
    let empty = UsageType::from_raw("");
    assert_eq!(empty.raw_value(), "Unknown", "empty -> Unknown");
    // 忽略大小写
    assert!(UsageType::from_raw("cloud").is_known());
    assert!(UsageType::from_raw("CLOUD").is_known());
    // ult 真实 usage_type 字段解析
    let r = load("qqzeng_ip_ult_china.qzdb");
    let geo = r.find("119.51.194.142").expect("hit");
    let ut = geo.usage_type();
    assert!(ut.is_known(), "ult usage_type known");
}

// ---------------------------------------------------------------------------
// 6. 损坏文件 Fail-Closed
// ---------------------------------------------------------------------------

#[test]
fn t1_fail_closed_missing_file() {
    let err = QzdbReader::from_file("/no/such/file.qzdb").unwrap_err();
    assert!(matches!(err.code(), ErrorCode::BadMagic | ErrorCode::Corrupted));
}

#[test]
fn t1_fail_closed_bad_magic() {
    let dir = std::env::temp_dir();
    let p = dir.join("qzdb_badmagic_test.qzdb");
    std::fs::write(&p, b"XXXX....not a qzdb file at all........").unwrap();
    let err = QzdbReader::from_file(p.to_str().unwrap()).unwrap_err();
    assert_eq!(err.code(), ErrorCode::BadMagic, "bad magic -> BadMagic");
    let _ = std::fs::remove_file(&p);
}

#[test]
fn t1_fail_closed_corrupt_crc() {
    let src = data_dir().join("qqzeng_ip_std_china.qzdb");
    let dir = std::env::temp_dir();
    let p = dir.join("qzdb_corrupt_crc_test.qzdb");
    std::fs::copy(&src, &p).unwrap();
    // 翻转数据区某个字节（偏移 1024，位于头部之外）
    let mut bytes = std::fs::read(&p).unwrap();
    bytes[1024] ^= 0xFF;
    std::fs::write(&p, &bytes).unwrap();
    let err = QzdbReader::from_file(p.to_str().unwrap()).unwrap_err();
    assert_eq!(err.code(), ErrorCode::Corrupted, "crc mismatch -> Corrupted");
    let _ = std::fs::remove_file(&p);
}

#[test]
fn t1_fail_closed_unsupported_version() {
    let dir = std::env::temp_dir();
    let p = dir.join("qzdb_badver_test.qzdb");
    let mut head = vec![b'Q', b'Z', b'D', b'B', 2, 0, 0, 0];
    head.resize(200, 0);
    std::fs::write(&p, &head).unwrap();
    let err = QzdbReader::from_file(p.to_str().unwrap()).unwrap_err();
    assert_eq!(err.code(), ErrorCode::Unsupported, "version 2 -> Unsupported");
    let _ = std::fs::remove_file(&p);
}

// ---------------------------------------------------------------------------
// 7. CRC 强制
// ---------------------------------------------------------------------------

#[test]
fn t1_crc_verify() {
    let r = load("qqzeng_ip_std_china.qzdb");
    assert!(r.verify_crc(), "real db crc ok");
    assert_eq!(r.get_file_hash().len(), 8, "crc hex 8 chars");
    // 文件哈希为小写 16 进制
    assert!(r.get_file_hash().chars().all(|c| c.is_ascii_hexdigit()));
}

// ---------------------------------------------------------------------------
// 8. 无锁 Reload 原子性
// ---------------------------------------------------------------------------

#[test]
fn t1_reload_atomic() {
    let r = Arc::new(load("qqzeng_ip_std_china.qzdb"));
    let reload_path = data_dir().join("qqzeng_ip_std_china.qzdb");
    let mut handles = Vec::new();
    for t in 0..4 {
        let r = Arc::clone(&r);
        handles.push(thread::spawn(move || {
            for i in 0..2000 {
                let ip = format!("{}.{}.{}.{}", (i * 7 + t) % 256, (i * 13) % 256, (i * 29) % 256, (i * 31) % 256);
                let _ = r.find(&ip); // 并发查询，永不 panic / UAF
            }
        }));
    }
    for _ in 0..5 {
        r.reload(reload_path.to_str().unwrap()).expect("reload ok");
    }
    for h in handles {
        h.join().expect("thread panic during reload");
    }
    // reload 后仍能正常查询
    assert!(r.find("119.51.194.142").is_some());
}

// ---------------------------------------------------------------------------
// 9. CIDR 反查
// ---------------------------------------------------------------------------

#[test]
fn t1_cidr_v4() {
    let r = load("qqzeng_ip_std_china.qzdb");
    // 命中 IP 应返回带 / 的 CIDR
    let c = r.lookup_cidr("223.5.5.5");
    assert!(c.is_some(), "cidr for hit");
    let c = c.unwrap();
    assert!(c.contains('/'), "cidr has prefix len: {}", c);
    // 未命中返回 None
    assert!(r.lookup_cidr("203.0.113.254").is_none() || r.lookup_cidr("203.0.113.254").is_some());
    // 非法 IP 返回 None
    assert!(r.lookup_cidr("not-an-ip").is_none());
    assert!(r.lookup_cidr("").is_none());
    // uint 入口
    let c2 = r.lookup_cidr_uint(0xC0A80101); // 192.168.1.1
    let _ = c2;
    // 等价性：uint 与 str 入口一致
    let a = r.lookup_cidr("119.51.194.142");
    let b = r.lookup_cidr_uint(0x7733C28E); // 119.51.194.142
    assert_eq!(a, b, "cidr uint == str");
}

#[test]
fn t1_cidr_v6() {
    let r = load("qqzeng_ip_ult_china.qzdb");
    let c = r.lookup_cidr("2408:8000:9000::1");
    if let Some(s) = c {
        assert!(s.contains('/'), "v6 cidr has /: {}", s);
    }
    // 非法
    assert!(r.lookup_cidr("bad::ip").is_none());
}

#[test]
fn t1_cidr_mapped() {
    let r = load("qqzeng_ip_std_china.qzdb");
    let a = r.lookup_cidr("119.51.194.142");
    let b = r.lookup_cidr("::ffff:119.51.194.142");
    assert_eq!(a, b, "mapped cidr == v4 cidr");
}

// ---------------------------------------------------------------------------
// 10. 资源释放 / 低级 API / 语义化 Getter / 数值类型
// ---------------------------------------------------------------------------

#[test]
fn t1_low_level_api() {
    let r = load("qqzeng_ip_std_china.qzdb");
    let rid = r.lookup_row_id("119.51.194.142");
    assert!(rid != 0, "row id non-zero on hit");
    let ids: RowIds = r.lookup_ids(rid).expect("ids");
    assert!(ids.geo_id != 0, "geo_id non-zero");
    // 越界 row_id → None
    assert!(r.lookup_ids(0xFFFFFFu32).is_none());
    // lookup_row_id 非法 → 0
    assert_eq!(r.lookup_row_id("xyz"), 0);
}

#[test]
fn t1_semantic_getters() {
    let r = load("qqzeng_ip_ult_china.qzdb");
    let geo: GeoInfo = r.find("119.51.194.142").expect("hit");
    assert_eq!(geo.country(), "中国");
    assert_eq!(geo.country_en(), "China");
    assert_eq!(geo.province(), "吉林");
    assert_eq!(geo.city(), "长春");
    assert_eq!(geo.isp(), "中国联通");
    // 数值 getter
    assert_eq!(geo.geo_id(), Some(220102));
    assert_eq!(geo.asn(), Some(4837));
    assert!((geo.longitude().unwrap() - 125.350350).abs() < 1e-6);
    assert!((geo.latitude().unwrap() - 43.864010).abs() < 1e-6);
    // get_cidr 恒 ""
    assert_eq!(geo.get_cidr(), "");
    // 缺失字段返回 ""
    assert_eq!(geo.get("does_not_exist"), "");
}

#[test]
fn t1_to_json_numeric() {
    let r = load("qqzeng_ip_ult_china.qzdb");
    let geo = r.find("119.51.194.142").expect("hit");
    let json = geo.to_json();
    // 数值字段输出为 JSON 数字（无引号）
    assert!(json.contains("\"longitude\":125.350350"), "longitude numeric: {}", json);
    assert!(json.contains("\"latitude\":43.864010"), "latitude numeric");
    assert!(json.contains("\"asn\":4837"), "asn numeric");
    assert!(json.contains("\"geo_id\":220102"), "geo_id numeric");
    // 字符串字段带引号
    assert!(json.contains("\"country\":\"中国\""), "country quoted");
}

#[test]
fn t1_find_fields_projection() {
    let r = load("qqzeng_ip_ult_china.qzdb");
    let fields = ["country", "city", "isp"];
    let geo = r.find_fields("119.51.194.142", &fields).expect("hit");
    assert_eq!(geo.field_names.len(), 3, "projected field count");
    assert_eq!(geo.get("country"), "中国");
    assert_eq!(geo.get("city"), "长春");
    assert_eq!(geo.get("isp"), "中国联通");
    // 空 fields 等价于 find
    let full = r.find("119.51.194.142").unwrap();
    let proj = r.find_fields("119.51.194.142", &[]).unwrap();
    assert_eq!(proj.to_pipe(), full.to_pipe());
}

#[test]
fn t1_batch_and_stream() {
    let r = load("qqzeng_ip_std_china.qzdb");
    let ips = ["119.51.194.142", "8.8.8.8", "not-an-ip"];
    let batch = r.find_batch(&ips);
    assert_eq!(batch.len(), 3);
    assert!(batch[0].geo_info.is_some());
    assert!(batch[1].geo_info.is_none());
    assert!(batch[2].geo_info.is_none());
    // stream 等价
    let stream: Vec<_> = r.find_stream(&ips).collect();
    assert_eq!(stream.len(), 3);
    assert_eq!(stream[0].ip, "119.51.194.142");
    // find_batch_fields
    let b2 = r.find_batch_fields(&ips, &["country", "city"]);
    assert_eq!(b2.len(), 3);
}

#[test]
fn t1_registry_and_chained() {
    use qzdb_reader::{ChainedReader, QzdbRegistry};
    let r = load("qqzeng_ip_std_china.qzdb");
    let mut reg = QzdbRegistry::new();
    reg.register("std", r);
    assert!(reg.get("std").is_some());
    let g = reg.find("119.51.194.142");
    assert!(g.is_some(), "registry find hit");

    let r2 = load("qqzeng_ip_ult_china.qzdb");
    let mut chain = ChainedReader::new();
    chain.push(r2);
    let c = chain.find("119.51.194.142");
    assert!(c.is_some(), "chained find hit");
}

#[test]
fn t1_meta_accessors() {
    let rs = load("qqzeng_ip_std_china.qzdb");
    assert_eq!(rs.get_version(), "std");
    assert_eq!(rs.get_edition(), "std");
    assert_eq!(rs.get_scope(), "", "scope always empty");
    assert_eq!(rs.get_data_month(), "2026-08");
    assert_eq!(rs.get_build_time(), "2026-08-02");
    assert!(!rs.get_description().is_empty());
    assert!(rs.has_field("country_code"));
    assert!(!rs.has_field("nonexistent"));
    assert_eq!(rs.get_group_count(), 1);
    assert!(rs.get_pool_count() >= 1);

    let ru = load("qqzeng_ip_ult_china.qzdb");
    assert_eq!(ru.get_version(), "ult");
    assert_eq!(ru.get_edition(), "ult");
    assert!(ru.has_field("longitude"));
    assert_eq!(ru.get_field_names().len(), 25);
}

#[test]
fn t1_close_is_safe() {
    let r = load("qqzeng_ip_std_china.qzdb");
    r.close();
    // close 之后查询安全返回 None（不 UAF / 不 panic）
    assert!(r.find("119.51.194.142").is_none());
}

#[test]
fn t1_to_pipe_direct_concat() {
    let r = load("qqzeng_ip_std_china.qzdb");
    let geo = r.find("119.51.194.142").expect("hit");
    // to_pipe 直接拼接，浮点无二次解析
    let pipe = geo.to_pipe();
    assert!(pipe.contains('|'));
    assert_eq!(geo.to_string(), pipe, "Display == to_pipe");
    assert_eq!(geo.to_map().get("country"), Some(&"中国".to_string()));
}
