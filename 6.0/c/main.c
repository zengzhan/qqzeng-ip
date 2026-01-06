#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <string.h>
#include <unistd.h>
#include <sys/time.h>
#include "src/qqzeng_ip_search.h"

// 计时辅助
double get_time_ms() {
    struct timeval tv;
    gettimeofday(&tv, NULL);
    return (tv.tv_sec * 1000.0) + (tv.tv_usec / 1000.0);
}

char* find_db_path() {
    static char path[1024];
    const char *attempts[] = {
        "qqzeng-ip-6.0-global.db",
        "../data/qqzeng-ip-6.0-global.db",
        "../../data/qqzeng-ip-6.0-global.db",
        "../../../data/qqzeng-ip-6.0-global.db"
    };
    for (int i = 0; i < 4; i++) {
        if (access(attempts[i], F_OK) == 0) {
            strcpy(path, attempts[i]);
            return path;
        }
    }
    return NULL;
}

char* find_test_file() {
    static char path[1024];
    const char *attempts[] = {
        "test.txt", "../data/test.txt", "../../data/test.txt", "../../../data/test.txt"
    };
    for (int i=0; i<4; i++) {
        if (access(attempts[i], F_OK) == 0) {
            strcpy(path, attempts[i]);
            return path;
        }
    }
    return NULL;
}

void trim_newline(char *str) {
    char *p = strchr(str, '\n'); if (p) *p = '\0';
    p = strchr(str, '\r'); if (p) *p = '\0';
}

uint32_t ip_to_u32(const char *ip) {
    uint32_t a, b, c, d;
    sscanf(ip, "%u.%u.%u.%u", &a, &b, &c, &d);
    return (a<<24)|(b<<16)|(c<<8)|d;
}

void u32_to_ip(uint32_t val, char *buf) {
    sprintf(buf, "%u.%u.%u.%u", val>>24, (val>>16)&0xFF, (val>>8)&0xFF, val&0xFF);
}

int verify(ipdb_search_t *ctx, const char *ip, const char *expected) {
    const char *result = ipdb_find(ctx, ip);
    if (strcmp(result, expected) != 0) {
        printf("[Fail] IP: %s\n", ip);
        printf("  Expected: %s\n", expected);
        printf("  Actual:   %s\n", result);
        return 0;
    }
    return 1;
}

// 简单的 xorshift32 随机数生成器 (保证跨平台一致性，比 rand() 快且好)
static uint32_t xorshift_state = 123;
uint32_t xorshift32() {
	uint32_t x = xorshift_state;
	x ^= x << 13;
	x ^= x >> 17;
	x ^= x << 5;
	xorshift_state = x;
	return x;
}

int main() {
    printf("正在初始化 qqzeng-ip 数据库...\n");
    double start = get_time_ms();
    
    char *db_path = find_db_path();
    if (!db_path) { printf("Fatal: Cannot find database file\n"); return 1; }
    
    ipdb_search_t *ctx = ipdb_init(db_path);
    if (!ctx) { printf("Fatal: Failed to load database\n"); return 1; }
    
    double elapsed = get_time_ms() - start;
    printf("数据库加载完成，耗时: %.2f ms\n", elapsed);
    
    // --- 验证逻辑 ---
    
    char *test_file = find_test_file();
    if (test_file) {
        printf("正在读取测试文件: %s\n", test_file);
        FILE *f = fopen(test_file, "r");
        char line[4096];
        int passed = 0;
        
        while(fgets(line, sizeof(line), f)) {
            trim_newline(line);
            if (strlen(line) == 0) continue;
            char *start_ip = strtok(line, "\t");
            char *end_ip = strtok(NULL, "\t");
            char *expected = strtok(NULL, "\t");
            if (!start_ip || !end_ip || !expected) continue;
            if (verify(ctx, start_ip, expected) && verify(ctx, end_ip, expected)) passed++;
        }
        fclose(f);
        printf("验证完成: 通 %d\n", passed);
    }
    
    // --- 随机压测 ---
    int total_count = 3000000;
    printf("\n生成 %d 个随机 IP (UInt32)...\n", total_count);
    uint32_t *random_ips = (uint32_t*)malloc(sizeof(uint32_t) * total_count);
    for(int i=0; i<total_count; i++) {
        random_ips[i] = xorshift32();
    }
    printf("生成完成，开始压测 (ipdb_find_uint)...\n");
    
    double bench_start = get_time_ms();
    
    for (int i=0; i<total_count; i++) {
        // 使用 volatile 防止被编译器完全优化掉调用 (虽然 ipdb_find_uint 有副作用? 并不，它只返回指针)
        // 为了防止 DCE (Dead Code Elimination)，我们用结果做个 checksum
        const char* res = ipdb_find_uint(ctx, random_ips[i]);
        // 实际上只要编译器看不穿 ipdb_find_uint 里的内存访问，通常不会删掉循环
        // 在同一个 binary 里 static link 可能会 inline 进而看穿
        // 简单 trick:
        if (res[0] == 1) printf(""); 
    }
    
    double bench_elapsed = get_time_ms() - bench_start;
    printf("\n%d 次随机查询耗时: %.2f ms\n", total_count, bench_elapsed);
    printf("QPS: %.2f\n", (double)total_count / (bench_elapsed / 1000.0));
    
    free(random_ips);
    ipdb_free(ctx);
    return 0;
}
