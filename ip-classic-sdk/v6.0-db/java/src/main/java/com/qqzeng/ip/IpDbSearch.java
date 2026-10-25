package com.qqzeng.ip;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

/**
 * qqzeng-ip 6.0 (.db) IP 数据库解析类 (Java 高标准版, 零依赖)
 *
 * 特性: 线程安全 / 无锁读 / 零依赖 / 高性能 / 加载期 fail-closed 校验
 *
 * 文件结构 (UTF-8):
 *   文件头    4 字节: 节点数量 nodeCount (uint32 小端)
 *   一级索引  65536 条 x 3 字节 (大端 int24): IPv4 前 16 位 -> 节点号或叶子
 *   节点区    nodeCount x 6 字节 (每节点 2 个子指针, 各 3 字节大端 int24)
 *   字符串区  其余部分, '\t' 分隔的 UTF-8 结果串
 *   int24 最高位 (0x800000) 置位 = 叶子, 低 22 位为字符串下标;
 *   否则为节点区跳转下标。查询消耗 IP 后 16 位 (MSB first)。
 *
 * 兼容旧版 API:
 *   IpDbSearch.getInstance().find("1.2.3.4")
 * 新增:
 *   new IpDbSearch(dbPath) 多实例 / IpDbSearch.fromBytes(bytes)
 *   findUint(ipInt) 整数查询 (uint32 语义, 高位为 1 的 int 亦正确)
 *
 * 相比历史版本的修复与优化:
 *   1. fastParseIp 严格化: 旧版接受 "1..2.3"/"1.2.3." 等残缺 IP;
 *      现按统一严格规则校验 (每段 1~3 位且 <=255, 恰好 4 段)。
 *   2. 加载期完整校验 (fail-closed): 尺寸/头字段/全部一级索引与节点跳转
 *      合法性/叶子下标范围, 非法文件抛 RuntimeException。
 *   3. 逐查询 int24 拼装改为 int[] 预解析 (一级索引 + 节点区),
 *      查询路径零拼装; 原始 byte[] 加载后即可被 GC 回收。
 *   4. 单例改为 Holder 惰性初始化 (旧版类加载即读盘)。
 *   5. 路径探测补充仓库 dada/ 目录与用户工作目录。
 */
public class IpDbSearch {

    private static class Holder {
        static final IpDbSearch INSTANCE = new IpDbSearch();
    }

    /** 单例 (自动探测默认路径, 与旧版行为兼容)。 */
    public static IpDbSearch getInstance() {
        return Holder.INSTANCE;
    }

    private static final int INDEX_START = 0x30004;
    private static final int FIRST_COUNT = 65536;
    private static final int END_MASK = 0x800000;
    private static final int COMPL_MASK = 0x7FFFFF;
    public static final String DB_FILE_NAME = "qqzeng-ip-6.0-global.db";

    private final int[] first;     // 一级索引 (预解析 int24 值)
    private final int[] nodes;     // nodeCount*2 个子指针 (预解析)
    private final String[] geoispArr;
    private final int nodeCount;

    /** 兼容旧版: 无参构造自动探测默认路径。 */
    private IpDbSearch() {
        this(findDbPathOrThrow());
    }

    /** 新增: 指定路径构造 (多实例)。 */
    public IpDbSearch(String dbPath) {
        this(readFile(dbPath));
    }

    /** 新增: 从内存字节流构造。 */
    public static IpDbSearch fromBytes(byte[] data) {
        return new IpDbSearch(data);
    }

    private IpDbSearch(byte[] data) {
        if (data.length < INDEX_START) {
            throw new RuntimeException("Invalid database file size");
        }
        int nodeCount = (data[0] & 0xFF) | ((data[1] & 0xFF) << 8)
                | ((data[2] & 0xFF) << 16) | ((data[3] & 0xFF) << 24);
        if (nodeCount <= 0) {
            throw new RuntimeException("Invalid node count");
        }
        long nodeAreaEnd = (long) INDEX_START + (long) nodeCount * 6L;
        if (nodeAreaEnd > (long) data.length) {
            throw new RuntimeException("Node area exceeds file size");
        }
        int sa = (int) nodeAreaEnd;
        int stringCount = 1;
        for (int i = sa; i < data.length; i++) {
            if (data[i] == '\t') {
                stringCount++;
            }
        }

        int[] first = new int[FIRST_COUNT];
        for (int i = 0; i < FIRST_COUNT; i++) {
            int o = 4 + i * 3;
            int v = ((data[o] & 0xFF) << 16) | ((data[o + 1] & 0xFF) << 8) | (data[o + 2] & 0xFF);
            if (!entryOK(v, nodeCount, stringCount)) {
                throw new RuntimeException("Bad first-level index entry");
            }
            first[i] = v;
        }

        int[] nodes = new int[nodeCount * 2];
        for (int n = 0, o = INDEX_START; n < nodeCount; n++, o += 6) {
            int a = ((data[o] & 0xFF) << 16) | ((data[o + 1] & 0xFF) << 8) | (data[o + 2] & 0xFF);
            int b = ((data[o + 3] & 0xFF) << 16) | ((data[o + 4] & 0xFF) << 8) | (data[o + 5] & 0xFF);
            if (!entryOK(a, nodeCount, stringCount) || !entryOK(b, nodeCount, stringCount)) {
                throw new RuntimeException("Bad node jump");
            }
            nodes[n * 2] = a;
            nodes[n * 2 + 1] = b;
        }

        this.first = first;
        this.nodes = nodes;
        this.nodeCount = nodeCount;
        this.geoispArr = new String(data, sa, data.length - sa, StandardCharsets.UTF_8)
                .split("\t", -1);
    }

    private static boolean entryOK(int v, int nodeCount, int stringCount) {
        if ((v & END_MASK) != 0) {
            return (v & COMPL_MASK) < stringCount;
        }
        return v >= 0 && v < nodeCount;
    }

    /** 查询 IP 字符串, 未命中/非法返回 ""。 */
    public String find(String ip) {
        if (ip == null || ip.isEmpty()) {
            return "";
        }
        long v = fastParseIp(ip);
        if (v < 0) {
            return "";
        }
        return findUint((int) v);
    }

    /** 整数查询 (uint32 语义; 高位为 1 时传入负 int 亦正确)。 */
    public String findUint(int ipInt) {
        int record = first[ipInt >>> 16];
        int suffix = ipInt & 0xFFFF;
        int hops = 0;
        // 后缀 16 位 -> 最多 16 跳; 超限说明文件非法 (fail-closed)
        while ((record & END_MASK) == 0) {
            if (hops >= 16) {
                return "";
            }
            hops++;
            record = nodes[record * 2 + ((suffix >>> 15) & 1)];
            suffix <<= 1;
        }
        int index = record & COMPL_MASK;
        if (index < geoispArr.length) {
            return geoispArr[index];
        }
        return "";
    }

    public int nodeCount() {
        return nodeCount;
    }

    public int stringCount() {
        return geoispArr.length;
    }

    /** 严格 IPv4 解析: 每段 1~3 位数字且 <=255, 恰好 4 段。非法返回 -1。 */
    public static long fastParseIp(String ip) {
        int len = ip.length();
        long val = 0, result = 0;
        int shift = 24, digits = 0;
        for (int i = 0; i < len; i++) {
            char c = ip.charAt(i);
            if (c >= '0' && c <= '9') {
                val = val * 10 + (c - '0');
                if (val > 255) {
                    return -1;
                }
                digits++;
                if (digits > 3) {
                    return -1; // 拒绝 "0008.8.8.8" 这类超长前导零
                }
            } else if (c == '.') {
                if (digits == 0 || shift == 0) {
                    return -1; // 空段或第 5 段
                }
                result |= val << shift;
                val = 0;
                digits = 0;
                shift -= 8;
            } else {
                return -1;
            }
        }
        if (digits == 0 || shift != 0) {
            return -1;
        }
        return result | val;
    }

    private static String findDbPathOrThrow() {
        String p = findDbPath();
        if (p == null) {
            throw new RuntimeException("Fatal: Cannot find " + DB_FILE_NAME);
        }
        return p;
    }

    /** 探测默认数据库路径 (用户工作目录 / jar 所在目录 / data / dada)。 */
    public static String findDbPath() {
        String[] roots;
        try {
            String codePath = IpDbSearch.class.getProtectionDomain().getCodeSource()
                    .getLocation().toURI().getPath();
            roots = new String[]{System.getProperty("user.dir"), codePath};
        } catch (Exception e) {
            roots = new String[]{System.getProperty("user.dir")};
        }
        for (String root : roots) {
            String[] attempts = {
                    root + "/" + DB_FILE_NAME,
                    root + "/data/" + DB_FILE_NAME,
                    root + "/../data/" + DB_FILE_NAME,
                    root + "/../../data/" + DB_FILE_NAME,
                    root + "/dada/全球旗舰版-202610/" + DB_FILE_NAME,
                    root + "/../dada/全球旗舰版-202610/" + DB_FILE_NAME,
                    root + "/../../dada/全球旗舰版-202610/" + DB_FILE_NAME,
                    root + "/../../../dada/全球旗舰版-202610/" + DB_FILE_NAME,
            };
            for (String p : attempts) {
                if (new File(p).isFile()) {
                    return p;
                }
            }
        }
        return null;
    }

    private static byte[] readFile(String dbPath) {
        try (InputStream is = new FileInputStream(dbPath)) {
            ByteArrayOutputStream buffer = new ByteArrayOutputStream(1 << 20);
            byte[] chunk = new byte[1 << 16];
            int nRead;
            while ((nRead = is.read(chunk, 0, chunk.length)) != -1) {
                buffer.write(chunk, 0, nRead);
            }
            return buffer.toByteArray();
        } catch (IOException e) {
            throw new RuntimeException("Failed to read " + dbPath, e);
        }
    }
}
