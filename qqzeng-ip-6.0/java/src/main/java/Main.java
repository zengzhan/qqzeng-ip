import com.qqzeng.ip.IpDbSearch;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;
import java.util.ArrayList;
import java.util.List;

public class Main {
    public static void main(String[] args) {
        System.out.println("正在初始化 qqzeng-ip 数据库...");
        long start = System.nanoTime();
        IpDbSearch searcher = IpDbSearch.getInstance();
        long elapsedMs = (System.nanoTime() - start) / 1000000;
        System.out.println("数据库加载完成，耗时: " + elapsedMs + " ms");

        String testFile = findTestFile();
        if (testFile == null) {
            System.out.println("无法找到测试文件");
            return;
        }

        System.out.println("正在读取测试文件: " + testFile);
        List<String> lines = new ArrayList<>();
        try (BufferedReader br = new BufferedReader(new FileReader(testFile))) {
            String line;
            while ((line = br.readLine()) != null) {
                if (!line.trim().isEmpty()) {
                    lines.add(line);
                }
            }
        } catch (Exception e) {
            e.printStackTrace();
            return;
        }

        System.out.println("共有 " + lines.size() + " 条测试记录");

        int passed = 0;
        int failed = 0;

        // 预热
        searcher.find("8.8.8.8");

        long testStart = System.nanoTime();
        for (String line : lines) {
            String[] parts = line.split("\t");
            if (parts.length < 3) continue;

            String startIp = parts[0];
            String endIp = parts[1];
            String expected = parts[2];

            if (!verify(searcher, startIp, expected)) {
                failed++;
                continue;
            }
            if (!verify(searcher, endIp, expected)) {
                failed++;
                continue;
            }
            
            // 中间IP测试
            String midIp = getMidIp(startIp, endIp);
            if (!verify(searcher, midIp, expected)) {
               failed++;
               continue;
            }

            passed++;
        }
        long testElapsedNs = System.nanoTime() - testStart;

        System.out.println("\n-------------------------------------------");
        System.out.println("测试完成!");
        System.out.println("总记录数: " + lines.size());
        System.out.println("通过: " + passed);
        System.out.println("失败: " + failed);
        System.out.println("总耗时: " + (testElapsedNs / 1000000.0) + " ms");
        if (lines.size() > 0) {
             System.out.printf("平均耗时: %.4f ms/query%n", (testElapsedNs / 1000000.0) / (lines.size() * 3));
        }
        System.out.println("-------------------------------------------");

        // 压测
        System.out.println("\n开始性能压测 (1,000,000 次查询)...");
        long benchStart = System.nanoTime();
        for (int i = 0; i < 1000000; i++) {
            searcher.find("1.0.0.1");
            searcher.find("255.255.255.255");
            searcher.find("114.114.114.114");
            searcher.find("8.8.8.8");
        }
        long benchElapsedNs = System.nanoTime() - benchStart;
        double benchSeconds = benchElapsedNs / 1_000_000_000.0;
        
        System.out.println("4,000,000 次查询耗时: " + (benchElapsedNs / 1000000) + " ms");
        System.out.printf("QPS: %.2f%n", 4000000.0 / benchSeconds);
    }

    private static boolean verify(IpDbSearch searcher, String ip, String expected) {
        String result = searcher.find(ip);
        if (!result.equals(expected)) {
            System.out.println("[Fail] IP: " + ip);
            System.out.println("  期望: " + expected);
            System.out.println("  实际: " + result);
            return false;
        }
        return true;
    }

    private static String findTestFile() {
        String userDir = System.getProperty("user.dir");
        String[] attempts = {
            "test.txt",
            "../data/test.txt",
            "../../data/test.txt", 
            "../../../data/test.txt"
        };
        for(String p : attempts) {
            File f = new File(userDir, p);
            if(f.exists()) return f.getAbsolutePath();
        }
        return null;
    }
    
    private static String getMidIp(String start, String end) {
        long s = ipToLong(start);
        long e = ipToLong(end);
        if (s == e) return start;
        long mid = s + (e - s) / 2;
        return longToIp(mid);
    }

    private static long ipToLong(String ip) {
        String[] parts = ip.split("\\.");
        return (Long.parseLong(parts[0]) << 24) |
               (Long.parseLong(parts[1]) << 16) |
               (Long.parseLong(parts[2]) << 8) |
               Long.parseLong(parts[3]);
    }
    
    private static String longToIp(long val) {
        return (val >> 24) + "." + ((val >> 16) & 0xFF) + "." + ((val >> 8) & 0xFF) + "." + (val & 0xFF); 
    }
}
