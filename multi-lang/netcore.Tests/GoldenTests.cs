using QQZeng.Qzdb;
using System.Text.Json;

/// <summary>
/// Tier 4 — Cross-language golden-vector conformance (Tier2 in the python/go/rust
/// runners). Loads <c>tools/golden_vectors.json</c> and asserts that
/// <c>QzdbReader.FindStr(ip)</c> equals the language-agnostic <c>expected</c> pipe
/// string for every vector in <c>std_china</c> / <c>ult_china</c>.
///
/// Assertion semantics mirror the other SDKs exactly:
///   - python:  got = reader.find(ip).to_pipe() if found else ""  (invalid -> catch -> "")
///   - go:      got = info.ToPipe() if info != nil else ""        (invalid -> (nil,nil) -> "")
///   - rust:    got = reader.find_str(ip)                         (invalid -> "")
/// C#'s <see cref="QzdbReader.FindStr"/> returns the pipe string on hit and "" on both
/// miss and malformed input, so it is the exact equivalent of all three.
/// </summary>
static class GoldenTests
{
    public static int FailCount { get; private set; }
    public static int TotalCount { get; private set; }

    public static void Run()
    {
        Console.WriteLine("\n--- Tier 4: Cross-language Golden Vectors (Tier2 conformance) ---");
        FailCount = 0;
        TotalCount = 0;

        string? goldenPath = LocateGolden();
        if (goldenPath == null)
        {
            Console.WriteLine("  [SKIP] golden_vectors.json not found; skipping Tier 4 golden test");
            return;
        }
        string? dataDir = LocateDataDir();
        if (dataDir == null)
        {
            Console.WriteLine("  [SKIP] data directory not found; skipping Tier 4 golden test");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(goldenPath));
        var root = doc.RootElement;

        // lib key -> db file name (qqzeng_ip_{key}.qzdb)
        var libs = new[] { "std_china", "ult_china" };
        var categories = new[] { "random_v4", "random_v6", "boundary_v4", "boundary_v6", "invalid" };

        foreach (var key in libs)
        {
            if (!root.TryGetProperty(key, out var libElem) || libElem.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine($"  [WARN] golden missing lib {key}");
                continue;
            }
            string dbFile = "qqzeng_ip_" + key + ".qzdb";
            string dbPath = Path.Combine(dataDir, dbFile);
            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"  [SKIP] db {dbFile} not found; skipping golden for {key}");
                continue;
            }

            using var reader = QzdbReader.Open(dbPath);
            foreach (var cat in categories)
            {
                if (!libElem.TryGetProperty(cat, out var catElem) || catElem.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var entry in catElem.EnumerateArray())
                {
                    if (!entry.TryGetProperty("ip", out var ipElem) || ipElem.ValueKind != JsonValueKind.String)
                        continue;
                    string ip = ipElem.GetString()!;
                    string expected = entry.TryGetProperty("expected", out var expElem) &&
                                      expElem.ValueKind == JsonValueKind.String
                        ? expElem.GetString()!
                        : "";
                    // FindStr returns the pipe string on hit, "" on miss or malformed IP —
                    // identical to python/go/rust golden semantics.
                    string got = reader.FindStr(ip);
                    TotalCount++;
                    if (got != expected)
                    {
                        FailCount++;
                        if (FailCount <= 20)
                            Console.WriteLine($"  MISMATCH [{key}/{cat}] ip={ip} expected='{expected}' got='{got}'");
                    }
                }
            }
            reader.Dispose();
        }

        if (FailCount == 0)
            Console.WriteLine($"Golden: {TotalCount}/{TotalCount} passed");
        else
            Console.WriteLine($"Golden: {TotalCount - FailCount}/{TotalCount} passed, {FailCount} failed");
    }

    static string? LocateGolden()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "tools", "golden_vectors.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "golden_vectors.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "golden_vectors.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "golden_vectors.json"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    static string? LocateDataDir()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "data"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "data"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (Directory.Exists(full)) return full;
        }
        return null;
    }
}
