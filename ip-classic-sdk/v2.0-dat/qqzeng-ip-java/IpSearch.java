package qqzeng.ip;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;

/**
 * qqzeng-ip 2.0 (.dat) 高性能解析 - Java 版 (JDK 8 ~ 21+)
 *
 * 文件结构 (UTF-8, Little-Endian):
 *   文件头  16 字节 (4-4-4-4): [索引区首条偏移][索引区末条偏移][前缀区首偏移][前缀区末偏移]
 *   内容区  唯一不重复的地区字符串,按 (offset,len) 被索引区引用
 *   索引区  12 字节/条 (4-4-3-1): [起始IP][结束IP][地区偏移24b][长度8b]
 *   前缀区   9 字节/条 (1-4-4): [前缀0-255][索引区start][索引区end]
 *
 * 线程安全: 初始化完成后全部状态只读,可多线程并发查询;
 *           getInstance() 由静态内部类 Holder 保证懒加载与初始化竞争安全。
 *
 * 相比历史版本的修复与优化:
 *   1. 移除硬编码 "C:/qqzeng-ip-java/bin/qqzeng-ip.dat",改为默认路径自动探测,
 *      并支持 new IpSearch(dbPath) / new IpSearch(byte[] bytes)。
 *   2. HashMap 前缀表改为固定 256 长度 int 数组,缺失前缀以 -1 标记
 *      (修复 "0.0.0.0/8 前缀 low=high=0 被判未命中" 的 bug)。
 *   3. ip.split("\\.") 改为单次扫描零分配的 fastParseIp 严格校验。
 *   4. 字符串池去重: 全球版 137 万条记录仅 16 万个唯一字符串,去重常驻,
 *      查询零解码零分配。
 *   5. 二分查找 (l+h)>>>1 防溢出;加载时完整校验文件结构,
 *      非法文件抛 RuntimeException(fail-closed)。
 *   6. synchronized getInstance() 改为静态内部类单例。
 */
public class IpSearch {

    private static final String[] DEFAULT_PATHS = {
        "qqzeng-ip-utf8.dat",
        "qqzeng-ip.dat",
        "qqzeng-ip-china-utf8.dat",
        "qqzeng-ip-global-utf8.dat",
        "dada/国内精华版-202610/qqzeng-ip-china-utf8.dat",
        "dada/全球旗舰版-202610/qqzeng-ip-global-utf8.dat",
        "../dada/国内精华版-202610/qqzeng-ip-china-utf8.dat",
        "../dada/全球旗舰版-202610/qqzeng-ip-global-utf8.dat",
        "../../dada/国内精华版-202610/qqzeng-ip-china-utf8.dat",
        "../../dada/全球旗舰版-202610/qqzeng-ip-global-utf8.dat",
    };

    private int[] starts;      /* 每条记录起始 IP (uint32 存 int) */
    private int[] ends;        /* 每条记录结束 IP */
    private int[] strIdx;      /* 记录 -> strings 下标 */
    private String[] strings;  /* 去重后的地区字符串 */
    private final int[] prefLo = new int[256];
    private final int[] prefHi = new int[256];

    /** 兼容旧版: 默认路径自动探测。 */
    public IpSearch() {
        RuntimeException last = null;
        for (String p : DEFAULT_PATHS) {
            File f = new File(p);
            if (!f.isFile()) {
                continue;
            }
            try {
                load(readFile(f));
                return;
            } catch (RuntimeException e) {
                last = e;
            }
        }
        throw new RuntimeException("IpSearch: no loadable .dat found"
                + (last == null ? "" : " (" + last.getMessage() + ")"));
    }

    /** 新增: 指定 .dat 路径,可同时加载国内版与全球版。 */
    public IpSearch(String dbPath) {
        File f = new File(dbPath);
        if (!f.isFile()) {
            throw new RuntimeException("IpSearch: cannot open " + dbPath);
        }
        load(readFile(f));
    }

    /** 新增: 从内存字节流加载。 */
    public IpSearch(byte[] bytes) {
        load(bytes);
    }

    private static class Holder {
        static final IpSearch INSTANCE = new IpSearch();
    }

    /** 单例(默认路径),线程安全。 */
    public static IpSearch getInstance() {
        return Holder.INSTANCE;
    }

    private static byte[] readFile(File f) {
        try (InputStream is = new FileInputStream(f)) {
            ByteArrayOutputStream buf = new ByteArrayOutputStream(1 << 20);
            byte[] chunk = new byte[1 << 16];
            int n;
            while ((n = is.read(chunk)) != -1) {
                buf.write(chunk, 0, n);
            }
            return buf.toByteArray();
        } catch (IOException e) {
            throw new RuntimeException("IpSearch: read failed: " + f, e);
        }
    }

    private void load(byte[] data) {
        if (data == null || data.length < 16) {
            throw new RuntimeException("database too small");
        }
        for (int i = 0; i < 256; i++) {
            prefLo[i] = -1;
            prefHi[i] = -1;
        }

        final int firstIndex = readLE32(data, 0);
        final int lastIndex = readLE32(data, 4);
        final int prefStart = readLE32(data, 8);
        final int prefEnd = readLE32(data, 12);

        if (firstIndex < 16 || lastIndex < firstIndex || (lastIndex - firstIndex) % 12 != 0
                || prefEnd < prefStart || prefStart < 16 || (prefEnd - prefStart) % 9 != 0) {
            throw new RuntimeException("bad header");
        }

        final int recordCount = (lastIndex - firstIndex) / 12 + 1;
        final int prefixCount = (prefEnd - prefStart) / 9 + 1;
        if ((long) firstIndex + 12L * recordCount > data.length
                || (long) prefStart + 9L * prefixCount > data.length) {
            throw new RuntimeException("header exceeds file size");
        }

        for (int k = 0; k < prefixCount; k++) {
            final int o = prefStart + k * 9;
            final int n = data[o] & 0xFF;
            final int lo = readLE32(data, o + 1);
            final int hi = readLE32(data, o + 5);
            if (lo > hi || hi >= recordCount) {
                throw new RuntimeException("bad prefix range");
            }
            prefLo[n] = lo;
            prefHi[n] = hi;
        }

        starts = new int[recordCount];
        ends = new int[recordCount];
        strIdx = new int[recordCount];

        /* 字符串池去重: key = offset<<8 | len (offset<2^24, len<2^8) */
        HashMap<Integer, Integer> seen = new HashMap<Integer, Integer>(recordCount / 2 + 16);
        String[] strs = new String[recordCount];
        int uniq = 0;
        for (int i = 0; i < recordCount; i++) {
            final int o = firstIndex + i * 12;
            final int off = (data[o + 8] & 0xFF) | ((data[o + 9] & 0xFF) << 8) | ((data[o + 10] & 0xFF) << 16);
            final int len = data[o + 11] & 0xFF;
            if ((long) off + len > data.length) {
                throw new RuntimeException("string region out of range");
            }
            starts[i] = readLE32(data, o);
            ends[i] = readLE32(data, o + 4);
            final int key = (off << 8) | len;
            Integer id = seen.get(key);
            if (id == null) {
                id = uniq++;
                strs[id] = new String(data, off, len, StandardCharsets.UTF_8);
                seen.put(key, id);
            }
            strIdx[i] = id;
        }
        strings = new String[uniq];
        System.arraycopy(strs, 0, strings, 0, uniq);
    }

    private static int readLE32(byte[] b, int o) {
        return (b[o] & 0xFF) | ((b[o + 1] & 0xFF) << 8) | ((b[o + 2] & 0xFF) << 16) | ((b[o + 3] & 0xFF) << 24);
    }

    /**
     * 严格 IPv4 解析: 每段 1~3 位数字且 <=255,恰好 4 段。
     * 拒绝空串/空段/负数/非数字/IPv6/前后缀垃圾。非法返回 false。
     */
    public static boolean fastParseIp(String ip, int[] out) {
        if (ip == null) {
            return false;
        }
        int val = 0, result = 0, shift = 24, digits = 0;
        final int len = ip.length();
        for (int i = 0; i < len; i++) {
            final char c = ip.charAt(i);
            if (c >= '0' && c <= '9') {
                val = val * 10 + (c - '0');
                if (val > 255) {
                    return false;
                }
                digits++;
                if (digits > 3) {
                    return false; /* 拒绝 "0008.8.8.8" 这类超长前导零 */
                }
            } else if (c == '.') {
                if (digits == 0 || shift == 0) {
                    return false; /* 空段或第 5 段 */
                }
                result |= val << shift;
                val = 0;
                digits = 0;
                shift -= 8;
            } else {
                return false;
            }
        }
        if (digits == 0 || shift != 0) {
            return false;
        }
        out[0] = result | val;
        return true;
    }

    /** 兼容旧版入口: 未命中/非法 IP 返回 ""。 */
    public String Get(String ip) {
        final int[] v = new int[1];
        if (!fastParseIp(ip, v)) {
            return "";
        }
        return findUint(v[0]);
    }

    /** 多语言统一别名。 */
    public String find(String ip) {
        return Get(ip);
    }

    /** 整数查询(跳过字符串解析,高性能场景)。 */
    public String findUint(int val) {
        final int p = (val >>> 24) & 0xFF;
        final int lo = prefLo[p];
        if (lo < 0) {
            return ""; /* 前缀不存在(不能以 high==0 判未命中) */
        }
        final int hi = prefHi[p];
        int idx;
        if (lo == hi) {
            idx = lo;
        } else {
            /* 二分逼近: 最小的 endip >= val。
             * int 为有符号类型, >= 128.0.0.0 的 IP 为负数,
             * 必须用无符号比较, 杜绝跨 0x80000000 边界的符号翻转。 */
            int l = lo, h = hi;
            idx = 0;
            while (l <= h) {
                final int mid = (l + h) >>> 1;
                if (Integer.compareUnsigned(ends[mid], val) >= 0) {
                    idx = mid;
                    if (mid == 0) {
                        break;
                    }
                    h = mid - 1;
                } else {
                    l = mid + 1;
                }
            }
        }
        if (Integer.compareUnsigned(starts[idx], val) <= 0
                && Integer.compareUnsigned(ends[idx], val) >= 0) {
            return strings[strIdx[idx]];
        }
        return "";
    }

    public static void main(String[] args) {
        IpSearch finder = IpSearch.getInstance();
        String ip = "58.213.198.68";
        System.out.println(ip);
        System.out.println(finder.Get(ip));
        System.out.println(finder.findUint(0x08080808));
    }
}
