package com.qqzeng.ip;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

/**
 * qqzeng-ip IP数据库解析类 (Java高标准版)
 * 特性: 线程安全 / 无锁读 / 零依赖 / 高性能
 */
public class IpDbSearch {

    private static final IpDbSearch INSTANCE = new IpDbSearch();

    // 核心数据
    private byte[] data;
    private String[] geoispArr;

    // 常量
    private static final int INDEX_START_INDEX = 0x30004;
    private static final int END_MASK = 0x800000;
    private static final int COMPL_MASK = ~END_MASK;
    private static final String DB_FILE_NAME = "qqzeng-ip-6.0-global.db";

    private IpDbSearch() {
        loadDb();
    }

    public static IpDbSearch getInstance() {
        return INSTANCE;
    }

    private void loadDb() {
        String dbPath = findDbPath();
        if (dbPath == null) {
            throw new RuntimeException("Fatal: Cannot find " + DB_FILE_NAME);
        }

        try (InputStream is = new FileInputStream(dbPath)) {
            data = toByteArray(is);
            
            if (data.length < INDEX_START_INDEX) {
                throw new RuntimeException("Invalid database file size");
            }

            // 读取节点数量 (小端序)
            // Java byte is signed (-128 to 127), so we must use & 0xFF
            int nodeCount = (data[0] & 0xFF) | 
                           ((data[1] & 0xFF) << 8) | 
                           ((data[2] & 0xFF) << 16) | 
                           ((data[3] & 0xFF) << 24);

            int stringAreaOffset = INDEX_START_INDEX + nodeCount * 6;
            
            if (stringAreaOffset > data.length) {
                throw new RuntimeException("Invalid metadata");
            }

            // 解析字符串区 (Pre-parse to array for O(1) access)
            String content = new String(data, stringAreaOffset, data.length - stringAreaOffset, StandardCharsets.UTF_8);
            geoispArr = content.split("\t");

        } catch (Exception e) {
            throw new RuntimeException("Failed to initialize IpDbSearch", e);
        }
    }

    public String find(String ip) {
        if (ip == null || ip.isEmpty()) {
            return "";
        }
        long ipLong = fastParseIp(ip);
        if (ipLong == -1) {
            return "";
        }
        
        int prefix = (int) (ipLong >> 16);
        int suffix = (int) (ipLong & 0xFFFF);
        
        return findInternal(prefix, suffix);
    }
    
    // 供内部或高级用户使用的 int查询
    private String findInternal(int prefix, int suffix) {
        // 一级索引
        int record = readInt24(4 + prefix * 3);
        
        // 二叉树遍历
        while ((record & END_MASK) != END_MASK) {
            int bit = (suffix >> 15) & 1;
            int offset = INDEX_START_INDEX + record * 6 + bit * 3;
            record = readInt24(offset);
            suffix <<= 1;
        }
        
        int index = record & COMPL_MASK;
        if (index < geoispArr.length) {
            return geoispArr[index];
        }
        return "";
    }

    private int readInt24(int offset) {
        // Java byte & 0xFF to get unsigned value
        // Data format: High8 Mid8 Low8
        return ((data[offset] & 0xFF) << 16) | 
               ((data[offset + 1] & 0xFF) << 8) | 
               (data[offset + 2] & 0xFF);
    }

    // IP String -> optimized long (to hold unsigned int32)
    // Returns -1 on error
    private long fastParseIp(String ip) {
        long val = 0;
        long result = 0;
        int shift = 24;
        int len = ip.length();
        
        for (int i = 0; i < len; i++) {
            char c = ip.charAt(i);
            if (c >= '0' && c <= '9') {
                val = val * 10 + (c - '0');
            } else if (c == '.') {
                if (val > 255) return -1;
                result |= val << shift;
                val = 0;
                shift -= 8;
            } else {
                return -1;
            }
        }
        
        if (val > 255 || shift != 0) return -1;
        result |= val;
        
        return result;
    }

    private String findDbPath() {
        String userDir = System.getProperty("user.dir");
        String[] attempts = {
            new File(userDir, DB_FILE_NAME).getAbsolutePath(),
            new File(userDir, "../data/" + DB_FILE_NAME).getAbsolutePath(),
            new File(userDir, "../../data/" + DB_FILE_NAME).getAbsolutePath(),
            new File(userDir, "../../../data/" + DB_FILE_NAME).getAbsolutePath()
        };

        for (String path : attempts) {
            if (new File(path).exists()) {
                return path;
            }
        }
        return null;
    }

    // JDK 8 doesn't have InputStream.readAllBytes()
    private byte[] toByteArray(InputStream is) throws IOException {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        int nRead;
        byte[] data = new byte[16384];
        while ((nRead = is.read(data, 0, data.length)) != -1) {
            buffer.write(data, 0, nRead);
        }
        return buffer.toByteArray();
    }
}
