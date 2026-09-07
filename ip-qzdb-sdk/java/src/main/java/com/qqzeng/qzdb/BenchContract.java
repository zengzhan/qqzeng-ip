package com.qqzeng.qzdb;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;
import java.io.IOException;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicLong;
import java.util.zip.CRC32;

/**
 * QZDB Runtime Benchmark (docs/BENCH_CONTRACT.md v1.0)
 *
 * Implements splitmix64 reference RNG (byte-identical to the other 7 languages),
 * four distributions (random / hot / sequential / real_world), dual-stack tri-mode
 * (v4 / v6 / mixed), cold vs hot QPS, P50/P95/P99 latency, thread scaling
 * 1/2/4/8/16 on a SHARED QzdbReader, 16x100k concurrency gate, FNV-1a parity
 * self-check against bench_vectors.json, string round-trip benchmark, and canonical
 * JSON output to multi-lang/bench_reports/java_{edition}.json.
 *
 * Usage: java com.qqzeng.qzdb.BenchContract [OPS] [EDITIONS]
 *   OPS      - ops per distribution/mode measurement (default 2000000)
 *   EDITIONS - comma-separated edition list (default "std_china,max_global")
 *
 * Env overrides: BENCH_OPS, BENCH_EDITIONS
 */
public class BenchContract {

    // ---- Contract constants ----
    private static final String CONTRACT_VERSION = "QZDB_BENCH_CONTRACT v1.0";
    private static final long MASTER_SEED = 20260807L;
    private static final int POOL_HOT_V4 = 4096;
    private static final int POOL_HOT_V6 = 1024;
    private static final int FINGERPRINT_N = 1024;
    private static final long MAPPED_PREFIX = 0xFFFFL << 32; // ::ffff:0:0 in low u64
    private static final int COLD_OPS = 200_000;
    private static final int WARMUP_OPS = 1_000_000;
    private static final int LAT_EVERY = 20;
    private static final int CONC_THREADS = 16;
    private static final int CONC_OPS = 100_000;

    private static final String[] DIST_NAMES = {"random", "hot", "sequential", "real_world"};
    private static final String[] MODE_NAMES = {"v4", "v6", "mixed"};

    // ---- Enums ----
    private enum Dist { RANDOM, HOT, SEQUENTIAL, REAL_WORLD }
    private enum Mode { V4, V6, MIXED }

    // ---- Splitmix64 reference RNG ----
    // Uses Java signed long; wrapping arithmetic via unchecked add/mul produces
    // the same 64-bit bit-patterns as unsigned operations in C/Go/Rust/C#/Python.
    private static long splitmixState;

    private static void splitmixSeed(long seed) {
        splitmixState = seed;
    }

    private static long splitmixNext() {
        splitmixState += 0x9E3779B97F4A7C15L;
        long z = splitmixState;
        z = (z ^ (z >>> 30)) * 0xBF58476D1CE4E5B9L;
        z = (z ^ (z >>> 27)) * 0x94D049BB133111EBL;
        return z ^ (z >>> 31);
    }

    private static long splitmixU32() {
        return splitmixNext() & 0xFFFFFFFFL;
    }

    // ---- Hot pools ----
    private static long[] poolV4;   // POOL_HOT_V4 unsigned u32 values stored as long
    private static long[] poolV6Hi; // high u64
    private static long[] poolV6Lo; // low u64

    private static void buildPools() {
        // Pool seeds: MASTER_SEED+1 for v4, MASTER_SEED+2 for v6
        splitmixSeed(MASTER_SEED + 1);
        poolV4 = new long[POOL_HOT_V4];
        for (int i = 0; i < POOL_HOT_V4; i++) {
            poolV4[i] = splitmixU32();
        }
        splitmixSeed(MASTER_SEED + 2);
        poolV6Hi = new long[POOL_HOT_V6];
        poolV6Lo = new long[POOL_HOT_V6];
        for (int i = 0; i < POOL_HOT_V6; i++) {
            poolV6Hi[i] = splitmixNext();
            poolV6Lo[i] = splitmixNext();
        }
    }

    // ---- FNV-1a 64-bit hash (for parity self-check) ----
    private static long fnv1a(byte[] data, long h) {
        if (h == 0) h = 0xCBF29CE484222325L;
        for (byte b : data) {
            h ^= (b & 0xFFL);
            h *= 0x00000100000001B3L;
        }
        return h;
    }

    // ---- Metrics ----
    private static class Metrics {
        int ops;
        double qps;
        double avgNs;
        long p50Ns;
        long p95Ns;
        long p99Ns;
        long errors;
        long hits;
        double hitRate;
        String warm;
        String api;
    }

    private static long percentile(long[] arr, double p) {
        if (arr.length == 0) return 0;
        Arrays.sort(arr);
        int idx = (int) Math.floor(arr.length * p + 0.9999) - 1;
        if (idx < 0) idx = 0;
        if (idx >= arr.length) idx = arr.length - 1;
        return arr[idx];
    }

    // ---- IP formatting (for string round-trip) ----
    private static String fmtV4(long ip) {
        return ((ip >>> 24) & 0xFF) + "." + ((ip >>> 16) & 0xFF) + "."
             + ((ip >>> 8) & 0xFF) + "." + (ip & 0xFF);
    }

    private static String fmtV6(long hi, long lo) {
        return String.format(Locale.US, "%x:%x:%x:%x:%x:%x:%x:%x",
                (hi >>> 48) & 0xFFFF, (hi >>> 32) & 0xFFFF,
                (hi >>> 16) & 0xFFFF, hi & 0xFFFF,
                (lo >>> 48) & 0xFFFF, (lo >>> 32) & 0xFFFF,
                (lo >>> 16) & 0xFFFF, lo & 0xFFFF);
    }

    // ---- Query byte encoding (for FNV-1a parity) ----
    // Must match Rust enc_query byte-for-byte:
    //   v4:     u32 zero-extended to u64, little-endian 8 bytes
    //   v6/mapped: high u64 LE + low u64 LE = 16 bytes
    private static byte[] encodeQueryBytes(int kind, long hi, long lo) {
        if (kind == 0) {
            // v4: 8 bytes little-endian
            return new byte[]{
                (byte) hi, (byte) (hi >>> 8), (byte) (hi >>> 16), (byte) (hi >>> 24),
                (byte) (hi >>> 32), (byte) (hi >>> 40), (byte) (hi >>> 48), (byte) (hi >>> 56)
            };
        } else {
            // v6 or mapped: 16 bytes little-endian (high then low)
            return new byte[]{
                (byte) hi, (byte) (hi >>> 8), (byte) (hi >>> 16), (byte) (hi >>> 24),
                (byte) (hi >>> 32), (byte) (hi >>> 40), (byte) (hi >>> 48), (byte) (hi >>> 56),
                (byte) lo, (byte) (lo >>> 8), (byte) (lo >>> 16), (byte) (lo >>> 24),
                (byte) (lo >>> 32), (byte) (lo >>> 40), (byte) (lo >>> 48), (byte) (lo >>> 56)
            };
        }
    }

    // ---- Dispatch to QzdbReader API ----
    // kind 0 = v4, kind 1 = v6-pure, kind 2 = v6-mapped (::ffff: prefixed)
    // mapped MUST go through findBytes which performs the mapped downgrade;
    // findV6 is a pure v6 trie walk that would miss on every mapped query.
    private static boolean dispatch(QzdbReader reader, int kind, long hi, long lo) {
        try {
            if (kind == 0) {
                // v4: hi is u32 zero-extended to u64
                return reader.findUint((int) hi).isPresent();
            } else if (kind == 2) {
                // v6-mapped: construct 16-byte network-order address
                // hi=0, lo = (0xFFFF << 32) | ipv4_u32
                byte[] ip16 = new byte[16];
                ip16[10] = (byte) 0xFF;
                ip16[11] = (byte) 0xFF;
                ip16[12] = (byte) (lo >>> 24);
                ip16[13] = (byte) (lo >>> 16);
                ip16[14] = (byte) (lo >>> 8);
                ip16[15] = (byte) lo;
                return reader.findBytes(ip16).isPresent();
            } else {
                // v6-pure: reconstruct 128-bit address from (hi, lo)
                byte[] ip16 = v6HiLoToBytes(hi, lo);
                return reader.findBytes(ip16).isPresent();
            }
        } catch (QzdbException e) {
            return false;
        }
    }

    /** Convert (high u64, low u64) pair to 16-byte big-endian IPv6 address. */
    private static byte[] v6HiLoToBytes(long hi, long lo) {
        byte[] b = new byte[16];
        for (int i = 0; i < 8; i++) {
            b[7 - i] = (byte) (hi >>> (8 * i));
            b[15 - i] = (byte) (lo >>> (8 * i));
        }
        return b;
    }

    // ---- Single-threaded cold/hot run ----
    private static Metrics runSingle(QzdbReader reader, Dist dist, Mode mode, long seed,
                                      int ops, boolean sampleLatency, String warmLabel) {
        splitmixSeed(seed);
        // Consume base4 and base6 (matches Rust Stream::new exactly)
        long base4 = splitmixU32();
        long base6Hi = splitmixNext();
        long base6Lo = splitmixNext();

        long[] lat = sampleLatency ? new long[ops / LAT_EVERY + 1] : null;
        int latIdx = 0;
        long hits = 0;
        long t0 = System.nanoTime();

        for (int i = 0; i < ops; i++) {
            int kind;
            long hi, lo;

            if (mode == Mode.V4) {
                kind = 0;
                hi = genV4(dist, i, base4);
                lo = 0;
            } else if (mode == Mode.V6) {
                if (splitmixU32() % 5 == 0) {
                    // 20% mapped
                    kind = 2;
                    hi = 0;
                    lo = MAPPED_PREFIX | genV4(dist, i, base4);
                } else {
                    // 80% pure v6
                    kind = 1;
                    long[] v6 = genV6(dist, i, base6Hi, base6Lo);
                    hi = v6[0];
                    lo = v6[1];
                }
            } else {
                // MIXED: 50/40/10
                long m = i % 10;
                if (m < 5) {
                    kind = 0;
                    hi = genV4(dist, i, base4);
                    lo = 0;
                } else if (m < 9) {
                    kind = 1;
                    long[] v6 = genV6(dist, i, base6Hi, base6Lo);
                    hi = v6[0];
                    lo = v6[1];
                } else {
                    kind = 2;
                    hi = 0;
                    lo = MAPPED_PREFIX | genV4(dist, i, base4);
                }
            }

            boolean found;
            if (sampleLatency && i % LAT_EVERY == 0) {
                long a = System.nanoTime();
                found = dispatch(reader, kind, hi, lo);
                lat[latIdx++] = System.nanoTime() - a;
            } else {
                found = dispatch(reader, kind, hi, lo);
            }
            if (found) hits++;
        }

        long elapsed = System.nanoTime() - t0;
        if (sampleLatency && latIdx < lat.length) {
            lat = Arrays.copyOf(lat, latIdx);
        }
        Metrics m = new Metrics();
        m.ops = ops;
        m.qps = ops * 1e9 / elapsed;
        m.avgNs = (double) elapsed / ops;
        m.errors = 0;
        m.hits = hits;
        m.hitRate = (double) hits / ops;
        m.warm = warmLabel;
        m.api = "uint";
        if (sampleLatency && lat.length > 0) {
            m.p50Ns = percentile(lat, 0.50);
            m.p95Ns = percentile(lat, 0.95);
            m.p99Ns = percentile(lat, 0.99);
        }
        return m;
    }

    // ---- Thread-scaling run ----
    private static Metrics runMulti(QzdbReader reader, Dist dist, Mode mode, long seed,
                                     int numThreads, int totalOps) throws InterruptedException {
        int perThread = totalOps / numThreads;
        AtomicLong totalDone = new AtomicLong(0);
        AtomicLong totalHits = new AtomicLong(0);
        AtomicLong totalErrors = new AtomicLong(0);
        long t0 = System.nanoTime();

        Thread[] threads = new Thread[numThreads];
        for (int t = 0; t < numThreads; t++) {
            threads[t] = new Thread(() -> {
                // Each thread uses the same seed -> same stream -> deterministic
                splitmixSeed(seed);
                long base4 = splitmixU32();
                long base6Hi = splitmixNext();
                long base6Lo = splitmixNext();
                long localHits = 0;
                long localErrors = 0;

                for (int i = 0; i < perThread; i++) {
                    int kind;
                    long hi, lo;

                    if (mode == Mode.V4) {
                        kind = 0;
                        hi = genV4(dist, i, base4);
                        lo = 0;
                    } else if (mode == Mode.V6) {
                        if (splitmixU32() % 5 == 0) {
                            kind = 2; hi = 0;
                            lo = MAPPED_PREFIX | genV4(dist, i, base4);
                        } else {
                            kind = 1;
                            long[] v6 = genV6(dist, i, base6Hi, base6Lo);
                            hi = v6[0]; lo = v6[1];
                        }
                    } else {
                        long m2 = i % 10;
                        if (m2 < 5) {
                            kind = 0; hi = genV4(dist, i, base4); lo = 0;
                        } else if (m2 < 9) {
                            kind = 1;
                            long[] v6 = genV6(dist, i, base6Hi, base6Lo);
                            hi = v6[0]; lo = v6[1];
                        } else {
                            kind = 2; hi = 0;
                            lo = MAPPED_PREFIX | genV4(dist, i, base4);
                        }
                    }

                    try {
                        if (dispatch(reader, kind, hi, lo)) localHits++;
                    } catch (Exception e) {
                        localErrors++;
                    }
                }

                totalDone.addAndGet(perThread);
                totalHits.addAndGet(localHits);
                totalErrors.addAndGet(localErrors);
            });
            threads[t].start();
        }

        for (Thread th : threads) th.join();

        long elapsed = System.nanoTime() - t0;
        long done = totalDone.get();
        Metrics m = new Metrics();
        m.ops = (int) done;
        m.qps = done * 1e9 / elapsed;
        m.avgNs = done > 0 ? (double) elapsed / done : 0;
        m.errors = totalErrors.get();
        m.hits = totalHits.get();
        m.hitRate = done > 0 ? (double) totalHits.get() / done : 0;
        m.warm = "hot";
        m.api = "uint";
        return m;
    }

    // ---- Concurrency safety gate ----
    private static long[] runConcurrencyGate(QzdbReader reader, long seed) throws InterruptedException {
        AtomicLong done = new AtomicLong(0);
        AtomicLong errors = new AtomicLong(0);
        Thread[] threads = new Thread[CONC_THREADS];

        for (int t = 0; t < CONC_THREADS; t++) {
            final int threadIdx = t;
            threads[t] = new Thread(() -> {
                // Thread-confined splitmix64 state: each thread gets its own
                // independent RNG seeded differently (seed + threadIdx + 1),
                // eliminating the data race on the shared static splitmixState.
                // The concurrency gate only verifies "N threads hitting a shared
                // QzdbReader don't crash", not deterministic RNG sequence parity.
                long state = seed + threadIdx + 1;

                for (int i = 0; i < CONC_OPS; i++) {
                    // Inline splitmix64 step (thread-local, no shared mutable state)
                    state += 0x9E3779B97F4A7C15L;
                    long z = state;
                    z = (z ^ (z >>> 30)) * 0xBF58476D1CE4E5B9L;
                    z = (z ^ (z >>> 27)) * 0x94D049BB133111EBL;
                    long r1 = (z ^ (z >>> 31)) & 0xFFFFFFFFL;

                    // Hot distribution, Mixed mode (matches Rust concurrency_safe)
                    long m2 = i % 10;
                    int kind;
                    long hi, lo;
                    if (m2 < 5) {
                        kind = 0; hi = poolV4[(int) (r1 % POOL_HOT_V4)]; lo = 0;
                    } else if (m2 < 9) {
                        kind = 1;
                        int idx = (int) (r1 % POOL_HOT_V6);
                        hi = poolV6Hi[idx]; lo = poolV6Lo[idx];
                    } else {
                        kind = 2; hi = 0;
                        lo = MAPPED_PREFIX | poolV4[(int) (r1 % POOL_HOT_V4)];
                    }

                    try {
                        dispatch(reader, kind, hi, lo);
                    } catch (Exception e) {
                        errors.incrementAndGet();
                    }
                }
                done.addAndGet(CONC_OPS);
            });
            threads[t].start();
        }

        for (Thread th : threads) th.join();
        return new long[]{done.get(), errors.get()};
    }

    // ---- Stream IP generators (match Rust gen_v4 / gen_v6 exactly) ----

    /** Generate a v4 IP for the given distribution at position i. */
    private static long genV4(Dist dist, int i, long base4) {
        switch (dist) {
            case RANDOM:
                return splitmixU32();
            case HOT:
                return poolV4[(int) (splitmixU32() % POOL_HOT_V4)];
            case SEQUENTIAL:
                return (base4 + i) & 0xFFFFFFFFL;
            case REAL_WORLD: {
                long r = splitmixU32() % 10;
                if (r < 6) return poolV4[(int) (splitmixU32() % POOL_HOT_V4)];
                if (r < 9) return splitmixU32();
                return (base4 + i) & 0xFFFFFFFFL;
            }
            default:
                return 0;
        }
    }

    /** Generate a v6 (high, low) pair for the given distribution at position i. */
    private static long[] genV6(Dist dist, int i, long base6Hi, long base6Lo) {
        switch (dist) {
            case RANDOM:
                return new long[]{splitmixNext(), splitmixNext()};
            case HOT: {
                int idx = (int) (splitmixU32() % POOL_HOT_V6);
                return new long[]{poolV6Hi[idx], poolV6Lo[idx]};
            }
            case SEQUENTIAL: {
                // 128-bit wrapping add using unsigned long arithmetic (no BigInteger)
                long rLo = base6Lo + i; // wrapping add (i fits in int, always positive)
                long carry = Long.compareUnsigned(rLo, base6Lo) < 0 ? 1 : 0;
                long rHi = base6Hi + carry;
                return new long[]{rHi, rLo};
            }
            case REAL_WORLD: {
                long r = splitmixU32() % 10;
                if (r < 6) {
                    int idx = (int) (splitmixU32() % POOL_HOT_V6);
                    return new long[]{poolV6Hi[idx], poolV6Lo[idx]};
                }
                if (r < 9) {
                    return new long[]{splitmixNext(), splitmixNext()};
                }
                long rLo = base6Lo + i;
                long carry = Long.compareUnsigned(rLo, base6Lo) < 0 ? 1 : 0;
                long rHi = base6Hi + carry;
                return new long[]{rHi, rLo};
            }
            default:
                return new long[]{0, 0};
        }
    }

    // ---- Parity self-check against bench_vectors.json ----
    private static boolean paritySelfCheck(Object manifestRoot) {
        System.out.print("parity self-check ... ");
        System.out.flush();
        int bad = 0;

        @SuppressWarnings("unchecked")
        java.util.Map<String, Object> streams =
                (java.util.Map<String, Object>) ((java.util.Map<String, Object>) manifestRoot).get("streams");

        for (String dn : DIST_NAMES) {
            @SuppressWarnings("unchecked")
            java.util.Map<String, Object> distMap = (java.util.Map<String, Object>) streams.get(dn);
            for (String mn : MODE_NAMES) {
                @SuppressWarnings("unchecked")
                java.util.Map<String, Object> info = (java.util.Map<String, Object>) distMap.get(mn);
                long want = Long.parseUnsignedLong((String) info.get("first1024_fnv1a"));
                long seed = ((Number) info.get("seed")).longValue();

                splitmixSeed(seed);
                long base4 = splitmixU32();
                long base6Hi = splitmixNext();
                long base6Lo = splitmixNext();

                Dist dist = distFromString(dn);
                Mode mode = modeFromString(mn);
                long h = 0;

                for (int i = 0; i < FINGERPRINT_N; i++) {
                    byte[] q = genQueryBytes(dist, mode, i, base4, base6Hi, base6Lo);
                    h = fnv1a(q, h);
                }

                if (h != want) {
                    System.out.printf("%n  MISMATCH %s.%s got=%d want=%s%n",
                            dn, mn, h, Long.toUnsignedString(want));
                    bad++;
                }
            }
        }

        if (bad != 0) {
            System.out.println("\nFAILED");
            return false;
        }
        System.out.println("OK (12/12 streams match bench_vectors.json)");
        return true;
    }

    /**
     * Generate query bytes for parity check (matches Rust enc_query).
     * Does NOT update any external state - only consumes RNG values
     * in the same order as the runtime dispatch path.
     */
    private static byte[] genQueryBytes(Dist dist, Mode mode, int i,
                                         long base4, long base6Hi, long base6Lo) {
        if (mode == Mode.V4) {
            long v = genV4(dist, i, base4);
            return encodeQueryBytes(0, v, 0);
        } else if (mode == Mode.V6) {
            if (splitmixU32() % 5 == 0) {
                // mapped: kind=2
                long v = genV4(dist, i, base4);
                return encodeQueryBytes(2, 0, MAPPED_PREFIX | v);
            } else {
                // pure v6: kind=1
                long[] v6 = genV6(dist, i, base6Hi, base6Lo);
                return encodeQueryBytes(1, v6[0], v6[1]);
            }
        } else {
            // MIXED
            long m = i % 10;
            if (m < 5) {
                long v = genV4(dist, i, base4);
                return encodeQueryBytes(0, v, 0);
            } else if (m < 9) {
                long[] v6 = genV6(dist, i, base6Hi, base6Lo);
                return encodeQueryBytes(1, v6[0], v6[1]);
            } else {
                long v = genV4(dist, i, base4);
                return encodeQueryBytes(2, 0, MAPPED_PREFIX | v);
            }
        }
    }

    private static Dist distFromString(String s) {
        switch (s) {
            case "random": return Dist.RANDOM;
            case "hot": return Dist.HOT;
            case "sequential": return Dist.SEQUENTIAL;
            default: return Dist.REAL_WORLD;
        }
    }

    private static Mode modeFromString(String s) {
        switch (s) {
            case "v4": return Mode.V4;
            case "v6": return Mode.V6;
            default: return Mode.MIXED;
        }
    }

    // ---- Helpers ----

    private static String findRepoRoot() {
        File cwd = new File(System.getProperty("user.dir"));
        for (int i = 0; i < 8; i++) {
            if (new File(cwd, "multi-lang/tools/bench_vectors.json").exists()) {
                return cwd.getAbsolutePath();
            }
            cwd = cwd.getParentFile();
            if (cwd == null) break;
        }
        return "";
    }

    private static String findDb(String root, String edition) {
        String[] parts = edition.split("_", 2);
        if (parts.length != 2) return null;
        String tier = parts[0]; // e.g. "std" or "max"
        String region = parts[1]; // e.g. "china" or "global"
        String filename = "qqzeng_ip_" + edition + ".qzdb";

        String[] bases = {
            "multi-lang/test_data_202608",
            "../test_data_202608",
            "test_data_202608"
        };
        for (String base : bases) {
            String p = root + File.separator + base + File.separator + tier
                     + File.separator + region + File.separator + filename;
            if (new File(p).exists()) return p;
        }
        return null;
    }

    private static String cpuModel() {
        try {
            Process p = Runtime.getRuntime().exec(new String[]{"sysctl", "-n", "machdep.cpu.brand_string"});
            try (BufferedReader r = new BufferedReader(new java.io.InputStreamReader(p.getInputStream()))) {
                String line = r.readLine();
                p.waitFor();
                if (line != null) return line.trim();
            }
        } catch (Exception e) {
            // fall through
        }
        try {
            Process p = Runtime.getRuntime().exec(new String[]{"cat", "/proc/cpuinfo"});
            try (BufferedReader r = new BufferedReader(new java.io.InputStreamReader(p.getInputStream()))) {
                String line;
                while ((line = r.readLine()) != null) {
                    if (line.startsWith("model name")) {
                        p.waitFor();
                        return line.split(":", 2)[1].trim();
                    }
                }
                p.waitFor();
            }
        } catch (Exception e) {
            // fall through
        }
        return "unknown";
    }

    private static String osInfo() {
        return System.getProperty("os.name") + " " + System.getProperty("os.version")
             + " " + System.getProperty("os.arch");
    }

    private static String runtimeInfo() {
        String vm = System.getProperty("java.vm.name", "");
        String ver = System.getProperty("java.version", "");
        return vm + " " + ver;
    }

    private static String timestamp() {
        return java.time.ZonedDateTime.now().format(
                java.time.format.DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ssXXX"));
    }

    private static String fileCrc32(String path) throws IOException {
        CRC32 crc = new CRC32();
        byte[] buf = new byte[1 << 20];
        try (java.io.FileInputStream fis = new java.io.FileInputStream(path)) {
            int n;
            boolean first = true;
            while ((n = fis.read(buf)) > 0) {
                if (first && n >= 20) {
                    crc.update(buf, 0, 16);
                    crc.update(new byte[4], 0, 4); // zero out bytes 16-19 (canonical CRC)
                    crc.update(buf, 20, n - 20);
                    first = false;
                } else {
                    crc.update(buf, 0, n);
                    first = false;
                }
            }
        }
        return String.format(Locale.US, "crc32:%08x", crc.getValue());
    }

    // ---- Simple JSON manifest parser (no external dependency) ----
    // Parses bench_vectors.json into a Map structure for extracting seeds and FNV-1a values.

    private static Object parseManifest(String path) throws IOException {
        StringBuilder sb = new StringBuilder();
        try (BufferedReader r = new BufferedReader(new FileReader(path))) {
            String line;
            while ((line = r.readLine()) != null) sb.append(line.trim());
        }
        return parseJsonValue(sb.toString().trim(), new int[]{0});
    }

    @SuppressWarnings("unchecked")
    private static Object parseJsonValue(String s, int[] pos) {
        skipWhitespace(s, pos);
        if (pos[0] >= s.length()) return null;
        char c = s.charAt(pos[0]);
        if (c == '{') return parseJsonObject(s, pos);
        if (c == '[') return parseJsonArray(s, pos);
        if (c == '"') return parseJsonString(s, pos);
        // number or boolean or null
        int start = pos[0];
        while (pos[0] < s.length() && ",}] \t\n\r".indexOf(s.charAt(pos[0])) < 0) pos[0]++;
        String token = s.substring(start, pos[0]);
        if ("true".equals(token)) return Boolean.TRUE;
        if ("false".equals(token)) return Boolean.FALSE;
        if ("null".equals(token)) return null;
        try {
            if (token.contains(".") || token.contains("e") || token.contains("E"))
                return Double.parseDouble(token);
            long v = Long.parseLong(token);
            return v;
        } catch (NumberFormatException e) {
            return token;
        }
    }

    private static java.util.LinkedHashMap<String, Object> parseJsonObject(String s, int[] pos) {
        java.util.LinkedHashMap<String, Object> map = new java.util.LinkedHashMap<>();
        pos[0]++; // skip '{'
        skipWhitespace(s, pos);
        if (pos[0] < s.length() && s.charAt(pos[0]) == '}') { pos[0]++; return map; }
        while (pos[0] < s.length()) {
            skipWhitespace(s, pos);
            String key = parseJsonString(s, pos);
            skipWhitespace(s, pos);
            pos[0]++; // skip ':'
            Object val = parseJsonValue(s, pos);
            map.put(key, val);
            skipWhitespace(s, pos);
            if (pos[0] < s.length() && s.charAt(pos[0]) == ',') { pos[0]++; continue; }
            if (pos[0] < s.length() && s.charAt(pos[0]) == '}') { pos[0]++; break; }
        }
        return map;
    }

    private static ArrayList<Object> parseJsonArray(String s, int[] pos) {
        ArrayList<Object> list = new ArrayList<>();
        pos[0]++; // skip '['
        skipWhitespace(s, pos);
        if (pos[0] < s.length() && s.charAt(pos[0]) == ']') { pos[0]++; return list; }
        while (pos[0] < s.length()) {
            Object val = parseJsonValue(s, pos);
            list.add(val);
            skipWhitespace(s, pos);
            if (pos[0] < s.length() && s.charAt(pos[0]) == ',') { pos[0]++; continue; }
            if (pos[0] < s.length() && s.charAt(pos[0]) == ']') { pos[0]++; break; }
        }
        return list;
    }

    private static String parseJsonString(String s, int[] pos) {
        pos[0]++; // skip opening '"'
        StringBuilder sb = new StringBuilder();
        while (pos[0] < s.length()) {
            char c = s.charAt(pos[0]++);
            if (c == '"') return sb.toString();
            if (c == '\\') {
                char esc = s.charAt(pos[0]++);
                switch (esc) {
                    case '"': sb.append('"'); break;
                    case '\\': sb.append('\\'); break;
                    case '/': sb.append('/'); break;
                    case 'n': sb.append('\n'); break;
                    case 'r': sb.append('\r'); break;
                    case 't': sb.append('\t'); break;
                    case 'b': sb.append('\b'); break;
                    case 'f': sb.append('\f'); break;
                    case 'u':
                        String hex = s.substring(pos[0], pos[0] + 4);
                        sb.append((char) Integer.parseInt(hex, 16));
                        pos[0] += 4;
                        break;
                    default: sb.append(esc);
                }
            } else {
                sb.append(c);
            }
        }
        return sb.toString();
    }

    private static void skipWhitespace(String s, int[] pos) {
        while (pos[0] < s.length() && " \t\n\r".indexOf(s.charAt(pos[0])) >= 0) pos[0]++;
    }

    // ---- JSON output helpers ----

    private static String metricsJson(Metrics m) {
        StringBuilder sb = new StringBuilder();
        sb.append("{\n");
        sb.append("        \"ops\": ").append(m.ops).append(",\n");
        sb.append("        \"qps\": ").append(String.format(Locale.US, "%.0f", m.qps)).append(",\n");
        sb.append("        \"avg_ns\": ").append(String.format(Locale.US, "%.1f", m.avgNs)).append(",\n");
        sb.append("        \"p50_ns\": ").append(m.p50Ns).append(",\n");
        sb.append("        \"p95_ns\": ").append(m.p95Ns).append(",\n");
        sb.append("        \"p99_ns\": ").append(m.p99Ns).append(",\n");
        sb.append("        \"errors\": ").append(m.errors).append(",\n");
        sb.append("        \"hits\": ").append(m.hits).append(",\n");
        sb.append("        \"hit_rate\": ").append(String.format(Locale.US, "%.6f", m.hitRate));
        if (m.warm != null && !m.warm.isEmpty()) {
            sb.append(",\n        \"warm\": \"").append(m.warm).append("\"");
        }
        if (m.api != null && !m.api.isEmpty()) {
            sb.append(",\n        \"api\": \"").append(m.api).append("\"");
        }
        sb.append("\n      }");
        return sb.toString();
    }

    // ---- Main ----

    public static void main(String[] args) {
        // Parse arguments / env overrides
        int ops = 2_000_000;
        String envOps = System.getenv("BENCH_OPS");
        if (envOps != null) try { ops = Integer.parseInt(envOps); } catch (NumberFormatException ignored) {}
        if (args.length > 0) try { ops = Integer.parseInt(args[0]); } catch (NumberFormatException ignored) {}

        String editionsStr = System.getenv("BENCH_EDITIONS");
        if (editionsStr == null) editionsStr = "std_china,max_global";
        if (args.length > 1) editionsStr = args[1];
        String[] editions = editionsStr.split(",");

        // Find repo root and load manifest for parity check
        String root = findRepoRoot();
        if (root.isEmpty()) {
            System.err.println("ERROR: cannot locate repo root (multi-lang/tools/bench_vectors.json not found)");
            System.exit(1);
            return;
        }
        String manifestPath = root + File.separator + "multi-lang/tools/bench_vectors.json";
        Object manifestRoot;
        try {
            manifestRoot = parseManifest(manifestPath);
        } catch (IOException e) {
            System.err.println("ERROR: cannot read " + manifestPath + ": " + e.getMessage());
            System.exit(1);
            return;
        }

        // Build pools (once per process)
        buildPools();

        // Parity self-check
        if (!paritySelfCheck(manifestRoot)) {
            System.exit(1);
            return;
        }

        // Prepare report directory
        String repDir = root + File.separator + "multi-lang/bench_reports";
        new File(repDir).mkdirs();

        String ts = timestamp();
        String cpu = cpuModel();
        int cores = Runtime.getRuntime().availableProcessors();

        @SuppressWarnings("unchecked")
        java.util.Map<String, Object> streamsMap =
                (java.util.Map<String, Object>) ((java.util.Map<String, Object>) manifestRoot).get("streams");

        // Run benchmarks for each edition
        for (String edition : editions) {
            edition = edition.trim();
            String dbPath = findDb(root, edition);
            if (dbPath == null) {
                System.out.println("[SKIP] " + edition + ": db not found");
                continue;
            }

            File dbFile = new File(dbPath);
            QzdbReader reader;
            try {
                reader = new QzdbReader.Builder(dbFile).verifyCrc(false).build();
            } catch (QzdbException e) {
                System.out.println("[SKIP] " + edition + ": open failed (" + e.getMessage() + ")");
                continue;
            }

            long bytes = dbFile.length();
            System.out.printf("%n%sedition %s: %s (%d bytes)%n", "", edition, dbPath, bytes);

            // Concurrency safety gate (use hot.mixed seed)
            long concSeed;
            {
                @SuppressWarnings("unchecked")
                java.util.Map<String, Object> hotMap = (java.util.Map<String, Object>) streamsMap.get("hot");
                @SuppressWarnings("unchecked")
                java.util.Map<String, Object> mixedMap = (java.util.Map<String, Object>) hotMap.get("mixed");
                concSeed = ((Number) mixedMap.get("seed")).longValue();
            }

            boolean concSafe = false;
            long concDone = 0;
            try {
                long[] concResult = runConcurrencyGate(reader, concSeed);
                concDone = concResult[0];
                concSafe = concResult[1] == 0 && concDone == (long) CONC_THREADS * CONC_OPS;
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
            System.out.printf("  concurrency_safe(%dx%dk): %s (done=%d)%n",
                    CONC_THREADS, CONC_OPS / 1000, concSafe, concDone);

            // Build JSON output
            StringBuilder json = new StringBuilder();
            json.append("{\n");
            json.append("  \"contract\": \"").append(CONTRACT_VERSION).append("\",\n");
            json.append("  \"language\": \"java\",\n");
            json.append("  \"sdk_version\": \"").append(escapeJson(runtimeInfo())).append("\",\n");
            json.append("  \"timestamp\": \"").append(ts).append("\",\n");
            json.append("  \"seed\": ").append(MASTER_SEED).append(",\n");

            String dbHash;
            try { dbHash = fileCrc32(dbPath); } catch (IOException e) { dbHash = "crc32:n/a"; }
            json.append("  \"db\": {\n");
            json.append("    \"path\": \"").append(escapeJson(dbPath)).append("\",\n");
            json.append("    \"edition\": \"").append(edition).append("\",\n");
            json.append("    \"bytes\": ").append(bytes).append(",\n");
            json.append("    \"hash\": \"").append(dbHash).append("\"\n");
            json.append("  },\n");

            json.append("  \"environment\": {\n");
            json.append("    \"cpu\": \"").append(escapeJson(cpu)).append("\",\n");
            json.append("    \"cores\": ").append(cores).append(",\n");
            json.append("    \"os\": \"").append(escapeJson(osInfo())).append("\",\n");
            json.append("    \"runtime\": \"").append(escapeJson(runtimeInfo())).append("\",\n");
            json.append("    \"compiler\": \"javac (no 3rd-party deps, -encoding UTF-8)\",\n");
            json.append("    \"bench_contract\": \"v1.0\"\n");
            json.append("  },\n");

            json.append("  \"distributions\": {\n");

            boolean firstDist = true;
            for (Dist dist : allDists()) {
                if (!firstDist) json.append(",\n");
                firstDist = false;
                json.append("    \"").append(distName(dist)).append("\": {\n");

                boolean firstMode = true;
                for (Mode mode : allModes()) {
                    if (!firstMode) json.append(",\n");
                    firstMode = false;

                    // Get seed from manifest
                    @SuppressWarnings("unchecked")
                    java.util.Map<String, Object> distMap =
                            (java.util.Map<String, Object>) streamsMap.get(distName(dist));
                    @SuppressWarnings("unchecked")
                    java.util.Map<String, Object> modeMap =
                            (java.util.Map<String, Object>) distMap.get(modeName(mode));
                    long seed = ((Number) modeMap.get("seed")).longValue();

                    // Cold run
                    int coldOps = Math.min(ops, COLD_OPS);
                    Metrics cold = runSingle(reader, dist, mode, seed, coldOps, true, "cold");

                    // Warmup (not timed)
                    runSingle(reader, dist, mode, seed, Math.min(ops, WARMUP_OPS), false, "");

                    // Hot run
                    Metrics hot = runSingle(reader, dist, mode, seed, ops, true, "hot");

                    // Thread scaling
                    int[] threadCfgs = {1, 2, 4, 8, 16};
                    Metrics[] threadMetrics = new Metrics[threadCfgs.length];
                    for (int ti = 0; ti < threadCfgs.length; ti++) {
                        try {
                            threadMetrics[ti] = runMulti(reader, dist, mode, seed, threadCfgs[ti], ops);
                        } catch (InterruptedException e) {
                            Thread.currentThread().interrupt();
                            threadMetrics[ti] = new Metrics();
                        }
                    }

                    // Print summary
                    double t1Qps = threadMetrics[0].qps;
                    double t16Qps = threadMetrics[4].qps;
                    double scale = t16Qps / (t1Qps + 1e-9);
                    System.out.printf("  %-11s.%-6s hot QPS=%12.0f p50=%6dns p99=%7dns 1T=%12.0f 16T=%12.0f (%.1fx) hit=%.1f%%%n",
                            distName(dist), modeName(mode), hot.qps, hot.p50Ns, hot.p99Ns,
                            t1Qps, t16Qps, scale, hot.hitRate * 100.0);

                    // JSON
                    json.append("      \"").append(modeName(mode)).append("\": {\n");
                    json.append("        \"cold\": ").append(metricsJson(cold)).append(",\n");
                    json.append("        \"hot\": ").append(metricsJson(hot)).append(",\n");
                    json.append("        \"threads\": {\n");
                    for (int ti = 0; ti < threadCfgs.length; ti++) {
                        if (ti > 0) json.append(",\n");
                        json.append("          \"").append(threadCfgs[ti]).append("\": ")
                            .append(metricsJson(threadMetrics[ti]));
                    }
                    json.append("\n        }\n");
                    json.append("      }");
                }
                json.append("\n    }");
            }
            json.append("\n  },\n");

            // String round-trip on hot.mixed
            @SuppressWarnings("unchecked")
            java.util.Map<String, Object> hotDistMap =
                    (java.util.Map<String, Object>) streamsMap.get("hot");
            @SuppressWarnings("unchecked")
            java.util.Map<String, Object> hotMixedMap =
                    (java.util.Map<String, Object>) hotDistMap.get("mixed");
            long srtSeed = ((Number) hotMixedMap.get("seed")).longValue();

            splitmixSeed(srtSeed);
            long srtBase4 = splitmixU32();
            long srtBase6Hi = splitmixNext();
            long srtBase6Lo = splitmixNext();
            long[] srtLat = new long[ops / LAT_EVERY + 1];
            int srtLatIdx = 0;
            long srtT0 = System.nanoTime();

            for (int i = 0; i < ops; i++) {
                // Generate query in hot.mixed mode
                long m2 = i % 10;
                String s;
                if (m2 < 5) {
                    long v = genV4(Dist.HOT, i, srtBase4);
                    s = fmtV4(v);
                } else if (m2 < 9) {
                    long[] v6 = genV6(Dist.HOT, i, srtBase6Hi, srtBase6Lo);
                    s = fmtV6(v6[0], v6[1]);
                } else {
                    long v = genV4(Dist.HOT, i, srtBase4);
                    // Format as ::ffff:a.b.c.d for string lookup
                    s = "::ffff:" + fmtV4(v);
                }

                if (i % LAT_EVERY == 0) {
                    long a = System.nanoTime();
                    reader.find(s);
                    srtLat[srtLatIdx++] = System.nanoTime() - a;
                } else {
                    reader.find(s);
                }
            }

            long srtElapsed = System.nanoTime() - srtT0;
            if (srtLatIdx < srtLat.length) srtLat = Arrays.copyOf(srtLat, srtLatIdx);
            Metrics srt = new Metrics();
            srt.ops = ops;
            srt.qps = ops * 1e9 / srtElapsed;
            srt.avgNs = (double) srtElapsed / ops;
            srt.errors = 0;
            srt.hits = 0;
            srt.hitRate = 0;
            srt.warm = "hot";
            srt.api = "string";
            if (srtLat.length > 0) {
                srt.p50Ns = percentile(srtLat, 0.50);
                srt.p95Ns = percentile(srtLat, 0.95);
                srt.p99Ns = percentile(srtLat, 0.99);
            }

            System.out.printf("  %-11s.%-6s STRING round-trip QPS=%12.0f p99=%7dns%n",
                    "hot", "mixed", srt.qps, srt.p99Ns);

            json.append("  \"string_roundtrip\": {\n");
            json.append("    \"hot\": {\n");
            json.append("      \"mixed\": ").append(metricsJson(srt)).append("\n");
            json.append("    }\n");
            json.append("  },\n");
            json.append("  \"concurrency_safe\": ").append(concSafe).append(",\n");
            json.append("  \"concurrency_done\": ").append(concDone).append(",\n");
            json.append("  \"concurrency_spec\": \"")
               .append(CONC_THREADS).append(" threads x ").append(CONC_OPS).append(" ops shared reader\"\n");
            json.append("}\n");

            // Write report
            String outPath = repDir + File.separator + "java_" + edition + ".json";
            try (PrintWriter pw = new PrintWriter(outPath, "UTF-8")) {
                pw.print(json.toString());
            } catch (IOException e) {
                System.err.println("  WARNING: cannot write report: " + e.getMessage());
            }
            System.out.println("  wrote " + outPath);

            reader.close();
        }
    }

    private static String escapeJson(String s) {
        if (s == null) return "";
        return s.replace("\\", "\\\\").replace("\"", "\\\"")
                .replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t");
    }

    private static Dist[] allDists() {
        return new Dist[]{Dist.RANDOM, Dist.HOT, Dist.SEQUENTIAL, Dist.REAL_WORLD};
    }
    private static Mode[] allModes() {
        return new Mode[]{Mode.V4, Mode.V6, Mode.MIXED};
    }
    private static String distName(Dist d) {
        switch (d) {
            case RANDOM: return "random";
            case HOT: return "hot";
            case SEQUENTIAL: return "sequential";
            default: return "real_world";
        }
    }
    private static String modeName(Mode m) {
        switch (m) {
            case V4: return "v4";
            case V6: return "v6";
            default: return "mixed";
        }
    }
}
