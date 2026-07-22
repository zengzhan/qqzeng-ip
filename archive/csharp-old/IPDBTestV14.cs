using System;
using System.Diagnostics;
using System.IO;
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace qqzengPgUI.ipdb8
{
    public class IPDBTestV14
    {
        public static void Run()
        {
            Console.WriteLine("================================================================");
            Console.WriteLine("                 QZip IPDB V14.0 Benchmark & Verification        ");
            Console.WriteLine("================================================================");

            string txtPath = "/Users/zengxiangzhan/ZengData/IP数据库/ipdb8/ipv4.txt";
            if (!File.Exists(txtPath))
            {
                txtPath = "/Users/zengxiangzhan/ZengData/IP数据库/ipdb8/ipv4_partial.txt";
            }
            
            // Create a dummy IPv6 text file to test IPv6 functionality properly
            string v6Path = "dummy_v6.txt"; 
            if (!File.Exists(v6Path))
            {
                var sb = new StringBuilder();
                // 1000 consecutive fake IPv6 prefix records to benchmark IPv6 performance
                var rnd = new Random(42);
                for (int i = 0; i < 1000; i++)
                {
                    string startIp = $"2001:4860:4860:{i:x4}::";
                    string endIp = $"2001:4860:4860:{i:x4}:ffff:ffff:ffff:ffff";
                    
                    IPAddress sAddr = IPAddress.Parse(startIp);
                    IPAddress eAddr = IPAddress.Parse(endIp);
                    
                    System.Numerics.BigInteger sBig = new System.Numerics.BigInteger(sAddr.GetAddressBytes(), true, true);
                    System.Numerics.BigInteger eBig = new System.Numerics.BigInteger(eAddr.GetAddressBytes(), true, true);
                    
                    sb.AppendLine($"{startIp}\t{endIp}\t{sBig}\t{eBig}\t北美洲|美国|加利福尼亚|山景城||Google|06080|United States|US|37.38605|-122.08385");
                }
                File.WriteAllText(v6Path, sb.ToString());
                Console.WriteLine($"[System] Created temporary IPv6 benchmark file: {v6Path}");
            }

            string db13Path = "v13.db";
            string db14Path = "v14.db";

            // 1. 构建 V13 和 V14
            Console.WriteLine("\n[阶段 1] 构建 V13 数据库...");
            IPDBBuilderV13.Build(txtPath, v6Path, db13Path);
            Console.WriteLine($"V13 数据库生成完毕: {new FileInfo(db13Path).Length/1024.0/1024.0:F3} MB");

            Console.WriteLine("\n[阶段 2] 构建 V14.0 数据库...");
            IPDBBuilderV14.Build(txtPath, v6Path, db14Path);
            Console.WriteLine($"V14.0 数据库生成完毕: {new FileInfo(db14Path).Length/1024.0/1024.0:F3} MB");

            // 2. 加载 Searcher
            var s13 = new IPDBSearcherV13(db13Path, true);
            var s14 = new IPDBSearcherV14(db14Path, true);

            Console.WriteLine("\n[Meta Comparison]");
            Console.WriteLine($"V13 Version: {s13.Version}, GeoCount: {s13.GeoCount}");
            Console.WriteLine($"V14 Version: {s14.Version}, GeoCount: {s14.GeoCount}");

            // 3. 数据正确性校验 (V13 vs V14)
            Console.WriteLine("\n[阶段 3] 正确性校验 (Data Consistency Check)...");
            bool passed = true;

            // 3.1 知名 IP 校验
            Console.WriteLine("1. 校验知名 IP 查询结果:");
            CheckIP(s13, s14, "8.8.8.8", "Google", ref passed);
            CheckIP(s13, s14, "114.114.114.114", "114", ref passed);
            CheckIP(s13, s14, "1.1.1.1", "Cloudflare", ref passed); 
            CheckIP(s13, s14, "223.5.5.5", "阿里云", ref passed);

            // 3.2 随机一致性校验 (100,000 次随机 IPv4 对比)
            Console.WriteLine("\n2. IPv4 随机查询一致性检查 - 抽样 100,000 次:");
            var rndGen = new Random(123456);
            int mismatchCount = 0;
            for(int i=0; i<100000; i++)
            {
                byte[] b = new byte[4];
                rndGen.NextBytes(b);
                uint ipUInt = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
                
                var r13 = s13.Find(ipUInt);
                var r14 = s14.Find(ipUInt);

                if (r13.ToString() != r14.ToString())
                {
                    mismatchCount++;
                    if (mismatchCount < 5) 
                        Console.WriteLine($"[Error] IPv4 不一致: IP={b[0]}.{b[1]}.{b[2]}.{b[3]}. V13Res={r13}, V14Res={r14}");
                }
            }

            if (mismatchCount == 0) Console.WriteLine("  => V13 vs V14 IPv4 随机对比 100% 一致。");
            else {
                Console.WriteLine($"  => 失败: 发现 {mismatchCount} 次 IPv4 不一致！");
                passed = false;
            }

            // 3.3 IPv6 一致性校验
            Console.WriteLine("\n3. IPv6 随机查询一致性检查 - 抽样 50,000 次:");
            int mismatchV6Count = 0;
            var v6BaseBytes = IPAddress.Parse("2001:4860:4860::8888").GetAddressBytes();
            for(int i=0; i<50000; i++)
            {
                v6BaseBytes[14] = (byte)rndGen.Next(255);
                v6BaseBytes[15] = (byte)rndGen.Next(255);
                var addr = new IPAddress(v6BaseBytes);
                
                var r13 = s13.FindV6(addr);
                var r14 = s14.FindV6(addr);

                if (r13.ToString() != r14.ToString())
                {
                    mismatchV6Count++;
                    if (mismatchV6Count < 5) 
                        Console.WriteLine($"[Error] IPv6 不一致: IP={addr}. V13Res={r13}, V14Res={r14}");
                }
            }

            if (mismatchV6Count == 0) Console.WriteLine("  => V13 vs V14 IPv6 随机对比 100% 一致。");
            else {
                Console.WriteLine($"  => 失败: 发现 {mismatchV6Count} 次 IPv6 不一致！");
                passed = false;
            }

            if (!passed)
            {
                Console.WriteLine("\n[严重错误] 数据校验未通过，终止性能测试。");
                return;
            }

            Console.WriteLine("\n[校验通过] 数据 100% 一致。开始极限性能对比测试...");
            System.Threading.Thread.Sleep(1000);

            // 4. 性能对比测试
            Console.WriteLine("\n[阶段 4] IPv4 极限性能测试 (1000 万次随机查询)...");
            int testCount = 10_000_000;
            var ips = new uint[testCount];
            for(int i=0; i<testCount; i++) ips[i] = (uint)rndGen.Next();
            
            // Warmup
            s13.Find(ips[0]);
            s14.Find(ips[0]);
            
            // V13 IPv4
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var sw = Stopwatch.StartNew();
            for(int i=0; i<testCount; i++) s13.Find(ips[i]);
            sw.Stop();
            double qps13 = testCount / (sw.Elapsed.TotalSeconds) / 1000000.0;
            
            // V14 IPv4
            GC.Collect();
            GC.WaitForPendingFinalizers();
            sw.Restart();
            for(int i=0; i<testCount; i++) s14.Find(ips[i]);
            sw.Stop();
            double qps14 = testCount / (sw.Elapsed.TotalSeconds) / 1000000.0;

            Console.WriteLine($"V13 IPv4 QPS : {qps13:F2} M/s");
            Console.WriteLine($"V14 IPv4 QPS : {qps14:F2} M/s (提升 {(qps14-qps13)*100.0/qps13:F1}%)");

            Console.WriteLine("\n[阶段 5] IPv6 极限性能测试 (500 万次随机查询)...");
            int testV6Count = 5_000_000;
            var v6Ips = new IPAddress[testV6Count];
            for(int i=0; i<testV6Count; i++)
            {
                v6BaseBytes[14] = (byte)rndGen.Next(255);
                v6BaseBytes[15] = (byte)rndGen.Next(255);
                v6Ips[i] = new IPAddress(v6BaseBytes);
            }

            // Warmup
            s13.FindV6(v6Ips[0]);
            s14.FindV6(v6Ips[0]);

            // V13 IPv6
            GC.Collect();
            GC.WaitForPendingFinalizers();
            sw.Restart();
            for(int i=0; i<testV6Count; i++) s13.FindV6(v6Ips[i]);
            sw.Stop();
            double qps13V6 = testV6Count / (sw.Elapsed.TotalSeconds) / 1000000.0;

            // V14 IPv6
            GC.Collect();
            GC.WaitForPendingFinalizers();
            sw.Restart();
            for(int i=0; i<testV6Count; i++) s14.FindV6(v6Ips[i]);
            sw.Stop();
            double qps14V6 = testV6Count / (sw.Elapsed.TotalSeconds) / 1000000.0;

            Console.WriteLine($"V13 IPv6 QPS : {qps13V6:F2} M/s");
            Console.WriteLine($"V14 IPv6 QPS : {qps14V6:F2} M/s (由于 Eytzinger Suffix 优化，提升 {(qps14V6-qps13V6)*100.0/qps13V6:F1}%)");

            // Clean up temporary IPv6 file
            if (File.Exists(v6Path))
            {
                File.Delete(v6Path);
            }
        }

        static void CheckIP(IPDBSearcherV13 s13, IPDBSearcherV14 s14, string ip, string expectKeyword, ref bool passed)
        {
            var i13 = s13.Find(ip);
            var i14 = s14.Find(ip);
            
            if (i13.ToString() != i14.ToString())
            {
                Console.WriteLine($"  [Fail] {ip,-15} -> V13 res '{i13}' does not match V14 res '{i14}'");
                passed = false;
                return;
            }

            if (i14.ToString().Contains(expectKeyword))
            {
                Console.WriteLine($"  [Pass] {ip,-15} -> {i14.Country} | {i14.ISP}");
            }
            else
            {
                Console.WriteLine($"  [Warn] {ip,-15} -> {i14.Country} | {i14.ISP} (不包含期望关键字 '{expectKeyword}')");
            }
        }
    }
}
