/**
 * QZDB C SDK — 原生浮点格式化统一契约边界回归（FORMAT §10.5 / ROADMAP P0-2）。
 *
 * format_float_value 是 qzdb_reader.c 的静态函数，本 harness 直接 include 源文件
 * 以获得访问权（独立编译，勿与 main.c/test_main.c 同链接）：
 *   gcc -O2 -o native_float_boundary_test native_float_boundary_test.c -lm
 *   ./native_float_boundary_test
 *
 * 用例与 python/test_native_float.py、nodejs/native_float_test.js、
 * go/qzdb/native_float_test.go 逐字同源。
 */
#include "qzdb_reader.c"

static int g_total = 0, g_pass = 0;

static void check_str(const char *got, const char *want, const char *label) {
    g_total++;
    if (strcmp(got, want) == 0) {
        g_pass++;
    } else {
        printf("  FAIL: %s: got \"%.44s\" want \"%.44s\"\n", label, got, want);
    }
}

/* double(1e300) 精确十进制展开（str(int(1e300)) 导出，非被测函数生成） */
static const char *E300 =
    "10000000000000000525047602552044202487044685811081591549158541155118024579889"
    "08195786371375080447864043704443832883878176942523235360430575644792184786706"
    "98284838720092657580373783023379478809005936895323497079994508111903896764088"
    "0074652742780142494579258788820056842838115669472196386865459400540160";

int main(void) {
    char buf[512];

    /* --- float64 整值路径 --- */
    format_float_value(116.0, buf, sizeof buf);   check_str(buf, "116", "整值无小数点");
    format_float_value(-3.0, buf, sizeof buf);    check_str(buf, "-3", "负整值");
    format_float_value(0.0, buf, sizeof buf);     check_str(buf, "0", "零");
    format_float_value(-0.0, buf, sizeof buf);    check_str(buf, "0", "负零归一");
    /* --- 非整数固定 6 位 --- */
    format_float_value(116.4, buf, sizeof buf);   check_str(buf, "116.400000", "非整数 6 位");
    format_float_value(-3.5, buf, sizeof buf);    check_str(buf, "-3.500000", "负非整数");
    /* --- NaN/Inf --- */
    format_float_value(NAN, buf, sizeof buf);     check_str(buf, "", "NaN");
    format_float_value(INFINITY, buf, sizeof buf);  check_str(buf, "", "+Inf");
    format_float_value(-INFINITY, buf, sizeof buf); check_str(buf, "", "-Inf");
    /* --- int64 范围边界（旧实现 ±2^52 guard 曾把以下走 %.6f 分支——回归守卫） --- */
    format_float_value(1e16, buf, sizeof buf);    check_str(buf, "10000000000000000", "2^53 整值");
    format_float_value(9.2e18, buf, sizeof buf);  check_str(buf, "9200000000000000000", "int64 上界内大整值");
    format_float_value(9223372036854774784.0, buf, sizeof buf);
    check_str(buf, "9223372036854774784", "< 2^63 最大可表示偶数整值");
    /* --- 恰为 ±2^63：cast UB 边界，必须走 %.0f 定点分支 --- */
    format_float_value(9223372036854775808.0, buf, sizeof buf);
    check_str(buf, "9223372036854775808", "恰为 2^63 走定点分支");
    format_float_value(-9223372036854775808.0, buf, sizeof buf);
    check_str(buf, "-9223372036854775808", "恰为 -2^63");
    /* --- > 2^63 定点整数位 / 精确展开 --- */
    format_float_value(1e20, buf, sizeof buf);    check_str(buf, "100000000000000000000", "> 2^63 定点整数位");
    format_float_value(1e300, buf, sizeof buf);   check_str(buf, E300, "1e300 定点展开精确值");
    {
        char want[512];
        snprintf(want, sizeof want, "-%s", E300);
        format_float_value(-1e300, buf, sizeof buf);
        check_str(buf, want, "-1e300");
    }

    /* --- float32 路径：float32(116.4) 精确 double 值 = 116.40000152587890625 --- */
    format_float32_value((float)116.4, buf, sizeof buf);
    check_str(buf, "116.400002", "f32 按精确 double 值舍入");
    format_float32_value((float)116.0, buf, sizeof buf);
    check_str(buf, "116", "f32 整值无小数点");

    printf("NativeFloat assertions: total=%d pass=%d fail=%d\n",
           g_total, g_pass, g_total - g_pass);
    if (g_total == g_pass) {
        printf("NATIVE_FLOAT_OK\n");
        return 0;
    }
    printf("NATIVE_FLOAT_FAIL\n");
    return 1;
}
