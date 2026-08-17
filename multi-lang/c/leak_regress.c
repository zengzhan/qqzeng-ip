/* 回归测试：加载失败路径不得泄漏堆态。
 *
 * 背景：init_from_buffer 在堆结构（group 数组 / pools / geo_cache / 元数据字符串）
 * 建好之后的错误返回（CRC 失败、OOM、apply_group_meta 失败）曾直接 return，
 * 调用方只归还 data 缓冲，堆分配全部泄漏。修复后统一走 goto fail →
 * free_heap_state()。本测试反复用「CRC 字段被破坏」的文件触发失败路径，
 * 用 malloc_zone_statistics 检查存活分配块数是否回到基线。
 *
 * 构建（注意：不要加 -fsanitize=address，ASan 会替换 malloc zone，
 * malloc_zone_statistics 的读数不可用；Linux CI 上可直接用 LSan 捕获同类泄漏）：
 *   clang -O1 -g -I. leak_regress.c qzdb_reader.c -o leak_regress
 * 运行：
 *   ./leak_regress ../data/qqzeng_ip_std_china.qzdb
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <malloc/malloc.h>
#include "qzdb_reader.h"

static long blocks_in_use(void) {
    malloc_statistics_t st;
    malloc_zone_statistics(NULL, &st);
    return (long)st.blocks_in_use;
}

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: %s db.qzdb\n", argv[0]); return 2; }
    FILE *f = fopen(argv[1], "rb");
    if (!f) return 2;
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    unsigned char *buf = malloc((size_t)n);
    if (fread(buf, 1, (size_t)n, f) != (size_t)n) return 2;
    fclose(f);

    /* 预热（排除一次性全局初始化的干扰） */
    for (int i = 0; i < 5; i++) {
        buf[16] ^= 0xFF;
        qzdb_reader_t ctx;
        if (qzdb_init_buffer(&ctx, buf, (size_t)n, 1) == QZDB_OK) {
            fprintf(stderr, "expected CRC failure\n"); return 1;
        }
        buf[16] ^= 0xFF;
    }
    long base = blocks_in_use();
    for (int i = 0; i < 200; i++) {
        buf[16] ^= 0xFF; /* 破坏 stored CRC → verify_crc 必失败 */
        qzdb_reader_t ctx;
        if (qzdb_init_buffer(&ctx, buf, (size_t)n, 1) == QZDB_OK) {
            fprintf(stderr, "expected CRC failure\n"); return 1;
        }
        buf[16] ^= 0xFF;
    }
    long after = blocks_in_use();
    free(buf);
    printf("blocks base=%ld after=%ld delta=%ld\n", base, after, after - base);
    if (after - base > 50) {
        fprintf(stderr, "LEAK_REGRESS_FAIL: 失败路径泄漏堆分配\n");
        return 1;
    }
    printf("LEAK_REGRESS_PASS\n");
    return 0;
}
