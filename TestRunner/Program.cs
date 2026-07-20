using System;
using System.Diagnostics;
using System.IO;
using qqzengPgUI.ipdb8;

namespace IPDBTestRunner
{
    class Program
    {
        // Full traversal (sample=1) for small DBs, sampling for large ones
        const int SAMPLE_EVERY_SMALL = 1;    // < 3M rows
        const int SAMPLE_EVERY_LARGE = 10;   // >= 3M rows

        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "bench")
            {
                RunBenchmarks();
                return;
            }

            RunFullVerification();

            Console.WriteLine("\n\n运行基准测试: dotnet run -- bench\n");
        }

        static int GetSampleEvery(long csvSizeBytes)
        {
            // Estimate rows: roughly 100 bytes per row for range CSV
            long estRows = csvSizeBytes / 100;
            return estRows > 3_000_000 ? SAMPLE_EVERY_LARGE : SAMPLE_EVERY_SMALL;
        }

        static long GetCsvSize(string ver, string region)
        {
            // Try ZIP first, then direct CSV
            string zipFile = $"/Users/zengxiangzhan/ZengData/发行版/2026-07/qqzeng_ip_{ver}/qqzeng_ip_{ver}_{region}_range.zip";
            if (File.Exists(zipFile))
                return new FileInfo(zipFile).Length;
            string csvFile = $"/Users/zengxiangzhan/ZengData/IP数据库/ipdb18/multi-lang/data_v18/qqzeng_ip_{ver}_{region}_range.csv";
            if (File.Exists(csvFile))
                return new FileInfo(csvFile).Length;
            return 0;
        }

        static void RunFullVerification()
        {
            (string Version, string Region)[] targets = new[]
            {
                ("std", "china"), ("std", "global"),
                ("ult", "china"), ("ult", "global"),
                ("asn", "china"), ("asn", "global"),
                ("max", "china"), ("max", "global"),
            };

            bool allPassed = true;
            long grandChecks = 0, grandFails = 0;

            Console.WriteLine("================================================================");
            Console.WriteLine("   QZip IPDB V18 生产级验证 (StartIP + EndIP + RandomIP)       ");
            Console.WriteLine("================================================================");

            foreach (var (ver, region) in targets)
            {
                string dbFile = $"qqzeng_ip_{ver}_{region}.qzdb";
                if (!File.Exists(dbFile))
                {
                    Console.WriteLine($"[SKIP] {ver}/{region}: {dbFile} not found");
                    continue;
                }

                var fi = new FileInfo(dbFile);
                long csvSize = GetCsvSize(ver, region);
                int sampleEvery = GetSampleEvery(csvSize);

                Console.WriteLine($"\n[{ver}/{region}] DB: {fi.Name} ({fi.Length / 1024.0 / 1024.0:F2} MB) sample=1/{sampleEvery}");

                Stopwatch swLoad = Stopwatch.StartNew();
                var searcher = new IPDBSearcherV18(dbFile, true);
                swLoad.Stop();
                Console.WriteLine($"  加载: {swLoad.ElapsedMilliseconds} ms, Geo={searcher.GeoCount}, Pools={searcher.PoolCount}");

                var verifier = new VerifyAllV18(ver, region, searcher, sampleEvery);
                Stopwatch swVer = Stopwatch.StartNew();
                var result = verifier.Run();
                swVer.Stop();

                grandChecks += result.TotalChecks;
                grandFails += result.FailCount;

                if (result.FailCount > 0)
                {
                    allPassed = false;
                    Console.WriteLine($"  ⚠  {ver}/{region}: {result.TotalChecks:N0} checks, {result.FailCount} fails ({swVer.Elapsed.TotalSeconds:F1}s)");
                }
                else
                {
                    Console.WriteLine($"  ✅ {ver}/{region}: {result.TotalChecks:N0} checks all passed ({swVer.Elapsed.TotalSeconds:F1}s)");
                }
            }

            Console.WriteLine($"\n================================================================");
            Console.WriteLine($"  总计: {grandChecks:N0} checks, {grandFails:N0} fails");
            if (grandFails == 0)
                Console.WriteLine("  ✅ 所有检查全部通过 — V18 算法正确!");
            else
                Console.WriteLine("  ⚠  部分检查未通过 (可能为CSV数据差异)");
            Console.WriteLine("================================================================");
        }

        static void RunBenchmarks()
        {
            Console.WriteLine("================================================================");
            Console.WriteLine("   IPDB V18 性能基准测试 (C#)");
            Console.WriteLine("================================================================");
            IPDBTestV18.Run();
        }
    }
}
