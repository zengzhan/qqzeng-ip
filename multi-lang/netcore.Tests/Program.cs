using Qzdb;
using System.Diagnostics;

class Program
{
    static string BP = "";

    static void Main()
    {
        BP = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "test_data_202608"));

        Console.WriteLine("=== C# .NET 10 SDK Performance Benchmark ===\n");

        // Single-thread benchmark
        using var reader = new DatabaseReader.Builder($"{BP}/std/china/qqzeng_ip_std_china.qzdb").Build();
        const int iterations = 1_000_000;

        // Warmup
        for (int i = 0; i < 100_000; i++) reader.Find("223.5.5.5");

        // Single-thread
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) reader.Find("223.5.5.5");
        sw.Stop();
        double singleQps = iterations / sw.Elapsed.TotalSeconds;
        double singleNs = sw.Elapsed.TotalNanoseconds / iterations;
        Console.WriteLine($"Single-thread: {singleQps:N0} QPS ({singleNs:F0} ns/op, {sw.Elapsed.TotalSeconds:F3}s)");

        // Multi-thread benchmarks
        foreach (int threads in new[] { 4, 8, 16 })
        {
            int opsPerThread = iterations / threads;
            sw.Restart();
            Parallel.For(0, threads, t =>
            {
                for (int i = 0; i < opsPerThread; i++)
                    reader.Find("223.5.5.5");
            });
            sw.Stop();
            double qps = iterations / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"{threads,2} threads:    {qps:N0} QPS ({sw.Elapsed.TotalSeconds:F3}s)");
        }

        // Max global DB (25 fields)
        Console.WriteLine("\n--- Max Global (25 fields) ---");
        using var maxReader = new DatabaseReader.Builder($"{BP}/max/global/qqzeng_ip_max_global.qzdb").Build();
        for (int i = 0; i < 100_000; i++) maxReader.Find("8.8.8.8");
        sw.Restart();
        for (int i = 0; i < iterations; i++) maxReader.Find("8.8.8.8");
        sw.Stop();
        double maxQps = iterations / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"Single-thread: {maxQps:N0} QPS");

        Console.WriteLine("\nTEST_PASS");
    }
}
