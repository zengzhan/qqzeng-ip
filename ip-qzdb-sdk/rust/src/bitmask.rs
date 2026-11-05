//! 面向 AI Agent、边缘 WAF 与高并发风控网关的零分配位掩码嗅探引擎。
//!
//! 设计目标：
//! - **零堆分配（0 Heap Allocation）**：查询期不构造 `GeoInfo`、不克隆字符串、不触发浮点格式化。
//! - **O(1) 预计算查表**：每个快照通过 `OnceLock<Box<[u32]>>` 惰性预计算一次 `entry_id -> IpAttrFlags`
//!   紧凑位掩码表（单库仅数十至数百 KB），后续每次查询仅需 `Trie 寻径 + 1 次数组下标读取`。
//! - **100% 语义自洽**：所有标志位严格由底层快照的 `country_code`、`country`、`province` 与
//!   `usage_type`（保留 21 类历史已知 `KnownUsage` 枚举作为兼容超集，虽数据库实盘中 IXP 等已收敛归入 Backbone）确定性推导，与 `GeoInfo` 完整解码结果零偏差。

use crate::{KnownUsage, ParsedIp, QzdbReader, Snapshot, SnapshotInner, ToIp, UsageType};

/// 32 位零分配 IP 属性特征位掩码。
///
/// 位布局说明：
/// - `Bit 0` (`HIT`)：库内命中有效记录（`row_id > 0` 且 `entry_id > 0`）。
/// - `Bit 1` (`DOMESTIC_CN`)：中国大陆境内（`country_code == "CN"` 且省份非港澳台）。
/// - `Bit 2` (`GREATER_CN`)：大中华区（`country_code` 为 `CN`/`HK`/`MO`/`TW` 或 `country == "中国"`）。
/// - `Bit 3` (`PRIVATE_RESERVED`)：私有保留/内网回环地址（`country == "局域网"` 或 `UsageType == Reserved`）。
/// - `Bit 4` (`DATACENTER`)：数据中心/云厂商/CDN（`UsageType` 为 `DataCenter` | `Cloud` | `CDN`）。
/// - `Bit 5` (`AI_CRAWLER`)：AI 训练或搜索爬虫（`UsageType == AICrawler`）。
/// - `Bit 6` (`SPIDER`)：通用搜索引擎或网络爬虫（`UsageType == Spider`）。
/// - `Bit 7` (`MOBILE`)：蜂窝移动网络（`UsageType == Mobile`）。
/// - `Bit 8` (`BROADBAND`)：家庭或企业宽带接入（`UsageType == Broadband`）。
/// - `Bit 9` (`VPN_PROXY`)：VPN/代理/隐私出口（`UsageType == VPN`）。
/// - `Bit 10` (`BACKBONE_IXP`)：骨干网或互联网交换中心（`UsageType` 为 `Backbone` | `IXP`）。
/// - `Bit 11` (`SATELLITE`)：卫星互联网接入（`UsageType == Satellite`）。
/// - `Bit 12` (`DNS_ANYCAST`)：公共 DNS 基础设施（`UsageType == DNS`）。
/// - `Bit 13` (`EDU_GOV_FIN`)：高校科研/政务/金融专网（`UsageType` 为 `Education` | `Government` | `Finance`）。
/// - `Bits 16..=20`：编码 21 类官方 [`KnownUsage`] 的枚举序号（`1..=21`，`0` 表示未知或无此字段）。
#[repr(transparent)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct IpAttrFlags(u32);

impl IpAttrFlags {
    pub const NONE: u32 = 0;
    pub const HIT: u32 = 1 << 0;
    pub const DOMESTIC_CN: u32 = 1 << 1;
    pub const GREATER_CN: u32 = 1 << 2;
    pub const PRIVATE_RESERVED: u32 = 1 << 3;
    pub const DATACENTER: u32 = 1 << 4;
    pub const AI_CRAWLER: u32 = 1 << 5;
    pub const SPIDER: u32 = 1 << 6;
    pub const MOBILE: u32 = 1 << 7;
    pub const BROADBAND: u32 = 1 << 8;
    pub const VPN_PROXY: u32 = 1 << 9;
    pub const BACKBONE_IXP: u32 = 1 << 10;
    pub const SATELLITE: u32 = 1 << 11;
    pub const DNS_ANYCAST: u32 = 1 << 12;
    pub const EDU_GOV_FIN: u32 = 1 << 13;

    const USAGE_SHIFT: u32 = 16;
    const USAGE_MASK: u32 = 0x1F;

    #[inline(always)]
    pub const fn from_bits(bits: u32) -> Self {
        Self(bits)
    }

    #[inline(always)]
    pub const fn bits(self) -> u32 {
        self.0
    }

    /// 是否在库内命中有效记录。
    #[inline(always)]
    pub const fn is_hit(self) -> bool {
        (self.0 & Self::HIT) != 0
    }

    /// 是否为中国大陆境内 IP（不含港澳台）。
    #[inline(always)]
    pub const fn is_domestic(self) -> bool {
        (self.0 & Self::DOMESTIC_CN) != 0
    }

    /// 是否属于大中华区（含中国大陆及港澳台）。
    #[inline(always)]
    pub const fn is_greater_china(self) -> bool {
        (self.0 & Self::GREATER_CN) != 0
    }

    /// 是否为局域网、环回或保留地址。
    #[inline(always)]
    pub const fn is_private_or_reserved(self) -> bool {
        (self.0 & Self::PRIVATE_RESERVED) != 0
    }

    /// 是否为数据中心、公有云或 CDN 节点。
    #[inline(always)]
    pub const fn is_datacenter(self) -> bool {
        (self.0 & Self::DATACENTER) != 0
    }

    /// 是否为爬虫流量（含 `AICrawler` 与通用 `Spider`）。
    #[inline(always)]
    pub const fn is_crawler(self) -> bool {
        (self.0 & (Self::AI_CRAWLER | Self::SPIDER)) != 0
    }

    /// 是否为 AI 训练/搜索爬虫（`AICrawler`）。
    #[inline(always)]
    pub const fn is_ai_crawler(self) -> bool {
        (self.0 & Self::AI_CRAWLER) != 0
    }

    /// 是否为蜂窝移动网络（`Mobile`）。
    #[inline(always)]
    pub const fn is_mobile(self) -> bool {
        (self.0 & Self::MOBILE) != 0
    }

    /// 是否为家庭/企业宽带（`Broadband`）。
    #[inline(always)]
    pub const fn is_broadband(self) -> bool {
        (self.0 & Self::BROADBAND) != 0
    }

    /// 是否为 VPN 或代理出口（`VPN`）。
    #[inline(always)]
    pub const fn is_vpn(self) -> bool {
        (self.0 & Self::VPN_PROXY) != 0
    }

    /// 是否为公共 DNS 节点（`DNS`）。
    #[inline(always)]
    pub const fn is_dns(self) -> bool {
        (self.0 & Self::DNS_ANYCAST) != 0
    }

    /// O(1) 还原已知的 [`KnownUsage`] 枚举（若库含 `usage_type` 且属于 21 类已知场景）。
    #[inline]
    pub const fn known_usage(self) -> Option<KnownUsage> {
        let code = (self.0 >> Self::USAGE_SHIFT) & Self::USAGE_MASK;
        match code {
            1 => Some(KnownUsage::AiCrawler),
            2 => Some(KnownUsage::Backbone),
            3 => Some(KnownUsage::Broadband),
            4 => Some(KnownUsage::Business),
            5 => Some(KnownUsage::Cdn),
            6 => Some(KnownUsage::Cloud),
            7 => Some(KnownUsage::Dns),
            8 => Some(KnownUsage::DataCenter),
            9 => Some(KnownUsage::Education),
            10 => Some(KnownUsage::Finance),
            11 => Some(KnownUsage::Government),
            12 => Some(KnownUsage::Isp),
            13 => Some(KnownUsage::Ixp),
            14 => Some(KnownUsage::Iot),
            15 => Some(KnownUsage::Mobile),
            16 => Some(KnownUsage::Reserved),
            17 => Some(KnownUsage::Satellite),
            18 => Some(KnownUsage::Spider),
            19 => Some(KnownUsage::Streaming),
            20 => Some(KnownUsage::Unknown),
            21 => Some(KnownUsage::Vpn),
            _ => None,
        }
    }
}

#[inline]
fn usage_to_code_and_bits(k: KnownUsage) -> (u32, u32) {
    match k {
        KnownUsage::AiCrawler => (1, IpAttrFlags::AI_CRAWLER),
        KnownUsage::Backbone => (2, IpAttrFlags::BACKBONE_IXP),
        KnownUsage::Broadband => (3, IpAttrFlags::BROADBAND),
        KnownUsage::Business => (4, 0),
        KnownUsage::Cdn => (5, IpAttrFlags::DATACENTER),
        KnownUsage::Cloud => (6, IpAttrFlags::DATACENTER),
        KnownUsage::Dns => (7, IpAttrFlags::DNS_ANYCAST),
        KnownUsage::DataCenter => (8, IpAttrFlags::DATACENTER),
        KnownUsage::Education => (9, IpAttrFlags::EDU_GOV_FIN),
        KnownUsage::Finance => (10, IpAttrFlags::EDU_GOV_FIN),
        KnownUsage::Government => (11, IpAttrFlags::EDU_GOV_FIN),
        KnownUsage::Isp => (12, 0),
        KnownUsage::Ixp => (13, IpAttrFlags::BACKBONE_IXP),
        KnownUsage::Iot => (14, 0),
        KnownUsage::Mobile => (15, IpAttrFlags::MOBILE),
        KnownUsage::Reserved => (16, IpAttrFlags::PRIVATE_RESERVED),
        KnownUsage::Satellite => (17, IpAttrFlags::SATELLITE),
        KnownUsage::Spider => (18, IpAttrFlags::SPIDER),
        KnownUsage::Streaming => (19, 0),
        KnownUsage::Unknown => (20, 0),
        KnownUsage::Vpn => (21, IpAttrFlags::VPN_PROXY),
    }
}

#[inline]
fn is_hk_mo_tw_province(prov: &str) -> bool {
    matches!(
        prov,
        "香港"
            | "澳门"
            | "台湾"
            | "香港特别行政区"
            | "澳门特别行政区"
            | "台湾省"
            | "Hong Kong"
            | "Macao"
            | "Macau"
            | "Taiwan"
    )
}

impl SnapshotInner {
    /// 计算单条 `entry_id` 的位掩码（用于初始化预计算表）。
    pub(crate) fn compute_entry_attr_flags(&self, entry_id: u32) -> IpAttrFlags {
        let Some(view) = self.resolve_geo_ref(entry_id, None) else {
            return IpAttrFlags::default();
        };
        let mut bits = IpAttrFlags::HIT;

        let cc = view.country_code();
        let country = view.country();
        let province = view.province();

        if cc.eq_ignore_ascii_case("CN") {
            bits |= IpAttrFlags::GREATER_CN;
            if !is_hk_mo_tw_province(province) {
                bits |= IpAttrFlags::DOMESTIC_CN;
            }
        } else if cc.eq_ignore_ascii_case("HK")
            || cc.eq_ignore_ascii_case("MO")
            || cc.eq_ignore_ascii_case("TW")
        {
            bits |= IpAttrFlags::GREATER_CN;
        } else if cc.is_empty() && country == "中国" {
            bits |= IpAttrFlags::GREATER_CN;
            if !is_hk_mo_tw_province(province) {
                bits |= IpAttrFlags::DOMESTIC_CN;
            }
        }

        if country == "局域网" || country == "保留地址" || country == "本机地址" {
            bits |= IpAttrFlags::PRIVATE_RESERVED;
        }

        let usage_raw = view.get("usage_type");
        if !usage_raw.is_empty() {
            if let UsageType::Known(k) = UsageType::from_raw(usage_raw) {
                let (code, extra_bits) = usage_to_code_and_bits(k);
                bits |= extra_bits | (code << IpAttrFlags::USAGE_SHIFT);
            }
        }

        IpAttrFlags::from_bits(bits)
    }

    /// 获取或惰性构建当前快照的 `entry_id -> u32` 位掩码表。
    #[inline]
    fn entry_flags_table(&self) -> &[u32] {
        self.entry_flags
            .get_or_init(|| {
                let count = self
                    .group_entry_counts
                    .get(self.group_index)
                    .copied()
                    .unwrap_or(0) as usize;
                let mut table = Vec::with_capacity(count);
                if count > 0 {
                    table.push(0u32); // entry_id 0 恒为空
                    for eid in 1..(count as u32) {
                        table.push(self.compute_entry_attr_flags(eid).bits());
                    }
                }
                table.into_boxed_slice()
            })
    }

    /// 按 `row_id` O(1) 提取位掩码（零堆分配）。
    #[inline]
    pub fn inspect_flags_by_row(&self, row_id: u32) -> IpAttrFlags {
        if row_id == 0 {
            return IpAttrFlags::default();
        }
        let Some(entry_id) = self.resolve_row_id_entry(row_id) else {
            return IpAttrFlags::default();
        };
        let table = self.entry_flags_table();
        let bits = table.get(entry_id as usize).copied().unwrap_or(0);
        IpAttrFlags::from_bits(bits)
    }

    /// 按 `u32` 主机序 IPv4 极速嗅探位掩码。
    #[inline]
    pub fn inspect_flags_uint(&self, ipv4: u32) -> IpAttrFlags {
        if !self.has_v4 {
            return IpAttrFlags::default();
        }
        let Some(rid) = self.trie_row_v4(ipv4) else {
            return IpAttrFlags::default();
        };
        self.inspect_flags_by_row(rid)
    }

    /// 按 16 字节 IPv6 极速嗅探位掩码（自动处理 IPv4-mapped IPv6）。
    #[inline]
    pub fn inspect_flags_v6_bytes(&self, bytes: &[u8; 16]) -> IpAttrFlags {
        if crate::is_ipv4_mapped_v6(bytes) {
            let v4 = u32::from_be_bytes([bytes[12], bytes[13], bytes[14], bytes[15]]);
            return self.inspect_flags_uint(v4);
        }
        if !self.has_v6 {
            return IpAttrFlags::default();
        }
        let Some(rid) = self.trie_row_v6(bytes) else {
            return IpAttrFlags::default();
        };
        self.inspect_flags_by_row(rid)
    }

    /// 通用 `ToIp` 位掩码嗅探。
    #[inline]
    pub fn inspect_flags(&self, ip: impl ToIp) -> IpAttrFlags {
        let Some(parsed) = ip.to_parsed_ip() else {
            return IpAttrFlags::default();
        };
        match parsed {
            ParsedIp::V4(v4) => self.inspect_flags_uint(v4),
            ParsedIp::V6(b) => self.inspect_flags_v6_bytes(&b),
        }
    }
}

impl Snapshot {
    /// 零分配位掩码嗅探。
    #[inline]
    pub fn inspect_flags(&self, ip: impl ToIp) -> IpAttrFlags {
        self.inner.inspect_flags(ip)
    }

    /// 按 `u32` 主机序 IPv4 零分配嗅探位掩码。
    #[inline]
    pub fn inspect_flags_uint(&self, ipv4: u32) -> IpAttrFlags {
        self.inner.inspect_flags_uint(ipv4)
    }
}

impl QzdbReader {
    /// 零堆分配、零字符串解码的极速位掩码嗅探接口。
    #[inline]
    pub fn inspect_flags(&self, ip: impl ToIp) -> IpAttrFlags {
        self.snap.load().inspect_flags(ip)
    }

    /// 按主机序 `u32` IPv4 极速嗅探位掩码。
    #[inline]
    pub fn inspect_flags_uint(&self, ipv4: u32) -> IpAttrFlags {
        self.snap.load().inspect_flags_uint(ipv4)
    }

    /// 按 `row_id` 直接提取预计算位掩码。
    #[inline]
    pub fn inspect_flags_by_row(&self, row_id: u32) -> IpAttrFlags {
        self.snap.load().inspect_flags_by_row(row_id)
    }

    /// 快速判定是否为数据中心、云厂商或 CDN 节点（零字符串分配）。
    #[inline]
    pub fn is_datacenter(&self, ip: impl ToIp) -> bool {
        self.inspect_flags(ip).is_datacenter()
    }

    /// 快速判定是否为中国大陆境内 IP（不含港澳台，零字符串分配）。
    #[inline]
    pub fn is_domestic(&self, ip: impl ToIp) -> bool {
        self.inspect_flags(ip).is_domestic()
    }

    /// 快速判定是否为爬虫流量（含 `AICrawler` 与 `Spider`，零字符串分配）。
    #[inline]
    pub fn is_crawler(&self, ip: impl ToIp) -> bool {
        self.inspect_flags(ip).is_crawler()
    }

    /// 快速判定是否为 AI 爬虫（`AICrawler`，零字符串分配）。
    #[inline]
    pub fn is_ai_crawler(&self, ip: impl ToIp) -> bool {
        self.inspect_flags(ip).is_ai_crawler()
    }

    /// 快速判定是否为蜂窝移动网络（`Mobile`，零字符串分配）。
    #[inline]
    pub fn is_mobile(&self, ip: impl ToIp) -> bool {
        self.inspect_flags(ip).is_mobile()
    }

    /// 快速判定是否为 VPN/代理节点（`VPN`，零字符串分配）。
    #[inline]
    pub fn is_vpn(&self, ip: impl ToIp) -> bool {
        self.inspect_flags(ip).is_vpn()
    }
}
