package com.qqzeng.qzdb;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.atomic.AtomicReference;
import java.util.stream.Stream;
import java.util.zip.CRC32;

/**
 * QZDB 高性能 IP 数据库查询引擎 (QzdbReader)
 * <p>
 * 采用无锁 (Lock-Free) 内存视图与原子替换 (Volatile Snapshot) 架构，支持文件 Mmap 与内存 Buffer 两种加载模式。
 * <p>
 * 格式依据 docs/QZDB_FORMAT.md（192 字节 Header、Jump Table + Patricia Trie、IPRow 间接层、
 * GroupMetadataTable/GROUP_SCHEMA 动态布局、原生标量字段、String Pools、Metadata TLV、CRC32）。
 * API 依据 docs/QZDB_SDK_API.md v2.4。
 */
public class QzdbReader implements AutoCloseable {

    private static final int HEADER_SIZE = 192;
    private static final int SENTINEL = 0x80000000;
    private static final int SENTINEL_MASK_24 = 0x7FFFFF;
    private static final int SENTINEL_MASK_31 = 0x7FFFFFFF;
    /** poolIdxSize ≤ 3 → 池索引最大 2^24-1，count 超出即视为损坏（防 OOM 护栏）。 */
    private static final int MAX_POOL_COUNT = 1 << 24;
    /** 流式 CRC 分块大小（避免整文件堆内拷贝）。 */
    private static final int CRC_CHUNK = 1 << 20;
    private static final byte[] ZERO4 = new byte[4];
    private static final int[] HEX_DIGITS = new int[128];
    private static final ThreadLocal<java.text.DecimalFormat> FLOAT6 =
            ThreadLocal.withInitial(() -> new java.text.DecimalFormat("0.000000",
                    java.text.DecimalFormatSymbols.getInstance(Locale.US)));

    static {
        java.util.Arrays.fill(HEX_DIGITS, -1);
        for (int i = 0; i < 10; i++) HEX_DIGITS['0' + i] = i;
        for (int i = 0; i < 6; i++) {
            HEX_DIGITS['a' + i] = 10 + i;
            HEX_DIGITS['A' + i] = 10 + i;
        }
    }

    // ------------------------------------------------------------------
    // 版本档次注册表（FORMAT §3.1 / §10.3）
    //
    // 文件是自描述的：Header.VersionMask（偏移 6）与每个 GROUP_SCHEMA.groupId
    // 都携带 one-hot 档次位掩码。这个位掩码——而不是字段数——才是权威的档次信号。
    // EDITION_BY_BIT 是规范的「位 -> 名字」注册表；将来新增档次只需在这里追加一位、
    // 在 EDITION_FIELD_NAMES 追加一行，任何解析代码都不用改。
    // ------------------------------------------------------------------
    static final String[] EDITION_BY_BIT = {"std", "asn", "pro", "max", "ult"}; // bit0..bit4

    /** 各档次的规范字段顺序（FORMAT 附录一）。仅在文件没带 Metadata field_names 时使用，绝不覆盖文件自己的名字。 */
    static final Map<String, String[]> EDITION_FIELD_NAMES = Map.of(
            "std", new String[]{"continent", "country_code", "country", "province", "city", "isp"},
            "asn", new String[]{"continent", "country_code", "country", "isp", "asn", "as_name",
                    "as_domain", "usage_type"},
            "pro", new String[]{"continent", "country_code", "country", "province", "city", "district",
                    "geo_id", "longitude", "latitude", "timezone", "isp"},
            "max", new String[]{"continent", "country_code", "country", "province", "city", "district",
                    "geo_id", "longitude", "latitude", "timezone", "isp", "asn", "as_name",
                    "as_domain", "usage_type"},
            "ult", new String[]{"continent", "continent_en", "country_code", "country_alpha3", "country",
                    "country_en", "province", "province_en", "city", "city_en", "district",
                    "district_en", "geo_id", "longitude", "latitude", "timezone", "languages",
                    "currency_code", "phone_prefix", "emoji_flag", "isp", "asn", "as_name",
                    "as_domain", "usage_type"});

    // 来源标记。8 种 SDK 里字符串值完全一致，跨语言校验可直接比对。
    /** 来自 VersionMask / groupId（权威）。 */
    public static final String EDITION_SOURCE_VERSION_MASK = "version_mask";
    /** 来自 Metadata primary_version / version_list。 */
    public static final String EDITION_SOURCE_METADATA = "metadata";
    /** 兜底：字段数唯一匹配。 */
    public static final String EDITION_SOURCE_INFERRED = "inferred";
    /** 确实判定不出来。 */
    public static final String EDITION_SOURCE_UNKNOWN = "unknown";

    /** 字段名来自文件自己的 Metadata。 */
    public static final String FIELD_NAMES_SOURCE_METADATA = "metadata";
    /** 字段名来自已知档次的规范表。 */
    public static final String FIELD_NAMES_SOURCE_EDITION = "edition";
    /** 字段名是 field_0..field_N-1 占位符。 */
    public static final String FIELD_NAMES_SOURCE_SYNTHETIC = "synthetic";

    /** 反向索引：字段数 -> 档次，只在该字段数唯一对应一个档次时才建立。 */
    private static final Map<Integer, String> EDITION_BY_FIELD_COUNT = buildEditionByFieldCount();

    private static Map<Integer, String> buildEditionByFieldCount() {
        Map<Integer, String> m = new HashMap<>();
        for (Map.Entry<String, String[]> e : EDITION_FIELD_NAMES.entrySet()) {
            int n = e.getValue().length;
            m.put(n, m.containsKey(n) ? null : e.getKey()); // 基数撞车 -> null（不唯一即弃用）
        }
        return m;
    }

    /** 把 one-hot 档次位掩码解析成档次名；不是 one-hot 就返回 ""。 */
    static String editionFromMask(int mask) {
        if (mask <= 0 || (mask & (mask - 1)) != 0) return ""; // 0 或多于一位 -> 不是单一档次
        int bit = Integer.numberOfTrailingZeros(mask);
        return bit < EDITION_BY_BIT.length ? EDITION_BY_BIT[bit] : "";
    }

    /**
     * 不可变只读数据快照（构造完成后经 AtomicReference 安全发布，此后只读）。
     */
    private static final class Snapshot {
        ByteBuffer data;
        int dataLen;
        int groupIndex;

        // Header 元数据
        int flags;
        boolean hasV4;
        boolean hasV6;
        boolean v4Node24;
        boolean v6Node24;
        int v6JumpBits;
        int poolCount;
        int poolIdxSize;
        int rowCount;
        int v4NodeCount;
        int v6NodeCount;
        int ipRowSize;
        int buildDate;          // yyyyMMdd（Header 偏移 32）

        // 偏移量
        long offRowSchema;
        long offGroupSchema;
        long offV4Jump;
        long offV4Nodes;
        long offV6Jump;
        long offV6Nodes;
        long offIPRow;
        long offGeoEntries;
        long offPools;
        long offMeta;

        // IPRow 动态宽度（ROW_SCHEMA 驱动，默认 3/3/0）
        int rowGeoWidth;
        int rowAsnWidth;
        int rowUsageWidth;

        // 版本组布局（GroupMetadataTable + GROUP_SCHEMA + 兜底）
        int actualGroups;
        int[] groupFieldCounts;
        long[] groupEntryCounts;
        int[] groupDimMasks;
        long[] groupEntryOffsets;
        int[] groupStrides;
        int[][] groupFieldWidths;
        int[][] groupFieldOffsets;
        boolean[][] groupFieldNative;
        int[][] groupFieldNativeType;
        /** 每组的 one-hot 档次位掩码（GROUP_SCHEMA.groupId；0 = 未声明）。 */
        int[] groupIds;
        /** 逐组解析出的字段名。dimensionMask 修复与跨组查询都要用到非当前组的名字。 */
        String[][] groupFieldNames;

        String[][][] pools;

        // 字段名与归一化索引（加载期一次性构建，见 SDK 规范 §6.1 性能强制项）
        String[] fieldNames;
        Map<String, Integer> normalizedFieldMap;
        boolean[] numericFieldFlags;

        // 元数据属性
        String version;      // Metadata type=1 version_list（无则 ""）
        String description;  // Metadata type=3 description（无则 ""）
        String dataMonth;    // Header BuildDate -> "yyyy-MM"（无则 ""）
        String buildTimeStr; // Header BuildDate -> "yyyy-MM-dd"（无则 ""）
        String edition;      // 按 §10.3 优先级判定：groupId/VersionMask -> Metadata -> 字段数 -> ""
        String editionSource;    // edition 命中了哪条规则
        String fieldNamesSource; // fieldNames 是读出来的还是补出来的
        int versionMask;         // Header 偏移 6：文件级 one-hot 档次位掩码
        String scope;        // 当前格式 Header 尚无 scope 字段，按规范 §13.1 返回 ""

        // CRC32（canonical：CRC 字段填 0 计算）。open 校验时顺带得出，否则首次 getFileHash 惰性计算。
        long storedCrc;
        volatile Long canonicalCrc;
        private int geoEntryGroupCount;

        Snapshot(ByteBuffer buffer, int groupIndex, boolean verifyCrc) throws QzdbException {
            this.data = buffer.duplicate().order(ByteOrder.LITTLE_ENDIAN);
            this.dataLen = data.capacity();
            this.groupIndex = groupIndex;

            parseHeader();
            parseSectionBounds();
            parseRowSchema();
            parseGroups();
            parseMetadata();

            if (verifyCrc) {
                long calc = computeCanonicalCrc(data);
                this.canonicalCrc = calc;
                if (calc != storedCrc) {
                    throw new QzdbException(ErrorCode.CORRUPTED,
                            String.format("CRC32 checksum mismatch: stored=0x%08x calculated=0x%08x — database corrupted or truncated",
                                    storedCrc, calc));
                }
            }

            this.pools = parsePools();
        }

        private void parseHeader() throws QzdbException {
            if (dataLen < HEADER_SIZE) {
                throw new QzdbException(ErrorCode.CORRUPTED, "File too small for QZDB header: " + dataLen + " bytes");
            }
            if (data.get(0) != 'Q' || data.get(1) != 'Z' || data.get(2) != 'D' || data.get(3) != 'B') {
                throw new QzdbException(ErrorCode.BAD_MAGIC, "Invalid magic, expected QZDB");
            }
            int fmtVer = data.get(4) & 0xFF;
            if (fmtVer != 1) {
                throw new QzdbException(ErrorCode.UNSUPPORTED,
                        "Unsupported HeaderVersion: " + fmtVer + " (QZDB requires version 1, see FORMAT.md §10.1)");
            }

            // VersionMask（偏移 6）：文件级 one-hot 档次位掩码，权威档次信号（FORMAT §3.1）。
            versionMask = readU16(data, 6);

            flags = readU16(data, 8);
            hasV4 = (flags & 1) != 0;
            hasV6 = (flags & 2) != 0;
            v4Node24 = (flags & 0x10) != 0;
            v6Node24 = (flags & 0x20) != 0;

            int v6Bits = data.get(11) & 0xFF;
            if (v6Bits == 0) v6Bits = 16;
            if (v6Bits < 8 || v6Bits > 20) {
                throw new QzdbException(ErrorCode.INVALID_PARAM, "v6JumpBits out of range [8,20]: " + v6Bits);
            }
            this.v6JumpBits = v6Bits;

            poolCount = data.get(12) & 0xFF;
            poolIdxSize = data.get(13) & 0xFF;
            if (poolIdxSize != 2 && poolIdxSize != 3) {
                throw new QzdbException(ErrorCode.INVALID_PARAM, "poolIdxSize must be 2 or 3, got " + poolIdxSize);
            }
            buildDate = readU32(data, 32);
            rowCount = readU32(data, 20);
            storedCrc = readU32(data, 16) & 0xFFFFFFFFL;

            int hs = readU32(data, 36);
            if (hs != HEADER_SIZE) {
                throw new QzdbException(ErrorCode.BAD_HEADER, "Unexpected header size: " + hs);
            }

            offRowSchema = readU64(data, 40);
            offGroupSchema = readU64(data, 48);
            offV4Jump = readU64(data, 64);
            offV4Nodes = readU64(data, 72);
            offV6Jump = readU64(data, 80);
            offV6Nodes = readU64(data, 88);
            offIPRow = readU64(data, 96);
            offGeoEntries = readU64(data, 104);
            offPools = readU64(data, 136);
            offMeta = readU64(data, 144);

            v4NodeCount = readU32(data, 152);
            v6NodeCount = readU32(data, 156);

            int rs = readU32(data, 160);
            if (rs < 1 || rs > 64) {
                throw new QzdbException(ErrorCode.INVALID_PARAM, "ipRowSize out of range [1,64]: " + rs);
            }
            ipRowSize = rs;

            int gCount = readU32(data, 164);
            if (gCount < 1 || gCount > 255) {
                throw new QzdbException(ErrorCode.INVALID_PARAM, "geoEntryGroupCount out of range [1,255]: " + gCount);
            }
            geoEntryGroupCount = gCount;
        }

        private void parseSectionBounds() throws QzdbException {
            int v4NodeSize = v4Node24 ? 6 : 8;
            int v6NodeSize = v6Node24 ? 6 : 8;
            checkSection("v4_jump", offV4Jump, 65536L * 4);
            checkSection("v4_nodes", offV4Nodes, (long) v4NodeCount * v4NodeSize);
            checkSection("v6_jump", offV6Jump, (1L << v6JumpBits) * 4);
            checkSection("v6_nodes", offV6Nodes, (long) v6NodeCount * v6NodeSize);
            checkSection("ip_row", offIPRow, (long) rowCount * ipRowSize);
            // 变长区段仅能校验起点（内容长度在各自解析处再逐步校验）；负值一律拒绝，避免后续 (int) 强转成任意下标
            if (offGeoEntries < 0 || offGeoEntries >= dataLen) {
                throw new QzdbException(ErrorCode.CORRUPTED, "Section geo_entries out of bounds: " + offGeoEntries);
            }
            if (offPools < 0 || offPools >= dataLen) {
                throw new QzdbException(ErrorCode.CORRUPTED, "Section pools out of bounds: " + offPools);
            }
            if (offMeta < 0 || offMeta > dataLen) {
                throw new QzdbException(ErrorCode.CORRUPTED, "Section meta out of bounds: " + offMeta);
            }
            if (offRowSchema < 0 || offRowSchema > dataLen) {
                throw new QzdbException(ErrorCode.CORRUPTED, "Section row_schema out of bounds: " + offRowSchema);
            }
            if (offGroupSchema < 0 || offGroupSchema > dataLen) {
                throw new QzdbException(ErrorCode.CORRUPTED, "Section group_schema out of bounds: " + offGroupSchema);
            }
            if (hasV4 && offV4Jump <= 0) {
                throw new QzdbException(ErrorCode.CORRUPTED, "Flags indicate V4 data but V4 jump offset is zero");
            }
            if (hasV4 && v4NodeCount > 0 && offV4Nodes <= 0) {
                throw new QzdbException(ErrorCode.CORRUPTED, "V4 node count > 0 but V4 nodes offset is zero");
            }
            if (hasV6 && offV6Jump <= 0) {
                throw new QzdbException(ErrorCode.CORRUPTED, "Flags indicate V6 data but V6 jump offset is zero");
            }
            if (hasV6 && v6NodeCount > 0 && offV6Nodes <= 0) {
                throw new QzdbException(ErrorCode.CORRUPTED, "V6 node count > 0 but V6 nodes offset is zero");
            }
            if (offIPRow <= 0) {
                throw new QzdbException(ErrorCode.CORRUPTED, "Missing IPRow section (offsetIPRow == 0)");
            }
        }

        private void parseRowSchema() throws QzdbException {
            int geoW = 3, asnW = 3, usageW = 0;
            if (offRowSchema > 0 && offRowSchema + 4 <= dataLen) {
                int sp = (int) offRowSchema;
                int fCount = data.get(sp) & 0xFF;
                int stride = data.get(sp + 1) & 0xFF;
                if (fCount >= 1 && fCount <= 8 && sp + 4 + (long) fCount * 4 <= dataLen && stride == ipRowSize) {
                    int g2 = 0, a2 = 0, u2 = 0, total = 0;
                    boolean ok = true;
                    int wpos = sp + 4;
                    for (int i = 0; i < fCount; i++) {
                        int fid = data.get(wpos) & 0xFF;
                        int w = data.get(wpos + 1) & 0xFF;
                        if (w < 1 || w > 4) ok = false;
                        if (fid == 0) g2 = w;
                        else if (fid == 1) a2 = w;
                        else if (fid == 2) u2 = w;
                        wpos += 4;
                        total += w;
                    }
                    if (ok && total == ipRowSize) {
                        geoW = g2;
                        asnW = a2;
                        usageW = u2;
                    }
                }
            }
            rowGeoWidth = geoW;
            rowAsnWidth = asnW;
            rowUsageWidth = usageW;
        }

        private void parseGroups() throws QzdbException {
            long[] headerGeoOffsets = new long[4];
            for (int i = 0; i < 4; i++) {
                headerGeoOffsets[i] = readU48(data, 168 + i * 6);
            }

            if (offGeoEntries <= 0) {
                throw new QzdbException(ErrorCode.CORRUPTED, "Missing GeoEntry section (offsetGeoEntries == 0)");
            }
            int gmOff = (int) offGeoEntries;
            if (gmOff + 1 > dataLen) {
                throw new QzdbException(ErrorCode.CORRUPTED, "GroupMetadataTable out of bounds");
            }
            int tableGroups = data.get(gmOff) & 0xFF;
            gmOff += 1;

            int groups = Math.min(tableGroups, geoEntryGroupCount);
            if (groups > 4) groups = 4;
            if (groups < 1) {
                throw new QzdbException(ErrorCode.CORRUPTED, "GroupMetadataTable groupCount is 0");
            }
            if (groupIndex < 0 || groupIndex >= groups) {
                throw new QzdbException(ErrorCode.INVALID_PARAM,
                        "groupIndex out of range [0," + (groups - 1) + "]: " + groupIndex);
            }
            if (gmOff + (long) groups * 7 > dataLen) {
                throw new QzdbException(ErrorCode.CORRUPTED, "GroupMetadataTable truncated");
            }
            this.actualGroups = groups;
            groupFieldCounts = new int[groups];
            groupEntryCounts = new long[groups];
            groupEntryOffsets = new long[groups];
            groupDimMasks = new int[groups];
            groupStrides = new int[groups];
            groupFieldWidths = new int[groups][];
            groupFieldOffsets = new int[groups][];
            groupFieldNative = new boolean[groups][];
            groupFieldNativeType = new int[groups][];
            groupIds = new int[groups];

            for (int gi = 0; gi < groups; gi++) {
                groupFieldCounts[gi] = data.get(gmOff) & 0xFF;
                gmOff += 1;
                groupEntryCounts[gi] = readU32(data, gmOff) & 0xFFFFFFFFL;
                gmOff += 4;
                groupDimMasks[gi] = readU16(data, gmOff);
                gmOff += 2;
                groupEntryOffsets[gi] = offGeoEntries + headerGeoOffsets[gi];
            }

            // GROUP_SCHEMA 自带的 fldCount 与 GEO_ENTRIES 表里的 groupFieldCounts 是两个独立来源，
            // 伪造文件可让二者不一致；此处记录 schema 侧取值，解析完统一做不变量对齐（见下方 reconcile 块）。
            int[] schemaFldCounts = new int[groups];
            java.util.Arrays.fill(schemaFldCounts, -1);

            if (offGroupSchema > 0 && offGroupSchema + 2 <= dataLen) {
                int sp = (int) offGroupSchema;
                int gsGroupCount = readU16(data, sp);
                sp += 2;
                int maxGsGroups = Math.min(gsGroupCount, groups);
                for (int gi = 0; gi < maxGsGroups; gi++) {
                    if (sp + 16 > dataLen) break;
                    // groupId 是本组的 one-hot 档次位掩码（FORMAT §3.1），是该组权威的档次信号，
                    // 不能跳过——parseMetadata() 依赖它。
                    groupIds[gi] = readU16(data, sp);
                    sp += 2;
                    int fldCount = readU16(data, sp);
                    sp += 2;
                    sp += 4; // entryCount
                    int stride = readU32(data, sp);
                    sp += 4;
                    sp += 4; // flags
                    if (fldCount < 0 || fldCount > 255 || sp + (long) fldCount * 12 > dataLen) break;

                    schemaFldCounts[gi] = fldCount;
                    groupStrides[gi] = stride;
                    int[] widths = new int[fldCount];
                    int[] offsets = new int[fldCount];
                    boolean[] natives = new boolean[fldCount];
                    int[] natTypes = new int[fldCount];
                    for (int fi = 0; fi < fldCount; fi++) {
                        // fieldId 只是槽位序号（0..N-1），不带跨档语义，读过即丢。
                        sp += 2;
                        int w = data.get(sp) & 0xFF;
                        sp += 1;
                        int fieldFlags = data.get(sp) & 0xFF;
                        sp += 1;
                        natives[fi] = (fieldFlags & 0x01) != 0;
                        natTypes[fi] = (fieldFlags >> 1) & 0x03;
                        offsets[fi] = readU32(data, sp);
                        sp += 4;
                        sp += 4; // poolSectionId
                        widths[fi] = w;
                    }
                    groupFieldWidths[gi] = widths;
                    groupFieldOffsets[gi] = offsets;
                    groupFieldNative[gi] = natives;
                    groupFieldNativeType[gi] = natTypes;
                }
            }
            // 不变量对齐：schema 的 fldCount 必须与 GEO_ENTRIES 的 groupFieldCounts 一致。
            // 不一致说明文件被篡改或损坏——丢弃该组的 schema 布局，退回 §9.4 默认布局，
            // 避免下游按 groupFieldCounts 索引 widths/offsets/native 数组时越界或读到错位字段。
            for (int g = 0; g < groups; g++) {
                if (schemaFldCounts[g] >= 0 && schemaFldCounts[g] != groupFieldCounts[g]) {
                    groupFieldWidths[g] = null;
                    groupFieldOffsets[g] = null;
                    groupFieldNative[g] = null;
                    groupFieldNativeType[g] = null;
                    groupStrides[g] = 0;
                }
            }

            // 兜底（stride = fieldCount × poolIdxSize，§9.4）
            for (int g = 0; g < groups; g++) {
                int fc = groupFieldCounts[g];
                if (groupStrides[g] == 0) groupStrides[g] = fc * poolIdxSize;
                if (groupFieldWidths[g] == null) {
                    int[] w = new int[fc];
                    int[] o = new int[fc];
                    java.util.Arrays.fill(w, poolIdxSize);
                    for (int i = 0; i < fc; i++) o[i] = i * poolIdxSize;
                    groupFieldWidths[g] = w;
                    groupFieldOffsets[g] = o;
                }
                if (groupFieldNative[g] == null) groupFieldNative[g] = new boolean[fc];
                if (groupFieldNativeType[g] == null) groupFieldNativeType[g] = new int[fc];
            }
        }

        /**
         * 从文件的自描述信息解析出版本档次与字段名。
         * <p>
         * 两个答案都只来自文件自己声明的内容，优先级在 8 种 SDK 中完全一致（FORMAT §10.3）：
         * <pre>
         *   edition      1. GROUP_SCHEMA.groupId / Header.VersionMask 的 one-hot 位
         *                2. Metadata primary_version，或只有一项的 version_list
         *                3. 字段数唯一匹配（兜底）
         *                4. ""（unknown）—— 判定不出就不臆造
         *   fieldNames   1. Metadata field_names（基数与该组一致时）
         *                2. 已知档次且基数一致时用规范表
         *                3. field_0..field_N-1 占位符
         * </pre>
         * {@link QzdbReader#getEditionSource()} / {@link QzdbReader#getFieldNamesSource()}
         * 报告命中了哪条规则，调用方可据此区分「文件里读出来的」与「SDK 补出来的」。
         */
        private void parseMetadata() throws QzdbException {
            String metaVersion = "";
            String metaDesc = "";
            String metaPrimary = "";
            String[] metaFields = null;
            if ((flags & 4) != 0 && offMeta > 0 && offMeta + 4 <= dataLen) {
                int cursor = (int) offMeta;
                while (cursor + 4 <= dataLen) {
                    int type = data.get(cursor) & 0xFF;
                    int length = readU16(data, cursor + 2);
                    if (type == 0 || length == 0) break;
                    if (cursor + 4L + length > dataLen) break;
                    String val = readUtf8(data, cursor + 4, length);
                    switch (type) {
                        case 1 -> metaVersion = val;
                        case 2 -> metaFields = splitFieldNames(val);
                        case 3 -> metaDesc = val;
                        case 4 -> metaPrimary = val;
                        default -> { /* 未知 type 按设计跳过（FORMAT §8.1） */ }
                    }
                    cursor += 4 + length;
                }
            }
            this.version = metaVersion;
            this.description = metaDesc;

            // --- 逐组解析 edition + 字段名 ---------------------------------------
            groupFieldNames = new String[actualGroups][];
            String[] groupEditions = new String[actualGroups];
            String[] groupEditionSources = new String[actualGroups];
            String[] groupNameSources = new String[actualGroups];

            for (int g = 0; g < actualGroups; g++) {
                int nFields = groupFieldCounts[g];

                // edition：先用本组自己的掩码，再回落到文件级掩码
                int mask = groupIds[g] != 0 ? groupIds[g] : versionMask;
                String edition = editionFromMask(mask);
                String source = EDITION_SOURCE_VERSION_MASK;
                if (edition.isEmpty()) {
                    edition = metaPrimary.trim();
                    if (edition.isEmpty()) {
                        String single = singleVersionToken(metaVersion);
                        if (single != null) edition = single;
                    }
                    if (!edition.isEmpty()) source = EDITION_SOURCE_METADATA;
                }
                if (edition.isEmpty()) {
                    String byCount = EDITION_BY_FIELD_COUNT.get(nFields);
                    edition = byCount == null ? "" : byCount;
                    source = edition.isEmpty() ? EDITION_SOURCE_UNKNOWN : EDITION_SOURCE_INFERRED;
                }

                // 字段名
                String[] names;
                String namesSource;
                if (metaFields != null && metaFields.length == nFields) {
                    names = metaFields.clone();
                    namesSource = FIELD_NAMES_SOURCE_METADATA;
                } else {
                    String[] canonical = EDITION_FIELD_NAMES.get(edition);
                    if (canonical != null && canonical.length == nFields) {
                        names = canonical.clone();
                        namesSource = FIELD_NAMES_SOURCE_EDITION;
                    } else {
                        names = new String[nFields];
                        for (int i = 0; i < nFields; i++) names[i] = "field_" + i;
                        namesSource = FIELD_NAMES_SOURCE_SYNTHETIC;
                    }
                }

                groupFieldNames[g] = names;
                groupEditions[g] = edition;
                groupEditionSources[g] = source;
                groupNameSources[g] = namesSource;
            }

            int gi = (groupIndex >= 0 && groupIndex < actualGroups) ? groupIndex : 0;
            this.fieldNames = groupFieldNames[gi];
            this.edition = groupEditions[gi];
            this.editionSource = groupEditionSources[gi];
            this.fieldNamesSource = groupNameSources[gi];
            this.normalizedFieldMap = GeoInfo.buildNormalizedMap(this.fieldNames);
            this.numericFieldFlags = new boolean[fieldNames.length];
            for (int i = 0; i < fieldNames.length; i++) {
                numericFieldFlags[i] = GeoInfo.isNumericFieldName(fieldNames[i]);
            }

            repairDimMasks();

            if (buildDate > 0) {
                int y = buildDate / 10000;
                int m = (buildDate / 100) % 100;
                int dd = buildDate % 100;
                this.dataMonth = String.format(Locale.US, "%04d-%02d", y, m);
                this.buildTimeStr = String.format(Locale.US, "%04d-%02d-%02d", y, m, dd);
            } else {
                this.dataMonth = "";
                this.buildTimeStr = "";
            }

            this.scope = "";
        }

        /**
         * dimensionMask 缺失时的重建（§5.4 / §6.2）。
         * <p>
         * 现行格式的合法文件在 GroupMetadataTable 里一定存了非 0 的 dimensionMask，所以这里通常什么都不做。
         * 某组的 mask 为 0（畸形/历史文件）时，只从该组<em>解析出来的字段名</em>推导——
         * 绝不看 fieldId（那只是槽位序号 0..N-1，不带跨档语义），也绝不看组下标
         * （真实的 asn 库把 asn 组放在 GroupMetadataTable 下标 0 且存了 0x02，按下标判定必错）。
         */
        private void repairDimMasks() {
            final String asnKey = GeoInfo.normalizeKey("asn");
            for (int g = 0; g < actualGroups; g++) {
                if (groupDimMasks[g] != 0) continue;
                boolean hasAsn = false;
                String[] names = groupFieldNames[g];
                if (names != null) {
                    for (String fn : names) {
                        if (asnKey.equals(GeoInfo.normalizeKey(fn))) {
                            hasAsn = true;
                            break;
                        }
                    }
                }
                groupDimMasks[g] = hasAsn ? 0x02 : 0x01;
            }
        }

        /** version_list 只有一项时返回该项，否则返回 null（多项无法定位单一档次）。 */
        private static String singleVersionToken(String versionList) {
            if (versionList == null || versionList.isEmpty()) return null;
            String only = null;
            for (String part : versionList.split(",")) {
                String t = part.trim();
                if (t.isEmpty()) continue;
                if (only != null) return null; // 超过一项
                only = t;
            }
            return only;
        }



        /**
         * 区段边界校验（Fail-Closed）。
         * off 源自 Header 的 u64，攻击者可控：负值（u64 高位置 1 后转 long）与 off+size 溢出都必须先行拒绝，
         * 因此这里使用溢出安全形式 {@code size > dataLen - off} 而非 {@code off + size > dataLen}。
         */
        private void checkSection(String name, long off, long size) throws QzdbException {
            if (off < 0 || size < 0 || (off > 0 && (off > dataLen || size > dataLen - off))) {
                throw new QzdbException(ErrorCode.CORRUPTED,
                        "Section " + name + " out of bounds: off=" + off + " size=" + size + " fileLen=" + dataLen);
            }
        }

        private static String[] splitFieldNames(String raw) {
            String s = raw.trim();
            if (s.isEmpty()) return null;
            String[] parts = (s.indexOf('|') >= 0 ? s.split("\\|") : s.split(","));
            for (int i = 0; i < parts.length; i++) parts[i] = parts[i].trim();
            return parts;
        }

        private String[][][] parsePools() {
            String[][][] result = new String[actualGroups][][];
            if (offPools <= 0) {
                for (int g = 0; g < actualGroups; g++) {
                    result[g] = new String[groupFieldCounts[g]][];
                    for (int f = 0; f < groupFieldCounts[g]; f++) result[g][f] = new String[0];
                }
                return result;
            }
            long poolCursor = offPools;
            long poolEnd = offMeta > 0 ? offMeta : dataLen;

            for (int g = 0; g < actualGroups; g++) {
                int fieldCount = groupFieldCounts[g];
                String[][] groupPoolList = new String[fieldCount][];
                boolean[] natives = groupFieldNative[g];

                for (int f = 0; f < fieldCount; f++) {
                    if (natives != null && f < natives.length && natives[f]) {
                        groupPoolList[f] = new String[0]; // 原生标量字段无池（§6.6）
                        continue;
                    }
                    if (poolCursor + 4 > poolEnd) {
                        groupPoolList[f] = new String[0];
                        continue;
                    }
                    long count = readU32(data, (int) poolCursor) & 0xFFFFFFFFL;
                    poolCursor += 4;
                    if (offRowSchema > 0) {
                        poolCursor += 4; // poolSizeBytes（当前构建器在含 ROW_SCHEMA 的 v1 文件中写入）
                    }
                    if (count <= 0 || count > MAX_POOL_COUNT) {
                        groupPoolList[f] = new String[0];
                        continue;
                    }
                    int cnt = (int) count;
                    long offsetsStart = poolCursor;
                    long stringDataStart = poolCursor + (count + 1) * 4;
                    if (stringDataStart > poolEnd) {
                        groupPoolList[f] = new String[0];
                        continue;
                    }

                    // 偏移表是"累积"结构：offsets[i+1] >= offsets[i]，末项 tail 即字符串区总字节数。
                    // 必须显式强制单调不减 —— 否则伪造文件可让每个 offset 段都横跨整个区域，
                    // count 个 × 区域长度的解码量会放大成 GB 级分配（实测 13.9 万项 × 11 MB ≈ 7.2 GB → OOM）。
                    // 有了 start >= prevEnd && end <= tail，各段互不重叠且落在 [0, tail]，总解码量必 <= tail <= avail。
                    long avail = dataLen - stringDataStart;
                    long tail = readU32(data, (int) (offsetsStart + count * 4)) & 0xFFFFFFFFL;
                    if (tail > avail) {
                        groupPoolList[f] = new String[0];
                        poolCursor = stringDataStart;
                        continue;
                    }

                    String[] strings = new String[cnt];
                    long prevEnd = 0;
                    for (int i = 0; i < cnt; i++) {
                        long start = readU32(data, (int) (offsetsStart + (long) i * 4)) & 0xFFFFFFFFL;
                        long end = readU32(data, (int) (offsetsStart + (long) (i + 1) * 4)) & 0xFFFFFFFFL;
                        if (start < prevEnd || end < start || end > tail) {
                            strings[i] = "";
                            continue;
                        }
                        prevEnd = end;
                        strings[i] = (end == start) ? "" : readUtf8(data, (int) (stringDataStart + start), (int) (end - start));
                    }
                    groupPoolList[f] = strings;
                    poolCursor = stringDataStart + tail;
                }
                result[g] = groupPoolList;
            }
            return result;
        }

        /**
         * 按 entryId 解包一行 GeoEntry。fieldFilter 为 null 时返回全字段（共享快照级归一化索引，零额外哈希构建）。
         */
        GeoInfo extractGeoInfo(int entryId, String[] fieldFilter) {
            if (entryId <= 0 || entryId >= groupEntryCounts[groupIndex]) {
                return null;
            }
            final int gi = groupIndex;
            final int fc = groupFieldCounts[gi];
            final long entryOff = groupEntryOffsets[gi] + (long) entryId * groupStrides[gi];
            if (entryOff < 0 || entryOff + groupStrides[gi] > dataLen) {
                return null; // 越界防护（损坏文件 fail-safe）
            }

            final int[] widths = groupFieldWidths[gi];
            final int[] offsets = groupFieldOffsets[gi];
            final boolean[] natives = groupFieldNative[gi];
            final int[] natTypes = groupFieldNativeType[gi];
            final String[][] groupPoolList = pools[gi];

            if (fieldFilter == null) {
                String[] values = new String[fc];
                for (int fi = 0; fi < fc; fi++) {
                    values[fi] = readFieldValue(entryOff, fi, widths, offsets, natives, natTypes, groupPoolList);
                }
                return new GeoInfo(fieldNames, values, normalizedFieldMap, numericFieldFlags);
            }

            // 字段投影模式（§9.6：未知字段补空串，不抛异常）
            String[] values = new String[fieldFilter.length];
            for (int i = 0; i < fieldFilter.length; i++) {
                Integer origIdx = normalizedFieldMap.get(GeoInfo.normalizeKey(fieldFilter[i]));
                if (origIdx == null || origIdx >= fc) {
                    values[i] = "";
                } else {
                    values[i] = readFieldValue(entryOff, origIdx, widths, offsets, natives, natTypes, groupPoolList);
                }
            }
            return new GeoInfo(fieldFilter, values);
        }

        private String readFieldValue(long entryOff, int fi, int[] widths, int[] offsets,
                                      boolean[] natives, int[] natTypes, String[][] groupPoolList) {
            int w = (fi < widths.length) ? widths[fi] : poolIdxSize;
            int fo = (fi < offsets.length) ? offsets[fi] : fi * poolIdxSize;
            if (fi < natives.length && natives[fi]) {
                int nt = fi < natTypes.length ? natTypes[fi] : 0;
                return readNativeValue(entryOff + fo, w, nt);
            }
            // fo 源自 GROUP_SCHEMA 的 u32，可能超出 stride/文件尾；查询期以空串降级，
            // 不让损坏文件把异常抛出 find() 之外（与 readNativeValue 的 fail-safe 语义保持一致）。
            long valPos = entryOff + fo;
            int need = w <= 1 ? 1 : (w == 2 ? 2 : (w == 3 ? 3 : 4));
            if (valPos < 0 || valPos > dataLen - need) {
                return "";
            }
            long valIdx = readUintWidthUnsigned(data, (int) valPos, w);
            if (fi < groupPoolList.length) {
                String[] pool = groupPoolList[fi];
                if (pool != null && valIdx < pool.length) {
                    return pool[(int) valIdx];
                }
            }
            return "";
        }

        /** 原生标量字段解码（§6.6 / §10.5）：int 原样；float 按 6 位小数格式化（FORMAT 规范跨语言一致）。 */
        private String readNativeValue(long off, int fw, int nt) {
            int iOff = (int) off;
            if (iOff < 0 || iOff + fw > dataLen) return "";
            if (nt == 1) {
                if (fw == 4) {
                    float f = Float.intBitsToFloat(readU32(data, iOff));
                    if (Float.isNaN(f) || Float.isInfinite(f)) return "";
                    if (f == Math.floor(f)) return Long.toString((long) f);
                    return FLOAT6.get().format(f);
                } else if (fw == 8) {
                    double dVal = Double.longBitsToDouble(readU64(data, iOff));
                    if (Double.isNaN(dVal) || Double.isInfinite(dVal)) return "";
                    if (dVal == Math.floor(dVal)) return Long.toString((long) dVal);
                    return FLOAT6.get().format(dVal);
                }
            }
            long valNum = readUintWidth(data, iOff, fw) & 0xFFFFFFFFL;
            return String.valueOf(valNum);
        }

        String fileHashHex() {
            Long crc = canonicalCrc;
            if (crc == null) {
                synchronized (this) {
                    crc = canonicalCrc;
                    if (crc == null) {
                        crc = computeCanonicalCrc(data);
                        canonicalCrc = crc;
                    }
                }
            }
            return String.format(Locale.US, "%08x", crc);
        }

        boolean verifyCrcNow() {
            return computeCanonicalCrc(data) == storedCrc;
        }
    }

    private final AtomicReference<Snapshot> activeSnapshot = new AtomicReference<>();
    private final File loadedFile;

    /**
     * QzdbReader 构建器
     */
    /**
     * QzdbReader 构建器。使用方式：
     * <pre>{@code
     *   QzdbReader reader = new QzdbReader.Builder(new File("ip.qzdb"))
     *       .verifyCrc(true)
     *       .groupIndex(0)
     *       .build();
     * }</pre>
     */
    public static class Builder {
        private File databaseFile;
        private byte[] bufferData;
        private int groupIndex = 0;
        private boolean verifyCrc = true;

        /** @param database 数据库文件路径 */
        public Builder(File database) {
            this.databaseFile = database;
        }

        /**
         * @param buffer 数据库字节（拷贝语义：内部 clone 传入数组，调用方可自由修改/释放原数组）
         */
        public Builder(byte[] buffer) {
            this.bufferData = buffer != null ? buffer.clone() : new byte[0];
        }

        /** @param stream 数据库输入流（读取全部字节） */
        public Builder(InputStream stream) throws IOException {
            this.bufferData = stream.readAllBytes();
        }

        /**
         * @param idx 版本组索引（0=主版本组，2=ASN 组等）
         * @return this（链式调用）
         */
        public Builder groupIndex(int idx) {
            this.groupIndex = idx;
            return this;
        }

        /**
         * @param enabled 是否开启 CRC32 校验（默认 true；仅 open 可关，reload 强制开启）
         * @return this（链式调用）
         */
        public Builder verifyCrc(boolean enabled) {
            this.verifyCrc = enabled;
            return this;
        }

        /**
         * 构建 QzdbReader 实例。
         *
         * @return 构建好的 QzdbReader
         * @throws QzdbException 文件不存在/CRC 失败/格式错误时抛出
         */
        public QzdbReader build() throws QzdbException {
            ByteBuffer buffer;
            File fileRef = databaseFile;

            if (databaseFile != null) {
                if (!databaseFile.exists() || !databaseFile.canRead()) {
                    throw new QzdbException(ErrorCode.FILE_NOT_FOUND,
                            "Database file does not exist or is not readable: " + databaseFile.getAbsolutePath());
                }
                try (RandomAccessFile raf = new RandomAccessFile(databaseFile, "r");
                     FileChannel ch = raf.getChannel()) {
                    long size = ch.size();
                    if (size > Integer.MAX_VALUE) {
                        throw new QzdbException(ErrorCode.INVALID_PARAM,
                                "Database file too large for single mapped buffer: " + size + " bytes");
                    }
                    buffer = ch.map(FileChannel.MapMode.READ_ONLY, 0, size);
                } catch (IOException e) {
                    throw new QzdbException(ErrorCode.FILE_NOT_FOUND,
                            "Failed to read database file: " + databaseFile.getAbsolutePath(), e);
                }
            } else if (bufferData != null) {
                buffer = ByteBuffer.wrap(bufferData);
            } else {
                throw new QzdbException(ErrorCode.INVALID_PARAM, "Neither database file nor buffer was provided");
            }

            Snapshot snapshot = new Snapshot(buffer, groupIndex, verifyCrc);
            QzdbReader reader = new QzdbReader(fileRef);
            reader.activeSnapshot.set(snapshot);
            return reader;
        }
    }

    private QzdbReader(File file) {
        this.loadedFile = file;
    }

    private Snapshot requireSnapshot() {
        Snapshot s = activeSnapshot.get();
        if (s == null) {
            throw new IllegalStateException("QzdbReader is closed");
        }
        return s;
    }

    // =========================================================================
    // 单条查询 API（所有入口共享同一套地址规范化与解析路径，见 SDK 规范 §5.3）
    // =========================================================================

    /**
     * 查询 IP 地址的地理信息。
     *
     * @param ipStr IP 地址字符串（IPv4 或 IPv6，支持 IPv4-mapped IPv6 自动降级）
     * @return 查询结果；未找到返回 {@link Optional#empty()}
     * @throws QzdbException IP 格式非法时抛出（错误码 {@link ErrorCode#INVALID_IP}）
     */
    public Optional<GeoInfo> find(String ipStr) {
        return findInternal(ipStr, null);
    }

    /**
     * 查询 {@link InetAddress} 的地理信息。
     *
     * @param addr InetAddress 实例（不可为 null）
     * @return 查询结果；未找到返回 {@link Optional#empty()}
     * @throws QzdbException addr 为 null 或 IP 格式非法时抛出
     */
    public Optional<GeoInfo> find(InetAddress addr) {
        if (addr == null) {
            throw new QzdbException(ErrorCode.INVALID_IP, "InetAddress cannot be null");
        }
        Snapshot snap = requireSnapshot();
        byte[] raw = addr.getAddress();
        int rowId = lookupRowIdFromBytes(snap, raw);
        return resolveRow(snap, rowId, null);
    }

    /**
     * 查询 IPv4 uint32 的地理信息。
     *
     * @param ipInt IPv4 地址的 uint32 值（如 0x01020304 = 1.2.3.4）
     * @return 查询结果；未找到返回 {@link Optional#empty()}
     */
    public Optional<GeoInfo> findUint(int ipInt) {
        Snapshot snap = requireSnapshot();
        int rowId = lookupRowIdUintInternal(snap, ipInt);
        return resolveRow(snap, rowId, null);
    }

    /**
     * 查询 16 字节 IP 地址（IPv6 或 IPv4-mapped IPv6）的地理信息。
     *
     * @param ip16 16 字节网络序地址；前 10 字节为 0 且第 10-11 字节为 0xFF 时按 IPv4-mapped 降级
     * @return 查询结果；未找到返回 {@link Optional#empty()}
     * @throws QzdbException 数组为 null 或长度非法时抛出
     */
    public Optional<GeoInfo> findBytes(byte[] ip16) {
        if (ip16 == null) {
            throw new QzdbException(ErrorCode.INVALID_IP, "IP bytes array cannot be null");
        }
        Snapshot snap = requireSnapshot();
        int rowId = lookupRowIdFromBytes(snap, ip16);
        if (rowId < 0) {
            throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IP byte array length: " + ip16.length);
        }
        return resolveRow(snap, rowId, null);
    }

    /**
     * 字段投影查询：只解析指定字段，减少不必要的池读取开销。
     *
     * @param ipStr  IP 地址字符串
     * @param fields 要查询的字段名（归一化匹配，大小写/下划线不敏感）；null 或空数组等价于 {@link #find(String)}
     * @return 查询结果（GeoInfo 仅包含 fields 指定的字段）；未找到返回 {@link Optional#empty()}
     * @throws QzdbException IP 格式非法时抛出
     */
    public Optional<GeoInfo> findFields(String ipStr, String[] fields) {
        if (fields == null || fields.length == 0) {
            return find(ipStr);
        }
        return findInternal(ipStr, fields);
    }

    /**
     * 查询并返回 pipe 分隔的结果字符串（格式：field1|field2|...|fieldN）。
     *
     * @param ipStr IP 地址字符串
     * @return pipe 分隔结果；未找到返回空字符串 ""
     * @throws QzdbException IP 格式非法时抛出
     */
    public String findStr(String ipStr) {
        Optional<GeoInfo> info = find(ipStr);
        return info.map(GeoInfo::toPipeString).orElse("");
    }

    private Optional<GeoInfo> findInternal(String ipStr, String[] fieldFilter) {
        if (ipStr == null || ipStr.isEmpty()) {
            throw new QzdbException(ErrorCode.INVALID_IP, "IP address string cannot be empty");
        }
        String ip = ipStr.trim();
        if (ip.isEmpty()) {
            throw new QzdbException(ErrorCode.INVALID_IP, "IP address string cannot be empty");
        }
        Snapshot snap = requireSnapshot();

        int rowId;
        if (ip.indexOf(':') >= 0) {
            byte[] bytes = parseIPv6Bytes(ip);
            rowId = isV4MappedBytes(bytes)
                    ? lookupRowIdUintInternal(snap, v4FromMappedBytes(bytes))
                    : lookupV6Bytes(snap, bytes);
        } else {
            rowId = lookupRowIdUintInternal(snap, parseIPv4Uint(ip));
        }
        return resolveRow(snap, rowId, fieldFilter);
    }

    /** rowId → IPRow → dimensionMask 选维 → GeoEntry 解包（所有 find* 变体的唯一汇聚路径）。 */
    private static Optional<GeoInfo> resolveRow(Snapshot s, int rowId, String[] fieldFilter) {
        if (rowId <= 0) {
            return Optional.empty();
        }
        long rOff = s.offIPRow + (long) rowId * s.ipRowSize;
        if (rowId >= s.rowCount || rOff + s.ipRowSize > s.dataLen) {
            return Optional.empty();
        }
        int geoId = readUintWidth(s.data, (int) rOff, s.rowGeoWidth);
        int asnId = s.rowAsnWidth > 0 ? readUintWidth(s.data, (int) (rOff + s.rowGeoWidth), s.rowAsnWidth) : 0;
        int usageId = s.rowUsageWidth > 0
                ? readUintWidth(s.data, (int) (rOff + s.rowGeoWidth + s.rowAsnWidth), s.rowUsageWidth) : 0;

        int dimMask = s.groupDimMasks[s.groupIndex];
        int entryId;
        if ((dimMask & 0x02) != 0) {
            entryId = asnId;
        } else if ((dimMask & 0x04) != 0) {
            entryId = usageId;
        } else {
            entryId = geoId;
        }
        if (entryId <= 0) {
            return Optional.empty();
        }
        return Optional.ofNullable(s.extractGeoInfo(entryId, fieldFilter));
    }

    // =========================================================================
    // 批量与流式 API（顺序执行，逐条保留三态语义，SDK 规范 §8）
    // =========================================================================

    /**
     * 批量顺序查询（SDK 内部不起线程池）。输入输出数组等长一一对应，逐条保留三态语义。
     *
     * @param ips IP 地址列表；null 返回空列表
     * @return 与输入等长的 {@link BatchResult} 列表
     */
    public List<BatchResult> findBatch(List<String> ips) {
        if (ips == null) return java.util.Collections.emptyList();
        List<BatchResult> results = new ArrayList<>(ips.size());
        for (String ip : ips) {
            try {
                Optional<GeoInfo> info = find(ip);
                results.add(new BatchResult(ip, info, null));
            } catch (QzdbException e) {
                results.add(new BatchResult(ip, Optional.empty(), e));
            }
        }
        return results;
    }

    /**
     * 批量字段投影查询。
     *
     * @param ips    IP 地址列表；null 返回空列表
     * @param fields 要查询的字段名（归一化匹配）
     * @return 与输入等长的 {@link BatchResult} 列表
     */
    public List<BatchResult> findBatchFields(List<String> ips, String[] fields) {
        if (ips == null) return java.util.Collections.emptyList();
        List<BatchResult> results = new ArrayList<>(ips.size());
        for (String ip : ips) {
            try {
                Optional<GeoInfo> info = findFields(ip, fields);
                results.add(new BatchResult(ip, info, null));
            } catch (QzdbException e) {
                results.add(new BatchResult(ip, Optional.empty(), e));
            }
        }
        return results;
    }

    /**
     * 流式惰性查询（Stream 惰性求值，不累积结果，内存占用恒定）。
     *
     * @param ips IP 地址流；null 返回空流
     * @return 惰性求值的 {@link BatchResult} 流
     */
    public Stream<BatchResult> findStream(Stream<String> ips) {
        if (ips == null) return Stream.empty();
        return ips.map(ip -> {
            try {
                Optional<GeoInfo> info = find(ip);
                return new BatchResult(ip, info, null);
            } catch (QzdbException e) {
                return new BatchResult(ip, Optional.empty(), e);
            }
        });
    }

    // =========================================================================
    // 低级行号 API
    // =========================================================================

    /**
     * 低级查询：只走 Trie 获取 row_id，不解包 GeoEntry。
     *
     * @param ipStr IP 地址字符串；null/空/非法格式返回 0
     * @return row_id（0 表示未找到或输入非法）
     */
    public int lookupRowId(String ipStr) {
        if (ipStr == null) return 0;
        String ip = ipStr.trim();
        if (ip.isEmpty()) return 0;
        Snapshot snap = requireSnapshot();
        try {
            if (ip.indexOf(':') >= 0) {
                byte[] bytes = parseIPv6Bytes(ip);
                return isV4MappedBytes(bytes)
                        ? lookupRowIdUintInternal(snap, v4FromMappedBytes(bytes))
                        : lookupV6Bytes(snap, bytes);
            }
            return lookupRowIdUintInternal(snap, parseIPv4Uint(ip));
        } catch (QzdbException e) {
            return 0;
        }
    }

    /**
     * 低级查询（IPv4 uint32 入口）。
     *
     * @param ipInt IPv4 地址的 uint32 值
     * @return row_id（0 表示未找到）
     */
    public int lookupRowIdUint(int ipInt) {
        return lookupRowIdUintInternal(requireSnapshot(), ipInt);
    }

    /**
     * 低级查询（字节数组入口）。
     *
     * @param ipBytes 4 字节 (IPv4) 或 16 字节 (IPv6/mapped)；null 返回 0
     * @return row_id（0 表示未找到或输入非法）
     */
    public int lookupRowIdBytes(byte[] ipBytes) {
        if (ipBytes == null) return 0;
        Snapshot snap = requireSnapshot();
        int rowId = lookupRowIdFromBytes(snap, ipBytes);
        return rowId < 0 ? 0 : rowId;
    }

    /**
     * 从 row_id 解包 IPRow 获取各维度 ID。
     *
     * @param rowId 行号（> 0 且 < rowCount）
     * @return RowIds 具名结构体；越界返回 null
     */
    public RowIds lookupIds(int rowId) {
        Snapshot s = requireSnapshot();
        if (rowId <= 0 || rowId >= s.rowCount) return null;
        long rOff = s.offIPRow + (long) rowId * s.ipRowSize;
        if (rOff + s.ipRowSize > s.dataLen) return null;
        int geoId = readUintWidth(s.data, (int) rOff, s.rowGeoWidth);
        int asnId = s.rowAsnWidth > 0 ? readUintWidth(s.data, (int) (rOff + s.rowGeoWidth), s.rowAsnWidth) : 0;
        int usageId = s.rowUsageWidth > 0
                ? readUintWidth(s.data, (int) (rOff + s.rowGeoWidth + s.rowAsnWidth), s.rowUsageWidth) : 0;
        return new RowIds(geoId, asnId, usageId);
    }

    // =========================================================================
    // CIDR 反查 API（由 Trie 匹配深度重建，数据库本身不存储 CIDR）
    // =========================================================================

    /**
     * 返回包含该 IP 的最具体网段的标准 CIDR（如 "1.0.1.0/24"、"2001:218::/32"）。
     * <p>
     * 原理：QZDB Trie 每个叶子对应构建时的一条 CIDR 记录，叶子深度 = 前缀长度 N；
     * 将 IP 的高 N 位保留、主机位清零即得网络地址（FORMAT §4.3 单比特步进，无跳层压缩）。
     * Jump Table 直接命中叶子时前 16/JumpBits 位内的深度信息被压缩，内部自动从根补走重建。
     * <p>
     * IPv4-mapped IPv6 按规范 §9.7 剥离后走 V4 Trie，返回 V4 CIDR。
     * 语义（QZDB_TEST_SPECIFICATION.md Tier 1 §7）：未命中返回 null；非法 IP 抛 INVALID_IP。
     *
     * @param ipStr IP 地址字符串（IPv4/IPv6/mapped）
     * @return 最具体网段的标准 CIDR；未覆盖返回 null
     * @throws QzdbException IP 格式非法时抛出（错误码 {@link ErrorCode#INVALID_IP}）
     */
    public String lookupCidr(String ipStr) {
        if (ipStr == null || ipStr.isEmpty()) {
            throw new QzdbException(ErrorCode.INVALID_IP, "IP address string cannot be empty");
        }
        String ip = ipStr.trim();
        if (ip.isEmpty()) {
            throw new QzdbException(ErrorCode.INVALID_IP, "IP address string cannot be empty");
        }
        Snapshot snap = requireSnapshot();
        if (ip.indexOf(':') >= 0) {
            byte[] bytes = parseIPv6Bytes(ip);
            if (isV4MappedBytes(bytes)) {
                int v4 = v4FromMappedBytes(bytes);
                int n = lookupV4PrefixLen(snap, v4);
                return n < 0 ? null : formatV4Cidr(v4, n);
            }
            int n = lookupV6PrefixLen(snap, bytes);
            return n < 0 ? null : formatV6Cidr(bytes, n);
        }
        int v4 = parseIPv4Uint(ip);
        int n = lookupV4PrefixLen(snap, v4);
        return n < 0 ? null : formatV4Cidr(v4, n);
    }

    /** IPv4 数值入口的 CIDR 反查。未覆盖返回 null。 */
    public String lookupCidrUint(int ipInt) {
        Snapshot snap = requireSnapshot();
        int n = lookupV4PrefixLen(snap, ipInt);
        return n < 0 ? null : formatV4Cidr(ipInt, n);
    }

    /**
     * 字节数组入口的 CIDR 反查（4 字节 = V4，16 字节 = V6/mapped）。
     * 未覆盖返回 null；字节数组为 null 或长度非法（非 4/16）抛 INVALID_IP。
     *
     * @param ipBytes 4 字节 (IPv4) 或 16 字节 (IPv6/mapped)
     * @return 最具体网段的标准 CIDR；未覆盖返回 null
     * @throws QzdbException 字节数组为 null 或长度非法时抛出（{@link ErrorCode#INVALID_IP}）
     */
    public String lookupCidrBytes(byte[] ipBytes) {
        if (ipBytes == null || (ipBytes.length != 4 && ipBytes.length != 16)) {
            throw new QzdbException(ErrorCode.INVALID_IP,
                    "Invalid IP byte array length: " + (ipBytes == null ? "null" : ipBytes.length));
        }
        Snapshot snap = requireSnapshot();
        if (ipBytes.length == 16) {
            if (isV4MappedBytes(ipBytes)) {
                int v4 = v4FromMappedBytes(ipBytes);
                int n = lookupV4PrefixLen(snap, v4);
                return n < 0 ? null : formatV4Cidr(v4, n);
            }
            int n = lookupV6PrefixLen(snap, ipBytes);
            return n < 0 ? null : formatV6Cidr(ipBytes, n);
        }
        int v4 = ((ipBytes[0] & 0xFF) << 24) | ((ipBytes[1] & 0xFF) << 16)
                | ((ipBytes[2] & 0xFF) << 8) | (ipBytes[3] & 0xFF);
        int n = lookupV4PrefixLen(snap, v4);
        return n < 0 ? null : formatV4Cidr(v4, n);
    }

    // =========================================================================
    // 热更新 reload API（影子对象 + CRC 强制 + 原子替换，SDK 规范 §4.3）
    // =========================================================================

    /**
     * 热替换正在服务的数据文件。构建完整新快照后原子替换引用，旧数据在替换失败时保持不变。
     * <p>
     * 强制 CRC 校验（不可关闭），校验失败抛异常且旧数据不动。
     *
     * @param path 新数据库文件路径
     * @throws QzdbException 文件不存在/CRC 失败/格式错误时抛出
     */
    public void reload(String path) throws QzdbException {
        File file = new File(path);
        if (!file.exists() || !file.canRead()) {
            throw new QzdbException(ErrorCode.FILE_NOT_FOUND, "Reload file does not exist: " + path);
        }
        try (RandomAccessFile raf = new RandomAccessFile(file, "r");
             FileChannel ch = raf.getChannel()) {
            long size = ch.size();
            if (size > Integer.MAX_VALUE) {
                throw new QzdbException(ErrorCode.INVALID_PARAM, "Reload file too large: " + size + " bytes");
            }
            MappedByteBuffer buffer = ch.map(FileChannel.MapMode.READ_ONLY, 0, size);
            Snapshot newSnap = new Snapshot(buffer, requireSnapshot().groupIndex, true); // reload 强制 CRC
            activeSnapshot.set(newSnap);
        } catch (IOException e) {
            throw new QzdbException(ErrorCode.FILE_NOT_FOUND, "Failed to read reload file: " + path, e);
        }
    }

    /**
     * 热替换正在服务的数据字节（拷贝语义：内部 clone 传入的 buffer）。
     *
     * @param buffer 新数据库字节；不可为 null 或空
     * @throws QzdbException buffer 为空/CRC 失败/格式错误时抛出
     */
    public void reloadBuffer(byte[] buffer) throws QzdbException {
        if (buffer == null || buffer.length == 0) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "Reload buffer cannot be null or empty");
        }
        ByteBuffer wrap = ByteBuffer.wrap(buffer.clone()); // 拷贝保护
        Snapshot newSnap = new Snapshot(wrap, requireSnapshot().groupIndex, true);
        activeSnapshot.set(newSnap);
    }

    /**
     * 释放 mmap/文件句柄/内存引用。幂等操作；关闭后任何查询/自省 API 抛 {@link IllegalStateException}。
     */
    @Override
    public void close() {
        activeSnapshot.set(null);
    }

    // =========================================================================
    // 元信息自省 API
    // =========================================================================

    /** @return Metadata type=1 版本列表；无 Metadata 返回 "" */
    public String getVersion() { return requireSnapshot().version; }

    /** @return 数据期号 "yyyy-MM"（由 Header BuildDate 推算）；无则 "" */
    public String getDataMonth() { return requireSnapshot().dataMonth; }

    /**
     * @return 版本档次 "std"|"pro"|"asn"|"max"|"ult"；判定不出时返回 ""（不臆造）。
     *         判定优先级见 FORMAT §10.3：groupId/VersionMask → Metadata → 字段数唯一匹配。
     *         用 {@link #getEditionSource()} 可以知道命中了哪条规则。
     */
    public String getEdition() { return requireSnapshot().edition; }

    /**
     * @return 文件级 one-hot 档次位掩码（Header 偏移 6）。
     *         bit0=std(1) bit1=asn(2) bit2=pro(4) bit3=max(8) bit4=ult(16)。
     */
    public int getVersionMask() { return requireSnapshot().versionMask; }

    /**
     * @return {@link #getEdition()} 的判定依据：
     *         {@code version_mask} | {@code metadata} | {@code inferred} | {@code unknown}
     */
    public String getEditionSource() { return requireSnapshot().editionSource; }

    /**
     * @return {@link #getFieldNames()} 的来源：
     *         {@code metadata}（文件自带） | {@code edition}（规范表补全） | {@code synthetic}（占位符）
     */
    public String getFieldNamesSource() { return requireSnapshot().fieldNamesSource; }

    /** @return 地域覆盖（当前格式 Header 尚无该字段，始终返回 ""） */
    public String getScope() { return requireSnapshot().scope; }

    /** @return 构建日期 "yyyy-MM-dd"（由 Header BuildDate 推算）；无则 "" */
    public String getBuildTime() { return requireSnapshot().buildTimeStr; }

    /** @return Metadata type=3 描述；无 Metadata 返回 "" */
    public String getDescription() { return requireSnapshot().description; }

    /** @return 文件 CRC32 十六进制字符串（8 位小写） */
    public String getFileHash() { return requireSnapshot().fileHashHex(); }

    /** @return 当前版本组的字段名数组（克隆副本，修改不影响内部状态） */
    public String[] getFieldNames() { return requireSnapshot().fieldNames.clone(); }

    /**
     * 判断当前版本组是否包含指定字段（归一化匹配，大小写/下划线不敏感）。
     *
     * @param name 字段名（如 "country_en"、"countryEn"、"COUNTRY_EN" 等价）
     * @return 是否包含该字段
     */
    public boolean hasField(String name) {
        return requireSnapshot().normalizedFieldMap.containsKey(GeoInfo.normalizeKey(name));
    }

    /** @return 重新计算全文件 CRC32 并与 Header 存储值比对（只读操作，不影响快照） */
    public boolean verifyCrc() {
        return requireSnapshot().verifyCrcNow();
    }

    /** @return 文件中包含的版本组数量（1~4） */
    public int getGroupCount() { return requireSnapshot().actualGroups; }

    /** @return 主版本组（group 0）的维度数（=字段数） */
    public int getPoolCount() { return requireSnapshot().poolCount; }

    // =========================================================================
    // IP 解析（严格、无 DNS、零外部依赖；所有入口共享，SDK 规范 §5.3）
    // =========================================================================

    /** 严格 IPv4 点分十进制解析：4 段、纯数字、拒绝前导 0、值 ≤ 255。失败抛 INVALID_IP。 */
    static int parseIPv4Uint(String ip) {
        int len = ip.length();
        int result = 0;
        int parts = 0;
        int i = 0;
        while (i < len) {
            int start = i;
            int val = 0;
            while (i < len && ip.charAt(i) != '.') {
                char c = ip.charAt(i);
                if (c < '0' || c > '9') {
                    throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv4 format: " + ip);
                }
                val = val * 10 + (c - '0');
                if (val > 255) {
                    throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv4 format: " + ip);
                }
                i++;
            }
            int partLen = i - start;
            if (partLen == 0 || partLen > 3 || (partLen > 1 && ip.charAt(start) == '0')) {
                throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv4 format: " + ip);
            }
            result = (result << 8) | val;
            parts++;
            if (i < len) i++; // skip '.'
        }
        if (parts != 4) {
            throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv4 format: " + ip);
        }
        return result;
    }

    /**
     * 严格 IPv6 解析 → 16 字节网络序。
     * 最多一个 '::'、≤8 组、末组允许点分 IPv4、拒绝 zone id 与空组。
     */
    static byte[] parseIPv6Bytes(String s) {
        if (s.indexOf('%') >= 0) {
            throw new QzdbException(ErrorCode.INVALID_IP, "IPv6 zone ids are not supported: " + s);
        }
        int dc = s.indexOf("::");
        String left, right;
        if (dc >= 0) {
            if (s.indexOf("::", dc + 2) >= 0) {
                throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv6 format: " + s);
            }
            left = s.substring(0, dc);
            right = s.substring(dc + 2);
        } else {
            left = s;
            right = null;
        }
        String[] lg = left.isEmpty() ? new String[0] : splitColon(left);
        String[] rg = (right == null || right.isEmpty()) ? new String[0] : splitColon(right);
        if (right != null && rg.length == 0 && !right.isEmpty()) {
            throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv6 format: " + s);
        }

        // 末组可为点分 IPv4
        int v4Int = 0;
        boolean hasV4 = false;
        String lastGroup = rg.length > 0 ? rg[rg.length - 1] : (lg.length > 0 ? lg[lg.length - 1] : null);
        if (lastGroup != null && lastGroup.indexOf('.') >= 0) {
            v4Int = parseIPv4Uint(lastGroup);
            hasV4 = true;
            if (rg.length > 0) rg = java.util.Arrays.copyOf(rg, rg.length - 1);
            else lg = java.util.Arrays.copyOf(lg, lg.length - 1);
        }

        int ng = lg.length + rg.length;
        int v4Slots = hasV4 ? 2 : 0;
        for (String g : lg) validateHexGroup(g, s);
        for (String g : rg) validateHexGroup(g, s);
        if (dc >= 0) {
            if (ng + v4Slots > 7) {
                throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv6 format: " + s);
            }
        } else {
            if (ng + v4Slots != 8) {
                throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv6 format: " + s);
            }
        }

        byte[] buf = new byte[16];
        int off = 0;
        for (String g : lg) {
            int v = parseHexGroup(g);
            buf[off] = (byte) (v >> 8);
            buf[off + 1] = (byte) v;
            off += 2;
        }
        off += (8 - ng - v4Slots) * 2;
        for (String g : rg) {
            int v = parseHexGroup(g);
            buf[off] = (byte) (v >> 8);
            buf[off + 1] = (byte) v;
            off += 2;
        }
        if (hasV4) {
            buf[12] = (byte) (v4Int >>> 24);
            buf[13] = (byte) (v4Int >>> 16);
            buf[14] = (byte) (v4Int >>> 8);
            buf[15] = (byte) v4Int;
        }
        return buf;
    }

    private static String[] splitColon(String s) {
        List<String> parts = new ArrayList<>(8);
        int start = 0;
        for (int i = 0; i <= s.length(); i++) {
            if (i == s.length() || s.charAt(i) == ':') {
                parts.add(s.substring(start, i));
                start = i + 1;
            }
        }
        return parts.toArray(new String[0]);
    }

    private static void validateHexGroup(String g, String full) {
        if (g.isEmpty() || g.length() > 4) {
            throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv6 format: " + full);
        }
        for (int i = 0; i < g.length(); i++) {
            char c = g.charAt(i);
            if (c >= HEX_DIGITS.length || HEX_DIGITS[c] < 0) {
                throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv6 format: " + full);
            }
        }
    }

    private static int parseHexGroup(String g) {
        int v = 0;
        for (int i = 0; i < g.length(); i++) {
            v = (v << 4) | HEX_DIGITS[g.charAt(i)];
        }
        return v;
    }

    /** IPv4-mapped IPv6 数值判定（§9.7：(ip >> 32) == 0xFFFF，即前 10 字节 0 + 2 字节 0xFF）。 */
    static boolean isV4MappedBytes(byte[] b) {
        if (b.length != 16) return false;
        for (int i = 0; i < 10; i++) {
            if (b[i] != 0) return false;
        }
        return (b[10] & 0xFF) == 0xFF && (b[11] & 0xFF) == 0xFF;
    }

    static int v4FromMappedBytes(byte[] b) {
        return ((b[12] & 0xFF) << 24) | ((b[13] & 0xFF) << 16) | ((b[14] & 0xFF) << 8) | (b[15] & 0xFF);
    }

    /** 4/16 字节入口统一规范化（含 mapped 剥离）。长度非法返回 -1。 */
    private static int lookupRowIdFromBytes(Snapshot snap, byte[] raw) {
        if (raw.length == 16) {
            if (isV4MappedBytes(raw)) {
                return lookupRowIdUintInternal(snap, v4FromMappedBytes(raw));
            }
            return lookupV6Bytes(snap, raw);
        }
        if (raw.length == 4) {
            return lookupRowIdUintInternal(snap,
                    ((raw[0] & 0xFF) << 24) | ((raw[1] & 0xFF) << 16) | ((raw[2] & 0xFF) << 8) | (raw[3] & 0xFF));
        }
        return -1;
    }

    // =========================================================================
    // Trie 查询（V4 Jump 16 位 + 至多 16 步；V6 Jump N 位 + 至多 128-N 步，§9.1/9.2）
    // =========================================================================

    private static int lookupRowIdUintInternal(Snapshot s, int ipInt) {
        if (!s.hasV4 || s.offV4Jump <= 0) return 0;
        int ptr = readU32(s.data, (int) (s.offV4Jump + (((ipInt >>> 16) & 0xFFFF) * 4L)));
        if (ptr == 0) return 0;
        if ((ptr & SENTINEL) != 0) return ptr & SENTINEL_MASK_31;

        int idx = ptr;
        int suffix = (ipInt & 0xFFFF) << 16;
        ByteBuffer d = s.data;
        long nOff = s.offV4Nodes;
        int nodeCount = s.v4NodeCount;

        if (s.v4Node24) {
            for (int step = 0; step < 16; step++) {
                if (idx >= nodeCount) return 0;
                int off = (int) (nOff + (long) idx * 6 + ((suffix >>> 31) == 0 ? 0 : 3));
                int child = readU24(d, off);
                if ((child & 0x800000) != 0) return child & SENTINEL_MASK_24;
                if (child == 0) return 0;
                idx = child;
                suffix <<= 1;
            }
        } else {
            for (int step = 0; step < 16; step++) {
                if (idx >= nodeCount) return 0;
                int off = (int) (nOff + (long) idx * 8 + ((suffix >>> 31) == 0 ? 0 : 4));
                int child = readU32(d, off);
                if ((child & SENTINEL) != 0) return child & SENTINEL_MASK_31;
                if (child == 0) return 0;
                idx = child;
                suffix <<= 1;
            }
        }
        return 0;
    }

    private static int lookupV6Bytes(Snapshot s, byte[] ip16) {
        if (!s.hasV6 || s.offV6Jump <= 0 || ip16.length != 16) return 0;
        int jumpBits = s.v6JumpBits;
        int pref = readPrefixBits(ip16, jumpBits);
        int ptr = readU32(s.data, (int) (s.offV6Jump + (long) pref * 4));
        if (ptr == 0) return 0;
        if ((ptr & SENTINEL) != 0) return ptr & SENTINEL_MASK_31;

        int idx = ptr;
        ByteBuffer d = s.data;
        long nOff = s.offV6Nodes;
        int nodeCount = s.v6NodeCount;

        if (s.v6Node24) {
            for (int depth = jumpBits; depth < 128; depth++) {
                if (idx >= nodeCount) return 0;
                int bit = (ip16[depth >> 3] >>> (7 - (depth & 7))) & 1;
                int off = (int) (nOff + (long) idx * 6 + (bit == 0 ? 0 : 3));
                int child = readU24(d, off);
                if ((child & 0x800000) != 0) return child & SENTINEL_MASK_24;
                if (child == 0) return 0;
                idx = child;
            }
        } else {
            for (int depth = jumpBits; depth < 128; depth++) {
                if (idx >= nodeCount) return 0;
                int bit = (ip16[depth >> 3] >>> (7 - (depth & 7))) & 1;
                int off = (int) (nOff + (long) idx * 8 + (bit == 0 ? 0 : 4));
                int child = readU32(d, off);
                if ((child & SENTINEL) != 0) return child & SENTINEL_MASK_31;
                if (child == 0) return 0;
                idx = child;
            }
        }
        return 0;
    }

    private static int readPrefixBits(byte[] bytes, int bits) {
        int val = 0;
        for (int i = 0; i < bits; i++) {
            int bit = (bytes[i >> 3] >>> (7 - (i & 7))) & 1;
            val = (val << 1) | bit;
        }
        return val;
    }

    // =========================================================================
    // 前缀长度重建（CIDR 反查核心：叶子深度 = 前缀位数）
    // =========================================================================

    /**
     * V4 前缀长度。Jump 命中叶子时深度信息在跳表内被压缩（≤16 的某个值），
     * 需从根重走得到精确深度；Jump 返回内节点则深度 = 16 + 后续步数。
     */
    private static int lookupV4PrefixLen(Snapshot s, int ipInt) {
        if (!s.hasV4 || s.offV4Jump <= 0) return -1;
        int ptr = readU32(s.data, (int) (s.offV4Jump + (((ipInt >>> 16) & 0xFFFF) * 4L)));
        if (ptr == 0) return -1;
        if ((ptr & SENTINEL) != 0) {
            return walkV4Depth(s, ipInt, 0, 0, 16);
        }
        return walkV4Depth(s, ipInt, ptr, 16, 32);
    }

    private static int walkV4Depth(Snapshot s, int ipInt, int startIdx, int startDepth, int maxDepth) {
        if (startDepth >= maxDepth) return -1;
        int idx = startIdx;
        ByteBuffer d = s.data;
        long nOff = s.offV4Nodes;
        int nodeCount = s.v4NodeCount;

        if (s.v4Node24) {
            for (int depth = startDepth; depth < maxDepth; depth++) {
                if (idx >= nodeCount) return -1;
                int bit = (ipInt >>> (31 - depth)) & 1;
                int child = readU24(d, (int) (nOff + (long) idx * 6 + (bit == 0 ? 0 : 3)));
                if ((child & 0x800000) != 0) return depth + 1;
                if (child == 0) return -1;
                idx = child;
            }
        } else {
            for (int depth = startDepth; depth < maxDepth; depth++) {
                if (idx >= nodeCount) return -1;
                int bit = (ipInt >>> (31 - depth)) & 1;
                int child = readU32(d, (int) (nOff + (long) idx * 8 + (bit == 0 ? 0 : 4)));
                if ((child & SENTINEL) != 0) return depth + 1;
                if (child == 0) return -1;
                idx = child;
            }
        }
        return -1;
    }

    /** V6 前缀长度。Jump 命中叶子时从根重走（≤ jumpBits 步）。 */
    private static int lookupV6PrefixLen(Snapshot s, byte[] ip16) {
        if (!s.hasV6 || s.offV6Jump <= 0 || ip16.length != 16) return -1;
        int jumpBits = s.v6JumpBits;
        int pref = readPrefixBits(ip16, jumpBits);
        int ptr = readU32(s.data, (int) (s.offV6Jump + (long) pref * 4));
        if (ptr == 0) return -1;
        if ((ptr & SENTINEL) != 0) {
            return walkV6Depth(s, ip16, 0, 0, jumpBits);
        }
        return walkV6Depth(s, ip16, ptr, jumpBits, 128);
    }

    private static int walkV6Depth(Snapshot s, byte[] ip16, int startIdx, int startDepth, int maxDepth) {
        if (startDepth >= maxDepth) return -1;
        int idx = startIdx;
        ByteBuffer d = s.data;
        long nOff = s.offV6Nodes;
        int nodeCount = s.v6NodeCount;

        if (s.v6Node24) {
            for (int depth = startDepth; depth < maxDepth; depth++) {
                if (idx >= nodeCount) return -1;
                int bit = (ip16[depth >> 3] >>> (7 - (depth & 7))) & 1;
                int child = readU24(d, (int) (nOff + (long) idx * 6 + (bit == 0 ? 0 : 3)));
                if ((child & 0x800000) != 0) return depth + 1;
                if (child == 0) return -1;
                idx = child;
            }
        } else {
            for (int depth = startDepth; depth < maxDepth; depth++) {
                if (idx >= nodeCount) return -1;
                int bit = (ip16[depth >> 3] >>> (7 - (depth & 7))) & 1;
                int child = readU32(d, (int) (nOff + (long) idx * 8 + (bit == 0 ? 0 : 4)));
                if ((child & SENTINEL) != 0) return depth + 1;
                if (child == 0) return -1;
                idx = child;
            }
        }
        return -1;
    }

    // =========================================================================
    // CIDR 格式化（网络地址 = IP 高 N 位；V6 按 RFC 5952 压缩）
    // =========================================================================

    private static String formatV4Cidr(int ipInt, int n) {
        int net = n == 0 ? 0 : (ipInt & (int) (0xFFFFFFFFL << (32 - n)));
        return ((net >>> 24) & 0xFF) + "." + ((net >>> 16) & 0xFF) + "."
                + ((net >>> 8) & 0xFF) + "." + (net & 0xFF) + "/" + n;
    }

    private static String formatV6Cidr(byte[] ip16, int n) {
        byte[] net = ip16.clone();
        for (int bit = n; bit < 128; bit++) {
            net[bit >> 3] &= (byte) ~(1 << (7 - (bit & 7)));
        }
        int[] g = new int[8];
        for (int i = 0; i < 8; i++) {
            g[i] = ((net[2 * i] & 0xFF) << 8) | (net[2 * i + 1] & 0xFF);
        }
        // RFC 5952：最长全零组段（并列取最左），长度 ≥2 才压缩
        int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
        for (int i = 0; i < 8; i++) {
            if (g[i] == 0) {
                if (curStart < 0) { curStart = i; curLen = 1; } else curLen++;
            } else {
                if (curLen > bestLen) { bestStart = curStart; bestLen = curLen; }
                curStart = -1; curLen = 0;
            }
        }
        if (curLen > bestLen) { bestStart = curStart; bestLen = curLen; }

        StringBuilder sb = new StringBuilder(44);
        if (bestLen >= 2) {
            for (int i = 0; i < bestStart; i++) {
                if (i > 0) sb.append(':');
                sb.append(Integer.toHexString(g[i]));
            }
            sb.append("::");
            boolean first = true;
            for (int i = bestStart + bestLen; i < 8; i++) {
                if (!first) sb.append(':');
                sb.append(Integer.toHexString(g[i]));
                first = false;
            }
        } else {
            for (int i = 0; i < 8; i++) {
                if (i > 0) sb.append(':');
                sb.append(Integer.toHexString(g[i]));
            }
        }
        return sb.append('/').append(n).toString();
    }

    // =========================================================================
    // CRC32（流式分块，避免整文件堆内拷贝；canonical = 偏移 16~19 填 0）
    // =========================================================================

    private static long computeCanonicalCrc(ByteBuffer src) {
        ByteBuffer d = src.duplicate();
        int len = d.capacity();
        CRC32 crc = new CRC32();
        byte[] chunk = new byte[Math.min(CRC_CHUNK, Math.max(len, 1))];
        int pos = 0;
        boolean first = true;
        while (pos < len) {
            int n = Math.min(chunk.length, len - pos);
            d.position(pos);
            d.get(chunk, 0, n);
            if (first && n >= 20) {
                crc.update(chunk, 0, 16);
                crc.update(ZERO4, 0, 4);
                crc.update(chunk, 20, n - 20);
                first = false;
            } else {
                crc.update(chunk, 0, n);
                first = false;
            }
            pos += n;
        }
        return crc.getValue();
    }

    // =========================================================================
    // 基础 LE 读取（绝对定位，无 position 副作用，线程安全）
    // =========================================================================

    private static String readUtf8(ByteBuffer d, int off, int len) {
        assertReadable(d, off, len);
        byte[] bytes = new byte[len];
        d.get(off, bytes, 0, len);
        return new String(bytes, StandardCharsets.UTF_8);
    }

    private static int readU16(ByteBuffer d, int off) {
        assertReadable(d, off, 2);
        return d.getShort(off) & 0xFFFF;
    }

    private static int readU32(ByteBuffer d, int off) {
        assertReadable(d, off, 4);
        return d.getInt(off);
    }

    private static long readU64(ByteBuffer d, int off) {
        assertReadable(d, off, 8);
        return d.getLong(off);
    }

    private static long readU48(ByteBuffer d, int off) {
        assertReadable(d, off, 6);
        // 逐字节读取（header 内偏移 186+6=192 恰为边界，getLong 会越界）
        return (d.get(off) & 0xFFL)
                | ((d.get(off + 1) & 0xFFL) << 8)
                | ((d.get(off + 2) & 0xFFL) << 16)
                | ((d.get(off + 3) & 0xFFL) << 24)
                | ((d.get(off + 4) & 0xFFL) << 32)
                | ((d.get(off + 5) & 0xFFL) << 40);
    }

    private static int readU24(ByteBuffer d, int off) {
        assertReadable(d, off, 3);
        return (d.get(off) & 0xFF) | ((d.getShort(off + 1) & 0xFFFF) << 8);
    }

    private static int readUintWidth(ByteBuffer d, int off, int width) {
        int need = width <= 1 ? 1 : (width == 2 ? 2 : (width == 3 ? 3 : 4));
        assertReadable(d, off, need);
        if (width <= 1) return d.get(off) & 0xFF;
        if (width == 2) return readU16(d, off);
        if (width == 3) return readU24(d, off);
        return readU32(d, off);
    }

    private static long readUintWidthUnsigned(ByteBuffer d, int off, int width) {
        int need = width <= 1 ? 1 : (width == 2 ? 2 : (width == 3 ? 3 : 4));
        assertReadable(d, off, need);
        if (width <= 1) return d.get(off) & 0xFFL;
        if (width == 2) return d.getShort(off) & 0xFFFFL;
        if (width == 3) return (d.get(off) & 0xFFL) | ((d.getShort(off + 1) & 0xFFFFL) << 8);
        return d.getInt(off) & 0xFFFFFFFFL;
    }

    /** Fail-Closed：读取前校验偏移/长度不越界，越界抛 QzdbException 而非原始 IndexOutOfBoundsException。 */
    private static void assertReadable(ByteBuffer d, int off, int need) {
        if (off < 0 || need < 0 || off + (long) need > d.limit()) {
            throw new QzdbException(ErrorCode.CORRUPTED,
                    String.format("Read out of bounds: off=%d need=%d limit=%d", off, need, d.limit()));
        }
    }
}
