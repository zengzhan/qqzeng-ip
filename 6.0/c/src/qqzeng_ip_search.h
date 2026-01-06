#ifndef QQZENG_IP_SEARCH_H
#define QQZENG_IP_SEARCH_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint8_t *data;
    uint32_t data_size;
    char **geoisp_arr;
    uint32_t geoisp_count;
    // 内部使用
    uint32_t node_count;
} ipdb_search_t;

// 初始化
ipdb_search_t* ipdb_init(const char *db_path);

// 查找IP (返回的字符串指针属于 ipdb_search_t 管理，不可 free)
const char* ipdb_find(ipdb_search_t *ctx, const char *ip);

// 暴露 uint 接口用于公平压测
const char* ipdb_find_uint(ipdb_search_t *ctx, uint32_t ip_int);

// 释放资源
void ipdb_free(ipdb_search_t *ctx);

#ifdef __cplusplus
}
#endif

#endif
