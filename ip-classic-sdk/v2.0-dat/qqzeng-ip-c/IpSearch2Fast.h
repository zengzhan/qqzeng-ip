/*
 * qqzeng-ip 2.0 (.dat) 高性能解析库 - C 版
 *
 * 文件结构 (UTF-8, Little-Endian):
 *   文件头  16 字节 (4-4-4-4): [索引区首条偏移][索引区末条偏移][前缀区首偏移][前缀区末偏移]
 *   内容区  唯一不重复的地区字符串,按 (offset,len) 被索引区引用
 *   索引区  12 字节/条 (4-4-3-1): [起始IP][结束IP][地区偏移24b][长度8b]
 *   前缀区   9 字节/条 (1-4-4): [前缀0-255][索引区start][索引区end]
 *
 * 线程安全: 初始化完成后全部状态只读,可多线程并发查询;
 *           getInstance() 首次调用本身需外部串行化(与旧版一致)。
 */

#ifndef IPSEARCH2FAST_H
#define IPSEARCH2FAST_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct IpSearch2Fast
{
    uint32_t  recordCount;   /* 索引区记录数 */
    uint32_t *starts;        /* [recordCount] 每条记录起始 IP */
    uint32_t *ends;          /* [recordCount] 每条记录结束 IP */
    uint32_t *strIndex;      /* [recordCount] 记录 -> strings[] 下标 */
    uint32_t  stringCount;   /* 去重后的字符串个数 */
    char    **strings;       /* 指向 stringPool 内 NUL 结尾字符串 */
    char     *stringPool;    /* 字符串池(去重后一次性连续分配) */
    int32_t   prefMap[256][2]; /* 前缀 -> [start,end] 索引,-1 表示前缀不存在 */
} IpSearch2Fast;

/* 兼容旧版: 默认路径单例(自动探测 qqzeng-ip*.dat 常见位置) */
IpSearch2Fast* getInstance(void);

/* 新增: 指定 .dat 文件路径初始化(可多实例,例如同时加载国内版与全球版) */
IpSearch2Fast* geoip_init(const char* dbPath);

/* 新增: 从内存字节流初始化(不改动 bytes,内部完成解析后自持数据) */
IpSearch2Fast* geoip_init_bytes(const uint8_t* bytes, size_t len);

/* 兼容旧版: 以默认路径加载到已分配的结构体(推荐改用 geoip_init) */
int32_t geoip_load_dat(IpSearch2Fast* p);

/* 释放资源(p 置 NULL 安全,重复释放安全) */
void geoip_free(IpSearch2Fast* p);

/* 查询: 未命中或非法 IP 返回 ""(返回指针归库所有,勿修改/勿 free) */
char* geoip_query(IpSearch2Fast* p, const char* ip);
char* geoip_query_uint(IpSearch2Fast* p, uint32_t ip_num); /* 跳过字符串解析 */

/* 兼容旧版导出 */
uint32_t geoip_binary_search(IpSearch2Fast* p, uint32_t low, uint32_t high, uint32_t ip_num);
int      geoip_parse_ip(const char* ip_str, uint32_t* out); /* 严格解析,成功返回 1 */
uint32_t geoip_ip2long(const char* ip_str); /* 兼容旧版单参数签名,非法返回 0xFFFFFFFF */
uint32_t geoip_read_int32(const uint8_t* buf, int pos);
uint32_t geoip_read_int24(const uint8_t* buf, int pos);

#ifdef __cplusplus
}
#endif

#endif /* IPSEARCH2FAST_H */
