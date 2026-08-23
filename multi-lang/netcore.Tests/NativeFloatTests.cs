using QQZeng.Qzdb;
using System.Reflection;

/// <summary>
/// FORMAT §10.5 原生浮点统一契约边界回归（ROADMAP P0-2）。
///
/// FormatFloat6 是 QzdbReader 的私有静态方法（canonical 实现），此处经反射调用，
/// 对 §10.5 全部边界断言：整数值无小数点、非整数固定 6 位小数、NaN/Inf 为 ""、
/// |v| ≥ 2^63 走 F0 定点分支（旧实现直接 (long) cast 在该区间是未指定行为——回归守卫）。
/// 用例与 python/test_native_float.py、nodejs/native_float_test.js、
/// go/qzdb/native_float_test.go、c native_float_boundary_test.c 逐字同源。
/// </summary>
static class NativeFloatTests
{
    public static int FailCount { get; private set; }
    public static int TotalCount { get; private set; }

    public static void Run()
    {
        Console.WriteLine("\n--- NativeFloat: FORMAT §10.5 boundary conformance ---");
        FailCount = 0;
        TotalCount = 0;

        var m = typeof(QzdbReader).GetMethod("FormatFloat6",
            BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(double) }, null);
        if (m == null)
        {
            Console.WriteLine("  [SKIP] QzdbReader.FormatFloat6(double) not found");
            return;
        }
        string F(double v) => (string)m.Invoke(null, new object[] { v })!;

        // 整值路径
        Check(F(116.0), "116", "整值无小数点");
        Check(F(-3.0), "-3", "负整值");
        Check(F(0.0), "0", "零");
        Check(F(-0.0), "0", "负零归一");
        // 非整数固定 6 位
        Check(F(116.4), "116.400000", "非整数固定 6 位");
        Check(F(-3.5), "-3.500000", "负非整数");
        // NaN / Inf
        Check(F(double.NaN), "", "NaN -> \"\"");
        Check(F(double.PositiveInfinity), "", "+Inf -> \"\"");
        Check(F(double.NegativeInfinity), "", "-Inf -> \"\"");
        // int64 范围边界（旧实现无范围保护，≥2^63 走 (long) cast 未指定行为）
        Check(F(1e16), "10000000000000000", "2^53 整值");
        Check(F(9.2e18), "9200000000000000000", "int64 上界内大整值");
        Check(F(9223372036854774784.0), "9223372036854774784", "< 2^63 最大可表示偶数整值");
        Check(F(9223372036854775808.0), "9223372036854775808", "恰为 2^63 走 F0 定点分支");
        Check(F(-9223372036854775808.0), "-9223372036854775808", "恰为 -2^63");
        Check(F(1e20), "100000000000000000000", "> 2^63 定点整数位");

        // double(1e300) 精确十进制展开（str(int(1e300)) 导出，非被测函数生成）
        const string e300 =
            "10000000000000000525047602552044202487044685811081591549158541155118024579889" +
            "08195786371375080447864043704443832883878176942523235360430575644792184786706" +
            "98284838720092657580373783023379478809005936895323497079994508111903896764088" +
            "0074652742780142494579258788820056842838115669472196386865459400540160";
        Check(F(1e300), e300, "1e300 定点展开精确值");
        Check(F(-1e300), "-" + e300, "-1e300");

        Console.WriteLine($"  NativeFloat assertions: total={TotalCount} fail={FailCount}");
    }

    static void Check(string got, string want, string label)
    {
        TotalCount++;
        if (got == want) return;
        FailCount++;
        Console.WriteLine($"  FAIL: {label}: got \"{Trunc(got)}\" want \"{Trunc(want)}\"");
    }

    static string Trunc(string s) => s.Length <= 44 ? s : s[..44] + "…";
}
