package com.qqzeng.qzdb;

import java.util.Collections;
import java.util.HashMap;
import java.util.Map;

/**
 * IP 地理位置与元数据响应实体 (GeoInfo)
 * <p>
 * 支持动态字段访问 (get) 与全量 Ult 25 字段语义化 Getter。
 * 归一化字段索引在加载期构建一次（SDK 规范 §6.1 性能强制项），查询期仅 O(1) 哈希查找。
 */
public final class GeoInfo {

    private static final char[] HEX_CHARS = "0123456789abcdef".toCharArray();

    private final String[] fieldNames;
    private final String[] values;

    // 归一化字段索引映射 (key: lowercase without underscores -> index)
    private final Map<String, Integer> normalizedMap;
    // toJson 数值类型标记（与 fieldNames 等长；内部快路径传入，公共构造时现算）
    private final boolean[] numericFlags;

    /**
     * 公共构造函数（自行构建归一化索引，用于 ChainedReader 合并结果等低频场景）
     *
     * @param fieldNames 字段名数组
     * @param values     字段值数组
     */
    public GeoInfo(String[] fieldNames, String[] values) {
        this.fieldNames = fieldNames != null ? fieldNames : new String[0];
        this.values = values != null ? values : new String[0];
        this.normalizedMap = buildNormalizedMap(this.fieldNames);
        boolean[] flags = new boolean[this.fieldNames.length];
        for (int i = 0; i < this.fieldNames.length; i++) {
            flags[i] = isNumericFieldName(this.fieldNames[i]);
        }
        this.numericFlags = flags;
    }

    /**
     * 内部热路径构造函数：复用快照级归一化索引与数值标记，避免每次查询重建 HashMap。
     */
    GeoInfo(String[] fieldNames, String[] values, Map<String, Integer> sharedNormalizedMap, boolean[] numericFlags) {
        this.fieldNames = fieldNames != null ? fieldNames : new String[0];
        this.values = values != null ? values : new String[0];
        this.normalizedMap = sharedNormalizedMap != null ? sharedNormalizedMap : buildNormalizedMap(this.fieldNames);
        this.numericFlags = numericFlags != null && numericFlags.length == this.fieldNames.length
                ? numericFlags : buildNumericFlags(this.fieldNames);
    }

    private static boolean[] buildNumericFlags(String[] names) {
        boolean[] flags = new boolean[names.length];
        for (int i = 0; i < names.length; i++) {
            flags[i] = isNumericFieldName(names[i]);
        }
        return flags;
    }

    /**
     * 构造归一化字段索引映射 (大小写与下划线不敏感)
     */
    public static Map<String, Integer> buildNormalizedMap(String[] fields) {
        if (fields == null || fields.length == 0) {
            return Collections.emptyMap();
        }
        Map<String, Integer> map = new HashMap<>(fields.length * 2);
        for (int i = 0; i < fields.length; i++) {
            if (fields[i] != null) {
                String norm = normalizeKey(fields[i]);
                map.putIfAbsent(norm, i);
            }
        }
        return map;
    }

    /**
     * 归一化算法: 小写化并移除所有 '_' 与 '-'
     * （QZDB_TEST_SPECIFICATION.md Tier 1 §2：country_code == countryCode == country-code 等价）。
     */
    public static String normalizeKey(String key) {
        if (key == null) return "";
        StringBuilder sb = new StringBuilder(key.length());
        for (int i = 0; i < key.length(); i++) {
            char c = key.charAt(i);
            if (c != '_' && c != '-') {
                sb.append(Character.toLowerCase(c));
            }
        }
        return sb.toString();
    }

    /**
     * toJson 数值类型字段判定（SDK 规范 §6.2）：longitude/latitude/asn/geo_id 输出 JSON 数字。
     */
    public static boolean isNumericFieldName(String name) {
        if (name == null) return false;
        // 与 normalizeKey(name) 等价的内联快路径（仅对不含 '_'/'-' 的形态启用，保证与归一化结果恒一致）
        if (name.indexOf('_') < 0 && name.indexOf('-') < 0) {
            switch (name.length()) {
                case 3:
                    return name.equalsIgnoreCase("asn");
                case 5:
                    return name.equalsIgnoreCase("geoid");
                case 8:
                    return name.equalsIgnoreCase("latitude");
                case 9:
                    return name.equalsIgnoreCase("longitude");
                default:
                    return false;
            }
        }
        String norm = normalizeKey(name);
        return norm.equals("longitude") || norm.equals("latitude")
                || norm.equals("asn") || norm.equals("geoid");
    }

    /**
     * 动态访问字段值 (大小写与下划线不敏感)
     *
     * @param name 字段名称 (例: "country_en", "countryEn", "COUNTRY_EN")
     * @return 字段值，缺失时返回空字符串 ""
     */
    public String get(String name) {
        if (name == null || name.isEmpty()) {
            return "";
        }
        Integer idx = normalizedMap.get(normalizeKey(name));
        if (idx != null && idx < values.length && values[idx] != null) {
            return values[idx];
        }
        return "";
    }

    /**
     * 获取所有字段名称数组
     */
    public String[] fieldNames() {
        return fieldNames.clone();
    }

    /**
     * 获取所有字段值数组
     */
    public String[] values() {
        return values.clone();
    }

    /**
     * 转换为 Pipe 竖线分隔文本
     */
    public String toPipeString() {
        if (values.length == 0) return "";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < values.length; i++) {
            if (i > 0) sb.append('|');
            sb.append(values[i] != null ? values[i] : "");
        }
        return sb.toString();
    }

    /**
     * 转换为字符串 Map (所有值均为 String)
     */
    public Map<String, String> toMap() {
        Map<String, String> map = new HashMap<>(fieldNames.length * 2);
        for (int i = 0; i < fieldNames.length; i++) {
            String val = (i < values.length && values[i] != null) ? values[i] : "";
            map.put(fieldNames[i], val);
        }
        return map;
    }

    /**
     * 序列化为 JSON 字符串
     * <p>
     * 保持原始 snake_case 键名，并将 longitude, latitude, asn, geo_id 保持为 JSON 数字类型
     * （SDK 规范 §6.2：手写序列化，不经反射式序列化框架）。
     */
    public String toJson() {
        StringBuilder sb = new StringBuilder(256);
        sb.append('{');
        boolean first = true;
        for (int i = 0; i < fieldNames.length; i++) {
            String name = fieldNames[i];
            if (name == null) continue;
            String val = (i < values.length) ? values[i] : null;

            if (!first) {
                sb.append(',');
            }
            first = false;

            sb.append('"').append(escapeJson(name)).append("\":");
            boolean numeric = i < numericFlags.length && numericFlags[i];
            if (val == null || val.isEmpty()) {
                sb.append(numeric ? "null" : "\"\"");
            } else if (numeric) {
                if (isJsonNumber(val)) {
                    sb.append(val);
                } else {
                    sb.append("null");
                }
            } else {
                sb.append('"').append(escapeJson(val)).append('"');
            }
        }
        sb.append('}');
        return sb.toString();
    }

    /** 校验字符串是否为合法 JSON 数字（整数或小数，允许负号）。 */
    private static boolean isJsonNumber(String val) {
        int i = 0, n = val.length();
        if (n == 0) return false;
        if (val.charAt(0) == '-') {
            if (n == 1) return false;
            i = 1;
        }
        boolean digit = false, dot = false;
        for (; i < n; i++) {
            char c = val.charAt(i);
            if (c >= '0' && c <= '9') {
                digit = true;
            } else if (c == '.' && !dot) {
                dot = true;
            } else {
                return false;
            }
        }
        return digit;
    }

    private static String escapeJson(String s) {
        if (s == null) return "";
        StringBuilder sb = new StringBuilder(s.length() + 8);
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"' -> sb.append("\\\"");
                case '\\' -> sb.append("\\\\");
                case '\b' -> sb.append("\\b");
                case '\f' -> sb.append("\\f");
                case '\n' -> sb.append("\\n");
                case '\r' -> sb.append("\\r");
                case '\t' -> sb.append("\\t");
                default -> {
                    if (c < 0x20) {
                        sb.append('\\').append('u')
                                .append(HEX_CHARS[(c >> 12) & 0xF])
                                .append(HEX_CHARS[(c >> 8) & 0xF])
                                .append(HEX_CHARS[(c >> 4) & 0xF])
                                .append(HEX_CHARS[c & 0xF]);
                    } else {
                        sb.append(c);
                    }
                }
            }
        }
        return sb.toString();
    }

    // =========================================================================
    // 语义 Getter 全集 (Ult 25 字段；空值兜底标准见 SDK 规范 §6.3)
    // =========================================================================

    public String getCidr() { return get("cidr"); }
    public String getCountry() { return get("country"); }
    public String getCountryEn() { return get("country_en"); }
    public String getProvince() { return get("province"); }
    public String getProvinceEn() { return get("province_en"); }
    public String getCity() { return get("city"); }
    public String getCityEn() { return get("city_en"); }
    public String getDistrict() { return get("district"); }

    public Long getGeoId() {
        String val = get("geo_id");
        if (val.isEmpty()) return null;
        try { return Long.parseLong(val); } catch (NumberFormatException e) { return null; }
    }

    public Double getLongitude() {
        String val = get("longitude");
        if (val.isEmpty()) return null;
        try { return Double.parseDouble(val); } catch (NumberFormatException e) { return null; }
    }

    public Double getLatitude() {
        String val = get("latitude");
        if (val.isEmpty()) return null;
        try { return Double.parseDouble(val); } catch (NumberFormatException e) { return null; }
    }

    public String getTimezone() { return get("timezone"); }
    public String getIsp() { return get("isp"); }
    public String getIspEn() { return get("isp_en"); }

    public Long getAsn() {
        String val = get("asn");
        if (val.isEmpty()) return null;
        try { return Long.parseLong(val); } catch (NumberFormatException e) { return null; }
    }

    public String getAsName() { return get("as_name"); }
    public String getAsDomain() { return get("as_domain"); }

    public UsageType getUsageType() {
        return UsageType.fromString(get("usage_type"));
    }

    public String getCountryAlpha2() { return get("country_alpha2"); }
    public String getCountryAlpha3() { return get("country_alpha3"); }
    public String getCurrencyCode() { return get("currency_code"); }
    public String getCurrencyName() { return get("currency_name"); }
    public String getPhonePrefix() { return get("phone_prefix"); }
    public String getEmojiFlag() { return get("emoji_flag"); }
    public String getLanguages() { return get("languages"); }

    @Override
    public String toString() {
        return toPipeString();
    }
}
