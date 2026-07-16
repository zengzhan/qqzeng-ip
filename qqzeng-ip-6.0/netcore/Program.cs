using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using qqzengIp;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("正在初始化 qqzeng-ip 数据库...");
        var sw = Stopwatch.StartNew();
        var searcher = IpDbSearch.Instance;
        sw.Stop();
        Console.WriteLine($"数据库加载完成，耗时: {sw.ElapsedMilliseconds} ms");

        string testFilePath = FindTestFile();
        if (testFilePath != null)
        {
             VerifyWithFile(searcher, testFilePath);
        }

        // --- 新的随机压测 ---
        int totalCount = 3_000_000;
        Console.WriteLine($"\n生成 {totalCount:N0} 个随机 IP (UInt32)...");
        var randomIps = GenerateRandomIps(totalCount);
        Console.WriteLine("生成完成，开始压测 (Find(uint))...");

        // GC 预清理
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var perfSw = Stopwatch.StartNew();
        for(int i=0; i < totalCount; i++)
        {
            searcher.Find(randomIps[i]);
        }
        perfSw.Stop();

        double elapsedSeconds = perfSw.Elapsed.TotalSeconds;
        Console.WriteLine($"\n{totalCount:N0} 次随机查询耗时: {perfSw.ElapsedMilliseconds} ms");
        Console.WriteLine($"QPS: {totalCount / elapsedSeconds:N2}");
    }

    static uint[] GenerateRandomIps(int count)
    {
        var ips = new uint[count];
        var rnd = new Random(123); // 固定种子
        
        // 简单生成
        for (int i = 0; i < count; i++)
        {
            ips[i] = (uint)rnd.Next(); // 0 to Int32.Max
            if (rnd.Next(2) == 0) ips[i] |= 0x80000000; // 设置最高位，覆盖高段IP
        }
        return ips;
    }

    static string FindTestFile()
    {
         string[] attempts = new[] { 
             "../data/test.txt", 
             "../../data/test.txt", 
             "../../../data/test.txt",
             Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../data/test.txt")
         };
         foreach(var p in attempts) if(File.Exists(p)) return p;
         return null;
    }

    static void VerifyWithFile(IpDbSearch searcher, string path)
    {
        Console.WriteLine($"正在读取测试文件: {path}");
        var lines = File.ReadAllLines(path);
        int passed = 0;
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            // 验证 String 接口
            bool match1 = searcher.Find(parts[0]) == parts[2];
            bool match2 = searcher.Find(parts[1]) == parts[2];
            if (match1 && match2) passed++;
        }
        Console.WriteLine($"校对完成: {passed}/{lines.Length} 通过");
    }
}
