/*
 * qqzeng-ip 6.0 (.db) 高性能解析 - C 实现
 * 格式与修复说明见 qqzeng_ip_search.h。
 */
#include "qqzeng_ip_search.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define INDEX_START 0x30004u      /* 一级索引之后节点区的起始偏移 */
#define FIRST_COUNT 65536u        /* 一级索引条数 = 2^16 */
#define END_MASK 0x800000u        /* int24 最高位: 叶子标志 */
#define COMPL_MASK 0x7FFFFFu      /* 叶子低 22 位: 字符串下标 */

static uint32_t read_le32(const uint8_t* p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint32_t read_int24(const uint8_t* p)
{
    return ((uint32_t)p[0] << 16) | ((uint32_t)p[1] << 8) | (uint32_t)p[2];
}

/* 严格 IPv4 解析: 每段 1~3 位数字且 <=255, 恰好 4 段。非法返回 -1。 */
static int fast_parse_ip(const char* ip, uint32_t* out)
{
    if (!ip)
        return -1;
    uint32_t val = 0, result = 0;
    int shift = 24, digits = 0;
    for (const char* p = ip; *p; p++)
    {
        if (*p >= '0' && *p <= '9')
        {
            val = val * 10u + (uint32_t)(*p - '0');
            if (val > 255u)
                return -1;
            digits++;
            if (digits > 3)
                return -1; /* 拒绝 "0008.8.8.8" 这类超长前导零 */
        }
        else if (*p == '.')
        {
            if (digits == 0 || shift == 0)
                return -1; /* 空段或第 5 段 */
            result |= val << shift;
            val = 0;
            digits = 0;
            shift -= 8;
        }
        else
        {
            return -1;
        }
    }
    if (digits == 0 || shift != 0)
        return -1;
    *out = result | val;
    return 0;
}

static uint32_t count_strings(const uint8_t* p, size_t n)
{
    if (n == 0)
        return 0;
    uint32_t c = 1;
    for (size_t i = 0; i < n; i++)
        c += (p[i] == '\t');
    return c;
}

/* 校验一个 int24 条目: 叶子下标或节点跳转必须在合法范围内。 */
static int entry_ok(uint32_t v, uint32_t node_count, uint32_t string_count)
{
    if (v & END_MASK)
        return (v & COMPL_MASK) < string_count;
    return v < node_count;
}

static ipdb_search_t* parse_db(const uint8_t* data, size_t len)
{
    if (len < INDEX_START)
        return NULL;

    uint32_t node_count = read_le32(data);
    uint64_t node_area_end = (uint64_t)INDEX_START + (uint64_t)node_count * 6u;
    if (node_area_end > (uint64_t)len || node_count == 0)
        return NULL;
    size_t sa = (size_t)node_area_end;
    size_t sa_len = len - sa;

    uint32_t string_count = count_strings(data + sa, sa_len);
    if (string_count == 0)
        return NULL; /* 至少应有一条记录 (叶子校验依赖非空表) */

    /* 分配安全的上限检查 (node_count*2 个 uint32) */
    if ((uint64_t)node_count > (uint64_t)(SIZE_MAX / 8u))
        return NULL;

    ipdb_search_t* ctx = (ipdb_search_t*)calloc(1, sizeof(ipdb_search_t));
    if (!ctx)
        return NULL;
    ctx->node_count = node_count;
    ctx->geoisp_count = string_count;
    ctx->first = (uint32_t*)malloc(FIRST_COUNT * sizeof(uint32_t));
    ctx->nodes = (uint32_t*)malloc((size_t)node_count * 2u * sizeof(uint32_t));
    ctx->string_pool = (char*)malloc(sa_len + 1u);
    ctx->geoisp_arr = (char**)malloc((size_t)string_count * sizeof(char*));
    if (!ctx->first || !ctx->nodes || !ctx->string_pool || !ctx->geoisp_arr)
    {
        ipdb_free(ctx);
        return NULL;
    }

    /* 一级索引预解析 + 逐条校验 */
    for (uint32_t i = 0; i < FIRST_COUNT; i++)
    {
        uint32_t v = read_int24(data + 4u + i * 3u);
        if (!entry_ok(v, node_count, string_count))
        {
            ipdb_free(ctx);
            return NULL;
        }
        ctx->first[i] = v;
    }

    /* 节点区预解析 + 跳转合法性校验 (fail-closed 核心) */
    {
        const uint8_t* p = data + INDEX_START;
        uint32_t* o = ctx->nodes;
        for (uint32_t i = 0; i < node_count; i++)
        {
            uint32_t a = read_int24(p);
            uint32_t b = read_int24(p + 3);
            if (!entry_ok(a, node_count, string_count) || !entry_ok(b, node_count, string_count))
            {
                ipdb_free(ctx);
                return NULL;
            }
            *o++ = a;
            *o++ = b;
            p += 6;
        }
    }

    /* 字符串区拷贝 + '\t' -> '\0' 终结 (原缓冲此后不再需要) */
    memcpy(ctx->string_pool, data + sa, sa_len);
    ctx->string_pool[sa_len] = '\0';
    {
        char* pool = ctx->string_pool;
        uint32_t idx = 0;
        ctx->geoisp_arr[0] = pool;
        for (size_t i = 0; i < sa_len; i++)
        {
            if (pool[i] == '\t')
            {
                pool[i] = '\0';
                ctx->geoisp_arr[++idx] = pool + i + 1u;
            }
        }
    }
    return ctx;
}

ipdb_search_t* ipdb_init(const char* db_path)
{
    if (!db_path || !db_path[0])
        return NULL;
    FILE* f = fopen(db_path, "rb");
    if (!f)
        return NULL;
    if (fseek(f, 0, SEEK_END) != 0)
    {
        fclose(f);
        return NULL;
    }
    long sz = ftell(f);
    if (sz < 0 || fseek(f, 0, SEEK_SET) != 0)
    {
        fclose(f);
        return NULL;
    }
    size_t len = (size_t)sz;
    uint8_t* buf = (uint8_t*)malloc(len ? len : 1u);
    if (!buf)
    {
        fclose(f);
        return NULL;
    }
    if (fread(buf, 1u, len, f) != len)
    {
        free(buf);
        fclose(f);
        return NULL;
    }
    fclose(f);
    ipdb_search_t* ctx = parse_db(buf, len);
    free(buf);
    return ctx;
}

ipdb_search_t* ipdb_init_bytes(const void* bytes, size_t len)
{
    if (!bytes)
        return NULL;
    return parse_db((const uint8_t*)bytes, len);
}

ipdb_search_t* ipdb_init_default(void)
{
    static const char* dirs[] = {
        ".",
        "dada/全球旗舰版-202610",
        "../dada/全球旗舰版-202610",
        "../../dada/全球旗舰版-202610",
        "../../../dada/全球旗舰版-202610",
        "data",
        "../data",
    };
    for (size_t i = 0; i < sizeof dirs / sizeof dirs[0]; i++)
    {
        char path[512];
        if (dirs[i][0] == '.' && dirs[i][1] == '\0')
            snprintf(path, sizeof path, "qqzeng-ip-6.0-global.db");
        else
            snprintf(path, sizeof path, "%s/qqzeng-ip-6.0-global.db", dirs[i]);
        ipdb_search_t* ctx = ipdb_init(path);
        if (ctx)
            return ctx;
    }
    return NULL;
}

const char* ipdb_find(ipdb_search_t* ctx, const char* ip)
{
    if (!ctx || !ip)
        return "";
    uint32_t val;
    if (fast_parse_ip(ip, &val) != 0)
        return "";
    return ipdb_find_uint(ctx, val);
}

const char* ipdb_find_uint(ipdb_search_t* ctx, uint32_t ip_int)
{
    if (!ctx)
        return "";
    uint32_t record = ctx->first[ip_int >> 16];
    uint32_t suffix = ip_int & 0xFFFFu;
    /* 后缀 16 位 -> 最多 16 跳; 超限说明文件非法 (fail-closed) */
    for (int hops = 0; (record & END_MASK) == 0; hops++)
    {
        if (hops >= 16)
            return "";
        record = ctx->nodes[(size_t)record * 2u + ((suffix >> 15) & 1u)];
        suffix <<= 1;
    }
    uint32_t index = record & COMPL_MASK;
    if (index < ctx->geoisp_count)
        return ctx->geoisp_arr[index];
    return "";
}

void ipdb_free(ipdb_search_t* ctx)
{
    if (ctx)
    {
        free(ctx->first);
        free(ctx->nodes);
        free(ctx->geoisp_arr);
        free(ctx->string_pool);
        free(ctx);
    }
}
