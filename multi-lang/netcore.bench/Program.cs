// QZDB reference-compliant benchmark for .NET  (docs/BENCH_CONTRACT.md v1.0)
//
// Implements the splitmix64 reference RNG (byte-identical to the other 7
// benches), four distributions, dual-stack tri-mode, QPS/P50/P95/P99 cold vs
// hot, thread scaling 1/2/4/8/16 over a SHARED QzdbReader + a 16x100k
// concurrency gate, and canonical JSON to
// multi-lang/bench_reports/netcore_<edition>.json.
//
// Parity guard: FNV-1a 64 over the first 1024 queries of every stream is
// compared against multi-lang/tools/bench_vectors.json — the same manifest
// every other language reads. A mismatch aborts before any number is printed,
// so a "fast" bench can never be a bench that measured a different workload.
//
// Env overrides:  BENCH_OPS=200000   BENCH_EDITIONS=std_china
//
// Run:  dotnet run --project multi-lang/netcore.bench -c Release

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QQZeng.Qzdb;

internal static class Bench
{
    private const ulong MasterSeed = 20260807UL;
    private const int PoolHotV4 = 4096;
    private const int PoolHotV6 = 1024;
    private const int FingerprintN = 1024;
    private const ulong MappedPrefixLow = 0x0000_FFFF_0000_0000UL; // ::ffff:0:0 (low 64 bits)
    private const int ColdOps = 200_000;
    private const int WarmupOps = 1_000_000;
    private const int LatEvery = 20;
    private const int ConcThreads = 16;
    private const int ConcOps = 100_000;

    private static readonly string[] DistNames = { "random", "hot", "sequential", "real_world" };
    private static readonly string[] ModeNames = { "v4", "v6", "mixed" };
    private static readonly int[] ThreadCfgs = { 1, 2, 4, 8, 16 };

    private enum Dist { Random, Hot, Sequential, RealWorld }
    private enum Mode { V4, V6, Mixed }

    // ------------------------------------------------------------ splitmix64

    private struct SplitMix64
    {
        private ulong _s;
        public SplitMix64(ulong seed) => _s = seed;

        public ulong Next()
        {
            unchecked
            {
                _s += 0x9E37_79B9_7F4A_7C15UL;
                ulong z = _s;
                z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
                return z ^ (z >> 31);
            }
        }

        public uint U32() => (uint)Next();
    }

    private static ulong Fnv1a(ReadOnlySpan<byte> data, ulong h)
    {
        unchecked
        {
            if (h == 0) h = 0xCBF2_9CE4_8422_2325UL;
            foreach (byte b in data)
            {
                h ^= b;
                h *= 0x0000_0100_0000_01B3UL;
            }
            return h;
        }
    }

    // ------------------------------------------------------ pools + stream

    private static (uint[] v4, (ulong hi, ulong lo)[] v6) BuildPools()
    {
        var p4 = new SplitMix64(MasterSeed + 1);
        var poolV4 = new uint[PoolHotV4];
        for (int i = 0; i < PoolHotV4; i++) poolV4[i] = p4.U32();

        var p6 = new SplitMix64(MasterSeed + 2);
        var poolV6 = new (ulong, ulong)[PoolHotV6];
        for (int i = 0; i < PoolHotV6; i++) poolV6[i] = (p6.Next(), p6.Next());

        return (poolV4, poolV6);
    }

    private sealed class Stream
    {
        private SplitMix64 _rng;
        private readonly Dist _dist;
        private readonly Mode _mode;
        private readonly uint[] _poolV4;
        private readonly (ulong hi, ulong lo)[] _poolV6;
        private readonly uint _base4;
        private readonly (ulong hi, ulong lo) _base6;
        private ulong _i;

        public Stream(Dist dist, Mode mode, ulong seed, uint[] poolV4, (ulong, ulong)[] poolV6)
        {
            _rng = new SplitMix64(seed);
            _dist = dist;
            _mode = mode;
            _poolV4 = poolV4;
            _poolV6 = poolV6;
            _base4 = _rng.U32();
            // NOTE: evaluation order matters — high word first, then low.
            ulong hi = _rng.Next();
            ulong lo = _rng.Next();
            _base6 = (hi, lo);
            _i = 0;
        }

        private uint GenV4()
        {
            unchecked
            {
                switch (_dist)
                {
                    case Dist.Random: return _rng.U32();
                    case Dist.Hot: return _poolV4[_rng.U32() % PoolHotV4];
                    case Dist.Sequential: return _base4 + (uint)_i;
                    default:
                        uint r = _rng.U32() % 10;
                        if (r < 6) return _poolV4[_rng.U32() % PoolHotV4];
                        if (r < 9) return _rng.U32();
                        return _base4 + (uint)_i;
                }
            }
        }

        private (ulong hi, ulong lo) SeqV6()
        {
            unchecked
            {
                ulong lo = _base6.lo + _i;
                ulong carry = lo < _base6.lo ? 1UL : 0UL;   // 128-bit add, low word wrapped
                return (_base6.hi + carry, lo);
            }
        }

        private (ulong hi, ulong lo) GenV6()
        {
            switch (_dist)
            {
                case Dist.Random:
                {
                    ulong hi = _rng.Next();
                    ulong lo = _rng.Next();
                    return (hi, lo);
                }
                case Dist.Hot:
                    return _poolV6[_rng.U32() % PoolHotV6];
                case Dist.Sequential:
                    return SeqV6();
                default:
                {
                    uint r = _rng.U32() % 10;
                    if (r < 6) return _poolV6[_rng.U32() % PoolHotV6];
                    if (r < 9)
                    {
                        ulong hi = _rng.Next();
                        ulong lo = _rng.Next();
                        return (hi, lo);
                    }
                    return SeqV6();
                }
            }
        }

        /// <summary>kind: 0 = v4, 1 = pure v6, 2 = v4-mapped v6.</summary>
        public (byte kind, ulong hi, ulong lo) Next()
        {
            byte kind;
            ulong hi, lo;
            switch (_mode)
            {
                case Mode.V4:
                    kind = 0; hi = GenV4(); lo = 0;
                    break;
                case Mode.V6:
                    if (_rng.U32() % 5 == 0)
                    {
                        kind = 2; hi = 0; lo = MappedPrefixLow | GenV4();
                    }
                    else
                    {
                        var v = GenV6();
                        kind = 1; hi = v.hi; lo = v.lo;
                    }
                    break;
                default:
                {
                    ulong m = _i % 10;
                    if (m < 5) { kind = 0; hi = GenV4(); lo = 0; }
                    else if (m < 9) { var v = GenV6(); kind = 1; hi = v.hi; lo = v.lo; }
                    else { kind = 2; hi = 0; lo = MappedPrefixLow | GenV4(); }
                    break;
                }
            }
            // `i` indexes the CURRENT query and only advances afterwards —
            // mirrors `for i in range(OPS)` in bench_gen.py.
            _i++;
            return (kind, hi, lo);
        }
    }

    private static int EncQuery(byte kind, ulong hi, ulong lo, Span<byte> dst)
    {
        if (kind == 0)
        {
            BinaryPrimitivesWriteLe(dst, hi);       // u32 zero-extended to u64, LE
            return 8;
        }
        BinaryPrimitivesWriteLe(dst, hi);
        BinaryPrimitivesWriteLe(dst[8..], lo);
        return 16;
    }

    private static void BinaryPrimitivesWriteLe(Span<byte> dst, ulong v)
    {
        for (int i = 0; i < 8; i++) dst[i] = (byte)(v >> (8 * i));
    }

    // ------------------------------------------------------------- metrics

    private sealed class Metrics
    {
        public int Ops;
        public double Qps;
        public double AvgNs;
        public long P50Ns, P95Ns, P99Ns;
        public long Errors;
        public long Hits;
        public double HitRate;
        public string Warm = "";
        public string Api = "uint";
    }

    private static long Pct(List<long> v, double p)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        int idx = (int)Math.Floor(v.Count * p + 0.9999) - 1;
        if (idx < 0) idx = 0;
        if (idx > v.Count - 1) idx = v.Count - 1;
        return v[idx];
    }

    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Route each query kind to the entry point a real caller would use.
    /// kind 2 (IPv4-mapped, ::ffff:w.x.y.z) must reach an entry that performs
    /// the mapped downgrade, otherwise it misses unconditionally and the bench
    /// times the early-exit miss path instead of a real lookup.
    ///
    /// API GAP: unlike the other 7 SDKs (find_v6 / FindV6Uint / findV6Bin /
    /// qzdb_find_v6 / find_v6_bytes), the .NET SDK exposes no *pure* v6 binary
    /// entry — FindBytes is the only 16-byte path and it always runs the
    /// IsV4Mapped check first. So kind 1 also goes through FindBytes here. The
    /// result is identical for non-mapped input; only a 12-byte prefix compare
    /// is added. Recorded in the report `note` so the numbers stay honest.
    /// FindBytes does not retain the array, so one scratch buffer per thread.
    /// </summary>
    private static bool Dispatch(QzdbReader r, byte kind, ulong hi, ulong lo, byte[] buf16)
    {
        if (kind == 0) return r.FindUint((uint)hi) != null;
        WriteBe(buf16, hi, lo);
        return r.FindBytes(buf16) != null;
    }

    private static void WriteBe(byte[] dst, ulong hi, ulong lo)
    {
        for (int i = 0; i < 8; i++) dst[i] = (byte)(hi >> (56 - 8 * i));
        for (int i = 0; i < 8; i++) dst[8 + i] = (byte)(lo >> (56 - 8 * i));
    }

    private static Metrics RunSingle(QzdbReader r, Dist dist, Mode mode, ulong seed,
                                     uint[] poolV4, (ulong, ulong)[] poolV6,
                                     int ops, bool sample)
    {
        var st = new Stream(dist, mode, seed, poolV4, poolV6);
        var lat = new List<long>(ops / LatEvery + 1);
        var buf = new byte[16];
        long hits = 0, errors = 0;

        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < ops; i++)
        {
            var (kind, hi, lo) = st.Next();
            bool found;
            if (sample && i % LatEvery == 0)
            {
                long a = Stopwatch.GetTimestamp();
                try { found = Dispatch(r, kind, hi, lo, buf); }
                catch (QzdbException) { found = false; errors++; }
                lat.Add((long)((Stopwatch.GetTimestamp() - a) * NsPerTick));
            }
            else
            {
                try { found = Dispatch(r, kind, hi, lo, buf); }
                catch (QzdbException) { found = false; errors++; }
            }
            if (found) hits++;
        }
        double el = (Stopwatch.GetTimestamp() - t0) * NsPerTick / 1e9;

        return new Metrics
        {
            Ops = ops,
            Qps = ops / el,
            AvgNs = el * 1e9 / ops,
            P50Ns = Pct(lat, 0.50),
            P95Ns = Pct(lat, 0.95),
            P99Ns = Pct(lat, 0.99),
            Errors = errors,
            Hits = hits,
            HitRate = (double)hits / ops,
            Api = "uint",
        };
    }

    private static Metrics RunMulti(QzdbReader r, Dist dist, Mode mode, ulong seed,
                                    uint[] poolV4, (ulong, ulong)[] poolV6,
                                    int threads, int ops)
    {
        int per = ops / threads;
        long done = 0, hits = 0;
        var workers = new Thread[threads];

        long t0 = Stopwatch.GetTimestamp();
        for (int t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                var st = new Stream(dist, mode, seed, poolV4, poolV6);
                var buf = new byte[16];
                long localHits = 0;
                for (int i = 0; i < per; i++)
                {
                    var (kind, hi, lo) = st.Next();
                    bool found;
                    try { found = Dispatch(r, kind, hi, lo, buf); }
                    catch (QzdbException) { found = false; }
                    if (found) localHits++;
                }
                Interlocked.Add(ref done, per);
                Interlocked.Add(ref hits, localHits);
            }, 1 << 20);
            workers[t].Start();
        }
        foreach (var w in workers) w.Join();
        double el = (Stopwatch.GetTimestamp() - t0) * NsPerTick / 1e9;

        long d = Interlocked.Read(ref done);
        long h = Interlocked.Read(ref hits);
        return new Metrics
        {
            Ops = (int)d,
            Qps = d / el,
            AvgNs = el * 1e9 / d,
            Hits = h,
            HitRate = (double)h / d,
            Warm = "hot",
            Api = "uint",
        };
    }

    private static (bool safe, long done) ConcurrencySafe(QzdbReader r, ulong seed,
                                                          uint[] poolV4, (ulong, ulong)[] poolV6)
    {
        long done = 0, failed = 0;
        var workers = new Thread[ConcThreads];
        for (int t = 0; t < ConcThreads; t++)
        {
            workers[t] = new Thread(() =>
            {
                try
                {
                    var st = new Stream(Dist.Hot, Mode.Mixed, seed, poolV4, poolV6);
                    var buf = new byte[16];
                    for (int i = 0; i < ConcOps; i++)
                    {
                        var (kind, hi, lo) = st.Next();
                        Dispatch(r, kind, hi, lo, buf);   // a miss is expected, not an error
                    }
                    Interlocked.Add(ref done, ConcOps);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failed);
                }
            }, 1 << 20);
            workers[t].Start();
        }
        foreach (var w in workers) w.Join();

        long d = Interlocked.Read(ref done);
        return (Interlocked.Read(ref failed) == 0 && d == (long)ConcThreads * ConcOps, d);
    }

    // --------------------------------------------------------------- parity

    private static bool ParitySelfCheck(JsonNode manifest, uint[] poolV4, (ulong, ulong)[] poolV6)
    {
        Console.Write("parity self-check ... ");
        int bad = 0;
        Span<byte> buf = stackalloc byte[16];
        foreach (var dn in DistNames)
        {
            foreach (var mn in ModeNames)
            {
                var info = manifest["streams"]![dn]![mn]!;
                ulong want = ulong.Parse(info["first1024_fnv1a"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                ulong seed = info["seed"]!.GetValue<ulong>();

                var st = new Stream(DistFrom(dn), ModeFrom(mn), seed, poolV4, poolV6);
                ulong h = 0;
                for (int i = 0; i < FingerprintN; i++)
                {
                    var (kind, hi, lo) = st.Next();
                    int n = EncQuery(kind, hi, lo, buf);
                    h = Fnv1a(buf[..n], h);
                }
                if (h != want)
                {
                    Console.WriteLine($"\n  MISMATCH {dn}.{mn} got={h} want={want}");
                    bad++;
                }
            }
        }
        if (bad != 0) { Console.WriteLine("FAILED"); return false; }
        Console.WriteLine("OK (12/12 streams match bench_vectors.json)");
        return true;
    }

    private static Dist DistFrom(string s) => s switch
    {
        "random" => Dist.Random,
        "hot" => Dist.Hot,
        "sequential" => Dist.Sequential,
        _ => Dist.RealWorld,
    };

    private static Mode ModeFrom(string s) => s switch
    {
        "v4" => Mode.V4,
        "v6" => Mode.V6,
        _ => Mode.Mixed,
    };

    // -------------------------------------------------------------- helpers

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 10 && d != null; i++)
        {
            if (File.Exists(Path.Combine(d.FullName, "multi-lang", "tools", "bench_vectors.json")))
                return d.FullName;
            d = d.Parent;
        }
        return "";
    }

    private static string? FindDb(string root, string edition)
    {
        (string tier, string region)? tr = edition switch
        {
            "std_china" => ("std", "china"),
            "max_global" => ("max", "global"),
            _ => null,
        };
        if (tr == null) return null;
        foreach (var bas in new[] { "multi-lang/test_data_202608", "test_data_202608" })
        {
            var p = Path.Combine(root, bas.Replace('/', Path.DirectorySeparatorChar),
                                 tr.Value.tier, tr.Value.region, $"qqzeng_ip_{edition}.qzdb");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string FmtV4(uint ip) =>
        $"{(ip >> 24) & 255}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";

    private static string FmtV6(ulong hi, ulong lo)
    {
        var sb = new StringBuilder(40);
        for (int k = 0; k < 4; k++) sb.Append(((hi >> (48 - 16 * k)) & 0xFFFF).ToString("x", CultureInfo.InvariantCulture)).Append(':');
        for (int k = 0; k < 4; k++)
        {
            sb.Append(((lo >> (48 - 16 * k)) & 0xFFFF).ToString("x", CultureInfo.InvariantCulture));
            if (k < 3) sb.Append(':');
        }
        return sb.ToString();
    }

    private static string Shell(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return "";
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            return o;
        }
        catch { return ""; }
    }

    private static JsonObject M(Metrics m)
    {
        var o = new JsonObject
        {
            ["ops"] = m.Ops,
            ["qps"] = (long)m.Qps,
            ["avg_ns"] = Math.Round(m.AvgNs, 1),
            ["p50_ns"] = m.P50Ns,
            ["p95_ns"] = m.P95Ns,
            ["p99_ns"] = m.P99Ns,
            ["errors"] = m.Errors,
            ["hits"] = m.Hits,
            ["hit_rate"] = Math.Round(m.HitRate, 6),
        };
        if (!string.IsNullOrEmpty(m.Warm)) o["warm"] = m.Warm;
        if (!string.IsNullOrEmpty(m.Api)) o["api"] = m.Api;
        return o;
    }

    // ------------------------------------------------------------------ main

    public static int Main()
    {
        int ops = 2_000_000;
        if (int.TryParse(Environment.GetEnvironmentVariable("BENCH_OPS"), out int envOps) && envOps > 0)
            ops = envOps;

        var editions = new[] { "std_china", "max_global" };
        var envEd = Environment.GetEnvironmentVariable("BENCH_EDITIONS");
        if (!string.IsNullOrWhiteSpace(envEd))
            editions = envEd.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string root = RepoRoot();
        if (root.Length == 0)
        {
            Console.Error.WriteLine("cannot locate repo root");
            return 1;
        }

        var manifest = JsonNode.Parse(
            File.ReadAllText(Path.Combine(root, "multi-lang", "tools", "bench_vectors.json")))!;

        var (poolV4, poolV6) = BuildPools();
        if (!ParitySelfCheck(manifest, poolV4, poolV6)) return 1;

        string repdir = Path.Combine(root, "multi-lang", "bench_reports");
        Directory.CreateDirectory(repdir);

        string ts = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        string cpu = Shell("sysctl", "-n machdep.cpu.brand_string");
        if (cpu.Length == 0) cpu = "unknown";
        int cores = Environment.ProcessorCount;

        ulong seedHotMixed = manifest["streams"]!["hot"]!["mixed"]!["seed"]!.GetValue<ulong>();

        foreach (var edition in editions)
        {
            string? db = FindDb(root, edition);
            if (db == null) { Console.WriteLine($"[SKIP] {edition}: db not found"); continue; }

            QzdbReader reader;
            try { reader = QzdbReader.Open(db, new ReaderOptions { VerifyCrc = false }); }
            catch (Exception e) { Console.WriteLine($"[SKIP] {edition}: open failed: {e.Message}"); continue; }

            long bytes = new FileInfo(db).Length;
            Console.WriteLine($"\nedition {edition}: {db} ({bytes} bytes)");

            var (safe, cdone) = ConcurrencySafe(reader, seedHotMixed, poolV4, poolV6);
            Console.WriteLine($"  concurrency_safe({ConcThreads}x{ConcOps / 1000}k): " +
                              $"{(safe ? "true" : "false")} (done={cdone})");

            var distOut = new JsonObject();
            foreach (var dn in DistNames)
            {
                var modeOut = new JsonObject();
                foreach (var mn in ModeNames)
                {
                    ulong seed = manifest["streams"]![dn]![mn]!["seed"]!.GetValue<ulong>();
                    var d = DistFrom(dn);
                    var m = ModeFrom(mn);

                    var cold = RunSingle(reader, d, m, seed, poolV4, poolV6, Math.Min(ops, ColdOps), true);
                    cold.Warm = "cold";
                    RunSingle(reader, d, m, seed, poolV4, poolV6, Math.Min(ops, WarmupOps), false);
                    var hot = RunSingle(reader, d, m, seed, poolV4, poolV6, ops, true);
                    hot.Warm = "hot";

                    var th = new JsonObject();
                    Metrics? t1 = null, t16 = null;
                    foreach (int tc in ThreadCfgs)
                    {
                        var mm = RunMulti(reader, d, m, seed, poolV4, poolV6, tc, ops);
                        th[tc.ToString(CultureInfo.InvariantCulture)] = M(mm);
                        if (tc == 1) t1 = mm;
                        if (tc == 16) t16 = mm;
                    }
                    double scl = t16!.Qps / (t1!.Qps + 1e-9);

                    modeOut[mn] = new JsonObject
                    {
                        ["cold"] = M(cold),
                        ["hot"] = M(hot),
                        ["threads"] = th,
                    };

                    Console.WriteLine(
                        $"  {dn,-11}.{mn,-6} hot QPS={hot.Qps,12:F0} p50={hot.P50Ns,6}ns p99={hot.P99Ns,7}ns " +
                        $"1T={t1.Qps,12:F0} 16T={t16.Qps,12:F0} ({scl:F1}x) hit={hot.HitRate * 100:F1}%");
                }
                distOut[dn] = modeOut;
            }

            // string round-trip on hot.mixed — parse + lookup, the API most apps use
            {
                var st = new Stream(Dist.Hot, Mode.Mixed, seedHotMixed, poolV4, poolV6);
                var lat = new List<long>(ops / LatEvery + 1);
                long serr = 0;
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < ops; i++)
                {
                    var (kind, hi, lo) = st.Next();
                    string s = kind == 0 ? FmtV4((uint)hi) : FmtV6(hi, lo);
                    if (i % LatEvery == 0)
                    {
                        long a = Stopwatch.GetTimestamp();
                        try { reader.Find(s); } catch (QzdbException) { serr++; }
                        lat.Add((long)((Stopwatch.GetTimestamp() - a) * NsPerTick));
                    }
                    else
                    {
                        try { reader.Find(s); } catch (QzdbException) { serr++; }
                    }
                }
                double el = (Stopwatch.GetTimestamp() - t0) * NsPerTick / 1e9;
                var srt = new Metrics
                {
                    Ops = ops,
                    Qps = ops / el,
                    AvgNs = el * 1e9 / ops,
                    P50Ns = Pct(lat, 0.50),
                    P95Ns = Pct(lat, 0.95),
                    P99Ns = Pct(lat, 0.99),
                    Errors = serr,
                    Hits = 0,
                    HitRate = 0,
                    Warm = "hot",
                    Api = "string",
                };
                Console.WriteLine($"  {"hot",-11}.{"mixed",-6} STRING round-trip QPS={srt.Qps,12:F0} p99={srt.P99Ns,7}ns");

                var report = new JsonObject
                {
                    ["contract"] = "QZDB_BENCH_CONTRACT v1.0",
                    ["language"] = "csharp",
                    ["sdk_version"] = "multi-lang/netcore (QQZeng.Qzdb)",
                    ["timestamp"] = ts,
                    ["seed"] = MasterSeed,
                    ["db"] = new JsonObject
                    {
                        ["path"] = db,
                        ["edition"] = edition,
                        ["bytes"] = bytes,
                        ["hash"] = "crc32:n/a",
                    },
                    ["environment"] = new JsonObject
                    {
                        ["cpu"] = cpu,
                        ["cores"] = cores,
                        ["os"] = "darwin arm64",
                        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        ["compiler"] = "roslyn",
                        ["bench_contract"] = "v1.0",
                        ["note"] = "single QzdbReader shared across System.Threading.Thread workers; " +
                                   "immutable Snapshot + per-snapshot geo cache. " +
                                   "Pure-v6 queries go through FindBytes (which runs an IsV4Mapped " +
                                   "prefix check first) because the .NET SDK exposes no pure-v6 " +
                                   "binary entry, unlike the other 7 SDKs.",
                    },
                    ["distributions"] = distOut,
                    ["string_roundtrip"] = new JsonObject { ["hot"] = new JsonObject { ["mixed"] = M(srt) } },
                    ["concurrency_safe"] = safe,
                    ["concurrency_done"] = cdone,
                    ["concurrency_spec"] = $"{ConcThreads} threads x {ConcOps} ops shared reader",
                };

                string outPath = Path.Combine(repdir, $"netcore_{edition}.json");
                File.WriteAllText(outPath,
                    report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"  wrote {outPath}");
            }

            reader.Dispose();
        }

        return 0;
    }
}
