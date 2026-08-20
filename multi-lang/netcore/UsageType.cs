namespace QQZeng.Qzdb;

using System.Globalization;

public enum KnownUsageType
{
    AICrawler, Backbone, Broadband, Business, CDN, Cloud, DNS, DataCenter,
    Education, Finance, Government, ISP, IXP, IoT, Mobile, Reserved,
    Satellite, Spider, Streaming, Unknown, VPN
}

public readonly struct UsageType
{
    private static readonly string[] ZhMap =
    [
        "AI 爬虫", "骨干网", "宽带", "企业", "CDN", "云服务", "DNS", "数据中心",
        "教育网", "金融", "政府", "互联网提供商", "交换中心", "物联网", "移动网络", "保留地址",
        "卫星互联网", "爬虫", "流媒体", "未知", "VPN/代理"
    ];

    private static readonly string[] EnMap =
    [
        "AICrawler", "Backbone", "Broadband", "Business", "CDN", "Cloud", "DNS", "DataCenter",
        "Education", "Finance", "Government", "ISP", "IXP", "IoT", "Mobile", "Reserved",
        "Satellite", "Spider", "Streaming", "Unknown", "VPN"
    ];

    private static readonly string[] DescMap =
    [
        "AI 训练 / AI 搜索爬虫（GPTBot、ClaudeBot 等）",
        "运营商骨干传输网 / 国际出口",
        "家庭/企业宽带接入（xDSL、光纤、Cable、拨号等）",
        "企业专线 / 企业组网",
        "内容分发网络",
        "公有云 / 托管云（AWS、阿里云、Azure 等）",
        "DNS 基础设施 / Anycast DNS",
        "IDC / 机房托管",
        "高校 / 科研网（CERNET 等）",
        "银行 / 证券 / 保险等金融机构",
        "政务 / 公共机构网络",
        "未细分类型的通用 ISP 接入",
        "互联网交换中心",
        "物联网设备接入网络",
        "蜂窝移动网络（2G/3G/4G/5G）",
        "保留 / 未分配地址",
        "卫星 / 低轨星座接入（Starlink 等）",
        "通用搜索引擎 / 通用网络爬虫",
        "音视频 / 直播流媒体平台",
        "无法判定用途",
        "VPN / 代理 / 隐私网络出口"
    ];

    public bool IsKnown { get; }
    public KnownUsageType Known { get; }
    public string RawValue { get; }

    private UsageType(KnownUsageType k) { IsKnown = true; Known = k; RawValue = EnMap[(int)k]; }
    private UsageType(string raw) { IsKnown = false; Known = KnownUsageType.Unknown; RawValue = raw; }

    private static readonly KnownUsageType[] RawToKnown =
    [
        KnownUsageType.AICrawler, KnownUsageType.Backbone, KnownUsageType.Broadband,
        KnownUsageType.Business, KnownUsageType.CDN, KnownUsageType.Cloud, KnownUsageType.DNS,
        KnownUsageType.DataCenter, KnownUsageType.Education, KnownUsageType.Finance,
        KnownUsageType.Government, KnownUsageType.ISP, KnownUsageType.IXP, KnownUsageType.IoT,
        KnownUsageType.Mobile, KnownUsageType.Reserved, KnownUsageType.Satellite,
        KnownUsageType.Spider, KnownUsageType.Streaming, KnownUsageType.Unknown, KnownUsageType.VPN
    ];

    private static readonly Dictionary<string, KnownUsageType> Map = new(StringComparer.OrdinalIgnoreCase);
    static UsageType()
    {
        for (int i = 0; i < 21; i++)
            Map[EnMap[i]] = RawToKnown[i];
    }

    public static UsageType FromString(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return new UsageType(KnownUsageType.Unknown);
        return Map.TryGetValue(raw, out var k) ? new UsageType(k) : new UsageType(raw);
    }

    public static UsageType Parse(string? raw) => FromString(raw);

    public string DisplayZh => IsKnown ? ZhMap[(int)Known] : RawValue;
    public string DisplayEn => IsKnown ? EnMap[(int)Known] : RawValue;
    public string Description => IsKnown ? DescMap[(int)Known] : $"未预期的细分场景分类: {RawValue}";

    public string GetDisplayZh() => DisplayZh;
    public string GetDisplayEn() => DisplayEn;
    public string GetDescription() => Description;
}
