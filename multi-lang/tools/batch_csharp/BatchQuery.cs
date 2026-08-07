using System;
using System.IO;
using System.Linq;
using QQZeng.Qzdb;

class BatchQueryCsharp
{
    // Use GeoInfo.ToPipe() so output byte-matches Python to_pipe()
    static string GeoToPipe(GeoInfo? info)
    {
        if (info == null) return "";
        return info.ToPipe();
    }

    static int ProcessFile(QzdbReader searcher, string testPath, string outPath, bool isV6)
    {
        if (!File.Exists(testPath)) return 0;

        var results = File.ReadAllLines(testPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(line =>
            {
                string pipeStr;
                if (isV6)
                {
                    pipeStr = GeoToPipe(searcher.Find(line));
                }
                else
                {
                    if (uint.TryParse(line, out var ip))
                    {
                        var info = searcher.FindUint(ip);
                        pipeStr = GeoToPipe(info);
                    }
                    else
                    {
                        pipeStr = "";
                    }
                }
                return line + "|" + pipeStr;
            })
            .ToArray();

        File.WriteAllText(outPath, string.Join("\n", results) + "\n");
        return results.Length;
    }

    static void Main(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("Usage: BatchQuery <db_path> <v4_test> <v4_out> <v6_test> <v6_out>");
            Environment.Exit(1);
        }

        var dbPath = args[0];
        var v4Test = args[1];
        var v4Out = args[2];
        var v6Test = args[3];
        var v6Out = args[4];

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"C#: Database not found: {dbPath}");
            Environment.Exit(1);
        }

        using var searcher = QzdbReader.Open(dbPath);

        var n4 = ProcessFile(searcher, v4Test, v4Out, false);
        Console.Error.WriteLine($"  C# V4: {n4} queries");

        var n6 = ProcessFile(searcher, v6Test, v6Out, true);
        Console.Error.WriteLine($"  C# V6: {n6} queries");

        Console.Error.WriteLine("  C# DONE");
    }
}
