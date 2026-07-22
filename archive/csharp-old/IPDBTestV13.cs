using System;
using System.Diagnostics;
using System.IO;
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace qqzengPgUI.ipdb8
{
    public class IPDBTestV13
    {
        public static void Run()
        {
            Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                 IPDB V13 严谨校验与性能测试                    ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════╝");

            string txtPath = "/Users/zengxiangzhan/ZengData/IP数据库/ipdb8/ipv4.txt";
            string v6Path = "dummy_v6.txt"; 
            string db13Path = "v13.db";

            // 1. 构建
            Console.WriteLine("\n[阶段 1] 构建数据库...");
            IPDBBuilderV13.Build(txtPath, v6Path, db13Path);
            Console.WriteLine($"数据库生成完毕: {new FileInfo(db13Path).Length/1024.0/1024.0:F2} MB");

            // 2. 加载 (默认开启 CRC 校验)
            var searcher = new IPDBSearcherV13(db13Path, true);
            Console.WriteLine($"[Meta] Version: {searcher.Version}");
            Console.WriteLine($"[Meta] Created: {searcher.CreationDate:yyyy-MM-dd}");
            Console.WriteLine($"[Meta] GeoCount: {searcher.GeoCount}");
            
            // 2.1 演示 Reload
            IPDBSearcherV13.Reload(db13Path);
            Console.WriteLine("[System] Reload success.");

            // 3. 数据正确性校验 (重点)
            Console.WriteLine("\n[阶段 2] 数据正确性校验 (Data Validation)...");
            bool passed = true;

            // 3.1 知名 IP 校验
            Console.WriteLine("1. 知名 IP 检查:");
            CheckIP(searcher, "8.8.8.8", "Google", ref passed);
            CheckIP(searcher, "114.114.114.114", "114", ref passed);
            CheckIP(searcher, "1.1.1.1", "Cloudflare", ref passed); 
            CheckIP(searcher, "223.5.5.5", "阿里云", ref passed);

            // 3.2 随机一致性校验 (比较 String 接口和 UInt 接口)
            Console.WriteLine("\n2. 接口一致性检查 (String vs UInt) - 抽样 10000 次:");
            var rnd = new Random(123456);
            int sampleCount = 10000;
            int mismatchCount = 0;
            for(int i=0; i<sampleCount; i++)
            {
                byte[] b = new byte[4];
                rnd.NextBytes(b);
                // 构造 IP 字符串
                string ipStr = $"{b[0]}.{b[1]}.{b[2]}.{b[3]}";
                // 构造 UInt (BigEndian for helper simplicity, but searcher handles it)
                // Searcher.Find(string) uses IsIPv4Fast which does correct conversion
                
                var resStr = searcher.Find(ipStr);
                
                uint ipUInt = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
                var resInt = searcher.Find(ipUInt);

                if (resStr.ToString() != resInt.ToString())
                {
                    mismatchCount++;
                    if (mismatchCount < 5) 
                        Console.WriteLine($"[Error] 不一致: IP={ipStr}. StrRes={resStr.Country}, IntRes={resInt.Country}");
                }
            }

            if (mismatchCount == 0) Console.WriteLine($"  => 通过: {sampleCount} 次随机对比全部一致。");
            else {
                Console.WriteLine($"  => 失败: 发现 {mismatchCount} 次不一致！");
                passed = false;
            }

            if (!passed)
            {
                Console.WriteLine("\n[严重错误] 数据校验未通过，终止性能测试。");
                return;
            }
            Console.WriteLine("\n[校验通过] 数据逻辑正确，准备开始性能轰炸...");
            System.Threading.Thread.Sleep(1000);

            // 4. 性能测试
            Console.WriteLine("\n[阶段 3] 极限性能测试 (Performance)...");

            // IPv4 Benchmark
            int testCount = 10_000_000;
            var ips = new uint[testCount];
            for(int i=0; i<testCount; i++) ips[i] = (uint)rnd.Next();
            
            // Warmup
            searcher.Find(ips[0]);
            
            var sw = Stopwatch.StartNew();
            for(int i=0; i<testCount; i++) searcher.Find(ips[i]);
            sw.Stop();
            
            double qps = testCount / (sw.ElapsedMilliseconds / 1000.0) / 1000000.0;
            Console.WriteLine($"IPv4 QPS: {qps:F2} M/s (1000万次随机查询)");

            // IPv6 Benchmark
            int v6Count = 1_000_000;
            var v6ips = new IPAddress[v6Count];
            var v6Base = IPAddress.Parse("2001:4860:4860::8888").GetAddressBytes();
            for(int i=0; i<v6Count; i++)
            {
                v6Base[15] = (byte)rnd.Next(255);
                v6ips[i] = new IPAddress(v6Base);
            }

            sw.Restart();
            for(int i=0; i<v6Count; i++) searcher.FindV6(v6ips[i]);
            sw.Stop();
            
            double v6qps = v6Count / (sw.ElapsedMilliseconds / 1000.0) / 1000000.0;
            Console.WriteLine($"IPv6 QPS: {v6qps:F2} M/s (100万次随机查询)");

            // 6. 演示部分数据构建 (Subset Build)
            Console.WriteLine("\n[阶段 6] 演示：生成仅中国版数据库 (China Only Build)");
            string cnDbPath = "v13_cn.db";
            
            // 定义过滤器：仅保留 Country == "中国"
            // parts: [大洲, 国家, 省份, 城市, 区县, 运营商, AreaCode, EnName, Code, Lng, Lat]
            // 索引: 0      1     2     3     4     5       6         7       8     9    10
            IPDBBuilderV13.Build(txtPath, v6Path, cnDbPath, (parts) => 
            {
                if (parts.Length > 1 && parts[1] == "中国") return true;
                return false;
            });

            long lenFull = new FileInfo(db13Path).Length;
            if (File.Exists(cnDbPath))
            {
                long lenCn = new FileInfo(cnDbPath).Length;
                double saving = (lenFull - lenCn) * 100.0 / lenFull;

                Console.WriteLine($"全量版体积: {lenFull / 1024.0 / 1024.0:F2} MB");
                Console.WriteLine($"中国版体积: {lenCn / 1024.0 / 1024.0:F2} MB (节省 {saving:F1}%)");

                // 验证中国版
                var sCn = new IPDBSearcherV13(cnDbPath);
                var r1 = sCn.Find("114.114.114.114"); // 中国 IP
                var r2 = sCn.Find("8.8.8.8");         // 美国 IP

                Console.WriteLine($"[CN DB] 114.114.114.114 -> {(r1.IsEmpty ? "Empty" : r1.ToString())} (期望: 有数据)");
                Console.WriteLine($"[CN DB] 8.8.8.8         -> {(r2.IsEmpty ? "Empty" : r2.Country)} (期望: Empty)");
            }
        }

        static void CheckIP(IPDBSearcherV13 searcher, string ip, string expectKeyword, ref bool passed)
        {
            var info = searcher.Find(ip);
            string result = info.ToString();
            // 简单模糊匹配
            if (result.Contains(expectKeyword))
            {
                Console.WriteLine($"  [Pass] {ip,-15} -> {info.Country} | {info.ISP}");
            }
            else
            {
                Console.WriteLine($"  [Fail] {ip,-15} -> {info.Country} | {info.ISP} (期望包含: {expectKeyword})");
                passed = false;
            }
        }
    }
}
