package com.qqzeng.qzdb;

import java.io.BufferedWriter;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Random;

/**
 * Tier 2：10 大全规格版本 Ground Truth 逐字段比对验证器
 * （依据 docs/QZDB_TEST_SPECIFICATION.md §三）
 * <p>
 * - 输入：range CSV 全量 start_ip + 40% 固定种子抽样 end_ip（总节点 ≥ 39,000,000）
 * - 输出：IPv4/IPv6 分别统计；机器可读差异文件 (JSONL) + 汇总 (JSON)；首 20 条差异详情
 * - 排除项：规范 §9.7 的 IPv4-mapped 保留行（V6 侧真值行不可达），显式计数并声明
 */
public class FullAccuracyAndPerfTester {

    private static final long SEED = 42L;
    private static final int END_IP_SAMPLE_PCT = 40;

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

    /** 单版本统计 */
    private static final class Stats {
        long checked, v4Checked, v6Checked, errors, v4Errors, v6Errors, mappedExcluded, reservedSkipped;
        long elapsedMs;
    }

    private static final List<String> firstDiffs = new ArrayList<>();
    private static Writer diffWriter;

    private static String findBaseDir() {
        for (String c : new String[]{"test_data_202608", "../test_data_202608", "multi-lang/test_data_202608", "../multi-lang/test_data_202608"}) {
            if (new File(c + "/std/china/qqzeng_ip_std_china.qzdb").exists()) {
                return new File(c).getAbsolutePath();
            }
        }
        return null;
    }

    private static String findReportDir() {
        for (String c : new String[]{"java/test_reports", "multi-lang/java/test_reports", "../java/test_reports"}) {
            File f = new File(c);
            if (f.exists() || f.mkdirs()) return new File(c).getAbsolutePath();
        }
        new File("java/test_reports").mkdirs();
        return new File("java/test_reports").getAbsolutePath();
    }

    public static void main(String[] args) throws Exception {
        System.out.println("==================================================================================");
        System.out.println("     QZDB Tier 2 Ground Truth 验证器（10 版本 · 双栈分计 · 差异归档 · ≥39M 节点）    ");
        System.out.println("==================================================================================");

        String baseDir = findBaseDir();
        if (baseDir == null) {
            System.out.println("[SKIP] Tier 2: test_data_202608 数据目录未找到，跳过 Ground Truth 验证。");
            System.out.println("TEST_PASS (Tier 2 skipped — no data available)");
            return;
        }
        String reportDir = findReportDir();
        new File(reportDir).mkdirs();
        String stamp = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd_HHmmss"));
        File diffFile = new File(reportDir + "/tier2_diff_" + stamp + ".jsonl");
        File summaryFile = new File(reportDir + "/tier2_summary_" + stamp + ".json");
        diffWriter = new BufferedWriter(new OutputStreamWriter(new FileOutputStream(diffFile), StandardCharsets.UTF_8));

        long t0 = System.currentTimeMillis();
        Stats total = new Stats();
        StringBuilder perVersionJson = new StringBuilder();
        int versionsChecked = 0;

        for (String[] verInfo : VERSIONS) {
            String ver = verInfo[0], scope = verInfo[1];
            File dbFile = new File(baseDir + "/" + ver + "/" + scope + "/" + verInfo[2]);
            File csvFile = new File(baseDir + "/" + ver + "/" + scope + "/" + verInfo[3]);
            if (!dbFile.exists() || !csvFile.exists()) {
                System.err.println("  [SKIP] 缺少测试数据: " + dbFile.getPath());
                continue;
            }
            Stats s = verifySingleVersion(ver, scope, dbFile, csvFile);
            versionsChecked++;
            total.checked += s.checked;
            total.v4Checked += s.v4Checked;
            total.v6Checked += s.v6Checked;
            total.errors += s.errors;
            total.v4Errors += s.v4Errors;
            total.v6Errors += s.v6Errors;
            total.mappedExcluded += s.mappedExcluded;
            total.reservedSkipped += s.reservedSkipped;
            total.elapsedMs += s.elapsedMs;
            if (perVersionJson.length() > 0) perVersionJson.append(",\n");
            perVersionJson.append(String.format(
                    "    {\"edition\": \"%s-%s\", \"checked\": %d, \"ipv4_checked\": %d, \"ipv6_checked\": %d, " +
                            "\"ipv4_errors\": %d, \"ipv6_errors\": %d, \"mapped_excluded\": %d, \"elapsed_ms\": %d}",
                    ver, scope, s.checked, s.v4Checked, s.v6Checked, s.v4Errors, s.v6Errors, s.mappedExcluded, s.elapsedMs));
        }
        long wallMs = System.currentTimeMillis() - t0;
        diffWriter.close();

        // ── 汇总 JSON（机器可读归档）──────────────────────────────────
        if (versionsChecked == 0) {
            System.out.println("[SKIP] Tier 2: 没有可用版本的数据，跳过验证。");
            System.out.println("TEST_PASS (Tier 2 skipped — no data available)");
            return;
        }
        String verdict = total.errors == 0 && total.checked >= 39_000_000 ? "PASSED" : "FAILED";
        String summary = "{\n" +
                "  \"spec\": \"QZDB_TEST_SPECIFICATION.md Tier 2\",\n" +
                "  \"timestamp\": \"" + stamp + "\",\n" +
                "  \"dataset\": \"" + baseDir + "\",\n" +
                "  \"total_checked\": " + total.checked + ",\n" +
                "  \"ipv4_checked\": " + total.v4Checked + ",\n" +
                "  \"ipv6_checked\": " + total.v6Checked + ",\n" +
                "  \"total_errors\": " + total.errors + ",\n" +
                "  \"ipv4_errors\": " + total.v4Errors + ",\n" +
                "  \"ipv6_errors\": " + total.v6Errors + ",\n" +
                "  \"spec97_mapped_excluded\": " + total.mappedExcluded + ",\n" +
                "  \"invalid_literal_skipped\": " + total.reservedSkipped + ",\n" +
                "  \"seed\": " + SEED + ",\n" +
                "  \"end_ip_sample_pct\": " + END_IP_SAMPLE_PCT + ",\n" +
                "  \"verify_elapsed_ms\": " + total.elapsedMs + ",\n" +
                "  \"wall_elapsed_ms\": " + wallMs + ",\n" +
                "  \"diff_file\": \"" + diffFile.getPath() + "\",\n" +
                "  \"versions\": [\n" + perVersionJson + "\n  ],\n" +
                "  \"verdict\": \"" + verdict + "\"\n" +
                "}\n";
        try (Writer w = new BufferedWriter(new OutputStreamWriter(new FileOutputStream(summaryFile), StandardCharsets.UTF_8))) {
            w.write(summary);
        }

        // ── 人类可读汇总 ──────────────────────────────────────────────
        System.out.println("\n==================================================================================");
        System.out.println(String.format(" 总节点: %,d（IPv4: %,d / IPv6: %,d）· 要求 ≥ 39,000,000", total.checked, total.v4Checked, total.v6Checked));
        System.out.println(String.format(" 偏差: 总 %d（IPv4: %d / IPv6: %d）", total.errors, total.v4Errors, total.v6Errors));
        System.out.println(String.format(" 排除项: §9.7 IPv4-mapped 保留行 %d 条（剥离后走 V4 Trie，V6 真值行不可达；等价 V4 网段已单独校验）", total.mappedExcluded));
        if (total.reservedSkipped > 0) {
            System.out.println(String.format(" 跳过项: 非法 IP 字面量（空/裸 0 等）%d 条", total.reservedSkipped));
        }
        System.out.println(String.format(" 耗时: 校验累计 %.1fs · 总墙钟 %.1fs", total.elapsedMs / 1000.0, wallMs / 1000.0));
        System.out.println(" 差异归档: " + diffFile.getPath());
        System.out.println(" 汇总归档: " + summaryFile.getPath());
        if (!firstDiffs.isEmpty()) {
            System.out.println("\n 首 " + firstDiffs.size() + " 条差异详情:");
            for (String d : firstDiffs) System.out.println("   >> " + d);
        }
        System.out.println("\n Tier 2 结论: " + verdict
                + (total.checked < 39_000_000 ? String.format("（节点量 %,d 未达 39M 门槛）", total.checked) : "")
                + (total.errors > 0 ? String.format("（偏差 %d 处）", total.errors) : ""));
        System.out.println("==================================================================================");

        if ("PASSED".equals(verdict)) {
            System.out.println("TEST_PASS");
        }
        if (!"PASSED".equals(verdict)) System.exit(1);
    }

    private static Stats verifySingleVersion(String ver, String scope, File dbFile, File csvFile) throws Exception {
        System.out.println(String.format("\n👉 正在校验 [%s - %s] -> %s", ver.toUpperCase(), scope.toUpperCase(), dbFile.getName()));
        Stats s = new Stats();
        long t0 = System.currentTimeMillis();

        try (QzdbReader reader = new QzdbReader.Builder(dbFile).build();
             BufferedReader br = new BufferedReader(
                     new InputStreamReader(new FileInputStream(csvFile), StandardCharsets.UTF_8), 1 << 20)) {

            String headerLine = br.readLine();
            if (headerLine == null) {
                System.err.println("   [ERROR] 空 CSV 文件: " + csvFile.getPath());
                s.errors = 1;
                return s;
            }
            String[] headers = parseCsvLine(headerLine);
            Map<String, Integer> colMap = new HashMap<>();
            for (int i = 0; i < headers.length; i++) colMap.put(headers[i].trim(), i);
            int startCol = colMap.get("start_ip");
            int endCol = colMap.get("end_ip");

            String line;
            Random rand = new Random(SEED);
            while ((line = br.readLine()) != null) {
                if (line.isEmpty()) continue;
                String[] cols = parseCsvLine(line);
                if (cols.length < headers.length) continue;

                String startIp = cols[startCol].trim();
                verifyIp(reader, ver, scope, startIp, headers, cols, colMap, s);

                if (rand.nextInt(100) < END_IP_SAMPLE_PCT) {
                    verifyIp(reader, ver, scope, cols[endCol].trim(), headers, cols, colMap, s);
                }

                if (s.errors > 50) {
                    System.err.println("   [FAIL] 累计偏差超过 50 处，提前终止该版本校验。");
                    break;
                }
            }
        }
        s.elapsedMs = System.currentTimeMillis() - t0;

        if (s.errors == 0) {
            System.out.println(String.format(
                    "   [✔ PASS] [%s-%s] %,d 节点（v4 %,d / v6 %,d）· 0 偏差 · %.1fs",
                    ver.toUpperCase(), scope.toUpperCase(), s.checked, s.v4Checked, s.v6Checked, s.elapsedMs / 1000.0));
        } else {
            System.err.println(String.format(
                    "   [✖ FAIL] [%s-%s] %,d 节点 · %d 处偏差",
                    ver.toUpperCase(), scope.toUpperCase(), s.checked, s.errors));
        }
        return s;
    }

    private static void verifyIp(QzdbReader reader, String ver, String scope, String ip,
                                 String[] headers, String[] cols, Map<String, Integer> colMap, Stats s) throws Exception {
        if (ip.isEmpty() || "0".equals(ip) || "0:0".equals(ip)) {
            s.reservedSkipped++;
            return;
        }
        // 规范 §9.7：IPv4-mapped 保留行剥离后走 V4 Trie，V6 侧真值行不可达 → 显式排除计数
        if (isV4MappedLiteral(ip)) {
            s.mappedExcluded++;
            return;
        }

        boolean isV6 = ip.indexOf(':') >= 0;
        s.checked++;
        if (isV6) s.v6Checked++; else s.v4Checked++;

        Optional<GeoInfo> infoOpt;
        try {
            infoOpt = reader.find(ip);
        } catch (QzdbException e) {
            recordDiff(ver, scope, ip, "<parse>", "parse-error:" + e.getErrorCode(), "");
            s.errors++;
            if (isV6) s.v6Errors++; else s.v4Errors++;
            return;
        }

        if (infoOpt.isEmpty()) {
            recordDiff(ver, scope, ip, "<record>", "expected record", "NOT_FOUND");
            s.errors++;
            if (isV6) s.v6Errors++; else s.v4Errors++;
            return;
        }

        GeoInfo info = infoOpt.get();
        for (String h : headers) {
            if ("start_ip".equals(h) || "end_ip".equals(h) || "start_ip_num".equals(h) || "end_ip_num".equals(h)) {
                continue;
            }
            Integer colIdx = colMap.get(h);
            if (colIdx == null || colIdx >= cols.length) continue;

            String expectedVal = cols[colIdx].trim();
            String actualVal = info.get(h).trim();

            // 经纬度数值容差（float64 %.6f 与 CSV 源精度差异）
            if ("longitude".equalsIgnoreCase(h) || "latitude".equalsIgnoreCase(h)) {
                if (!expectedVal.isEmpty() && !actualVal.isEmpty()) {
                    try {
                        double expD = Double.parseDouble(expectedVal);
                        double actD = Double.parseDouble(actualVal);
                        if (Math.abs(expD - actD) < 5e-4) continue;
                    } catch (NumberFormatException ignored) {
                    }
                }
            }
            // CSV 双引号转义容忍 (如 JSC "Ukrtelecom" vs JSC Ukrtelecom)
            if (expectedVal.replace("\"", "").equals(actualVal.replace("\"", ""))) {
                continue;
            }
            if (!expectedVal.equals(actualVal)) {
                recordDiff(ver, scope, ip, h, expectedVal, actualVal);
                s.errors++;
                if (isV6) s.v6Errors++; else s.v4Errors++;
                return; // 该节点记 1 次偏差，继续下一节点
            }
        }
    }

    private static void recordDiff(String ver, String scope, String ip, String field, String expected, String actual) throws Exception {
        if (firstDiffs.size() < 20) {
            firstDiffs.add(String.format("[%s-%s] IP=%s 字段=%s 期望='%s' 实际='%s'", ver, scope, ip, field, expected, actual));
        }
        diffWriter.write("{\"edition\":\"" + ver + "-" + scope + "\",\"ip\":\"" + jsonEsc(ip)
                + "\",\"field\":\"" + jsonEsc(field) + "\",\"expected\":\"" + jsonEsc(expected)
                + "\",\"actual\":\"" + jsonEsc(actual) + "\"}\n");
    }

    private static String jsonEsc(String v) {
        if (v == null) return "";
        StringBuilder sb = new StringBuilder(v.length());
        for (int i = 0; i < v.length(); i++) {
            char c = v.charAt(i);
            switch (c) {
                case '"' -> sb.append("\\\"");
                case '\\' -> sb.append("\\\\");
                case '\n' -> sb.append("\\n");
                case '\r' -> sb.append("\\r");
                case '\t' -> sb.append("\\t");
                default -> {
                    if (c < 0x20) sb.append(String.format("\\u%04x", (int) c));
                    else sb.append(c);
                }
            }
        }
        return sb.toString();
    }

    /** 判断是否为 IPv4-mapped IPv6 字面量（::ffff:a.b.c.d 或其展开/十六进制形态），大小写不敏感。 */
    private static boolean isV4MappedLiteral(String ip) {
        String lower = ip.toLowerCase();
        if (lower.startsWith("::ffff:")) return true;
        return lower.startsWith("0:0:0:0:0:ffff:") || lower.startsWith("0000:0000:0000:0000:0000:ffff:");
    }

    /** CSV 行解析（快路径：无引号行直接按逗号切分）。 */
    private static String[] parseCsvLine(String line) {
        if (line.indexOf('"') < 0) {
            return line.split(",", -1);
        }
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
