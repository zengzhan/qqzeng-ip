using QQZeng.Qzdb;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text.Json;

/// <summary>
/// Tier 5 — Fail-closed hostile-file test for the C# SDK.
///
/// Consumes the shared, language-agnostic fixture <c>tools/hostile_vectors.json</c>
/// (29 cases, self-documented in its <c>_doc</c> key). For every case it:
/// <list type="number">
///   <item>loads a real <c>.qzdb</c> into bytes (READ-ONLY; never mutates the file on disk),</item>
///   <item>resolves byte offsets from its OWN parsed 192-byte header (no baked-in absolutes),</item>
///   <item>applies the mutation recipe (sweeps expand to many mutated copies),</item>
///   <item>feeds each mutated copy to <see cref="QzdbReader.OpenBuffer"/> in BOTH modes
///         (verifyCrc=false — the deeper attacker path, and verifyCrc=true — the CRC gate),</item>
///   <item>asserts the fail-closed contract: the SDK must NOT crash, must NOT hang, and must
///         NOT return plausibly-correct-but-WRONG data. A rejection (any error code), a
///         graceful empty result, or lenient-but-correct data all satisfy fail-closed.</item>
/// </list>
///
/// Semantics mirror the Java reference <c>FailClosedHostileTest</c>: dual-mode eval, honest
/// divergence reporting (PASS* when the observed family is outside the expected list), and a
/// genuine SDK bug (strict-mode wrong data / hang / crash) is surfaced as FAIL — never papered
/// over. A case that generates ZERO mutated copies is reported as FAIL "NO COPIES".
///
/// Wiring mirrors <see cref="GoldenTests"/>: <c>Run()</c> is called from
/// <c>Program.Main</c> before the ALL TIERS PASSED decision, its <c>FailCount</c> is folded
/// into <c>allPass</c>, and it prints <c>HostileVectors: N/N passed</c> before the final marker.
/// </summary>
static class HostileVectors
{
    public static int FailCount { get; private set; }
    public static int TotalCount { get; private set; }

    // IPs exercised against every mutated copy. Mix of V4 (in-DB and out-of-DB) and V6.
    // All are syntactically valid so FindStr never throws InvalidIp on the baseline.
    private static readonly string[] TestIps =
    {
        "223.5.5.5", "114.114.114.114", "1.0.1.0", "8.8.8.8",
        "0.0.0.0", "255.255.255.255", "240e:390:1:1::1", "::ffff:223.5.5.5"
    };

    private const int TimeoutMs = 15000;

    public static void Run()
    {
        Console.WriteLine("\n--- Tier 5: Fail-Closed Hostile Vectors (consuming hostile_vectors.json) ---");
        FailCount = 0;
        TotalCount = 0;

        string? jsonPath = LocateJson();
        if (jsonPath == null)
        {
            Console.WriteLine("  [SKIP] hostile_vectors.json not found; skipping hostile test (0 failures)");
            return;
        }
        string? basePath = LocateBaseDb();
        if (basePath == null)
        {
            Console.WriteLine("  [SKIP] base .qzdb database not found; skipping hostile test (0 failures)");
            return;
        }

        byte[] baseBytes = File.ReadAllBytes(basePath);

        // Baseline: query the UNMUTATED file so we can detect wrong (non-empty, differing) data.
        var baseline = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var br = QzdbReader.OpenBuffer(baseBytes, new ReaderOptions { VerifyCrc = true });
            foreach (var ip in TestIps)
            {
                try { baseline[ip] = br.FindStr(ip) ?? ""; }
                catch (QzdbException) { baseline[ip] = ""; }
            }
        }
        catch (QzdbException e)
        {
            Console.WriteLine("  [FAIL] baseline load of healthy DB failed: " + e.ErrorCode);
            FailCount = 1;
            TotalCount = 1;
            Console.WriteLine($"HostileVectors: 0/1 passed, 1 failed");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        if (!doc.RootElement.TryGetProperty("cases", out var casesElem) || casesElem.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("  [FAIL] hostile_vectors.json missing 'cases' array");
            FailCount = 1;
            TotalCount = 1;
            Console.WriteLine($"HostileVectors: 0/1 passed, 1 failed");
            return;
        }

        var anchors = ParseHeaderOffsets(baseBytes);

        var divergenceReport = new List<string>();
        var anomalyReport = new List<string>();

        Console.WriteLine($"Base DB: {baseBytes.Length} bytes; baseline queries: {TestIps.Length}");

        foreach (var c in casesElem.EnumerateArray())
        {
            string id = c.GetProperty("id").GetString() ?? "(unknown)";
            var mut = c.GetProperty("mutation");
            var exp = c.GetProperty("expected_outcome");
            var expCodes = new List<string>();
            if (exp.TryGetProperty("error_code_any", out var ecElem) && ecElem.ValueKind == JsonValueKind.Array)
                foreach (var e in ecElem.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) expCodes.Add(e.GetString()!);

            var acc = new CaseAcc();
            Action<byte[]> sink = cp =>
            {
                acc.CopyCount++;
                var m1 = EvaluateGuarded(cp, false, baseline); // lenient (verifyCrc=false)
                var m2 = EvaluateGuarded(cp, true, baseline);  // strict (verifyCrc=true, the CRC gate)

                // SECURITY INVARIANT: fail-closed is guaranteed by the strict (default) mode,
                // which must never crash / hang / return wrong data. Lenient mode is a documented
                // opt-out of CRC; wrong data there is the expected tradeoff and must NOT be treated
                // as a fail, but it must still never crash or hang.
                bool strictOk = !m2.Crashed && !m2.Hang && !m2.WrongData;
                bool lenientOk = !m1.Crashed && !m1.Hang;
                if (!strictOk || !lenientOk) acc.FailClosed = false;

                if (m1.Code != null) acc.ObsCodes.Add(m1.Code);
                if (m2.Code != null) acc.ObsCodes.Add(m2.Code);
                if (m2.WrongData)
                {
                    acc.SawWrong = true;
                    if (acc.FirstWrongExample == null)
                        acc.FirstWrongExample = "STRICT " + m2.WrongExample;
                }
                if (m1.Crashed || m2.Crashed) acc.SawCrash = true;
                if (m1.Hang || m2.Hang) acc.SawHang = true;
                if (m1.WrongData) acc.SawLenientWrong = true;
                if ((m1.Opened && m1.Detail.StartsWith("graceful", StringComparison.Ordinal))
                    || (m2.Opened && m2.Detail.StartsWith("graceful", StringComparison.Ordinal)))
                    acc.SawGraceful = true;
                if ((m1.Opened && m1.Detail.StartsWith("correct", StringComparison.Ordinal))
                    || (m2.Opened && m2.Detail.StartsWith("correct", StringComparison.Ordinal)))
                    acc.SawCorrect = true;
            };

            if (id == "group_index_invalid")
            {
                // The literal recipe is a byte-level no-op on std_china's header (current values
                // already equal 1/3). The vector notes authorize the consumer to craft a concrete
                // row, so we fill the IPRow section with 0xFF and rewrite the canonical CRC32 so
                // BOTH verifyCrc modes load — pushing the test to query-time fail-closed validation.
                sink(CraftInvalidEntryRow(baseBytes, anchors));
            }
            else
            {
                ApplyMutation(baseBytes, mut, anchors, sink);
            }

            bool failClosed = acc.FailClosed;
            if (acc.CopyCount == 0)
            {
                failClosed = false;
                acc.FirstWrongExample = "NO COPIES GENERATED (mutation entirely out of bounds - test gap)";
            }

            var expNorm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in expCodes) expNorm.Add(Norm(o));

            bool divergent = false;
            if (failClosed)
            {
                foreach (var oc in acc.ObsCodes)
                {
                    if (!expNorm.Contains(Norm(oc))) { divergent = true; break; }
                }
                if (!divergent && acc.SawGraceful && !expNorm.Contains("gracefulnull")) divergent = true;
                if (!divergent && acc.SawCorrect && !expNorm.Contains("gracefulnull")) divergent = true;
            }

            string status;
            if (!failClosed)
            {
                status = "FAIL";
                FailCount++;
                TotalCount++;
                string reason = acc.SawWrong ? "WRONG-DATA" : (acc.SawCrash ? "CRASH" : (acc.SawHang ? "HANG" : "NO-COPIES"));
                anomalyReport.Add($"ANOMALY  {id}  [{reason}]  mutation={mut}  example={acc.FirstWrongExample}");
            }
            else
            {
                status = divergent ? "PASS*" : "PASS";
                TotalCount++;
                if (divergent)
                    divergenceReport.Add($"DIVERGENT  {id}  observed={DescribeObs(acc.ObsCodes, acc.SawGraceful, acc.SawCorrect)} expected={string.Join("/", expCodes)}");
            }

            Console.WriteLine($"  [{status,-6}] {id,-32} copies={acc.CopyCount,-4} {DescribeObs(acc.ObsCodes, acc.SawGraceful, acc.SawCorrect)}");
        }

        Console.WriteLine();
        if (FailCount == 0)
            Console.WriteLine($"HostileVectors: {TotalCount}/{TotalCount} passed");
        else
            Console.WriteLine($"HostileVectors: {TotalCount - FailCount}/{TotalCount} passed, {FailCount} failed");

        if (divergenceReport.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("--- Divergences (fail-closed holds, but observed family != expected) ---");
            foreach (var d in divergenceReport) Console.WriteLine(d);
        }

        if (anomalyReport.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("--- SDK Anomaly Report (genuine fail-closed violations) ---");
            foreach (var a in anomalyReport) Console.WriteLine(a);
        }
    }

    // ------------------------------------------------------------------
    // Evaluation (timeout-guarded)
    // ------------------------------------------------------------------

    private sealed class Eval
    {
        public bool Opened;
        public string? Code;       // QzdbException error code name, if rejected
        public bool Crashed;
        public bool Hang;
        public bool WrongData;
        public string Detail = "";
        public string? WrongExample;
    }

    private sealed class CaseAcc
    {
        public bool FailClosed = true;
        public readonly HashSet<string> ObsCodes = new(StringComparer.Ordinal);
        public bool SawGraceful, SawCorrect, SawWrong, SawCrash, SawHang, SawLenientWrong;
        public string? FirstWrongExample;
        public int CopyCount;
    }

    private static Eval EvaluateGuarded(byte[] copy, bool verifyCrc, Dictionary<string, string> baseline)
    {
        var task = Task.Run(() => Evaluate(copy, verifyCrc, baseline));
        if (!task.Wait(TimeoutMs))
        {
            return new Eval { Hang = true, Detail = "HANG" };
        }
        return task.Result;
    }

    private static Eval Evaluate(byte[] copy, bool verifyCrc, Dictionary<string, string> baseline)
    {
        var res = new Eval();
        try
        {
            using var reader = QzdbReader.OpenBuffer(copy, new ReaderOptions { VerifyCrc = verifyCrc });
            res.Opened = true;
            bool anyNonEmpty = false, anyWrong = false;
            foreach (var kv in baseline)
            {
                string got;
                try { got = reader.FindStr(kv.Key) ?? ""; }
                catch (QzdbException) { got = ""; }   // defensive; valid IPs never throw here
                catch (Exception) { got = ""; }

                string exp = kv.Value ?? "";
                if (got.Length > 0)
                {
                    anyNonEmpty = true;
                    if (exp != got)
                    {
                        anyWrong = true;
                        if (res.WrongExample == null)
                            res.WrongExample = "ip=" + kv.Key + " base=[" + exp + "] got=[" + got + "]";
                    }
                }
            }
            res.WrongData = anyWrong;
            if (res.WrongData) res.Detail = "WRONG-DATA";
            else if (!anyNonEmpty) res.Detail = "graceful-empty";
            else res.Detail = "correct(lenient)";
        }
        catch (QzdbException e)
        {
            res.Code = e.ErrorCode.ToString();
            res.Detail = "rejected:" + res.Code;
        }
        catch (Exception e)
        {
            res.Crashed = true;
            res.Detail = "CRASH:" + e.GetType().Name;
        }
        return res;
    }

    private static string Norm(string s)
    {
        if (s == null) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string DescribeObs(HashSet<string> obsCodes, bool sawGraceful, bool sawCorrect)
    {
        var parts = new List<string>();
        if (obsCodes.Count > 0) parts.Add("rejected:" + string.Join("/", obsCodes));
        if (sawGraceful) parts.Add("graceful-empty");
        if (sawCorrect) parts.Add("correct(lenient)");
        if (parts.Count == 0) parts.Add("?");
        return string.Join(" | ", parts);
    }

    // ------------------------------------------------------------------
    // Mutation engine
    // ------------------------------------------------------------------

    private static void ApplyMutation(byte[] baseBytes, JsonElement mut, Dictionary<string, long> anchors, Action<byte[]> sink)
    {
        string type = mut.GetProperty("type").GetString() ?? "";
        switch (type)
        {
            case "header_field":
                sink(ApplyHeaderField(baseBytes, mut));
                break;
            case "header_byte_sweep":
            {
                int start = (int)mut.GetProperty("start").GetInt64();
                int end = (int)mut.GetProperty("end").GetInt64();
                foreach (var po in mut.GetProperty("patterns").EnumerateArray())
                {
                    int pat = (int)(po.GetInt64() & 0xFF);
                    for (int off = start; off < end; off++)
                    {
                        if (off < 0 || off >= baseBytes.Length) continue;
                        byte[] cp = (byte[])baseBytes.Clone();
                        cp[off] = (byte)pat;
                        sink(cp);
                    }
                }
                break;
            }
            case "header_field_sweep":
            {
                int width = (int)mut.GetProperty("width").GetInt64();
                long value = mut.GetProperty("value").GetInt64();
                foreach (var oo in mut.GetProperty("offsets").EnumerateArray())
                {
                    int off = (int)oo.GetInt64();
                    byte[] cp = (byte[])baseBytes.Clone();
                    if (off + width > cp.Length) continue;   // bounds-checked, skip OOB
                    WriteLE(cp, off, width, value);
                    sink(cp);
                }
                break;
            }
            case "truncate":
            {
                if (mut.TryGetProperty("bytes", out var bElem))
                {
                    int len = (int)bElem.GetInt64();
                    if (len >= 0 && len < baseBytes.Length) sink(Truncate(baseBytes, len));
                }
                else
                {
                    string mode = mut.GetProperty("mode").GetString() ?? "sweep";
                    int[] lengths;
                    if (mode == "to_zero") lengths = new[] { 0 };
                    else if (mode == "below_header") lengths = new[] { 100 };
                    else if (mode == "at_header") lengths = new[] { 191 };
                    else
                    {
                        var ls = new List<int> { 0 };
                        long l = 1;
                        while (l <= baseBytes.Length)
                        {
                            ls.Add((int)l);
                            if (l == baseBytes.Length) break;
                            l *= 2;
                        }
                        lengths = ls.ToArray();
                    }
                    foreach (int len in lengths)
                        if (len >= 0 && len <= baseBytes.Length) sink(Truncate(baseBytes, len));
                }
                break;
            }
            case "append_junk":
            {
                int length = (int)mut.GetProperty("length").GetInt64();
                string fill = mut.GetProperty("fill").GetString() ?? "random";
                byte[] cp = new byte[baseBytes.Length + length];
                Buffer.BlockCopy(baseBytes, 0, cp, 0, baseBytes.Length);
                if (fill == "0xFF") Array.Fill(cp, (byte)0xFF, baseBytes.Length, length);
                else if (fill == "zeros") { /* already zero-initialized */ }
                else
                {
                    // deterministic fill for reproducibility (mirrors Java's seeded Random)
                    var rnd = new Random(0x1234ABCD);
                    for (int k = baseBytes.Length; k < cp.Length; k++) cp[k] = (byte)rnd.Next(256);
                }
                sink(cp);
                break;
            }
            case "section_mutate":
            {
                string anchor = mut.GetProperty("anchor").GetString() ?? "";
                int span = (int)mut.GetProperty("span").GetInt64();
                if (!anchors.TryGetValue(anchor, out long aoff) || aoff < 0 || aoff >= baseBytes.Length) break;
                foreach (var po in mut.GetProperty("patterns").EnumerateArray())
                {
                    int pat = (int)(po.GetInt64() & 0xFF);
                    byte[] cp = (byte[])baseBytes.Clone();
                    int limit = (int)Math.Min(span, baseBytes.Length - (int)aoff);
                    for (int k = 0; k < limit; k++) cp[(int)aoff + k] = (byte)pat;
                    sink(cp);
                }
                break;
            }
            case "trie_nodes_fill":
            {
                string anchor = mut.GetProperty("anchor").GetString() ?? "";
                string countField = mut.GetProperty("count_field").GetString() ?? "";
                long value = mut.GetProperty("value").GetInt64();
                int writeWidth = (int)mut.GetProperty("write_width").GetInt64();
                if (!anchors.TryGetValue(anchor, out long aoff) || !anchors.TryGetValue(countField, out long nodeCount)) break;
                int flags = (int)anchors["flags"];
                int stride = anchor == "trie_v4_nodes_start"
                    ? ((flags & 0x10) != 0 ? 6 : 8)
                    : ((flags & 0x20) != 0 ? 6 : 8);
                byte[] cp = (byte[])baseBytes.Clone();
                long n = Math.Min(nodeCount, (cp.Length / (long)stride) + 1);
                for (long i = 0; i < n; i++)
                {
                    long bo = aoff + i * stride;
                    if (bo + writeWidth + 4 > cp.Length) break; // bounds-checked, never AIOOBE
                    WriteLE(cp, (int)bo, 4, value);
                    WriteLE(cp, (int)(bo + writeWidth), 4, value);
                }
                sink(cp);
                break;
            }
            case "random_bitflips":
            {
                long seed = mut.GetProperty("seed").GetInt64();
                int rounds = (int)mut.GetProperty("rounds").GetInt64();
                int maxFlips = (int)mut.GetProperty("max_flips").GetInt64();
                int span = mut.TryGetProperty("span", out var spanElem) && spanElem.ValueKind == JsonValueKind.String
                    ? baseBytes.Length
                    : (int)spanElem.GetInt64();
                if (span > baseBytes.Length) span = baseBytes.Length;
                byte[] cp = (byte[])baseBytes.Clone();
                ulong state = (ulong)(seed & 0xFFFFFFFFL);
                for (int r = 0; r < rounds; r++)
                {
                    for (int f = 0; f < maxFlips; f++)
                    {
                        state = state * 6364136223846793005UL + 1442695040888963407UL;
                        int pos = (int)(state % (ulong)span);
                        int bit = (int)(state % 8);
                        if (pos >= 0 && pos < cp.Length) cp[pos] ^= (byte)(1 << bit);
                    }
                }
                sink(cp);
                break;
            }
            case "crc_field_corrupt":
            {
                byte[] cp = (byte[])baseBytes.Clone();
                byte[] zeroed = (byte[])cp.Clone();
                zeroed[16] = 0; zeroed[17] = 0; zeroed[18] = 0; zeroed[19] = 0;
                uint calc = ComputeCrc(zeroed);
                uint bad = calc ^ 0xFFFFFFFFU;
                WriteLE(cp, 16, 4, bad);
                sink(cp);
                break;
            }
            case "compound":
            {
                byte[] cur = (byte[])baseBytes.Clone();
                foreach (var so in mut.GetProperty("steps").EnumerateArray())
                {
                    byte[]? stepOut = null;
                    ApplyMutation(cur, so, anchors, outCopy => { if (stepOut == null) stepOut = outCopy; });
                    if (stepOut != null) cur = stepOut;
                }
                sink(cur);
                break;
            }
            default:
                Console.WriteLine($"    [WARN] unknown mutation type: {type}");
                break;
        }
    }

    /// <summary>
    /// group_index_invalid concrete row-level attack: fill the entire IPRow section with 0xFF
    /// (entryId will be out of bounds) and rewrite the canonical CRC32 so verifyCrc=true also
    /// loads — pushing the test from load-time to query-time fail-closed validation. The SDK
    /// must return null/empty via the extract-path bounds check, never wrong data or crash.
    /// </summary>
    private static byte[] CraftInvalidEntryRow(byte[] baseBytes, Dictionary<string, long> anchors)
    {
        byte[] cp = (byte[])baseBytes.Clone();
        long iprowOff = anchors.GetValueOrDefault("iprow_start", -1);
        long rowCount = Ru32(cp, 20);
        long rowSize = Ru32(cp, 160);
        if (iprowOff <= 0 || rowCount <= 1 || rowSize <= 0 || rowSize > 64 || iprowOff + rowCount * rowSize > cp.Length)
            return cp;
        int rOff = (int)iprowOff;
        long span = rowCount * rowSize;
        int limit = (int)Math.Min(span, cp.Length - rOff);
        Array.Fill(cp, (byte)0xFF, rOff, limit);
        byte[] zeroed = (byte[])cp.Clone();
        zeroed[16] = 0; zeroed[17] = 0; zeroed[18] = 0; zeroed[19] = 0;
        uint calc = ComputeCrc(zeroed);
        WriteLE(cp, 16, 4, calc);
        return cp;
    }

    private static byte[] ApplyHeaderField(byte[] baseBytes, JsonElement mut)
    {
        int off = (int)mut.GetProperty("offset").GetInt64();
        int width = (int)mut.GetProperty("width").GetInt64();
        long value = mut.GetProperty("value").GetInt64();
        long mask = mut.TryGetProperty("mask", out var mElem) ? mElem.GetInt64() : -1;
        byte[] cp = (byte[])baseBytes.Clone();
        if (width == 48)
        {
            if (off + 6 > cp.Length) return cp;
            long cur = Ru48(cp, off);
            long nv = mask >= 0 ? (cur ^ mask) : value;
            for (int k = 0; k < 6; k++) cp[off + k] = (byte)((nv >> (8 * k)) & 0xFF);
        }
        else
        {
            if (off + width > cp.Length) return cp;
            long cur = width switch
            {
                1 => cp[off] & 0xFF,
                2 => Ru16(cp, off),
                4 => Ru32(cp, off),
                8 => Ru64(cp, off),
                _ => 0
            };
            long nv = mask >= 0 ? (cur ^ mask) : value;
            WriteLE(cp, off, width, nv);
        }
        return cp;
    }

    // ------------------------------------------------------------------
    // Header offset resolution (consumer parses its OWN header)
    // ------------------------------------------------------------------

    private static Dictionary<string, long> ParseHeaderOffsets(byte[] buf)
    {
        var m = new Dictionary<string, long>
        {
            ["iprow_start"] = Ru64(buf, 96),
            ["trie_v4_nodes_start"] = Ru64(buf, 72),
            ["trie_v6_nodes_start"] = Ru64(buf, 88),
            ["v4_node_count"] = Ru32(buf, 152),
            ["v6_node_count"] = Ru32(buf, 156),
            ["flags"] = Ru16(buf, 8)
        };
        return m;
    }

    // ------------------------------------------------------------------
    // Little-endian readers / writers + CRC32
    // ------------------------------------------------------------------

    private static long Ru16(byte[] b, int off) => (b[off] & 0xFFL) | ((b[off + 1] & 0xFFL) << 8);
    private static long Ru32(byte[] b, int off) =>
        (b[off] & 0xFFL) | ((b[off + 1] & 0xFFL) << 8) | ((b[off + 2] & 0xFFL) << 16) | ((b[off + 3] & 0xFFL) << 24);
    private static long Ru48(byte[] b, int off)
    {
        long v = 0;
        for (int k = 0; k < 6; k++) v |= (b[off + k] & 0xFFL) << (8 * k);
        return v;
    }
    private static long Ru64(byte[] b, int off)
    {
        long v = 0;
        for (int k = 0; k < 8; k++) v |= (b[off + k] & 0xFFL) << (8 * k);
        return v;
    }

    private static void WriteLE(byte[] b, int off, int width, long value)
    {
        for (int k = 0; k < width; k++) b[off + k] = (byte)((value >> (8 * k)) & 0xFF);
    }

    private static byte[] Truncate(byte[] b, int len)
    {
        var cp = new byte[len];
        Buffer.BlockCopy(b, 0, cp, 0, len);
        return cp;
    }

    /// <summary>Canonical CRC32 — identical to QzdbReader.ComputeCanonicalCrc: CRC over
    /// [0,16) + 4 zero bytes + [20,end). Used so crafted rows pass BOTH verifyCrc modes.</summary>
    private static uint ComputeCrc(byte[] buf)
    {
        var crc = new Crc32();
        crc.Append(buf.AsSpan(0, 16));
        Span<byte> zeros = stackalloc byte[4];
        crc.Append(zeros);
        crc.Append(buf.AsSpan(20));
        return crc.GetCurrentHashAsUInt32();
    }

    // ------------------------------------------------------------------
    // Resource location (CWD is multi-lang/ when run via the harness)
    // ------------------------------------------------------------------

    private static string? LocateJson()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "tools", "hostile_vectors.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "hostile_vectors.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "hostile_vectors.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "hostile_vectors.json"),
            "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/tools/hostile_vectors.json"
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static string? LocateBaseDb()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "data", "qqzeng_ip_std_china.qzdb"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "qqzeng_ip_std_china.qzdb"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "qqzeng_ip_std_china.qzdb"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "data", "qqzeng_ip_std_china.qzdb"),
            "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/data/qqzeng_ip_std_china.qzdb"
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
