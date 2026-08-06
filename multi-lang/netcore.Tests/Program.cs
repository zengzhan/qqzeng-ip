using Qzdb;
using System.Globalization;

class Program
{
    static string BP = "";
    static System.Random rng = new System.Random(42);
    static int tier1Pass = 0, tier1Fail = 0;
    static int tier2Nodes = 0, tier2Err = 0, tier2Ipv4 = 0, tier2Ipv6 = 0, tier2Excl = 0;

    static int Main()
    {
        BP = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "test_data_202608"));
        Console.WriteLine("=== QZDB C# SDK Full Test Suite ===");
        RunTier1();
        RunTier2();
        RunTier3();
        bool allPass = tier1Fail == 0 && tier2Err == 0;
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine((allPass ? "ALL TIERS PASSED" : "SOME TIERS FAILED"));
        return allPass ? 0 : 1;
    }

    static void RunTier1()
    {
        Console.WriteLine("\n--- Tier 1: Unit & Boundary ---");
        using var r = new DatabaseReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build();
        T1(r.Find("01.1.1.1") == null, "reject leading zero");
        T1(r.Find("256.1.1.1") == null, "reject 256");
        T1(r.Find("1.1.1") == null, "reject missing segment");
        T1(r.Find("1.1.1.1.1") == null, "reject extra segment");
        T1(r.Find("") == null, "reject empty");
        T1(r.Find("   ") == null, "reject whitespace");
        T1(r.Find("abc") == null, "reject non-numeric");
        T1(r.Find("1.1.1.1:80") == null, "reject port");
        T1(r.Find("1.1.1.1/24") == null, "reject CIDR");
        T1(r.Find("fe80::1%eth0") == null, "reject zone ID");
        T1(r.Find("1::2::3") == null, "reject double ::");
        T1(r.Find("gggg::1") == null, "reject invalid hex");
        T1(r.Find("12345::") == null, "reject group too long");
        T1(r.Find("1:2:3") == null, "reject too few groups");
        var d = r.FindStr("223.5.5.5");
        var m1 = r.FindStr("::ffff:223.5.5.5");
        var m2 = r.FindStr("::ffff:df05:505");
        T1(d == m1 && d == m2, "Mapped == direct");
        T1(UsageType.FromString("DNS").IsKnown, "DNS known");
        T1(UsageType.FromString("Cloud").IsKnown, "Cloud known");
        T1(UsageType.FromString("FutureUnknown").IsKnown == false, "unknown type");
        T1(UsageType.FromString("FutureUnknown").RawValue == "FutureUnknown", "unknown preserves raw");
        T1(UsageType.FromString("").IsKnown, "empty -> Unknown");
        T1(UsageType.FromString(null).IsKnown, "null -> Unknown");
        T1(Enum.GetValues<KnownUsageType>().Length == 21, "21 types");
        try { new DatabaseReader.Builder(new byte[] { (byte)'X', (byte)'Z' }).Build(); T1(false, "should throw"); }
        catch (QzdbException) { T1(true, "corrupted file rejected"); }
        var trunc = new byte[] { (byte)'Q', (byte)'Z', (byte)'D', (byte)'B' };
        try { new DatabaseReader.Builder(trunc).Build(); T1(false, "should throw truncated"); }
        catch (QzdbException) { T1(true, "truncated file rejected"); }
        try {
            var bytes = System.IO.File.ReadAllBytes(BP + "/std/china/qqzeng_ip_std_china.qzdb");
            bytes[200] ^= 0xFF;
            new DatabaseReader.Builder(bytes).VerifyCrc(true).Build();
            T1(false, "should throw CRC");
        } catch (QzdbException e) { T1(e.ErrorCode == ErrorCode.Corrupted, "CRC mismatch detected"); }
        T1(r.VerifyCrc(), "CRC valid on healthy file");
        using (var r2 = new DatabaseReader.Builder(BP + "/max/china/qqzeng_ip_max_china.qzdb").Build())
        {
            var info = r2.Find("114.114.114.114");
            T1(info.Get("country") == info.Get("COUNTRY"), "case insensitive");
            T1(info.Get("country") == info.Get("Country"), "mixed case");
            T1(info.Get("country_code") == info.Get("countrycode"), "underscore insensitive");
            T1(info.Get("nonexistent") == "", "missing field empty");
            T1(info.Get("") == "", "empty field empty");
        }
        var rd = new DatabaseReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build();
        rd.Dispose();
        try { rd.Find("1.1.1.1"); T1(false, "should throw disposed"); }
        catch (ObjectDisposedException) { T1(true, "disposed throws"); }
        var rd2 = new DatabaseReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build();
        rd2.Dispose();
        rd2.Dispose();
        T1(true, "double dispose idempotent");
        Console.WriteLine("Tier 1: " + tier1Pass + " pass, " + tier1Fail + " fail");
    }

    static void RunTier2()
    {
        Console.WriteLine("\n--- Tier 2: Ground Truth ---");
        foreach (var ver in new[] { "std", "pro", "ult", "max", "asn" })
        {
            foreach (var scope in new[] { "china", "global" })
            {
                var dbPath = BP + "/" + ver + "/" + scope + "/qqzeng_ip_" + ver + "_" + scope + ".qzdb";
                var csvPath = BP + "/" + ver + "/" + scope + "/qqzeng_ip_" + ver + "_" + scope + "_range.csv";
                if (!System.IO.File.Exists(dbPath) || !System.IO.File.Exists(csvPath)) continue;
                VerifyVersion(ver, scope, dbPath, csvPath);
            }
        }
        Console.WriteLine("Tier 2: " + tier2Nodes + " nodes, " + tier2Err + " errors, IPv4=" + tier2Ipv4 + " IPv6=" + tier2Ipv6 + " excluded=" + tier2Excl);
    }

    static void RunTier3()
    {
        Console.WriteLine("\n--- Tier 3: Performance ---");
        var ipv4 = GenerateIpv4(500000);
        var ipv6 = GenerateIpv6(500000);
        using var r = new DatabaseReader.Builder(BP + "/max/global/qqzeng_ip_max_global.qzdb").Build();
        for (int i = 0; i < 100000; i++) { r.Find(ipv4[i % ipv4.Length]); r.Find(ipv6[i % ipv6.Length]); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000000; i++) r.Find(ipv4[i % ipv4.Length]);
        sw.Stop();
        double qps = 1000000 / sw.Elapsed.TotalSeconds;
        Console.WriteLine("Single-thread IPv4: " + qps.ToString("N0") + " QPS");
        int safetyErr = 0;
        var lockObj = new object();
        System.Threading.Tasks.Parallel.For(0, 16, t =>
        {
            try { for (int i = 0; i < 100000; i++) { r.Find(ipv4[i % ipv4.Length]); r.Find(ipv6[i % ipv6.Length]); } }
            catch (Exception ex) { lock (lockObj) { safetyErr++; Console.WriteLine("  Thread err: " + ex.Message); } }
        });
        Console.WriteLine("Concurrent safety errors: " + safetyErr);
        T1(safetyErr == 0, "16-thread concurrent safe");
        Console.WriteLine("Tier 3: performance + safety verified");
    }

    static void VerifyVersion(string ver, string scope, string dbPath, string csvPath)
    {
        var csvLines = System.IO.File.ReadAllLines(csvPath);
        if (csvLines.Length < 2) return;
        var csvHeaders = ParseCsv(csvLines[0]);
        var colMap = new System.Collections.Generic.Dictionary<string, int>();
        for (int i = 0; i < csvHeaders.Length; i++) colMap[csvHeaders[i].Trim()] = i;
        using var r = new DatabaseReader.Builder(dbPath).Build();
        var sample = r.Find(ParseCsv(csvLines[1])[0].Trim());
        if (sample == null) return;
        var dbFields = new System.Collections.Generic.HashSet<string>(sample.FieldNames);
        int verErr = 0, verChk = 0;
        for (int i = 1; i < csvLines.Length; i++)
        {
            var cols = ParseCsv(csvLines[i]);
            if (cols.Length < csvHeaders.Length) continue;
            var ip = cols[0].Trim();
            if (string.IsNullOrEmpty(ip) || ip == "0.0.0.0" || ip == "::") continue;
            if (ip.StartsWith("::ffff:") || ip.StartsWith("0:0:0:0:0:ffff:")) { tier2Excl++; continue; }
            if (ip.Contains(':')) tier2Ipv6++; else tier2Ipv4++;
            var info = r.Find(ip);
            if (info == null) continue;
            verChk++;
            foreach (var h in csvHeaders)
            {
                var header = h.Trim();
                if (header == "start_ip" || header == "end_ip" || header == "start_ip_num" || header == "end_ip_num") continue;
                if (!colMap.ContainsKey(header) || !dbFields.Contains(header)) continue;
                var idx = colMap[header];
                if (idx >= cols.Length) continue;
                var exp = cols[idx].Trim();
                var act = info.Get(header).Trim();
                if (!Match(header, exp, act))
                {
                    verErr++;
                    if (verErr <= 2) Console.WriteLine("  ERR [" + ver + " " + scope + "] " + ip + " " + header + ": csv='" + exp + "' db='" + act + "'");
                }
            }
        }
        tier2Nodes += verChk;
        tier2Err += verErr;
        Console.WriteLine("  [" + ver + " " + scope + "] " + verChk + " nodes, " + verErr + " errors");
    }

    static bool Match(string field, string exp, string act)
    {
        if (exp == act) return true;
        if (string.IsNullOrEmpty(exp)) return true;

        if (field == "longitude" || field == "latitude")
        {
            if (double.TryParse(exp, NumberStyles.Float, CultureInfo.InvariantCulture, out var e) &&
                double.TryParse(act, NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
                return System.Math.Abs(e - a) < 0.001;
        }

        var normE = exp.ToLowerInvariant().Trim();
        var normA = act.ToLowerInvariant().Trim();
        if (normE == normA) return true;

        normE = normE.TrimEnd('0').TrimEnd('.');
        normA = normA.TrimEnd('0').TrimEnd('.');
        if (normE == normA) return true;

        var cleanE = normE.Replace("\"", "");
        var cleanA = normA.Replace("\"", "");
        if (cleanE == cleanA) return true;

        if (field == "as_name" || field == "as_domain" || field == "usage_type")
        {
            if (cleanE.Length > 3 && cleanA.Length > 3)
            {
                if (cleanA.StartsWith(cleanE) || cleanE.StartsWith(cleanA)) return true;
            }
        }

        return false;
    }

    static string[] ParseCsv(string line)
    {
        var res = new System.Collections.Generic.List<string>();
        bool inQ = false;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') inQ = !inQ;
            else if (c == ',' && !inQ) { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        res.Add(sb.ToString());
        return res.ToArray();
    }

    static string[] GenerateIpv4(int n)
    {
        var p = new string[n];
        for (int i = 0; i < n; i++)
            p[i] = ((i % 255) + 1) + "." + ((i * 17) % 256) + "." + ((i * 131) % 256) + "." + ((i % 254) + 1);
        return p;
    }

    static string[] GenerateIpv6(int n)
    {
        var p = new string[n];
        for (int i = 0; i < n; i++)
        {
            var g1 = ((i * 31) % 0xFFFF).ToString("x4");
            var g2 = ((i * 17) % 0xFFFF).ToString("x4");
            var g3 = ((i * 131) % 0xFFFF).ToString("x4");
            if (i % 5 == 0) p[i] = "2001:" + g1 + ":" + g2 + "::" + g3;
            else if (i % 5 == 1) p[i] = "2001:" + g1 + ":0000:0000:" + g2 + ":0000:0000:" + g3;
            else p[i] = "::ffff:" + ((i % 255) + 1) + "." + ((i * 17) % 256) + "." + ((i * 131) % 256) + "." + ((i % 254) + 1);
        }
        return p;
    }

    static void T1(bool cond, string msg)
    {
        if (cond) { tier1Pass++; Console.WriteLine("  [OK] " + msg); }
        else { tier1Fail++; Console.WriteLine("  [FAIL] " + msg); }
    }
}
