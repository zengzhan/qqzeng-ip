/**
 * QzdbSearcher - C# SDK calling example
 *
 * Usage: dotnet run
 * Place qqzeng_ip_std_china.qzdb in the same directory or specify the path.
 */

using System;
using System.IO;

class Program
{
    static string FindDb()
    {
        foreach (var c in new[] {
            "qqzeng_ip_std_china.qzdb",
            "../data/qqzeng_ip_std_china.qzdb",
            "data/qqzeng_ip_std_china.qzdb",
        })
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    static void Main()
    {
        var dbPath = FindDb();
        if (dbPath == null)
        {
            Console.WriteLine("Database file not found");
            return;
        }

        var searcher = Qqzeng.QzdbSearcher.Instance;
        searcher.Load(dbPath);

        Console.WriteLine($"Version: {searcher.Version}");
        Console.WriteLine($"Fields ({searcher.FieldNames.Length}): {string.Join(", ", searcher.FieldNames)}\n");

        // Query sample V4 IPs
        foreach (var ip in new[] { "114.114.114.114", "223.5.5.5", "8.8.8.8" })
        {
            var result = searcher.FindStr(ip);
            Console.WriteLine($"find(\"{ip}\") => {result}");
        }

        // Query a V6 IP
        var v6 = searcher.FindStr("2408:8000:9000::1");
        Console.WriteLine($"find(\"2408:8000:9000::1\") => {v6}");

        // Get structured fields
        Console.WriteLine("\n--- Structured fields for 114.114.114.114 ---");
        var loc = searcher.Find("114.114.114.114");
        if (loc != null)
        {
            for (int i = 0; i < searcher.FieldNames.Length; i++)
            {
                Console.WriteLine($"  {searcher.FieldNames[i]}: {loc.Values[i]}");
            }
        }
        Console.WriteLine("TEST_PASS");
    }
}
