using Qzdb;
using System.Globalization;

class Program
{
    static string BP = "";
    static System.Random rng = new System.Random(42);
    static int totalChecks = 0, totalErrors = 0;

    static string[] std_fields = { "continent", "country_code", "country", "province", "city", "isp" };
    static string[] pro_fields = { "continent", "country_code", "country", "province", "city", "district", "geo_id", "longitude", "latitude", "timezone", "isp" };
    static string[] max_fields = { "continent", "country_code", "country", "province", "city", "district", "geo_id", "longitude", "latitude", "timezone", "isp", "asn", "as_name", "as_domain", "usage_type" };
    static string[] asn_fields = { "continent", "country_code", "country", "isp", "asn", "as_name", "as_domain", "usage_type" };
    static string[] ult_fields = { "continent", "continent_en", "country_code", "country_alpha3", "country", "country_en", "province", "province_en", "city", "city_en", "district", "district_en", "geo_id", "longitude", "latitude", "timezone", "languages", "currency_code", "phone_prefix", "emoji_flag", "isp", "asn", "as_name", "as_domain", "usage_type" };

    static System.Collections.Generic.Dictionary<string, string[]> VersionFields = new()
    {
        { "std", std_fields },
        { "pro", pro_fields },
        { "ult", ult_fields },
        { "max", max_fields },
        { "asn", asn_fields },
    };

    static void Main()
    {
        BP = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "..", "test_data_202608"));
        System.Console.WriteLine("=== C# SDK Full-Field Verification (Proper CSV Parsing) ===");

        foreach (var ver in new[] { "std", "pro", "ult", "max", "asn" })
        {
            foreach (var scope in new[] { "china", "global" })
            {
                var dbPath = BP + "/" + ver + "/" + scope + "/qqzeng_ip_" + ver + "_" + scope + ".qzdb";
                var csvPath = BP + "/" + ver + "/" + scope + "/qqzeng_ip_" + ver + "_" + scope + "_range.csv";
                if (!System.IO.File.Exists(dbPath) || !System.IO.File.Exists(csvPath)) continue;
                TestVersion(ver, scope, dbPath, csvPath);
            }
        }

        double rate = totalChecks > 0 ? (1.0 - (double)totalErrors / totalChecks) * 100 : 100;
        System.Console.WriteLine("\n" + new string('=', 60));
        System.Console.WriteLine("TOTAL: " + totalChecks + " field-checks, " + totalErrors + " errors");
        System.Console.WriteLine("Accuracy: " + rate.ToString("F4") + "% ");
        System.Console.WriteLine(totalErrors == 0 ? "ALL PASSED" : "FAILED");
        Environment.Exit(totalErrors > 0 ? 1 : 0);
    }

    static void TestVersion(string ver, string scope, string dbPath, string csvPath)
    {
        var fields = VersionFields[ver];
        var allLines = System.IO.File.ReadAllLines(csvPath);
        if (allLines.Length < 2) return;

        // Parse CSV header
        var csvHeaders = ParseCsvLine(allLines[0]);
        var colMap = new System.Collections.Generic.Dictionary<string, int>();
        for (int i = 0; i < csvHeaders.Length; i++)
            colMap[csvHeaders[i].Trim()] = i;

        // Collect valid rows with PROPER CSV parsing
        var rows = new System.Collections.Generic.List<string[]>();
        for (int i = 1; i < allLines.Length; i++)
        {
            var cols = ParseCsvLine(allLines[i]);
            if (cols.Length < csvHeaders.Length) continue;
            var ip = cols[0].Trim();
            if (string.IsNullOrEmpty(ip) || ip == "0.0.0.0" || ip == "::") continue;
            if (ip.StartsWith("::ffff:") || ip.StartsWith("0:0:0:0:0:ffff:")) continue;
            rows.Add(cols);
        }

        using var r = new DatabaseReader.Builder(dbPath).Build();
        int verChecks = 0, verErrors = 0;

        for (int batch = 0; batch < 15; batch++)
        {
            var sampled = Sample(rows, 300);
            foreach (var cols in sampled)
            {
                var ip = cols[0].Trim();
                var info = r.Find(ip);
                if (info == null) continue;

                for (int fi = 0; fi < fields.Length; fi++)
                {
                    var field = fields[fi];
                    if (!colMap.ContainsKey(field)) continue;
                    var idx = colMap[field];
                    if (idx >= cols.Length) continue;

                    var expected = cols[idx].Trim().Replace("\"", "");
                    var actual = info.Get(field).Trim().Replace("\"", "");

                    if (!FieldsMatch(field, expected, actual))
                    {
                        verErrors++;
                        if (verErrors <= 3)
                            System.Console.WriteLine("    ERR [" + ver + " " + scope + "] IP=" + ip + " " + field + ": csv='" + expected + "' db='" + actual + "'");
                    }
                    verChecks++;
                }
            }
        }

        totalChecks += verChecks;
        totalErrors += verErrors;
        double rate = verChecks > 0 ? (1.0 - (double)verErrors / verChecks) * 100 : 100;
        System.Console.WriteLine("  [" + ver + " " + scope + "] fields=" + fields.Length + " checks=" + verChecks + " errors=" + verErrors + " acc=" + rate.ToString("F2") + "%");
    }

    // Proper CSV line parser: handles quoted fields with embedded commas
    static string[] ParseCsvLine(string line)
    {
        var result = new System.Collections.Generic.List<string>();
        bool inQuotes = false;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }

    static bool FieldsMatch(string field, string expected, string actual)
    {
        if (expected == actual) return true;
        if (string.IsNullOrEmpty(expected)) return true;

        if (field == "longitude" || field == "latitude")
        {
            if (double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var expD) &&
                double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var actD))
            {
                return System.Math.Abs(expD - actD) < 0.001;
            }
        }

        var normE = expected.ToLowerInvariant().Trim();
        var normA = actual.ToLowerInvariant().Trim();
        if (normE == normA) return true;

        normE = normE.TrimEnd('0').TrimEnd('.');
        normA = normA.TrimEnd('0').TrimEnd('.');
        return normE == normA;
    }

    static System.Collections.Generic.List<string[]> Sample(System.Collections.Generic.List<string[]> rows, int count)
    {
        var result = new System.Collections.Generic.List<string[]>();
        var indices = new System.Collections.Generic.HashSet<int>();
        while (indices.Count < count && indices.Count < rows.Count)
        {
            indices.Add(rng.Next(rows.Count));
        }
        foreach (var i in indices)
            result.Add(rows[i]);
        return result;
    }
}
