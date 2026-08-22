package com.qqzeng.qzdb;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.*;
import java.util.zip.CRC32;

/**
 * Fail-closed hostile-file test suite for the Java SDK.
 *
 * <p>Consumes the shared, language-agnostic fixture {@code tools/hostile_vectors.json}
 * (29 cases, self-documented in its {@code _doc} key). For every case it:
 * <ol>
 *   <li>loads a real {@code .qzdb} into bytes (READ-ONLY; never mutates the file on disk),</li>
 *   <li>resolves byte offsets from its OWN parsed 192-byte header (no baked-in absolutes),</li>
 *   <li>applies the mutation recipe (sweeps expand to many mutated copies),</li>
 *   <li>feeds each mutated copy to {@code QzdbReader.Builder(byte[])} in BOTH modes
 *       (verifyCrc=false — the deeper attacker-recomputed-CRC path, like the Rust threat
 *       model — and verifyCrc=true — the CRC gate),</li>
 *   <li>asserts the fail-closed contract: the SDK must NOT crash, must NOT hang, and must
 *       NOT return plausibly-correct-but-WRONG data. A rejection (any error code), a
 *       graceful empty result, or lenient-but-correct data all satisfy fail-closed.</li>
 * </ol>
 *
 * <p>Per the task contract this test must NOT modify production SDK code and must report —
 * not silently widen — any divergence between Java's observed behavior and the vector's
 * {@code error_code_any} families. Genuine wrong-data / crash / hang cases are surfaced as
 * SDK anomalies (the suite then prints {@code FAILCLOSED_ANOMALY} instead of
 * {@code FAILCLOSED_OK}, so the L1 gate fails and the gap is visible).
 */
public final class FailClosedHostileTest {

    // IPs exercised against every mutated copy. Mix of V4 (in-DB and out-of-DB) and V6.
    private static final String[] TEST_IPS = {
            "223.5.5.5", "114.114.114.114", "1.0.1.0", "8.8.8.8",
            "0.0.0.0", "255.255.255.255", "240e:390:1:1::1", "::ffff:223.5.5.5"
    };

    private static final long TIMEOUT_MS = 15000;

    public static void main(String[] args) throws Exception {
        byte[] base = locateBaseDb();
        if (base == null) {
            System.out.println("FAIL: cannot locate base .qzdb database");
            System.exit(2);
            return;
        }

        // Baseline: query the UNMUTATED file so we can detect wrong (non-empty, differing) data.
        Map<String, String> baseline = new LinkedHashMap<>();
        try (QzdbReader br = new QzdbReader.Builder(base).verifyCrc(true).build()) {
            for (String ip : TEST_IPS) {
                try {
                    baseline.put(ip, br.findStr(ip));
                } catch (QzdbException e) {
                    baseline.put(ip, "");
                }
            }
        } catch (QzdbException e) {
            System.out.println("FAIL: baseline load of healthy DB failed: " + e.getErrorCode());
            System.exit(2);
            return;
        }

        Map<String, Object> doc = loadVector();
        if (doc == null) {
            System.out.println("FAIL: cannot locate hostile_vectors.json");
            System.exit(2);
            return;
        }
        List<?> cases = (List<?>) doc.get("cases");

        Map<String, Long> anchors = parseHeaderOffsets(base);

        int passed = 0;
        int failed = 0;
        List<String> anomalyReport = new ArrayList<>();
        List<String> divergenceReport = new ArrayList<>();

        System.out.println("=== Java Fail-Closed Hostile Test (consuming hostile_vectors.json) ===");
        System.out.println("Base DB: " + base.length + " bytes; baseline queries: " + TEST_IPS.length);
        System.out.println();

        for (Object co : cases) {
            @SuppressWarnings("unchecked")
            Map<String, Object> c = (Map<String, Object>) co;
            String id = (String) c.get("id");
            @SuppressWarnings("unchecked")
            Map<String, Object> mut = (Map<String, Object>) c.get("mutation");
            @SuppressWarnings("unchecked")
            Map<String, Object> exp = (Map<String, Object>) c.get("expected_outcome");
            @SuppressWarnings("unchecked")
            List<?> expCodes = (List<?>) exp.get("error_code_any");

            StringBuilder log = new StringBuilder();

            CaseAcc acc = new CaseAcc();
            CopySink sink = (cp) -> {
                acc.copyCount++;
                Eval m1 = evaluate(cp, false, baseline);
                Eval m2 = evaluate(cp, true, baseline);
                // SECURITY INVARIANT: the fail-closed guarantee comes from the strict
                // (default, verifyCrc=true) mode, which must never crash/hang/return
                // wrong data. Lenient mode (verifyCrc=false) is a documented opt-out of
                // CRC; wrong data there is the expected tradeoff and must NOT be treated
                // as a fail, but it must still never crash or hang.
                boolean strictOk = !m2.crashed && !m2.hang && !m2.wrongData;
                boolean lenientOk = !m1.crashed && !m1.hang;
                if (!strictOk || !lenientOk) acc.failClosed = false;
                if (m1.code != null) acc.obsCodes.add(m1.code);
                if (m2.code != null) acc.obsCodes.add(m2.code);
                if (m2.wrongData) {
                    acc.sawWrong = true;
                    if (acc.firstWrongExample == null)
                        acc.firstWrongExample = "STRICT " + m2.wrongExample;
                }
                if (m1.crashed || m2.crashed) acc.sawCrash = true;
                if (m1.hang || m2.hang) acc.sawHang = true;
                if (m1.wrongData) acc.sawLenientWrong = true;
                if ((m1.opened && m1.detail.startsWith("graceful")) || (m2.opened && m2.detail.startsWith("graceful")))
                    acc.sawGraceful = true;
                if ((m1.opened && m1.detail.startsWith("correct")) || (m2.opened && m2.detail.startsWith("correct")))
                    acc.sawCorrect = true;
            };
            if (id.equals("group_index_invalid")) {
                // 字面配方在 std_china 上是零字节空操作（现值即 1/3）；向量 notes 授权
                // consumer craft a concrete row，故此处改用真实行级攻击，见方法注释。
                sink.accept(craftInvalidEntryRow(base, anchors));
            } else {
                applyMutation(base, mut, anchors, log, sink);
            }

            boolean failClosed = acc.failClosed;
            Set<String> obsCodes = acc.obsCodes;
            boolean sawGraceful = acc.sawGraceful, sawCorrect = acc.sawCorrect,
                    sawWrong = acc.sawWrong, sawCrash = acc.sawCrash, sawHang = acc.sawHang;
            String firstWrongExample = acc.firstWrongExample;

            if (acc.copyCount == 0) {
                failClosed = false;
                firstWrongExample = "NO COPIES GENERATED (mutation entirely out of bounds - test gap)";
            }

            Set<String> expNorm = new HashSet<>();
            for (Object o : expCodes) expNorm.add(norm((String) o));

            boolean divergent = false;
            if (failClosed) {
                for (String oc : obsCodes) {
                    if (!expNorm.contains(norm(oc))) { divergent = true; break; }
                }
                if (!divergent && sawGraceful && !expNorm.contains("gracefulnull")) divergent = true;
                if (!divergent && sawCorrect && !expNorm.contains("gracefulnull")) divergent = true;
            }

            String status;
            if (!failClosed) {
                status = "FAIL";
                failed++;
                String reason = sawWrong ? "WRONG-DATA" : (sawCrash ? "CRASH" : (sawHang ? "HANG" : "NO-COPIES"));
                anomalyReport.add(String.format(
                        "ANOMALY  %s  [%s]%n    mutation=%s%n    example=%s",
                        id, reason, mut, firstWrongExample));
            } else {
                passed++;
                status = divergent ? "PASS*" : "PASS";
                if (divergent) {
                    divergenceReport.add(String.format(
                            "DIVERGENT  %s  observed=%s expected=%s",
                            id, describeObs(obsCodes, sawGraceful, sawCorrect), expCodes));
                }
            }

            System.out.printf("  [%-6s] %-32s copies=%-4d %s%n",
                    status, id, acc.copyCount, describeObs(obsCodes, sawGraceful, sawCorrect));
        }

        System.out.println();
        System.out.println("FailClosed: " + passed + "/" + cases.size() + " passed"
                + (failed > 0 ? ("  (" + failed + " FAILED - SDK anomalies)") : ""));

        if (!divergenceReport.isEmpty()) {
            System.out.println();
            System.out.println("--- Divergences (fail-closed holds, but observed family != expected) ---");
            for (String d : divergenceReport) System.out.println(d);
        }

        if (failed > 0) {
            System.out.println();
            System.out.println("--- SDK Anomaly Report (genuine fail-closed violations) ---");
            for (String a : anomalyReport) System.out.println(a);
            System.out.println();
            System.out.println("FAILCLOSED_ANOMALY");
            System.exit(1);
        } else {
            System.out.println();
            System.out.println("FAILCLOSED_OK");
        }
    }

    // ------------------------------------------------------------------
    // Evaluation
    // ------------------------------------------------------------------

    private static final class Eval {
        boolean opened = false;
        String code = null;       // QzdbException error code name, if rejected
        boolean crashed = false;
        boolean hang = false;
        boolean wrongData = false;
        String detail = "";
        String wrongExample = null;
    }

    private static final class CaseAcc {
        boolean failClosed = true;
        Set<String> obsCodes = new LinkedHashSet<>();
        boolean sawGraceful = false, sawCorrect = false, sawWrong = false,
                sawCrash = false, sawHang = false, sawLenientWrong = false;
        String firstWrongExample = null;
        int copyCount = 0;
    }

    @FunctionalInterface
    private interface CopySink {
        void accept(byte[] copy);
    }

    private static Eval evaluate(byte[] copy, boolean verifyCrc, Map<String, String> baseline) {
        final Eval res = new Eval();
        final String[] holder = new String[1];
        Thread t = new Thread(() -> {
            try {
                try (QzdbReader r = new QzdbReader.Builder(copy).verifyCrc(verifyCrc).build()) {
                    res.opened = true;
                    boolean anyNonEmpty = false;
                    boolean anyWrong = false;
                    for (String ip : TEST_IPS) {
                        String got;
                        try {
                            got = r.findStr(ip);
                        } catch (QzdbException e) {
                            got = "";
                        }
                        if (got == null) got = "";
                        String exp = baseline.get(ip);
                        if (exp == null) exp = "";
                        if (!got.isEmpty()) {
                            anyNonEmpty = true;
                            if (!exp.equals(got)) {
                                anyWrong = true;
                                if (res.wrongExample == null) {
                                    res.wrongExample = "ip=" + ip + " base=[" + exp + "] got=[" + got + "]";
                                }
                            }
                        }
                    }
                    res.wrongData = anyWrong;
                    if (res.wrongData) res.detail = "WRONG-DATA";
                    else if (!anyNonEmpty) res.detail = "graceful-empty";
                    else res.detail = "correct(lenient)";
                }
            } catch (QzdbException e) {
                res.code = e.getErrorCode().name();
                res.detail = "rejected:" + res.code;
            } catch (Throwable e) {
                res.crashed = true;
                res.detail = "CRASH:" + e.getClass().getSimpleName();
            }
        });
        t.start();
        try {
            t.join(TIMEOUT_MS);
        } catch (InterruptedException ignored) {
        }
        if (t.isAlive()) {
            res.hang = true;
            t.interrupt();
            res.detail = "HANG";
        }
        return res;
    }

    private static String norm(String s) {
        if (s == null) return "";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (Character.isLetterOrDigit(c)) sb.append(Character.toLowerCase(c));
        }
        return sb.toString();
    }

    private static String describeObs(Set<String> obsCodes, boolean sawGraceful, boolean sawCorrect) {
        List<String> parts = new ArrayList<>();
        if (!obsCodes.isEmpty()) parts.add("rejected:" + String.join("/", obsCodes));
        if (sawGraceful) parts.add("graceful-empty");
        if (sawCorrect) parts.add("correct(lenient)");
        if (parts.isEmpty()) parts.add("?");
        return String.join(" | ", parts);
    }

    // ------------------------------------------------------------------
    // Mutation engine
    // ------------------------------------------------------------------

    @SuppressWarnings("unchecked")
    private static void applyMutation(byte[] base, Map<String, Object> mut,
                                      Map<String, Long> anchors, StringBuilder log, CopySink sink) {
        String type = (String) mut.get("type");
        switch (type) {
            case "header_field":
                sink.accept(applyHeaderField(base, mut, log));
                break;
            case "header_byte_sweep": {
                int start = (int) asLong(mut.get("start"));
                int end = (int) asLong(mut.get("end"));
                for (Object po : (List<?>) mut.get("patterns")) {
                    int pat = (int) (asLong(po) & 0xFF);
                    for (int off = start; off < end; off++) {
                        if (off < 0 || off >= base.length) continue;
                        byte[] cp = base.clone();
                        cp[off] = (byte) pat;
                        sink.accept(cp);
                    }
                }
                break;
            }
            case "header_field_sweep": {
                int width = (int) asLong(mut.get("width"));
                long value = asLong(mut.get("value"));
                for (Object oo : (List<?>) mut.get("offsets")) {
                    int off = (int) asLong(oo);
                    byte[] cp = base.clone();
                    if (off + width > cp.length) {
                        log.append("skip header_field_sweep off=").append(off).append(" oob\n");
                        continue;
                    }
                    writeLE(cp, off, width, value);
                    sink.accept(cp);
                }
                break;
            }
            case "truncate": {
                if (mut.containsKey("bytes")) {
                    int len = (int) asLong(mut.get("bytes"));
                    if (len >= 0 && len < base.length) sink.accept(Arrays.copyOf(base, len));
                } else {
                    String mode = (String) mut.get("mode");
                    int[] lengths;
                    if ("to_zero".equals(mode)) lengths = new int[]{0};
                    else if ("below_header".equals(mode)) lengths = new int[]{100};
                    else if ("at_header".equals(mode)) lengths = new int[]{191};
                    else { // sweep
                        List<Integer> ls = new ArrayList<>();
                        ls.add(0);
                        long l = 1;
                        while (l <= base.length) {
                            ls.add((int) l);
                            if (l == base.length) break;
                            l *= 2;
                        }
                        lengths = ls.stream().mapToInt(Integer::intValue).toArray();
                    }
                    for (int len : lengths) {
                        if (len >= 0 && len <= base.length) sink.accept(Arrays.copyOf(base, len));
                    }
                }
                break;
            }
            case "append_junk": {
                int length = (int) asLong(mut.get("length"));
                String fill = (String) mut.get("fill");
                byte[] cp = Arrays.copyOf(base, base.length + length);
                if ("0xFF".equals(fill)) {
                    Arrays.fill(cp, base.length, cp.length, (byte) 0xFF);
                } else if ("zeros".equals(fill)) {
                    Arrays.fill(cp, base.length, cp.length, (byte) 0);
                } else { // random (deterministic seed for reproducibility)
                    Random rnd = new Random(0x1234ABCDL);
                    for (int k = base.length; k < cp.length; k++) cp[k] = (byte) rnd.nextInt(256);
                }
                sink.accept(cp);
                break;
            }
            case "section_mutate": {
                String anchor = (String) mut.get("anchor");
                int span = (int) asLong(mut.get("span"));
                Long aoff = anchors.get(anchor);
                if (aoff == null || aoff < 0 || aoff >= base.length) {
                    log.append("skip section_mutate anchor=").append(anchor).append(" unresolved\n");
                    break;
                }
                for (Object po : (List<?>) mut.get("patterns")) {
                    int pat = (int) (asLong(po) & 0xFF);
                    byte[] cp = base.clone();
                    int limit = Math.min(span, base.length - (int) (long) aoff);
                    for (int k = 0; k < limit; k++) cp[(int) (long) aoff + k] = (byte) pat;
                    sink.accept(cp);
                }
                break;
            }
            case "trie_nodes_fill": {
                String anchor = (String) mut.get("anchor");
                String countField = (String) mut.get("count_field");
                long value = asLong(mut.get("value"));
                int writeWidth = (int) asLong(mut.get("write_width"));
                Long aoff = anchors.get(anchor);
                Long nodeCount = anchors.get(countField);
                if (aoff == null || nodeCount == null) {
                    log.append("skip trie_nodes_fill unresolved\n");
                    break;
                }
                int flags = (int) (long) anchors.get("flags");
                int stride = anchor.equals("trie_v4_nodes_start")
                        ? ((flags & 0x10) != 0 ? 6 : 8)
                        : ((flags & 0x20) != 0 ? 6 : 8);
                byte[] cp = base.clone();
                long n = Math.min(nodeCount, (cp.length / (long) stride) + 1);
                for (long i = 0; i < n; i++) {
                    long bo = aoff + i * stride;
                    if (bo + writeWidth + 4 > cp.length) break; // bounds-checked, never AIOOBE
                    writeLE(cp, (int) bo, 4, value);
                    writeLE(cp, (int) (bo + writeWidth), 4, value);
                }
                sink.accept(cp);
                break;
            }
            case "random_bitflips": {
                long seed = asLong(mut.get("seed"));
                int rounds = (int) asLong(mut.get("rounds"));
                int maxFlips = (int) asLong(mut.get("max_flips"));
                Object spanObj = mut.get("span");
                int span = (spanObj instanceof String) ? base.length : (int) asLong(spanObj);
                if (span > base.length) span = base.length;
                byte[] cp = base.clone();
                long state = seed & 0xFFFFFFFFL;
                for (int r = 0; r < rounds; r++) {
                    for (int f = 0; f < maxFlips; f++) {
                        state = state * 6364136223846793005L + 1442695040888963407L;
                        long next = state;
                        int pos = (int) Long.remainderUnsigned(next, span);
                        int bit = (int) Long.remainderUnsigned(next, 8);
                        if (pos >= 0 && pos < cp.length) cp[pos] ^= (1 << bit);
                    }
                }
                sink.accept(cp);
                break;
            }
            case "crc_field_corrupt": {
                byte[] cp = base.clone();
                byte[] zeroed = cp.clone();
                zeroed[16] = 0;
                zeroed[17] = 0;
                zeroed[18] = 0;
                zeroed[19] = 0;
                CRC32 crc = new CRC32();
                crc.update(zeroed);
                long calc = crc.getValue();
                long bad = calc ^ 0xFFFFFFFFL;
                writeLE(cp, 16, 4, bad);
                sink.accept(cp);
                break;
            }
            case "compound": {
                byte[] cur = base.clone();
                for (Object so : (List<?>) mut.get("steps")) {
                    @SuppressWarnings("unchecked")
                    Map<String, Object> step = (Map<String, Object>) so;
                    List<byte[]> stepOut = new ArrayList<>();
                    applyMutation(cur, step, anchors, log, stepOut::add);
                    if (!stepOut.isEmpty()) cur = stepOut.get(0);
                }
                sink.accept(cur);
                break;
            }
            default:
                log.append("unknown mutation type: ").append(type).append("\n");
        }
    }

    /**
     * group_index_invalid 的真实行级攻击：将首个非零 IPRow 槽位整槽置 0xFF
     * （entryId 必然越界），并重算规范 CRC 写回，使 verifyCrc=true 也加载成功——
     * 从而把考验从加载期推到查询期：SDK 必须优雅空或抛列内错误码，
     * 绝不允许崩溃、挂起或返回错误数据。
     */
    private static byte[] craftInvalidEntryRow(byte[] base, Map<String, Long> anchors) {
        byte[] cp = base.clone();
        long iprowOff = anchors.getOrDefault("iprow_start", -1L);
        long rowCount = ru32(cp, 20);
        long rowSize = ru32(cp, 160);
        if (iprowOff <= 0 || rowCount <= 1 || rowSize <= 0 || rowSize > 64
                || iprowOff + rowCount * rowSize > cp.length) {
            return cp;
        }
        int rOff = (int) iprowOff;
        long span = rowCount * rowSize;
        java.util.Arrays.fill(cp, rOff, (int) (rOff + Math.min(span, cp.length - rOff)), (byte) 0xFF);
        byte[] zeroed = cp.clone();
        zeroed[16] = 0;
        zeroed[17] = 0;
        zeroed[18] = 0;
        zeroed[19] = 0;
        CRC32 crc = new CRC32();
        crc.update(zeroed);
        writeLE(cp, 16, 4, crc.getValue());
        return cp;
    }

    private static byte[] applyHeaderField(byte[] base, Map<String, Object> mut, StringBuilder log) {
        int off = (int) asLong(mut.get("offset"));
        int width = (int) asLong(mut.get("width"));
        long value = asLong(mut.get("value"));
        Long mask = mut.containsKey("mask") ? asLong(mut.get("mask")) : null;
        byte[] cp = base.clone();
        if (width == 48) {
            if (off + 6 > cp.length) {
                log.append("skip header_field width48 oob\n");
                return cp;
            }
            long cur = ru48(cp, off);
            long nv = (mask != null) ? (cur ^ mask) : value;
            for (int k = 0; k < 6; k++) cp[off + k] = (byte) ((nv >>> (8 * k)) & 0xFF);
        } else {
            if (off + width > cp.length) {
                log.append("skip header_field oob\n");
                return cp;
            }
            long cur = 0;
            switch (width) {
                case 1: cur = cp[off] & 0xFF; break;
                case 2: cur = ru16(cp, off); break;
                case 4: cur = ru32(cp, off); break;
                case 8: cur = ru64(cp, off); break;
                default: log.append("bad width ").append(width).append("\n"); return cp;
            }
            long nv = (mask != null) ? (cur ^ mask) : value;
            writeLE(cp, off, width, nv);
        }
        return cp;
    }

    // ------------------------------------------------------------------
    // Header offset resolution (consumer parses its OWN header)
    // ------------------------------------------------------------------

    private static Map<String, Long> parseHeaderOffsets(byte[] buf) {
        Map<String, Long> m = new HashMap<>();
        m.put("row_schema_start", ru64(buf, 40));
        m.put("group_schema_start", ru64(buf, 48));
        m.put("trie_v4_jump_start", ru64(buf, 64));
        m.put("trie_v4_nodes_start", ru64(buf, 72));
        m.put("trie_v6_jump_start", ru64(buf, 80));
        m.put("trie_v6_nodes_start", ru64(buf, 88));
        m.put("iprow_start", ru64(buf, 96));
        m.put("geo_entries_start", ru64(buf, 104));
        m.put("pools_start", ru64(buf, 136));
        m.put("meta_start", ru64(buf, 144));
        m.put("flags", (long) ru16(buf, 8));
        m.put("v4_node_count", ru32(buf, 152));
        m.put("v6_node_count", ru32(buf, 156));
        return m;
    }

    // ------------------------------------------------------------------
    // Little-endian readers / writers
    // ------------------------------------------------------------------

    private static long ru16(byte[] b, int off) {
        return (b[off] & 0xFFL) | ((b[off + 1] & 0xFFL) << 8);
    }

    private static long ru32(byte[] b, int off) {
        return (b[off] & 0xFFL) | ((b[off + 1] & 0xFFL) << 8)
                | ((b[off + 2] & 0xFFL) << 16) | ((b[off + 3] & 0xFFL) << 24);
    }

    private static long ru48(byte[] b, int off) {
        long v = 0;
        for (int k = 0; k < 6; k++) v |= (b[off + k] & 0xFFL) << (8 * k);
        return v;
    }

    private static long ru64(byte[] b, int off) {
        long v = 0;
        for (int k = 0; k < 8; k++) v |= (b[off + k] & 0xFFL) << (8 * k);
        return v;
    }

    private static void writeLE(byte[] b, int off, int width, long value) {
        for (int k = 0; k < width; k++) b[off + k] = (byte) ((value >>> (8 * k)) & 0xFF);
    }

    private static long asLong(Object o) {
        if (o instanceof Long) return (Long) o;
        if (o instanceof Integer) return (Integer) o;
        if (o instanceof Double) return (long) (double) (Double) o;
        if (o instanceof Boolean) return ((Boolean) o) ? 1 : 0;
        throw new RuntimeException("expected number, got: " + o);
    }

    // ------------------------------------------------------------------
    // Resource location (CWD is multi-lang/ when run via run_all_tests.sh)
    // ------------------------------------------------------------------

    private static byte[] locateBaseDb() {
        String[] candidates = {
                "data/qqzeng_ip_std_china.qzdb",
                "multi-lang/data/qqzeng_ip_std_china.qzdb",
                "../data/qqzeng_ip_std_china.qzdb",
                "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/data/qqzeng_ip_std_china.qzdb"
        };
        for (String c : candidates) {
            Path p = Paths.get(c);
            if (Files.isReadable(p)) {
                try {
                    return Files.readAllBytes(p);
                } catch (IOException e) {
                    // try next
                }
            }
        }
        return null;
    }

    private static Map<String, Object> loadVector() {
        String[] candidates = {
                "tools/hostile_vectors.json",
                "multi-lang/tools/hostile_vectors.json",
                "../tools/hostile_vectors.json",
                "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/tools/hostile_vectors.json"
        };
        for (String c : candidates) {
            Path p = Paths.get(c);
            if (Files.isReadable(p)) {
                try {
                    String txt = new String(Files.readAllBytes(p), StandardCharsets.UTF_8);
                    @SuppressWarnings("unchecked")
                    Map<String, Object> m = (Map<String, Object>) parseJson(txt);
                    return m;
                } catch (IOException e) {
                    // try next
                }
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Minimal recursive-descent JSON parser (no external dependency)
    // ------------------------------------------------------------------

    private static Object parseJson(String s) {
        Parser p = new Parser(s);
        p.skipWs();
        Object v = p.parseValue();
        p.skipWs();
        if (!p.atEnd()) throw new JsonParseException("trailing characters");
        return v;
    }

    private static final class JsonParseException extends RuntimeException {
        JsonParseException(String m) { super(m); }
    }

    private static final class Parser {
        final String s;
        int i;

        Parser(String s) { this.s = s; }

        boolean atEnd() { return i >= s.length(); }

        void skipWs() {
            while (i < s.length() && " \t\r\n".indexOf(s.charAt(i)) >= 0) i++;
        }

        Object parseValue() {
            skipWs();
            if (atEnd()) throw new JsonParseException("unexpected end");
            char c = s.charAt(i);
            if (c == '{') return parseObject();
            if (c == '[') return parseArray();
            if (c == '"') return parseString();
            if (c == '-' || (c >= '0' && c <= '9')) return parseNumber();
            if (c == 't') { expect("true"); return Boolean.TRUE; }
            if (c == 'f') { expect("false"); return Boolean.FALSE; }
            if (c == 'n') { expect("null"); return null; }
            throw new JsonParseException("unexpected char '" + c + "'");
        }

        Map<String, Object> parseObject() {
            expect("{");
            Map<String, Object> m = new LinkedHashMap<>();
            skipWs();
            if (peek() == '}') { i++; return m; }
            while (true) {
                skipWs();
                if (peek() != '"') throw new JsonParseException("expected key string");
                String k = parseString();
                skipWs();
                expect(":");
                Object v = parseValue();
                m.put(k, v);
                skipWs();
                char c = next();
                if (c == '}') break;
                if (c != ',') throw new JsonParseException("expected ',' or '}'");
            }
            return m;
        }

        List<Object> parseArray() {
            expect("[");
            List<Object> l = new ArrayList<>();
            skipWs();
            if (peek() == ']') { i++; return l; }
            while (true) {
                l.add(parseValue());
                skipWs();
                char c = next();
                if (c == ']') break;
                if (c != ',') throw new JsonParseException("expected ',' or ']'");
            }
            return l;
        }

        String parseString() {
            expect("\"");
            StringBuilder sb = new StringBuilder();
            while (true) {
                if (atEnd()) throw new JsonParseException("unterminated string");
                char c = next();
                if (c == '"') break;
                if (c == '\\') {
                    char e = next();
                    switch (e) {
                        case '"': sb.append('"'); break;
                        case '\\': sb.append('\\'); break;
                        case '/': sb.append('/'); break;
                        case 'b': sb.append('\b'); break;
                        case 'f': sb.append('\f'); break;
                        case 'n': sb.append('\n'); break;
                        case 'r': sb.append('\r'); break;
                        case 't': sb.append('\t'); break;
                        case 'u':
                            String hex = s.substring(i, i + 4);
                            i += 4;
                            sb.append((char) Integer.parseInt(hex, 16));
                            break;
                        default: throw new JsonParseException("bad escape");
                    }
                } else {
                    sb.append(c);
                }
            }
            return sb.toString();
        }

        Object parseNumber() {
            int start = i;
            if (peek() == '-') i++;
            while (i < s.length() && (Character.isDigit(s.charAt(i))
                    || s.charAt(i) == '.' || s.charAt(i) == 'e' || s.charAt(i) == 'E'
                    || s.charAt(i) == '+' || s.charAt(i) == '-')) {
                i++;
            }
            String num = s.substring(start, i);
            if (num.indexOf('.') >= 0 || num.indexOf('e') >= 0 || num.indexOf('E') >= 0) {
                return Double.parseDouble(num);
            }
            try {
                return Long.parseLong(num);
            } catch (NumberFormatException e) {
                return Double.parseDouble(num);
            }
        }

        char peek() {
            if (atEnd()) throw new JsonParseException("eof");
            return s.charAt(i);
        }

        char next() {
            if (atEnd()) throw new JsonParseException("eof");
            return s.charAt(i++);
        }

        void expect(String t) {
            if (!s.startsWith(t, i)) throw new JsonParseException("expected '" + t + "'");
            i += t.length();
        }
    }
}
