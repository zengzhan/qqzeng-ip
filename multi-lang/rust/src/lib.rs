//! QZDB 离线 IP 地理定位数据库 Rust 读取器。
//!
//! 设计要点（对齐 API_CONTRACT.md v2.4 与认证参考实现 Java/C#）：
//! - 不可变快照 + 原子替换（[`arc_swap::ArcSwap`]），查询路径对快照只读、无锁、热更新线程安全。
//! - per-snapshot 有界无锁 GeoInfo 缓存（开放寻址，碰撞只重算、绝不返回错值）。
//! - 浮点字段在解码期格式化为 6 位小数（NaN/Inf → ""）；`to_pipe` 直接拼接已解码字符串。
//! - SENTINEL 哨兵位在 Trie 返回 row_id 时即剥离。

use std::collections::HashMap;
use std::fs;
use std::sync::{Arc, OnceLock};

use arc_swap::ArcSwap;

// ---------------------------------------------------------------------------
// 常量
// ---------------------------------------------------------------------------

const SENTINEL: u32 = 0x80000000;
const SENTINEL_MASK_31: u32 = 0x7FFFFFFF;

/// GeoInfo 缓存槽位数（2 的幂，≈16K 槽 ≈ 196KB/快照）。
const GEO_CACHE_SIZE: usize = 1 << 14;

// ---------------------------------------------------------------------------
// 错误
// ---------------------------------------------------------------------------

/// 错误码（API_CONTRACT §7）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ErrorCode {
    NotFound,
    Corrupted,
    OutOfBounds,
    InvalidParam,
    BadHeader,
    BadMagic,
    Unsupported,
    InvalidIp,
}

/// QZDB 操作错误。
#[derive(Debug, Clone)]
pub struct QzdbError {
    pub code: ErrorCode,
    pub message: String,
}

impl QzdbError {
    pub fn code(&self) -> ErrorCode {
        self.code
    }
}

impl std::fmt::Display for QzdbError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:?}: {}", self.code, self.message)
    }
}

impl std::error::Error for QzdbError {}

impl From<std::io::Error> for QzdbError {
    fn from(e: std::io::Error) -> Self {
        let code = if e.kind() == std::io::ErrorKind::NotFound {
            ErrorCode::BadMagic
        } else {
            ErrorCode::Corrupted
        };
        QzdbError { code, message: e.to_string() }
    }
}

fn err(code: ErrorCode, msg: impl Into<String>) -> QzdbError {
    QzdbError { code, message: msg.into() }
}

/// 解析期边界守卫：把 `Option` 收口为 `QzdbError::OutOfBounds`，使畸形 .qzdb 文件
/// Fail-Closed（返回错误而非 panic 导致宿主进程崩溃）。
macro_rules! ro {
    ($opt:expr, $name:expr) => {
        $opt.ok_or_else(|| err(ErrorCode::OutOfBounds, $name))
    };
}

/// 安全单字节读取（越界返回 None，配合 `ro!` 收口）。
fn rget(d: &[u8], off: usize) -> Option<u8> {
    d.get(off).copied()
}

/// 把 `Vec<Option<T>>` 整体收口成 `Vec<T>`：任一元素缺失即返回 `Corrupted`，
/// 而不是 `unwrap()` panic。`collect::<Result<_,_>>()` 短路于首个错误。
fn take_all<T>(v: Vec<Option<T>>, name: &'static str) -> Result<Vec<T>, QzdbError> {
    v.into_iter()
        .map(|o| o.ok_or_else(|| err(ErrorCode::Corrupted, format!("{name} not initialized"))))
        .collect()
}

// ---------------------------------------------------------------------------
// CRC32
// ---------------------------------------------------------------------------

static CRC32_TABLE: OnceLock<[u32; 256]> = OnceLock::new();

fn crc32_table() -> &'static [u32; 256] {
    CRC32_TABLE.get_or_init(|| {
        let mut table = [0u32; 256];
        for i in 0..256u32 {
            let mut c = i;
            for _ in 0..8 {
                if c & 1 != 0 {
                    c = (c >> 1) ^ 0xEDB88320;
                } else {
                    c >>= 1;
                }
            }
            table[i as usize] = c;
        }
        table
    })
}

/// 规范 CRC32（与参考实现一致）：bytes[0..16] + 4 个零字节（代替 CRC 字段）+ bytes[20..]。
fn compute_canonical_crc(data: &[u8]) -> u32 {
    let table = crc32_table();
    let mut crc: u32 = 0xFFFFFFFF;
    for &b in &data[..16.min(data.len())] {
        crc = table[((crc ^ b as u32) & 0xFF) as usize] ^ (crc >> 8);
    }
    for _ in 0..4 {
        crc = table[(crc & 0xFF) as usize] ^ (crc >> 8);
    }
    if data.len() > 20 {
        for &b in &data[20..] {
            crc = table[((crc ^ b as u32) & 0xFF) as usize] ^ (crc >> 8);
        }
    }
    crc ^ 0xFFFFFFFF
}

// ---------------------------------------------------------------------------
// 安全读取助手
// ---------------------------------------------------------------------------

/// 定长读取的统一入口：`off + N` 用 `checked_add` 防止 usize 回绕
/// （回绕会让 `off + N > len` 判为 false 而后续切片 panic），
/// 越界一律返回 `None`。const 泛型使长度在编译期确定，零额外开销。
#[inline(always)]
fn read_arr<const N: usize>(d: &[u8], off: usize) -> Option<[u8; N]> {
    let end = off.checked_add(N)?;
    let slice = d.get(off..end)?;
    let mut out = [0u8; N];
    out.copy_from_slice(slice);
    Some(out)
}

#[inline(always)]
fn safe_read_u16(d: &[u8], off: usize) -> Option<u16> {
    Some(u16::from_le_bytes(read_arr::<2>(d, off)?))
}

#[inline(always)]
fn safe_read_u24(d: &[u8], off: usize) -> Option<u32> {
    let b = read_arr::<3>(d, off)?;
    Some(b[0] as u32 | (b[1] as u32) << 8 | (b[2] as u32) << 16)
}

#[inline(always)]
fn safe_read_u32(d: &[u8], off: usize) -> Option<u32> {
    Some(u32::from_le_bytes(read_arr::<4>(d, off)?))
}

#[inline(always)]
fn safe_read_u64(d: &[u8], off: usize) -> Option<u64> {
    Some(u64::from_le_bytes(read_arr::<8>(d, off)?))
}

#[inline(always)]
fn safe_read_u48(d: &[u8], off: usize) -> Option<u64> {
    let b = read_arr::<6>(d, off)?;
    Some(
        b[0] as u64
            | (b[1] as u64) << 8
            | (b[2] as u64) << 16
            | (b[3] as u64) << 24
            | (b[4] as u64) << 32
            | (b[5] as u64) << 40,
    )
}

#[inline(always)]
fn safe_read_uint_width(d: &[u8], off: usize, width: usize) -> u32 {
    match width {
        0 | 1 => {
            if off < d.len() {
                d[off] as u32
            } else {
                0
            }
        }
        2 => safe_read_u16(d, off).map(|v| v as u32).unwrap_or(0),
        3 => safe_read_u24(d, off).unwrap_or(0),
        _ => safe_read_u32(d, off).unwrap_or(0),
    }
}

// ---------------------------------------------------------------------------
// 版本档次判定契约（FORMAT §10.3 —— 8 种 SDK 逐字一致）
//
// 档次的权威来源是 Header.VersionMask（offset 6，u16 LE）与
// GROUP_SCHEMA.groupId，二者都是 one-hot 位掩码：
//   bit0=std(1) bit1=asn(2) bit2=pro(4) bit3=max(8) bit4=ult(16)
// 字段个数只是最后兜底。EDITION_BY_BIT 按 bit0..bit4 顺序给出档次名。
// ---------------------------------------------------------------------------

/// bit0..bit4 对应的档次名。
pub const EDITION_BY_BIT: [&str; 5] = ["std", "asn", "pro", "max", "ult"];

/// 各档次的规范字段表（仅在文件未自带 Metadata field_names 时使用）。
pub fn edition_field_names(edition: &str) -> Option<&'static [&'static str]> {
    match edition {
        "std" => Some(&["continent", "country_code", "country", "province", "city", "isp"]),
        "asn" => Some(&[
            "continent", "country_code", "country", "isp", "asn", "as_name", "as_domain",
            "usage_type",
        ]),
        "pro" => Some(&[
            "continent", "country_code", "country", "province", "city", "district", "geo_id",
            "longitude", "latitude", "timezone", "isp",
        ]),
        "max" => Some(&[
            "continent", "country_code", "country", "province", "city", "district", "geo_id",
            "longitude", "latitude", "timezone", "isp", "asn", "as_name", "as_domain",
            "usage_type",
        ]),
        "ult" => Some(&[
            "continent", "continent_en", "country_code", "country_alpha3", "country",
            "country_en", "province", "province_en", "city", "city_en", "district",
            "district_en", "geo_id", "longitude", "latitude", "timezone", "languages",
            "currency_code", "phone_prefix", "emoji_flag", "isp", "asn", "as_name", "as_domain",
            "usage_type",
        ]),
        _ => None,
    }
}

/// 档次来源：来自 VersionMask/groupId（权威）。
pub const EDITION_SOURCE_VERSION_MASK: &str = "version_mask";
/// 档次来源：来自 Metadata primary_version / 单条目 version_list。
pub const EDITION_SOURCE_METADATA: &str = "metadata";
/// 档次来源：兜底，字段数唯一匹配。
pub const EDITION_SOURCE_INFERRED: &str = "inferred";
/// 档次来源：确实判定不出，不臆造。
pub const EDITION_SOURCE_UNKNOWN: &str = "unknown";

/// 字段名来源：文件自带 Metadata field_names。
pub const FIELD_NAMES_SOURCE_METADATA: &str = "metadata";
/// 字段名来源：已知档次的规范表。
pub const FIELD_NAMES_SOURCE_EDITION: &str = "edition";
/// 字段名来源：field_0..field_N-1 占位符。
pub const FIELD_NAMES_SOURCE_SYNTHETIC: &str = "synthetic";

/// one-hot 掩码 → 档次名；非 one-hot 或越界返回 ""。
pub fn edition_from_mask(mask: u16) -> &'static str {
    if mask == 0 || (mask & (mask - 1)) != 0 {
        return "";
    }
    let bit = mask.trailing_zeros() as usize;
    if bit < EDITION_BY_BIT.len() {
        EDITION_BY_BIT[bit]
    } else {
        ""
    }
}

/// 字段数 → 档次名（仅当该基数在规范表中唯一时才成立）。
fn edition_by_field_count(count: usize) -> &'static str {
    let mut hit = "";
    for ed in EDITION_BY_BIT.iter() {
        if let Some(names) = edition_field_names(ed) {
            if names.len() == count {
                if !hit.is_empty() {
                    return ""; // 基数不唯一，不猜
                }
                hit = ed;
            }
        }
    }
    hit
}

/// field_0..field_{n-1} 占位符。
fn synthetic_field_names(count: usize) -> Vec<String> {
    (0..count).map(|i| format!("field_{}", i)).collect()
}

// ---------------------------------------------------------------------------
// 字段名归一化（转小写 + 去除 `_` 与 `-`，API_CONTRACT §6）
// ---------------------------------------------------------------------------

fn normalize_key(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        if c == '_' || c == '-' {
            continue;
        }
        if c.is_ascii_alphabetic() {
            out.push(c.to_ascii_lowercase());
        } else {
            out.push(c);
        }
    }
    out
}

fn is_numeric_field_name(name: &str) -> bool {
    matches!(name, "geo_id" | "longitude" | "latitude" | "asn")
}

/// 检测 IPv4-Mapped IPv6 地址 (::ffff:a.b.c.d)。
#[inline(always)]
fn is_ipv4_mapped_v6(bytes: &[u8; 16]) -> bool {
    bytes[10] == 0xff && bytes[11] == 0xff && bytes[..10].iter().all(|&x| x == 0)
}

/// 原生浮点格式化（API_CONTRACT §8.2）：整数→无小数点，小数→6 位。
fn fmt_native_float(f: f64) -> String {
    if f.is_nan() || f.is_infinite() {
        return String::new();
    }
    if f == f.trunc() && f.abs() < 1e16 {
        format!("{}", f as i64)
    } else {
        format!("{:.6}", f)
    }
}

// ---------------------------------------------------------------------------
// 语义化用法类型（API_CONTRACT §6）
// ---------------------------------------------------------------------------

/// IP 使用场景类型。21 个已知场景 + 未知兜底。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UsageType {
    Known(KnownUsage),
    Unknown(String),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KnownUsage {
    AiCrawler,
    Backbone,
    Broadband,
    Business,
    Cdn,
    Cloud,
    Dns,
    DataCenter,
    Education,
    Finance,
    Government,
    Isp,
    Ixp,
    Iot,
    Mobile,
    Reserved,
    Satellite,
    Spider,
    Streaming,
    Unknown,
    Vpn,
}

impl KnownUsage {
    pub fn raw(self) -> &'static str {
        match self {
            KnownUsage::AiCrawler => "AICrawler",
            KnownUsage::Backbone => "Backbone",
            KnownUsage::Broadband => "Broadband",
            KnownUsage::Business => "Business",
            KnownUsage::Cdn => "CDN",
            KnownUsage::Cloud => "Cloud",
            KnownUsage::Dns => "DNS",
            KnownUsage::DataCenter => "DataCenter",
            KnownUsage::Education => "Education",
            KnownUsage::Finance => "Finance",
            KnownUsage::Government => "Government",
            KnownUsage::Isp => "ISP",
            KnownUsage::Ixp => "IXP",
            KnownUsage::Iot => "IoT",
            KnownUsage::Mobile => "Mobile",
            KnownUsage::Reserved => "Reserved",
            KnownUsage::Satellite => "Satellite",
            KnownUsage::Spider => "Spider",
            KnownUsage::Streaming => "Streaming",
            KnownUsage::Unknown => "Unknown",
            KnownUsage::Vpn => "VPN",
        }
    }
}

impl UsageType {
    pub fn from_raw(raw: &str) -> UsageType {
        let r = raw.trim();
        let k = match r.to_ascii_lowercase().as_str() {
            "aicrawler" => KnownUsage::AiCrawler,
            "backbone" => KnownUsage::Backbone,
            "broadband" => KnownUsage::Broadband,
            "business" => KnownUsage::Business,
            "cdn" => KnownUsage::Cdn,
            "cloud" => KnownUsage::Cloud,
            "dns" => KnownUsage::Dns,
            "datacenter" => KnownUsage::DataCenter,
            "education" => KnownUsage::Education,
            "finance" => KnownUsage::Finance,
            "government" => KnownUsage::Government,
            "isp" => KnownUsage::Isp,
            "ixp" => KnownUsage::Ixp,
            "iot" => KnownUsage::Iot,
            "mobile" => KnownUsage::Mobile,
            "reserved" => KnownUsage::Reserved,
            "satellite" => KnownUsage::Satellite,
            "spider" => KnownUsage::Spider,
            "streaming" => KnownUsage::Streaming,
            "unknown" => KnownUsage::Unknown,
            "vpn" => KnownUsage::Vpn,
            _ => {
                if r.is_empty() {
                    return UsageType::Unknown("Unknown".to_string());
                }
                return UsageType::Unknown(r.to_string());
            }
        };
        UsageType::Known(k)
    }

    pub fn raw_value(&self) -> &str {
        match self {
            UsageType::Known(k) => k.raw(),
            UsageType::Unknown(s) => s.as_str(),
        }
    }

    pub fn is_known(&self) -> bool {
        matches!(self, UsageType::Known(_))
    }
}

// ---------------------------------------------------------------------------
// GeoInfo 响应实体
// ---------------------------------------------------------------------------

/// 单条 IP 的地理信息响应（API_CONTRACT §6）。
#[derive(Debug, Clone)]
pub struct GeoInfo {
    pub field_names: Arc<Vec<String>>,
    pub values: Vec<String>,
    norm_map: Arc<HashMap<String, usize>>,
    numeric_indices: Arc<Vec<usize>>,
}

impl GeoInfo {
    /// 按字段名取值（大小写/下划线/连字符不敏感）。未匹配返回 ""，绝不 panic。
    pub fn get(&self, name: &str) -> &str {
        self.norm_map
            .get(&normalize_key(name))
            .and_then(|i| self.values.get(*i))
            .map(|s| s.as_str())
            .unwrap_or("")
    }

    /// 全部字段以 `|` 拼接（直接拼接已解码字符串，禁止重新格式化浮点）。
    pub fn to_pipe(&self) -> String {
        self.values.join("|")
    }

    /// 字段名 → 值（全 string）。
    pub fn to_map(&self) -> HashMap<String, String> {
        let mut m = HashMap::with_capacity(self.field_names.len());
        for (i, name) in self.field_names.iter().enumerate() {
            let v = self.values.get(i).cloned().unwrap_or_default();
            m.insert(name.clone(), v);
        }
        m
    }

    /// 手写 JSON 序列化（API_CONTRACT §6）：longitude/latitude/asn/geo_id 输出为数字，
    /// 空值 → 数值字段 `null`、其余 `""`；无法解析为数字 → `null`。
    pub fn to_json(&self) -> String {
        let mut out = String::from("{");
        let mut first = true;
        for (i, name) in self.field_names.iter().enumerate() {
            if name.is_empty() {
                continue;
            }
            let val = self.values.get(i).map(|s| s.as_str()).unwrap_or("");
            if !first {
                out.push(',');
            }
            first = false;
            out.push('"');
            out.push_str(&escape_json(name));
            out.push_str("\":");
            let numeric = self.numeric_indices.contains(&i);
            if val.is_empty() {
                out.push_str(if numeric { "null" } else { "\"\"" });
            } else if numeric {
                if is_json_number(val) {
                    out.push_str(val);
                } else {
                    out.push_str("null");
                }
            } else {
                out.push('"');
                out.push_str(&escape_json(val));
                out.push('"');
            }
        }
        out.push('}');
        out
    }

    pub fn to_string_pipe(&self) -> String {
        self.to_pipe()
    }

    // ---- 语义化 Getter 全集（缺失返回 "" 或 None） ----

    pub fn country(&self) -> &str { self.get("country") }
    pub fn country_en(&self) -> &str { self.get("country_en") }
    pub fn province(&self) -> &str { self.get("province") }
    pub fn province_en(&self) -> &str { self.get("province_en") }
    pub fn city(&self) -> &str { self.get("city") }
    pub fn city_en(&self) -> &str { self.get("city_en") }
    pub fn district(&self) -> &str { self.get("district") }

    pub fn geo_id(&self) -> Option<u64> {
        let v = self.get("geo_id");
        if v.is_empty() {
            None
        } else {
            v.parse::<u64>().ok()
        }
    }

    pub fn longitude(&self) -> Option<f64> {
        let v = self.get("longitude");
        if v.is_empty() {
            None
        } else {
            v.parse::<f64>().ok()
        }
    }

    pub fn latitude(&self) -> Option<f64> {
        let v = self.get("latitude");
        if v.is_empty() {
            None
        } else {
            v.parse::<f64>().ok()
        }
    }

    pub fn timezone(&self) -> &str { self.get("timezone") }
    pub fn isp(&self) -> &str { self.get("isp") }
    pub fn isp_en(&self) -> &str { self.get("isp_en") }

    pub fn asn(&self) -> Option<u64> {
        let v = self.get("asn");
        if v.is_empty() {
            None
        } else {
            v.parse::<u64>().ok()
        }
    }

    pub fn as_name(&self) -> &str { self.get("as_name") }
    pub fn as_domain(&self) -> &str { self.get("as_domain") }

    pub fn usage_type(&self) -> UsageType {
        UsageType::from_raw(self.get("usage_type"))
    }

    pub fn country_alpha2(&self) -> &str { self.get("country_alpha2") }
    pub fn country_alpha3(&self) -> &str { self.get("country_alpha3") }
    pub fn currency_code(&self) -> &str { self.get("currency_code") }
    pub fn currency_name(&self) -> &str { self.get("currency_name") }
    pub fn phone_prefix(&self) -> &str { self.get("phone_prefix") }
    pub fn emoji_flag(&self) -> &str { self.get("emoji_flag") }
    pub fn languages(&self) -> &str { self.get("languages") }

    /// `getCidr()` 恒返回 ""（CIDR 非数据库字段）。真实网段用 `reader.lookup_cidr(ip)`。
    pub fn get_cidr(&self) -> &str { self.get("cidr") }
}

impl std::fmt::Display for GeoInfo {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.to_pipe())
    }
}

fn escape_json(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{08}' => out.push_str("\\b"),
            '\u{0c}' => out.push_str("\\f"),
            c if (c as u32) < 0x20 => {
                out.push_str(&format!("\\u{:04x}", c as u32));
            }
            c => out.push(c),
        }
    }
    out
}

fn is_json_number(v: &str) -> bool {
    if v.is_empty() {
        return false;
    }
    let mut chars = v.chars();
    let mut has_digit = false;
    let mut dot = false;
    if let Some(c) = chars.clone().next() {
        if c == '-' {
            chars.next();
        }
    }
    for c in chars {
        if c.is_ascii_digit() {
            has_digit = true;
        } else if c == '.' && !dot {
            dot = true;
        } else {
            return false;
        }
    }
    has_digit
}

// ---------------------------------------------------------------------------
// 行号三元组 / 批量结果
// ---------------------------------------------------------------------------

/// 行号三元组（geo_id, asn_id, usage_id）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RowIds {
    pub geo_id: u32,
    pub asn_id: u32,
    pub usage_id: u32,
}

/// 批量查询结果：三态（命中 / 未命中 / 错误）。
#[derive(Debug, Clone)]
pub struct BatchResult {
    pub ip: String,
    pub geo_info: Option<GeoInfo>,
    pub error: Option<String>,
}

// ---------------------------------------------------------------------------
// 不可变快照（加载期构建一次，查询期只读）
// ---------------------------------------------------------------------------

struct CacheSlot {
    key: u32,
    val: Option<Arc<GeoInfo>>,
}

pub struct SnapshotInner {
    data: Arc<Vec<u8>>,
    group_index: usize,

    // 头部解析结果
    _flags: u16,
    has_v4: bool,
    has_v6: bool,
    v4_node_24: bool,
    v6_node_24: bool,
    v6_jump_bits: usize,
    pool_count: usize,
    _pool_idx_size: usize,
    _geo_count: usize,
    row_count: usize,
    v4_node_count: u32,
    v6_node_count: u32,
    ip_row_size: usize,

    row_geo_width: usize,
    row_asn_width: usize,
    row_usage_width: usize,

    off_v4_jump: u64,
    off_v4_nodes: u64,
    off_v6_jump: u64,
    off_v6_nodes: u64,
    off_ip_row: u64,
    off_geo_entries: u64,
    _off_pools: u64,
    _off_meta: u64,
    off_row_schema: u64,
    _off_group_schema: u64,

    group_field_counts: Vec<usize>,
    group_entry_counts: Vec<u32>,
    group_dim_masks: Vec<u16>,
    group_entry_offsets: Vec<u64>,
    group_strides: Vec<usize>,
    group_field_widths: Vec<Vec<usize>>,
    group_field_offsets: Vec<Vec<usize>>,
    group_field_native: Vec<Vec<bool>>,
    group_field_native_type: Vec<Vec<usize>>,

    pools: Vec<Vec<Vec<String>>>,

    // 元信息
    field_names: Arc<Vec<String>>,
    norm_map: Arc<HashMap<String, usize>>,
    numeric_indices: Arc<Vec<usize>>,
    version: String,
    description: String,
    data_month: String,
    build_time: String,
    edition: String,
    version_mask: u16,
    edition_source: &'static str,
    field_names_source: &'static str,
    canonical_crc: u32,

    // per-snapshot 有界无锁 GeoInfo 缓存
    geo_cache: Vec<std::sync::Mutex<CacheSlot>>,
}

impl SnapshotInner {
    fn from_bytes(
        data: Arc<Vec<u8>>,
        group_index: usize,
        verify_crc: bool,
    ) -> Result<SnapshotInner, QzdbError> {
        let d = data.as_slice();
        let data_len = d.len() as u64;

        if data_len < 4 {
            return Err(err(ErrorCode::Corrupted, "file too small (<4 bytes)"));
        }

        if &d[..4] != b"QZDB" {
            return Err(err(ErrorCode::BadMagic, "invalid magic"));
        }

        if data_len < 192 {
            return Err(err(ErrorCode::Corrupted, "file too small (<192 bytes)"));
        }

        let fmt_ver = d[4];
        if fmt_ver != 1 {
            return Err(err(
                ErrorCode::Unsupported,
                format!("unsupported version: {} (only v1 supported)", fmt_ver),
            ));
        }

        // VersionMask（offset 6）是档次判定的权威来源，必须在 flags 之前读出。
        let version_mask = safe_read_u16(d, 6).unwrap();

        let flags = safe_read_u16(d, 8).unwrap();
        let has_v4 = flags & 1 != 0;
        let has_v6 = flags & 2 != 0;
        let v4_node_24 = flags & 0x10 != 0;
        let v6_node_24 = flags & 0x20 != 0;

        let mut v6_jump_bits = d[11] as usize;
        if v6_jump_bits == 0 {
            v6_jump_bits = 16;
        }
        if !(8..=20).contains(&v6_jump_bits) {
            return Err(err(
                ErrorCode::InvalidParam,
                format!("v6_jump_bits out of range: {}", v6_jump_bits),
            ));
        }

        let pool_count = d[12] as usize;
        let pool_idx_size = d[13] as usize;
        if pool_idx_size != 2 && pool_idx_size != 3 {
            return Err(err(
                ErrorCode::InvalidParam,
                format!("pool_idx_size must be 2 or 3, got {}", pool_idx_size),
            ));
        }

        let geo_count = safe_read_u16(d, 14).unwrap() as usize;
        let row_count = safe_read_u32(d, 20).unwrap() as usize;
        let _v4_rec_count = safe_read_u32(d, 24).unwrap();
        let _v6_rec_count = safe_read_u32(d, 28).unwrap();
        let build_date = i32::from_le_bytes(d[32..36].try_into().unwrap());

        let hs = safe_read_u32(d, 36).unwrap();
        if hs != 192 {
            return Err(err(ErrorCode::BadHeader, format!("unexpected header size: {}", hs)));
        }

        let off_row_schema = safe_read_u64(d, 40).unwrap();
        let off_group_schema = safe_read_u64(d, 48).unwrap();
        let off_v4_jump = safe_read_u64(d, 64).unwrap();
        let off_v4_nodes = safe_read_u64(d, 72).unwrap();
        let off_v6_jump = safe_read_u64(d, 80).unwrap();
        let off_v6_nodes = safe_read_u64(d, 88).unwrap();
        let off_ip_row = safe_read_u64(d, 96).unwrap();
        let off_geo_entries = safe_read_u64(d, 104).unwrap();
        let off_pools = safe_read_u64(d, 136).unwrap();
        let off_meta = safe_read_u64(d, 144).unwrap();

        let v4_node_count = safe_read_u32(d, 152).unwrap();
        let v6_node_count = safe_read_u32(d, 156).unwrap();
        let ip_row_size = safe_read_u32(d, 160).unwrap() as usize;
        if !(1..=64).contains(&ip_row_size) {
            return Err(err(
                ErrorCode::InvalidParam,
                format!("ip_row_size out of range [1,64]: {}", ip_row_size),
            ));
        }

        let geo_entry_group_count = safe_read_u32(d, 164).unwrap() as usize;
        if !(1..=255).contains(&geo_entry_group_count) {
            return Err(err(
                ErrorCode::InvalidParam,
                format!("geo_entry_group_count out of range [1,255]: {}", geo_entry_group_count),
            ));
        }

        fn check_offset(
            data_len: u64,
            offset: u64,
            required: u64,
            field: &'static str,
        ) -> Result<(), QzdbError> {
            let end = match offset.checked_add(required) {
                Some(end) => end,
                None => {
                    return Err(err(ErrorCode::OutOfBounds, format!("overflow at {}", field)));
                }
            };
            if end > data_len {
                return Err(err(
                    ErrorCode::OutOfBounds,
                    format!("section {} out of bounds (need {}, have {})", field, end, data_len),
                ));
            }
            Ok(())
        }

        let v4_node_size = if v4_node_24 { 6u64 } else { 8u64 };
        let v6_node_size = if v6_node_24 { 6u64 } else { 8u64 };
        let v6_jump_size = (1u64 << v6_jump_bits) * 4;

        check_offset(data_len, off_v4_jump, 65536 * 4, "off_v4_jump")?;
        check_offset(data_len, off_v4_nodes, v4_node_count as u64 * v4_node_size, "off_v4_nodes")?;
        check_offset(data_len, off_v6_jump, v6_jump_size, "off_v6_jump")?;
        check_offset(data_len, off_v6_nodes, v6_node_count as u64 * v6_node_size, "off_v6_nodes")?;
        check_offset(data_len, off_ip_row, row_count as u64 * ip_row_size as u64, "off_ip_row")?;
        if off_geo_entries > 0 {
            check_offset(data_len, off_geo_entries, 16, "off_geo_entries")?;
        }
        if off_pools > 0 {
            check_offset(data_len, off_pools, 4, "off_pools")?;
        }
        if off_meta > 0 {
            check_offset(data_len, off_meta, 4, "off_meta")?;
        }
        if off_group_schema > 0 {
            check_offset(data_len, off_group_schema, 2, "off_group_schema")?;
        }
        if off_row_schema > 0 {
            check_offset(data_len, off_row_schema, 1, "off_row_schema")?;
        }

        // ---- ROW_SCHEMA ----
        let mut row_geo_width = 3;
        let mut row_asn_width = 3;
        let mut row_usage_width = 0;
        if off_row_schema > 0 {
            let sp = off_row_schema as usize;
            // 注意：上面的 check_offset 只保证 sp 本身可读（required=1），
            // 而这里还要读 sp+1（stride），必须各自单独收口。
            let f_count = ro!(rget(d, sp), "row_schema.field_count")? as usize;
            let stride = ro!(rget(d, sp + 1), "row_schema.stride")? as usize;
            if (1..=8).contains(&f_count)
                && sp + 4 + f_count * 4 <= d.len()
                && stride == ip_row_size
            {
                let mut wpos = sp + 4;
                let mut g2 = 0usize;
                let mut a2 = 0usize;
                let mut u2 = 0usize;
                let mut total = 0usize;
                let mut ok = true;
                for _ in 0..f_count {
                    let fid = d[wpos];
                    let w = d[wpos + 1] as usize;
                    if fid == 0 {
                        g2 = w;
                    } else if fid == 1 {
                        a2 = w;
                    } else if fid == 2 {
                        u2 = w;
                    }
                    wpos += 4;
                    total += w;
                    if !(1..=4).contains(&w) {
                        ok = false;
                    }
                }
                if ok && total == ip_row_size {
                    row_geo_width = g2;
                    row_asn_width = a2;
                    row_usage_width = u2;
                }
            }
        }

        // ---- 组入口偏移（头部 48 位） ----
        let mut group_entry_offsets = Vec::with_capacity(4);
        for i in 0..4 {
            group_entry_offsets.push(ro!(safe_read_u48(d, 168 + i * 6), "group_entry_offsets")?);
        }

        let gm_off = off_geo_entries as usize;
        let group_count_in_table = ro!(rget(d, gm_off), "geo_entries.group_count")? as usize;
        let mut gm_off = gm_off + 1;

        let mut actual_groups = group_count_in_table.min(1.max(geo_entry_group_count));
        if actual_groups > 4 {
            actual_groups = 4;
        }
        if actual_groups < 1 {
            return Err(err(ErrorCode::Corrupted, "group count is 0"));
        }
        if group_index >= actual_groups {
            return Err(err(
                ErrorCode::InvalidParam,
                format!("group_index {} out of range (groups={})", group_index, actual_groups),
            ));
        }

        let mut group_field_counts = vec![0; actual_groups];
        let mut group_entry_counts = vec![0; actual_groups];
        let mut group_dim_masks = vec![0u16; actual_groups];
        // GROUP_SCHEMA.groupId：每组自己的 one-hot 版本位掩码（FORMAT §10.2）。
        let mut group_ids: Vec<u16> = vec![0; actual_groups];

        for gi in 0..actual_groups {
            group_field_counts[gi] = ro!(rget(d, gm_off), "group_field_count")? as usize;
            gm_off += 1;
            group_entry_counts[gi] = ro!(safe_read_u32(d, gm_off), "group_entry_count")?;
            gm_off += 4;
            group_dim_masks[gi] = ro!(safe_read_u16(d, gm_off), "group_dim_mask")?;
            gm_off += 2;
        }

        let mut group_strides = vec![0; actual_groups];
        let mut group_field_widths: Vec<Option<Vec<usize>>> = vec![None; actual_groups];
        let mut group_field_offsets: Vec<Option<Vec<usize>>> = vec![None; actual_groups];
        let mut group_field_native: Vec<Option<Vec<bool>>> = vec![None; actual_groups];
        let mut group_field_native_type: Vec<Option<Vec<usize>>> = vec![None; actual_groups];

        if off_group_schema > 0 {
            let mut sp = off_group_schema as usize;
            let gs_group_count = ro!(safe_read_u16(d, sp), "group_schema_count")? as usize;
            sp += 2;
            let max_gs = gs_group_count.min(actual_groups);
            for gi in 0..max_gs {
                group_ids[gi] = ro!(safe_read_u16(d, sp), "group_schema_group_id")?;
                sp += 2;
                let fld_count = ro!(safe_read_u16(d, sp), "group_field_count")? as usize;
                sp += 2;
                sp += 4;
                let stride = ro!(safe_read_u32(d, sp), "group_stride")? as usize;
                sp += 4;
                sp += 4;
                if gi < actual_groups {
                    group_strides[gi] = stride;
                    let mut widths = vec![0; fld_count];
                    let mut offsets = vec![0; fld_count];
                    let mut natives = vec![false; fld_count];
                    let mut nat_types = vec![0; fld_count];
                    for fi in 0..fld_count {
                        // fieldId 只是槽位序号 0..N-1，不带跨档语义，读过即弃。
                        sp += 2;
                        widths[fi] = ro!(rget(d, sp), "group_field_width")? as usize;
                        sp += 1;
                        let ff = ro!(rget(d, sp), "group_field_flags")?;
                        sp += 1;
                        natives[fi] = (ff & 0x01) != 0;
                        nat_types[fi] = ((ff >> 1) & 0x03) as usize;
                        offsets[fi] = ro!(safe_read_u32(d, sp), "group_field_offset")? as usize;
                        sp += 4;
                        sp += 4;
                    }
                    group_field_widths[gi] = Some(widths);
                    group_field_offsets[gi] = Some(offsets);
                    group_field_native[gi] = Some(natives);
                    group_field_native_type[gi] = Some(nat_types);
                } else {
                    sp += fld_count * 12;
                }
            }
        }

        for g in 0..actual_groups {
            if group_strides[g] == 0 {
                group_strides[g] = group_field_counts[g] * pool_idx_size;
            }
            if group_field_widths[g].is_none() {
                group_field_widths[g] = Some(vec![pool_idx_size; group_field_counts[g]]);
            }
            if group_field_offsets[g].is_none() {
                group_field_offsets[g] =
                    Some((0..group_field_counts[g]).map(|i| i * pool_idx_size).collect());
            }
            if group_field_native[g].is_none() {
                group_field_native[g] = Some(vec![false; group_field_counts[g]]);
            }
            if group_field_native_type[g].is_none() {
                group_field_native_type[g] = Some(vec![0; group_field_counts[g]]);
            }
        }

        // 上面的补齐循环保证每个槽位都已填充；这里统一收口为具体类型，
        // 万一将来补齐逻辑被改坏也只会返回 Corrupted，而不会 panic。
        let mut group_field_widths = take_all(group_field_widths, "group_field_widths")?;
        let mut group_field_offsets = take_all(group_field_offsets, "group_field_offsets")?;
        let mut group_field_native = take_all(group_field_native, "group_field_native")?;
        let mut group_field_native_type =
            take_all(group_field_native_type, "group_field_native_type")?;

        // 关键不变量：字段数有两个来源——GEO_ENTRIES 表的 group_field_counts[g]
        // 与 GROUP_SCHEMA 的 fld_count。畸形文件可让二者不一致，而查询热路径
        // 以 group_field_counts[g] 为循环上界、直接下标访问 widths/offsets/...，
        // 长度不足即越界 panic。这里在解析期一次性对齐：不一致则该组回退到
        // pool_idx_size 默认布局，使 `len() == group_field_counts[g]` 恒成立，
        // 热路径因此无需任何逐次边界判断。
        for g in 0..actual_groups {
            let fc = group_field_counts[g];
            if group_field_widths[g].len() == fc
                && group_field_offsets[g].len() == fc
                && group_field_native[g].len() == fc
                && group_field_native_type[g].len() == fc
            {
                continue;
            }
            group_strides[g] = fc * pool_idx_size;
            group_field_widths[g] = vec![pool_idx_size; fc];
            group_field_offsets[g] = (0..fc).map(|i| i * pool_idx_size).collect();
            group_field_native[g] = vec![false; fc];
            group_field_native_type[g] = vec![0; fc];
        }

        // ---- Meta / 档次 / 字段名（FORMAT §10.3 统一契约） ----
        //
        //   edition      1. GROUP_SCHEMA.groupId / Header.VersionMask 的 one-hot 位
        //                2. Metadata primary_version，或只有一项的 version_list
        //                3. 字段数唯一匹配（兜底）
        //                4. ""（unknown）—— 判定不出就不臆造
        //   field_names  1. Metadata field_names（基数与该组一致时）
        //                2. 已知档次且基数一致时用规范表
        //                3. field_0..field_N-1 占位符
        let meta = parse_metadata(d, off_meta);
        let version = meta.version_name;
        let description = meta.description;

        let mut group_field_names: Vec<Vec<String>> = Vec::with_capacity(actual_groups);
        let mut group_editions: Vec<&'static str> = Vec::with_capacity(actual_groups);
        let mut group_edition_sources: Vec<&'static str> = Vec::with_capacity(actual_groups);
        let mut group_name_sources: Vec<&'static str> = Vec::with_capacity(actual_groups);

        // Metadata 里的档次名（type 4 优先，其次只有一项的 type 1 列表）。
        let meta_edition: String = {
            let pv = meta.primary_version.trim();
            if !pv.is_empty() {
                pv.to_string()
            } else {
                let tokens: Vec<&str> = version
                    .split(',')
                    .map(|s| s.trim())
                    .filter(|s| !s.is_empty())
                    .collect();
                if tokens.len() == 1 { tokens[0].to_string() } else { String::new() }
            }
        };

        for g in 0..actual_groups {
            let num_fields = group_field_counts[g];

            // edition：先用本组自己的掩码，再回落到文件级掩码
            let mask = if group_ids[g] != 0 { group_ids[g] } else { version_mask };
            let mut edition: &'static str = edition_from_mask(mask);
            let mut source: &'static str = EDITION_SOURCE_VERSION_MASK;
            if edition.is_empty() && !meta_edition.is_empty() {
                // 只接受规范表里已知的档次名，避免把任意字符串当档次外传。
                if let Some(known) = EDITION_BY_BIT.iter().find(|e| **e == meta_edition) {
                    edition = known;
                    source = EDITION_SOURCE_METADATA;
                }
            }
            if edition.is_empty() {
                edition = edition_by_field_count(num_fields);
                source = if edition.is_empty() {
                    EDITION_SOURCE_UNKNOWN
                } else {
                    EDITION_SOURCE_INFERRED
                };
            }

            // 字段名
            let (names, names_source) = if meta.field_names.len() == num_fields
                && num_fields > 0
            {
                (meta.field_names.clone(), FIELD_NAMES_SOURCE_METADATA)
            } else {
                match edition_field_names(edition) {
                    Some(canon) if canon.len() == num_fields => (
                        canon.iter().map(|s| s.to_string()).collect(),
                        FIELD_NAMES_SOURCE_EDITION,
                    ),
                    _ => (synthetic_field_names(num_fields), FIELD_NAMES_SOURCE_SYNTHETIC),
                }
            };

            group_field_names.push(names);
            group_editions.push(edition);
            group_edition_sources.push(source);
            group_name_sources.push(names_source);
        }

        let edition = group_editions[group_index].to_string();
        let edition_source = group_edition_sources[group_index];
        let field_names_source = group_name_sources[group_index];
        let field_names: Arc<Vec<String>> = Arc::new(group_field_names[group_index].clone());

        let mut norm_map: HashMap<String, usize> = HashMap::with_capacity(field_names.len());
        for (i, n) in field_names.iter().enumerate() {
            norm_map.insert(normalize_key(n), i);
        }
        let norm_map = Arc::new(norm_map);

        let numeric_indices: Vec<usize> = field_names
            .iter()
            .enumerate()
            .filter(|(_, n)| is_numeric_field_name(n))
            .map(|(i, _)| i)
            .collect();
        let numeric_indices = Arc::new(numeric_indices);

        // ---- 维度掩码修复 ----
        // 只看该组解析出来的字段名里有没有 asn。fieldId 只是槽位序号（0..N-1），
        // 不带任何跨档语义，绝不可用来判定维度。
        let asn_key = normalize_key("asn");
        for (g, dim_mask) in group_dim_masks.iter_mut().enumerate() {
            if *dim_mask != 0 {
                continue;
            }
            let has_asn = group_field_names
                .get(g)
                .map(|names| names.iter().any(|n| normalize_key(n) == asn_key))
                .unwrap_or(false);
            *dim_mask = if has_asn { 0x02 } else { 0x01 };
        }

        // ---- pools (eager) ----
        let pools = parse_pools(
            d,
            &group_field_counts,
            &group_field_native,
            off_pools,
            off_meta,
            &off_row_schema,
            pool_idx_size,
        );

        // ---- 构建元数据字符串 ----
        let (data_month, build_time) = if build_date > 0 {
            let y = build_date / 10000;
            let m = (build_date / 100) % 100;
            let dd = build_date % 100;
            (
                format!("{:04}-{:02}", y, m),
                format!("{:04}-{:02}-{:02}", y, m, dd),
            )
        } else {
            (String::new(), String::new())
        };

        let canonical_crc = compute_canonical_crc(d);
        if verify_crc {
            let stored = safe_read_u32(d, 16).unwrap_or(0);
            if stored != canonical_crc {
                return Err(err(
                    ErrorCode::Corrupted,
                    format!(
                        "CRC32 mismatch: stored=0x{:08x} calculated=0x{:08x}",
                        stored, canonical_crc
                    ),
                ));
            }
        }

        let geo_cache = (0..GEO_CACHE_SIZE)
            .map(|_| std::sync::Mutex::new(CacheSlot { key: u32::MAX, val: None }))
            .collect();

        Ok(SnapshotInner {
            data,
            group_index,
            _flags: flags,
            has_v4,
            has_v6,
            v4_node_24,
            v6_node_24,
            v6_jump_bits,
            pool_count,
            _pool_idx_size: pool_idx_size,
            _geo_count: geo_count,
            row_count,
            v4_node_count,
            v6_node_count,
            ip_row_size,
            row_geo_width,
            row_asn_width,
            row_usage_width,
            off_v4_jump,
            off_v4_nodes,
            off_v6_jump,
            off_v6_nodes,
            off_ip_row,
            off_geo_entries,
            _off_pools: off_pools,
            _off_meta: off_meta,
            off_row_schema,
            _off_group_schema: off_group_schema,
            group_field_counts,
            group_entry_counts,
            group_dim_masks,
            group_entry_offsets,
            group_strides,
            group_field_widths,
            group_field_offsets,
            group_field_native,
            group_field_native_type,
            pools,
            field_names,
            norm_map,
            numeric_indices,
            version,
            description,
            data_month,
            build_time,
            edition,
            version_mask,
            edition_source,
            field_names_source,
            canonical_crc,
            geo_cache,
        })
    }

    // ---- Trie 子节点读取（保留哨兵位） ----
    #[inline(always)]
    fn get_v4_child(&self, node_idx: u32, bit: u32) -> u32 {
        if node_idx >= self.v4_node_count {
            return 0;
        }
        if self.v4_node_24 {
            let off = self.off_v4_nodes as usize + node_idx as usize * 6 + if bit == 0 { 0 } else { 3 };
            let v = safe_read_u24(self.data.as_slice(), off).unwrap_or(0);
            if v & 0x800000 != 0 {
                (v & 0x7FFFFF) | SENTINEL
            } else {
                v
            }
        } else {
            let off = self.off_v4_nodes as usize + node_idx as usize * 8 + bit as usize * 4;
            safe_read_u32(self.data.as_slice(), off).unwrap_or(0)
        }
    }

    #[inline(always)]
    fn get_v6_child(&self, node_idx: u32, bit: u32) -> u32 {
        if node_idx >= self.v6_node_count {
            return 0;
        }
        if self.v6_node_24 {
            let off = self.off_v6_nodes as usize + node_idx as usize * 6 + if bit == 0 { 0 } else { 3 };
            let v = safe_read_u24(self.data.as_slice(), off).unwrap_or(0);
            if v & 0x800000 != 0 {
                (v & 0x7FFFFF) | SENTINEL
            } else {
                v
            }
        } else {
            let off = self.off_v6_nodes as usize + node_idx as usize * 8 + bit as usize * 4;
            safe_read_u32(self.data.as_slice(), off).unwrap_or(0)
        }
    }

    /// V4 Trie 行走，返回 (row_id, 前缀长度)；未命中返回 None。row_id 已剥离 SENTINEL。
    fn trie_walk_v4(&self, ip: u32) -> Option<(u32, u8)> {
        if !self.has_v4 || self.off_v4_jump == 0 {
            return None;
        }
        let hi16 = ((ip >> 16) & 0xFFFF) as usize;
        let ptr = safe_read_u32(self.data.as_slice(), self.off_v4_jump as usize + hi16 * 4)?;
        if ptr == 0 {
            return None;
        }
        if ptr & SENTINEL != 0 {
            return self.walk_v4_depth(ip, 0, 0, 16);
        }
        self.walk_v4_depth(ip, ptr, 16, 32)
    }

    fn walk_v4_depth(&self, ip: u32, mut idx: u32, start_depth: u8, max_depth: u8) -> Option<(u32, u8)> {
        if start_depth >= max_depth {
            return None;
        }
        let mut depth = start_depth;
        while depth < max_depth {
            if idx >= self.v4_node_count {
                return None;
            }
            let bit = (ip >> (31 - depth)) & 1;
            let child = self.get_v4_child(idx, bit);
            if child == 0 {
                return None;
            }
            if child & SENTINEL != 0 {
                return Some((child & SENTINEL_MASK_31, depth + 1));
            }
            idx = child;
            depth += 1;
        }
        None
    }

    /// V6 Trie 行走，返回 (row_id, 前缀长度)。
    fn trie_walk_v6(&self, bytes: &[u8; 16]) -> Option<(u32, u8)> {
        if !self.has_v6 || self.off_v6_jump == 0 {
            return None;
        }
        let shift = 128 - self.v6_jump_bits;
        let idx_jump = ((u128::from_be_bytes(*bytes) >> shift) & ((1u128 << self.v6_jump_bits) - 1)) as usize;
        let ptr = safe_read_u32(self.data.as_slice(), self.off_v6_jump as usize + idx_jump * 4)?;
        if ptr == 0 {
            return None;
        }
        if ptr & SENTINEL != 0 {
            return self.walk_v6_depth(bytes, 0, 0, self.v6_jump_bits as u8);
        }
        self.walk_v6_depth(bytes, ptr, self.v6_jump_bits as u8, 128)
    }

    fn walk_v6_depth(
        &self,
        bytes: &[u8; 16],
        mut idx: u32,
        start_depth: u8,
        max_depth: u8,
    ) -> Option<(u32, u8)> {
        if start_depth >= max_depth {
            return None;
        }
        let mut depth = start_depth;
        while depth < max_depth {
            if idx >= self.v6_node_count {
                return None;
            }
            let bit = ((bytes[(depth >> 3) as usize] as u32) >> (7 - (depth & 7))) & 1;
            let child = self.get_v6_child(idx, bit);
            if child == 0 {
                return None;
            }
            if child & SENTINEL != 0 {
                return Some((child & SENTINEL_MASK_31, depth + 1));
            }
            idx = child;
            depth += 1;
        }
        None
    }

    #[inline(always)]
    fn read_uint_width(&self, off: usize, width: usize) -> u32 {
        safe_read_uint_width(self.data.as_slice(), off, width)
    }

    fn read_ip_row(&self, row_id: u32) -> (u32, u32, u32) {
        if row_id == 0 || row_id >= self.row_count as u32 {
            return (0, 0, 0);
        }
        let off = self.off_ip_row as usize + row_id as usize * self.ip_row_size;
        let d = self.data.as_slice();
        if self.off_row_schema > 0 {
            let mut p = off;
            let geo_id = safe_read_uint_width(d, p, self.row_geo_width);
            p += self.row_geo_width;
            let asn_id = if self.row_asn_width > 0 {
                safe_read_uint_width(d, p, self.row_asn_width)
            } else {
                0
            };
            let usage_id = if self.row_usage_width > 0 {
                safe_read_uint_width(d, p + self.row_asn_width, self.row_usage_width)
            } else {
                0
            };
            (geo_id, asn_id, usage_id)
        } else {
            let geo_id = safe_read_u24(d, off).unwrap_or(0);
            let asn_id = safe_read_u24(d, off + 3).unwrap_or(0);
            let usage_id = if self.ip_row_size >= 9 {
                safe_read_u24(d, off + 6).unwrap_or(0)
            } else {
                0
            };
            (geo_id, asn_id, usage_id)
        }
    }

    fn resolve_row_id(&self, row_id: u32) -> Option<Arc<GeoInfo>> {
        let (geo_id, asn_id, usage_id) = self.read_ip_row(row_id);
        let mask = *self.group_dim_masks.get(self.group_index)?;
        let entry_id = if mask & 0x02 != 0 {
            asn_id
        } else if mask & 0x04 != 0 {
            usage_id
        } else {
            geo_id
        };
        if entry_id == 0 {
            return None;
        }
        self.resolve_geo(entry_id)
    }

    /// 有界无锁缓存解析：键为 entry_id；返回前校验 key 一致，碰撞只重算、绝不返回错值。
    fn resolve_geo(&self, entry_id: u32) -> Option<Arc<GeoInfo>> {
        if entry_id == 0 || entry_id >= self.group_entry_counts[self.group_index] {
            return None;
        }
        let slot = &self.geo_cache[(entry_id as usize) & (GEO_CACHE_SIZE - 1)];
        {
            let guard = slot.lock().unwrap();
            if guard.key == entry_id {
                if let Some(v) = &guard.val {
                    return Some(Arc::clone(v));
                }
            }
        }
        let geo = self.build_geo(entry_id);
        let mut guard = slot.lock().unwrap();
        guard.key = entry_id;
        guard.val = Some(Arc::clone(&geo));
        Some(geo)
    }

    fn build_geo(&self, entry_id: u32) -> Arc<GeoInfo> {
        let gi = self.group_index;
        let fc = self.group_field_counts[gi];
        let entry_off = self.off_geo_entries as usize + self.group_entry_offsets[gi] as usize
            + entry_id as usize * self.group_strides[gi];
        let d = self.data.as_slice();
        let widths = &self.group_field_widths[gi];
        let offsets = &self.group_field_offsets[gi];
        let natives = &self.group_field_native[gi];
        let nat_types = &self.group_field_native_type[gi];
        let pools = &self.pools[gi];

        let mut values = Vec::with_capacity(fc);
        for i in 0..fc {
            let w = widths[i];
            let fo = entry_off + offsets[i];
            let val = if natives[i] {
                let t = nat_types[i];
                if t == 1 {
                    if w == 4 {
                        let bits = safe_read_u32(d, fo).unwrap_or(0);
                        fmt_native_float(f32::from_bits(bits) as f64)
                    } else {
                        let bits = safe_read_u64(d, fo).unwrap_or(0);
                        fmt_native_float(f64::from_bits(bits))
                    }
                } else {
                    self.read_uint_width(fo, w).to_string()
                }
            } else {
                let idx = self.read_uint_width(fo, w) as usize;
                if i < pools.len() && idx < pools[i].len() {
                    pools[i][idx].clone()
                } else {
                    String::new()
                }
            };
            values.push(val);
        }

        Arc::new(GeoInfo {
            field_names: Arc::clone(&self.field_names),
            values,
            norm_map: Arc::clone(&self.norm_map),
            numeric_indices: Arc::clone(&self.numeric_indices),
        })
    }

    fn resolve_fields(&self, row_id: u32, fields: &[&str]) -> Option<Arc<GeoInfo>> {
        let (geo_id, asn_id, usage_id) = self.read_ip_row(row_id);
        let mask = *self.group_dim_masks.get(self.group_index)?;
        let entry_id = if mask & 0x02 != 0 {
            asn_id
        } else if mask & 0x04 != 0 {
            usage_id
        } else {
            geo_id
        };
        if entry_id == 0 || entry_id >= self.group_entry_counts[self.group_index] {
            return None;
        }
        let gi = self.group_index;
        let fc = self.group_field_counts[gi];
        let entry_off = self.off_geo_entries as usize + self.group_entry_offsets[gi] as usize
            + entry_id as usize * self.group_strides[gi];
        let d = self.data.as_slice();
        let widths = &self.group_field_widths[gi];
        let offsets = &self.group_field_offsets[gi];
        let natives = &self.group_field_native[gi];
        let nat_types = &self.group_field_native_type[gi];
        let pools = &self.pools[gi];

        let mut names = Vec::new();
        let mut values = Vec::new();
        let mut nmap: HashMap<String, usize> = HashMap::new();
        let mut nidx = Vec::new();
        for f in fields {
            if f.is_empty() {
                continue;
            }
            let key = normalize_key(f);
            let fi = match self.norm_map.get(&key) {
                Some(&i) if i < fc => i,
                _ => continue,
            };
            if nmap.contains_key(&key) {
                continue;
            }
            let w = widths[fi];
            let fo = entry_off + offsets[fi];
            let val = if natives[fi] {
                let t = nat_types[fi];
                if t == 1 {
                    if w == 4 {
                        let bits = safe_read_u32(d, fo).unwrap_or(0);
                        fmt_native_float(f32::from_bits(bits) as f64)
                    } else {
                        let bits = safe_read_u64(d, fo).unwrap_or(0);
                        fmt_native_float(f64::from_bits(bits))
                    }
                } else {
                    self.read_uint_width(fo, w).to_string()
                }
            } else {
                let idx = self.read_uint_width(fo, w) as usize;
                if fi < pools.len() && idx < pools[fi].len() {
                    pools[fi][idx].clone()
                } else {
                    String::new()
                }
            };
            nmap.insert(key, names.len());
            if is_numeric_field_name(&self.field_names[fi]) {
                nidx.push(names.len());
            }
            names.push(self.field_names[fi].clone());
            values.push(val);
        }
        if names.is_empty() {
            return None;
        }
        Some(Arc::new(GeoInfo {
            field_names: Arc::new(names),
            values,
            norm_map: Arc::new(nmap),
            numeric_indices: Arc::new(nidx),
        }))
    }

    fn lookup_cidr_v4(&self, ip: u32) -> Option<String> {
        self.trie_walk_v4(ip).map(|(_, n)| {
            let net = if n == 0 {
                0u32
            } else {
                ip & (0xFFFFFFFFu32 << (32 - n))
            };
            format!(
                "{}.{}.{}.{}/{}",
                (net >> 24) & 0xFF,
                (net >> 16) & 0xFF,
                (net >> 8) & 0xFF,
                net & 0xFF,
                n
            )
        })
    }

    fn lookup_cidr_v6(&self, bytes: &[u8; 16]) -> Option<String> {
        self.trie_walk_v6(bytes).map(|(_rid, n)| {
            use std::fmt::Write;
            let mut g = [0u16; 8];
            for i in 0..8 {
                g[i] = ((bytes[2 * i] as u16) << 8) | bytes[2 * i + 1] as u16;
            }
            // 清零主机位（n..128）
            for bit in (n as usize)..128 {
                g[bit >> 4] &= !(0x8000u16 >> (bit & 15));
            }
            // RFC 5952：最长全零段（并列取最左），长度 ≥2 才压缩
            let (mut best_start, mut best_len) = (0, 0);
            let (mut cur_start, mut cur_len) = (0, 0);
            let mut run = false;
            for (i, &hextet) in g.iter().enumerate() {
                if hextet == 0 {
                    if !run {
                        cur_start = i;
                        cur_len = 1;
                        run = true;
                    } else {
                        cur_len += 1;
                    }
                } else {
                    if cur_len > best_len {
                        best_start = cur_start;
                        best_len = cur_len;
                    }
                    run = false;
                }
            }
            if cur_len > best_len {
                best_start = cur_start;
                best_len = cur_len;
            }

            let mut out = String::with_capacity(40);
            if best_len >= 2 {
                for (i, &hextet) in g.iter().enumerate().take(best_start) {
                    if i > 0 {
                        out.push(':');
                    }
                    let _ = write!(out, "{:x}", hextet);
                }
                out.push_str("::");
                let end = best_start + best_len;
                for (i, &hextet) in g.iter().enumerate().skip(end) {
                    if i > end {
                        out.push(':');
                    }
                    let _ = write!(out, "{:x}", hextet);
                }
            } else {
                for (i, &hextet) in g.iter().enumerate() {
                    if i > 0 {
                        out.push(':');
                    }
                    let _ = write!(out, "{:x}", hextet);
                }
            }
            out.push('/');
            let _ = write!(out, "{}", n);
            out
        })
    }

    fn verify_crc_inner(&self) -> bool {
        let stored = safe_read_u32(self.data.as_slice(), 16).unwrap_or(0);
        stored == self.canonical_crc
    }
}

/// Metadata TLV 段解析结果（FORMAT §8.1）。
#[derive(Default)]
struct MetaInfo {
    /// type 1：version_list（可能是逗号分隔的多档次串）
    version_name: String,
    /// type 2：field_names（`|` 分隔）
    field_names: Vec<String>,
    /// type 3：description
    description: String,
    /// type 4：primary_version（单一权威档次名）
    primary_version: String,
}

fn parse_metadata(d: &[u8], off_meta: u64) -> MetaInfo {
    let mut m = MetaInfo::default();
    if (d[8] & 4) == 0 || off_meta == 0 || off_meta + 4 > d.len() as u64 {
        return m;
    }
    let mut pos = off_meta as usize;
    while pos + 4 <= d.len() {
        let t = d[pos];
        let length = safe_read_u16(d, pos + 2).unwrap_or(0) as usize;
        if t == 0 || length == 0 {
            break;
        }
        // 伪造的 TLV 可以声明一个越过 EOF 的长度；直接停止遍历，
        // 不去解码被截断的尾巴。
        if length > d.len() - (pos + 4) {
            break;
        }
        let val = String::from_utf8_lossy(&d[pos + 4..pos + 4 + length]).into_owned();
        match t {
            1 => m.version_name = val,
            2 => m.field_names = val.split('|').map(|s| s.to_string()).collect(),
            3 => m.description = val,
            4 => m.primary_version = val,
            // 未知 type 按设计跳过（FORMAT §8.1）
            _ => {}
        }
        pos += 4 + length;
    }
    m
}

fn parse_pools(
    d: &[u8],
    group_field_counts: &[usize],
    group_field_native: &[Vec<bool>],
    off_pools: u64,
    off_meta: u64,
    off_row_schema: &u64,
    _pool_idx_size: usize,
) -> Vec<Vec<Vec<String>>> {
    let group_count = group_field_counts.len();
    let mut result = vec![Vec::new(); group_count];
    if off_pools == 0 {
        return result;
    }
    let pool_end = if off_meta > 0 {
        off_meta as usize
    } else {
        d.len()
    };
    let mut pool_cursor = off_pools as usize;
    for g in 0..group_count {
        let fc = group_field_counts[g];
        let mut group_pools = Vec::with_capacity(fc);
        let natives = &group_field_native[g];
        for f in 0..fc {
            if natives.get(f).copied().unwrap_or(false) {
                group_pools.push(Vec::new());
                continue;
            }
            if pool_cursor + 4 > pool_end {
                group_pools.push(Vec::new());
                continue;
            }
            let count = safe_read_u32(d, pool_cursor).unwrap_or(0) as usize;
            pool_cursor += 4;
            if *off_row_schema > 0 {
                pool_cursor += 4;
            }
            if count == 0 || count > 16_000_000 {
                group_pools.push(Vec::new());
                continue;
            }
            let string_data_start = pool_cursor + (count + 1) * 4;
            if string_data_start > pool_end {
                group_pools.push(Vec::new());
                continue;
            }
            let mut offsets = Vec::with_capacity(count + 1);
            let mut ok = true;
            for _ in 0..=count {
                match safe_read_u32(d, pool_cursor) {
                    Some(v) => offsets.push(v as usize),
                    None => {
                        ok = false;
                        break;
                    }
                }
                pool_cursor += 4;
            }
            if !ok || offsets.len() <= count {
                group_pools.push(Vec::new());
                continue;
            }
            // 偏移表是累积结构：offsets[i+1] >= offsets[i]，末项为字符串区总字节数。
            // 单调性必须强制校验 —— 仅判断 b <= d.len() 时，伪造表可让每一项都横跨整个
            // section，count 段 × section 长度会放大成 GB 级 String 拷贝（同类构造实测
            // 达 7.2 GB → OOM）。加上 start >= prev_end && end <= tail 后，各段互不重叠
            // 且落在 [0, tail]，总拷贝量必 <= tail <= avail。
            let avail = pool_end.min(d.len()).saturating_sub(string_data_start);
            let tail = offsets[count];
            if tail > avail {
                group_pools.push(Vec::new());
                pool_cursor = string_data_start;
                continue;
            }
            let mut strings = vec![String::new(); count];
            let mut prev_end = 0usize;
            for s in 0..count {
                let start = offsets[s];
                let end = offsets[s + 1];
                // end < start 会让 `end - start` 发生 usize 下溢（debug panic /
                // release 回绕），进而使 &d[a..b] 区间倒置 panic。
                if start < prev_end || end < start || end > tail {
                    continue;
                }
                prev_end = end;
                if end == start {
                    continue;
                }
                let (Some(a), Some(b)) =
                    (string_data_start.checked_add(start), string_data_start.checked_add(end))
                else {
                    continue;
                };
                if b <= d.len() {
                    strings[s] = String::from_utf8_lossy(&d[a..b]).into_owned();
                }
            }
            pool_cursor = string_data_start.saturating_add(tail);
            group_pools.push(strings);
        }
        result[g] = group_pools;
    }
    result
}

// ---------------------------------------------------------------------------
// IP 解析
// ---------------------------------------------------------------------------

enum ParsedIp {
    V4(u32),
    V6([u8; 16]),
}

static HEX: [u8; 128] = {
    let mut h = [0u8; 128];
    let mut i = 0u8;
    while i < 10 {
        h[48 + i as usize] = i;
        i += 1;
    }
    let mut i = 0u8;
    while i < 6 {
        h[97 + i as usize] = 10 + i;
        h[65 + i as usize] = 10 + i;
        i += 1;
    }
    h
};

fn parse_v4(s: &str) -> Option<u32> {
    let bytes = s.as_bytes();
    let n = bytes.len();
    if n == 0 || bytes[n - 1] == b'.' {
        return None;
    }
    let mut result = 0u32;
    let mut dots = 0u32;
    let mut start = 0;
    for i in 0..=n {
        let c = if i < n { bytes[i] } else { b'.' };
        if c == b'.' {
            let seg_len = i - start;
            if seg_len == 0 || seg_len > 3 {
                return None;
            }
            if seg_len > 1 && bytes[start] == b'0' {
                return None;
            }
            let mut val = 0u32;
            for &d in &bytes[start..i] {
                if !d.is_ascii_digit() {
                    return None;
                }
                val = val * 10 + (d - b'0') as u32;
            }
            if val > 255 {
                return None;
            }
            result = (result << 8) | val;
            dots += 1;
            start = i + 1;
        }
    }
    if dots != 4 {
        return None;
    }
    Some(result)
}

fn parse_ip(s: &str) -> Option<ParsedIp> {
    let n = s.len();
    for &b in s.as_bytes() {
        if b == b' ' || b == b'\t' || b == b'\n' || b == b'\r' || b == 0x0B || b == 0x0C {
            return None;
        }
    }
    if n == 0 || n > 45 {
        return None;
    }
    if !s.contains(':') {
        return parse_v4(s).map(ParsedIp::V4);
    }
    if s.contains('%') {
        return None;
    }
    let dc = s.find("::");
    if let Some(dc) = dc {
        if s[dc + 2..].find("::").is_some() {
            return None;
        }
    }
    let (lft, rgt) = match dc {
        Some(dc) => (&s[..dc], &s[dc + 2..]),
        None => (s, ""),
    };
    let mut lg: Vec<&str> = if lft.is_empty() { Vec::new() } else { lft.split(':').collect() };
    let mut rg: Vec<&str> = if rgt.is_empty() { Vec::new() } else { rgt.split(':').collect() };
    if lg.len() == 1 && lg[0].is_empty() {
        lg.clear();
    }
    if rg.len() == 1 && rg[0].is_empty() {
        rg.clear();
    }
    for g in &lg {
        if g.is_empty() {
            return None;
        }
    }
    for g in &rg {
        if g.is_empty() {
            return None;
        }
    }
    let mut allg: Vec<&str> = Vec::with_capacity(lg.len() + rg.len());
    allg.extend_from_slice(&lg);
    allg.extend_from_slice(&rg);
    let mut has_v4 = false;
    let mut v4_int = 0u32;
    if let Some(last) = allg.last() {
        if last.contains('.') {
            v4_int = parse_v4(last)?;
            has_v4 = true;
            allg.pop();
        }
    }
    let ng = allg.len();
    let v4_slots: usize = if has_v4 { 2 } else { 0 };
    if dc.is_some() {
        if ng + v4_slots > 7 {
            return None;
        }
    } else if ng + v4_slots != 8 {
        return None;
    }
    for g in &allg {
        let gl = g.len();
        if gl == 0 || gl > 4 {
            return None;
        }
        for &cc in g.as_bytes() {
            if cc >= 128 || (HEX[cc as usize] == 0 && cc != b'0') {
                return None;
            }
        }
    }
    let zeros = 8 - ng - v4_slots;
    let mut buf = [0u8; 16];
    let mut off = 0usize;
    for g in &lg {
        let mut v = 0u16;
        for &c in g.as_bytes() {
            v = (v << 4) | HEX[c as usize] as u16;
        }
        buf[off] = (v >> 8) as u8;
        buf[off + 1] = v as u8;
        off += 2;
    }
    off += zeros * 2;
    for g in &rg {
        let mut v = 0u16;
        for &c in g.as_bytes() {
            v = (v << 4) | HEX[c as usize] as u16;
        }
        buf[off] = (v >> 8) as u8;
        buf[off + 1] = v as u8;
        off += 2;
    }
    if has_v4 {
        buf[12] = (v4_int >> 24) as u8;
        buf[13] = (v4_int >> 16) as u8;
        buf[14] = (v4_int >> 8) as u8;
        buf[15] = v4_int as u8;
    }
    if is_ipv4_mapped_v6(&buf) {
        return Some(ParsedIp::V4(u32::from_be_bytes([buf[12], buf[13], buf[14], buf[15]])));
    }
    Some(ParsedIp::V6(buf))
}

// ---------------------------------------------------------------------------
// QzdbReader（公开 API，持有快照、无锁热更新）
// ---------------------------------------------------------------------------

/// QZDB 读取器。内部为不可变快照 + 原子替换：查询无锁只读，reload 线程安全。
///
/// 生命周期由 `Drop` trait 管理：超出作用域时自动释放持有的数据，
/// 无需手动调用 `close()`。如需显式释放可调用 `close()`。
pub struct QzdbReader {
    snap: ArcSwap<SnapshotInner>,
}

impl Drop for QzdbReader {
    fn drop(&mut self) {
        // 确保在 drop 前原子切换到占位快照，释放持有的数据。
        self.close();
    }
}

impl std::fmt::Debug for QzdbReader {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("QzdbReader").finish()
    }
}

impl QzdbReader {
    /// 从文件路径加载（默认 CRC 校验开启）。
    pub fn from_file(path: &str) -> Result<QzdbReader, QzdbError> {
        let bytes = fs::read(path)?;
        Self::from_bytes(&bytes, 0, true)
    }

    /// 从内存字节加载。
    pub fn from_bytes(bytes: &[u8], group_index: usize, verify_crc: bool) -> Result<QzdbReader, QzdbError> {
        let data = Arc::new(bytes.to_vec());
        let inner = SnapshotInner::from_bytes(data, group_index, verify_crc)?;
        Ok(QzdbReader {
            snap: ArcSwap::from_pointee(inner),
        })
    }

    /// 构造 Builder（API_CONTRACT §2）。
    pub fn builder(path: &str) -> Builder {
        Builder::new(path)
    }

    fn inner(&self) -> Arc<SnapshotInner> {
        self.snap.load_full()
    }

    // ---- 单条查询 ----

    pub fn find(&self, ip_str: &str) -> Option<GeoInfo> {
        let parsed = parse_ip(ip_str)?;
        self.find_parsed(&parsed)
    }

    fn find_parsed(&self, parsed: &ParsedIp) -> Option<GeoInfo> {
        let snap = self.inner();
        match parsed {
            ParsedIp::V4(v4) => self.find_uint_inner(&snap, *v4),
            ParsedIp::V6(b) => self.find_v6_bytes_inner(&snap, b),
        }
    }

    fn find_uint_inner(&self, snap: &SnapshotInner, ip: u32) -> Option<GeoInfo> {
        if !snap.has_v4 {
            return None;
        }
        match snap.trie_walk_v4(ip) {
            Some((rid, _)) if rid != 0 => snap.resolve_row_id(rid).map(|a| (*a).clone()),
            _ => None,
        }
    }

    pub fn find_uint(&self, ip: u32) -> Option<GeoInfo> {
        let snap = self.inner();
        self.find_uint_inner(&snap, ip)
    }

    fn find_v6_bytes_inner(&self, snap: &SnapshotInner, bytes: &[u8; 16]) -> Option<GeoInfo> {
        if !snap.has_v6 {
            return None;
        }
        match snap.trie_walk_v6(bytes) {
            Some((rid, _)) if rid != 0 => snap.resolve_row_id(rid).map(|a| (*a).clone()),
            _ => None,
        }
    }

    pub fn find_v6(&self, ip: u128) -> Option<GeoInfo> {
        let snap = self.inner();
        if !snap.has_v6 {
            return None;
        }
        let b = ip.to_be_bytes();
        self.find_v6_bytes_inner(&snap, &b)
    }

    /// 从 4/16 字节查询（16 字节含 IPv4-Mapped 降级）。长度非 4/16 返回 None。
    pub fn find_bytes(&self, ip_bytes: &[u8]) -> Option<GeoInfo> {
        let snap = self.inner();
        match ip_bytes.len() {
            16 => {
                let mut b = [0u8; 16];
                b.copy_from_slice(ip_bytes);
                if is_ipv4_mapped_v6(&b) {
                    let v4 = u32::from_be_bytes([b[12], b[13], b[14], b[15]]);
                    self.find_uint_inner(&snap, v4)
                } else {
                    self.find_v6_bytes_inner(&snap, &b)
                }
            }
            4 => {
                let v4 = u32::from_be_bytes([ip_bytes[0], ip_bytes[1], ip_bytes[2], ip_bytes[3]]);
                self.find_uint_inner(&snap, v4)
            }
            _ => None,
        }
    }

    /// 字段投影查询；fields 为空等价于 find。
    pub fn find_fields(&self, ip_str: &str, fields: &[&str]) -> Option<GeoInfo> {
        if fields.is_empty() {
            return self.find(ip_str);
        }
        let parsed = parse_ip(ip_str)?;
        let snap = self.inner();
        let row_id = match &parsed {
            ParsedIp::V4(v4) => snap.trie_walk_v4(*v4).map(|(r, _)| r).unwrap_or(0),
            ParsedIp::V6(b) => snap.trie_walk_v6(b).map(|(r, _)| r).unwrap_or(0),
        };
        if row_id == 0 {
            return None;
        }
        snap.resolve_fields(row_id, fields).map(|a| (*a).clone())
    }

    /// 返回 `to_pipe()` 字符串；未命中/非法返回 ""。
    pub fn find_str(&self, ip_str: &str) -> String {
        self.find(ip_str).map(|g| g.to_pipe()).unwrap_or_default()
    }

    // ---- 低级行号 ----

    pub fn lookup_row_id(&self, ip_str: &str) -> u32 {
        let parsed = match parse_ip(ip_str) {
            Some(p) => p,
            None => return 0,
        };
        let snap = self.inner();
        match parsed {
            ParsedIp::V4(v4) => snap.trie_walk_v4(v4).map(|(r, _)| r).unwrap_or(0),
            ParsedIp::V6(b) => snap.trie_walk_v6(&b).map(|(r, _)| r).unwrap_or(0),
        }
    }

    pub fn lookup_row_id_uint(&self, ip: u32) -> u32 {
        let snap = self.inner();
        if !snap.has_v4 {
            return 0;
        }
        snap.trie_walk_v4(ip).map(|(r, _)| r).unwrap_or(0)
    }

    pub fn lookup_row_id_v6(&self, ip: u128) -> u32 {
        let snap = self.inner();
        if !snap.has_v6 {
            return 0;
        }
        let b = ip.to_be_bytes();
        snap.trie_walk_v6(&b).map(|(r, _)| r).unwrap_or(0)
    }

    pub fn lookup_row_id_bytes(&self, ip_bytes: &[u8]) -> u32 {
        let snap = self.inner();
        match ip_bytes.len() {
            16 => {
                let mut b = [0u8; 16];
                b.copy_from_slice(ip_bytes);
                if is_ipv4_mapped_v6(&b) {
                    let v4 = u32::from_be_bytes([b[12], b[13], b[14], b[15]]);
                    if !snap.has_v4 {
                        return 0;
                    }
                    snap.trie_walk_v4(v4).map(|(r, _)| r).unwrap_or(0)
                } else {
                    if !snap.has_v6 {
                        return 0;
                    }
                    snap.trie_walk_v6(&b).map(|(r, _)| r).unwrap_or(0)
                }
            }
            4 => {
                let v4 = u32::from_be_bytes([ip_bytes[0], ip_bytes[1], ip_bytes[2], ip_bytes[3]]);
                if !snap.has_v4 {
                    return 0;
                }
                snap.trie_walk_v4(v4).map(|(r, _)| r).unwrap_or(0)
            }
            _ => 0,
        }
    }

    /// 返回 (geo_id, asn_id, usage_id)；越界返回 None。
    pub fn lookup_ids(&self, row_id: u32) -> Option<RowIds> {
        let snap = self.inner();
        if row_id == 0 || row_id >= snap.row_count as u32 {
            return None;
        }
        let (g, a, u) = snap.read_ip_row(row_id);
        Some(RowIds { geo_id: g, asn_id: a, usage_id: u })
    }

    // ---- CIDR 反查 ----

    pub fn lookup_cidr(&self, ip_str: &str) -> Option<String> {
        let parsed = parse_ip(ip_str)?;
        let snap = self.inner();
        match parsed {
            ParsedIp::V4(v4) => snap.lookup_cidr_v4(v4),
            ParsedIp::V6(b) => snap.lookup_cidr_v6(&b),
        }
    }

    pub fn lookup_cidr_uint(&self, ip: u32) -> Option<String> {
        let snap = self.inner();
        if !snap.has_v4 {
            return None;
        }
        snap.lookup_cidr_v4(ip)
    }

    pub fn lookup_cidr_bytes(&self, ip_bytes: &[u8]) -> Option<String> {
        let snap = self.inner();
        match ip_bytes.len() {
            16 => {
                let mut b = [0u8; 16];
                b.copy_from_slice(ip_bytes);
                if is_ipv4_mapped_v6(&b) {
                    let v4 = u32::from_be_bytes([b[12], b[13], b[14], b[15]]);
                    if !snap.has_v4 {
                        return None;
                    }
                    snap.lookup_cidr_v4(v4)
                } else {
                    if !snap.has_v6 {
                        return None;
                    }
                    snap.lookup_cidr_v6(&b)
                }
            }
            4 => {
                let v4 = u32::from_be_bytes([ip_bytes[0], ip_bytes[1], ip_bytes[2], ip_bytes[3]]);
                if !snap.has_v4 {
                    return None;
                }
                snap.lookup_cidr_v4(v4)
            }
            _ => None,
        }
    }

    // ---- 批量 / 流式 ----

    pub fn find_batch(&self, ips: &[&str]) -> Vec<BatchResult> {
        ips.iter().map(|ip| self.batch_one(ip)).collect()
    }

    pub fn find_batch_fields(&self, ips: &[&str], fields: &[&str]) -> Vec<BatchResult> {
        if fields.is_empty() {
            return self.find_batch(ips);
        }
        ips.iter()
            .map(|ip| match self.find_fields(ip, fields) {
                Some(g) => BatchResult { ip: ip.to_string(), geo_info: Some(g), error: None },
                None => self.batch_one(ip),
            })
            .collect()
    }

    /// 流式查询：惰性迭代，内存恒定（不累积结果）。
    pub fn find_stream<'a>(&'a self, ips: &'a [&'a str]) -> impl Iterator<Item = BatchResult> + 'a {
        ips.iter().map(move |ip| self.batch_one(ip))
    }

    fn batch_one(&self, ip: &str) -> BatchResult {
        let ip_str = ip.to_string();
        match self.find(ip) {
            Some(g) => BatchResult { ip: ip_str, geo_info: Some(g), error: None },
            None => BatchResult { ip: ip_str, geo_info: None, error: None },
        }
    }

    // ---- 元信息自省 ----

    pub fn get_version(&self) -> String {
        self.inner().version.clone()
    }
    pub fn get_data_month(&self) -> String {
        self.inner().data_month.clone()
    }
    pub fn get_edition(&self) -> String {
        self.inner().edition.clone()
    }
    /// Header.VersionMask 原值（offset 6，u16 LE）。one-hot：
    /// bit0=std bit1=asn bit2=pro bit3=max bit4=ult。
    pub fn get_version_mask(&self) -> u16 {
        self.inner().version_mask
    }
    /// `get_edition()` 的判定依据：version_mask / metadata / inferred / unknown。
    pub fn get_edition_source(&self) -> String {
        self.inner().edition_source.to_string()
    }
    /// `get_field_names()` 的来源：metadata / edition / synthetic。
    pub fn get_field_names_source(&self) -> String {
        self.inner().field_names_source.to_string()
    }
    /// scope 恒返回 ""（API_CONTRACT §5）。
    pub fn get_scope(&self) -> String {
        String::new()
    }
    pub fn get_build_time(&self) -> String {
        self.inner().build_time.clone()
    }
    pub fn get_description(&self) -> String {
        self.inner().description.clone()
    }
    /// CRC32 十六进制 8 位小写。
    pub fn get_file_hash(&self) -> String {
        format!("{:08x}", self.inner().canonical_crc)
    }
    pub fn get_field_names(&self) -> Vec<String> {
        self.inner().field_names.as_slice().to_vec()
    }
    pub fn has_field(&self, name: &str) -> bool {
        self.inner().norm_map.contains_key(&normalize_key(name))
    }
    pub fn verify_crc(&self) -> bool {
        self.inner().verify_crc_inner()
    }
    pub fn get_group_count(&self) -> usize {
        self.inner().group_field_counts.len()
    }
    pub fn get_pool_count(&self) -> usize {
        self.inner().pool_count
    }

    // ---- 热更新（原子替换，强制 CRC；失败旧快照继续服务） ----

    pub fn reload(&self, path: &str) -> Result<(), QzdbError> {
        let bytes = fs::read(path)?;
        let group_index = self.inner().group_index;
        let new_inner = SnapshotInner::from_bytes(Arc::new(bytes), group_index, true)?;
        self.snap.store(Arc::new(new_inner));
        Ok(())
    }

    pub fn reload_bytes(&self, bytes: &[u8]) -> Result<(), QzdbError> {
        let group_index = self.inner().group_index;
        let new_inner = SnapshotInner::from_bytes(Arc::new(bytes.to_vec()), group_index, true)?;
        self.snap.store(Arc::new(new_inner));
        Ok(())
    }

    /// 关闭：丢弃快照引用，替换为占位空快照。之后再查询会安全返回 None（不 UAF）。
    pub fn close(&self) {
        self.snap.store(empty_snapshot_arc());
    }
}

/// 占位空快照（close 后使用），所有查询安全失败。
fn empty_snapshot() -> SnapshotInner {
    // 构造一个最小合法（但无数据）的快照；查询在 has_v4/has_v6=false 时直接返回 None。
    let data = Arc::new(vec![0u8; 192]);
    // 该快照仅用于 close 后占位；CRC 等不校验。
    SnapshotInner {
        data,
        group_index: 0,
        _flags: 0,
        has_v4: false,
        has_v6: false,
        v4_node_24: false,
        v6_node_24: false,
        v6_jump_bits: 16,
        pool_count: 0,
        _pool_idx_size: 2,
        _geo_count: 0,
        row_count: 0,
        v4_node_count: 0,
        v6_node_count: 0,
        ip_row_size: 1,
        row_geo_width: 3,
        row_asn_width: 3,
        row_usage_width: 0,
        off_v4_jump: 0,
        off_v4_nodes: 0,
        off_v6_jump: 0,
        off_v6_nodes: 0,
        off_ip_row: 0,
        off_geo_entries: 0,
        _off_pools: 0,
        _off_meta: 0,
        off_row_schema: 0,
        _off_group_schema: 0,
        group_field_counts: vec![0],
        group_entry_counts: vec![0],
        group_dim_masks: vec![0],
        group_entry_offsets: vec![0],
        group_strides: vec![0],
        group_field_widths: vec![vec![]],
        group_field_offsets: vec![vec![]],
        group_field_native: vec![vec![]],
        group_field_native_type: vec![vec![]],
        pools: vec![vec![]],
        field_names: Arc::new(Vec::new()),
        norm_map: Arc::new(HashMap::new()),
        numeric_indices: Arc::new(Vec::new()),
        version: String::new(),
        description: String::new(),
        data_month: String::new(),
        build_time: String::new(),
        edition: String::new(),
        version_mask: 0,
        edition_source: EDITION_SOURCE_UNKNOWN,
        field_names_source: FIELD_NAMES_SOURCE_SYNTHETIC,
        canonical_crc: 0,
        geo_cache: (0..GEO_CACHE_SIZE)
            .map(|_| std::sync::Mutex::new(CacheSlot { key: u32::MAX, val: None }))
            .collect(),
    }
}

// 用 OnceLock 持有空快照（close 后复用同一份）。
static EMPTY_SNAPSHOT_HOLDER: OnceLock<Arc<SnapshotInner>> = OnceLock::new();
fn empty_snapshot_arc() -> Arc<SnapshotInner> {
    EMPTY_SNAPSHOT_HOLDER.get_or_init(|| Arc::new(empty_snapshot())).clone()
}

// ---------------------------------------------------------------------------
// Builder
// ---------------------------------------------------------------------------

/// 加载构造器（API_CONTRACT §2）。
pub struct Builder {
    path: Option<String>,
    bytes: Option<Vec<u8>>,
    group_index: usize,
    verify_crc: bool,
}

impl Builder {
    pub fn new(path: &str) -> Self {
        Builder {
            path: Some(path.to_string()),
            bytes: None,
            group_index: 0,
            verify_crc: true,
        }
    }

    pub fn from_bytes(bytes: &[u8]) -> Self {
        Builder {
            path: None,
            bytes: Some(bytes.to_vec()),
            group_index: 0,
            verify_crc: true,
        }
    }

    pub fn group_index(mut self, g: usize) -> Self {
        self.group_index = g;
        self
    }

    pub fn verify_crc(mut self, b: bool) -> Self {
        self.verify_crc = b;
        self
    }

    pub fn build(self) -> Result<QzdbReader, QzdbError> {
        let data = match (self.path, self.bytes) {
            (Some(p), _) => Arc::new(fs::read(p)?),
            (None, Some(b)) => Arc::new(b),
            (None, None) => {
                return Err(err(ErrorCode::InvalidParam, "no path or bytes provided"));
            }
        };
        let inner = SnapshotInner::from_bytes(data, self.group_index, self.verify_crc)?;
        Ok(QzdbReader {
            snap: ArcSwap::from_pointee(inner),
        })
    }
}

// ---------------------------------------------------------------------------
// QzdbRegistry（多库注册表）
// ---------------------------------------------------------------------------

/// 多数据库注册表：按名称持有多个 [`QzdbReader`]，查询时按优先级返回首个命中。
pub struct QzdbRegistry {
    readers: HashMap<String, QzdbReader>,
    order: Vec<String>,
}

impl QzdbRegistry {
    pub fn new() -> Self {
        QzdbRegistry {
            readers: HashMap::new(),
            order: Vec::new(),
        }
    }

    pub fn register(&mut self, name: &str, reader: QzdbReader) {
        if !self.readers.contains_key(name) {
            self.order.push(name.to_string());
        }
        self.readers.insert(name.to_string(), reader);
    }

    pub fn get(&self, name: &str) -> Option<&QzdbReader> {
        self.readers.get(name)
    }

    /// 按注册顺序返回首个命中（非 None）；全未命中返回 None。
    pub fn find(&self, ip: &str) -> Option<GeoInfo> {
        for name in &self.order {
            if let Some(r) = self.readers.get(name) {
                if let Some(g) = r.find(ip) {
                    return Some(g);
                }
            }
        }
        None
    }

    pub fn find_str(&self, ip: &str) -> String {
        self.find(ip).map(|g| g.to_pipe()).unwrap_or_default()
    }
}

impl Default for QzdbRegistry {
    fn default() -> Self {
        Self::new()
    }
}

// ---------------------------------------------------------------------------
// ChainedReader（多库链式合并）
// ---------------------------------------------------------------------------

/// 多库链式合并模式（API_CONTRACT §9.3）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChainMode {
    /// 依次查询，返回首个命中的结果。
    Fallback,
    /// 合并所有命中；空缺字段由后库补充（先库优先）。
    Merge,
    /// 合并所有命中；后库的非空值覆盖先库。
    MergeOverride,
}

/// 多库链式合并：按优先级串联多个 [`QzdbReader`]。
pub struct ChainedReader {
    readers: Vec<QzdbReader>,
    mode: ChainMode,
}

impl ChainedReader {
    pub fn new() -> Self {
        ChainedReader {
            readers: Vec::new(),
            mode: ChainMode::Fallback,
        }
    }

    /// 设置合并模式（默认 Fallback）。
    pub fn mode(mut self, mode: ChainMode) -> Self {
        self.mode = mode;
        self
    }

    pub fn push(&mut self, reader: QzdbReader) {
        self.readers.push(reader);
    }

    pub fn find(&self, ip: &str) -> Option<GeoInfo> {
        match self.mode {
            ChainMode::Fallback => {
                for r in &self.readers {
                    if let Some(g) = r.find(ip) {
                        return Some(g);
                    }
                }
                None
            }
            ChainMode::Merge | ChainMode::MergeOverride => {
                let mut merged: Option<GeoInfo> = None;
                for r in &self.readers {
                    if let Some(g) = r.find(ip) {
                        merged = Some(match &merged {
                            None => g,
                            Some(base) => merge_geo(base, &g, self.mode),
                        });
                    }
                }
                merged
            }
        }
    }

    pub fn find_str(&self, ip: &str) -> String {
        self.find(ip).map(|g| g.to_pipe()).unwrap_or_default()
    }
}

impl Default for ChainedReader {
    fn default() -> Self {
        Self::new()
    }
}

/// 合并两个 GeoInfo：Merge 模式下空缺补充，MergeOverride 模式下非空覆盖。
fn merge_geo(base: &GeoInfo, overlay: &GeoInfo, mode: ChainMode) -> GeoInfo {
    let field_names: Arc<Vec<String>> = if base.field_names.len() >= overlay.field_names.len() {
        Arc::clone(&base.field_names)
    } else {
        Arc::clone(&overlay.field_names)
    };
    let fc = field_names.len();
    let mut values = Vec::with_capacity(fc);
    let mut nmap: HashMap<String, usize> = HashMap::with_capacity(fc);
    let mut nidx = Vec::new();
    for (i, name) in field_names.iter().enumerate() {
        let key = normalize_key(name);
        nmap.insert(key, i);
        if is_numeric_field_name(name) {
            nidx.push(i);
        }
        let base_val = base.norm_map.get(&normalize_key(name)).and_then(|&i| base.values.get(i));
        let ov_val = overlay
            .norm_map
            .get(&normalize_key(name))
            .and_then(|&i| overlay.values.get(i));
        let merged_val = match mode {
            ChainMode::Merge => {
                if let Some(v) = base_val {
                    v.clone()
                } else if let Some(v) = ov_val {
                    v.clone()
                } else {
                    String::new()
                }
            }
            ChainMode::MergeOverride => {
                if let Some(v) = ov_val {
                    if !v.is_empty() {
                        v.clone()
                    } else if let Some(v) = base_val {
                        v.clone()
                    } else {
                        String::new()
                    }
                } else if let Some(v) = base_val {
                    v.clone()
                } else {
                    String::new()
                }
            }
            ChainMode::Fallback => unreachable!(),
        };
        values.push(merged_val);
    }
    GeoInfo {
        field_names,
        values,
        norm_map: Arc::new(nmap),
        numeric_indices: Arc::new(nidx),
    }
}

#[cfg(test)]
mod tests {
    use super::fmt_native_float;

    #[test]
    fn t_fmt_native_float_whole_number() {
        assert_eq!(fmt_native_float(116.0), "116");
        assert_eq!(fmt_native_float(0.0), "0");
        assert_eq!(fmt_native_float(-39.0), "-39");
    }

    #[test]
    fn t_fmt_native_float_with_decimals() {
        assert_eq!(fmt_native_float(116.4), "116.400000");
        assert_eq!(fmt_native_float(43.864010), "43.864010");
        assert_eq!(fmt_native_float(0.001), "0.001000");
    }

    #[test]
    fn t_fmt_native_float_nan_inf() {
        assert_eq!(fmt_native_float(f64::NAN), "");
        assert_eq!(fmt_native_float(f64::INFINITY), "");
        assert_eq!(fmt_native_float(f64::NEG_INFINITY), "");
    }
}
