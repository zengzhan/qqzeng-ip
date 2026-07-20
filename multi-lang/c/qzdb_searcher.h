#ifndef QZDB_IP_SEARCH_H
#define QZDB_IP_SEARCH_H

#include <stdint.h>
#include <stddef.h>

#define QZDB_MAX_FIELDS 32

typedef struct {
    uint8_t* data;
    size_t data_size;
    int group_index;

    // Header fields
    uint16_t flags;
    int has_v4;
    int has_v6;
    int v4_node_24;
    int v6_node_24;
    int v6_jump_bits;
    int pool_count;
    int pool_idx_size;
    int geo_count;
    int row_count;
    uint32_t v4_rec_count;
    uint32_t v6_rec_count;
    uint32_t v4_node_count;
    uint32_t v6_node_count;
    int ip_row_size;
    int geo_entry_group_count;

    // Offsets
    uint64_t off_v4_jump;
    uint64_t off_v4_nodes;
    uint64_t off_v6_jump;
    uint64_t off_v6_nodes;
    uint64_t off_ip_row;
    uint64_t off_geo_entries;
    uint64_t off_pools;
    uint64_t off_meta;
    uint64_t off_row_schema;
    uint64_t off_group_schema;

    // Schema/layout (dynamically sized)
    int actual_groups;
    int* group_field_counts;
    uint32_t* group_entry_counts;
    uint16_t* group_dim_masks;
    uint64_t* group_entry_offsets;

    int* group_strides;
    int** group_field_widths;
    int** group_field_offsets;
    int** group_field_native;
    int** group_field_native_type;

    char**** group_pools;
    int** group_pool_counts;
    int pools_loaded;

    char** field_names;
    int* float_field_flags;
    int field_count;
    char* version_name;
    int version_code;
} qzdb_searcher_t;

typedef struct {
    char* values[QZDB_MAX_FIELDS];
} qzdb_geo_info_t;

int qzdb_init(qzdb_searcher_t* ctx, const char* db_path);
void qzdb_free(qzdb_searcher_t* ctx);
qzdb_searcher_t* qzdb_instance(const char* db_path);
int qzdb_instance_load(const char* db_path);
int qzdb_find(qzdb_searcher_t* ctx, const char* ip_str, qzdb_geo_info_t* result);
int qzdb_find_uint(qzdb_searcher_t* ctx, uint32_t ip_int, qzdb_geo_info_t* result);
int qzdb_find_v6(qzdb_searcher_t* ctx, const uint8_t* ip_bin, qzdb_geo_info_t* result);
int qzdb_find_str(qzdb_searcher_t* ctx, const char* ip_str, char* out, size_t out_size);
int qzdb_verify_crc(qzdb_searcher_t* ctx);

#endif
