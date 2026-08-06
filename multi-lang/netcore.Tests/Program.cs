using Qzdb;
using System.Diagnostics;

class Program
{
    static int passed = 0, failed = 0;
    static string BP = "";

    static void Main()
    {
        BP = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "test_data_202608"));

        Run("std china", "std/china", "223.5.5.5", "亚洲|CN|中国|浙江|杭州|阿里云");
        Run("std global", "std/global", "8.8.8.8", "北美洲|US|美国|加利福尼亚州|山景城|谷歌云");
        Run("pro china", "pro/china", "114.114.114.114", null);
        Run("pro global", "pro/global", "8.8.8.8", null);
        Run("ult china", "ult/china", "114.114.114.114", null);
        Run("ult global", "ult/global", "8.8.8.8", null);
        Run("asn china", "asn/china", "114.114.114.114", null);
        Run("asn global", "asn/global", "8.8.8.8", null);
        Run("max china", "max/china", "223.5.5.5", null);
        Run("max global", "max/global", "8.8.8.8", null);

        Mapped(BP);
        Invalid(BP);
        Concurrent(BP);
        Buffer(BP);

        Console.WriteLine($"\n=== Results: passed={passed} failed={failed} ===");
        Environment.Exit(failed > 0 ? 1 : 0);
    }

    static void Run(string n, string sub, string ip, string? exp)
    {
        try
        {
            using var r = new DatabaseReader.Builder($"{BP}/{sub}/qqzeng_ip_{sub.Replace("/", "_")}.qzdb").Build();
            var res = r.FindStr(ip);
            if (exp != null && res != exp) { Console.WriteLine($"  [FAIL] {n}: got '{res}'"); failed++; return; }
            Console.WriteLine($"  [PASS] {n}: {r.Edition} ({r.FieldNames.Length}f)");
            passed++;
        }
        catch (Exception ex) { Console.WriteLine($"  [FAIL] {n}: {ex.Message}"); failed++; }
    }

    static void Mapped(string bp)
    {
        try
        {
            using var r = new DatabaseReader.Builder($"{bp}/std/china/qqzeng_ip_std_china.qzdb").Build();
            var d = r.FindStr("223.5.5.5");
            var m = r.FindStr("::ffff:223.5.5.5");
            var mh = r.FindStr("0:0:0:0:0:ffff:df05:505");
            var ok = d == m && m == mh && d.Contains("阿里云");
            Console.WriteLine(ok ? "  [PASS] V4-mapped" : $"  [FAIL] V4-mapped: d='{d}' m='{m}' h='{mh}'");
            if (ok) passed++; else failed++;
        }
        catch (Exception ex) { Console.WriteLine($"  [FAIL] Mapped: {ex.Message}"); failed++; }
    }

    static void Invalid(string bp)
    {
        try
        {
            using var r = new DatabaseReader.Builder($"{bp}/std/china/qqzeng_ip_std_china.qzdb").Build();
            string[] bad = { "", "   ", "256.1.1.1", "abc", "01.1.1.1" };
            bool ok = bad.All(ip => r.FindStr(ip) == "");
            Console.WriteLine(ok ? "  [PASS] Invalid IPs" : "  [FAIL] Invalid IPs");
            if (ok) passed++; else failed++;
        }
        catch (Exception ex) { Console.WriteLine($"  [FAIL] Invalid: {ex.Message}"); failed++; }
    }

    static void Concurrent(string bp)
    {
        try
        {
            using var r = new DatabaseReader.Builder($"{bp}/std/china/qqzeng_ip_std_china.qzdb").Build();
            var sw = Stopwatch.StartNew();
            Parallel.For(0, 16, t => { for (int i = 0; i < 100_000; i++) r.Find("223.5.5.5"); });
            sw.Stop();
            double qps = 1_600_000 / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  [PASS] Concurrent: {qps:N0} QPS");
            passed++;
        }
        catch (Exception ex) { Console.WriteLine($"  [FAIL] Concurrent: {ex.Message}"); failed++; }
    }

    static void Buffer(string bp)
    {
        try
        {
            var bytes = File.ReadAllBytes($"{bp}/std/china/qqzeng_ip_std_china.qzdb");
            using var r = new DatabaseReader.Builder(bytes).Build();
            var ok = r.FindStr("223.5.5.5").Contains("阿里云");
            Console.WriteLine(ok ? "  [PASS] Buffer" : "  [FAIL] Buffer");
            if (ok) passed++; else failed++;
        }
        catch (Exception ex) { Console.WriteLine($"  [FAIL] Buffer: {ex.Message}"); failed++; }
    }
}
