package qzdb;

import java.io.IOException;
import java.io.RandomAccessFile;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.math.BigInteger;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.util.HashSet;
import java.util.zip.CRC32;

public class QzdbSearcher {

    private static final int SENTINEL = 0x80000000;
    private static final HashSet<String> FLOAT_FIELDS = new HashSet<>();
    static {
        FLOAT_FIELDS.add("longitude");
        FLOAT_FIELDS.add("latitude");
    }

    private static final QzdbSearcher INSTANCE = new QzdbSearcher();

    private volatile byte[] data;
    private int groupIndex = 0;
    private String[] fieldNames;
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
    private int[] groupFieldCounts;
    private long[] groupEntryCounts;
    private int[] groupDimMasks;
    private long[] groupEntryOffsets;

    private int[] groupStrides;
    private int[][] groupFieldWidths;
    private int[][] groupFieldOffsets;
    private boolean[][] groupFieldNative;
    private int[][] groupFieldNativeType;

    private String[][][] groupPools;
    private boolean poolsLoaded;

    private QzdbSearcher() {}

    public static QzdbSearcher getInstance() {
        return INSTANCE;
    }

    public synchronized void load(String dbPath) throws IOException {
        byte[] raw;
        try (RandomAccessFile raf = new RandomAccessFile(dbPath, "r");
             FileChannel ch = raf.getChannel()) {
            ByteBuffer buf = ByteBuffer.allocate((int) ch.size());
            ch.read(buf);
            raw = buf.array();
        }
        parseHeader(raw);
        data = raw;
        poolsLoaded = false;
        ensurePoolsLoaded();
    }

    private int readU16(byte[] d, int off) {
        return (d[off] & 0xFF) | ((d[off + 1] & 0xFF) << 8);
    }

    private int readU32(byte[] d, int off) {
        return (d[off] & 0xFF) | ((d[off + 1] & 0xFF) << 8) |
               ((d[off + 2] & 0xFF) << 16) | ((d[off + 3] & 0xFF) << 24);
    }

    private long readU64(byte[] d, int off) {
        return (readU32(d, off) & 0xFFFFFFFFL) | ((long) readU32(d, off + 4) << 32);
    }

    private int readU24(byte[] d, int off) {
        return (d[off] & 0xFF) | ((d[off + 1] & 0xFF) << 8) | ((d[off + 2] & 0xFF) << 16);
    }

    private long readU48(byte[] d, int off) {
        return (d[off] & 0xFFL)
                | ((d[off + 1] & 0xFFL) << 8)
                | ((d[off + 2] & 0xFFL) << 16)
                | ((d[off + 3] & 0xFFL) << 24)
                | ((d[off + 4] & 0xFFL) << 32)
                | ((d[off + 5] & 0xFFL) << 40);
    }

    private int readUintWidth(byte[] d, int off, int width) {
        if (width <= 1) {
            return d[off] & 0xFF;
        } else if (width == 2) {
            return readU16(d, off);
        } else if (width == 3) {
            return readU24(d, off);
        } else {
            return readU32(d, off);
        }
    }

    private void parseHeader(byte[] d) {
        if (d.length < 192) {
            throw new RuntimeException("File too small for QZDB header");
        }
        if ((d[0] != 'Q' || d[1] != 'Z' || d[2] != 'D' || d[3] != 'B') &&
            (d[0] != 'Q' || d[1] != 'Z' || d[2] != '2' || d[3] != '0')) {
            throw new RuntimeException("Invalid magic, expected QZDB");
        }

        int fmtVer = d[4] & 0xFF;
        if (fmtVer < 1 || fmtVer > 6) {
            throw new RuntimeException("Unsupported format version: " + fmtVer);
        }

        flags = readU16(d, 8);
        hasV4 = (flags & 1) != 0;
        hasV6 = (flags & 2) != 0;
        v4Node24 = (flags & 0x10) != 0;
        v6Node24 = (flags & 0x20) != 0;

        v6JumpBits = d[11] & 0xFF;
        if (v6JumpBits == 0) v6JumpBits = 16;

        poolCount = d[12] & 0xFF;
        poolIdxSize = d[13] & 0xFF;
        geoCount = readU16(d, 14);
        rowCount = readU32(d, 20);
        v4RecCount = readU32(d, 24);
        v6RecCount = readU32(d, 28);

        int hs = readU32(d, 36);
        if (hs != 192) {
            throw new RuntimeException("Unexpected header size: " + hs);
        }

        offRowSchema = readU64(d, 40);
        offGroupSchema = readU64(d, 48);
        offV4Jump = readU64(d, 64);
        offV4Nodes = readU64(d, 72);
        offV6Jump = readU64(d, 80);
        offV6Nodes = readU64(d, 88);
        offIPRow = readU64(d, 96);
        offGeoEntries = readU64(d, 104);
        offPools = readU64(d, 136);
        offMeta = readU64(d, 144);

        v4NodeCount = readU32(d, 152);
        v6NodeCount = readU32(d, 156);
        ipRowSize = readU32(d, 160);
        geoEntryGroupCount = readU32(d, 164);

        groupEntryOffsets = new long[4];
        for (int i = 0; i < 4; i++) {
            groupEntryOffsets[i] = readU48(d, 168 + i * 6);
        }

        int gmOff = (int) offGeoEntries;
        int groupCount = d[gmOff] & 0xFF;
        gmOff++;

        int actualGroups = Math.min(groupCount, Math.max(1, geoEntryGroupCount));
        if (actualGroups > 4) actualGroups = 4;
        groupFieldCounts = new int[actualGroups];
        groupEntryCounts = new long[actualGroups];
        groupDimMasks = new int[actualGroups];

        for (int gi = 0; gi < actualGroups; gi++) {
            groupFieldCounts[gi] = d[gmOff] & 0xFF;
            gmOff++;
            if (fmtVer == 1 || fmtVer >= 4) {
                groupEntryCounts[gi] = readU32(d, gmOff) & 0xFFFFFFFFL;
                gmOff += 4;
            } else {
                groupEntryCounts[gi] = readU16(d, gmOff) & 0xFFFFL;
                gmOff += 2;
            }
            if (fmtVer == 1 || fmtVer >= 3) {
                groupDimMasks[gi] = readU16(d, gmOff);
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

        if (offGroupSchema > 0) {
            int sp = (int) offGroupSchema;
            int gsGroupCount = readU16(d, sp);
            sp += 2;
            int maxGsGroups = Math.min(gsGroupCount, actualGroups);
            for (int gi = 0; gi < maxGsGroups; gi++) {
                sp += 2; // skip groupId
                int fldCount = readU16(d, sp);
                sp += 2;
                sp += 4; // skip entryCount
                int stride = readU32(d, sp);
                sp += 4;
                sp += 4; // skip flags

                if (gi < actualGroups) {
                    groupStrides[gi] = stride;
                    int[] widths = new int[fldCount];
                    int[] offsets = new int[fldCount];
                    boolean[] natives = new boolean[fldCount];
                    int[] natTypes = new int[fldCount];
                    for (int fi = 0; fi < fldCount; fi++) {
                        sp += 2; // skip fieldId
                        widths[fi] = d[sp] & 0xFF;
                        sp++;
                        int fieldFlags = d[sp] & 0xFF;
                        sp++;
                        natives[fi] = (fieldFlags & 0x01) != 0;
                        natTypes[fi] = (fieldFlags >> 1) & 0x03;
                        offsets[fi] = readU32(d, sp);
                        sp += 4;
                        sp += 4; // skip poolSectionId
                    }
                    groupFieldWidths[gi] = widths;
                    groupFieldOffsets[gi] = offsets;
                    groupFieldNative[gi] = natives;
                    groupFieldNativeType[gi] = natTypes;
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

    private void resolveFieldNames(byte[] d) {
        if ((flags & 4) != 0 && offMeta > 0 && offMeta + 4 <= d.length) {
            String[] fNames = null;
            int pos = (int) offMeta;
            while (pos + 4 <= d.length) {
                int t = d[pos] & 0xFF;
                int length = readU16(d, pos + 2);
                if (t == 0 || length == 0) break;
                if (pos + 4 + length > d.length) break;
                String val = new String(d, pos + 4, length, StandardCharsets.UTF_8);
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

    private synchronized void ensurePoolsLoaded() {
        if (poolsLoaded) return;
        poolsLoaded = true;

        int groupCount = groupFieldCounts.length;
        groupPools = new String[groupCount][][];

        if (offPools <= 0) return;

        int poolCursor = (int) offPools;
        int poolEnd = offMeta > 0 ? (int) offMeta : data.length;
        byte[] d = data;

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
                int count = readU32(d, poolCursor);
                poolCursor += 4;
                if (offRowSchema > 0) {
                    poolCursor += 4;
                }
                if (count == 0) {
                    groupPoolList[f] = new String[0];
                    continue;
                }

                int[] offsets = new int[count + 1];
                for (int o = 0; o <= count; o++) {
                    offsets[o] = readU32(d, poolCursor);
                    poolCursor += 4;
                }

                String[] strings = new String[count];
                for (int s = 0; s < count; s++) {
                    int start = offsets[s];
                    int end = offsets[s + 1];
                    int length = end - start;
                    if (length > 0) {
                        strings[s] = new String(d, poolCursor + start, length, StandardCharsets.UTF_8);
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
            int val = (data[offset] & 0xFF) | ((data[offset + 1] & 0xFF) << 8) | ((data[offset + 2] & 0xFF) << 16);
            if ((val & 0x800000) != 0) {
                return (val & 0x7FFFFF) | SENTINEL;
            }
            return val;
        } else {
            int childOff = (int) offV4Nodes + nodeIdx * 8 + bit * 4;
            return readU32(data, childOff);
        }
    }

    private int getV6Child(int nodeIdx, int bit) {
        if (v6Node24) {
            int nodeOffset = (int) offV6Nodes + nodeIdx * 6;
            int offset = bit == 0 ? nodeOffset : nodeOffset + 3;
            int val = (data[offset] & 0xFF) | ((data[offset + 1] & 0xFF) << 8) | ((data[offset + 2] & 0xFF) << 16);
            if ((val & 0x800000) != 0) {
                return (val & 0x7FFFFF) | SENTINEL;
            }
            return val;
        } else {
            int childOff = (int) offV6Nodes + nodeIdx * 8 + bit * 4;
            return readU32(data, childOff);
        }
    }

    private int trieWalkV4(int ipInt) {
        int hi16 = (ipInt >>> 16) & 0xFFFF;
        int ptr = readU32(data, (int) (offV4Jump + hi16 * 4L));

        if (ptr == 0) return 0;
        if ((ptr & SENTINEL) != 0) return ptr & 0x7FFFFFFF;

        int idx = ptr;
        int suffix = (ipInt & 0xFFFF) << 16;

        while (true) {
            int bit = (suffix >>> 31) & 1;
            int child = getV4Child(idx, bit);

            if (child == 0) return 0;
            if ((child & SENTINEL) != 0) return child & 0x7FFFFFFF;

            idx = child;
            suffix <<= 1;
        }
    }

    private int trieWalkV6(BigInteger ipInt) {
        int shift = 128 - v6JumpBits;
        int idxJump = ipInt.shiftRight(shift).intValue() & ((1 << v6JumpBits) - 1);
        int ptr = readU32(data, (int) (offV6Jump + idxJump * 4L));

        if (ptr == 0) return 0;
        if ((ptr & SENTINEL) != 0) return ptr & 0x7FFFFFFF;

        int idx = ptr;
        int depth = v6JumpBits;

        while (depth < 128) {
            int bit = ipInt.shiftRight(127 - depth).intValue() & 1;
            int child = getV6Child(idx, bit);

            if (child == 0) return 0;
            if ((child & SENTINEL) != 0) return child & 0x7FFFFFFF;

            idx = child;
            depth += 1;
        }
        return 0;
    }

    private int[] readIPRow(int rowId) {
        if (rowId <= 0 || rowId >= rowCount) return new int[]{0, 0, 0};
        int off = (int) offIPRow + rowId * ipRowSize;
        int geoId = readU24(data, off);
        int asnId = readU24(data, off + 3);

        int usageTypeId = 0;
        if (ipRowSize >= 9) {
            usageTypeId = readU24(data, off + 6);
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
        if (entryId < 0 || entryId >= groupEntryCounts[groupIndex]) return null;

        ensurePoolsLoaded();

        int fieldCount = groupFieldCounts[groupIndex];
        if (fieldCount <= 0) return null;

        long groupEntryStart = offGeoEntries + groupEntryOffsets[groupIndex];
        int stride = groupStrides[groupIndex];
        long entryOffset = groupEntryStart + (long) entryId * stride;
        byte[] d = data;

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
                        val = Float.toString(ByteBuffer.wrap(d, fo, 4).order(ByteOrder.LITTLE_ENDIAN).getFloat());
                    } else {
                        val = Double.toString(ByteBuffer.wrap(d, fo, 8).order(ByteOrder.LITTLE_ENDIAN).getDouble());
                    }
                } else {
                    int valNum = readUintWidth(d, fo, w);
                    val = Integer.toString(valNum);
                }
            } else {
                int idx = readUintWidth(d, fo, w);
                String[][] gp = groupPools[groupIndex];
                if (gp != null && i < gp.length && idx < gp[i].length) {
                    val = gp[i][idx];
                } else {
                    val = "";
                }
            }

            String fname = i < fieldNames.length ? fieldNames[i] : "field_" + i;
            if (floatFieldIndices.contains(fname) && !val.isEmpty()) {
                try {
                    val = new BigDecimal(Double.parseDouble(val)).setScale(6, RoundingMode.HALF_EVEN).toPlainString();
                } catch (NumberFormatException ignored) {}
            }
            values[i] = val;
        }

        return new IpLocation(values);
    }

    public IpLocation find(String ipStr) {
        if (ipStr == null || ipStr.isEmpty()) return null;

        if (ipStr.contains(":")) {
            java.net.InetAddress addr;
            try {
                addr = java.net.InetAddress.getByName(ipStr);
            } catch (java.net.UnknownHostException e) {
                return null;
            }
            byte[] bytes = addr.getAddress();
            if (bytes.length == 4) {
                int ipInt = ((bytes[0] & 0xFF) << 24) | ((bytes[1] & 0xFF) << 16) |
                           ((bytes[2] & 0xFF) << 8) | (bytes[3] & 0xFF);
                return findUint(ipInt);
            }

            // Embedded IPv4
            if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 &&
                bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 &&
                bytes[8] == 0 && bytes[9] == 0 && bytes[10] == (byte) 0xff && bytes[11] == (byte) 0xff) {
                int ipInt = ((bytes[12] & 0xFF) << 24) | ((bytes[13] & 0xFF) << 16) |
                           ((bytes[14] & 0xFF) << 8) | (bytes[15] & 0xFF);
                return findUint(ipInt);
            }

            BigInteger ipInt = new BigInteger(1, bytes);
            return findV6Uint(ipInt);
        }

        int ipInt = fastParseIp(ipStr);
        return findUint(ipInt);
    }

    public IpLocation findUint(int ipInt) {
        if (!hasV4) return null;
        int rowId = trieWalkV4(ipInt);
        if (rowId == 0) return null;
        return resolveRowId(rowId, groupIndex);
    }

    public IpLocation findV6Uint(BigInteger ipInt) {
        if (!hasV6) return null;
        int rowId = trieWalkV6(ipInt);
        if (rowId == 0) return null;
        return resolveRowId(rowId, groupIndex);
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
        if (data == null || data.length < 20) return false;
        int stored = readU32(data, 16);
        byte[] copy = data.clone();
        copy[16] = 0; copy[17] = 0; copy[18] = 0; copy[19] = 0;
        CRC32 crc = new CRC32();
        crc.update(copy);
        return stored == (int) crc.getValue();
    }

    private static int fastParseIp(String ip) {
        int result = 0, val = 0, dots = 0;
        int digitCount = 0;
        
        for (int i = 0; i < ip.length(); i++) {
            char c = ip.charAt(i);
            if (c >= '0' && c <= '9') {
                digitCount++;
                if (digitCount > 3) return -1;
                val = val * 10 + (c - '0');
                if (val > 255) return -1;
            } else if (c == '.') {
                if (digitCount == 0) return -1;
                if (dots == 3) return -1;
                result = (result << 8) | val;
                val = 0;
                digitCount = 0;
                dots++;
            } else {
                return -1;
            }
        }
        
        if (dots != 3) return -1;
        if (digitCount == 0) return -1;
        
        return (result << 8) | val;
    }
}
