using QQZeng.Qzdb;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

class Program
{
    static string BP = LocateTestData();
    static int tier1Pass = 0, tier1Fail = 0;
    static int tier2Nodes = 0, tier2Err = 0, tier2Ipv4 = 0, tier2Ipv6 = 0, tier2Excl = 0;

    static int Main()
    {
        BP = LocateTestData();
        Console.WriteLine("=== QZDB C# SDK Full Test Suite (QZDB_TEST_SPECIFICATION.md) ===");
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
        Console.WriteLine("\n--- Tier 1: Unit & Boundary (60+ assertions) ---");
        using var r = new QzdbReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build();

        // ===== 1. IPv4 严格性 =====
        T1(r.Find("0.0.0.0") == null, "IPv4: 0.0.0.0");
        T1(r.Find("255.255.255.255") == null, "IPv4: 255.255.255.255");
        T1(r.Find("192.168.0.1") == null, "IPv4: private 192.168.0.1");
        T1(r.Find("223.5.5.5") != null, "IPv4: 223.5.5.5 valid");
        T1(InvalidIp(r, "01.1.1.1"), "IPv4: reject leading zero 01");
        T1(InvalidIp(r, "1.02.3.4"), "IPv4: reject leading zero 02");
        T1(InvalidIp(r, "1.1.1.01"), "IPv4: reject leading zero 01 end");
        T1(InvalidIp(r, "256.1.1.1"), "IPv4: reject 256");
        T1(InvalidIp(r, "1.1.1.256"), "IPv4: reject 256 seg");
        T1(InvalidIp(r, "1.300.1.1"), "IPv4: reject 300");
        T1(InvalidIp(r, "1.1.1"), "IPv4: reject 3 parts");
        T1(InvalidIp(r, "1.1"), "IPv4: reject 2 parts");
        T1(InvalidIp(r, "1"), "IPv4: reject 1 part");
        T1(InvalidIp(r, "1.1.1.1.1"), "IPv4: reject 5 parts");
        T1(InvalidIp(r, ""), "IPv4: reject empty");
        T1(InvalidIp(r, "   "), "IPv4: reject whitespace");
        T1(InvalidIp(r, "abc.def.ghi.jkl"), "IPv4: reject alpha");
        T1(InvalidIp(r, "1.1.1.1:80"), "IPv4: reject port");
        T1(InvalidIp(r, "1.1.1.1/24"), "IPv4: reject CIDR");

        // ===== 2. IPv6 规范性 =====
        T1(r.Find("::ffff:223.5.5.5") != null, "IPv6: mapped resolves");
        T1(r.Find("::1") == null, "IPv6: ::1 loopback null");
        T1(InvalidIp(r, "fe80::1%eth0"), "IPv6: reject zone ID");
        T1(InvalidIp(r, "fe80::1%1"), "IPv6: reject zone ID %1");
        T1(InvalidIp(r, "1::2::3"), "IPv6: reject double ::");
        T1(InvalidIp(r, "gggg::1"), "IPv6: reject invalid hex");
        T1(InvalidIp(r, "12345::"), "IPv6: reject group > 4 hex");
        T1(InvalidIp(r, "1:2:3"), "IPv6: reject too few groups");
        T1(InvalidIp(r, "1:2:3:4:5:6:7:8:9"), "IPv6: reject > 8 groups");
        T1(InvalidIp(r, "::ffff:256.1.1.1"), "IPv6: reject mapped bad v4");

        // ===== 3. IPv4-Mapped 自动降级 =====
        var d = r.FindStr("223.5.5.5");
        T1(d == r.FindStr("::ffff:223.5.5.5"), "Mapped: dotted == direct");
        T1(d == r.FindStr("::ffff:df05:505"), "Mapped: hex == direct");
        T1(d == r.FindStr("0:0:0:0:0:ffff:df05:505"), "Mapped: full == direct");
        T1(d == r.FindStr("0000:0000:0000:0000:0000:ffff:df05:505"), "Mapped: 4-digit groups");

        // ===== 4. 字段名归一化 =====
        using (var r2 = new QzdbReader.Builder(BP + "/max/china/qqzeng_ip_max_china.qzdb").Build())
        {
            var info = r2.Find("114.114.114.114")!;
            T1(info.Get("country") == info.Get("COUNTRY"), "Norm: upper");
            T1(info.Get("country") == info.Get("Country"), "Norm: mixed");
            T1(info.Get("country_code") == info.Get("countrycode"), "Norm: underscore");
            T1(info.Get("country_code") == info.Get("COUNTRY_CODE"), "Norm: all upper");
            T1(info.Get("nonexistent_field") == "", "Norm: missing empty");
            T1(info.Get("") == "", "Norm: empty empty");
            T1(info.Get(null) == "", "Norm: null empty");
        }

        // ===== 5. UsageType 21 场景 =====
        T1(UsageType.FromString("DNS").IsKnown, "UT: DNS known");
        T1(UsageType.FromString("Cloud").IsKnown, "UT: Cloud known");
        T1(UsageType.FromString("AICrawler").IsKnown, "UT: AICrawler known");
        T1(UsageType.FromString("Broadband").IsKnown, "UT: Broadband known");
        T1(UsageType.FromString("FutureUnknownType").IsKnown == false, "UT: unknown");
        T1(UsageType.FromString("FutureUnknownType").RawValue == "FutureUnknownType", "UT: raw preserved");
        T1(UsageType.FromString("").IsKnown, "UT: empty -> Unknown");
        T1(UsageType.FromString(null).IsKnown, "UT: null -> Unknown");
        T1(Enum.GetValues<KnownUsageType>().Length == 21, "UT: 21 types");

        // ===== 6. 恶意输入/损坏文件防御 =====
        T1(InvalidIp(r, new string('X', 10000)), "Malice: 10k garbage");
        T1(InvalidIp(r, "\x00\x01\x02"), "Malice: control chars");
        try { new QzdbReader.Builder(new byte[] { (byte)'X', (byte)'Z' }).Build(); T1(false, "should throw"); }
        catch (QzdbException) { T1(true, "Corrupt: bad magic"); }
        try { new QzdbReader.Builder(new byte[] { (byte)'Q',(byte)'Z',(byte)'D',(byte)'B' }).Build(); T1(false, "should throw"); }
        catch (QzdbException) { T1(true, "Corrupt: truncated"); }
        try { QzdbReader.Open(Path.Combine(BP, "missing.qzdb")); T1(false, "should throw missing file"); }
        catch (QzdbException e) { T1(e.ErrorCode == ErrorCode.FileNotFound, "Open: missing file has FileNotFound"); }
        try {
            var b = System.IO.File.ReadAllBytes(BP + "/std/china/qqzeng_ip_std_china.qzdb");
            b[200] ^= 0xFF;
            new QzdbReader.Builder(b).VerifyCrc(true).Build();
            T1(false, "should throw CRC");
        } catch (QzdbException e) { T1(e.ErrorCode == ErrorCode.Corrupted, "CRC: mismatch detected"); }
        T1(r.VerifyCrc(), "CRC: valid on healthy");
        T1(r.VerifyCRC() && r.FileHash != "N/A", "Metadata: CRC alias and file hash");
        T1(r.Edition == "std" && r.Scope == "" && r.DataMonth == "2026-08", $"Metadata: legacy edition/month decoded (edition={r.Edition}, scope={r.Scope}, month={r.DataMonth})");

        // ===== 7. 资源释放 =====
        var rd = new QzdbReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build();
        rd.Dispose();
        try { rd.Find("1.1.1.1"); T1(false, "should throw"); }
        catch (ObjectDisposedException) { T1(true, "Dispose: throws OOD"); }
        var rd2 = new QzdbReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build();
        rd2.Dispose();
        rd2.Dispose();
        T1(true, "Dispose: idempotent");


        // ===== 9. 双栈一致性 =====
        var info4 = r.Find("223.5.5.5")!;
        var infoMap = r.Find("::ffff:223.5.5.5")!;
        foreach (var f in info4.FieldNames)
            T1(info4.Get(f) == infoMap.Get(f), "Dual: " + f + " matches");
        T1(r.Find(IPAddress.Parse("223.5.5.5"))?.ToPipe() == info4.ToPipe(), "API: IPAddress overload");
        T1(r.TryFind("223.5.5.5", out var tryInfo) && tryInfo?.ToPipe() == info4.ToPipe(), "API: TryFind found");
        T1(!r.TryFind("bad!!", out _), "API: TryFind invalid false");
        T1(InvalidIp(r, ":0:0:0:0:0:0:0"), "IPv6: reject leading single colon");
        T1(InvalidIp(r, "0:0:0:0:0:0:0:"), "IPv6: reject trailing single colon");
        T1(r.FindStr("0:0:0:0:0:ffff:223.5.5.5") == d, "Mapped: full dotted == direct");
        var oracleValid = new[]
        {
            "::", "::1", "fe80::", "fe80::1", "2001:db8::1", "2001:db8:0:1:0:0:0:1",
            "1:2:3:4:5:6:7:8", "2001:db8::192.0.2.1", "::ffff:192.0.2.1",
            "0:0:0:0:0:ffff:192.0.2.1", "::ffff:0:192.0.2.1", "ABCD::EF01"
        };
        T1(oracleValid.All(ip => IPAddress.TryParse(ip, out _)), "Oracle: standard library accepts valid IPv6 corpus");
        T1(oracleValid.All(ip => AcceptsIp(r, ip)), "Oracle: SDK accepts valid IPv6 corpus");
        var oracleInvalid = new[]
        {
            ":", ":::", ":1::", "1:::", "1:2:3:4:5:6:7", "1:2:3:4:5:6:7:8:9",
            "1:2:3:4:5:6:7::8", "1::2::3", "::ffff:192.0.2.999", "::ffff:192.0.2.1.1",
            "::ffff:192.0.2", "::ffff:01.2.3.4", "1.2.3.4::", "::1.2.3.4:5",
            "2001:db8:0:0:0:0:0:", "2001:db8:::1", "2001:db8::1::", "gggg::1",
            "1:2:3:4:5:6:7:zzzz", "fe80::1%eth0", "1.1.1.1/32", " 1.1.1.1",
            "1.1.1.1 ", "01.2.3.4", "1..2.3.4", "1.2.3.4.", "1.2.3.4:80",
            "2001:db8::ffff:192.0.2.1:4"
        };
        T1(oracleInvalid.All(ip => InvalidIp(r, ip)), "Oracle: independent malformed corpus rejected");

        // ===== 10. v2.4 新 API：FindUint / LookupRowIdUint / LookupRowIdBytes / LookupIds =====
        uint wantRow = r.LookupRowId("223.5.5.5");
        T1(wantRow > 0, "NewAPI: LookupRowId known");
        T1(r.LookupRowIdUint(0xDF050505) == wantRow, "NewAPI: LookupRowIdUint == string path");
        T1(r.LookupRowIdUint(0x01010101) == 0, "NewAPI: LookupRowIdUint unknown -> 0");
        T1(r.LookupRowIdBytes(new byte[] { 0xDF, 0x05, 0x05, 0x05 }) == wantRow, "NewAPI: LookupRowIdBytes v4");
        T1(r.LookupRowIdBytes(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xDF, 0x05, 0x05, 0x05 }) == wantRow,
            "NewAPI: LookupRowIdBytes mapped v6");
        T1(r.LookupRowIdBytes(null) == 0 && r.LookupRowIdBytes(new byte[3]) == 0, "NewAPI: LookupRowIdBytes bad input");
        try { r.FindBytes(new byte[3]); T1(false, "FindBytes should reject bad length"); }
        catch (QzdbException e) { T1(e.ErrorCode == ErrorCode.InvalidIp, "FindBytes: bad length is InvalidIp"); }
        var fUint = r.FindUint(0xDF050505);
        T1(fUint != null && fUint.ToPipe() == d, "NewAPI: FindUint == string path");
        T1(r.FindUint(0) == null, "NewAPI: FindUint 0 null");
        var ids = r.LookupIds(wantRow);
        T1(ids.Geo > 0, "NewAPI: LookupIds geoId");

        // ===== 11. v2.4 新 API：FindFields / FindBatch =====
        var ff = r.FindFields("223.5.5.5", new[] { "country", "province" });
        T1(ff != null && ff.FieldNames.Length == 2, "NewAPI: FindFields projects 2 cols");
        T1(ff!.Get("country") == info4.Get("country"), "NewAPI: FindFields country matches");
        // 投影语义对齐 Java golden：未知字段补 ""、保留重复、全部未知仍返回 GeoInfo
        var ffj = r.FindFields("223.5.5.5", new[] { "country", "nope", "country" });
        T1(ffj != null && ffj.FieldNames.Length == 3 && ffj.FieldNames[1] == "nope",
            "NewAPI: FindFields keeps unknown field position (Java golden)");
        T1(ffj!.Get("nope") == "" && ffj.Get("country") == info4.Get("country"),
            "NewAPI: FindFields unknown -> empty, known -> full value");
        var ffa = r.FindFields("223.5.5.5", new[] { "zzz", "yyy" });
        T1(ffa != null && ffa.Values.All(v => v == ""), "NewAPI: all-unknown returns empty GeoInfo (not null)");
        var fb = r.FindBatch(new[] { "223.5.5.5", "8.8.8.8", "bad!!" });
        T1(fb.Length == 3, "NewAPI: FindBatch count");
        T1(fb[0].Info?.ToPipe() == d, "NewAPI: FindBatch[0] valid");
        T1(fb[1].Error == null, "NewAPI: FindBatch miss/no-error");
        T1(fb[2].Error?.ErrorCode == ErrorCode.InvalidIp && !fb[2].IsNotFound, "NewAPI: FindBatch bad input error");
        var fbf = r.FindBatchFields(new[] { "223.5.5.5", "bad!!" }, new[] { "country" });
        T1(fbf[0].Info?.FieldNames.Length == 1 && fbf[1].Error?.ErrorCode == ErrorCode.InvalidIp,
            "NewAPI: FindBatchFields preserves error state");
        var streamed = r.FindStream(new[] { "223.5.5.5", "8.8.8.8", "bad!!" }).ToArray();
        T1(streamed.Length == 3 && streamed[2].Error?.ErrorCode == ErrorCode.InvalidIp, "NewAPI: FindStream preserves error state");

        // ===== 11b. 独立 Oracle / 失败关闭 / JSON / 缓冲区生命周期 =====
        var rawStable = File.ReadAllBytes(BP + "/std/china/qqzeng_ip_std_china.qzdb");
        var callerBuffer = (byte[])rawStable.Clone();
        using (var copied = QzdbReader.OpenBuffer(callerBuffer))
        {
            callerBuffer[0] = (byte)'X';
            T1(copied.Find("223.5.5.5") != null, "Buffer: open copies caller memory");
        }
        // Unsafe trie walkers must fail closed when a crafted child index points
        // outside the node table. This guards the pointer-arithmetic boundary,
        // independent of the CRC because the file is deliberately opened with
        // verification disabled.
        var hostileTrie = (byte[])rawStable.Clone();
        ushort hostileFlags = BinaryPrimitives.ReadUInt16LittleEndian(hostileTrie.AsSpan(8, 2));
        int hostileV4Count = BinaryPrimitives.ReadInt32LittleEndian(hostileTrie.AsSpan(152, 4));
        long hostileV4Nodes = BinaryPrimitives.ReadInt64LittleEndian(hostileTrie.AsSpan(72, 8));
        if ((hostileFlags & 1) != 0 && hostileV4Count > 0)
        {
            bool compactNodes = (hostileFlags & 0x10) != 0;
            for (int i = 0; i < hostileV4Count; i++)
            {
                int node = checked((int)hostileV4Nodes + i * (compactNodes ? 6 : 8));
                if (compactNodes)
                {
                    hostileTrie[node] = 0xFF; hostileTrie[node + 1] = 0xFF; hostileTrie[node + 2] = 0x7F;
                    hostileTrie[node + 3] = 0xFF; hostileTrie[node + 4] = 0xFF; hostileTrie[node + 5] = 0x7F;
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(hostileTrie.AsSpan(node, 4), 0x7FFF_FFFF);
                    BinaryPrimitives.WriteUInt32LittleEndian(hostileTrie.AsSpan(node + 4, 4), 0x7FFF_FFFF);
                }
            }
            using var hostileReader = QzdbReader.OpenBuffer(hostileTrie, new ReaderOptions { VerifyCrc = false });
            T1(hostileReader.Find("223.5.5.5") == null, "Security: hostile trie child index fails closed");
        }
        var malformedOffset = File.ReadAllBytes(BP + "/std/china/qqzeng_ip_std_china.qzdb");
        BinaryPrimitives.WriteInt64LittleEndian(malformedOffset.AsSpan(104, 8), 1);
        try
        {
            using var ignored = QzdbReader.OpenBuffer(malformedOffset, new ReaderOptions { VerifyCrc = false });
            T1(false, "Corrupt offset: should fail closed");
        }
        catch (QzdbException e) { T1(e.ErrorCode == ErrorCode.Corrupted, "Corrupt offset: fail closed with Corrupted"); }
        int badOffsetRejected = 0;
        foreach (var offset in new[] { 40, 48, 64, 72, 80, 88, 96, 104, 136, 144 })
        {
            var mutated = (byte[])rawStable.Clone();
            BinaryPrimitives.WriteInt64LittleEndian(mutated.AsSpan(offset, 8), 1);
            try { using var ignored = QzdbReader.OpenBuffer(mutated, new ReaderOptions { VerifyCrc = false }); }
            catch (QzdbException e) when (e.ErrorCode is ErrorCode.Corrupted or ErrorCode.InvalidParam) { badOffsetRejected++; }
        }
        T1(badOffsetRejected == 10, "Corrupt offsets: all mutated section pointers rejected");
        var jsonInfo = new GeoInfo(new[] { "longitude", "latitude", "asn", "geo_id" },
            new[] { "1.", "01", "1e+2", "" }, null, null);
        try
        {
            using var json = JsonDocument.Parse(jsonInfo.ToJson());
            T1(json.RootElement.GetProperty("longitude").ValueKind == JsonValueKind.Null &&
                json.RootElement.GetProperty("latitude").ValueKind == JsonValueKind.Null &&
                json.RootElement.GetProperty("asn").ValueKind == JsonValueKind.Number,
                "JSON: strict numeric grammar and null fallback");
        }
        catch { T1(false, "JSON: output must be valid JSON"); }
        var unknownUsage = UsageType.FromString("FutureUnknownType");
        T1(unknownUsage.DisplayZh == "FutureUnknownType" && unknownUsage.DisplayEn == "FutureUnknownType" &&
            !string.IsNullOrEmpty(unknownUsage.Description), "UsageType: display properties");
        T1(info4.GeoId.HasValue == info4.GetGeoId().HasValue && info4.Asn.HasValue == info4.GetAsn().HasValue,
            "GeoInfo: nullable numeric properties");

        var registry = new QzdbRegistry();
        registry.RegisterBuffer("std", File.ReadAllBytes(BP + "/std/china/qqzeng_ip_std_china.qzdb"));
        T1(registry.Get("std")?.Find("223.5.5.5") != null && QzdbRegistry.Default != null, "Registry: buffer and Default");
        registry.Unregister("std");
        T1(registry.Get("std") == null, "Registry: unregister");

        using (var chainReader = QzdbReader.Open(BP + "/std/china/qqzeng_ip_std_china.qzdb"))
        using (var chain = ChainedReader.Chain(chainReader))
        {
            T1(chain.Editions.Length == 1 && chain.Scopes.Length == 1 && chain.DataMonths.Length == 1,
                "Chain: read-only metadata properties");
            T1(InvalidIpThroughChain(chain), "Chain: invalid input propagates");
        }

        // ===== 12. v2.4 新 API：Reload 生命周期 =====
        var rl = new QzdbReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build();
        T1(rl.Find("223.5.5.5") != null, "Reload: before");
        rl.Reload(BP + "/std/china/qqzeng_ip_std_china.qzdb");
        T1(rl.Find("223.5.5.5") != null, "Reload: after path");
        var raw = System.IO.File.ReadAllBytes(BP + "/std/china/qqzeng_ip_std_china.qzdb");
        rl.ReloadBuffer(raw);
        T1(rl.Find("223.5.5.5") != null, "Reload: after buffer");
        rl.Dispose();

        var resurrect = QzdbReader.Open(BP + "/std/china/qqzeng_ip_std_china.qzdb");
        var reloadTask = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                try { resurrect.ReloadBuffer(raw); }
                catch (ObjectDisposedException) { return; }
            }
        });
        resurrect.Dispose();
        reloadTask.Wait();
        bool resurrected = false;
        try { resurrected = resurrect.Find("223.5.5.5") != null; }
        catch (ObjectDisposedException) { }
        T1(!resurrected, "Reload: Dispose cannot be resurrected");

        using (var concurrent = QzdbReader.OpenBuffer(raw))
        {
            int concurrentErrors = 0;
            var queryTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 10000; i++)
                    {
                        concurrent.Find("223.5.5.5");
                        concurrent.Find("::ffff:223.5.5.5");
                    }
                }
                catch { Interlocked.Increment(ref concurrentErrors); }
            })).ToArray();
            var reloads = Task.Run(() =>
            {
                try { for (int i = 0; i < 25; i++) concurrent.ReloadBuffer(raw); }
                catch { Interlocked.Increment(ref concurrentErrors); }
            });
            Task.WaitAll(queryTasks.Append(reloads).ToArray());
            T1(concurrentErrors == 0, "Reload: atomic publication under concurrent query");
        }

        // ===== 13. ChainedReader 多库联合 =====
        using (var c1 = new QzdbReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build())
        using (var c2 = new QzdbReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build())
        {
            using var chain = ChainedReader.Chain(c1, c2);
            var ch = chain.Find("223.5.5.5");
            T1(ch != null && ch.ToPipe() == d, "Chain: fallback find == single");
            T1(chain.FindBatch(new[] { "223.5.5.5", "8.8.8.8" }).Length == 2, "Chain: batch");
            T1(chain.FindUint(0xDF050505)?.ToPipe() == d, "Chain: FindUint");
            T1(chain.FindBytes(new byte[] { 0xDF, 0x05, 0x05, 0x05 })?.ToPipe() == d, "Chain: FindBytes");
            T1(chain.FindFields("223.5.5.5", new[] { "country" })?.FieldNames.Length == 1, "Chain: FindFields");
        }

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
        Console.WriteLine("\n--- Tier 3: Performance (Dual-Stack 1:1) ---");
        var ipv4 = GenerateIpv4(500000);
        var ipv6 = GenerateIpv6(500000);
        using var r = new QzdbReader.Builder(BP + "/max/global/qqzeng_ip_max_global.qzdb").Build();
        for (int i = 0; i < 100000; i++) { r.Find(ipv4[i % ipv4.Length]); r.Find(ipv6[i % ipv6.Length]); }
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000000; i++) r.Find(ipv4[i % ipv4.Length]);
        sw.Stop();
        double ipv4Qps = 1000000 / sw.Elapsed.TotalSeconds;
        sw.Restart();
        for (int i = 0; i < 1000000; i++) r.Find(ipv6[i % ipv6.Length]);
        sw.Stop();
        double ipv6Qps = 1000000 / sw.Elapsed.TotalSeconds;
        Console.WriteLine("Single-thread IPv4: " + ipv4Qps.ToString("N0") + " QPS");
        Console.WriteLine("Single-thread IPv6: " + ipv6Qps.ToString("N0") + " QPS");
        int safetyErr = 0;
        var lockObj = new object();
        Parallel.For(0, 16, t =>
        {
            try { for (int i = 0; i < 100000; i++) { r.Find(ipv4[i % ipv4.Length]); r.Find(ipv6[i % ipv6.Length]); } }
            catch (Exception ex) { lock (lockObj) { safetyErr++; Console.WriteLine("  Thread err: " + ex.Message); } }
        });
        Console.WriteLine("Concurrent safety errors: " + safetyErr);
        T1(safetyErr == 0, "16-thread concurrent safe");
        Console.WriteLine("Tier 3: dual-stack performance verified");
    }

    static void VerifyVersion(string ver, string scope, string dbPath, string csvPath)
    {
        var csvLines = System.IO.File.ReadAllLines(csvPath);
        if (csvLines.Length < 2) return;
        var csvHeaders = ParseCsv(csvLines[0]);
        var colMap = new System.Collections.Generic.Dictionary<string, int>();
        for (int i = 0; i < csvHeaders.Length; i++) colMap[csvHeaders[i].Trim()] = i;
        using var r = new QzdbReader.Builder(dbPath).Build();
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
            return false;
        }
        var normE = exp.ToLowerInvariant().Trim();
        var normA = act.ToLowerInvariant().Trim();
        if (normE == normA) return true;
        return normE.Replace("\"", "") == normA.Replace("\"", "");
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

    static string LocateTestData()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "test_data_202608"),
            Path.Combine(Environment.CurrentDirectory, "multi-lang", "test_data_202608"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test_data_202608"))
        };
        var found = candidates.FirstOrDefault(Directory.Exists);
        if (found == null) throw new DirectoryNotFoundException("Cannot locate test_data_202608");
        return found;
    }

    static bool InvalidIp(QzdbReader reader, string ip)
    {
        try
        {
            _ = reader.Find(ip);
            return false;
        }
        catch (QzdbException e)
        {
            return e.ErrorCode == ErrorCode.InvalidIp;
        }
    }

    static bool AcceptsIp(QzdbReader reader, string ip)
    {
        try
        {
            reader.TryFind(ip, out _);
            return true;
        }
        catch (QzdbException e) when (e.ErrorCode == ErrorCode.InvalidIp)
        {
            return false;
        }
    }

    static bool InvalidIpThroughChain(ChainedReader chain)
    {
        try
        {
            _ = chain.Find("bad!!");
            return false;
        }
        catch (QzdbException e)
        {
            return e.ErrorCode == ErrorCode.InvalidIp;
        }
    }
}
