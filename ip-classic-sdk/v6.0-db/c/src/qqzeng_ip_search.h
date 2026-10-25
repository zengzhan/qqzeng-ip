/*
 * qqzeng-ip 6.0 (.db) 高性能解析 - C 版 (C11, 零依赖)
 *
 * 文件结构 (UTF-8):
 *   文件头    4 字节: 节点数量 nodeCount (uint32 小端)
 *   一级索引  65536 条 x 3 字节 (大端 int24): IPv4 前 16 位 -> 节点号或叶子
 *   节点区    nodeCount x 6 字节 (每节点 2 个子指针, 各 3 字节大端 int24)
 *   字符串区  其余部分, '\t' 分隔的 UTF-8 结果串
 *   int24 最高位 (0x800000) 置位 = 叶子, 低 22 位为字符串下标;
 *   否则为节点区跳转下标。查询消耗 IP 后 16 位 (MSB first)。
 *
 * 兼容旧版 API:
 *   ipdb_init(path) / ipdb_find(ctx, ip) / ipdb_find_uint(ctx, ip_int) / ipdb_free(ctx)
 * 新增:
 *   ipdb_init_default() 自动探测默认路径(含 dada/ 目录)
 *   ipdb_init_bytes(bytes, len) 从内存字节流加载
 *
 * 相比历史版本的修复与优化:
 *   1. fastParse_ip 严格化: 旧版接受 "1..2.3"/"1.2.3."/.1.2.3" 等残缺 IP,
 *      且 5 段 IP 触发 val << 负数 的未定义行为; 现按统一严格规则校验。
 *   2. 加载期完整校验 (fail-closed): 尺寸/头字段/全部 65536 一级索引与
 *      全部节点跳转合法性/叶子下标范围, 非法文件直接返回 NULL,
 *      杜绝查询期越界读与死循环。
 *   3. 逐查询 int24 拼装改为加载期一次性预解析为对齐 uint32 数组,
 *      查询路径零拼装; 字符串区 NUL 终结后原文件缓冲整体释放。
 *   4. realloc 失败检查、无符号溢出防护 (nodeCount*6 用 64 位计算)。
 */
#ifndef QQZENG_IP_SEARCH_H
#define QQZENG_IP_SEARCH_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct
{
    uint32_t* first;      /* 65536 条一级索引 (预解析 int24 值) */
    uint32_t* nodes;      /* nodeCount*2 个子指针 (预解析 int24 值) */
    uint32_t node_count;
    char** geoisp_arr;    /* 字符串表 (NUL 终结) */
    uint32_t geoisp_count;
    char* string_pool;    /* geoisp_arr 指向的连续内存 */
} ipdb_search_t;

/* 初始化: 失败返回 NULL (文件不存在/结构非法, fail-closed)。 */
ipdb_search_t* ipdb_init(const char* db_path);

/* 自动探测默认路径初始化 (当前目录与 dada/ 相对目录)。 */
ipdb_search_t* ipdb_init_default(void);

/* 从内存字节流初始化 (不修改也不接管 bytes 的所有权)。 */
ipdb_search_t* ipdb_init_bytes(const void* bytes, size_t len);

/* 查找 IP (返回字符串属于 ctx 管理, 不可 free; 未命中/非法返回 "")。 */
const char* ipdb_find(ipdb_search_t* ctx, const char* ip);

/* 整数查询 (跳过字符串解析, 高性能场景)。 */
const char* ipdb_find_uint(ipdb_search_t* ctx, uint32_t ip_int);

/* 释放资源。 */
void ipdb_free(ipdb_search_t* ctx);

#ifdef __cplusplus
}
#endif

#endif /* QQZENG_IP_SEARCH_H */
