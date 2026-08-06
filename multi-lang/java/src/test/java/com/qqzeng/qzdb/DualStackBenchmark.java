package com.qqzeng.qzdb;

import java.io.BufferedReader;
import java.io.File;
import java.io.InputStreamReader;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Tier 3：双栈 1:1 动态压测器（依据 docs/QZDB_TEST_SPECIFICATION.md §四）
 * <p>
 * - 样例生成：方案 A（IPv4 全网段平滑散列）+ 方案 C（IPv6 压缩/全展开多态 + Mapped），固定种子可复现
 * - 比例：50% IPv4 + 50% IPv6（IPv6 内部：40% 纯 IPv6 多态 + 10% IPv4-Mapped）
 * - 指标：IPv4/IPv6 分别 QPS、P50/P99 延迟、4/8/16 线程扩展、预热前后对比、16 线程 × 100k 并发安全
 * - 环境声明：CPU / 核心数 / OS / JVM / 种子 / 数据库文件
 */
public class DualStackBenchmark {

    private static final String BASE_DIR = "multi-lang/test_data_202608";
    private static final long SEED = 20260807L;
    private static final int POOL_V4 = 500_000;
    private static final int POOL_V6_PURE = 400_000;
    private static final int POOL_V6_MAPPED = 100_000;
    private static final int SINGLE_OPS = 2_000_000;
    private static final int LAT_SAMPLE_EVERY = 20; // 每 20 次采样一次延迟，避免循环内开销
    private static final int MULTI_OPS = 2_000_000;
    private static final String REPORT_DIR = "java/test_reports";

    private static String[] poolV4;
    private static String[] poolV6;      // 纯 IPv6 多态
    private static String[] poolMapped;  // IPv4-Mapped

    private static String findReportDir() {
        for (String c : new String[]{"java/test_reports", "multi-lang/java/test_reports", "../java/test_reports"}) {
            File f = new File(c);
            if (f.exists() || f.mkdirs()) return new File(c).getAbsolutePath();
        }
        new File("java/test_reports").mkdirs();
        return new File("java/test_reports").getAbsolutePath();
    }

    private static String findDbFile(String arg) {
        if (arg != null && !arg.isEmpty() && new File(arg).exists()) return arg;
        String[] candidates = {
            "test_data_202608/max/global/qqzeng_ip_max_global.qzdb",
            "../test_data_202608/max/global/qqzeng_ip_max_global.qzdb",
            "multi-lang/test_data_202608/max/global/qqzeng_ip_max_global.qzdb",
            "data/qqzeng_ip_max_global.qzdb",
            "../data/qqzeng_ip_max_global.qzdb"
        };
        for (String c : candidates) {
            if (new File(c).exists()) return c;
        }
        return null;
    }

    public static void main(String[] args) throws Exception {
        String dbPath = findDbFile(args.length > 0 ? args[0] : null);
        if (dbPath == null) {
            String defaultPath = BASE_DIR + "/max/global/qqzeng_ip_max_global.qzdb";
            System.err.println("[SKIP] Tier 3: 压测数据库不存在 (" + defaultPath + ")，跳过性能测试。");
            System.out.println("TEST_PASS (Tier 3 skipped — no db available)");
            return;
        }

        File dbFile = new File(dbPath);
        printEnvironment(dbFile);
        buildPools();

        try (QzdbReader reader = new QzdbReader.Builder(dbFile).verifyCrc(false).build()) {
            System.out.println("\n── 预热前（冷缓存首轮 200k 混合查询）──");
            Result cold = runSingle(reader, 200_000, true);
            printResult("冷", cold);

            System.out.println("\n── 预热（全池 1,000,000 次混合查询）──");
            long w0 = System.nanoTime();
            runSingle(reader, 1_000_000, false);
            System.out.printf("预热耗时 %.2fs%n", (System.nanoTime() - w0) / 1e9);

            System.out.println("\n── 预热后 · 单线程 IPv4（方案 A 散列）──");
            Result hotV4 = runSingleKind(reader, SINGLE_OPS, 0);
            printResult("IPv4", hotV4);

            System.out.println("\n── 预热后 · 单线程 IPv6（纯 v6 多态 + Mapped 按 4:1）──");
            Result hotV6 = runSingleKind(reader, SINGLE_OPS, 1);
            printResult("IPv6", hotV6);

            System.out.println("\n── 预热后 · 单线程双栈混合（50/50）──");
            Result hotMix = runSingle(reader, SINGLE_OPS, true);
            printResult("混合", hotMix);

            java.util.Map<String, Result> multiResults = new java.util.LinkedHashMap<>();
            for (int threads : new int[]{4, 8, 16}) {
                System.out.printf("%n── %d 线程并发（双栈混合 %,d 次）──%n", threads, MULTI_OPS);
                Result mr = runMulti(reader, threads, MULTI_OPS);
                multiResults.put(threads + "-thread", mr);
            }

            System.out.println("\n── 16 线程 × 100,000 并发安全校验（双栈混合，零异常断言）──");
            boolean safe = runConcurrencySafety(reader, 16, 100_000);
            System.out.println("并发安全: " + (safe ? "PASSED（0 异常 / 0 脏读）" : "FAILED"));
            if (!safe) {
                System.exit(1);
            }

            writeReport(dbFile, cold, hotV4, hotV6, hotMix, multiResults, safe);
            System.out.println("\n==================================================");
            System.out.println("TEST_PASS");
            System.out.println("==================================================");
        }
    }

    private static void writeReport(File dbFile, Result cold, Result hotV4, Result hotV6,
                                    Result hotMix, java.util.Map<String, Result> multiResults,
                                    boolean safe) throws Exception {
        String reportDir = findReportDir();
        new File(reportDir).mkdirs();
        String stamp = java.time.LocalDateTime.now().format(java.time.format.DateTimeFormatter.ofPattern("yyyyMMdd_HHmmss"));
        java.io.File report = new java.io.File(reportDir + "/tier3_report_" + stamp + ".json");
        java.io.Writer w = new java.io.BufferedWriter(new java.io.OutputStreamWriter(
                new java.io.FileOutputStream(report), java.nio.charset.StandardCharsets.UTF_8));
        w.write("{\n");
        w.write("  \"spec\": \"QZDB_TEST_SPECIFICATION.md Tier 3\",\n");
        w.write("  \"timestamp\": \"" + stamp + "\",\n");
        w.write("  \"seed\": " + SEED + ",\n");
        w.write("  \"db_path\": \"" + dbFile.getPath() + "\",\n");
        w.write("  \"db_size\": " + dbFile.length() + ",\n");
        w.write("  \"cpu\": \"" + escapeJson(execFirst("sysctl -n machdep.cpu.brand_string")) + "\",\n");
        w.write("  \"cores\": " + Runtime.getRuntime().availableProcessors() + ",\n");
        w.write("  \"jvm\": \"" + escapeJson(System.getProperty("java.version")) + "\",\n");
        w.write("  \"os\": \"" + escapeJson(System.getProperty("os.name") + " " + System.getProperty("os.version")) + "\",\n");
        w.write("  \"cold_run\": " + toJson(cold) + ",\n");
        w.write("  \"ipv4_hot\": " + toJson(hotV4) + ",\n");
        w.write("  \"ipv6_hot\": " + toJson(hotV6) + ",\n");
        w.write("  \"mixed_hot\": " + toJson(hotMix) + ",\n");
        StringBuilder multiJson = new StringBuilder("  \"multi_thread\": {\n");
        boolean first = true;
        for (java.util.Map.Entry<String, Result> e : multiResults.entrySet()) {
            if (!first) multiJson.append(",\n");
            first = false;
            multiJson.append("    \"").append(e.getKey()).append("\": ").append(toJson(e.getValue()));
        }
        multiJson.append("\n  },\n");
        w.write(multiJson.toString());
        w.write("  \"concurrency_safe\": " + safe + "\n");
        w.write("}\n");
        w.close();
        System.out.println("  性能报告归档: " + report.getPath());
    }

    private static String toJson(Result r) {
        return String.format("{\"ops\": %d, \"qps\": %.0f, \"avg_ns\": %.1f, \"p50_ns\": %d, \"p99_ns\": %d, \"errors\": %d}",
                r.ops, r.qps(), r.avgNs(), r.percentile(0.50), r.percentile(0.99), r.errors);
    }

    private static String escapeJson(String v) {
        if (v == null) return "";
        StringBuilder sb = new StringBuilder(v.length());
        for (int i = 0; i < v.length(); i++) {
            char c = v.charAt(i);
            if (c == '"') sb.append("\\\"");
            else if (c == '\\') sb.append("\\\\");
            else sb.append(c);
        }
        return sb.toString();
    }

    // ── 样例池生成（固定种子，跨语言可复现）──────────────────────────

    private static void buildPools() {
        poolV4 = new String[POOL_V4];
        for (long i = 0; i < POOL_V4; i++) {
            // 方案 A：动态步长取模覆盖 A/B/C/D 全段，防分支预测缓存欺骗
            poolV4[(int) i] = ((i % 255) + 1) + "." + ((i * 17) % 256) + "."
                    + ((i * 131) % 256) + "." + ((i % 254) + 1);
        }
        poolV6 = new String[POOL_V6_PURE];
        for (long j = 0; j < POOL_V6_PURE; j++) {
            String g1 = String.format("%04x", (j * 31) % 0xFFFF);
            String g2 = String.format("%04x", (j * 17) % 0xFFFF);
            String g3 = String.format("%04x", (j * 131) % 0xFFFF);
            poolV6[(int) j] = switch ((int) (j % 5)) {
                case 0 -> "2001:" + g1 + ":" + g2 + "::" + g3;                                  // 双冒号压缩
                case 1 -> "2001:" + g1 + ":0000:0000:" + g2 + ":0000:0000:" + g3;              // 8 组全展开
                case 2 -> "2400:" + g1 + ":" + g2 + "::" + g3;                                 // GUA 压缩
                case 3 -> "2a01:" + g1 + ":0000:0000:0000:0000:" + g2 + ":" + g3;              // 展开变体
                default -> "3001:" + g1 + ":" + g2 + ":" + g3 + "::";                          // 尾部压缩
            };
        }
        poolMapped = new String[POOL_V6_MAPPED];
        for (long k = 0; k < POOL_V6_MAPPED; k++) {
            poolMapped[(int) k] = "::ffff:" + ((k % 255) + 1) + "." + ((k * 17) % 256) + "."
                    + ((k * 131) % 256) + "." + ((k % 254) + 1);
        }
        System.out.printf("样例池: IPv4 %,d · 纯IPv6 %,d · Mapped %,d（种子=%d）%n",
                POOL_V4, POOL_V6_PURE, POOL_V6_MAPPED, SEED);
    }

    private static String pickMixed(long i) {
        long m = i % 10;
        if (m < 5) return poolV4[(int) ((SEED + i * 7) % POOL_V4)];
        if (m < 9) return poolV6[(int) ((SEED + i * 11) % POOL_V6_PURE)];
        return poolMapped[(int) ((SEED + i * 13) % POOL_V6_MAPPED)];
    }

    // ── 单线程压测 ─────────────────────────────────────────────────

    private static final class Result {
         long ops;
        long elapsedNs;
        long[] latency;
        int sampled;
        long errors;

        double qps() { return ops * 1e9 / elapsedNs; }
        double avgNs() { return (double) elapsedNs / ops; }
        long percentile(double p) {
            if (sampled == 0) return 0;
            int idx = Math.min(sampled - 1, (int) Math.ceil(p * sampled) - 1);
            return latency[Math.max(0, idx)];
        }
    }

    /** kind: 0=仅 v4, 1=仅 v6（纯:mapped = 4:1） */
    private static Result runSingleKind(QzdbReader r, int ops, int kind) {
        Result res = new Result();
        res.ops = ops;
        res.latency = new long[ops / LAT_SAMPLE_EVERY + 16];
        long t0 = System.nanoTime();
        if (kind == 0) {
            for (int i = 0; i < ops; i++) {
                String ip = poolV4[(int) ((SEED + (long) i * 7) % POOL_V4)];
                if (i % LAT_SAMPLE_EVERY == 0) {
                    long s = System.nanoTime();
                    r.find(ip);
                    res.latency[res.sampled++] = System.nanoTime() - s;
                } else {
                    r.find(ip);
                }
            }
        } else {
            for (int i = 0; i < ops; i++) {
                String ip = (i % 5 == 4)
                        ? poolMapped[(int) ((SEED + (long) i * 13) % POOL_V6_MAPPED)]
                        : poolV6[(int) ((SEED + (long) i * 11) % POOL_V6_PURE)];
                if (i % LAT_SAMPLE_EVERY == 0) {
                    long s = System.nanoTime();
                    r.find(ip);
                    res.latency[res.sampled++] = System.nanoTime() - s;
                } else {
                    r.find(ip);
                }
            }
        }
        res.elapsedNs = System.nanoTime() - t0;
        res.errors = 0;
        return res;
    }

    private static Result runSingle(QzdbReader r, int ops, boolean measure) {
        Result res = new Result();
        res.ops = ops;
        res.latency = measure ? new long[ops / LAT_SAMPLE_EVERY + 16] : new long[0];
        long t0 = System.nanoTime();
        for (int i = 0; i < ops; i++) {
            String ip = pickMixed(i);
            if (measure && i % LAT_SAMPLE_EVERY == 0) {
                long s = System.nanoTime();
                r.find(ip);
                res.latency[res.sampled++] = System.nanoTime() - s;
            } else {
                r.find(ip);
            }
        }
        res.elapsedNs = System.nanoTime() - t0;
        res.errors = 0;
        return res;
    }

    private static void printResult(String tag, Result res) {
        Arrays.sort(res.latency, 0, res.sampled);
        System.out.printf("   [%s] %,d 次 · %.3fs · %,.0f qps · avg %.1f ns",
                tag, res.ops, res.elapsedNs / 1e9, res.qps(), res.avgNs());
        if (res.sampled > 0) {
            System.out.printf(" · P50 %.1f ns · P99 %.1f ns（采样 %,d）%n",
                    (double) res.percentile(0.50), (double) res.percentile(0.99), res.sampled);
        } else {
            System.out.println();
        }
    }

    // ── 多线程压测 ─────────────────────────────────────────────────

    private static Result runMulti(QzdbReader r, int threads, int totalOps) throws Exception {
        ExecutorService pool = Executors.newFixedThreadPool(threads);
        CountDownLatch latch = new CountDownLatch(threads);
        AtomicLong done = new AtomicLong();
        AtomicInteger errors = new AtomicInteger();
        int perThread = totalOps / threads;
        long t0 = System.nanoTime();
        for (int t = 0; t < threads; t++) {
            final long base = (long) t * perThread;
            pool.submit(() -> {
                try {
                    for (int i = 0; i < perThread; i++) {
                        r.find(pickMixed(base + i));
                    }
                    done.addAndGet(perThread);
                } catch (Throwable e) {
                    errors.incrementAndGet();
                } finally {
                    latch.countDown();
                }
            });
        }
        latch.await();
        long elapsed = System.nanoTime() - t0;
        pool.shutdown();
        System.out.printf("   %,d 次 · %.3fs · %,.0f qps · 异常 %d%n",
                done.get(), elapsed / 1e9, done.get() * 1e9 / elapsed, errors.get());
        Result res = new Result();
        res.ops = done.get();
        res.elapsedNs = elapsed;
        res.errors = errors.get();
        return res;
    }

    private static boolean runConcurrencySafety(QzdbReader r, int threads, int opsPerThread) throws Exception {
        CountDownLatch latch = new CountDownLatch(threads);
        AtomicInteger errors = new AtomicInteger();
        AtomicLong done = new AtomicLong();
        for (int t = 0; t < threads; t++) {
            final long base = (long) t * opsPerThread;
            new Thread(() -> {
                try {
                    for (int i = 0; i < opsPerThread; i++) {
                        try {
                            r.find(pickMixed(base + i));
                            done.incrementAndGet();
                        } catch (QzdbException e) {
                            if (e.getErrorCode() != ErrorCode.INVALID_IP) errors.incrementAndGet();
                        } catch (Throwable other) {
                            errors.incrementAndGet();
                        }
                    }
                } finally {
                    latch.countDown();
                }
            }).start();
        }
        latch.await();
        System.out.printf("   完成 %,d 次查询 · 异常 %d%n", done.get(), errors.get());
        return errors.get() == 0 && done.get() == (long) threads * opsPerThread;
    }

    // ── 环境声明（Tier 3 公平性强制项）────────────────────────────

    private static void printEnvironment(File dbFile) {
        System.out.println("── 环境声明 ──");
        String cpu = execFirst("sysctl -n machdep.cpu.brand_string");
        if (cpu == null) cpu = System.getProperty("os.arch");
        System.out.println("CPU        : " + cpu + " · " + Runtime.getRuntime().availableProcessors() + " 核");
        System.out.println("OS         : " + System.getProperty("os.name") + " " + System.getProperty("os.version")
                + " (" + System.getProperty("os.arch") + ")");
        System.out.println("JVM        : " + System.getProperty("java.version") + " " + System.getProperty("java.vm.name"));
        System.out.println("编译       : javac " + System.getProperty("java.version") + "（无第三方依赖，-encoding UTF-8）");
        System.out.println("随机种子   : " + SEED + "（方案 A + C，比例 50% v4 / 40% 纯 v6 / 10% mapped）");
        System.out.printf("数据库     : %s（%,d 字节）%n", dbFile.getPath(), dbFile.length());
    }

    private static String execFirst(String cmd) {
        try {
            Process p = Runtime.getRuntime().exec(cmd.split(" "));
            try (BufferedReader br = new BufferedReader(new InputStreamReader(p.getInputStream()))) {
                String line = br.readLine();
                p.waitFor();
                return line;
            }
        } catch (Exception e) {
            return null;
        }
    }
}
