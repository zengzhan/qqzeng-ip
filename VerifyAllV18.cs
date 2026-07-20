using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;

namespace qqzengPgUI.ipdb8
{
    public class VerifyAllV18
    {
        const string CSV_ROOT = "/Users/zengxiangzhan/ZengData/发行版/2026-07";

        public class SummaryResult
        {
            public long TotalChecks;
            public long FailCount;
        }

        struct TestResult
        {
            public long TotalRows;
            public long SuccessStart;
            public long SuccessEnd;
            public long SuccessRnd;
            public long FailStart;
            public long FailEnd;
            public long FailRnd;
            public long V4Rows;
            public long V6Rows;
            public long V6Uncovered;
            public List<string> SampleErrors;

            public void Report(string label)
            {
                long totalChecks = (V4Rows * 3) + ((V6Rows - V6Uncovered) * 3);
                long ok = SuccessStart + SuccessEnd + SuccessRnd;
                long fail = FailStart + FailEnd + FailRnd;
                double pct = totalChecks > 0 ? 100.0 * ok / totalChecks : 100;
                Console.WriteLine($"  [{label}] {TotalRows:N0} rows ({V4Rows:N0} v4 / {V6Rows:N0} v6), {totalChecks:N0} checks");
                if (V6Uncovered > 0)
                    Console.WriteLine($"    V6 UNCOVERED (not in this database): {V6Uncovered:N0} rows");
                Console.WriteLine($"    OK={ok:N0}  FAIL={fail:N0}  ACC={pct:F6}%");
                if (fail > 0 && SampleErrors.Count > 0)
                {
                    Console.WriteLine($"    First {Math.Min(5, SampleErrors.Count)} failures:");
                    for (int i = 0; i < Math.Min(5, SampleErrors.Count); i++)
                        Console.WriteLine($"      {SampleErrors[i]}");
                }
            }

            public long FailCount => FailStart + FailEnd + FailRnd;
            public long TotalChecks => (V4Rows * 3) + ((V6Rows - V6Uncovered) * 3);
        }

        readonly string _version;
        readonly string _region;
        readonly IPDBSearcherV18 _searcher;
        readonly string _zipPath;
        readonly string _csvEntryName;
        readonly int _sampleEvery;

        public VerifyAllV18(string version, string region, IPDBSearcherV18 searcher, int sampleEvery = 1)
        {
            _version = version;
            _region = region;
            _searcher = searcher;
            _sampleEvery = sampleEvery;
            _zipPath = $"{CSV_ROOT}/qqzeng_ip_{version}/qqzeng_ip_{version}_{region}_range.zip";
            _csvEntryName = $"qqzeng_ip_{version}_{region}_range.csv";
        }

        public SummaryResult Run()
        {
            var result = new TestResult { SampleErrors = new List<string>() };

            if (!File.Exists(_zipPath))
            {
                // Fallback: try direct CSV from data_v18/
                string fallback = $"/Users/zengxiangzhan/ZengData/IP数据库/ipdb18/multi-lang/data_v18/{_csvEntryName}";
                if (!File.Exists(fallback))
                {
                    Console.WriteLine($"  [SKIP] ZIP not found: {_zipPath}");
                    Console.WriteLine($"  [SKIP] Fallback CSV not found: {fallback}");
                    return new SummaryResult();
                }
                Console.WriteLine($"  [FALLBACK] Using direct CSV: {fallback}");
                RunFromReader(new StreamReader(fallback), ref result);
            }
            else
            {
                using var zip = ZipFile.OpenRead(_zipPath);
                var entry = zip.GetEntry(_csvEntryName);
                if (entry == null) { Console.WriteLine($"  [SKIP] CSV not found in zip: {_csvEntryName}"); return new SummaryResult(); }
                using var reader = new StreamReader(entry.Open());
                RunFromReader(reader, ref result);
            }

            result.Report($"{_version}/{_region}");
            if (result.FailCount > 0 && _region == "global")
            {
                string errFile = $"failures_{_version}_global.txt";
                using var w = new StreamWriter(errFile, false);
                foreach (var e in result.SampleErrors)
                    w.WriteLine(e);
                Console.WriteLine($"  [DETAILS] Wrote {result.SampleErrors.Count} failures to {errFile}");
            }

            return new SummaryResult { TotalChecks = result.TotalChecks, FailCount = result.FailCount };
        }

        void RunFromReader(StreamReader reader, ref TestResult result)
        {
            // Skip header
            reader.ReadLine();

            string line;
            var rng = new Random(42);
            long lineNo = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                // 抽样: 仅当 lineNo % _sampleEvery == 1 时验证
                if (_sampleEvery > 1 && (lineNo - 1) % _sampleEvery != 0)
                    continue;

                var csvFields = ParseCsvLine(line);
                if (csvFields.Length < 2) continue;

                // Range CSV: start_ip(0), end_ip(1), start_ip_num(2), end_ip_num(3), data_cols(4+)
                int expectedCols = _version == "std" ? 9 : _version == "ult" ? 15 : _version == "asn" ? 11 : _version == "max" ? 29 : 0;
                if (csvFields.Length < expectedCols)
                    Array.Resize(ref csvFields, expectedCols);

                string startIp = csvFields[0];
                result.TotalRows++;

                if (startIp.Contains(':'))
                    TestV6Row(csvFields, csvFields[0], ref result, rng);
                else
                    TestV4Row(csvFields, ref result, rng);
            }
        }

        void TestV4Row(string[] csv, ref TestResult r, Random rng)
        {
            r.V4Rows++;
            // Range CSV: column 2=start_ip_num, column 3=end_ip_num
            if (!uint.TryParse(csv[2], out uint startIP) || !uint.TryParse(csv[3], out uint endIP))
                return;

            var expected = BuildExpectedKey(csv);

            // Start
            var info = _searcher.Find(startIP);
            if (!MatchKey(expected, info, startIP))
            {
                r.FailStart++;
                AddSampleError(ref r, "V4", csv[0], startIP.ToString(), expected, KeyFrom(info));
                return;
            }
            r.SuccessStart++;

            // End
            info = _searcher.Find(endIP);
            if (!MatchKey(expected, info, endIP))
            {
                r.FailEnd++;
                AddSampleError(ref r, "V4", csv[0], endIP.ToString(), expected, KeyFrom(info));
                return;
            }
            r.SuccessEnd++;

            // Random
            uint rndIP = (uint)(startIP + (ulong)rng.Next((int)(endIP - startIP + 1)));
            info = _searcher.Find(rndIP);
            if (!MatchKey(expected, info, rndIP))
            {
                r.FailRnd++;
                AddSampleError(ref r, "V4", csv[0], rndIP.ToString(), expected, KeyFrom(info));
                return;
            }
            r.SuccessRnd++;
        }

        void TestV6Row(string[] csv, string cidr, ref TestResult r, Random rng)
        {
            r.V6Rows++;
            if (!TryParseCidrV6(cidr, out var startHigh, out var startLow, out var endHigh, out var endLow)) return;

            var expected = BuildExpectedKey(csv);

            // Check if database has V6 data at all
            var addr = IPAddress.Parse(cidr.Split('/')[0]);
            var info = _searcher.FindV6(addr);
            if (info.IsEmpty)
            {
                r.V6Uncovered++;
                return;
            }

            if (!MatchKey(expected, info, addr.ToString()))
            {
                r.FailStart++;
                AddSampleError(ref r, "V6", csv[0], addr.ToString(), expected, KeyFrom(info));
                return;
            }
            r.SuccessStart++;

            var endAddr = UInt128ToIPAddress(endHigh, endLow);
            info = _searcher.FindV6(endAddr);
            if (!MatchKey(expected, info, endAddr.ToString()))
            {
                r.FailEnd++;
                AddSampleError(ref r, "V6", csv[0], endAddr.ToString(), expected, KeyFrom(info));
                return;
            }
            r.SuccessEnd++;

            ulong rndHigh, rndLow;
            if (startHigh == endHigh)
            {
                rndHigh = startHigh;
                ulong span = endLow - startLow;
                if (span < int.MaxValue)
                    rndLow = startLow + (ulong)rng.Next((int)(span + 1));
                else
                    rndLow = startLow + (ulong)((double)rng.NextDouble() * (double)(span));
            }
            else
            {
                ulong hSpan = endHigh - startHigh;
                if (hSpan < int.MaxValue)
                    rndHigh = startHigh + (ulong)rng.Next((int)(hSpan + 1));
                else
                    rndHigh = startHigh + (ulong)((double)rng.NextDouble() * (double)(hSpan));
                ulong rangeLo = rndHigh == startHigh ? startLow : 0;
                ulong rangeHi = rndHigh == endHigh ? endLow : ulong.MaxValue;
                ulong span = rangeHi - rangeLo;
                if (span < int.MaxValue)
                    rndLow = rangeLo + (ulong)rng.Next((int)(span + 1));
                else
                    rndLow = rangeLo + (ulong)((double)rng.NextDouble() * (double)(span));
            }

            var rndAddr = UInt128ToIPAddress(rndHigh, rndLow);
            info = _searcher.FindV6(rndAddr);
            if (!MatchKey(expected, info, rndAddr.ToString()))
            {
                r.FailRnd++;
                AddSampleError(ref r, "V6", csv[0], rndAddr.ToString(), expected, KeyFrom(info));
                return;
            }
            r.SuccessRnd++;
        }

        string BuildExpectedKey(string[] csv)
        {
            // Range CSV columns: start_ip(0), end_ip(1), start_ip_num(2), end_ip_num(3), data_columns(4+)
            switch (_version)
            {
                case "std":
                    // std: continent(4), country(5), province(6), city(7), isp(8)
                    return $"c:{csv[4]}|co:{csv[5]}|p:{csv[6]}|ci:{csv[7]}|i:{csv[8]}";
                case "ult":
                    // ult: continent(4), country(5), province(6), city(7), district(8), isp(9), area_code(10),
                    //      country_english(11), country_code(12), longitude(13), latitude(14)
                    return $"c:{csv[4]}|co:{csv[5]}|p:{csv[6]}|ci:{csv[7]}|d:{csv[8]}|i:{csv[9]}|e:{csv[11]}|cd:{csv[12]}|lng:{csv[13]}|lat:{csv[14]}";
                case "asn":
                    // asn range CSV 列: start_ip(0), end_ip(1), start_ip_num(2), end_ip_num(3),
                    //   asn(4), asn_org(5), asn_domain(6), usage_type(7)[VARCHAR 英文分类值], country(8), country_code(9), isp(10)
                    // ★ usage_type 已从旧版 uint64 位掩码迁移为英文字符串（如 Broadband/DataCenter/VPN），迁移 079 已执行
                    // 当前验证仅比对 isp(10)/asn_org(5)/asn_domain(6) 三字段（usage_type 通过 Metadata 动态映射，此处跳过）
                    return $"i:{csv[10]}|e:{csv[5]}|cd:{csv[6]}";
                case "max":
                    // max: continent(4), country(5), province(6), city(7), district(8), isp(9), area_code(10),
                    //      country_english(11), country_code(12), ... longitude(16), latitude(17)
                    return $"c:{csv[4]}|co:{csv[5]}|p:{csv[6]}|ci:{csv[7]}|d:{csv[8]}|i:{csv[9]}|e:{csv[11]}|cd:{csv[12]}|lng:{csv[16]}|lat:{csv[17]}";
                default:
                    return "";
            }
        }

        string KeyFrom(IPDBSearcherV18.IPInfo info)
        {
            switch (_version)
            {
                case "std":
                    return $"c:{info.Continent}|co:{info.Country}|p:{info.Province}|ci:{info.City}|i:{info.ISP}";
                case "ult":
                    return $"c:{info.Continent}|co:{info.Country}|p:{info.Province}|ci:{info.City}|d:{info.District}|i:{info.ISP}|e:{info.EnName}|cd:{info.Code}|lng:{info.Lng:F6}|lat:{info.Lat:F6}";
                case "asn":
                    return $"i:{info.ISP}|e:{info.EnName}|cd:{info.Code}";
                case "max":
                    return $"c:{info.Continent}|co:{info.Country}|p:{info.Province}|ci:{info.City}|d:{info.District}|i:{info.ISP}|e:{info.EnName}|cd:{info.Code}|lng:{info.Lng:F6}|lat:{info.Lat:F6}";
                default:
                    return "";
            }
        }

        bool MatchKey(string expected, IPDBSearcherV18.IPInfo info, object ip)
        {
            string actual = KeyFrom(info);

            if (_version == "ult" || _version == "max")
            {
                return FuzzyMatch(expected, actual);
            }

            return expected == actual;
        }

        bool FuzzyMatch(string expected, string actual)
        {
            var eParts = expected.Split('|');
            var aParts = actual.Split('|');
            if (eParts.Length != aParts.Length) return false;
            for (int i = 0; i < eParts.Length; i++)
            {
                var eVal = eParts[i];
                var aVal = aParts[i];
                if (eVal.StartsWith("lng:") || eVal.StartsWith("lat:"))
                {
                    ReadOnlySpan<char> eSpan = eVal.AsSpan(4);
                    ReadOnlySpan<char> aSpan = aVal.AsSpan(4);
                    if (eSpan.Length == 0) continue;
                    if (aSpan.Length == 0) return false;
                    float ef = float.Parse(eSpan);
                    float af = float.Parse(aSpan);
                    if (Math.Abs(ef - af) > 0.0001f) return false;
                }
                else
                {
                    bool eEmpty = string.IsNullOrEmpty(eVal) || eVal.EndsWith(":");
                    bool aEmpty = string.IsNullOrEmpty(aVal) || aVal.EndsWith(":");
                    if (eEmpty && aEmpty) continue;
                    if (eVal != aVal) return false;
                }
            }
            return true;
        }

        void AddSampleError(ref TestResult r, string type, string cidr, string testIp, string expected, string actual)
        {
            if (r.SampleErrors.Count < 100)
                r.SampleErrors.Add($"{type} CIDR={cidr} IP={testIp} EXPECT=[{expected}] ACTUAL=[{actual}]");
        }

        static bool TryParseCidrV4(string cidr, out uint start, out uint end)
        {
            start = end = 0;
            int slash = cidr.IndexOf('/');
            if (slash < 0) return false;
            if (!IPAddress.TryParse(cidr.AsSpan(0, slash), out var addr)) return false;
            var bytes = addr.GetAddressBytes();
            if (bytes.Length != 4) return false;
            uint ip = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            if (!int.TryParse(cidr.AsSpan(slash + 1), out int prefix)) return false;
            if (prefix < 0 || prefix > 32) return false;
            if (prefix == 0) { start = 0; end = 0xFFFFFFFF; return true; }
            if (prefix == 32) { start = end = ip; return true; }
            uint mask = ~(0xFFFFFFFFu >> prefix);
            start = ip & mask;
            end = start | ~mask;
            return true;
        }

        static bool TryParseCidrV6(string cidr, out ulong startHigh, out ulong startLow, out ulong endHigh, out ulong endLow)
        {
            startHigh = startLow = endHigh = endLow = 0;
            int slash = cidr.IndexOf('/');
            if (slash < 0) return false;
            if (!IPAddress.TryParse(cidr.AsSpan(0, slash), out var addr)) return false;
            var bytes = addr.GetAddressBytes();
            if (bytes.Length != 16) return false;
            if (!int.TryParse(cidr.AsSpan(slash + 1), out int prefix)) return false;
            if (prefix < 0 || prefix > 128) return false;

            startHigh = (ulong)bytes[0] << 56 | (ulong)bytes[1] << 48 | (ulong)bytes[2] << 40 | (ulong)bytes[3] << 32
                      | (ulong)bytes[4] << 24 | (ulong)bytes[5] << 16 | (ulong)bytes[6] << 8 | bytes[7];
            startLow = (ulong)bytes[8] << 56 | (ulong)bytes[9] << 48 | (ulong)bytes[10] << 40 | (ulong)bytes[11] << 32
                     | (ulong)bytes[12] << 24 | (ulong)bytes[13] << 16 | (ulong)bytes[14] << 8 | bytes[15];

            if (prefix == 0)
            {
                endHigh = ulong.MaxValue; endLow = ulong.MaxValue;
            }
            else if (prefix >= 128) { endHigh = startHigh; endLow = startLow; }
            else if (prefix == 64) { endHigh = startHigh; endLow = ulong.MaxValue; }
            else if (prefix < 64)
            {
                ulong mask = ~(0xFFFFFFFFFFFFFFFFu >> prefix);
                startHigh &= mask;
                endHigh = startHigh | ~mask;
                endLow = ulong.MaxValue;
            }
            else
            {
                int lowShift = prefix - 64;
                ulong mask = ~(0xFFFFFFFFFFFFFFFFu >> lowShift);
                startLow &= mask;
                endLow = startLow | ~mask;
                endHigh = startHigh;
            }
            return true;
        }

        static IPAddress UInt128ToIPAddress(ulong high, ulong low)
        {
            var bytes = new byte[16];
            bytes[0] = (byte)(high >> 56);
            bytes[1] = (byte)(high >> 48);
            bytes[2] = (byte)(high >> 40);
            bytes[3] = (byte)(high >> 32);
            bytes[4] = (byte)(high >> 24);
            bytes[5] = (byte)(high >> 16);
            bytes[6] = (byte)(high >> 8);
            bytes[7] = (byte)high;
            bytes[8] = (byte)(low >> 56);
            bytes[9] = (byte)(low >> 48);
            bytes[10] = (byte)(low >> 40);
            bytes[11] = (byte)(low >> 32);
            bytes[12] = (byte)(low >> 24);
            bytes[13] = (byte)(low >> 16);
            bytes[14] = (byte)(low >> 8);
            bytes[15] = (byte)low;
            return new IPAddress(bytes);
        }

        static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            int i = 0, len = line.Length;
            while (i < len)
            {
                if (line[i] == '"')
                {
                    i++; // skip opening quote
                    var sb = new System.Text.StringBuilder();
                    while (i < len)
                    {
                        if (line[i] == '"')
                        {
                            if (i + 1 < len && line[i + 1] == '"')
                            { sb.Append('"'); i += 2; }
                            else
                            { i++; break; }
                        }
                        else
                        { sb.Append(line[i]); i++; }
                    }
                    fields.Add(sb.ToString());
                    if (i < len && line[i] == ',') i++;
                }
                else
                {
                    int start = i;
                    while (i < len && line[i] != ',') i++;
                    fields.Add(line.Substring(start, i - start));
                    if (i < len && line[i] == ',') i++;
                }
            }
            return fields.ToArray();
        }
    }
}
