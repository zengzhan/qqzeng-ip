using System;

namespace qqzengIp
{
    /// <summary>
    /// qqzeng-ip 6.0 (.db) C# 版冒烟示例
    /// 用法: dotnet run -c Release -p:TargetFramework=net10.0 -- [db_path] [ip ...]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
#if HAS_VERIFY
            // --verify <db> <vec> <mode> [step] [mustMiss]: 供内部验证编排调用
            if (args.Length > 1 && args[0] == "--verify")
            {
                var rest = new string[args.Length - 1];
                Array.Copy(args, 1, rest, 0, rest.Length);
                return Verify.Run(rest);
            }
#endif

            IpDbSearch db;
            if (args.Length > 0)
            {
                db = new IpDbSearch(args[0]);
            }
            else
            {
                db = IpDbSearch.Instance;
            }
            Console.WriteLine($"数据库加载完成: nodes={db.NodeCount}, strings={db.StringCount}");

            string[] demo = { "1.10.11.12", "8.8.8.8", "58.213.198.68", "255.255.255.255", "1..2.3" };
            foreach (string ip in demo)
            {
                Console.WriteLine($"{ip} -> {db.Find(ip)}");
            }
            for (int i = 1; i < args.Length; i++)
            {
                Console.WriteLine($"{args[i]} -> {db.Find(args[i])}");
            }

            // 快速压测
            long seed = 123456789;
            long checksum = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int n = 1000000;
            for (int i = 0; i < n; i++)
            {
                seed = seed * 48271L % 2147483647L;
                checksum += db.Find((uint)seed).Length;
            }
            sw.Stop();
            Console.WriteLine($"bench: {n} queries, {n / (sw.Elapsed.TotalMilliseconds / 1000):F0} qps (checksum {checksum})");
            return 0;
        }
    }
}
