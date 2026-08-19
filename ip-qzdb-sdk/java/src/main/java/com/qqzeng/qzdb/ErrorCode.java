package com.qqzeng.qzdb;

/**
 * QZDB 错误码枚举
 */
public enum ErrorCode {
    /**
     * 文件不存在或读取失败
     */
    FILE_NOT_FOUND("File not found or unreadable"),

    /**
     * 无效的 Magic 魔数 (非 QZDB)
     */
    BAD_MAGIC("Invalid magic header, expected QZDB"),

    /**
     * 错误的 Header 头信息结构
     */
    BAD_HEADER("Bad header structure"),

    /**
     * 不支持的版本格式
     */
    UNSUPPORTED("Unsupported database format version"),

    /**
     * 数据库文件已损坏或 CRC32 校验失败
     */
    CORRUPTED("Database file is corrupted or CRC32 checksum mismatch"),

    /**
     * 无效的参数
     */
    INVALID_PARAM("Invalid parameter"),

    /**
     * 未找到记录
     */
    NOT_FOUND("IP location not found"),

    /**
     * 无效的 IP 地址格式
     */
    INVALID_IP("Invalid IP address format");

    private final String message;

    ErrorCode(String message) {
        this.message = message;
    }

    public String getMessage() {
        return message;
    }
}
