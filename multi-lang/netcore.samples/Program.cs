using QQZeng.Qzdb;
using System.Globalization;
using System.Diagnostics;

class Program
{
    static string? FindDb()
    {
        foreach (var c in new[] {
            "test_data_202608/std/china/qqzeng_ip_std_china.qzdb",
            "../test_data_202608/std/china/qqzeng_ip_std_china.qzdb",
            "../../test_data_202608/std/china/qqzeng_ip_std_china.qzdb",
            "multi-lang/test_data_202608/std/china/qqzeng_ip_std_china.qzdb",
            "../data/qqzeng_ip_std_china.qzdb",
        })
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    static void Main(string[] args)
    {
        var dbPath = args.Length > 0 ? args[0] : FindDb();
        if (dbPath == null)
        {
            Console.WriteLine("Database file not found");
            return;
        }

        using var reader = new QzdbReader.Builder(dbPath).Build();

        Console.WriteLine($"Version: {reader.Version}");
        Console.WriteLine($"Edition: {reader.Edition}");
        Console.WriteLine($"DataMonth: {reader.DataMonth}");
        Console.WriteLine($"Fields: {string.Join(", ", reader.FieldNames)}");
        Console.WriteLine();

        foreach (var ip in new[] { "114.114.114.114", "223.5.5.5", "8.8.8.8" })
        {
            var info = reader.Find(ip);
            Console.WriteLine($"Find(\"{ip}\") => {(info != null ? info.ToPipe() : "null")}");
        }

        Console.WriteLine($"\nFind(\"2408:8000:9000::1\") => {reader.FindStr("2408:8000:9000::1")}");
        Console.WriteLine($"LookupRowId(\"223.5.5.5\") => {reader.LookupRowId("223.5.5.5")}");

        // Perf benchmark
        const int iterations = 1_000_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            reader.Find("223.5.5.5");
        }
        sw.Stop();
        double qps = iterations / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"\nPerf: {qps:N0} QPS (single-thread, {sw.Elapsed.TotalSeconds:F2}s for {iterations:N0} ops)");
        Console.WriteLine($"TEST_PASS");
    }
}
