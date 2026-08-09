package com.qqzeng.qzdb;

/**
 * 未知或新增的 IP 使用场景兜底记录
 */
public record UnknownUsageType(String rawValue) implements UsageType {

    @Override
    public String getDisplayZh() {
        return rawValue != null ? rawValue : "未知";
    }

    @Override
    public String getDisplayEn() {
        return rawValue != null ? rawValue : "Unknown";
    }

    @Override
    public String getDescription() {
        return "未预期的细分场景分类: " + rawValue;
    }

    @Override
    public boolean isKnown() {
        return false;
    }
}
