/* Fail-Closed 模糊验证：畸形 .qzdb 必须被拒绝，且不得越界读写。
 * 建议用 AddressSanitizer 编译，越界会被 ASAN 直接捕获：
 *   clang -O1 -g -fsanitize=address,undefined -I. failclosed.c qzdb_reader.c -o failclosed
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "qzdb_reader.h"

static unsigned char *g_full = NULL;
static size_t g_len = 0;

static void probe(const unsigned char *buf, size_t n) {
    qzdb_reader_t ctx;
    memset(&ctx, 0, sizeof(ctx));
    /* borrowed 变体不拷贝，能让 ASAN 精确定位对源缓冲区的越界读 */
    int rc = qzdb_init_buffer_borrowed(&ctx, (const uint8_t *)buf, n, 0);
    if (rc != 0) {
        qzdb_free(&ctx);
        return;
    }
    qzdb_geo_info_t gi;
    qzdb_find(&ctx, "114.114.114.114", &gi);
    qzdb_find(&ctx, "8.8.8.8", &gi);
    qzdb_find(&ctx, "2400:3200::1", &gi);
    char out[512];
    qzdb_find_str(&ctx, "1.2.3.4", out, sizeof(out));
    qzdb_free(&ctx);
}

int main(int argc, char **argv) {
    const char *src = argc > 1 ? argv[1]
                               : "../test_data_202608/ult/china/qqzeng_ip_ult_china.qzdb";
    FILE *f = fopen(src, "rb");
    if (!f) { fprintf(stderr, "cannot open %s\n", src); return 2; }
    fseek(f, 0, SEEK_END);
    g_len = (size_t)ftell(f);
    fseek(f, 0, SEEK_SET);
    g_full = (unsigned char *)malloc(g_len);
    if (fread(g_full, 1, g_len, f) != g_len) { fprintf(stderr, "read fail\n"); return 2; }
    fclose(f);

    unsigned char *m = (unsigned char *)malloc(g_len);

    printf("== 截断测试 ==\n");
    size_t sizes[] = {0, 3, 100, 191, 192, 200, 250, 500, 5000, 100000, 1000000};
    for (size_t i = 0; i < sizeof(sizes) / sizeof(sizes[0]); i++) {
        if (sizes[i] > g_len) continue;
        /* 精确长度分配，让 ASAN 能捕获读越尾部 */
        unsigned char *t = (unsigned char *)malloc(sizes[i] ? sizes[i] : 1);
        memcpy(t, g_full, sizes[i]);
        probe(t, sizes[i]);
        free(t);
    }
    printf("  done\n");

    printf("== 头部 192 字节全域穷举 ==\n");
    unsigned char pats[] = {0x00, 0xFF, 0x7F, 0x80};
    int cases = 0;
    for (size_t pos = 4; pos < 192; pos++) {
        for (int p = 0; p < 4; p++) {
            memcpy(m, g_full, g_len);
            m[pos] = pats[p];
            probe(m, g_len);
            cases++;
        }
    }
    printf("  %d 例 done\n", cases);

    printf("== 字节洪泛（随机位翻转 2000 次）==\n");
    unsigned long long seed = 0x9E3779B97F4A7C15ULL;
    size_t lim = g_len < 512 * 1024 ? g_len : 512 * 1024;
    for (int i = 0; i < 2000; i++) {
        seed = seed * 6364136223846793005ULL + 1442695040888963407ULL;
        size_t pos = (size_t)(seed >> 16) % lim;
        memcpy(m, g_full, g_len);
        m[pos] ^= 0xFF;
        probe(m, g_len);
    }
    printf("  done\n");

    printf("== 尾部随机截断 500 次 ==\n");
    for (int i = 0; i < 500; i++) {
        seed = seed * 6364136223846793005ULL + 1442695040888963407ULL;
        size_t n = (size_t)(seed >> 16) % g_len;
        unsigned char *t = (unsigned char *)malloc(n ? n : 1);
        memcpy(t, g_full, n);
        probe(t, n);
        free(t);
    }
    printf("  done\n");

    free(m);
    free(g_full);
    printf("\n==== 全部用例执行完毕，无 ASAN 报错即 PASS ====\n");
    return 0;
}
