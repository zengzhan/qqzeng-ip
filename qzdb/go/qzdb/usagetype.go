package qzdb

import "strings"

// UsageType 表示 IP 网络使用场景类型。
// 包含官方 21 个已知场景；未知场景安全兜底（不崩溃）。
type UsageType struct {
	raw        string
	displayZh  string
	displayEn  string
	description string
	known      bool
}

// RawValue 原始编码字符串（如 "Broadband"、"Cloud"）。
func (u UsageType) RawValue() string { return u.raw }

// DisplayZh 中文显示名称。
func (u UsageType) DisplayZh() string { return u.displayZh }

// DisplayEn 英文显示名称（与 raw 通常一致）。
func (u UsageType) DisplayEn() string { return u.displayEn }

// Description 场景详细描述。
func (u UsageType) Description() string { return u.description }

// IsKnown 是否为官方已知场景。
func (u UsageType) IsKnown() bool { return u.known }

// String 返回原始值。
func (u UsageType) String() string { return u.raw }

// 21 个官方已知场景。
var knownUsageTypes = []UsageType{
	{"AICrawler", "AI 爬虫", "AICrawler", "AI 训练 / AI 搜索爬虫（GPTBot、ClaudeBot 等）", true},
	{"Backbone", "骨干网", "Backbone", "运营商骨干传输网 / 国际出口", true},
	{"Broadband", "宽带", "Broadband", "家庭/企业宽带接入（xDSL、光纤、Cable、拨号等）", true},
	{"Business", "企业", "Business", "企业专线 / 企业组网", true},
	{"CDN", "CDN", "CDN", "内容分发网络", true},
	{"Cloud", "云服务", "Cloud", "公有云 / 托管云（AWS、阿里云、Azure 等）", true},
	{"DNS", "DNS", "DNS", "DNS 基础设施 / Anycast DNS", true},
	{"DataCenter", "数据中心", "DataCenter", "IDC / 机房托管", true},
	{"Education", "教育网", "Education", "高校 / 科研网（CERNET 等）", true},
	{"Finance", "金融", "Finance", "银行 / 证券 / 保险等金融机构", true},
	{"Government", "政府", "Government", "政务 / 公共机构网络", true},
	{"ISP", "互联网提供商", "ISP", "未细分类型的通用 ISP 接入", true},
	{"IXP", "交换中心", "IXP", "互联网交换中心", true},
	{"IoT", "物联网", "IoT", "物联网设备接入网络", true},
	{"Mobile", "移动网络", "Mobile", "蜂窝移动网络（2G/3G/4G/5G）", true},
	{"Reserved", "保留地址", "Reserved", "保留 / 未分配地址", true},
	{"Satellite", "卫星互联网", "Satellite", "卫星 / 低轨星座接入（Starlink 等）", true},
	{"Spider", "爬虫", "Spider", "通用搜索引擎 / 通用网络爬虫", true},
	{"Streaming", "流媒体", "Streaming", "音视频 / 直播流媒体平台", true},
	{"Unknown", "未知", "Unknown", "无法判定用途", true},
	{"VPN", "VPN/代理", "VPN", "VPN / 代理 / 隐私网络出口", true},
}

var usageTypeByRaw map[string]UsageType

func init() {
	usageTypeByRaw = make(map[string]UsageType, len(knownUsageTypes))
	for _, t := range knownUsageTypes {
		usageTypeByRaw[strings.ToLower(t.raw)] = t
	}
}

// UnknownUsageType 返回统一的未知兜底实例。
func UnknownUsageType(raw string) UsageType {
	if raw == "" {
		raw = "Unknown"
	}
	return UsageType{raw: raw, displayZh: raw, displayEn: raw, description: "未知场景", known: false}
}

// ParseUsageType 从原始文本解析 UsageType；已知场景返回强类型，未知安全兜底。
func ParseUsageType(raw string) UsageType {
	if raw == "" {
		return usageTypeByRaw["unknown"]
	}
	if t, ok := usageTypeByRaw[strings.ToLower(raw)]; ok {
		return t
	}
	return UnknownUsageType(raw)
}

// KnownUsageTypes 返回全部 21 个官方已知场景（只读）。
func KnownUsageTypes() []UsageType {
	out := make([]UsageType, len(knownUsageTypes))
	copy(out, knownUsageTypes)
	return out
}
