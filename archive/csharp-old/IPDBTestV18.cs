using System;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace qqzengPgUI.ipdb8
{
    public class IPDBTestV18
    {
        const string VER = "V18.0";

        static readonly (string Label, string DbPath)[] Targets = new[]
        {
            ("std global",   "qqzeng_ip_std_global.qzdb"),
            ("ult global",   "qqzeng_ip_ult_global.qzdb"),
            ("asn global",   "qqzeng_ip_asn_global.qzdb"),
            ("max global",   "qqzeng_ip_max_global.qzdb"),
            ("std china",    "qqzeng_ip_std_china.qzdb"),
            ("ult china",    "qqzeng_ip_ult_china.qzdb"),
            ("asn china",    "qqzeng_ip_asn_china.qzdb"),
            ("max china",    "qqzeng_ip_max_china.qzdb"),
        };

        public static void Run()
        {
            Console.WriteLine("================================================================");
            Console.WriteLine($"              QZip IPDB {VER} Verification & Benchmark        ");
            Console.WriteLine("================================================================");

            foreach (var (label, dbPath) in Targets)
            {
                if (!File.Exists(dbPath))
                {
                    Console.WriteLine($"[SKIP] {label}: {dbPath} not found");
                    continue;
                }

                var fi = new FileInfo(dbPath);
                Console.WriteLine($"\n[{label}] Loading ({fi.Length / 1024.0 / 1024.0:F2} MB)...");
                var searcher = new IPDBSearcherV18(dbPath, true);
                Console.WriteLine($"  Signature: QZ18, GeoCount: {searcher.GeoCount}");

                var v4RndIps = new uint[1_000_000];
                var v6RndIps = new IPAddress[500_000];
                var rnd = new Random(42);
                for (int i = 0; i < v4RndIps.Length; i++)
                    v4RndIps[i] = (uint)rnd.Next();
                for (int i = 0; i < v6RndIps.Length; i++)
                {
                    var buf = new byte[16];
                    rnd.NextBytes(buf);
                    buf[0] = 0x20;
                    v6RndIps[i] = new IPAddress(buf);
                }

                // warmup
                searcher.Find(v4RndIps[0]);
                searcher.FindV6(v6RndIps[0]);

                var knownIPs = new[] {
                    "8.8.8.8", "114.114.114.114", "1.1.1.1",
                    "223.5.5.5", "180.101.49.10", "119.29.29.29",
                };
                Console.WriteLine("  Known IPs:");
                foreach (var ip in knownIPs)
                {
                    var info = searcher.Find(ip);
                    Console.WriteLine($"    {ip,-15} -> {info.Country,-8} {info.Province,-6} {info.ISP}");
                }

                // Known V6
                var v6Known = new[] { "2001:4860:4860::8888", "2400:3200::1", "2606:4700:4700::1111" };
                Console.WriteLine("  Known V6:");
                foreach (var ip in v6Known)
                {
                    var addr = IPAddress.Parse(ip);
                    var info = searcher.FindV6(addr);
                    Console.WriteLine($"    {ip,-30} -> {info.Country,-8} {info.Province,-6} {info.ISP}");
                }

                // ---- V4 benchmark ----
                GC.Collect(); GC.WaitForPendingFinalizers();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < v4RndIps.Length; i++) searcher.Find(v4RndIps[i]);
                sw.Stop();
                double v4_qps = v4RndIps.Length / sw.Elapsed.TotalSeconds / 1_000_000.0;
                Console.WriteLine($"  V4 QPS: {v4_qps:F2} M/s ({(v4RndIps.Length / sw.Elapsed.TotalSeconds) / 1_000_000:F2}M)");

                // ---- V6 benchmark ----
                GC.Collect(); GC.WaitForPendingFinalizers();
                sw.Restart();
                for (int i = 0; i < v6RndIps.Length; i++) searcher.FindV6(v6RndIps[i]);
                sw.Stop();
                double v6_qps = v6RndIps.Length / sw.Elapsed.TotalSeconds / 1_000_000.0;
                Console.WriteLine($"  V6 QPS: {v6_qps:F2} M/s ({(v6RndIps.Length / sw.Elapsed.TotalSeconds) / 1_000_000:F2}M)");
            }

            Console.WriteLine("\n================================================================");
            Console.WriteLine($"              {VER} Test Complete                           ");
            Console.WriteLine("================================================================");
        }
    }
}
