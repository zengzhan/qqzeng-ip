package com.qqzeng.qzdb;

/**
 * IP 网络使用场景类型 (密封接口)
 * <p>
 * 包含官方 21 个场景强类型枚举，并在未知场景出现时以 {@link UnknownUsageType} 安全兜底不崩溃。
 */
public sealed interface UsageType permits KnownUsageType, UnknownUsageType {

    /**
     * 获取原始编码字符串 (例如 "Broadband", "Cloud", "AICrawler")
     */
    String rawValue();

    /**
     * 获取中文显示名称 (例如 "宽带", "云服务", "AI 爬虫")
     */
    String getDisplayZh();

    /**
     * 获取英文显示名称 (例如 "Broadband", "Cloud", "AICrawler")
     */
    String getDisplayEn();

    /**
     * 获取场景详细描述
     */
    String getDescription();

    /**
     * 是否为已知官方场景
     */
    boolean isKnown();

    /**
     * 从原始文本解析 UsageType
     *
     * @param raw 原始场景文本
     * @return UsageType 实例 (已知场景返回 KnownUsageType，未知场景返回 UnknownUsageType)
     */
    static UsageType fromString(String raw) {
        if (raw == null || raw.isEmpty()) {
            return KnownUsageType.UNKNOWN;
        }
        KnownUsageType known = KnownUsageType.fromRaw(raw);
        if (known != null) {
            return known;
        }
        return new UnknownUsageType(raw);
    }
}
