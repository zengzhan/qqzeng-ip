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

    static void Main(string[] args)
    {
        var dbPath = args.Length > 0 ? args[0] : FindDb();
        var ipQuery = args.Length > 1 ? args[1] : "114.114.114.114";
        if (dbPath == null)
        {
            Console.WriteLine("Database file not found");
            return;
        }

        var searcher = new Qqzeng.QzdbSearcher();
        searcher.Load(dbPath);

        Console.WriteLine($"Version: {searcher.Version}");
        Console.WriteLine($"Fields ({searcher.FieldNames.Length}): {string.Join(", ", searcher.FieldNames)}\n");

        var result = searcher.FindStr(ipQuery);
        Console.WriteLine($"find(\"{ipQuery}\") => {result}");
    }
}
