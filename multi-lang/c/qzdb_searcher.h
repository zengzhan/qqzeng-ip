#ifndef QZDB_IP_SEARCH_H
#define QZDB_IP_SEARCH_H

#include <stdint.h>
#include <stddef.h>

#define QZDB_MAX_FIELDS 32
#define QZDB_MAX_TRIE_WALK_STEPS 1000
#define QZDB_SENTINEL 0x80000000u
#define QZDB_SENTINEL_MASK_24 0x7FFFFFu
#define QZDB_SENTINEL_MASK_31 0x7FFFFFFFu

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

    uint16_t** group_field_ids;
    uint32_t** group_pool_section_ids;

    char**** group_pools;
    int** group_pool_counts;
    int pools_loaded;
    char* pool_arena;  /* single backing store for all pool C-strings (null-terminated views) */

    char** field_names;
    int* float_field_flags;
    int field_count;
    char* version_name;
    int version_code;
} qzdb_searcher_t;

typedef struct {
    char* values[QZDB_MAX_FIELDS];
    uint32_t values_mask;  // bit i = 1 if values[i] is heap-owned and must be freed
} qzdb_geo_info_t;

typedef struct {
    uint32_t geo_id;
    uint32_t asn_id;
    uint32_t usage_id;
} qzdb_ids_t;

/* Error codes */
typedef enum {
    QZDB_OK = 0,
    QZDB_ERR_NOT_FOUND = -1,
    QZDB_ERR_CORRUPTED = -2,
    QZDB_ERR_OUT_OF_MEMORY = -3,
    QZDB_ERR_INVALID_PARAM = -4,
    QZDB_ERR_BAD_HEADER = -5,
    QZDB_ERR_BAD_MAGIC = -6,
    QZDB_ERR_UNSUPPORTED = -7,
    QZDB_ERR_BOUNDS = -8,
} qzdb_error_t;

const char* qzdb_strerror(int error_code);

int qzdb_init(qzdb_searcher_t* ctx, const char* db_path);
void qzdb_free(qzdb_searcher_t* ctx);
qzdb_searcher_t* qzdb_instance(const char* db_path);
int qzdb_instance_load(const char* db_path);
int qzdb_find(qzdb_searcher_t* ctx, const char* ip_str, qzdb_geo_info_t* result);
int qzdb_find_uint(qzdb_searcher_t* ctx, uint32_t ip_int, qzdb_geo_info_t* result);
int qzdb_find_v6(qzdb_searcher_t* ctx, const uint8_t* ip_bin, qzdb_geo_info_t* result);
int qzdb_find_str(qzdb_searcher_t* ctx, const char* ip_str, char* out, size_t out_size);
int qzdb_verify_crc(qzdb_searcher_t* ctx);

/* Buffer-based APIs */
int qzdb_find_uint_buf(qzdb_searcher_t* ctx, uint32_t ip_int,
                       char** values, char (*bufs)[64], int buf_size);
int qzdb_find_v6_buf(qzdb_searcher_t* ctx, const uint8_t* ip_bin,
                     char** values, char (*bufs)[64], int buf_size);
int qzdb_find_fields_buf(qzdb_searcher_t* ctx, const char* ip_str,
                         const char** field_names,
                         char** values, char (*bufs)[64], int buf_size);
int qzdb_find_fields_uint_buf(qzdb_searcher_t* ctx, uint32_t ip_int,
                               const char** field_names,
                               char** values, char (*bufs)[64], int buf_size);

/*
 * Layer 1: Lookup row_id only (trie walk, no data access).
 * Returns row_id (1-based), or 0 if not found.
 */
uint32_t qzdb_lookup_row_id(qzdb_searcher_t* ctx, const char* ip_str);
uint32_t qzdb_lookup_row_id_uint(qzdb_searcher_t* ctx, uint32_t ip_int);
uint32_t qzdb_lookup_row_id_v6(qzdb_searcher_t* ctx, const uint8_t* ip_bin);

/*
 * Layer 2: Lookup raw entry IDs from a row_id.
 * Fills geo_id, asn_id, usage_id. Returns 0 on success, -1 on error.
 */
int qzdb_lookup_ids(qzdb_searcher_t* ctx, uint32_t row_id, qzdb_ids_t* out);

/*
 * Field projection API.
 *
 * Resolve only the fields named in field_names[] (NULL-terminated).
 * Same caller-buffer semantics as qzdb_find_uint_buf.
 * Returns field_count on success, 0 if not found, -1 on error.
 */
int qzdb_find_fields_buf(qzdb_searcher_t* ctx, const char* ip_str,
                          const char** field_names,
                          char** values, char (*bufs)[64], int buf_size);
int qzdb_find_fields_uint_buf(qzdb_searcher_t* ctx, uint32_t ip_int,
                               const char** field_names,
                               char** values, char (*bufs)[64], int buf_size);

/*
 * Atomic reload — re-initialize from a different .qzdb file.
 * Thread-safe: the new data is loaded completely before swapping pointers.
 * Returns 0 on success, -1 on error (old context unchanged on failure).
 */
int qzdb_reload(qzdb_searcher_t* ctx, const char* db_path);

#endif
