package com.qqzeng.qzdb;

import java.util.Optional;

/**
 * 批量查询单条结果包装 (保留 命中 / 未找到 / 输入错误 完整精度)
 *
 * @param input  输入的原始 IP 文本
 * @param result 查询结果，若未找到则为 Optional.empty()
 * @param error  若输入格式错误或发生故障则非 null，正常或未找到时为 null
 */
public record BatchResult(
        String input,
        Optional<GeoInfo> result,
        QzdbException error
) {
    /**
     * 是否查询成功且命中记录
     */
    public boolean isSuccess() {
        return error == null && result.isPresent();
    }

    /**
     * 是否为有效 IP 但未找到记录
     */
    public boolean isNotFound() {
        return error == null && result.isEmpty();
    }

    /**
     * 是否发生输入格式错误或底层故障
     */
    public boolean hasError() {
        return error != null;
    }
}
