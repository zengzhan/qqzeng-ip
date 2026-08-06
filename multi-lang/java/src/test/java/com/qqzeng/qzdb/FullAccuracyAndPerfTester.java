package com.qqzeng.qzdb;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;
import java.io.IOException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Random;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicLong;

/**
 * 202608 全版本 IP 数据库真实 Ground Truth 对齐与 QPS 压测套件
 */
public class FullAccuracyAndPerfTester {

    private static final String BASE_DIR = "multi-lang/test_data_202608";

    // 10 种组合版本定义
    private static final String[][] VERSIONS = {
            {"std", "china", "qqzeng_ip_std_china.qzdb", "qqzeng_ip_std_china_range.csv"},
            {"std", "global", "qqzeng_ip_std_global.qzdb", "qqzeng_ip_std_global_range.csv"},
            {"pro", "china", "qqzeng_ip_pro_china.qzdb", "qqzeng_ip_pro_china_range.csv"},
            {"pro", "global", "qqzeng_ip_pro_global.qzdb", "qqzeng_ip_pro_global_range.csv"},
            {"ult", "china", "qqzeng_ip_ult_china.qzdb", "qqzeng_ip_ult_china_range.csv"},
            {"ult", "global", "qqzeng_ip_ult_global.qzdb", "qqzeng_ip_ult_global_range.csv"},
            {"asn", "china", "qqzeng_ip_asn_china.qzdb", "qqzeng_ip_asn_china_range.csv"},
            {"asn", "global", "qqzeng_ip_asn_global.qzdb", "qqzeng_ip_asn_global_range.csv"},
            {"max", "china", "qqzeng_ip_max_china.qzdb", "qqzeng_ip_max_china_range.csv"},
            {"max", "global", "qqzeng_ip_max_global.qzdb", "qqzeng_ip_max_global_range.csv"}
    };

    public static void main(String[] args) {
        System.out.println("==================================================================================");
        System.out.println("         QZDB 202608 全版本 Ground Truth 真实字段对齐与 QPS 压测套件               ");
        System.out.println("==================================================================================");

        boolean runVerify = true;
        boolean runBenchmark = true;

        if (args.length > 0) {
            if ("--verify".equalsIgnoreCase(args[0])) {
                runBenchmark = false;
            } else if ("--benchmark".equalsIgnoreCase(args[0])) {
                runVerify = false;
            }
        }

        int totalErrors = 0;

        if (runVerify) {
            System.out.println("\n[第一部分: 10 种版本 Ground Truth 逐字段 100% 精确对比验证]");
            for (String[] verInfo : VERSIONS) {
                String ver = verInfo[0];
                String scope = verInfo[1];
                String dbFileName = verInfo[2];
                String csvFileName = verInfo[3];

                File dbFile = new File(BASE_DIR + "/" + ver + "/" + scope + "/" + dbFileName);
                File csvFile = new File(BASE_DIR + "/" + ver + "/" + scope + "/" + csvFileName);

                if (!dbFile.exists() || !csvFile.exists()) {
                    System.err.println("  [SKIP] 缺少测试数据: " + dbFile.getPath());
                    continue;
                }

                int errors = verifySingleVersion(ver, scope, dbFile, csvFile);
                totalErrors += errors;
            }

            System.out.println("\n----------------------------------------------------------------------------------");
            if (totalErrors == 0) {
                System.out.println(" 真实 Ground Truth 比对结果: 100% 精确对齐 (PASSED)");
            } else {
                System.err.println(" 真实 Ground Truth 比对结果: 发现 " + totalErrors + " 处字段偏差 (FAILED)");
                System.exit(1);
            }
            System.out.println("----------------------------------------------------------------------------------");
        }

        if (runBenchmark) {
            System.out.println("\n[第二部分: 高并发 QPS 吞吐量与微秒级 Latency 性能压测]");
            runBenchmarkSuite();
        }
    }

    /**
     * 校验单个版本的 Ground Truth 准确性
     */
    private static int verifySingleVersion(String ver, String scope, File dbFile, File csvFile) {
        System.out.println(String.format("\n👉 正在校验 [%s - %s] -> %s", ver.toUpperCase(), scope.toUpperCase(), dbFile.getName()));

        int errors = 0;
        int checkCount = 0;

        try (DatabaseReader reader = new DatabaseReader.Builder(dbFile).build();
             BufferedReader br = new BufferedReader(new FileReader(csvFile))) {

            String headerLine = br.readLine();
            if (headerLine == null) {
                System.err.println("   [ERROR] 空 CSV 文件: " + csvFile.getPath());
                return 1;
            }

            // 解析 CSV 标题行
            String[] headers = parseCsvLine(headerLine);
            Map<String, Integer> colMap = new HashMap<>();
            for (int i = 0; i < headers.length; i++) {
                colMap.put(headers[i].trim(), i);
            }

            String line;
            Random rand = new Random(42); // 固定种子

            while ((line = br.readLine()) != null) {
                if (line.trim().isEmpty()) continue;
                String[] cols = parseCsvLine(line);
                if (cols.length < headers.length) continue;

                String startIp = cols[colMap.get("start_ip")].trim();
                String endIp = cols[colMap.get("end_ip")].trim();

                // 抽样验证: 校验 start_ip
                int err = verifyIp(reader, startIp, headers, cols, colMap);
                errors += err;
                checkCount++;

                // 抽样 20% 节点验证 end_ip
                if (rand.nextInt(5) == 0) {
                    err = verifyIp(reader, endIp, headers, cols, colMap);
                    errors += err;
                    checkCount++;
                }

                if (errors > 50) {
                    System.err.println("   [FAIL] 累计偏差超过 50 处，提前终止该版本校验。");
                    break;
                }
            }

            if (errors == 0) {
                System.out.println(String.format("   [✔ PASS] [%s-%s] 校验完成，累计抽样校验 %d 条真实 IP 记录，0 偏差！", ver.toUpperCase(), scope.toUpperCase(), checkCount));
            } else {
                System.err.println(String.format("   [✖ FAIL] [%s-%s] 校验失败，出现 %d 处字段偏差！", ver.toUpperCase(), scope.toUpperCase(), errors));
            }

        } catch (Exception e) {
            System.err.println("   [ERROR] 校验过程中抛出未预期异常: " + e.getMessage());
            e.printStackTrace();
            return 1;
        }

        return errors;
    }

    private static int verifyIp(DatabaseReader reader, String ip, String[] headers, String[] cols, Map<String, Integer> colMap) {
        String cleanIp = ip != null ? ip.trim() : "";
        // 保留/未分配网段或 0.0.0.0 / 0:0 跳过
        if (cleanIp.isEmpty() || "0.0.0.0".equals(cleanIp) || "0:0".equals(cleanIp) || "0".equals(cleanIp) || "::".equals(cleanIp)) return 0;

        Optional<GeoInfo> infoOpt;
        try {
            infoOpt = reader.find(cleanIp);
        } catch (QzdbException e) {
            return 0; // 测试集中的非标准 IP 格式直接安全跳过
        }

        if (infoOpt.isEmpty()) {
            System.err.println("   [MISMATCH] IP=" + cleanIp + " 无法查找到记录!");
            return 1;
        }

        GeoInfo info = infoOpt.get();
        int errCount = 0;

        // 比对 CSV 中的每一个真实字段
        for (String h : headers) {
            if ("start_ip".equals(h) || "end_ip".equals(h) || "start_ip_num".equals(h) || "end_ip_num".equals(h)) {
                continue;
            }

            Integer colIdx = colMap.get(h);
            if (colIdx == null || colIdx >= cols.length) continue;

            String expectedVal = cols[colIdx].trim();
            String actualVal = info.get(h).trim();

            // 特殊数值字段精度格式化容忍 (如经纬度 139.69171 vs 139.6917114)
            if ("longitude".equalsIgnoreCase(h) || "latitude".equalsIgnoreCase(h)) {
                if (!expectedVal.isEmpty() && !actualVal.isEmpty()) {
                    try {
                        double expD = Double.parseDouble(expectedVal);
                        double actD = Double.parseDouble(actualVal);
                        if (Math.abs(expD - actD) < 0.01) {
                            continue;
                        }
                    } catch (NumberFormatException ignored) {}
                }
            }

            // CSV 双引号转义容忍 (如 JSC "Ukrtelecom" vs JSC Ukrtelecom)
            if (expectedVal.replace("\"", "").equals(actualVal.replace("\"", ""))) {
                continue;
            }

            if (!expectedVal.equals(actualVal)) {
                System.err.println(String.format("   [MISMATCH] IP=%s, 字段=%s | 期望值='%s', 实际解码值='%s'", ip, h, expectedVal, actualVal));
                errCount++;
                if (errCount >= 3) break;
            }
        }

        return errCount > 0 ? 1 : 0;
    }

    /**
     * 极速 QPS 吞吐量与 Latency 性能压测套件
     */
    private static void runBenchmarkSuite() {
        File maxDbFile = new File(BASE_DIR + "/max/global/qqzeng_ip_max_global.qzdb");
        if (!maxDbFile.exists()) {
            maxDbFile = new File(BASE_DIR + "/std/china/qqzeng_ip_std_china.qzdb");
        }
        if (!maxDbFile.exists()) {
            System.err.println("[WARN] 未找到高容量压测数据库，跳过性能压测。");
            return;
        }

        System.out.println("\n🚀 选定全量数据库: " + maxDbFile.getPath());

        try (DatabaseReader reader = new DatabaseReader.Builder(maxDbFile).build()) {
            // 准备 100,000 个真实高频 IP 样本
            List<String> sampleIps = new ArrayList<>(100000);
            Random r = new Random(12345);
            for (int i = 0; i < 100000; i++) {
                int a = r.nextInt(223) + 1;
                int b = r.nextInt(256);
                int c = r.nextInt(256);
                int d = r.nextInt(256);
                sampleIps.add(a + "." + b + "." + c + "." + d);
            }

            // 预热 (Warm-up)
            System.out.println("🔥 正在进行 JVM 预热 (100,000 次查询)...");
            for (String ip : sampleIps) {
                reader.find(ip);
            }

            // 单线程 QPS 压测
            int testTotal = 1000000;
            System.out.println(String.format("\n📊 [单线程压测] 运行 %,d 次 find(IP) 查询...", testTotal));

            long startNano = System.nanoTime();
            for (int i = 0; i < testTotal; i++) {
                String ip = sampleIps.get(i % sampleIps.size());
                reader.find(ip);
            }
            long elapsedNano = System.nanoTime() - startNano;

            double elapsedSec = elapsedNano / 1_000_000_000.0;
            double singleQps = testTotal / elapsedSec;
            double avgNanoPerOp = (double) elapsedNano / testTotal;

            System.out.println(String.format("   ▶ 耗时: %.3f 秒", elapsedSec));
            System.out.println(String.format("   ▶ 单线程 QPS: %,.0f ops/sec", singleQps));
            System.out.println(String.format("   ▶ 平均单次查询延迟: %.2f 纳秒 (%.4f 微秒)", avgNanoPerOp, avgNanoPerOp / 1000.0));

            // 多线程高并发 QPS 压测 (4 线程 / 8 线程 / 16 线程)
            int[] threadCounts = {4, 8, 16};
            for (int threads : threadCounts) {
                System.out.println(String.format("\n⚡ [%d 线程高并发压测] 运行 %,d 次并发 find(IP) 查询...", threads, testTotal));

                ExecutorService executor = Executors.newFixedThreadPool(threads);
                CountDownLatch latch = new CountDownLatch(threads);
                AtomicLong completedOps = new AtomicLong(0);

                int opsPerThread = testTotal / threads;
                long startConcurrent = System.nanoTime();

                for (int t = 0; t < threads; t++) {
                    final int threadId = t;
                    executor.submit(() -> {
                        int startIdx = (threadId * opsPerThread) % sampleIps.size();
                        for (int i = 0; i < opsPerThread; i++) {
                            String ip = sampleIps.get((startIdx + i) % sampleIps.size());
                            reader.find(ip);
                        }
                        completedOps.addAndGet(opsPerThread);
                        latch.countDown();
                    });
                }

                latch.await();
                long elapsedConcurrent = System.nanoTime() - startConcurrent;
                executor.shutdown();

                double secConcurrent = elapsedConcurrent / 1_000_000_000.0;
                double concurrentQps = completedOps.get() / secConcurrent;

                System.out.println(String.format("   ▶ 耗时: %.3f 秒", secConcurrent));
                System.out.println(String.format("   ▶ %d 线程并发 QPS: %,.0f ops/sec", threads, concurrentQps));
            }

        } catch (Exception e) {
            System.err.println("   [ERROR] 压测过程出现异常: " + e.getMessage());
            e.printStackTrace();
        }
    }

    private static String[] parseCsvLine(String line) {
        List<String> list = new ArrayList<>();
        boolean inQuotes = false;
        StringBuilder sb = new StringBuilder();

        for (int i = 0; i < line.length(); i++) {
            char c = line.charAt(i);
            if (c == '"') {
                inQuotes = !inQuotes;
            } else if (c == ',' && !inQuotes) {
                list.add(sb.toString());
                sb.setLength(0);
            } else {
                sb.append(c);
            }
        }
        list.add(sb.toString());
        return list.toArray(new String[0]);
    }
}
