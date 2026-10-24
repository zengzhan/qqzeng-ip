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

    /**
     * 字段名索引（原名精确表 + 归一化表）。
     * <p>
     * 原名（canonical snake_case）→ 索引的精确映射让语义 getter 完全避开
     * {@link #normalizeKey} 的 StringBuilder 分配。<b>热路径必须由快照共享</b>：
     * 每个 GeoInfo 都新建 HashMap 会让 64K 槽解码缓存额外背上数十 MB 堆。
     * <p>
     * 投影 / 合并结果（{@code findFields}、ChainedReader）的字段序由调用方决定，
     * 无法共享快照表——这里改为<b>首次 get() 时惰性构建</b>，且字段数
     * ≤ {@link #SMALL_INDEX_MAX} 时不建 HashMap，只建一份归一化名数组、get() 线性比对
     * （首个匹配胜出，与 putIfAbsent 语义一致）。原实现每次 findFields 构造两张 HashMap，
     * 实测 560 B/op，是该路径的主要开销。
     * <p>
     * 并发语义与 {@link #pipeCache} 同型：非 volatile 单引用惰性写，最坏重复构建一次；
     * {@link Index} 全字段 final，经数据竞争发布也保证读到完整构建的表（JLS §17.5）。
     */
    private Index index;

    /**
     * 不可变索引；final 字段保证竞态发布下的可见性。
     * 大表形态：exact/normalized 非 null；小表形态：二者为 null，normNames 为各字段归一化名。
     */
    private record Index(Map<String, Integer> exact, Map<String, Integer> normalized, String[] normNames) {
    }

    /** 不建 HashMap、改线性比对的字段数上限（投影查询通常只取少数字段）。 */
    private static final int SMALL_INDEX_MAX = 8;
    // toJson 数值类型标记（与 fieldNames 等长；内部快路径传入，其余现算——代价仅一个 boolean[]）
    private final boolean[] numericFlags;

    // to_pipe 惰性记忆化（对齐 C#/Node/Python/Go/Rust 的 _pipe 缓存语义）。
    // 非 volatile 的单引用惰性写与 String.hashCode 同型：最坏情形是并发下
    // 重复计算一次，任何线程读到的都是完整 String，绝不读到半成品。
    private String pipeCache;

    /**
     * 公共构造函数（自行构建归一化索引，用于 ChainedReader 合并结果等低频场景）
     * <p>
     * 数组做防御性拷贝：构造后外部修改原数组不影响本实体，亦是 to_pipe
     * 记忆化正确性的前提（内部数组从此不可经外部引用变更）。
     *
     * @param fieldNames 字段名数组
     * @param values     字段值数组
     */
    public GeoInfo(String[] fieldNames, String[] values) {
        this.fieldNames = fieldNames != null ? fieldNames.clone() : new String[0];
        this.values = values != null ? values.clone() : new String[0];
        this.numericFlags = buildNumericFlags(this.fieldNames);
        // 索引惰性构建（见 index 字段注释）
    }

    /**
     * 内部热路径构造函数：复用快照级归一化索引与数值标记，避免每次查询重建 HashMap。
     */
    GeoInfo(String[] fieldNames, String[] values, Map<String, Integer> sharedNormalizedMap,
            boolean[] numericFlags, Map<String, Integer> sharedExactMap) {
        this.fieldNames = fieldNames != null ? fieldNames : new String[0];
        this.values = values != null ? values : new String[0];
        // 快照级共享表齐备时直接挂上（热路径零构建）；否则留空，首次 get() 时惰性构建。
        this.numericFlags = numericFlags != null && numericFlags.length == this.fieldNames.length
                ? numericFlags : buildNumericFlags(this.fieldNames);
        if (sharedExactMap != null && sharedNormalizedMap != null) {
            this.index = new Index(sharedExactMap, sharedNormalizedMap, null);
        }
    }

    /** 取（必要时构建）字段索引。快路径保持极小以便 JIT 内联，构建逻辑放在 {@link #buildIndex()}。 */
    private Index index() {
        Index idx = index;
        return idx != null ? idx : buildIndex();
    }

    private Index buildIndex() {
        Index idx;
        if (fieldNames.length <= SMALL_INDEX_MAX) {
            String[] norm = new String[fieldNames.length];
            for (int i = 0; i < norm.length; i++) {
                norm[i] = fieldNames[i] != null ? normalizeKey(fieldNames[i]) : null;
            }
            idx = new Index(null, null, norm);
        } else {
            idx = new Index(buildExactMap(fieldNames), buildNormalizedMap(fieldNames), null);
        }
        index = idx;
        return idx;
    }

    private static boolean[] buildNumericFlags(String[] names) {
        boolean[] flags = new boolean[names.length];
        for (int i = 0; i < names.length; i++) {
            flags[i] = isNumericFieldName(names[i]);
        }
        return flags;
    }

    /** 构造原名 → 索引映射（精确匹配，不做任何归一化）。 */
    public static Map<String, Integer> buildExactMap(String[] fields) {
        if (fields == null || fields.length == 0) {
            return Collections.emptyMap();
        }
        Map<String, Integer> map = new HashMap<>(fields.length * 2);
        for (int i = 0; i < fields.length; i++) {
            if (fields[i] != null) map.putIfAbsent(fields[i], i);
        }
        return map;
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
        // 快路径：字段名不含 '_'/'-' 且无大写时直接返回原串，零分配。
        // 语义 getter 传常量键（"country"/"city" 等）全部命中此路径；
        // 含分隔符或大小写混排时走原 StringBuilder 归一化。
        int n = key.length();
        boolean simple = true;
        for (int i = 0; i < n; i++) {
            char c = key.charAt(i);
            if (c == '_' || c == '-' || (c >= 'A' && c <= 'Z')) { simple = false; break; }
        }
        if (simple) return key;
        StringBuilder sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) {
            char c = key.charAt(i);
            if (c != '_' && c != '-') {
                if (c >= 'A' && c <= 'Z') {
                    c = (char) (c + ('a' - 'A'));
                }
                sb.append(c);
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
        // 规范名（country_code / as_name / geo_id ...）是语义 getter 的绝对多数路径：
        // 精确命中 → 零分配；别名（countryCode / COUNTRY-CODE）才付归一化代价。
        Index ix = index();
        if (ix.exact() == null) {
            return getSmall(ix.normNames(), name);
        }
        Integer idx = ix.exact().get(name);
        if (idx == null) {
            idx = ix.normalized().get(normalizeKey(name));
        }
        if (idx != null && idx < values.length && values[idx] != null) {
            return values[idx];
        }
        return "";
    }

    /** 小表形态的 get：先精确、再归一化，均取首个匹配（与大表 putIfAbsent 语义一致）。 */
    private String getSmall(String[] normNames, String name) {
        int idx = -1;
        for (int i = 0; i < fieldNames.length; i++) {
            if (name.equals(fieldNames[i])) { idx = i; break; }
        }
        if (idx < 0) {
            String key = normalizeKey(name);
            for (int i = 0; i < normNames.length; i++) {
                if (key.equals(normNames[i])) { idx = i; break; }
            }
        }
        if (idx >= 0 && idx < values.length && values[idx] != null) {
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
     * 包内可见的零拷贝视图，仅供 SDK 内部只读热路径使用。
     * 前提：构造后数组内容不再变更（内部构造函数直接持有 decodeFull 新分配的数组，
     * 公共构造函数持有的是入参的拷贝），因此对外暴露不会破坏不可变语义。
     */
    String[] fieldNamesRaw() {
        return fieldNames;
    }

    /** 同 {@link #fieldNamesRaw()}，只读、零拷贝。 */
    String[] valuesRaw() {
        return values;
    }

    /**
     * 转换为 Pipe 竖线分隔文本（结果按实例记忆化，重复查询零重建）
     */
    public String toPipeString() {
        String cached = pipeCache;
        if (cached != null) return cached;
        if (values.length == 0) return "";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < values.length; i++) {
            if (i > 0) sb.append('|');
            sb.append(values[i] != null ? values[i] : "");
        }
        String out = sb.toString();
        pipeCache = out;
        return out;
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

    // 数据集以 country_code 存储 ISO 3166-1 alpha-2（如 "CN"），并不存在 country_alpha2 字段；
    // getCountryAlpha2 重定向到 country_code 以返回真实二字码（历史返回 "" 为字段名笔误 bug）。
    public String getCountryAlpha2() { return get("country_code"); }
    public String getCountryAlpha3() { return get("country_alpha3"); }
    public String getCurrencyCode() { return get("currency_code"); }
    public String getCurrencyName() { return get("currency_name"); }
    public String getPhonePrefix() { return get("phone_prefix"); }
    public String getEmojiFlag() { return get("emoji_flag"); }
    public String getLanguages() { return get("languages"); }

    /** 大洲（本地化，如 "亚洲"）；数据首字段，所有版本档均存在。 */
    public String getContinent() { return get("continent"); }
    /** 大洲（英文，如 "Asia"）；仅 ult 版含 continent_en 字段，其它版本返回 ""。 */
    public String getContinentEn() { return get("continent_en"); }
    /** ISO 3166-1 alpha-2 国家码（如 "CN"）；数据集以 country_code 存储。 */
    public String getCountryCode() { return get("country_code"); }

    @Override
    public String toString() {
        return toPipeString();
    }
}
