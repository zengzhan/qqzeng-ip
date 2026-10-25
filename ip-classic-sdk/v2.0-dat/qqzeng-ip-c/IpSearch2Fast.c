/*
 * qqzeng-ip 2.0 (.dat) 高性能解析库 - C 实现
 *
 * 相比历史版本的修复与优化:
 *   1. 移除硬编码 uint32_t ipMap[1000000][2] 静态数组(全球版 137 万条必溢出),
 *      改为按文件头计算的动态数组。
 *   2. fopen_s -> 标准 C99 fopen,跨平台;支持自定义路径与内存字节流。
 *   3. 字符串池去重: 全球版 137 万条记录仅 16 万个唯一字符串,去重后一次性
 *      连续分配 + 指针表,内存从数百 MB 降至约 20MB,消除百万次 malloc。
 *   4. 前缀表以 -1 标记不存在的前缀,修复 "0.0.0.0/8 前缀 low=high=0 被误判
 *      未命中" 的老 bug(旧版用 high==0 判断未命中)。
 *   5. fastParseIp 单次扫描零分配,严格校验(空段/越界/非数字/IPv6 均拒绝)。
 *   6. 加载时完整校验文件长度与前缀区间,错误路径 fail-closed。
 */

#include "IpSearch2Fast.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---------------- 内部工具 ---------------- */

static uint32_t readLE32(const uint8_t* b)
{
    return (uint32_t)b[0] | ((uint32_t)b[1] << 8) | ((uint32_t)b[2] << 16) | ((uint32_t)b[3] << 24);
}

uint32_t geoip_read_int32(const uint8_t* buf, int pos)
{
    return readLE32(buf + pos);
}

uint32_t geoip_read_int24(const uint8_t* buf, int pos)
{
    return (uint32_t)buf[pos] | ((uint32_t)buf[pos + 1] << 8) | ((uint32_t)buf[pos + 2] << 16);
}

/*
 * 严格 IPv4 解析: 每段 1~3 位数字且 <=255,恰好 4 段。
 * 拒绝空串/空段("1..2.3")/负数/非数字/IPv6/前后缀垃圾/超长前导零("0008.8.8.8")。
 */
int geoip_parse_ip(const char* ip_str, uint32_t* out)
{
    uint32_t val = 0, result = 0;
    int shift = 24, digits = 0;
    const unsigned char* p = (const unsigned char*)ip_str;

    if (p == NULL)
        return 0;
    for (; *p != '\0'; p++)
    {
        unsigned char c = *p;
        if (c >= '0' && c <= '9')
        {
            val = val * 10u + (uint32_t)(c - '0');
            if (val > 255u)
                return 0;
            digits++;
            if (digits > 3)
                return 0;
        }
        else if (c == '.')
        {
            if (digits == 0 || shift == 0)
                return 0; /* 空段或第 5 段 */
            result |= val << shift;
            val = 0;
            digits = 0;
            shift -= 8;
        }
        else
        {
            return 0;
        }
    }
    if (digits == 0 || shift != 0)
        return 0;
    result |= val;
    *out = result;
    return 1;
}

/* 兼容旧版单参数签名: 非法返回 0xFFFFFFFF(INADDR_NONE 语义) */
uint32_t geoip_ip2long(const char* ip_str)
{
    uint32_t out = 0;
    if (!geoip_parse_ip(ip_str, &out))
        return 0xFFFFFFFFu;
    return out;
}

/* ---------------- 加载 ---------------- */

static uint8_t* read_file(const char* path, size_t* outLen)
{
    FILE* fp = fopen(path, "rb");
    if (fp == NULL)
        return NULL;
    if (fseek(fp, 0, SEEK_END) != 0)
    {
        fclose(fp);
        return NULL;
    }
    long sz = ftell(fp);
    if (sz < 16)
    {
        fclose(fp);
        return NULL;
    }
    rewind(fp);
    uint8_t* buf = (uint8_t*)malloc((size_t)sz);
    if (buf == NULL)
    {
        fclose(fp);
        return NULL;
    }
    if (fread(buf, 1, (size_t)sz, fp) != (size_t)sz)
    {
        free(buf);
        fclose(fp);
        return NULL;
    }
    fclose(fp);
    *outLen = (size_t)sz;
    return buf;
}

static int cmp_u32(const void* a, const void* b)
{
    uint32_t x = *(const uint32_t*)a, y = *(const uint32_t*)b;
    return (x > y) - (x < y);
}

/*
 * 解析核心: 输入完整文件字节(调用后 *data 所有权转移,成功时被释放)。
 * 返回 0 成功;-1 失败(p 内资源已回滚,可安全 geoip_free)。
 */
static int load_from_bytes(IpSearch2Fast* p, uint8_t* data, size_t dataLen)
{
    memset(p, 0, sizeof(*p));
    for (int i = 0; i < 256; i++)
    {
        p->prefMap[i][0] = -1;
        p->prefMap[i][1] = -1;
    }

    uint32_t firstIndex = readLE32(data);
    uint32_t lastIndex  = readLE32(data + 4);
    uint32_t prefStart  = readLE32(data + 8);
    uint32_t prefEnd    = readLE32(data + 12);

    if (firstIndex < 16 || lastIndex < firstIndex || (lastIndex - firstIndex) % 12u != 0u)
        goto fail;
    if (prefEnd < prefStart || prefStart < 16 || (prefEnd - prefStart) % 9u != 0u)
        goto fail;

    uint32_t recordCount = (lastIndex - firstIndex) / 12u + 1u;
    uint32_t prefixCount = (prefEnd - prefStart) / 9u + 1u;
    if ((size_t)firstIndex + 12u * (size_t)recordCount > dataLen)
        goto fail;
    if ((size_t)prefStart + 9u * (size_t)prefixCount > dataLen)
        goto fail;

    /* 前缀表: -1 标记缺失前缀 */
    for (uint32_t k = 0; k < prefixCount; k++)
    {
        const uint8_t* e = data + prefStart + k * 9u;
        uint32_t n = e[0];
        uint32_t lo = readLE32(e + 1);
        uint32_t hi = readLE32(e + 5);
        if (lo > hi || hi >= recordCount)
            goto fail;
        p->prefMap[n][0] = (int32_t)lo;
        p->prefMap[n][1] = (int32_t)hi;
    }

    p->recordCount = recordCount;
    p->starts   = (uint32_t*)malloc(sizeof(uint32_t) * recordCount);
    p->ends     = (uint32_t*)malloc(sizeof(uint32_t) * recordCount);
    p->strIndex = (uint32_t*)malloc(sizeof(uint32_t) * recordCount);
    if (p->starts == NULL || p->ends == NULL || p->strIndex == NULL)
        goto fail;

    /* 第一遍: 记录基本字段 + key = offset<<8|len (offset<2^24, len<2^8) */
    uint32_t* keys = (uint32_t*)malloc(sizeof(uint32_t) * recordCount);
    if (keys == NULL)
        goto fail;
    for (uint32_t i = 0; i < recordCount; i++)
    {
        const uint8_t* r = data + firstIndex + i * 12u;
        uint32_t off = (uint32_t)r[8] | ((uint32_t)r[9] << 8) | ((uint32_t)r[10] << 16);
        uint32_t len = r[11];
        if ((size_t)off + len > dataLen)
        {
            free(keys);
            goto fail;
        }
        p->starts[i] = readLE32(r);
        p->ends[i]   = readLE32(r + 4);
        keys[i]      = (off << 8) | len;
    }

    /* 排序去重,一次性构建字符串池 */
    qsort(keys, recordCount, sizeof(uint32_t), cmp_u32);
    size_t poolBytes = 0;
    uint32_t uniq = 0;
    uint32_t* rank = (uint32_t*)malloc(sizeof(uint32_t) * recordCount); /* 排序位置 -> 唯一下标 */
    if (rank == NULL)
        goto fail;
    for (uint32_t i = 0; i < recordCount; i++)
    {
        if (i == 0 || keys[i] != keys[i - 1])
        {
            poolBytes += (keys[i] & 0xFFu) + 1u; /* len + NUL */
            uniq++;
        }
        rank[i] = uniq - 1u;
    }
    p->stringCount = uniq;
    p->stringPool = (char*)malloc(poolBytes ? poolBytes : 1);
    p->strings    = (char**)malloc(sizeof(char*) * (uniq ? uniq : 1));
    if (p->stringPool == NULL || p->strings == NULL)
    {
        free(rank);
        goto fail;
    }

    /* 第二遍需要 (record -> 唯一下标): 重新扫描记录,用已排序 key 数组二分定位 */
    {
        char* w = p->stringPool;
        uint32_t filled = 0;
        for (uint32_t i = 0; i < recordCount; i++)
        {
            /* keys 已被排序破坏了与记录的对应关系,重新从原始数据取 key */
            const uint8_t* r = data + firstIndex + i * 12u;
            uint32_t off = (uint32_t)r[8] | ((uint32_t)r[9] << 8) | ((uint32_t)r[10] << 16);
            uint32_t len = r[11];
            uint32_t key = (off << 8) | len;

            /* 在排序数组中找 key 的首次出现下标,rank[] 换算成唯一字符串下标 */
            uint32_t lo = 0, hi = recordCount - 1, pos = 0;
            while (lo <= hi)
            {
                uint32_t mid = lo + ((hi - lo) >> 1);
                if (keys[mid] >= key)
                {
                    pos = mid;
                    if (mid == 0)
                        break;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }
            /* 字符串按排序顺序惰性物化(仅在 key 首次出现时写入池) */
            while (filled <= pos)
            {
                if (filled == 0 || keys[filled] != keys[filled - 1])
                {
                    uint32_t k2 = keys[filled];
                    uint32_t o2 = k2 >> 8, l2 = k2 & 0xFFu;
                    memcpy(w, data + o2, l2);
                    w[l2] = '\0';
                    p->strings[rank[filled]] = w;
                    w += l2 + 1u;
                }
                filled++;
            }
            p->strIndex[i] = rank[pos];
        }
    }
    free(rank);
    free(keys);
    free(data); /* 字符串池已物化,原始文件数据不再需要 */
    return 0;

fail:
    free(data);
    geoip_free(p);
    return -1;
}

IpSearch2Fast* geoip_init_bytes(const uint8_t* bytes, size_t len)
{
    if (bytes == NULL || len < 16)
        return NULL;
    uint8_t* copy = (uint8_t*)malloc(len);
    if (copy == NULL)
        return NULL;
    memcpy(copy, bytes, len);
    IpSearch2Fast* p = (IpSearch2Fast*)calloc(1, sizeof(IpSearch2Fast));
    if (p == NULL || load_from_bytes(p, copy, len) != 0)
    {
        free(p);
        return NULL;
    }
    return p;
}

IpSearch2Fast* geoip_init(const char* dbPath)
{
    size_t len = 0;
    uint8_t* data = read_file(dbPath, &len);
    if (data == NULL)
        return NULL;
    IpSearch2Fast* p = (IpSearch2Fast*)calloc(1, sizeof(IpSearch2Fast));
    if (p == NULL || load_from_bytes(p, data, len) != 0)
    {
        free(p);
        return NULL;
    }
    return p;
}

static const char* const kDefaultPaths[] = {
    "qqzeng-ip-utf8.dat",
    "qqzeng-ip.dat",
    "qqzeng-ip-china-utf8.dat",
    "qqzeng-ip-global-utf8.dat",
    "dada/国内精华版-202610/qqzeng-ip-china-utf8.dat",
    "dada/全球旗舰版-202610/qqzeng-ip-global-utf8.dat",
    "../dada/国内精华版-202610/qqzeng-ip-china-utf8.dat",
    "../dada/全球旗舰版-202610/qqzeng-ip-global-utf8.dat",
    "../../dada/国内精华版-202610/qqzeng-ip-china-utf8.dat",
    "../../dada/全球旗舰版-202610/qqzeng-ip-global-utf8.dat",
    NULL
};

static IpSearch2Fast* g_instance = NULL;

IpSearch2Fast* getInstance(void)
{
    if (g_instance != NULL)
        return g_instance;
    for (int i = 0; kDefaultPaths[i] != NULL; i++)
    {
        g_instance = geoip_init(kDefaultPaths[i]);
        if (g_instance != NULL)
            return g_instance;
    }
    return NULL;
}

int32_t geoip_load_dat(IpSearch2Fast* p)
{
    if (p == NULL)
        return -1;
    for (int i = 0; kDefaultPaths[i] != NULL; i++)
    {
        size_t len = 0;
        uint8_t* data = read_file(kDefaultPaths[i], &len);
        if (data == NULL)
            continue;
        if (load_from_bytes(p, data, len) == 0)
            return 0;
    }
    return -1;
}

void geoip_free(IpSearch2Fast* p)
{
    if (p == NULL)
        return;
    if (p == g_instance)
        g_instance = NULL; /* 释放单例后允许重新加载,避免悬挂指针 */
    free(p->starts);
    free(p->ends);
    free(p->strIndex);
    free(p->strings);
    free(p->stringPool);
    memset(p, 0, sizeof(*p));
}

/* ---------------- 查询 ---------------- */

uint32_t geoip_binary_search(IpSearch2Fast* p, uint32_t low, uint32_t high, uint32_t ip_num)
{
    /* 二分逼近: 找最小的 endip >= ip_num 的记录下标 */
    uint32_t M = 0;
    while (low <= high)
    {
        uint32_t mid = low + ((high - low) >> 1);
        uint32_t endipNum = p->ends[mid];
        if (endipNum >= ip_num)
        {
            M = mid;
            if (mid == 0)
                break; /* 防止无符号下溢 */
            high = mid - 1u;
        }
        else
        {
            low = mid + 1u;
        }
    }
    return M;
}

char* geoip_query_uint(IpSearch2Fast* p, uint32_t val)
{
    if (p == NULL)
        return (char*)"";
    int32_t lo = p->prefMap[(val >> 24) & 0xFFu][0];
    if (lo < 0)
        return (char*)""; /* 前缀不存在(绝不能用 high==0 判断) */
    int32_t hi = p->prefMap[(val >> 24) & 0xFFu][1];
    uint32_t idx = (lo == hi) ? (uint32_t)lo : geoip_binary_search(p, (uint32_t)lo, (uint32_t)hi, val);
    if (p->starts[idx] <= val && p->ends[idx] >= val)
        return p->strings[p->strIndex[idx]];
    return (char*)"";
}

char* geoip_query(IpSearch2Fast* p, const char* ip)
{
    uint32_t val;
    if (p == NULL || ip == NULL)
        return (char*)"";
    if (!geoip_parse_ip(ip, &val))
        return (char*)"";
    return geoip_query_uint(p, val);
}
