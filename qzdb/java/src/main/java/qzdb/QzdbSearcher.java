package qzdb;

import java.io.IOException;
import java.io.RandomAccessFile;
import java.math.BigInteger;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.util.HashSet;
import java.util.zip.CRC32;
import java.util.Locale;

public class QzdbSearcher {

    private static final int SENTINEL = 0x80000000;
    private static final int SENTINEL_MASK_24 = 0x7FFFFF;
    private static final int SENTINEL_MASK_31 = 0x7FFFFFFF;
    private static final int MAX_TRIE_WALK_STEPS = 1000;
    private static final HashSet<String> FLOAT_FIELDS = new HashSet<>();
    static {
        FLOAT_FIELDS.add("longitude");
        FLOAT_FIELDS.add("latitude");
    }

    private static final QzdbSearcher INSTANCE = new QzdbSearcher();

    private volatile MappedByteBuffer data;
    private int groupIndex = 0;
    private volatile String[] fieldNames;
    private final HashSet<String> floatFieldIndices = new HashSet<>();
    private String versionName = "";

    // Header fields
    private int flags;
    private boolean hasV4;
    private boolean hasV6;
    private boolean v4Node24;
    private boolean v6Node24;
    private int v6JumpBits = 16;
    private int poolCount;
    private int poolIdxSize;
    private int geoCount;
    private int rowCount;
    private int v4RecCount;
    private int v6RecCount;
    private int v4NodeCount;
    private int v6NodeCount;
    private int ipRowSize = 6;
    private int geoEntryGroupCount;

    // Offsets
    private long offV4Jump;
    private long offV4Nodes;
    private long offV6Jump;
    private long offV6Nodes;
    private long offIPRow;
    private long offGeoEntries;
    private long offPools;
    private long offMeta;
    private long offRowSchema;
    private long offGroupSchema;

    // Schema/layout cache
    private volatile int[] groupFieldCounts;
    private volatile long[] groupEntryCounts;
    private volatile int[] groupDimMasks;
    private volatile long[] groupEntryOffsets;

    private volatile int[] groupStrides;
    private volatile int[][] groupFieldWidths;
    private volatile int[][] groupFieldOffsets;
    private volatile boolean[][] groupFieldNative;
    private volatile int[][] groupFieldNativeType;

    private volatile String[][][] groupPools;
    private boolean poolsLoaded;

    public QzdbSearcher() {}

    public static QzdbSearcher getInstance() {
        return INSTANCE;
    }

    public synchronized void load(String dbPath) throws QzdbException {
        MappedByteBuffer mapped;
        try (RandomAccessFile raf = new RandomAccessFile(dbPath, "r");
             FileChannel ch = raf.getChannel()) {
            mapped = ch.map(FileChannel.MapMode.READ_ONLY, 0, ch.size());
        } catch (IOException e) {
            throw new QzdbException(ErrorCode.CORRUPTED, "Failed to read database file: " + dbPath, e);
        }
        parseHeader(mapped);
        data = mapped;
        poolsLoaded = false;
        ensurePoolsLoaded();
    }

    private int safeReadU16(MappedByteBuffer d, int off) {
        return (d.get(off) & 0xFF) | ((d.get(off + 1) & 0xFF) << 8);
    }

    private int safeReadU32(MappedByteBuffer d, int off) {
        return (d.get(off) & 0xFF) | ((d.get(off + 1) & 0xFF) << 8) |
               ((d.get(off + 2) & 0xFF) << 16) | ((d.get(off + 3) & 0xFF) << 24);
    }

    private long safeReadU64(MappedByteBuffer d, int off) {
        return (safeReadU32(d, off) & 0xFFFFFFFFL) | ((long) safeReadU32(d, off + 4) << 32);
    }

    private int safeReadU24(MappedByteBuffer d, int off) {
        return (d.get(off) & 0xFF) | ((d.get(off + 1) & 0xFF) << 8) | ((d.get(off + 2) & 0xFF) << 16);
    }

    private long safeReadU48(MappedByteBuffer d, int off) {
        return (d.get(off) & 0xFFL)
                | ((d.get(off + 1) & 0xFFL) << 8)
                | ((d.get(off + 2) & 0xFFL) << 16)
                | ((d.get(off + 3) & 0xFFL) << 24)
                | ((d.get(off + 4) & 0xFFL) << 32)
                | ((d.get(off + 5) & 0xFFL) << 40);
    }

    private int safeReadUintWidth(MappedByteBuffer d, int off, int width) {
        if (width <= 1) {
            return d.get(off) & 0xFF;
        } else if (width == 2) {
            return safeReadU16(d, off);
        } else if (width == 3) {
            return safeReadU24(d, off);
        } else {
            return safeReadU32(d, off);
        }
    }

    private void parseHeader(MappedByteBuffer d) throws QzdbException {
        if (d.capacity() < 192) {
            throw new QzdbException(ErrorCode.CORRUPTED, "File too small for QZDB header");
        }
        if (d.get(0) != 'Q' || d.get(1) != 'Z' || d.get(2) != 'D' || d.get(3) != 'B') {
            throw new QzdbException(ErrorCode.BAD_MAGIC, "Invalid magic, expected QZDB");
        }

        int fmtVer = d.get(4) & 0xFF;
        if (fmtVer < 1 || fmtVer > 6) {
            throw new QzdbException(ErrorCode.UNSUPPORTED, "Unsupported format version: " + fmtVer);
        }

        flags = safeReadU16(d, 8);
        hasV4 = (flags & 1) != 0;
        hasV6 = (flags & 2) != 0;
        v4Node24 = (flags & 0x10) != 0;
        v6Node24 = (flags & 0x20) != 0;

        v6JumpBits = d.get(11) & 0xFF;
        if (v6JumpBits == 0) v6JumpBits = 16;
        if (v6JumpBits < 16 || v6JumpBits > 20) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "v6JumpBits out of range [16,20]: " + v6JumpBits);
        }

        poolCount = d.get(12) & 0xFF;
        poolIdxSize = d.get(13) & 0xFF;
        if (poolIdxSize != 2 && poolIdxSize != 3) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "poolIdxSize must be 2 or 3, got " + poolIdxSize);
        }
        geoCount = safeReadU16(d, 14);
        rowCount = safeReadU32(d, 20);
        v4RecCount = safeReadU32(d, 24);
        v6RecCount = safeReadU32(d, 28);

        int hs = safeReadU32(d, 36);
        if (hs != 192) {
            throw new QzdbException(ErrorCode.BAD_HEADER, "Unexpected header size: " + hs);
        }

        offRowSchema = safeReadU64(d, 40);
        offGroupSchema = safeReadU64(d, 48);
        offV4Jump = safeReadU64(d, 64);
        offV4Nodes = safeReadU64(d, 72);
        offV6Jump = safeReadU64(d, 80);
        offV6Nodes = safeReadU64(d, 88);
        offIPRow = safeReadU64(d, 96);
        offGeoEntries = safeReadU64(d, 104);
        offPools = safeReadU64(d, 136);
        offMeta = safeReadU64(d, 144);

        v4NodeCount = safeReadU32(d, 152);
        v6NodeCount = safeReadU32(d, 156);
        ipRowSize = safeReadU32(d, 160);
        if (ipRowSize < 1 || ipRowSize > 64) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "ipRowSize out of range [1,64]: " + ipRowSize);
        }
        geoEntryGroupCount = safeReadU32(d, 164);
        if (geoEntryGroupCount < 1 || geoEntryGroupCount > 255) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "geoEntryGroupCount out of range [1,255]: " + geoEntryGroupCount);
        }

        // Bounds validation for section offsets
        long dlen = d.capacity();
        long v4NodeSize = v4Node24 ? 6 : 8;
        long v6NodeSize = v6Node24 ? 6 : 8;
        long v6JumpSize = (1L << v6JumpBits) * 4;

        if (offV4Jump > 0 && offV4Jump + 65536 * 4 > dlen) {
            throw new QzdbException(ErrorCode.OUT_OF_BOUNDS, "V4 jump table offset out of bounds");
        }
        if (offV4Nodes > 0 && offV4Nodes + (long) v4NodeCount * v4NodeSize > dlen) {
            throw new QzdbException(ErrorCode.OUT_OF_BOUNDS, "V4 nodes table offset out of bounds");
        }
        if (offV6Jump > 0 && offV6Jump + v6JumpSize > dlen) {
            throw new QzdbException(ErrorCode.OUT_OF_BOUNDS, "V6 jump table offset out of bounds");
        }
        if (offV6Nodes > 0 && offV6Nodes + (long) v6NodeCount * v6NodeSize > dlen) {
            throw new QzdbException(ErrorCode.OUT_OF_BOUNDS, "V6 nodes table offset out of bounds");
        }
        if (offIPRow > 0 && offIPRow + (long) rowCount * ipRowSize > dlen) {
            throw new QzdbException(ErrorCode.OUT_OF_BOUNDS, "IP row table offset out of bounds");
        }
        if (offGeoEntries > 0 && offGeoEntries + 16 > dlen) {
            throw new QzdbException(ErrorCode.OUT_OF_BOUNDS, "Geo entries section offset out of bounds");
        }
        if (offPools > 0 && offPools >= dlen) {
            throw new QzdbException(ErrorCode.OUT_OF_BOUNDS, "Pools section offset out of bounds");
        }
        if (offMeta > 0 && offMeta >= dlen) {
            throw new QzdbException(ErrorCode.OUT_OF_BOUNDS, "Meta section offset out of bounds");
        }
        if (offRowSchema > 0 && offRowSchema >= dlen) {
            throw new QzdbException(ErrorCode.OUT_OF_BOUNDS, "Row schema section offset out of bounds");
        }
        if (offGroupSchema > 0 && offGroupSchema + 2 > dlen) {
            throw new QzdbException(ErrorCode.OUT_OF_BOUNDS, "Group schema section offset out of bounds");
        }

        groupEntryOffsets = new long[4];
        for (int i = 0; i < 4; i++) {
            groupEntryOffsets[i] = safeReadU48(d, 168 + i * 6);
        }

        int gmOff = (int) offGeoEntries;
        int groupCount = d.get(gmOff) & 0xFF;
        gmOff++;

        int actualGroups = Math.min(groupCount, Math.max(1, geoEntryGroupCount));
        if (actualGroups > 4) actualGroups = 4;
        groupFieldCounts = new int[actualGroups];
        groupEntryCounts = new long[actualGroups];
        groupDimMasks = new int[actualGroups];

        for (int gi = 0; gi < actualGroups; gi++) {
            groupFieldCounts[gi] = d.get(gmOff) & 0xFF;
            gmOff++;
            if (fmtVer == 1 || fmtVer >= 4) {
                groupEntryCounts[gi] = safeReadU32(d, gmOff) & 0xFFFFFFFFL;
                gmOff += 4;
            } else {
                groupEntryCounts[gi] = safeReadU16(d, gmOff) & 0xFFFFL;
                gmOff += 2;
            }
            if (fmtVer == 1 || fmtVer >= 3) {
                groupDimMasks[gi] = safeReadU16(d, gmOff);
                gmOff += 2;
            } else {
                groupDimMasks[gi] = (gi != 2) ? 0x01 : 0x02;
            }
        }

        groupStrides = new int[actualGroups];
        groupFieldWidths = new int[actualGroups][];
        groupFieldOffsets = new int[actualGroups][];
        groupFieldNative = new boolean[actualGroups][];
        groupFieldNativeType = new int[actualGroups][];
        int[][] groupFieldIds = new int[actualGroups][];
        int[][] groupPoolSectionIds = new int[actualGroups][];

        if (offGroupSchema > 0) {
            int sp = (int) offGroupSchema;
            int gsGroupCount = safeReadU16(d, sp);
            sp += 2;
            int maxGsGroups = Math.min(gsGroupCount, actualGroups);
            for (int gi = 0; gi < maxGsGroups; gi++) {
                sp += 2; // skip groupId
                int fldCount = safeReadU16(d, sp);
                sp += 2;
                sp += 4; // skip entryCount
                int stride = safeReadU32(d, sp);
                sp += 4;
                sp += 4; // skip flags

                if (gi < actualGroups) {
                    groupStrides[gi] = stride;
                    int[] widths = new int[fldCount];
                    int[] offsets = new int[fldCount];
                    boolean[] natives = new boolean[fldCount];
                    int[] natTypes = new int[fldCount];
                    int[] fieldIds = new int[fldCount];
                    int[] poolSectionIds = new int[fldCount];
                    for (int fi = 0; fi < fldCount; fi++) {
                        fieldIds[fi] = safeReadU16(d, sp);
                        sp += 2;
                        widths[fi] = d.get(sp) & 0xFF;
                        sp++;
                        int fieldFlags = d.get(sp) & 0xFF;
                        sp++;
                        natives[fi] = (fieldFlags & 0x01) != 0;
                        natTypes[fi] = (fieldFlags >> 1) & 0x03;
                        offsets[fi] = safeReadU32(d, sp);
                        sp += 4;
                        poolSectionIds[fi] = safeReadU32(d, sp);
                        sp += 4;
                    }
                    groupFieldWidths[gi] = widths;
                    groupFieldOffsets[gi] = offsets;
                    groupFieldNative[gi] = natives;
                    groupFieldNativeType[gi] = natTypes;
                    groupFieldIds[gi] = fieldIds;
                    groupPoolSectionIds[gi] = poolSectionIds;
                } else {
                    sp += fldCount * 12;
                }
            }
        }

        for (int g = 0; g < actualGroups; g++) {
            if (groupStrides[g] == 0) {
                groupStrides[g] = groupFieldCounts[g] * poolIdxSize;
            }
            if (groupFieldWidths[g] == null) {
                groupFieldWidths[g] = new int[groupFieldCounts[g]];
                java.util.Arrays.fill(groupFieldWidths[g], poolIdxSize);
            }
            if (groupFieldOffsets[g] == null) {
                groupFieldOffsets[g] = new int[groupFieldCounts[g]];
                for (int i = 0; i < groupFieldCounts[g]; i++) {
                    groupFieldOffsets[g][i] = i * poolIdxSize;
                }
            }
            if (groupFieldNative[g] == null) {
                groupFieldNative[g] = new boolean[groupFieldCounts[g]];
            }
            if (groupFieldNativeType[g] == null) {
                groupFieldNativeType[g] = new int[groupFieldCounts[g]];
            }
        }

        resolveFieldNames(d);
        poolsLoaded = false;
        groupPools = null;
    }

    private void resolveFieldNames(MappedByteBuffer d) {
        if ((flags & 4) != 0 && offMeta > 0 && offMeta + 4 <= d.capacity()) {
            String[] fNames = null;
            int pos = (int) offMeta;
            while (pos + 4 <= d.capacity()) {
                int t = d.get(pos) & 0xFF;
                int length = safeReadU16(d, pos + 2);
                if (t == 0 || length == 0) break;
                if (pos + 4 + length > d.capacity()) break;
                byte[] bytes = new byte[length];
                d.position(pos + 4);
                d.get(bytes);
                String val = new String(bytes, StandardCharsets.UTF_8);
                if (t == 1) {
                    versionName = val;
                } else if (t == 2) {
                    fNames = val.split("\\|");
                }
                pos += 4 + length;
            }

            if (fNames != null && fNames.length == groupFieldCounts[0]) {
                fieldNames = fNames;
                floatFieldIndices.clear();
                for (String n : fNames) {
                    if (FLOAT_FIELDS.contains(n)) {
                        floatFieldIndices.add(n);
                    }
                }
                return;
            }
        }

        fieldNames = new String[groupFieldCounts[0]];
        for (int i = 0; i < fieldNames.length; i++) {
            fieldNames[i] = "field_" + i;
        }
        floatFieldIndices.clear();
    }

    private synchronized void ensurePoolsLoaded() throws QzdbException {
        if (poolsLoaded) return;
        poolsLoaded = true;

        int groupCount = groupFieldCounts.length;
        groupPools = new String[groupCount][][];

        if (offPools <= 0) return;

        int poolCursor = (int) offPools;
        int poolEnd = offMeta > 0 ? (int) offMeta : data.capacity();
        MappedByteBuffer d = data;

        for (int g = 0; g < groupCount; g++) {
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
                int count = safeReadU32(d, poolCursor);
                poolCursor += 4;
                if (offRowSchema > 0) {
                    poolCursor += 4;
                }
                if (count == 0) {
                    groupPoolList[f] = new String[0];
                    continue;
                }
                if (count < 0) {
                    throw new QzdbException(ErrorCode.CORRUPTED, "Invalid pool count: " + count);
                }

                int[] offsets = new int[count + 1];
                for (int o = 0; o <= count; o++) {
                    offsets[o] = safeReadU32(d, poolCursor);
                    poolCursor += 4;
                }

                String[] strings = new String[count];
                for (int s = 0; s < count; s++) {
                    int start = offsets[s];
                    int end = offsets[s + 1];
                    int length = end - start;
                    if (length > 0) {
                        byte[] bytes = new byte[length];
                        d.position(poolCursor + start);
                        d.get(bytes);
                        strings[s] = new String(bytes, StandardCharsets.UTF_8);
                    } else {
                        strings[s] = "";
                    }
                }
                poolCursor += offsets[count];
                groupPoolList[f] = strings;
            }
            groupPools[g] = groupPoolList;
        }
    }

    private int getV4Child(int nodeIdx, int bit) {
        if (v4Node24) {
            int nodeOffset = (int) offV4Nodes + nodeIdx * 6;
            int offset = bit == 0 ? nodeOffset : nodeOffset + 3;
            int val = (data.get(offset) & 0xFF) | ((data.get(offset + 1) & 0xFF) << 8) | ((data.get(offset + 2) & 0xFF) << 16);
            if ((val & 0x800000) != 0) {
                return (val & SENTINEL_MASK_24) | SENTINEL;
            }
            return val;
        } else {
            int childOff = (int) offV4Nodes + nodeIdx * 8 + bit * 4;
            return safeReadU32(data, childOff);
        }
    }

    private int getV6Child(int nodeIdx, int bit) {
        if (v6Node24) {
            int nodeOffset = (int) offV6Nodes + nodeIdx * 6;
            int offset = bit == 0 ? nodeOffset : nodeOffset + 3;
            int val = (data.get(offset) & 0xFF) | ((data.get(offset + 1) & 0xFF) << 8) | ((data.get(offset + 2) & 0xFF) << 16);
            if ((val & 0x800000) != 0) {
                return (val & SENTINEL_MASK_24) | SENTINEL;
            }
            return val;
        } else {
            int childOff = (int) offV6Nodes + nodeIdx * 8 + bit * 4;
            return safeReadU32(data, childOff);
        }
    }

    private int trieWalkV4(int ipInt) {
        int hi16 = (ipInt >>> 16) & 0xFFFF;
        int ptr = safeReadU32(data, (int) (offV4Jump + hi16 * 4L));

        if (ptr == 0) return 0;
        if ((ptr & SENTINEL) != 0) return ptr & SENTINEL_MASK_31;

        int idx = ptr;
        int suffix = (ipInt & 0xFFFF) << 16;
        int steps = 0;

        while (true) {
            if (++steps >= MAX_TRIE_WALK_STEPS) return 0;
            int bit = (suffix >>> 31) & 1;
            int child = getV4Child(idx, bit);

            if (child == 0) return 0;
            if ((child & SENTINEL) != 0) return child & SENTINEL_MASK_31;

            idx = child;
            suffix <<= 1;
        }
    }

    private int trieWalkV6(long ipHigh, long ipLow) {
        int idxJump = (int)(ipHigh >>> (64 - v6JumpBits)) & ((1 << v6JumpBits) - 1);
        int ptr = safeReadU32(data, (int) (offV6Jump + idxJump * 4L));

        if (ptr == 0) return 0;
        if ((ptr & SENTINEL) != 0) return ptr & SENTINEL_MASK_31;

        int idx = ptr;
        int depth = v6JumpBits;
        int steps = 0;

        while (depth < 128) {
            if (++steps >= MAX_TRIE_WALK_STEPS) return 0;
            int bit = (depth <= 63)
                ? (int)((ipHigh >>> (63 - depth)) & 1)
                : (int)((ipLow >>> (127 - depth)) & 1);
            int child = getV6Child(idx, bit);

            if (child == 0) return 0;
            if ((child & SENTINEL) != 0) return child & SENTINEL_MASK_31;

            idx = child;
            depth += 1;
        }
        return 0;
    }

    private int[] readIPRow(int rowId) {
        if (rowId <= 0 || rowId >= rowCount) return new int[]{0, 0, 0};
        int off = (int) offIPRow + rowId * ipRowSize;
        int geoId = safeReadU24(data, off);
        int asnId = safeReadU24(data, off + 3);

        int usageTypeId = 0;
        if (ipRowSize >= 9) {
            usageTypeId = safeReadU24(data, off + 6);
        }
        return new int[]{geoId, asnId, usageTypeId};
    }

    private IpLocation resolveRowId(int rowId, int groupIndex) {
        int[] ids = readIPRow(rowId);
        int geoId = ids[0];
        int asnId = ids[1];
        int usageTypeId = ids[2];
        int mask = groupIndex < groupDimMasks.length ? groupDimMasks[groupIndex] : 0;

        int entryId = geoId;
        if ((mask & 0x02) != 0) {
            entryId = asnId;
        } else if ((mask & 0x04) != 0) {
            entryId = usageTypeId;
        }

        if (entryId == 0) return null;
        return resolveGeo(entryId, groupIndex);
    }

    private IpLocation resolveGeo(int entryId, int groupIndex) {
        if (groupIndex < 0 || groupIndex >= groupFieldCounts.length) return null;
        if (entryId < 0) return null;
        if (entryId >= groupEntryCounts[groupIndex]) return null;

        ensurePoolsLoaded();

        int fieldCount = groupFieldCounts[groupIndex];
        if (fieldCount <= 0) return null;

        long groupEntryStart = offGeoEntries + groupEntryOffsets[groupIndex];
        int stride = groupStrides[groupIndex];
        long entryOffset = groupEntryStart + (long) entryId * stride;
        MappedByteBuffer d = data;

        int[] widths = groupFieldWidths[groupIndex];
        int[] baseOffsets = groupFieldOffsets[groupIndex];
        boolean[] natives = groupFieldNative[groupIndex];
        int[] natTypes = groupFieldNativeType[groupIndex];

        String[] values = new String[fieldCount];
        for (int i = 0; i < fieldCount; i++) {
            int w = widths[i];
            int fo = (int) (entryOffset + baseOffsets[i]);
            boolean isNative = natives != null && i < natives.length && natives[i];

            String val;
            if (isNative) {
                int t = (natTypes != null && i < natTypes.length) ? natTypes[i] : 0;
                if (t == 1) {
                    if (w == 4) {
                        int bits = (d.get(fo) & 0xFF) | ((d.get(fo + 1) & 0xFF) << 8) | ((d.get(fo + 2) & 0xFF) << 16) | ((d.get(fo + 3) & 0xFF) << 24);
                        val = Float.toString(Float.intBitsToFloat(bits));
                    } else {
                        byte[] bytes = new byte[8];
                        d.position(fo);
                        d.get(bytes);
                        val = Double.toString(ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getDouble());
                    }
                } else {
                    int valNum = safeReadUintWidth(d, fo, w);
                    val = Integer.toString(valNum);
                }
            } else {
                int idx = safeReadUintWidth(d, fo, w);
                String[][] gp = groupPools[groupIndex];

                // Pools are indexed by field position, not poolSectionId
                if (gp != null && i < gp.length && idx >= 0 && idx < gp[i].length) {
                    val = gp[i][idx];
                } else {
                    val = "";
                }
            }

            String fname = i < fieldNames.length ? fieldNames[i] : "field_" + i;
            if (floatFieldIndices.contains(fname) && !val.isEmpty()) {
                try {
                    val = String.format(Locale.US, "%.6f", Double.parseDouble(val));
                } catch (NumberFormatException ignored) {}
            }
            values[i] = val;
        }

        return new IpLocation(values);
    }

    public IpLocation find(String ipStr) {
        if (ipStr == null || ipStr.isEmpty()) return null;
        ParseResult result = fastParseIp(ipStr);
        if (result == null) return null;
        if (result.isV4) return findUint(result.v4);
        return findV6Uint(result.v6High, result.v6Low);
    }

    public IpLocation findUint(int ipInt) {
        if (!hasV4) return null;
        int rowId = trieWalkV4(ipInt);
        if (rowId == 0) return null;
        return resolveRowId(rowId, groupIndex);
    }

    /** @deprecated Use findV6Uint(long, long) to avoid BigInteger allocation */
    @Deprecated
    public IpLocation findV6Uint(BigInteger ipInt) {
        if (!hasV6) return null;
        long ipHigh = ipInt.shiftRight(64).longValue();
        long ipLow = ipInt.longValue();
        return findV6Uint(ipHigh, ipLow);
    }

    private IpLocation findV6Uint(long ipHigh, long ipLow) {
        if (!hasV6) return null;
        int rowId = trieWalkV6(ipHigh, ipLow);
        if (rowId == 0) return null;
        return resolveRowId(rowId, groupIndex);
    }

    /** Lookup row_id only (trie walk, no data materialization). Returns 0 if not found. */
    public int lookupRowId(String ipStr) {
        if (ipStr == null || ipStr.isEmpty()) return 0;
        ParseResult result = fastParseIp(ipStr);
        if (result == null) return 0;
        if (result.isV4) return lookupRowIdUint(result.v4);
        return lookupRowIdV6(result.v6High, result.v6Low);
    }

    /** Lookup row_id for a pre-parsed IPv4 integer. */
    public int lookupRowIdUint(int ipInt) {
        if (!hasV4) return 0;
        return trieWalkV4(ipInt);
    }

    /** Lookup row_id for a pre-parsed IPv6 (high, low) pair. */
    public int lookupRowIdV6(long ipHigh, long ipLow) {
        if (!hasV6) return 0;
        return trieWalkV6(ipHigh, ipLow);
    }

    /**
     * Field projection: return only the requested fields (avoids resolving unused fields).
     * Returns IpLocation where only projected fields are populated (others remain null).
     */
    public IpLocation findFields(String ipStr, String[] fieldNames) {
        if (fieldNames == null || fieldNames.length == 0)
            return find(ipStr);
        int rowId = lookupRowId(ipStr);
        if (rowId == 0) return null;
        return resolveGeoFields(rowId, groupIndex, fieldNames);
    }

    private IpLocation resolveGeoFields(int rowId, int groupIndex, String[] fieldNames) {
        int[] ids = readIPRow(rowId);
        int geoId = ids[0], asnId = ids[1], usageTypeId = ids[2];
        int mask = groupIndex < groupDimMasks.length ? groupDimMasks[groupIndex] : 0;
        int entryId = (mask & 0x02) != 0 ? asnId : (mask & 0x04) != 0 ? usageTypeId : geoId;
        if (entryId == 0 || groupIndex < 0 || groupIndex >= groupFieldCounts.length) return null;
        if (entryId >= groupEntryCounts[groupIndex]) return null;

        ensurePoolsLoaded();
        int fieldCount = groupFieldCounts[groupIndex];
        if (fieldCount <= 0) return null;

        long groupEntryStart = offGeoEntries + groupEntryOffsets[groupIndex];
        int stride = groupStrides[groupIndex];
        long entryOffset = groupEntryStart + (long) entryId * stride;
        MappedByteBuffer d = data;
        int[] widths = groupFieldWidths[groupIndex];
        int[] baseOffsets = groupFieldOffsets[groupIndex];
        boolean[] natives = groupFieldNative[groupIndex];
        int[] natTypes = groupFieldNativeType[groupIndex];

        String[] values = new String[fieldCount];
        for (int fi = 0; fi < fieldNames.length; fi++) {
            for (int i = 0; i < this.fieldNames.length; i++) {
                if (!this.fieldNames[i].equals(fieldNames[fi])) continue;
                if (i < 0 || i >= fieldCount) break;
                int w = widths[i];
                int fo = (int) (entryOffset + baseOffsets[i]);
                boolean isNative = natives != null && i < natives.length && natives[i];
                if (isNative) {
                    int t = (natTypes != null && i < natTypes.length) ? natTypes[i] : 0;
                    if (t == 1) {
                        if (w == 4) {
                            int bits = (d.get(fo) & 0xFF) | ((d.get(fo + 1) & 0xFF) << 8) | ((d.get(fo + 2) & 0xFF) << 16) | ((d.get(fo + 3) & 0xFF) << 24);
                            values[i] = Float.toString(Float.intBitsToFloat(bits));
                        } else {
                            byte[] bytes = new byte[8];
                            d.position(fo);
                            d.get(bytes);
                            values[i] = Double.toString(ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getDouble());
                        }
                    } else {
                        values[i] = Integer.toString(safeReadUintWidth(d, fo, w));
                    }
                } else {
                    int idx = safeReadUintWidth(d, fo, w);
                    String[][] gp = groupPools[groupIndex];
                    values[i] = (gp != null && i < gp.length && idx >= 0 && idx < gp[i].length) ? gp[i][idx] : "";
                }
            }
        }
        return new IpLocation(values);
    }

    /** Atomically reload database from a new path. Thread-safe (volatile fields). */
    public void reload(String path) throws IOException, QzdbException {
        load(path);
    }

    /**
     * Lookup raw entry IDs from a row_id.
     * Returns int[]{geoId, asnId, usageTypeId} on success, or null if row_id is invalid.
     */
    public int[] lookupIds(int rowId) {
        if (rowId <= 0 || rowId >= rowCount) return null;
        return readIPRow(rowId);
    }

    public String findStr(String ipStr) {
        IpLocation info = find(ipStr);
        if (info == null) return "";
        StringBuilder sb = new StringBuilder();
        String[] vals = info.getValues();
        for (int i = 0; i < vals.length; i++) {
            if (i > 0) sb.append('|');
            sb.append(vals[i]);
        }
        return sb.toString();
    }

    public String[] getFieldNames() {
        return fieldNames == null ? new String[0] : fieldNames.clone();
    }

    public int getVersionCode() {
        return switch (poolCount) {
            case 6 -> 1;
            case 7 -> 2;
            case 25 -> 3;
            default -> 3;
        };
    }

    public int getPoolCount() {
        return poolCount;
    }

    public boolean verifyCrc() {
        if (data == null || data.capacity() < 20) return false;
        int stored = safeReadU32(data, 16);
        // Segmented CRC to avoid cloning the full buffer
        CRC32 crc = new CRC32();
        byte[] head = new byte[16];
        data.position(0);
        data.get(head);
        crc.update(head);
        crc.update(new byte[4]);
        int tailLen = data.capacity() - 20;
        if (tailLen > 0) {
            byte[] tail = new byte[tailLen];
            data.get(tail);
            crc.update(tail);
        }
        return stored == (int) crc.getValue();
    }

    private static final byte[] HEX = new byte[128];
    static {
        for (int i = 0; i < 10; i++) HEX[48 + i] = (byte) i;
        for (int i = 0; i < 6; i++) { HEX[97 + i] = (byte) (10 + i); HEX[65 + i] = (byte) (10 + i); }
    }

    private static class ParseResult {
        final int v4;
        final long v6High, v6Low;
        final boolean isV4;
        ParseResult(int v4) { this.v4 = v4; this.v6High = 0; this.v6Low = 0; this.isV4 = true; }
        ParseResult(long v6High, long v6Low) { this.v4 = 0; this.v6High = v6High; this.v6Low = v6Low; this.isV4 = false; }
    }

    private static long fastParseIpv4(String s) {
        int n = s.length();
        if (n == 0 || s.charAt(n - 1) == '.') return -1L;
        int result = 0, val = 0, dots = 0, start = 0;
        for (int i = 0; i <= n; i++) {
            char c = i < n ? s.charAt(i) : '.';
            if (c == '.') {
                int segLen = i - start;
                if (segLen == 0 || segLen > 3) return -1L;
                if (segLen > 1 && s.charAt(start) == '0') return -1L;
                val = 0;
                for (int j = start; j < i; j++) {
                    char d = s.charAt(j);
                    if (d < '0' || d > '9') return -1L;
                    val = val * 10 + (d - '0');
                }
                if (val > 255) return -1L;
                result = (result << 8) | val;
                dots++;
                start = i + 1;
            }
        }
        return dots == 4 ? (result & 0xFFFFFFFFL) : -1L;
    }

    private static ParseResult fastParseIp(String s) {
        if (s == null) return null;
        int n = s.length();
        // Reject whitespace — SSRF-safe, cross-language consistent
        for (int i = 0; i < n; i++) {
            char c = s.charAt(i);
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f')
                return null;
        }
        if (n == 0 || n > 45) return null;
        if (!s.contains(":")) {
            long v4 = fastParseIpv4(s);
            if (v4 < 0) return null;
            return new ParseResult((int) v4);
        }
        if (s.contains("%")) return null;
        int dc = s.indexOf("::");
        if (dc >= 0 && s.indexOf("::", dc + 2) >= 0) return null;
        String lft = dc >= 0 ? s.substring(0, dc) : s;
        String rgt = dc >= 0 ? s.substring(dc + 2) : "";
        String[] lg = lft.isEmpty() ? new String[0] : lft.split(":");
        String[] rg = rgt.isEmpty() ? new String[0] : rgt.split(":");
        if (lg.length == 1 && lg[0].isEmpty()) lg = new String[0];
        if (rg.length == 1 && rg[0].isEmpty()) rg = new String[0];
        for (String g : lg) if (g.isEmpty()) return null;
        for (String g : rg) if (g.isEmpty()) return null;
        java.util.ArrayList<String> allg = new java.util.ArrayList<>(lg.length + rg.length);
        for (String g : lg) allg.add(g);
        for (String g : rg) allg.add(g);
        boolean hasV4 = false;
        int v4Int = 0;
        int last = allg.size() - 1;
        if (last >= 0 && allg.get(last).contains(".")) {
            long parsedV4 = fastParseIpv4(allg.get(last));
            if (parsedV4 < 0) return null;
            v4Int = (int) parsedV4;
            hasV4 = true;
            allg.remove(last);
        }
        int ng = allg.size();
        int v4Slots = hasV4 ? 2 : 0;
        if (dc >= 0) {
            if (ng + v4Slots > 7) return null;
        } else {
            if (ng + v4Slots != 8) return null;
        }
        for (String g : allg) {
            int gl = g.length();
            if (gl == 0 || gl > 4) return null;
            for (int j = 0; j < gl; j++) {
                char cc = g.charAt(j);
                if (cc >= 128 || (HEX[cc] == 0 && cc != '0')) return null;
            }
        }
        int zeros = 8 - ng - v4Slots;
        byte[] buf = new byte[16];
        int off = 0;
        for (String g : lg) {
            int v = 0;
            for (int j = 0; j < g.length(); j++) v = (v << 4) | HEX[g.charAt(j)];
            buf[off] = (byte) (v >> 8); buf[off + 1] = (byte) v;
            off += 2;
        }
        off += zeros * 2;
        for (String g : rg) {
            int v = 0;
            for (int j = 0; j < g.length(); j++) v = (v << 4) | HEX[g.charAt(j)];
            buf[off] = (byte) (v >> 8); buf[off + 1] = (byte) v;
            off += 2;
        }
        if (hasV4) { buf[12] = (byte) (v4Int >> 24); buf[13] = (byte) (v4Int >> 16); buf[14] = (byte) (v4Int >> 8); buf[15] = (byte) v4Int; }
        if (buf[10] == (byte) 0xff && buf[11] == (byte) 0xff &&
            buf[0] == 0 && buf[1] == 0 && buf[2] == 0 && buf[3] == 0 &&
            buf[4] == 0 && buf[5] == 0 && buf[6] == 0 && buf[7] == 0 &&
            buf[8] == 0 && buf[9] == 0) {
            return new ParseResult(((buf[12] & 0xFF) << 24) | ((buf[13] & 0xFF) << 16) | ((buf[14] & 0xFF) << 8) | (buf[15] & 0xFF));
        }
        long v6High = 0, v6Low = 0;
        for (int i = 0; i < 8; i++) v6High = (v6High << 8) | (buf[i] & 0xFF);
        for (int i = 8; i < 16; i++) v6Low = (v6Low << 8) | (buf[i] & 0xFF);
        return new ParseResult(v6High, v6Low);
    }
}
