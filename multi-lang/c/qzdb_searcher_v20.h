#ifndef QZDB_IP_SEARCH_V20_H
#define QZDB_IP_SEARCH_V20_H

#include <stdint.h>
#include <stddef.h>

#define QZDB_V20_MAX_FIELDS 64
#define QZDB_V20_MAX_GROUPS 8

typedef struct {
    uint8_t* data;
    size_t data_size;
    uint8_t fmt_ver;
    uint16_t flags;
    uint8_t has_v4;
    uint8_t has_v6;
    uint8_t v6_jump_bits;
    uint8_t pool_count;
    uint8_t pool_idx_size;
    uint32_t geo_count;
    uint32_t row_count;
    uint32_t v4_node_count;
    uint32_t v6_node_count;
    uint32_t ip_row_size;
    uint32_t geo_entry_group_count;

    uint64_t off_v4_jump;
    uint64_t off_v4_nodes;
    uint64_t off_v6_jump;
    uint64_t off_v6_nodes;
    uint64_t off_ip_row;
    uint64_t off_geo_entries;
    uint64_t off_pools;
    uint64_t off_meta;

    // Group metadata
    int actual_groups;
    uint8_t group_field_counts[QZDB_V20_MAX_GROUPS];
    uint32_t group_entry_counts[QZDB_V20_MAX_GROUPS];
    uint16_t group_dim_masks[QZDB_V20_MAX_GROUPS];
    uint64_t group_entry_offsets[4]; // relative to off_geo_entries

    // Pools: group_pools[groupIdx][fieldIdx][stringIdx]
    char**** group_pools;
    int* group_pool_counts;   // [groupIdx][fieldIdx]
    int* group_field_counts_arr;

    // Field names
    char** field_names;
    uint8_t* float_field_flags;
    int field_names_count;

    char* version_name;
    int group_index;
    uint8_t pools_loaded;
} qzdb_searcher_v20_t;

typedef struct {
    char* values[QZDB_V20_MAX_FIELDS];
} qzdb_geo_info_v20_t;

int qzdb_v20_init(qzdb_searcher_v20_t* ctx, const char* db_path);
int qzdb_v20_init_group(qzdb_searcher_v20_t* ctx, const char* db_path, int group_index);
void qzdb_v20_free(qzdb_searcher_v20_t* ctx);
void qzdb_v20_set_group(qzdb_searcher_v20_t* ctx, int group_index);
int qzdb_v20_find(qzdb_searcher_v20_t* ctx, const char* ip_str, qzdb_geo_info_v20_t* result);
int qzdb_v20_find_uint(qzdb_searcher_v20_t* ctx, uint32_t ip_int, qzdb_geo_info_v20_t* result);
int qzdb_v20_find_str(qzdb_searcher_v20_t* ctx, const char* ip_str, char* out, size_t out_size);
int qzdb_v20_find_v6(qzdb_searcher_v20_t* restrict ctx, uint64_t ip_high, uint64_t ip_low, qzdb_geo_info_v20_t* restrict result);
int qzdb_v20_verify_crc(qzdb_searcher_v20_t* ctx);

#endif
