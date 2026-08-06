package com.qqzeng.qzdb;

import java.util.Collections;
import java.util.HashMap;
import java.util.Map;
import java.util.Set;

/**
 * IP 地理位置与元数据响应实体 (GeoInfo)
 * <p>
 * 支持动态字段访问 (get) 与全量 Ult 25 字段语义化 Getter。
 */
public final class GeoInfo {

    private final String[] fieldNames;
    private final String[] values;

    // 归一化字段索引映射 (key: lowercase without underscores -> index)
    private final Map<String, Integer> normalizedMap;

    /**
     * 内部构造函数
     *
     * @param fieldNames 字段名数组
     * @param values     字段值数组
     */
    public GeoInfo(String[] fieldNames, String[] values) {
        this.fieldNames = fieldNames != null ? fieldNames : new String[0];
        this.values = values != null ? values : new String[0];
        this.normalizedMap = buildNormalizedMap(this.fieldNames);
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
     * 归一化算法: 小写化并移除所有 '_'
     */
    public static String normalizeKey(String key) {
        if (key == null) return "";
        StringBuilder sb = new StringBuilder(key.length());
        for (int i = 0; i < key.length(); i++) {
            char c = key.charAt(i);
            if (c != '_') {
                sb.append(Character.toLowerCase(c));
            }
        }
        return sb.toString();
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
     * 保持原始 snake_case 键名，并将 longitude, latitude, asn, geo_id 保持为 JSON 数字类型。
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
            if (val == null || val.isEmpty()) {
                if (isNumericField(name)) {
                    sb.append("null");
                } else {
                    sb.append("\"\"");
                }
            } else if (isNumericField(name)) {
                try {
                    // 校验是否为有效数值
                    Double.parseDouble(val);
                    sb.append(val);
                } catch (NumberFormatException e) {
                    sb.append("null");
                }
            } else {
                sb.append('"').append(escapeJson(val)).append('"');
            }
        }
        sb.append('}');
        return sb.toString();
    }

    private static boolean isNumericField(String name) {
        String norm = normalizeKey(name);
        return norm.equals("longitude") || norm.equals("latitude") ||
               norm.equals("asn") || norm.equals("geoid");
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
                        sb.append(String.format("\\u%04x", (int) c));
                    } else {
                        sb.append(c);
                    }
                }
            }
        }
        return sb.toString();
    }

    // =========================================================================
    // 语义 Getter 全集 (Ult 25 字段)
    // =========================================================================

    public String getCidr() { return get("cidr"); }
    public String getCountry() { return get("country"); }
    public String getCountryEn() {
        String val = get("country_en");
        return !val.isEmpty() ? val : getCountry();
    }
    public String getProvince() { return get("province"); }
    public String getProvinceEn() {
        String val = get("province_en");
        return !val.isEmpty() ? val : getProvince();
    }
    public String getCity() { return get("city"); }
    public String getCityEn() {
        String val = get("city_en");
        return !val.isEmpty() ? val : getCity();
    }
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
    public String getIspEn() {
        String val = get("isp_en");
        return !val.isEmpty() ? val : getIsp();
    }

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
