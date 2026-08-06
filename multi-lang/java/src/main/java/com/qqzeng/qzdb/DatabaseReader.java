package com.qqzeng.qzdb;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.net.InetAddress;
import java.net.UnknownHostException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.TimeZone;
import java.util.concurrent.atomic.AtomicReference;
import java.util.stream.Stream;
import java.util.zip.CRC32;

/**
 * QZDB 高性能 IP 数据库查询引擎 (DatabaseReader)
 * <p>
 * 采用无锁 (Lock-Free) 内存视图与原子替换 (Volatile Snapshot) 架构，支持文件 Mmap 与内存 Buffer 两种加载模式。
 */
public class DatabaseReader implements AutoCloseable {

    private static final int SENTINEL = 0x80000000;
    private static final int SENTINEL_MASK_24 = 0x7FFFFF;
    private static final int SENTINEL_MASK_31 = 0x7FFFFFFF;
    private static final int MAX_TRIE_WALK_STEPS = 1000;

    /**
     * 不可变只读数据快照
     */
    private static final class Snapshot {
        final ByteBuffer data;
        final int groupIndex;
        final String[] fieldNames;
        final Map<String, Integer> normalizedFieldMap;
        final String[][][] pools;

        // Header 元数据
        final int flags;
        final boolean hasV4;
        final boolean hasV6;
        final boolean v4Node24;
        final boolean v6Node24;
        final int v6JumpBits;
        final int poolCount;
        final int poolIdxSize;
        final int geoCount;
        final int rowCount;
        final int v4RecCount;
        final int v6RecCount;
        final int v4NodeCount;
        final int v6NodeCount;
        int ipRowSize = 6;
        final int geoEntryGroupCount;

        // 字段宽度
        int rowGeoWidth = 3;
        int rowAsnWidth = 3;
        int rowUsageWidth = 0;

        // 偏移量
        final long offV4Jump;
        final long offV4Nodes;
        final long offV6Jump;
        final long offV6Nodes;
        final long offIPRow;
        final long offGeoEntries;
        final long offPools;
        final long offMeta;
        final long offRowSchema;
        final long offGroupSchema;

        // Schema 布局缓存
        int[] groupFieldCounts;
        long[] groupEntryCounts;
        int[] groupDimMasks;
        long[] groupEntryOffsets;
        int[] groupStrides;
        int[][] groupFieldWidths;
        int[][] groupFieldOffsets;
        boolean[][] groupFieldNative;
        int[][] groupFieldNativeType;
        int[][] groupFieldIds;

        // 元数据属性
        final String version;
        final String dataMonth;
        final String edition;
        final String scope;
        final String buildTimeStr;
        final String fileHash;

        Snapshot(ByteBuffer buffer, int groupIndex, boolean verifyCrc) throws QzdbException {
            this.data = buffer.duplicate().order(ByteOrder.LITTLE_ENDIAN);
            this.groupIndex = groupIndex;

            if (data.capacity() < 192) {
                throw new QzdbException(ErrorCode.CORRUPTED, "File too small for QZDB header");
            }
            if (data.get(0) != 'Q' || data.get(1) != 'Z' || data.get(2) != 'D' || data.get(3) != 'B') {
                throw new QzdbException(ErrorCode.BAD_MAGIC, "Invalid magic, expected QZDB");
            }

            int fmtVer = data.get(4) & 0xFF;
            if (fmtVer != 1) {
                throw new QzdbException(ErrorCode.UNSUPPORTED, "Unsupported format version: " + fmtVer + " (only version 1 is supported)");
            }

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
            geoCount = readU16(data, 14);
            rowCount = readU32(data, 20);
            v4RecCount = readU32(data, 24);
            v6RecCount = readU32(data, 28);

            int hs = readU32(data, 36);
            if (hs != 192) {
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
            if (rs > 0) ipRowSize = rs;

            int gCount = readU32(data, 164);
            geoEntryGroupCount = gCount > 0 ? gCount : 1;
            if (groupIndex < 0 || groupIndex >= geoEntryGroupCount) {
                throw new QzdbException(ErrorCode.INVALID_PARAM, "groupIndex out of range [0," + (geoEntryGroupCount - 1) + "]: " + groupIndex);
            }

            parseRowSchema();
            groupFieldCounts = new int[geoEntryGroupCount];
            groupEntryCounts = new long[geoEntryGroupCount];
            groupDimMasks = new int[geoEntryGroupCount];
            groupEntryOffsets = new long[geoEntryGroupCount];

            parseGroupSchema();

            // 解析元属性与哈希
            this.fileHash = calculateCrc32Hex(data);
            if (verifyCrc) {
                long headerCrc = readU32(data, 16) & 0xFFFFFFFFL;
                CRC32 crc = new CRC32();
                byte[] temp = new byte[data.capacity()];
                data.position(0);
                data.get(temp);
                crc.update(temp, 0, 16);
                crc.update(new byte[4], 0, 4); // 16~20 填零
                crc.update(temp, 20, temp.length - 20);
                long calcCrc = crc.getValue();
                if (headerCrc != 0 && headerCrc != calcCrc) {
                    throw new QzdbException(ErrorCode.CORRUPTED, "CRC32 checksum mismatch — the database file is corrupted or truncated");
                }
            }

            // 加载字符串池
            this.pools = parsePools();

            // 解析字段名数组
            int numFields = groupFieldCounts[groupIndex];
            this.fieldNames = new String[numFields];
            for (int i = 0; i < numFields; i++) {
                int fid = groupFieldIds[groupIndex][i];
                fieldNames[i] = fid < DEFAULT_FIELD_NAMES.length ? DEFAULT_FIELD_NAMES[fid] : "field_" + fid;
            }
            this.normalizedFieldMap = GeoInfo.buildNormalizedMap(this.fieldNames);

            // 版本推断
            this.version = "2.0";
            long buildTs = readU32(data, 144) & 0xFFFFFFFFL;
            if (buildTs > 0) {
                SimpleDateFormat sdf = new SimpleDateFormat("yyyy-MM", Locale.US);
                sdf.setTimeZone(TimeZone.getTimeZone("UTC"));
                this.dataMonth = sdf.format(new Date(buildTs * 1000L));
                SimpleDateFormat sdfFull = new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US);
                sdfFull.setTimeZone(TimeZone.getTimeZone("UTC"));
                this.buildTimeStr = sdfFull.format(new Date(buildTs * 1000L));
            } else {
                this.dataMonth = "Unknown";
                this.buildTimeStr = "Unknown";
            }

            this.edition = inferEdition(numFields, normalizedFieldMap);
            this.scope = inferScope(normalizedFieldMap);
        }

        private static String inferEdition(int count, Map<String, Integer> normMap) {
            if (normMap.containsKey("district")) return "ult";
            if (normMap.containsKey("currencycode") || count >= 20) return "max";
            if (normMap.containsKey("asn")) return "asn";
            if (count >= 10) return "pro";
            return "std";
        }

        private static String inferScope(Map<String, Integer> normMap) {
            if (normMap.containsKey("countryen") || normMap.containsKey("currencycode")) {
                return "global";
            }
            return "cn";
        }

        private void parseRowSchema() {
            rowGeoWidth = 3;
            rowAsnWidth = 3;
            rowUsageWidth = 0;
            if (offRowSchema <= 0 || offRowSchema >= data.capacity()) {
                return;
            }
            int sp = (int) offRowSchema;
            int fCount = data.get(sp) & 0xFF;
            int stride = data.get(sp + 1) & 0xFF;
            if (fCount < 1 || fCount > 8) return;
            if (sp + 4 + (long) fCount * 4 > data.capacity()) return;
            if (stride != ipRowSize) return;

            int geoW = 0, asnW = 0, usageW = 0, total = 0;
            int wpos = sp + 4;
            boolean ok = true;
            for (int i = 0; i < fCount; i++) {
                int fid = data.get(wpos) & 0xFF;
                int w = data.get(wpos + 1) & 0xFF;
                if (fid == 0) geoW = w;
                else if (fid == 1) asnW = w;
                else if (fid == 2) usageW = w;
                wpos += 4;
                total += w;
                if (w < 1 || w > 4) ok = false;
            }
            if (ok && total == ipRowSize) {
                rowGeoWidth = geoW;
                rowAsnWidth = asnW;
                rowUsageWidth = usageW;
            }
        }

        private void parseGroupSchema() {
            // 1. 读取 Header 168 处的 GeoEntryOffsets[4] (uint48 LE x 4)
            long[] headerGeoOffsets = new long[4];
            for (int i = 0; i < 4; i++) {
                headerGeoOffsets[i] = readU48(data, 168 + i * 6);
            }

            // 2. 解析 GroupMetadataTable (位于 offGeoEntries)
            int gmOff = (int) offGeoEntries;
            int gCount = data.get(gmOff) & 0xFF;
            gmOff += 1;

            int actualGroups = Math.min(gCount, Math.max(1, geoEntryGroupCount));
            if (actualGroups > 4) actualGroups = 4;

            groupFieldCounts = new int[actualGroups];
            groupEntryCounts = new long[actualGroups];
            groupDimMasks = new int[actualGroups];
            groupEntryOffsets = new long[actualGroups];

            for (int gi = 0; gi < actualGroups; gi++) {
                groupFieldCounts[gi] = data.get(gmOff) & 0xFF;
                gmOff += 1;
                groupEntryCounts[gi] = readU32(data, gmOff) & 0xFFFFFFFFL;
                gmOff += 4;
                groupDimMasks[gi] = readU16(data, gmOff);
                gmOff += 2;
                groupEntryOffsets[gi] = offGeoEntries + headerGeoOffsets[gi];
            }

            // 3. 初始化 Schema 数组
            groupStrides = new int[actualGroups];
            groupFieldWidths = new int[actualGroups][];
            groupFieldOffsets = new int[actualGroups][];
            groupFieldNative = new boolean[actualGroups][];
            groupFieldNativeType = new int[actualGroups][];
            groupFieldIds = new int[actualGroups][];

            // 4. 从 GROUP_SCHEMA 填入真实字段布局与 Stride
            if (offGroupSchema > 0) {
                int sp = (int) offGroupSchema;
                int gsGroupCount = readU16(data, sp);
                sp += 2;
                int maxGsGroups = Math.min(gsGroupCount, actualGroups);

                for (int gi = 0; gi < maxGsGroups; gi++) {
                    sp += 2; // skip groupId
                    int fldCount = readU16(data, sp);
                    sp += 2;
                    sp += 4; // skip entryCount
                    int stride = readU32(data, sp);
                    sp += 4;
                    sp += 4; // skip flags

                    groupStrides[gi] = stride;
                    int[] widths = new int[fldCount];
                    int[] offsets = new int[fldCount];
                    boolean[] natives = new boolean[fldCount];
                    int[] natTypes = new int[fldCount];
                    int[] fids = new int[fldCount];

                    int curOffset = 0;
                    for (int fi = 0; fi < fldCount; fi++) {
                        int fid = readU16(data, sp);
                        sp += 2;
                        fids[fi] = fid;
                        int w = data.get(sp) & 0xFF;
                        sp += 1;
                        int fieldFlags = data.get(sp) & 0xFF;
                        sp += 1;
                        natives[fi] = (fieldFlags & 0x01) != 0;
                        natTypes[fi] = data.get(sp) & 0xFF;
                        sp += 1;
                        sp += 1; // skip reserved byte

                        widths[fi] = w;
                        offsets[fi] = curOffset;
                        curOffset += w;
                    }

                    groupFieldWidths[gi] = widths;
                    groupFieldOffsets[gi] = offsets;
                    groupFieldNative[gi] = natives;
                    groupFieldNativeType[gi] = natTypes;
                    groupFieldIds[gi] = fids;
                }
            }
        }

        private String[][][] parsePools() {
            String[][][] result = new String[geoEntryGroupCount][][];
            int poolCursor = (int) offPools;
            int poolEnd = offMeta > 0 ? (int) offMeta : data.capacity();

            for (int g = 0; g < geoEntryGroupCount; g++) {
                int fieldCount = groupFieldCounts[g];
                String[][] groupPoolList = new String[fieldCount][];
                boolean[] natives = groupFieldNative[g];

                for (int f = 0; f < fieldCount; f++) {
                    if (natives != null && f < natives.length && natives[f]) {
                        groupPoolList[f] = new String[0];
                        continue;
                    }
                    if (poolCursor + 4 > poolEnd) {
                        groupPoolList[f] = new String[0];
                        continue;
                    }
                    int count = readU32(data, poolCursor);
                    poolCursor += 4;
                    if (offRowSchema > 0) {
                        poolCursor += 4; // 跳过 poolSizeBytes (4B)
                    }
                    if (count <= 0) {
                        groupPoolList[f] = new String[0];
                        continue;
                    }

                    String[] strings = new String[count];
                    int offsetsStart = poolCursor;
                    int stringDataStart = poolCursor + (count + 1) * 4;

                    for (int i = 0; i < count; i++) {
                        int strOff = readU32(data, offsetsStart + i * 4);
                        int nextOff = readU32(data, offsetsStart + (i + 1) * 4);
                        int len = nextOff - strOff;

                        if (len > 0) {
                            byte[] strBytes = new byte[len];
                            int pos = data.position();
                            data.position(stringDataStart + strOff);
                            data.get(strBytes);
                            data.position(pos);
                            strings[i] = new String(strBytes, StandardCharsets.UTF_8);
                        } else {
                            strings[i] = "";
                        }
                    }
                    groupPoolList[f] = strings;
                    poolCursor = stringDataStart + readU32(data, offsetsStart + count * 4);
                }
                result[g] = groupPoolList;
            }
            return result;
        }

        GeoInfo extractGeoInfo(int geoId, int asnId, int usageId, String[] fieldFilter) {
            if (geoId < 0 || geoId >= groupEntryCounts[groupIndex]) {
                return null;
            }

            int fc = groupFieldCounts[groupIndex];
            String[] targetFields = fieldFilter != null ? fieldFilter : fieldNames;
            String[] values = new String[targetFields.length];

            long entryOff = groupEntryOffsets[groupIndex] + (long) geoId * groupStrides[groupIndex];

            for (int i = 0; i < targetFields.length; i++) {
                String reqField = targetFields[i];
                Integer origIdx = normalizedFieldMap.get(GeoInfo.normalizeKey(reqField));
                if (origIdx == null || origIdx >= fc) {
                    values[i] = "";
                    continue;
                }

                int fw = groupFieldWidths[groupIndex][origIdx];
                int fo = groupFieldOffsets[groupIndex][origIdx];
                boolean isNative = groupFieldNative[groupIndex][origIdx];
                int nt = groupFieldNativeType[groupIndex][origIdx];

                if (isNative) {
                    values[i] = readNativeValue(entryOff + fo, fw, nt);
                } else {
                    int valIdx = readUintWidth(data, (int) (entryOff + fo), fw);
                    String[] pool = pools[groupIndex][origIdx];
                    if (pool != null && valIdx >= 0 && valIdx < pool.length) {
                        values[i] = pool[valIdx];
                    } else {
                        values[i] = "";
                    }
                }
            }
            return new GeoInfo(targetFields, values);
        }

        private String readNativeValue(long off, int fw, int nt) {
            int iOff = (int) off;
            if (nt == 1) { // float
                return String.valueOf(Float.intBitsToFloat(readU32(data, iOff)));
            } else if (nt == 2) { // double
                return String.valueOf(Double.longBitsToDouble(readU64(data, iOff)));
            } else if (nt == 3) { // int32
                return String.valueOf(readU32(data, iOff));
            } else if (nt == 4) { // uint32
                return String.valueOf(readU32(data, iOff) & 0xFFFFFFFFL);
            }
            return "";
        }
    }

    private static final String[] DEFAULT_FIELD_NAMES = {
            "country", "province", "city", "isp", "district", "longitude", "latitude",
            "country_en", "province_en", "city_en", "isp_en", "cidr", "asn", "as_name",
            "as_domain", "usage_type", "country_alpha2", "country_alpha3", "currency_code",
            "currency_name", "phone_prefix", "emoji_flag", "languages", "timezone", "geo_id"
    };

    private final AtomicReference<Snapshot> activeSnapshot = new AtomicReference<>();
    private final File loadedFile;

    /**
     * DatabaseReader 构建器
     */
    public static class Builder {
        private File databaseFile;
        private byte[] bufferData;
        private int groupIndex = 0;
        private boolean verifyCrc = true;

        public Builder(File database) {
            this.databaseFile = database;
        }

        public Builder(byte[] buffer) {
            // 拷贝语义保护调用方数据
            this.bufferData = buffer != null ? buffer.clone() : new byte[0];
        }

        public Builder(InputStream stream) throws IOException {
            this.bufferData = stream.readAllBytes();
        }

        public Builder groupIndex(int idx) {
            this.groupIndex = idx;
            return this;
        }

        public Builder verifyCrc(boolean enabled) {
            this.verifyCrc = enabled;
            return this;
        }

        public DatabaseReader build() throws QzdbException {
            ByteBuffer buffer;
            File fileRef = databaseFile;

            if (databaseFile != null) {
                if (!databaseFile.exists() || !databaseFile.canRead()) {
                    throw new QzdbException(ErrorCode.FILE_NOT_FOUND, "Database file does not exist or is not readable: " + databaseFile.getAbsolutePath());
                }
                try (RandomAccessFile raf = new RandomAccessFile(databaseFile, "r");
                     FileChannel ch = raf.getChannel()) {
                    buffer = ch.map(FileChannel.MapMode.READ_ONLY, 0, ch.size());
                } catch (IOException e) {
                    throw new QzdbException(ErrorCode.FILE_NOT_FOUND, "Failed to read database file: " + databaseFile.getAbsolutePath(), e);
                }
            } else if (bufferData != null) {
                buffer = ByteBuffer.wrap(bufferData);
            } else {
                throw new QzdbException(ErrorCode.INVALID_PARAM, "Neither database file nor buffer was provided");
            }

            Snapshot snapshot = new Snapshot(buffer, groupIndex, verifyCrc);
            DatabaseReader reader = new DatabaseReader(fileRef);
            reader.activeSnapshot.set(snapshot);
            return reader;
        }
    }

    private DatabaseReader(File file) {
        this.loadedFile = file;
    }

    // =========================================================================
    // 单条查询 API
    // =========================================================================

    public Optional<GeoInfo> find(String ipStr) {
        if (ipStr == null || ipStr.isEmpty()) {
            throw new QzdbException(ErrorCode.INVALID_IP, "IP address string cannot be empty");
        }

        // 规范化检测与 IPv4-mapped IPv6 剥离 (::ffff:1.12.0.0)
        String normalizedIp = normalizeIpString(ipStr);
        if (normalizedIp == null) {
            throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IP address format: " + ipStr);
        }

        Snapshot snap = activeSnapshot.get();
        int rowId = lookupRowIdInternal(snap, normalizedIp);
        if (rowId <= 0) {
            return Optional.empty();
        }

        RowIds ids = lookupIdsInternal(snap, rowId);
        if (ids == null) {
            return Optional.empty();
        }

        GeoInfo info = snap.extractGeoInfo(ids.geoId(), ids.asnId(), ids.usageId(), null);
        return Optional.ofNullable(info);
    }

    public Optional<GeoInfo> find(InetAddress addr) {
        if (addr == null) {
            throw new QzdbException(ErrorCode.INVALID_IP, "InetAddress cannot be null");
        }
        return find(addr.getHostAddress());
    }

    public Optional<GeoInfo> findUint(int ipInt) {
        Snapshot snap = activeSnapshot.get();
        int rowId = lookupRowIdUintInternal(snap, ipInt);
        if (rowId <= 0) return Optional.empty();

        RowIds ids = lookupIdsInternal(snap, rowId);
        if (ids == null) return Optional.empty();

        GeoInfo info = snap.extractGeoInfo(ids.geoId(), ids.asnId(), ids.usageId(), null);
        return Optional.ofNullable(info);
    }

    public Optional<GeoInfo> findBytes(byte[] ip16) {
        if (ip16 == null) {
            throw new QzdbException(ErrorCode.INVALID_IP, "IP bytes array cannot be null");
        }

        // IPv4-mapped IPv6 自动剥离 (16 字节中前 10 字节为 0，接下来 2 字节为 0xFF)
        if (ip16.length == 16 && isV4MappedV6Bytes(ip16)) {
            int ipInt = ((ip16[12] & 0xFF) << 24) | ((ip16[13] & 0xFF) << 16) |
                        ((ip16[14] & 0xFF) << 8) | (ip16[15] & 0xFF);
            return findUint(ipInt);
        }

        if (ip16.length == 4) {
            int ipInt = ((ip16[0] & 0xFF) << 24) | ((ip16[1] & 0xFF) << 16) |
                        ((ip16[2] & 0xFF) << 8) | (ip16[3] & 0xFF);
            return findUint(ipInt);
        }

        try {
            InetAddress addr = InetAddress.getByAddress(ip16);
            return find(addr.getHostAddress());
        } catch (UnknownHostException e) {
            throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IP byte array length: " + ip16.length);
        }
    }

    public Optional<GeoInfo> findFields(String ipStr, String[] fields) {
        if (fields == null || fields.length == 0) {
            return find(ipStr);
        }
        String normalizedIp = normalizeIpString(ipStr);
        if (normalizedIp == null) {
            throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IP address format: " + ipStr);
        }

        Snapshot snap = activeSnapshot.get();
        int rowId = lookupRowIdInternal(snap, normalizedIp);
        if (rowId <= 0) return Optional.empty();

        RowIds ids = lookupIdsInternal(snap, rowId);
        if (ids == null) return Optional.empty();

        GeoInfo info = snap.extractGeoInfo(ids.geoId(), ids.asnId(), ids.usageId(), fields);
        return Optional.ofNullable(info);
    }

    public String findStr(String ipStr) {
        Optional<GeoInfo> info = find(ipStr);
        return info.map(GeoInfo::toPipeString).orElse("");
    }

    // =========================================================================
    // 批量与流式 API
    // =========================================================================

    public List<BatchResult> findBatch(List<String> ips) {
        if (ips == null) return Collections.emptyList();
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

    public List<BatchResult> findBatchFields(List<String> ips, String[] fields) {
        if (ips == null) return Collections.emptyList();
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

    public int lookupRowId(String ipStr) {
        String normalizedIp = normalizeIpString(ipStr);
        if (normalizedIp == null) return 0;
        try {
            return lookupRowIdInternal(activeSnapshot.get(), normalizedIp);
        } catch (QzdbException e) {
            return 0;
        }
    }

    public int lookupRowIdUint(int ipInt) {
        return lookupRowIdUintInternal(activeSnapshot.get(), ipInt);
    }

    public RowIds lookupIds(int rowId) {
        return lookupIdsInternal(activeSnapshot.get(), rowId);
    }

    // =========================================================================
    // 热更新 reload API
    // =========================================================================

    public void reload(String path) throws QzdbException {
        File file = new File(path);
        if (!file.exists() || !file.canRead()) {
            throw new QzdbException(ErrorCode.FILE_NOT_FOUND, "Reload file does not exist: " + path);
        }
        try (RandomAccessFile raf = new RandomAccessFile(file, "r");
             FileChannel ch = raf.getChannel()) {
            MappedByteBuffer buffer = ch.map(FileChannel.MapMode.READ_ONLY, 0, ch.size());
            Snapshot newSnap = new Snapshot(buffer, activeSnapshot.get().groupIndex, true); // reload 强制 CRC
            activeSnapshot.set(newSnap);
        } catch (IOException e) {
            throw new QzdbException(ErrorCode.FILE_NOT_FOUND, "Failed to read reload file: " + path, e);
        }
    }

    public void reloadBuffer(byte[] buffer) throws QzdbException {
        if (buffer == null || buffer.length == 0) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "Reload buffer cannot be null or empty");
        }
        ByteBuffer wrap = ByteBuffer.wrap(buffer.clone()); // 拷贝保护
        Snapshot newSnap = new Snapshot(wrap, activeSnapshot.get().groupIndex, true);
        activeSnapshot.set(newSnap);
    }

    @Override
    public void close() {
        // 无锁清空引用
        activeSnapshot.set(null);
    }

    // =========================================================================
    // 元信息自省 API
    // =========================================================================

    public String getVersion() { return activeSnapshot.get().version; }
    public String getDataMonth() { return activeSnapshot.get().dataMonth; }
    public String getEdition() { return activeSnapshot.get().edition; }
    public String getScope() { return activeSnapshot.get().scope; }
    public String getBuildTime() { return activeSnapshot.get().buildTimeStr; }
    public String getFileHash() { return activeSnapshot.get().fileHash; }
    public String[] getFieldNames() { return activeSnapshot.get().fieldNames.clone(); }
    public boolean hasField(String name) {
        return activeSnapshot.get().normalizedFieldMap.containsKey(GeoInfo.normalizeKey(name));
    }
    public boolean verifyCrc() {
        return activeSnapshot.get() != null;
    }

    // =========================================================================
    // 内部私有查找实现 (无锁零分配)
    // =========================================================================

    private static String normalizeIpString(String ipStr) {
        if (ipStr == null) return null;
        String trimmed = ipStr.trim();
        if (trimmed.isEmpty()) return null;

        // IPv4-mapped IPv6 剥离 (例如 ::ffff:1.12.0.0)
        String lower = trimmed.toLowerCase();
        if (lower.startsWith("::ffff:")) {
            return trimmed.substring(7);
        }
        return trimmed;
    }

    private static boolean isV4MappedV6Bytes(byte[] bytes) {
        for (int i = 0; i < 10; i++) {
            if (bytes[i] != 0) return false;
        }
        return (bytes[10] & 0xFF) == 0xFF && (bytes[11] & 0xFF) == 0xFF;
    }

    private int lookupRowIdInternal(Snapshot s, String ipStr) {
        if (ipStr.contains(":")) {
            // IPv6
            byte[] bytes;
            try {
                bytes = InetAddress.getByName(ipStr).getAddress();
            } catch (UnknownHostException e) {
                throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv6 format: " + ipStr);
            }
            return lookupV6Bytes(s, bytes);
        } else {
            // IPv4
            int ipInt = parseIPv4Uint(ipStr);
            if (ipInt == 0 && !ipStr.equals("0.0.0.0") && !ipStr.equals("0")) {
                throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IPv4 format: " + ipStr);
            }
            return lookupRowIdUintInternal(s, ipInt);
        }
    }

    private int lookupRowIdUintInternal(Snapshot s, int ipInt) {
        if (!s.hasV4 || s.offV4Jump <= 0) return 0;
        int pref = (ipInt >>> 16) & 0xFFFF;
        int jOff = (int) (s.offV4Jump + pref * 4L);
        int cur = readU32(s.data, jOff);

        int nodeSize = s.v4Node24 ? 6 : 8;
        long nOff = s.offV4Nodes;

        for (int step = 0; step < MAX_TRIE_WALK_STEPS; step++) {
            if ((cur & SENTINEL) != 0) {
                return cur & SENTINEL_MASK_31;
            }
            if (cur == 0) return 0;

            int bit = (ipInt >>> (31 - step)) & 1;
            long nodeAddr = nOff + (long) (cur - 1) * nodeSize;

            if (s.v4Node24) {
                int left = readU24(s.data, (int) nodeAddr);
                int right = readU24(s.data, (int) nodeAddr + 3);
                cur = (bit == 0) ? left : right;
            } else {
                int left = readU32(s.data, (int) nodeAddr);
                int right = readU32(s.data, (int) nodeAddr + 4);
                cur = (bit == 0) ? left : right;
            }
        }
        return 0;
    }

    private int lookupV6Bytes(Snapshot s, byte[] ip16) {
        if (!s.hasV6 || s.offV6Jump <= 0 || ip16.length != 16) return 0;
        int jumpBits = s.v6JumpBits;
        int pref = readPrefixBits(ip16, jumpBits);
        int jOff = (int) (s.offV6Jump + pref * 4L);
        int cur = readU32(s.data, jOff);

        int nodeSize = s.v6Node24 ? 6 : 8;
        long nOff = s.offV6Nodes;

        for (int step = jumpBits; step < 128; step++) {
            if ((cur & SENTINEL) != 0) {
                return cur & SENTINEL_MASK_31;
            }
            if (cur == 0) return 0;

            int bitIndex = step;
            int byteIdx = bitIndex >> 3;
            int bitIdx = 7 - (bitIndex & 7);
            int bit = (ip16[byteIdx] >>> bitIdx) & 1;

            long nodeAddr = nOff + (long) (cur - 1) * nodeSize;
            if (s.v6Node24) {
                int left = readU24(s.data, (int) nodeAddr);
                int right = readU24(s.data, (int) nodeAddr + 3);
                cur = (bit == 0) ? left : right;
            } else {
                int left = readU32(s.data, (int) nodeAddr);
                int right = readU32(s.data, (int) nodeAddr + 4);
                cur = (bit == 0) ? left : right;
            }
        }
        return 0;
    }

    private RowIds lookupIdsInternal(Snapshot s, int rowId) {
        if (rowId <= 0 || rowId >= s.rowCount) return null;
        long rOff = s.offIPRow + (long) rowId * s.ipRowSize;

        int geoId = readUintWidth(s.data, (int) rOff, s.rowGeoWidth);
        int asnId = s.rowAsnWidth > 0 ? readUintWidth(s.data, (int) (rOff + s.rowGeoWidth), s.rowAsnWidth) : 0;
        int usageId = s.rowUsageWidth > 0 ? readUintWidth(s.data, (int) (rOff + s.rowGeoWidth + s.rowAsnWidth), s.rowUsageWidth) : 0;

        return new RowIds(geoId, asnId, usageId);
    }

    private static int parseIPv4Uint(String ip) {
        String[] parts = ip.split("\\.");
        if (parts.length != 4) return 0;
        try {
            int a = Integer.parseInt(parts[0]);
            int b = Integer.parseInt(parts[1]);
            int c = Integer.parseInt(parts[2]);
            int d = Integer.parseInt(parts[3]);
            if (a < 0 || a > 255 || b < 0 || b > 255 || c < 0 || c > 255 || d < 0 || d > 255) return 0;
            return (a << 24) | (b << 16) | (c << 8) | d;
        } catch (NumberFormatException e) {
            return 0;
        }
    }

    private static int readPrefixBits(byte[] bytes, int bits) {
        int val = 0;
        for (int i = 0; i < bits; i++) {
            int byteIdx = i >> 3;
            int bitIdx = 7 - (i & 7);
            int bit = (bytes[byteIdx] >>> bitIdx) & 1;
            val = (val << 1) | bit;
        }
        return val;
    }

    private static String calculateCrc32Hex(ByteBuffer d) {
        CRC32 crc = new CRC32();
        byte[] bytes = new byte[d.capacity()];
        int pos = d.position();
        d.position(0);
        d.get(bytes);
        d.position(pos);
        crc.update(bytes);
        return String.format("%08x", crc.getValue());
    }

    private static int readU16(ByteBuffer d, int off) {
        return (d.get(off) & 0xFF) | ((d.get(off + 1) & 0xFF) << 8);
    }

    private static int readU32(ByteBuffer d, int off) {
        return (d.get(off) & 0xFF) | ((d.get(off + 1) & 0xFF) << 8) |
               ((d.get(off + 2) & 0xFF) << 16) | ((d.get(off + 3) & 0xFF) << 24);
    }

    private static long readU64(ByteBuffer d, int off) {
        return (readU32(d, off) & 0xFFFFFFFFL) | ((long) readU32(d, off + 4) << 32);
    }

    private static long readU48(ByteBuffer d, int off) {
        return (d.get(off) & 0xFFL)
                | ((d.get(off + 1) & 0xFFL) << 8)
                | ((d.get(off + 2) & 0xFFL) << 16)
                | ((d.get(off + 3) & 0xFFL) << 24)
                | ((d.get(off + 4) & 0xFFL) << 32)
                | ((d.get(off + 5) & 0xFFL) << 40);
    }

    private static int readU24(ByteBuffer d, int off) {
        return (d.get(off) & 0xFF) | ((d.get(off + 1) & 0xFF) << 8) | ((d.get(off + 2) & 0xFF) << 16);
    }

    private static int readUintWidth(ByteBuffer d, int off, int width) {
        if (width <= 1) return d.get(off) & 0xFF;
        if (width == 2) return readU16(d, off);
        if (width == 3) return readU24(d, off);
        return readU32(d, off);
    }
}
