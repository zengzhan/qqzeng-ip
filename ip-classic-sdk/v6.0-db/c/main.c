/*
 * qqzeng-ip 6.0 (.db) C 版冒烟示例
 * 用法: main [db_path] [ip ...]
 */
#include "src/qqzeng_ip_search.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static double now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec * 1000.0 + ts.tv_nsec / 1e6;
}

int main(int argc, char** argv)
{
    const char* path = argc > 1 ? argv[1] : NULL;

    double t0 = now_ms();
    ipdb_search_t* db = path ? ipdb_init(path) : ipdb_init_default();
    if (!db)
    {
        fprintf(stderr, "load failed (usage: main [db_path] [ip ...])\n");
        return 1;
    }
    printf("load: %.1f ms, nodes=%u, strings=%u\n",
           now_ms() - t0, db->node_count, db->geoisp_count);

    const char* demo[] = {"1.10.11.12", "8.8.8.8", "58.213.198.68", "255.255.255.255"};
    for (int i = 0; i < 4; i++)
        printf("%s -> %s\n", demo[i], ipdb_find(db, demo[i]));

    for (int i = 2; i < argc; i++)
        printf("%s -> %s\n", argv[i], ipdb_find(db, argv[i]));

    /* 非法输入优雅返回空串 */
    printf("(bad) -> '%s'\n", ipdb_find(db, "1..2.3"));

    /* 快速压测 */
    t0 = now_ms();
    unsigned long long seed = 123456789ULL, checksum = 0;
    const int n = 1000000;
    for (int i = 0; i < n; i++)
    {
        seed = (seed * 48271ULL) % 2147483647ULL;
        checksum += (unsigned long long)strlen(ipdb_find_uint(db, (uint32_t)seed));
    }
    double ms = now_ms() - t0;
    printf("bench: %d queries, %.0f qps (checksum %llu)\n", n, n / (ms / 1000.0), checksum);

    ipdb_free(db);
    return 0;
}
