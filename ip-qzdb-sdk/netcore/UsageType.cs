namespace QQZeng.Qzdb;

/// <summary>Predefined IP usage-type categories recognized by the SDK.</summary>
public enum KnownUsageType
{
    /// <summary>AI training / AI search crawler (GPTBot, ClaudeBot, etc.).</summary>
    AICrawler,
    /// <summary>Carrier backbone / international transit network.</summary>
    Backbone,
    /// <summary>Residential / business broadband access (xDSL, fiber, cable, dial-up).</summary>
    Broadband,
    /// <summary>Enterprise leased line / enterprise networking.</summary>
    Business,
    /// <summary>Content delivery network.</summary>
    CDN,
    /// <summary>Public / hosted cloud (AWS, Alibaba Cloud, Azure, etc.).</summary>
    Cloud,
    /// <summary>DNS infrastructure / Anycast DNS.</summary>
    DNS,
    /// <summary>IDC / colocation facility.</summary>
    DataCenter,
    /// <summary>University / research network (CERNET, etc.).</summary>
    Education,
    /// <summary>Banking / securities / insurance institutions.</summary>
    Finance,
    /// <summary>Government / public agency network.</summary>
    Government,
    /// <summary>Generic ISP access of unspecified subtype.</summary>
    ISP,
    /// <summary>Internet exchange point.</summary>
    IXP,
    /// <summary>IoT device access network.</summary>
    IoT,
    /// <summary>Cellular mobile network (2G/3G/4G/5G).</summary>
    Mobile,
    /// <summary>Reserved / unallocated address.</summary>
    Reserved,
    /// <summary>Satellite / LEO constellation access (Starlink, etc.).</summary>
    Satellite,
    /// <summary>Generic search engine / generic web crawler.</summary>
    Spider,
    /// <summary>Audio / video / live streaming platform.</summary>
    Streaming,
    /// <summary>Usage could not be determined.</summary>
    Unknown,
    /// <summary>VPN / proxy / privacy network egress.</summary>
    VPN
}

/// <summary>Resolved IP usage-type classification. Wraps a predefined <see cref="KnownUsageType"/> when the raw value is recognized, otherwise preserves the raw string.</summary>
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

    /// <summary>True when <see cref="RawValue"/> matched a predefined <see cref="KnownUsageType"/>; otherwise false.</summary>
    public bool IsKnown { get; }
    /// <summary>The matched predefined category; <see cref="KnownUsageType.Unknown"/> when <see cref="IsKnown"/> is false.</summary>
    public KnownUsageType Known { get; }
    /// <summary>The original usage-type string from the database (or the canonical English name when known).</summary>
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

    /// <summary>Parses a raw usage-type string into a <see cref="UsageType"/>; unrecognized values keep the raw string.</summary>
    public static UsageType FromString(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return new UsageType(KnownUsageType.Unknown);
        return Map.TryGetValue(raw, out var k) ? new UsageType(k) : new UsageType(raw);
    }

    /// <summary>Alias of <see cref="FromString(string?)"/>; parses a raw usage-type string.</summary>
    public static UsageType Parse(string? raw) => FromString(raw);

    /// <summary>Chinese display name (the predefined Chinese label, or <see cref="RawValue"/> when unknown).</summary>
    public string DisplayZh => IsKnown ? ZhMap[(int)Known] : RawValue;
    /// <summary>English display name (the predefined English label, or <see cref="RawValue"/> when unknown).</summary>
    public string DisplayEn => IsKnown ? EnMap[(int)Known] : RawValue;
    /// <summary>Human-readable description of the usage category (or a note about the unexpected value when unknown).</summary>
    public string Description => IsKnown ? DescMap[(int)Known] : $"未预期的细分场景分类: {RawValue}";

    /// <summary>Returns <see cref="DisplayZh"/>.</summary>
    public string GetDisplayZh() => DisplayZh;
    /// <summary>Returns <see cref="DisplayEn"/>.</summary>
    public string GetDisplayEn() => DisplayEn;
    /// <summary>Returns <see cref="Description"/>.</summary>
    public string GetDescription() => Description;
}
