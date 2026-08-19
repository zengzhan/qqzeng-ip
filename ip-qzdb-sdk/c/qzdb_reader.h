#ifndef QZDB_READER_H
#define QZDB_READER_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>
#include <stddef.h>
#include <pthread.h>

#define QZDB_MAX_FIELDS 32
#define QZDB_MAX_TRIE_WALK_STEPS 1000
#define QZDB_SENTINEL 0x80000000u
#define QZDB_SENTINEL_MASK_24 0x7FFFFFu
#define QZDB_SENTINEL_MASK_31 0x7FFFFFFFu

/* ---- Field normalization hash table (O(1) lookup, spec §6.1) ---- */
typedef struct {
    uint32_t hash;          /* FNV-1a of normalized name */
    int      index;         /* index into ctx->field_names */
} qzdb_norm_entry_t;

typedef struct {
    qzdb_norm_entry_t* buckets;
    uint32_t           cap;     /* power of two */
    uint32_t           mask;    /* cap - 1 */
    uint32_t           count;
} qzdb_norm_map_t;

/* Per-snapshot bounded decode cache.
 *
 * LIFETIME CONTRACT (load-bearing — do not weaken):
 *   A cache entry is built to completion *before* it becomes reachable, and is
 *   then immutable and never freed until qzdb_free(). qzdb_find() hands the
 *   caller borrowed pointers into the entry (values_mask bit stays clear, so
 *   qzdb_free_geo_info() will not free them). That borrow is only sound while
 *   nothing can free the strings underneath it — hence: THE CACHE NEVER EVICTS.
 *
 *   Any "evict + free + re-decode" scheme (whether guarded by one global mutex
 *   or by per-slot mutexes) turns every previously returned qzdb_geo_info_t
 *   into a dangling pointer. That is a use-after-free, trivially reproducible
 *   with qzdb_find_batch(), and it is why this cache is fill-only.
 *
 *   When the probe window is exhausted the lookup simply reports a miss and
 *   the caller falls back to get_geo_info(), which returns caller-owned
 *   strings with the proper values_mask bits set. Bounded memory, no eviction.
 */
typedef struct qzdb_cache_entry {
    uint64_t key;     /* (group << 40) | entry_id */
    char**   values;  /* count heap strings, alive for the snapshot's lifetime */
    int      count;
} qzdb_cache_entry_t;

typedef struct {
    uint8_t* data;
    size_t   data_size;
    int      data_is_heap;   /* 1 if data was malloc'd (qzdb_init_buffer), else mmap'd */
    int      data_is_borrowed; /* 1 if data points into caller-owned buffer (qzdb_init_buffer_borrowed): skip free/munmap in qzdb_free */
    int      group_index;

    // Header fields
    uint16_t version_mask;   /* offset 6: one-hot 版本位掩码（档次判定权威来源） */
    uint16_t flags;
    int      has_v4;
    int      has_v6;
    int      v4_node_24;
    int      v6_node_24;
    int      v6_jump_bits;
    int      pool_count;
    int      pool_idx_size;
    int      geo_count;
    int      row_count;
    uint32_t v4_rec_count;
    uint32_t v6_rec_count;
    uint32_t v4_node_count;
    uint32_t v6_node_count;
    int      ip_row_size;
    int      geo_entry_group_count;

    int row_geo_width;
    int row_asn_width;
    int row_usage_width;

    uint64_t off_v4_jump;
    uint64_t off_v4_nodes;
    uint64_t off_v6_jump;
    uint64_t off_v6_nodes;
    /* 段基址指针（data + off_*）：init 已验证节点段整体在界内且
     * get_*_child 入口已挡 node_idx >= node_count，热路径子节点读取
     * 因此可直读基址、免去 safe_read_* 的每次全量边界检查。 */
    const uint8_t* v4_nodes_base;
    const uint8_t* v6_nodes_base;
    uint64_t off_ip_row;
    uint64_t off_geo_entries;
    uint64_t off_pools;
    uint64_t off_meta;
    uint64_t off_row_schema;
    uint64_t off_group_schema;

    // Schema/layout (dynamically sized)
    int      actual_groups;
    int*     group_field_counts;
    uint32_t* group_entry_counts;
    uint16_t* group_dim_masks;
    uint64_t* group_entry_offsets;

    int*     group_strides;
    int**    group_field_widths;
    int**    group_field_offsets;
    int**    group_field_native;
    int**    group_field_native_type;

    uint16_t*  group_ids;              /* GROUP_SCHEMA.groupId：每组 one-hot 掩码 */
    uint32_t** group_pool_section_ids;

    char**** group_pools;
    int**    group_pool_counts;
    int      pools_loaded;
    char*    pool_arena;

    /* 每组字段名表（owned）。field_names 借用当前 group_index 对应的那一行，
     * 因此 qzdb_free 只释放 group_field_names，不重复释放 field_names。 */
    char***  group_field_names;
    const char** group_editions;        /* 静态字符串，无需释放 */
    const char** group_edition_sources;
    const char** group_name_sources;

    char**   field_names;               /* 借用 group_field_names[group_index] */
    int*     float_field_flags;
    int      field_count;
    char*    version_name;
    char*    description;
    char*    edition;
    const char* edition_source;         /* version_mask / metadata / inferred / unknown */
    const char* field_names_source;     /* metadata / edition / synthetic */
    char*    data_month;
    char*    build_time_str;
    int      build_date;
    char**   norm_field_names;

    qzdb_norm_map_t norm_map;   /* O(1) normalized-name → index */

    int version_code;

    uint32_t file_crc;
    int      crc_valid;

    /* Per-snapshot bounded GeoInfo decode cache (keyed by group<<40|entry_id).
     * Slot array of entry pointers; NULL == empty. Readers do a plain acquire
     * load, writers publish with a release CAS — no mutex on the query path,
     * which is what makes the "lock-free concurrent queries" claim true for C.
     * Declared as a plain pointer (not _Atomic) so this header stays valid C++
     * for the extern "C" consumers; the atomicity lives in qzdb_reader.c via
     * the __atomic_* builtins. */
    qzdb_cache_entry_t** geo_cache;
    uint32_t             geo_cache_cap;
} qzdb_reader_t;

typedef struct {
    char*    values[QZDB_MAX_FIELDS];
    uint32_t values_mask;  /* bit i = 1 if values[i] is heap-owned and must be freed */
} qzdb_geo_info_t;

typedef struct {
    uint32_t geo_id;
    uint32_t asn_id;
    uint32_t usage_id;
} qzdb_ids_t;

/* Error codes */
typedef enum {
    QZDB_OK              =  0,
    QZDB_ERR_NOT_FOUND   = -1,
    QZDB_ERR_CORRUPTED   = -2,
    QZDB_ERR_OUT_OF_MEMORY = -3,
    QZDB_ERR_INVALID_PARAM = -4,
    QZDB_ERR_BAD_HEADER  = -5,
    QZDB_ERR_BAD_MAGIC   = -6,
    QZDB_ERR_UNSUPPORTED = -7,
    QZDB_ERR_BOUNDS      = -8,
} qzdb_error_t;

/* ---- Batch result (spec §8.2) ---- */
typedef struct {
    qzdb_geo_info_t info;
    int             error_code;
} qzdb_batch_result_t;

typedef void (*qzdb_find_callback)(int index, const qzdb_batch_result_t* result, void* user_data);

/* ---- ChainedReader (spec §9) ---- */
typedef struct qzdb_chain qzdb_chain_t;

/* ---- Registry (spec §3.2) ---- */
typedef struct qzdb_registry qzdb_registry_t;

const char* qzdb_strerror(int error_code);

/* ---- Lifecycle (canonical names; spec-compliant aliases below) ---- */
int  qzdb_init(qzdb_reader_t* ctx, const char* db_path);
int  qzdb_init_ex(qzdb_reader_t* ctx, const char* db_path, int verify_crc);
void qzdb_free(qzdb_reader_t* ctx);

/* Buffer-based loading — default copy semantics (spec §4.1) */
int  qzdb_init_buffer(qzdb_reader_t* ctx, const uint8_t* buf, size_t len, int verify_crc);

/* Zero-copy variant — caller must keep buf alive & unchanged until qzdb_free */
int  qzdb_init_buffer_borrowed(qzdb_reader_t* ctx, const uint8_t* buf, size_t len, int verify_crc);

int  qzdb_reload(qzdb_reader_t* ctx, const char* db_path);
int  qzdb_reload_buffer(qzdb_reader_t* ctx, const uint8_t* buf, size_t len);

int  qzdb_set_group_index(qzdb_reader_t* ctx, int group_index);
int  qzdb_verify_crc(qzdb_reader_t* ctx);

/* ---- Query ---- */
int      qzdb_find(qzdb_reader_t* ctx, const char* ip_str, qzdb_geo_info_t* result);
int      qzdb_find_uint(qzdb_reader_t* ctx, uint32_t ip_int, qzdb_geo_info_t* result);
int      qzdb_find_v6(qzdb_reader_t* ctx, const uint8_t* ip_bin, qzdb_geo_info_t* result);
int      qzdb_find_bytes(qzdb_reader_t* ctx, const uint8_t ip_bin[16], qzdb_geo_info_t* result);
int      qzdb_find_str(qzdb_reader_t* ctx, const char* ip_str, char* out, size_t out_size);
int      qzdb_find_fields(qzdb_reader_t* ctx, const char* ip_str,
                          const char** fields, qzdb_geo_info_t* result);
int      qzdb_find_fields_uint(qzdb_reader_t* ctx, uint32_t ip_int,
                               const char** fields, qzdb_geo_info_t* result);

/* ---- Batch & streaming (spec §8) ---- */
int  qzdb_find_batch(qzdb_reader_t* ctx, const char** ips, int count,
                     qzdb_batch_result_t* results);
int  qzdb_find_each(qzdb_reader_t* ctx, const char** ips, int count,
                    qzdb_find_callback cb, void* user_data);

/* Caller-buffer (zero-heap-allocation) variants */
int  qzdb_find_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int,
                        char** values, char (*bufs)[64], int buf_size);
int  qzdb_find_v6_buf(qzdb_reader_t* ctx, const uint8_t* ip_bin,
                      char** values, char (*bufs)[64], int buf_size);
int  qzdb_find_fields_buf(qzdb_reader_t* ctx, const char* ip_str,
                          const char** field_names,
                          char** values, char (*bufs)[64], int buf_size);
int  qzdb_find_fields_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int,
                               const char** field_names,
                               char** values, char (*bufs)[64], int buf_size);

/* ---- Low-level lookups ---- */
uint32_t qzdb_lookup_row_id(qzdb_reader_t* ctx, const char* ip_str);
uint32_t qzdb_lookup_row_id_uint(qzdb_reader_t* ctx, uint32_t ip_int);
uint32_t qzdb_lookup_row_id_v6(qzdb_reader_t* ctx, const uint8_t* ip_bin);
uint32_t qzdb_lookup_row_id_bytes(qzdb_reader_t* ctx, const uint8_t* ip_bytes, int len);
int      qzdb_lookup_ids(qzdb_reader_t* ctx, uint32_t row_id, qzdb_ids_t* out);
int      qzdb_parse_ip(const char* s, uint32_t* v4_out, uint8_t v6_out[16], int* is_v4);

/* ---- CIDR reverse lookup ---- */
char* qzdb_lookup_cidr(qzdb_reader_t* ctx, const char* ip_str, char* out, size_t out_size);
char* qzdb_lookup_cidr_uint(qzdb_reader_t* ctx, uint32_t ip_int, char* out, size_t out_size);
char* qzdb_lookup_cidr_bytes(qzdb_reader_t* ctx, const uint8_t* ip_bytes, int len,
                             char* out, size_t out_size);

/* ---- GeoInfo access ---- */
const char* qzdb_geo_info_get(qzdb_reader_t* ctx, const qzdb_geo_info_t* info, const char* name);
int         qzdb_geo_info_to_pipe(qzdb_reader_t* ctx, const qzdb_geo_info_t* info,
                                  char* out, size_t out_size);
const char* qzdb_geo_info_get_cidr(void);
void        qzdb_free_geo_info(qzdb_geo_info_t* info);

/* ---- Metadata introspection ---- */
const char*  qzdb_get_version(qzdb_reader_t* ctx);
const char*  qzdb_get_data_month(qzdb_reader_t* ctx);
const char*  qzdb_get_edition(qzdb_reader_t* ctx);
const char*  qzdb_get_scope(qzdb_reader_t* ctx);
const char*  qzdb_get_build_time(qzdb_reader_t* ctx);
const char*  qzdb_get_description(qzdb_reader_t* ctx);
int          qzdb_get_file_hash(qzdb_reader_t* ctx, char* out, size_t out_size);
const char** qzdb_get_field_names(qzdb_reader_t* ctx);
int          qzdb_get_field_count(qzdb_reader_t* ctx);
int          qzdb_has_field(qzdb_reader_t* ctx, const char* name);
int          qzdb_get_group_count(qzdb_reader_t* ctx);
int          qzdb_get_pool_count(qzdb_reader_t* ctx);
/* Header.VersionMask 原值（offset 6）。one-hot:
 * bit0=std bit1=asn bit2=pro bit3=max bit4=ult */
uint16_t     qzdb_get_version_mask(qzdb_reader_t* ctx);
/* qzdb_get_edition() 的判定依据：version_mask / metadata / inferred / unknown */
const char*  qzdb_get_edition_source(qzdb_reader_t* ctx);
/* qzdb_get_field_names() 的来源：metadata / edition / synthetic */
const char*  qzdb_get_field_names_source(qzdb_reader_t* ctx);
/* one-hot 掩码 → 档次名；非 one-hot 或越界返回 "" */
const char*  qzdb_edition_from_mask(uint16_t mask);

/* ---- UsageType helpers (spec §6.4) ---- */
/* Resolves usage_type by field name (correct). Requires ctx to map the
 * geo_info values[] array (which is schema-field-ordered) back to the
 * usage_type field. */
const char* qzdb_geo_usage_type(qzdb_reader_t* ctx, const qzdb_geo_info_t* info);
int         qzdb_usage_type_is_known(const char* raw);
const char* qzdb_usage_type_display_zh(const char* raw);
const char* qzdb_usage_type_display_en(const char* raw);
const char* qzdb_usage_type_description(const char* raw);

/* ---- ChainedReader (spec §9) ---- */
qzdb_chain_t* qzdb_chain_new(qzdb_reader_t** ctxs, int count, int mode);
int           qzdb_chain_find(qzdb_chain_t* chain, const char* ip, qzdb_geo_info_t* out);
int           qzdb_chain_find_uint(qzdb_chain_t* chain, uint32_t ip, qzdb_geo_info_t* out);
int           qzdb_chain_find_bytes(qzdb_chain_t* chain, const uint8_t ip16[16], qzdb_geo_info_t* out);
int           qzdb_chain_find_batch(qzdb_chain_t* chain, const char** ips, int count,
                                   qzdb_batch_result_t* results);
int           qzdb_chain_find_str(qzdb_chain_t* chain, const char* ip, char* buf, size_t size);
const char**  qzdb_chain_editions(qzdb_chain_t* chain, int* count);
const char**  qzdb_chain_scopes(qzdb_chain_t* chain, int* count);
const char**  qzdb_chain_data_months(qzdb_chain_t* chain, int* count);
void          qzdb_chain_free(qzdb_chain_t* chain);

/* Chain mode constants */
#define QZDB_CHAIN_FALLBACK        0
#define QZDB_CHAIN_MERGE           1
#define QZDB_CHAIN_MERGE_OVERRIDE  2

/* ---- Registry (spec §3.2) ---- */
qzdb_registry_t* qzdb_registry_new(void);
void             qzdb_registry_free(qzdb_registry_t* reg);
int              qzdb_registry_register(qzdb_registry_t* reg, const char* name, const char* path);
int              qzdb_registry_register_buffer(qzdb_registry_t* reg, const char* name,
                                              const uint8_t* buffer, size_t size);
qzdb_reader_t*   qzdb_registry_get(qzdb_registry_t* reg, const char* name);
void             qzdb_registry_unregister(qzdb_registry_t* reg, const char* name);
int              qzdb_registry_count(qzdb_registry_t* reg);

/* ---- Spec-compliant aliases (Appendix A.8 naming) ---- */
#define qzdb_open(path, ctxptr)         qzdb_init_ex(ctxptr, path, 1)
#define qzdb_open_buffer(buf, sz, ctxptr) qzdb_init_buffer(ctxptr, buf, sz, 1)
#define qzdb_open_buffer_borrowed(buf, sz, ctxptr) qzdb_init_buffer_borrowed(ctxptr, buf, sz, 1)
#define qzdb_open_ex(path, crc, group, ctxptr) qzdb_open_ex_impl(path, crc, group, ctxptr)
#define qzdb_close(ctx)                 qzdb_free(ctx)
static inline int qzdb_open_ex_impl(const char* path, int verify_crc, int group_index, qzdb_reader_t* ctx) {
    int rc = qzdb_init_ex(ctx, path, verify_crc);
    if (rc == QZDB_OK && group_index > 0) rc = qzdb_set_group_index(ctx, group_index);
    return rc;
}

#ifdef __cplusplus
}
#endif

#endif
