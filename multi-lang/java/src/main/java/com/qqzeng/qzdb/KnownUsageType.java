package com.qqzeng.qzdb;

import java.util.HashMap;
import java.util.Map;

/**
 * 官方 21 个已知的 IP 使用场景枚举
 */
public enum KnownUsageType implements UsageType {
    AI_CRAWLER("AICrawler", "AI 爬虫", "AICrawler", "AI 训练 / AI 搜索爬虫（GPTBot、ClaudeBot 等）"),
    BACKBONE("Backbone", "骨干网", "Backbone", "运营商骨干传输网 / 国际出口"),
    BROADBAND("Broadband", "宽带", "Broadband", "家庭/企业宽带接入（xDSL、光纤、Cable、拨号等）"),
    BUSINESS("Business", "企业", "Business", "企业专线 / 企业组网"),
    CDN("CDN", "CDN", "CDN", "内容分发网络"),
    CLOUD("Cloud", "云服务", "Cloud", "公有云 / 托管云（AWS、阿里云、Azure 等）"),
    DNS("DNS", "DNS", "DNS", "DNS 基础设施 / Anycast DNS"),
    DATA_CENTER("DataCenter", "数据中心", "DataCenter", "IDC / 机房托管"),
    EDUCATION("Education", "教育网", "Education", "高校 / 科研网（CERNET 等）"),
    FINANCE("Finance", "金融", "Finance", "银行 / 证券 / 保险等金融机构"),
    GOVERNMENT("Government", "政府", "Government", "政务 / 公共机构网络"),
    ISP("ISP", "互联网提供商", "ISP", "未细分类型的通用 ISP 接入"),
    IXP("IXP", "交换中心", "IXP", "互联网交换中心"),
    IOT("IoT", "物联网", "IoT", "物联网设备接入网络"),
    MOBILE("Mobile", "移动网络", "Mobile", "蜂窝移动网络（2G/3G/4G/5G）"),
    RESERVED("Reserved", "保留地址", "Reserved", "保留 / 未分配地址"),
    SATELLITE("Satellite", "卫星互联网", "Satellite", "卫星 / 低轨星座接入（Starlink 等）"),
    SPIDER("Spider", "爬虫", "Spider", "通用搜索引擎 / 通用网络爬虫"),
    STREAMING("Streaming", "流媒体", "Streaming", "音视频 / 直播流媒体平台"),
    UNKNOWN("Unknown", "未知", "Unknown", "无法判定用途"),
    VPN("VPN", "VPN/代理", "VPN", "VPN / 代理 / 隐私网络出口");

    private final String rawValue;
    private final String displayZh;
    private final String displayEn;
    private final String description;

    private static final Map<String, KnownUsageType> RAW_MAP = new HashMap<>();

    static {
        for (KnownUsageType type : values()) {
            RAW_MAP.put(type.rawValue.toLowerCase(), type);
        }
    }

    KnownUsageType(String rawValue, String displayZh, String displayEn, String description) {
        this.rawValue = rawValue;
        this.displayZh = displayZh;
        this.displayEn = displayEn;
        this.description = description;
    }

    @Override
    public String rawValue() {
        return rawValue;
    }

    @Override
    public String getDisplayZh() {
        return displayZh;
    }

    @Override
    public String getDisplayEn() {
        return displayEn;
    }

    @Override
    public String getDescription() {
        return description;
    }

    @Override
    public boolean isKnown() {
        return true;
    }

    public static KnownUsageType fromRaw(String raw) {
        if (raw == null) return null;
        return RAW_MAP.get(raw.toLowerCase());
    }
}
