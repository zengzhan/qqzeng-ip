#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <time.h>
#include <math.h>
#include "qzdb_searcher.h"

static uint32_t rand_u32(uint32_t *seed) {
    *seed = *seed * 1664525u + 1013904223u;
    return *seed;
}

static uint32_t rand_u32_mt(uint32_t *state) {
    *state = *state * 1664525u + 1013904223u;
    return *state;
}

void run_bench(const char* name, const char* db_path) {
    qzdb_searcher_t ctx;

    clock_t load_start = clock();
    if (qzdb_init(&ctx, db_path) != 0) {
        printf("  %s: load failed\n", name);
        return;
    }
    double load_ms = (double)(clock() - load_start) * 1000.0 / CLOCKS_PER_SEC;
    printf("  %-12s Load: %.1fms\n", name, load_ms);

    int count = 3000000;
    uint32_t seed = 123;
    uint32_t* ips = malloc(count * sizeof(uint32_t));
    for (int i = 0; i < count; i++) ips[i] = rand_u32(&seed);

    clock_t start = clock();
    qzdb_geo_info_t info;
    for (int i = 0; i < count; i++) qzdb_find_uint(&ctx, ips[i], &info);
    double elapsed = (double)(clock() - start) / CLOCKS_PER_SEC;
    printf("  %-12s V4 Lookup QPS: %.0f\n", name, count / elapsed);

    // V6
    int count6 = 1000000;
    uint32_t v6seed = 456;
    start = clock();
    for (int i = 0; i < count6; i++) {
        v6seed = rand_u32(&v6seed);
        uint64_t hi = ((uint64_t)v6seed << 32) | (uint64_t)(v6seed ^ 0xDEADBEEF);
        uint64_t lo = (uint64_t)(v6seed ^ 0xCAFEBABE) << 32;
        uint8_t ip_bin[16];
        for (int b = 0; b < 8; b++) { ip_bin[b] = (hi >> (56 - b*8)) & 0xFF; ip_bin[8+b] = (lo >> (56 - b*8)) & 0xFF; }
        qzdb_find_v6(&ctx, ip_bin, &info);
    }
    elapsed = (double)(clock() - start) / CLOCKS_PER_SEC;
    printf("  %-12s V6 Lookup QPS: %.0f\n", name, count6 / elapsed);

    free(ips);
    qzdb_free(&ctx);
}

int main() {
    printf("C QPS Benchmarks (M4 Pro)\n");
    run_bench("std_china", "../data/qqzeng_ip_std_china.qzdb");
    run_bench("max_china", "../data/qqzeng_ip_max_china.qzdb");
    run_bench("max_global", "../data/qqzeng_ip_max_global.qzdb");
    return 0;
}
