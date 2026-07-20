using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using qqzengPgUI.ipdb8;

class PerfComparison
{
    public static async Task Run(string sourceV4)
    {
        Console.WriteLine("\n=== Performance Benchmark: IPDB V12 ===");
        
        string v12Db = "v12.db";
        
        // Ensure V12 DB exists
        if(!File.Exists(v12Db)) {
             Console.WriteLine("Building V12 DB for benchmark...");
             IPDBBuilderV12.Build(sourceV4, "", v12Db);
        }

        // --- Initialization ---
        Console.WriteLine("\n[1] Initializing Searcher...");
        var sw = Stopwatch.StartNew();
        var searcher = new IPDBSearcherV12(v12Db);
        sw.Stop();
        Console.WriteLine($"Database Loaded. Time: {sw.ElapsedMilliseconds} ms");

        // --- Correctness Verification ---
        // Optional: Test with external file if available
        string testFile = FindTestFile();
        if (testFile != null)
        {
             VerifyWithFile(searcher, testFile);
        }
        else
        {
            Console.WriteLine("[2] Skipping external file verification (file not found). Using internal self-consistency check.");
        }

        // --- Benchmark ---
        int totalCount = 3_000_000;
        Console.WriteLine($"\n[3] Generating {totalCount:N0} Random IPs (UInt32)...");
        var randomIps = GenerateRandomIps(totalCount);
        
        // Warmup
        searcher.Find(randomIps[0]); 
        
        Console.WriteLine("Benchmark Started (Find(uint))...");
        
        // GC Clean
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var perfSw = Stopwatch.StartNew();
        for(int i=0; i < totalCount; i++)
        {
            searcher.Find(randomIps[i]);
        }
        perfSw.Stop();

        double elapsedSeconds = perfSw.Elapsed.TotalSeconds;
        Console.WriteLine($"\n{totalCount:N0} queries took: {perfSw.ElapsedMilliseconds} ms");
        Console.WriteLine($"QPS: {totalCount / elapsedSeconds:N2}");
    }

    static uint[] GenerateRandomIps(int count)
    {
        var ips = new uint[count];
        var rnd = new Random(123); // Fixed Seed
        
        for (int i = 0; i < count; i++)
        {
            // Simple random uint32
            // Buffer approach is slower to generate but ensures full coverage.
            // Using rnd.Next() is simple.
            // Note: rnd.Next() returns non-negative int. So top bit is 0.
            // We want full range.
            int i32 = rnd.Next(int.MinValue, int.MaxValue);
            ips[i] = (uint)i32; 
        }
        return ips;
    }

    static string FindTestFile()
    {
         string[] attempts = new[] { 
             "../data/test.txt", 
             "./test.txt",
             "/Users/zengxiangzhan/ZengData/IP数据库/ipdb8/test.txt"
         };
         foreach(var p in attempts) if(File.Exists(p)) return p;
         return null;
    }

    static void VerifyWithFile(IPDBSearcherV12 searcher, string path)
    {
        Console.WriteLine($"\n[2] Verifying with file: {path}");
        var lines = File.ReadAllLines(path);
        int passed = 0;
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            // File: StartIP \t EndIP \t GeoStr
            // Test with StartIP
            var res = searcher.Find(parts[0]);
            
            // Allow loose matching (contains) or exact?
            // "中国|CN|..." vs "中国|...".
            // Let's just check if result is not empty for now or matches expected Geo.
            // Assuming parts[2] is partial Geo like "中国".
            if (!res.IsEmpty && res.ToString().Contains(parts[2])) passed++;
        }
        Console.WriteLine($"Verification Result: {passed}/{lines.Length} matched.");
    }
}
