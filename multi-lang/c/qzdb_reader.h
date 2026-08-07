#ifndef QZDB_IP_SEARCH_H
#define QZDB_IP_SEARCH_H

#include <stdint.h>
#include <stddef.h>
#include <pthread.h>

#define QZDB_MAX_FIELDS 32
#define QZDB_MAX_TRIE_WALK_STEPS 1000
#define QZDB_SENTINEL 0x80000000u
#define QZDB_SENTINEL_MASK_24 0x7FFFFFu
#define QZDB_SENTINEL_MASK_31 0x7FFFFFFFu

/* Per-snapshot bounded decode-cache slot (open addressing). The cached value
 * strings are owned by the snapshot and live until qzdb_free(); a GeoInfo that
 * points at them must NOT free them (no values_mask bit set). */
typedef struct {
    uint64_t key;   /* (group << 40) | entry_id ; 0 == empty */
    char** values;  /* field_count heap strings (persistent for snapshot lifetime) */
    int count;
} qzdb_cache_slot_t;

typedef struct {
    uint8_t* data;
    size_t data_size;
    int data_is_heap;   /* 1 if data was malloc'd (qzdb_init_buffer), else mmap'd */
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

    int row_geo_width;
    int row_asn_width;
    int row_usage_width;

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

    char** field_names;          /* group-0 field names from meta (or fallback) */
    int* float_field_flags;
    int field_count;
    char* version_name;           /* meta type=1 (version list) */
    char* description;            /* meta type=3 */
    char* edition;                /* meta type=4 (primary version), else fallback */
    char* data_month;             /* "yyyy-MM" from Header BuildDate */
    char* build_time_str;         /* "yyyy-MM-dd" from Header BuildDate */
    int build_date;               /* yyyyMMdd (Header offset 32) */
    char** norm_field_names;      /* normalized (lower+strip _/-) field names */

    int version_code;

    /* Per-snapshot bounded GeoInfo decode cache (keyed by group<<40|entry_id). */
    qzdb_cache_slot_t* geo_cache;
    uint32_t geo_cache_cap;
    pthread_mutex_t geo_cache_lock;
} qzdb_reader_t;

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

int qzdb_init(qzdb_reader_t* ctx, const char* db_path);
/* Like qzdb_init, but verify_crc=0 skips the default §10.6 CRC32 check. */
int qzdb_init_ex(qzdb_reader_t* ctx, const char* db_path, int verify_crc);
void qzdb_free(qzdb_reader_t* ctx);
qzdb_reader_t* qzdb_instance(const char* db_path);
int qzdb_instance_load(const char* db_path);
int qzdb_find(qzdb_reader_t* ctx, const char* ip_str, qzdb_geo_info_t* result);
int qzdb_find_uint(qzdb_reader_t* ctx, uint32_t ip_int, qzdb_geo_info_t* result);
int qzdb_find_v6(qzdb_reader_t* ctx, const uint8_t* ip_bin, qzdb_geo_info_t* result);
int qzdb_find_str(qzdb_reader_t* ctx, const char* ip_str, char* out, size_t out_size);
int qzdb_verify_crc(qzdb_reader_t* ctx);

/* Buffer-based APIs */
int qzdb_find_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int,
                       char** values, char (*bufs)[64], int buf_size);
int qzdb_find_v6_buf(qzdb_reader_t* ctx, const uint8_t* ip_bin,
                     char** values, char (*bufs)[64], int buf_size);
int qzdb_find_fields_buf(qzdb_reader_t* ctx, const char* ip_str,
                         const char** field_names,
                         char** values, char (*bufs)[64], int buf_size);
int qzdb_find_fields_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int,
                               const char** field_names,
                               char** values, char (*bufs)[64], int buf_size);

/*
 * Layer 1: Lookup row_id only (trie walk, no data access).
 * Returns row_id (1-based), or 0 if not found.
 */
uint32_t qzdb_lookup_row_id(qzdb_reader_t* ctx, const char* ip_str);

/* Standalone strict IP parser (no DB needed). Returns 1 if valid, 0 otherwise.
 * For IPv4, *is_v4=1 and *v4_out holds the uint32 (network order irrelevant);
 * for IPv6, *is_v4=0 and v6_out[16] holds the 16-byte address. Rejects leading
 * zeros, >255, missing segments, CIDR suffixes, whitespace, zone-ids, etc. */
int qzdb_parse_ip(const char* s, uint32_t* v4_out, uint8_t v6_out[16], int* is_v4);
uint32_t qzdb_lookup_row_id_uint(qzdb_reader_t* ctx, uint32_t ip_int);
uint32_t qzdb_lookup_row_id_v6(qzdb_reader_t* ctx, const uint8_t* ip_bin);

/*
 * Layer 2: Lookup raw entry IDs from a row_id.
 * Fills geo_id, asn_id, usage_id. Returns 0 on success, -1 on error.
 */
int qzdb_lookup_ids(qzdb_reader_t* ctx, uint32_t row_id, qzdb_ids_t* out);

/*
 * Field projection API.
 *
 * Resolve only the fields named in field_names[] (NULL-terminated).
 * Same caller-buffer semantics as qzdb_find_uint_buf.
 * Returns field_count on success, 0 if not found, -1 on error.
 */
int qzdb_find_fields_buf(qzdb_reader_t* ctx, const char* ip_str,
                          const char** field_names,
                          char** values, char (*bufs)[64], int buf_size);
int qzdb_find_fields_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int,
                               const char** field_names,
                               char** values, char (*bufs)[64], int buf_size);

/*
 * Atomic reload — re-initialize from a different .qzdb file.
 * Thread-safe: the new data is loaded completely before swapping pointers.
 * Returns 0 on success, -1 on error (old context unchanged on failure).
 */
int qzdb_reload(qzdb_reader_t* ctx, const char* db_path);

/* Load from an in-memory byte buffer (copy semantics). Pass verify_crc=0 to
 * skip the default CRC32 check (trusted data / benchmarks only). */
int qzdb_init_buffer(qzdb_reader_t* ctx, const uint8_t* buf, size_t len, int verify_crc);

/* Set the active version group index (0 = main group; ASN group is typically
 * 2 with dimensionMask 0x02). Returns 0 on success, QZDB_ERR_INVALID_PARAM if
 * out of range. Must be called before querying; affects all find / lookup APIs. */
int qzdb_set_group_index(qzdb_reader_t* ctx, int group_index);

/* findBytes: query a 16-byte network-order address (IPv6 or IPv4-mapped IPv6).
 * IPv4-mapped addresses are downgraded to the V4 trie. Returns QZDB_OK / error. */
int qzdb_find_bytes(qzdb_reader_t* ctx, const uint8_t ip_bin[16], qzdb_geo_info_t* result);

/* Field-projection queries returning a full qzdb_geo_info_t. `fields` is a
 * NULL-terminated array of field names (case/underscore/hyphen insensitive);
 * NULL or empty array is equivalent to find. Returns QZDB_OK / error. */
int qzdb_find_fields(qzdb_reader_t* ctx, const char* ip_str,
                     const char** fields, qzdb_geo_info_t* result);
int qzdb_find_fields_uint(qzdb_reader_t* ctx, uint32_t ip_int,
                          const char** fields, qzdb_geo_info_t* result);

/* lookupRowIdBytes: 4-byte (IPv4) or 16-byte (IPv6/mapped) address. Returns
 * row_id (0 = not found / invalid length). */
uint32_t qzdb_lookup_row_id_bytes(qzdb_reader_t* ctx, const uint8_t* ip_bytes, int len);

/* CIDR reverse lookup (network reconstructed from trie leaf depth). Writes the
 * CIDR string (e.g. "1.0.1.0/24", "2001:218::/32") into out (RFC 5952 for V6);
 * returns out on success, NULL if the IP is not covered / invalid. */
char* qzdb_lookup_cidr(qzdb_reader_t* ctx, const char* ip_str, char* out, size_t out_size);
char* qzdb_lookup_cidr_uint(qzdb_reader_t* ctx, uint32_t ip_int, char* out, size_t out_size);
char* qzdb_lookup_cidr_bytes(qzdb_reader_t* ctx, const uint8_t* ip_bytes, int len, char* out, size_t out_size);

/* GeoInfo field access. get() normalizes the name (lower-case + strip '_'/'-')
 * and returns the value, or "" if absent (never NULL, never throws).
 * to_pipe() joins all fields with '|' (already-correct strings, no re-format).
 * get_cidr() always returns "" (CIDR is not a stored field). */
const char* qzdb_geo_info_get(qzdb_reader_t* ctx, const qzdb_geo_info_t* info, const char* name);
int qzdb_geo_info_to_pipe(qzdb_reader_t* ctx, const qzdb_geo_info_t* info, char* out, size_t out_size);
/* Free heap-owned strings inside a GeoInfo (safe to call repeatedly / on empty). */
void qzdb_free_geo_info(qzdb_geo_info_t* info);
const char* qzdb_geo_info_get_cidr(void);

/* Metadata introspection. All return "" (empty) or sensible defaults if the
 * file lacks the corresponding metadata; never return NULL. */
const char* qzdb_get_version(qzdb_reader_t* ctx);
const char* qzdb_get_data_month(qzdb_reader_t* ctx);
const char* qzdb_get_edition(qzdb_reader_t* ctx);
const char* qzdb_get_scope(qzdb_reader_t* ctx);          /* always "" (no scope field yet) */
const char* qzdb_get_build_time(qzdb_reader_t* ctx);
const char* qzdb_get_description(qzdb_reader_t* ctx);
/* getFileHash: CRC32 hex (8 lowercase chars) written into out; returns 0 on success. */
int qzdb_get_file_hash(qzdb_reader_t* ctx, char* out, size_t out_size);
const char** qzdb_get_field_names(qzdb_reader_t* ctx);    /* NULL-terminated array */
int qzdb_get_field_count(qzdb_reader_t* ctx);
int qzdb_has_field(qzdb_reader_t* ctx, const char* name);
int qzdb_get_group_count(qzdb_reader_t* ctx);
int qzdb_get_pool_count(qzdb_reader_t* ctx);

#endif
