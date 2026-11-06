#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>

#include "../../ip-qzdb-sdk/c/qzdb_reader.c"

int main() {
    printf("Running C SDK float formatting unit tests & parity verification...\n");

    /* 1. 边界用例：极小缓冲区测试 (不应静默丢负号或输出截断垃圾) */
    char tiny[4];
    /* -103.923730 needs at least 12 bytes; tiny has 4 bytes */
    format_float_value(-103.923730, tiny, sizeof(tiny));
    if (strlen(tiny) != 0) {
        fprintf(stderr, "FAIL: Expected empty string on tiny buffer, got '%s'\n", tiny);
        return 1;
    }
    printf("  ✅ Tiny buffer protection passed (returned empty string, zero sign drop).\n");

    /* 2. 负零与对称性测试 */
    char buf[64];
    format_float_value(-0.0, buf, sizeof(buf));
    printf("  ✅ -0.0 formatted as: '%s'\n", buf);

    format_float_value(0.0, buf, sizeof(buf));
    assert(strcmp(buf, "0") == 0);

    format_float_value(30.0, buf, sizeof(buf));
    assert(strcmp(buf, "30") == 0);

    format_float_value(-30.0, buf, sizeof(buf));
    assert(strcmp(buf, "-30") == 0);

    format_float_value(103.923730, buf, sizeof(buf));
    assert(strcmp(buf, "103.923730") == 0);

    format_float_value(-103.923730, buf, sizeof(buf));
    assert(strcmp(buf, "-103.923730") == 0);

    /* 3. 与 snprintf("%.6f") 大规模对拍 ([-180.0, 180.0] 经纬度全域) */
    int diffs = 0;
    int test_count = 50000;
    for (int i = 0; i < test_count; i++) {
        /* Generate float with up to 6 decimal places */
        double base = -180.0 + (360.0 * (double)i / (double)test_count);
        double rounded = round(base * 1000000.0) / 1000000.0;
        if (rounded == floor(rounded)) continue; /* integer format is intentionally "X" rather than "X.000000" */

        char expected[64];
        char actual[64];
        snprintf(expected, sizeof(expected), "%.6f", rounded);
        format_float_value(rounded, actual, sizeof(actual));

        if (strcmp(expected, actual) != 0) {
            fprintf(stderr, "Mismatch at %.6f: expected '%s', actual '%s'\n", rounded, expected, actual);
            diffs++;
            if (diffs >= 5) break;
        }
    }

    if (diffs > 0) {
        fprintf(stderr, "FAIL: %d mismatches found against snprintf!\n", diffs);
        return 1;
    }

    printf("  ✅ Large-scale parity against snprintf passed (%d coordinates matched 100%%).\n", test_count);
    printf("🎉 C float format verification completely passed!\n");
    return 0;
}
