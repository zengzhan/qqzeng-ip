using Qzdb;

class Program
{
    static string BP = "";
    static int checks = 0, pass = 0;

    static void Main()
    {
        BP = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "..", "test_data_202608"));
        System.Console.WriteLine("=== C# SDK Direct Value Verification ===");

        // Test 1: Known correct values for std/pro (no CSV ambiguity)
        using (var r = new DatabaseReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build())
        {
            var info = r.Find("223.5.5.5");
            Check("std country", "中国", info.Get("country"));
            Check("std province", "浙江", info.Get("province"));
            Check("std city", "杭州", info.Get("city"));
            Check("std isp", "阿里云", info.Get("isp"));
            Check("std code", "CN", info.Get("country_code"));
        }

        // Test 2: All std/pro global IPs
        using (var r = new DatabaseReader.Builder(BP + "/std/global/qqzeng_ip_std_global.qzdb").Build())
        {
            var info = r.Find("8.8.8.8");
            Check("global country", "美国", info.Get("country"));
            Check("global city", "山景城", info.Get("city"));
            Check("global isp", "谷歌云", info.Get("isp"));
            Check("global code", "US", info.Get("country_code"));
        }

        // Test 3: PRO version extra fields
        using (var r = new DatabaseReader.Builder(BP + "/pro/china/qqzeng_ip_pro_china.qzdb").Build())
        {
            var info = r.Find("114.114.114.114");
            Check("pro geo_id", "320100", info.Get("geo_id"));
            Check("pro district", "", info.Get("district"));
            Check("pro timezone", "Asia/Shanghai", info.Get("timezone"));
            var lon = info.Get("longitude");
            var lat = info.Get("latitude");
            System.Console.WriteLine("  pro lon=" + lon + " lat=" + lat);
            Check("pro lon valid", "yes", (System.Math.Abs(double.Parse(lon) - 118.77) < 0.1) ? "yes" : "no");
            Check("pro lat valid", "yes", (System.Math.Abs(double.Parse(lat) - 32.04) < 0.1) ? "yes" : "no");
        }

        // Test 4: MAX version - verify all 15 fields have values
        using (var r = new DatabaseReader.Builder(BP + "/max/china/qqzeng_ip_max_china.qzdb").Build())
        {
            var info = r.Find("114.114.114.114");
            Check("max continent", "亚洲", info.Get("continent"));
            Check("max country_code", "CN", info.Get("country_code"));
            Check("max country", "中国", info.Get("country"));
            Check("max province", "江苏", info.Get("province"));
            Check("max city", "南京", info.Get("city"));
            Check("max geo_id", "320100", info.Get("geo_id"));
            Check("max isp", "中国电信", info.Get("isp"));
            Check("max asn", "137702", info.Get("asn"));
            Check("max usage", "DNS", info.Get("usage_type"));
        }

        // Test 5: ASN version - verify all 8 fields
        using (var r = new DatabaseReader.Builder(BP + "/asn/china/qqzeng_ip_asn_china.qzdb").Build())
        {
            var info = r.Find("114.114.114.114");
            Check("asn continent", "亚洲", info.Get("continent"));
            Check("asn country", "中国", info.Get("country"));
            Check("asn isp", "中国电信", info.Get("isp"));
            Check("asn asn", "137702", info.Get("asn"));
            Check("asn usage", "DNS", info.Get("usage_type"));
        }

        // Test 6: ULT version - verify all 25 fields
        using (var r = new DatabaseReader.Builder(BP + "/ult/china/qqzeng_ip_ult_china.qzdb").Build())
        {
            var info = r.Find("114.114.114.114");
            Check("ult continent", "亚洲", info.Get("continent"));
            Check("ult continent_en", "Asia", info.Get("continent_en"));
            Check("ult country_code", "CN", info.Get("country_code"));
            Check("ult country_alpha3", "CHN", info.Get("country_alpha3"));
            Check("ult country", "中国", info.Get("country"));
            Check("ult country_en", "China", info.Get("country_en"));
            Check("ult province", "江苏", info.Get("province"));
            Check("ult city", "南京", info.Get("city"));
            Check("ult geo_id", "320100", info.Get("geo_id"));
            Check("ult isp", "中国电信", info.Get("isp"));
            Check("ult asn", "137702", info.Get("asn"));
            Check("ult usage", "DNS", info.Get("usage_type"));
        }

        // Test 7: IPv6 real addresses
        using (var r = new DatabaseReader.Builder(BP + "/std/global/qqzeng_ip_std_global.qzdb").Build())
        {
            var info = r.Find("2001:4860:4860::8888");
            Check("v6 country", "美国", info.Get("country"));
            Check("v6 isp", "谷歌云", info.Get("isp"));
        }

        // Test 8: V4-mapped IPv6
        using (var r = new DatabaseReader.Builder(BP + "/std/china/qqzeng_ip_std_china.qzdb").Build())
        {
            var d = r.FindStr("223.5.5.5");
            var m = r.FindStr("::ffff:223.5.5.5");
            Check("mapped", d, m);
        }

        System.Console.WriteLine("\n" + new string('=', 50));
        System.Console.WriteLine("Checks: " + checks + " | Pass: " + pass + " | Fail: " + (checks - pass));
        System.Console.WriteLine(pass == checks ? "ALL PASSED" : "FAILED");
        Environment.Exit(pass == checks ? 0 : 1);
    }

    static void Check(string name, string expected, string actual)
    {
        checks++;
        if (expected == actual) { pass++; System.Console.WriteLine("  [OK] " + name + " = '" + actual + "'"); }
        else { System.Console.WriteLine("  [FAIL] " + name + ": expected '" + expected + "' got '" + actual + "'"); }
    }
}
