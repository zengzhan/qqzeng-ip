/**
 * QZDB C SDK — IP 解析严格性契约回归（缺陷审计 + 守卫）。
 *
 * fast_parse_ip 是 qzdb_reader.c 的静态函数，本 harness 直接 include 源文件
 * 以获得访问权（独立编译，勿与 main.c/test_main.c 同链接）：
 *   gcc -O2 -o ip_strict_test ip_strict_test.c -lm
 *   ./ip_strict_test
 *
 * 行 1-3 为修复目标——嵌入 IPv4 落在左侧分组、`::` 右侧为空（如 `1.2.3.4::`、
 * `0.0.0.0::`、`2001:db8:1.2.3.4::`），Go netip.ParseAddr 与 Go SDK 均拒绝；
 * 行 4-10 为既有不变量，必须保持不变（v4-mapped 降级、zone、双 gap 等）。
 */
#include "qzdb_reader.c"

static int g_total = 0, g_pass = 0;

/* 期望 accept：is_accept=1；期望 reject：is_accept=0 */
static void check_parse(const char *s, int is_accept, const char *label) {
    g_total++;
    parse_result_t res;
    int ok = fast_parse_ip(s, &res);
    if (is_accept && ok) {
        g_pass++;
    } else if (!is_accept && !ok) {
        g_pass++;
    } else {
        printf("  FAIL: %s: input \"%s\" expected %s but got %s\n",
               label, s, is_accept ? "ACCEPT" : "REJECT", ok ? "ACCEPT" : "REJECT");
    }
}

int main(void) {
    /* 行 1-3：修复目标，必须 REJECT */
    check_parse("0.0.0.0::",            0, "0.0.0.0:: 必须拒绝");
    check_parse("1.2.3.4::",            0, "1.2.3.4:: 必须拒绝");
    check_parse("2001:db8:1.2.3.4::",   0, "2001:db8:1.2.3.4:: 必须拒绝");
    /* 行 4-10：既有不变量，必须保持不变 */
    check_parse("::1.2.3.4",            1, "::1.2.3.4 必须接受（v4-mapped 降级）");
    check_parse("2001:db8::1.2.3.4",    1, "2001:db8::1.2.3.4 必须接受");
    check_parse("1::2.3.4.5",           1, "1::2.3.4.5 必须接受");
    check_parse("114.114.114.114",      1, "114.114.114.114 必须接受（纯 v4）");
    check_parse("::ffff:7272:7272",     1, "::ffff:7272:7272 必须接受（既有）");
    check_parse("fe80::1%eth0",         0, "fe80::1%eth0 必须拒绝（zone，既有）");
    check_parse("1::2::3",              0, "1::2::3 必须拒绝（双 gap，既有）");

    printf("IpStrict assertions: total=%d pass=%d fail=%d\n",
           g_total, g_pass, g_total - g_pass);
    if (g_total == g_pass) {
        printf("IP_STRICT_OK\n");
        return 0;
    }
    printf("IP_STRICT_FAIL\n");
    return 1;
}
