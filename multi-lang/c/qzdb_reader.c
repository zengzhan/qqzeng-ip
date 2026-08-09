#define _DARWIN_C_SOURCE
#include "qzdb_reader.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>
#include <arpa/inet.h>
#include <math.h>
#include <pthread.h>
#include <locale.h>

/* ========================================================================
 * Error messages
 * ======================================================================== */
static const char* error_messages[] = {
    "Success", "Not found", "Corrupted data", "Out of memory",
    "Invalid parameter", "Bad header", "Bad magic", "Unsupported format",
    "Bounds check failed"
};

const char* qzdb_strerror(int error_code) {
    if (error_code >= 0 && error_code < (int)(sizeof(error_messages)/sizeof(error_messages[0])))
        return error_messages[error_code];
    return "Unknown error";
}

/* ========================================================================
 * CRC32
 * ======================================================================== */
static uint32_t crc32_table[256];
static int crc32_ready = 0;

static void crc32_init(void) {
    for (uint32_t i = 0; i < 256; i++) {
        uint32_t c = i;
        for (int j = 0; j < 8; j++)
            c = (c & 1) ? (c >> 1) ^ 0xEDB88320 : c >> 1;
        crc32_table[i] = c;
    }
    crc32_ready = 1;
}

static uint32_t crc32_update(uint32_t crc, const uint8_t* buf, size_t len) {
    if (!crc32_ready) crc32_init();
    for (size_t i = 0; i < len; i++)
        crc = crc32_table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
    return crc;
}

static uint32_t crc32_compute_file(const uint8_t* d, size_t size) {
    if (size < 20) return 0;
    uint32_t crc = 0xFFFFFFFF;
    crc = crc32_update(crc, d, 16);
    uint8_t zeros[4] = {0, 0, 0, 0};
    crc = crc32_update(crc, zeros, 4);
    crc = crc32_update(crc, d + 20, size - 20);
    return crc ^ 0xFFFFFFFF;
}

/* ========================================================================
 * Safe read helpers with bounds checking
 * ======================================================================== */
static int safe_read_u16(const uint8_t* data, size_t data_size, uint64_t off, uint16_t* out) {
    if (off + 2 > data_size) return QZDB_ERR_BOUNDS;
    *out = (uint16_t)data[off] | ((uint16_t)data[off+1] << 8);
    return QZDB_OK;
}

static int safe_read_u24(const uint8_t* data, size_t data_size, uint64_t off, uint32_t* out) {
    if (off + 3 > data_size) return QZDB_ERR_BOUNDS;
    *out = (uint32_t)data[off] | ((uint32_t)data[off+1] << 8) | ((uint32_t)data[off+2] << 16);
    return QZDB_OK;
}

static int safe_read_u32(const uint8_t* data, size_t data_size, uint64_t off, uint32_t* out) {
    if (off + 4 > data_size) return QZDB_ERR_BOUNDS;
    *out = (uint32_t)data[off] | ((uint32_t)data[off+1] << 8) | ((uint32_t)data[off+2] << 16) | ((uint32_t)data[off+3] << 24);
    return QZDB_OK;
}

static int safe_read_u64(const uint8_t* data, size_t data_size, uint64_t off, uint64_t* out) {
    if (off + 8 > data_size) return QZDB_ERR_BOUNDS;
    uint32_t lo, hi;
    if (safe_read_u32(data, data_size, off, &lo) != QZDB_OK) return QZDB_ERR_BOUNDS;
    if (safe_read_u32(data, data_size, off+4, &hi) != QZDB_OK) return QZDB_ERR_BOUNDS;
    *out = (uint64_t)lo | ((uint64_t)hi << 32);
    return QZDB_OK;
}

#define READ_LE16(p) ((uint16_t)(p)[0] | ((uint16_t)(p)[1] << 8))
#define READ_LE32(p) ((uint32_t)(p)[0] | ((uint32_t)(p)[1] << 8) | ((uint32_t)(p)[2] << 16) | ((uint32_t)(p)[3] << 24))
#define READ_LE64(p) ((uint64_t)READ_LE32(p) | ((uint64_t)READ_LE32((p)+4) << 32))
#define READ_LE48(p) ((uint64_t)READ_LE32(p) | ((uint64_t)(p)[4] << 32) | ((uint64_t)(p)[5] << 40))

static int safe_read_uint_width(const uint8_t* data, size_t data_size, uint64_t off, int w, uint32_t* out) {
    if (w <= 1) {
        if (off >= data_size) return QZDB_ERR_BOUNDS;
        *out = data[off];
    } else if (w == 2) {
        uint16_t v; int r = safe_read_u16(data, data_size, off, &v);
        if (r != QZDB_OK) return r; *out = v;
    } else if (w == 3) {
        return safe_read_u24(data, data_size, off, out);
    } else {
        return safe_read_u32(data, data_size, off, out);
    }
    return QZDB_OK;
}

/* ========================================================================
 * Field name normalization: lowercase + strip '_' and '-' (spec §6.1)
 * ======================================================================== */
static void normalize_field_name(const char* name, char* out, size_t out_size) {
    size_t j = 0;
    if (!name) { if (out_size) out[0] = '\0'; return; }
    for (size_t i = 0; name[i] && j + 1 < out_size; i++) {
        char c = name[i];
        if (c == '_' || c == '-') continue;
        if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
        out[j++] = c;
    }
    out[j] = '\0';
}

/* FNV-1a hash for normalized field names */
static uint32_t fnv1a(const char* s) {
    uint32_t h = 2166136261u;
    for (; *s; s++) { h ^= (uint8_t)*s; h *= 16777619u; }
    return h;
}

/* Build O(1) normalized-name → index hash table (spec §6.1 performance mandate) */
static void norm_map_build(qzdb_reader_t* ctx) {
    int n = ctx->field_count;
    ctx->norm_map.cap = 1;
    while (ctx->norm_map.cap < (uint32_t)(n * 4)) ctx->norm_map.cap <<= 1;  /* 2x load factor */
    ctx->norm_map.mask = ctx->norm_map.cap - 1;
    ctx->norm_map.count = 0;
    ctx->norm_map.buckets = calloc(ctx->norm_map.cap, sizeof(qzdb_norm_entry_t));
    if (!ctx->norm_map.buckets) return;
    /* initialize keys to 0 (== empty since FNV-1a never yields 0 for non-empty strings) */
    for (int i = 0; i < n; i++) {
        const char* nn = ctx->norm_field_names[i];
        if (!nn) continue;
        uint32_t h = fnv1a(nn);
        if (h == 0) h = 1;  /* reserve 0 as "empty" sentinel */
        uint32_t idx = h & ctx->norm_map.mask;
        while (ctx->norm_map.buckets[idx].hash != 0) {
            idx = (idx + 1) & ctx->norm_map.mask;
        }
        ctx->norm_map.buckets[idx].hash = h;
        ctx->norm_map.buckets[idx].index = i;
        ctx->norm_map.count++;
    }
}

static void norm_map_free(qzdb_reader_t* ctx) {
    free(ctx->norm_map.buckets);
    ctx->norm_map.buckets = NULL;
    ctx->norm_map.cap = 0;
}

/* 把 group g 的字段名视图绑定到 ctx->field_names / 归一化索引 / float 标志。
 * field_names 只是借用 group_field_names[g]（不深拷贝），因此 qzdb_free 只
 * 释放 group_field_names。切换 group_index 时重新调用即可保持一致。 */
static int apply_group_meta(qzdb_reader_t* ctx, int g) {
    if (!ctx || g < 0 || g >= ctx->actual_groups || !ctx->group_field_names) {
        return QZDB_ERR_INVALID_PARAM;
    }
    int nf = ctx->group_field_counts[g];

    if (ctx->norm_field_names) {
        for (int i = 0; i < ctx->field_count; i++) free(ctx->norm_field_names[i]);
        free(ctx->norm_field_names);
        ctx->norm_field_names = NULL;
    }
    norm_map_free(ctx);
    free(ctx->float_field_flags); ctx->float_field_flags = NULL;
    free(ctx->edition);           ctx->edition = NULL;

    ctx->field_names        = ctx->group_field_names[g];
    ctx->field_count        = nf;
    ctx->edition            = strdup(ctx->group_editions[g] ? ctx->group_editions[g] : "");
    ctx->edition_source     = ctx->group_edition_sources[g];
    ctx->field_names_source = ctx->group_name_sources[g];
    if (!ctx->edition) return QZDB_ERR_OUT_OF_MEMORY;

    ctx->float_field_flags = calloc((size_t)(nf > 0 ? nf : 1), sizeof(int));
    ctx->norm_field_names  = calloc((size_t)(nf > 0 ? nf : 1), sizeof(char*));
    if (!ctx->float_field_flags || !ctx->norm_field_names) return QZDB_ERR_OUT_OF_MEMORY;

    for (int i = 0; i < nf; i++) {
        const char* fn = ctx->field_names[i] ? ctx->field_names[i] : "";
        if (strcmp(fn, "longitude") == 0 || strcmp(fn, "latitude") == 0)
            ctx->float_field_flags[i] = 1;
        char* n = malloc(strlen(fn) + 1);
        if (!n) return QZDB_ERR_OUT_OF_MEMORY;
        size_t j = 0;
        for (size_t k = 0; fn[k]; k++) {
            char c = fn[k];
            if (c == '_' || c == '-') continue;
            if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
            n[j++] = c;
        }
        n[j] = '\0';
        ctx->norm_field_names[i] = n;
    }
    norm_map_build(ctx);   /* O(1) 归一化索引，加载期一次性构建（spec §6.1） */
    return QZDB_OK;
}

/* O(1) lookup — returns -1 if not found */
static int field_index_normalized(qzdb_reader_t* ctx, const char* name) {
    if (!ctx || !name || !ctx->norm_map.buckets) return -1;
    char norm[64];
    normalize_field_name(name, norm, sizeof(norm));
    uint32_t h = fnv1a(norm);
    if (h == 0) h = 1;
    uint32_t idx = h & ctx->norm_map.mask;
    while (ctx->norm_map.buckets[idx].hash != 0) {
        const qzdb_norm_entry_t* e = &ctx->norm_map.buckets[idx];
        if (e->hash == h && ctx->norm_field_names[e->index] &&
            strcmp(ctx->norm_field_names[e->index], norm) == 0) {
            return e->index;
        }
        idx = (idx + 1) & ctx->norm_map.mask;
    }
    return -1;
}

/* ========================================================================
 * UsageType (spec §6.4) — 21 official scenarios
 * ======================================================================== */
static const struct { const char* raw; const char* zh; const char* en; } usage_types[] = {
    {"AICrawler",  "AI 爬虫",     "AICrawler"},
    {"Backbone",   "骨干网",      "Backbone"},
    {"Broadband",  "宽带",        "Broadband"},
    {"Business",   "企业",        "Business"},
    {"CDN",        "CDN",         "CDN"},
    {"Cloud",      "云服务",      "Cloud"},
    {"DNS",        "DNS",         "DNS"},
    {"DataCenter", "数据中心",    "DataCenter"},
    {"Education",  "教育网",      "Education"},
    {"Finance",    "金融",        "Finance"},
    {"Government", "政府",        "Government"},
    {"ISP",        "互联网提供商", "ISP"},
    {"IXP",        "交换中心",    "IXP"},
    {"IoT",        "物联网",      "IoT"},
    {"Mobile",     "移动网络",    "Mobile"},
    {"Reserved",   "保留地址",    "Reserved"},
    {"Satellite",  "卫星互联网",  "Satellite"},
    {"Spider",     "爬虫",        "Spider"},
    {"Streaming",  "流媒体",      "Streaming"},
    {"Unknown",    "未知",        "Unknown"},
    {"VPN",        "VPN/代理",    "VPN"},
    {NULL, NULL, NULL}
};

const char* qzdb_geo_usage_type(qzdb_reader_t* ctx, const qzdb_geo_info_t* info) {
    if (!ctx || !info) return "";
    /* Resolve by field name, NOT by positional values[0]: the geo_info
     * values[] array is indexed by schema field order, so values[0] is
     * merely the first schema field (e.g. continent/country), not usage_type. */
    int idx = field_index_normalized(ctx, "usage_type");
    if (idx < 0 || idx >= QZDB_MAX_FIELDS) return "";
    const char* v = info->values[idx];
    return v ? v : "";
}

int qzdb_usage_type_is_known(const char* raw) {
    if (!raw) return 0;
    for (int i = 0; usage_types[i].raw; i++)
        if (strcmp(raw, usage_types[i].raw) == 0) return 1;
    return 0;
}

const char* qzdb_usage_type_display_zh(const char* raw) {
    if (!raw) return "";
    for (int i = 0; usage_types[i].raw; i++)
        if (strcmp(raw, usage_types[i].raw) == 0) return usage_types[i].zh;
    return "未知";
}

const char* qzdb_usage_type_display_en(const char* raw) {
    if (!raw) return "";
    for (int i = 0; usage_types[i].raw; i++)
        if (strcmp(raw, usage_types[i].raw) == 0) return usage_types[i].en;
    return "Unknown";
}

/* ========================================================================
 * Float formatting — canonical cross-language (spec §8.2)
 * ======================================================================== */
static void format_float_value(double dv, char* buf, size_t buf_size) {
    if (isnan(dv) || isinf(dv)) { buf[0] = '\0'; return; }
    if (dv == floor(dv) && dv >= -9007199254740992.0 && dv <= 9007199254740992.0)
        snprintf(buf, buf_size, "%ld", (long)dv);
    else
        snprintf(buf, buf_size, "%.6f", dv);
}

static void format_float32_value(float fv, char* buf, size_t buf_size) {
    format_float_value((double)fv, buf, buf_size);
}

/* ========================================================================
 * Forward declarations
 * ======================================================================== */
static void ensure_pools_loaded(qzdb_reader_t* ctx);
static int  read_ip_row(qzdb_reader_t* ctx, uint32_t row_id, uint32_t* geo_id,
                        uint32_t* asn_id, uint32_t* usage_id);
static int  get_geo_info(qzdb_reader_t* ctx, uint32_t entry_id, int group_index,
                         qzdb_geo_info_t* result);
static int  get_geo_info_buf(qzdb_reader_t* ctx, uint32_t entry_id, int group_index,
                             char** values, char (*bufs)[64], int buf_size, int* out_count);
static void geo_cache_init(qzdb_reader_t* ctx);
static void geo_cache_free(qzdb_reader_t* ctx);
static char** geo_cache_lookup(qzdb_reader_t* ctx, int group, uint32_t entry_id, int* out_count);
static int  resolve_row_id_cached(qzdb_reader_t* ctx, uint32_t row_id, int group_index,
                                  qzdb_geo_info_t* result);
static void free_geo_info(qzdb_geo_info_t* info);

/* ========================================================================
 * 版本档次判定契约（FORMAT §10.3 —— 8 种 SDK 逐字一致）
 *
 * 档次的权威来源是 Header.VersionMask（offset 6，u16 LE）与
 * GROUP_SCHEMA.groupId，二者都是 one-hot 位掩码：
 *   bit0=std(1) bit1=asn(2) bit2=pro(4) bit3=max(8) bit4=ult(16)
 * 字段个数只是最后兜底。
 * ======================================================================== */
static const char* const EDITION_BY_BIT[5] = { "std", "asn", "pro", "max", "ult" };

#define QZDB_EDITION_SOURCE_VERSION_MASK  "version_mask"
#define QZDB_EDITION_SOURCE_METADATA      "metadata"
#define QZDB_EDITION_SOURCE_INFERRED      "inferred"
#define QZDB_EDITION_SOURCE_UNKNOWN       "unknown"
#define QZDB_FIELD_NAMES_SOURCE_METADATA  "metadata"
#define QZDB_FIELD_NAMES_SOURCE_EDITION   "edition"
#define QZDB_FIELD_NAMES_SOURCE_SYNTHETIC "synthetic"

static const char* const EDITION_NAMES_STD[6] = {
    "continent", "country_code", "country", "province", "city", "isp"
};
static const char* const EDITION_NAMES_ASN[8] = {
    "continent", "country_code", "country", "isp", "asn", "as_name", "as_domain",
    "usage_type"
};
static const char* const EDITION_NAMES_PRO[11] = {
    "continent", "country_code", "country", "province", "city", "district", "geo_id",
    "longitude", "latitude", "timezone", "isp"
};
static const char* const EDITION_NAMES_MAX[15] = {
    "continent", "country_code", "country", "province", "city", "district", "geo_id",
    "longitude", "latitude", "timezone", "isp", "asn", "as_name", "as_domain",
    "usage_type"
};
static const char* const EDITION_NAMES_ULT[25] = {
    "continent", "continent_en", "country_code", "country_alpha3", "country",
    "country_en", "province", "province_en", "city", "city_en", "district",
    "district_en", "geo_id", "longitude", "latitude", "timezone", "languages",
    "currency_code", "phone_prefix", "emoji_flag", "isp", "asn", "as_name",
    "as_domain", "usage_type"
};

/* 各档次的规范字段表（仅在文件未自带 Metadata field_names 时使用）。 */
static const char* const* edition_field_names(const char* edition, int* out_count) {
    if (!edition || !edition[0]) { *out_count = 0; return NULL; }
    if (strcmp(edition, "std") == 0) { *out_count = 6;  return EDITION_NAMES_STD; }
    if (strcmp(edition, "asn") == 0) { *out_count = 8;  return EDITION_NAMES_ASN; }
    if (strcmp(edition, "pro") == 0) { *out_count = 11; return EDITION_NAMES_PRO; }
    if (strcmp(edition, "max") == 0) { *out_count = 15; return EDITION_NAMES_MAX; }
    if (strcmp(edition, "ult") == 0) { *out_count = 25; return EDITION_NAMES_ULT; }
    *out_count = 0; return NULL;
}

const char* qzdb_edition_from_mask(uint16_t mask) {
    if (mask == 0 || (mask & (uint16_t)(mask - 1)) != 0) return "";
    int bit = 0;
    while (bit < 16 && ((mask >> bit) & 1u) == 0) bit++;
    return bit < 5 ? EDITION_BY_BIT[bit] : "";
}

/* 字段数 → 档次名（仅当该基数在规范表中唯一时才成立）。 */
static const char* edition_by_field_count(int count) {
    const char* hit = "";
    for (int i = 0; i < 5; i++) {
        int n = 0;
        if (edition_field_names(EDITION_BY_BIT[i], &n) && n == count) {
            if (hit[0]) return "";  /* 基数不唯一，不猜 */
            hit = EDITION_BY_BIT[i];
        }
    }
    return hit;
}

/* 把 "a, b" 形式的 version_list 解析成单一档次名；为空或多于一项时返回 0。 */
static int single_version_token(const char* list, char* out, size_t out_size) {
    if (!list || out_size == 0) return 0;
    int found = 0;
    const char* p = list;
    for (;;) {
        const char* q = p;
        while (*q && *q != ',') q++;
        const char* a = p;
        const char* b = q;
        while (a < b && (*a == ' ' || *a == '\t')) a++;
        while (b > a && (b[-1] == ' ' || b[-1] == '\t')) b--;
        if (b > a) {
            if (found) return 0;   /* 多于一项：无法确定唯一档次 */
            size_t n = (size_t)(b - a);
            if (n > out_size - 1) n = out_size - 1;
            memcpy(out, a, n); out[n] = '\0';
            found = 1;
        }
        if (*q != ',') break;
        p = q + 1;
    }
    return found;
}

/* ========================================================================
 * Trie walking
 * ======================================================================== */
static uint32_t get_v4_child(const qzdb_reader_t* ctx, uint32_t node_idx, uint32_t bit) {
    if (node_idx >= ctx->v4_node_count) return 0;
    if (ctx->v4_node_24) {
        uint64_t node_offset = ctx->off_v4_nodes + (uint64_t)node_idx * 6;
        uint64_t offset = bit == 0 ? node_offset : node_offset + 3;
        uint32_t val;
        if (safe_read_u24(ctx->data, ctx->data_size, offset, &val) != QZDB_OK) return 0;
        if (val & 0x800000u) return (val & 0x7FFFFFu) | QZDB_SENTINEL;
        return val;
    } else {
        uint64_t child_off = ctx->off_v4_nodes + (uint64_t)node_idx * 8 + (uint64_t)bit * 4;
        uint32_t val;
        if (safe_read_u32(ctx->data, ctx->data_size, child_off, &val) != QZDB_OK) return 0;
        return val;
    }
}

static uint32_t get_v6_child(const qzdb_reader_t* ctx, uint32_t node_idx, uint32_t bit) {
    if (node_idx >= ctx->v6_node_count) return 0;
    if (ctx->v6_node_24) {
        uint64_t node_offset = ctx->off_v6_nodes + (uint64_t)node_idx * 6;
        uint64_t offset = bit == 0 ? node_offset : node_offset + 3;
        uint32_t val;
        if (safe_read_u24(ctx->data, ctx->data_size, offset, &val) != QZDB_OK) return 0;
        if (val & 0x800000u) return (val & 0x7FFFFFu) | QZDB_SENTINEL;
        return val;
    } else {
        uint64_t child_off = ctx->off_v6_nodes + (uint64_t)node_idx * 8 + (uint64_t)bit * 4;
        uint32_t val;
        if (safe_read_u32(ctx->data, ctx->data_size, child_off, &val) != QZDB_OK) return 0;
        return val;
    }
}

static uint32_t trie_walk_v4(const qzdb_reader_t* ctx, uint32_t ip_int) {
    uint32_t hi16 = (ip_int >> 16) & 0xFFFF;
    uint32_t ptr;
    if (safe_read_u32(ctx->data, ctx->data_size, ctx->off_v4_jump + hi16 * 4, &ptr) != QZDB_OK) return 0;
    if (ptr == 0) return 0;
    if (ptr & QZDB_SENTINEL) return ptr & QZDB_SENTINEL_MASK_31;
    uint32_t idx = ptr;
    uint32_t suffix = (ip_int & 0xFFFF) << 16;
    uint32_t steps = 0;
    while (1) {
        if (++steps >= QZDB_MAX_TRIE_WALK_STEPS) return 0;
        uint32_t bit = (suffix >> 31) & 1;
        uint32_t child = get_v4_child(ctx, idx, bit);
        if (child == 0) return 0;
        if (child & QZDB_SENTINEL) return child & QZDB_SENTINEL_MASK_31;
        idx = child;
        suffix <<= 1;
    }
}

static uint32_t trie_walk_v6(const qzdb_reader_t* ctx, const uint8_t* ip_bin) {
    int v6_jump_bits = ctx->v6_jump_bits;
    uint32_t idx_jump = 0;
    int bits_collected = 0;
    for (int i = 0; i < 16; i++) {
        uint8_t b = ip_bin[i];
        int bits_left = v6_jump_bits - bits_collected;
        if (bits_left <= 0) break;
        if (bits_left >= 8) { idx_jump = (idx_jump << 8) | b; bits_collected += 8; }
        else { idx_jump = (idx_jump << bits_left) | (b >> (8 - bits_left)); bits_collected += bits_left; break; }
    }
    uint32_t ptr;
    if (safe_read_u32(ctx->data, ctx->data_size, ctx->off_v6_jump + idx_jump * 4, &ptr) != QZDB_OK) return 0;
    if (ptr == 0) return 0;
    if (ptr & QZDB_SENTINEL) return ptr & QZDB_SENTINEL_MASK_31;
    uint32_t idx = ptr;
    int depth = v6_jump_bits;
    int steps = 0;
    while (depth < 128) {
        if (++steps >= QZDB_MAX_TRIE_WALK_STEPS) return 0;
        if (idx >= ctx->v6_node_count) return 0;
        int byte_idx = depth / 8;
        int bit_idx = 7 - (depth % 8);
        uint32_t bit = (ip_bin[byte_idx] >> bit_idx) & 1;
        uint32_t child = get_v6_child(ctx, idx, bit);
        if (child == 0) return 0;
        if (child & QZDB_SENTINEL) return child & QZDB_SENTINEL_MASK_31;
        idx = child;
        depth++;
    }
    return 0;
}

/* ========================================================================
 * GeoInfo decode cache (per-snapshot bounded)
 * ======================================================================== */
static void geo_cache_init(qzdb_reader_t* ctx) {
    ctx->geo_cache_cap = 16384;
    ctx->geo_cache = calloc(ctx->geo_cache_cap, sizeof(qzdb_cache_slot_t));
    pthread_mutex_init(&ctx->geo_cache_lock, NULL);
}

static void geo_cache_free(qzdb_reader_t* ctx) {
    if (ctx->geo_cache) {
        for (uint32_t i = 0; i < ctx->geo_cache_cap; i++) {
            qzdb_cache_slot_t* s = &ctx->geo_cache[i];
            if (s->key != 0 && s->values) { for (int k = 0; k < s->count; k++) free(s->values[k]); free(s->values); }
        }
        free(ctx->geo_cache);
        ctx->geo_cache = NULL;
    }
    pthread_mutex_destroy(&ctx->geo_cache_lock);
}

static char** geo_cache_store(qzdb_reader_t* ctx, qzdb_cache_slot_t* slot,
                              int group, uint32_t entry_id, int* out_count) {
    char bufs[QZDB_MAX_FIELDS][64];
    char* vals[QZDB_MAX_FIELDS];
    int cnt = 0;
    if (get_geo_info_buf(ctx, entry_id, group, vals, bufs, 64, &cnt) != QZDB_OK) { *out_count = 0; return NULL; }
    char** pv = malloc((size_t)cnt * sizeof(char*));
    if (!pv) { *out_count = 0; return NULL; }
    for (int i = 0; i < cnt; i++) pv[i] = strdup(vals[i] ? vals[i] : "");
    slot->key = ((uint64_t)group << 40) | (uint64_t)entry_id;
    slot->values = pv;
    slot->count = cnt;
    *out_count = cnt;
    return pv;
}

static char** geo_cache_lookup(qzdb_reader_t* ctx, int group, uint32_t entry_id, int* out_count) {
    *out_count = 0;
    if (!ctx->geo_cache) {
        /* Cache disabled — decode into heap (caller frees) */
        char bufs[QZDB_MAX_FIELDS][64];
        char* vals[QZDB_MAX_FIELDS];
        int cnt = 0;
        if (get_geo_info_buf(ctx, entry_id, group, vals, bufs, 64, &cnt) == QZDB_OK) {
            char** pv = malloc((size_t)cnt * sizeof(char*));
            if (pv) { for (int i = 0; i < cnt; i++) pv[i] = strdup(vals[i] ? vals[i] : ""); *out_count = cnt; return pv; }
        }
        return NULL;
    }
    uint64_t key = ((uint64_t)group << 40) | (uint64_t)entry_id;
    uint32_t mask = ctx->geo_cache_cap - 1;
    uint32_t h = (uint32_t)key * 2654435761u;
    pthread_mutex_lock(&ctx->geo_cache_lock);
    for (uint32_t i = 0; i < ctx->geo_cache_cap; i++) {
        uint32_t idx = (h + i) & mask;
        qzdb_cache_slot_t* s = &ctx->geo_cache[idx];
        if (s->key == 0) {
            char** pv = geo_cache_store(ctx, s, group, entry_id, out_count);
            pthread_mutex_unlock(&ctx->geo_cache_lock);
            return pv;
        }
        if (s->key == key) { *out_count = s->count; pthread_mutex_unlock(&ctx->geo_cache_lock); return s->values; }
    }
    /* Table full: overwrite home slot (old slot may be shared across threads, but we just replace it) */
    qzdb_cache_slot_t* s = &ctx->geo_cache[h & mask];
    if (s->values) {
        for (int k = 0; k < s->count; k++) free(s->values[k]);
        free(s->values);
        s->values = NULL;
        s->count = 0;
        s->key = 0;
    }
    char** pv = geo_cache_store(ctx, s, group, entry_id, out_count);
    pthread_mutex_unlock(&ctx->geo_cache_lock);
    return pv;
}

/* ========================================================================
 * GeoInfo decode
 * ======================================================================== */
static int get_geo_info(qzdb_reader_t* ctx, uint32_t entry_id, int group_index, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    if (group_index < 0 || group_index >= ctx->actual_groups) return QZDB_ERR_INVALID_PARAM;
    if (entry_id >= ctx->group_entry_counts[group_index]) return QZDB_ERR_INVALID_PARAM;
    int field_count = ctx->group_field_counts[group_index];
    if (field_count <= 0) return QZDB_ERR_CORRUPTED;
    uint64_t group_entry_start = ctx->off_geo_entries + ctx->group_entry_offsets[group_index];
    int stride = ctx->group_strides[group_index];
    uint64_t entry_offset = group_entry_start + (uint64_t)entry_id * stride;
    if (entry_offset + stride > ctx->data_size) return QZDB_ERR_BOUNDS;

    int* widths = ctx->group_field_widths[group_index];
    int* base_offsets = ctx->group_field_offsets[group_index];
    int* natives = ctx->group_field_native[group_index];
    int* nat_types = ctx->group_field_native_type[group_index];

    memset(result, 0, sizeof(*result));
    for (int i = 0; i < field_count && i < QZDB_MAX_FIELDS; i++) {
        int w = widths[i];
        uint64_t fo = entry_offset + base_offsets[i];
        int is_native = natives[i];
        if (is_native) {
            int t = nat_types[i];
            char buf[64];
            if (t == 1) {
                if (w == 4) {
                    uint32_t bits; if (safe_read_u32(ctx->data, ctx->data_size, fo, &bits) != QZDB_OK) return QZDB_ERR_BOUNDS;
                    union { uint32_t u; float f; } u; u.u = bits;
                    format_float32_value(u.f, buf, sizeof(buf));
                } else {
                    uint64_t bits; if (safe_read_u64(ctx->data, ctx->data_size, fo, &bits) != QZDB_OK) return QZDB_ERR_BOUNDS;
                    union { uint64_t u; double d; } u; u.u = bits;
                    format_float_value(u.d, buf, sizeof(buf));
                }
            } else {
                uint32_t val; if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &val) != QZDB_OK) return QZDB_ERR_BOUNDS;
                snprintf(buf, sizeof(buf), "%lu", (unsigned long)val);
            }
            result->values[i] = strdup(buf);
            result->values_mask |= (1u << i);
        } else {
            uint32_t idx; if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &idx) != QZDB_OK) return QZDB_ERR_BOUNDS;
            if (ctx->group_pools[group_index] && ctx->group_pools[group_index][i] && (int)idx < ctx->group_pool_counts[group_index][i])
                result->values[i] = ctx->group_pools[group_index][i][idx];
            else
                result->values[i] = "";
        }
    }
    return QZDB_OK;
}

static int get_geo_info_buf(qzdb_reader_t* ctx, uint32_t entry_id, int group_index,
                             char** values, char (*bufs)[64], int buf_size, int* out_count) {
    if (!ctx || !values || !bufs || !out_count) return QZDB_ERR_INVALID_PARAM;
    if (group_index < 0 || group_index >= ctx->actual_groups) return QZDB_ERR_INVALID_PARAM;
    if (entry_id >= ctx->group_entry_counts[group_index]) return QZDB_ERR_INVALID_PARAM;
    int field_count = ctx->group_field_counts[group_index];
    if (field_count <= 0) return QZDB_ERR_CORRUPTED;
    uint64_t group_entry_start = ctx->off_geo_entries + ctx->group_entry_offsets[group_index];
    int stride = ctx->group_strides[group_index];
    uint64_t entry_offset = group_entry_start + (uint64_t)entry_id * stride;
    if (entry_offset + stride > ctx->data_size) return QZDB_ERR_BOUNDS;

    int* widths = ctx->group_field_widths[group_index];
    int* base_offsets = ctx->group_field_offsets[group_index];
    int* natives = ctx->group_field_native[group_index];
    int* nat_types = ctx->group_field_native_type[group_index];

    for (int i = 0; i < field_count && i < QZDB_MAX_FIELDS; i++) {
        int w = widths[i];
        uint64_t fo = entry_offset + base_offsets[i];
        int is_native = natives[i];
        if (is_native) {
            int t = nat_types[i];
            if (t == 1) {
                if (w == 4) {
                    uint32_t bits; if (safe_read_u32(ctx->data, ctx->data_size, fo, &bits) != QZDB_OK) { values[i] = ""; continue; }
                    union { uint32_t u; float f; } u; u.u = bits;
                    format_float32_value(u.f, bufs[i], buf_size);
                } else {
                    uint64_t bits; if (safe_read_u64(ctx->data, ctx->data_size, fo, &bits) != QZDB_OK) { values[i] = ""; continue; }
                    union { uint64_t u; double d; } u; u.u = bits;
                    format_float_value(u.d, bufs[i], buf_size);
                }
            } else {
                uint32_t val; if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &val) != QZDB_OK) { values[i] = ""; continue; }
                snprintf(bufs[i], buf_size, "%lu", (unsigned long)val);
            }
            values[i] = bufs[i];
        } else {
            uint32_t idx; if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &idx) != QZDB_OK) { values[i] = ""; continue; }
            if (ctx->group_pools[group_index] && ctx->group_pools[group_index][i] && (int)idx < ctx->group_pool_counts[group_index][i])
                values[i] = ctx->group_pools[group_index][i][idx];
            else
                values[i] = "";
        }
    }
    *out_count = field_count;
    return QZDB_OK;
}

static void free_geo_info(qzdb_geo_info_t* info) {
    for (int i = 0; i < QZDB_MAX_FIELDS; i++) {
        if (info->values_mask & (1u << i)) { free(info->values[i]); info->values[i] = NULL; info->values_mask &= ~(1u << i); }
    }
}

/* ========================================================================
 * IPRow reading (dynamic widths)
 * ======================================================================== */
static int read_ip_row(qzdb_reader_t* ctx, uint32_t row_id, uint32_t* geo_id, uint32_t* asn_id, uint32_t* usage_id) {
    if (!ctx || row_id == 0 || row_id >= (uint32_t)ctx->row_count) return QZDB_ERR_INVALID_PARAM;
    uint64_t off = ctx->off_ip_row + (uint64_t)row_id * ctx->ip_row_size;
    *geo_id = 0; *asn_id = 0; *usage_id = 0;
    if (ctx->off_row_schema > 0) {
        uint64_t p = off;
        if (safe_read_uint_width(ctx->data, ctx->data_size, p, ctx->row_geo_width, geo_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
        p += ctx->row_geo_width;
        if (ctx->row_asn_width > 0) {
            if (safe_read_uint_width(ctx->data, ctx->data_size, p, ctx->row_asn_width, asn_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
            p += ctx->row_asn_width;
        }
        if (ctx->row_usage_width > 0) {
            if (safe_read_uint_width(ctx->data, ctx->data_size, p, ctx->row_usage_width, usage_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
        }
    } else {
        if (safe_read_u24(ctx->data, ctx->data_size, off, geo_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
        if (safe_read_u24(ctx->data, ctx->data_size, off + 3, asn_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
        if (ctx->ip_row_size >= 9)
            if (safe_read_u24(ctx->data, ctx->data_size, off + 6, usage_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    }
    return QZDB_OK;
}

/* ========================================================================
 * Resolve row_id → GeoInfo (cache-backed)
 * ======================================================================== */
static int resolve_row_id_cached(qzdb_reader_t* ctx, uint32_t row_id, int group_index, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    uint32_t geo_id, asn_id, usage_id;
    int err = read_ip_row(ctx, row_id, &geo_id, &asn_id, &usage_id);
    if (err != QZDB_OK) return err;
    uint16_t mask = group_index < ctx->actual_groups ? ctx->group_dim_masks[group_index] : 0;
    uint32_t entry_id = geo_id;
    if (mask & 0x02) entry_id = asn_id;
    else if (mask & 0x04) entry_id = usage_id;
    if (entry_id == 0) return QZDB_ERR_NOT_FOUND;
    int cnt = 0;
    char** cached = geo_cache_lookup(ctx, group_index, entry_id, &cnt);
    if (cached) {
        memset(result, 0, sizeof(*result));
        for (int i = 0; i < cnt && i < QZDB_MAX_FIELDS; i++) result->values[i] = cached[i];
        return QZDB_OK;
    }
    return get_geo_info(ctx, entry_id, group_index, result);
}

/* ========================================================================
 * Resolve row_id → caller-buffer (used by find_*_buf variants)
 * ======================================================================== */
static int resolve_row_id_buf(qzdb_reader_t* ctx, uint32_t row_id, int group_index,
                              char** values, char (*bufs)[64], int buf_size, int* out_count) {
    if (!ctx || !values || !bufs || !out_count) return QZDB_ERR_INVALID_PARAM;
    uint32_t geo_id, asn_id, usage_id;
    int err = read_ip_row(ctx, row_id, &geo_id, &asn_id, &usage_id);
    if (err != QZDB_OK) return err;
    uint16_t mask = group_index < ctx->actual_groups ? ctx->group_dim_masks[group_index] : 0;
    uint32_t entry_id = geo_id;
    if (mask & 0x02) entry_id = asn_id;
    else if (mask & 0x04) entry_id = usage_id;
    if (entry_id == 0) return QZDB_ERR_NOT_FOUND;  /* BUG-2 fix */
    return get_geo_info_buf(ctx, entry_id, group_index, values, bufs, buf_size, out_count);
}

/* ========================================================================
 * Batch & streaming (spec §8)
 * ======================================================================== */
int qzdb_find_batch(qzdb_reader_t* ctx, const char** ips, int count, qzdb_batch_result_t* results) {
    if (!ctx || !ips || !results || count <= 0) return QZDB_ERR_INVALID_PARAM;
    for (int i = 0; i < count; i++) {
        results[i].info.values_mask = 0;
        results[i].error_code = qzdb_find(ctx, ips[i], &results[i].info);
    }
    return QZDB_OK;
}

int qzdb_find_each(qzdb_reader_t* ctx, const char** ips, int count,
                    qzdb_find_callback cb, void* user_data) {
    if (!ctx || !ips || !cb || count <= 0) return QZDB_ERR_INVALID_PARAM;
    for (int i = 0; i < count; i++) {
        qzdb_batch_result_t res;
        res.info.values_mask = 0;
        res.error_code = qzdb_find(ctx, ips[i], &res.info);
        cb(i, &res, user_data);
    }
    return QZDB_OK;
}

/* ========================================================================
 * GeoInfo access
 * ======================================================================== */
const char* qzdb_geo_info_get(qzdb_reader_t* ctx, const qzdb_geo_info_t* info, const char* name) {
    if (!ctx || !info || !name) return "";
    int idx = field_index_normalized(ctx, name);
    if (idx < 0 || idx >= QZDB_MAX_FIELDS) return "";
    return info->values[idx] ? info->values[idx] : "";
}

int qzdb_geo_info_to_pipe(qzdb_reader_t* ctx, const qzdb_geo_info_t* info, char* out, size_t out_size) {
    if (!ctx || !info || !out || out_size == 0) return QZDB_ERR_INVALID_PARAM;
    size_t pos = 0;
    int fc = ctx->group_field_counts[ctx->group_index];
    for (int i = 0; i < fc && i < QZDB_MAX_FIELDS; i++) {
        if (i > 0 && pos < out_size - 1) out[pos++] = '|';
        const char* v = info->values[i] ? info->values[i] : "";
        size_t len = strlen(v);
        if (pos + len >= out_size) { if (out_size > pos) { memcpy(out + pos, v, out_size - pos - 1); pos = out_size - 1; } break; }
        memcpy(out + pos, v, len); pos += len;
    }
    out[pos] = '\0';
    return QZDB_OK;
}

const char* qzdb_geo_info_get_cidr(void) { return ""; }
void qzdb_free_geo_info(qzdb_geo_info_t* info) { free_geo_info(info); }

/* ========================================================================
 * Metadata accessors
 * ======================================================================== */
const char* qzdb_get_version(qzdb_reader_t* ctx) { return ctx && ctx->version_name ? ctx->version_name : ""; }
const char* qzdb_get_data_month(qzdb_reader_t* ctx) { return ctx && ctx->data_month ? ctx->data_month : ""; }
const char* qzdb_get_edition(qzdb_reader_t* ctx) { return ctx && ctx->edition ? ctx->edition : ""; }
uint16_t    qzdb_get_version_mask(qzdb_reader_t* ctx) { return ctx ? ctx->version_mask : 0; }
const char* qzdb_get_edition_source(qzdb_reader_t* ctx) {
    return ctx && ctx->edition_source ? ctx->edition_source : QZDB_EDITION_SOURCE_UNKNOWN;
}
const char* qzdb_get_field_names_source(qzdb_reader_t* ctx) {
    return ctx && ctx->field_names_source ? ctx->field_names_source : QZDB_FIELD_NAMES_SOURCE_SYNTHETIC;
}
const char* qzdb_get_scope(qzdb_reader_t* ctx) { (void)ctx; return ""; }
const char* qzdb_get_build_time(qzdb_reader_t* ctx) { return ctx && ctx->build_time_str ? ctx->build_time_str : ""; }
const char* qzdb_get_description(qzdb_reader_t* ctx) { return ctx && ctx->description ? ctx->description : ""; }

int qzdb_get_file_hash(qzdb_reader_t* ctx, char* out, size_t out_size) {
    if (!ctx || !out || out_size < 9) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->data || ctx->data_size < 20) return QZDB_ERR_CORRUPTED;
    if (!ctx->crc_valid) { ctx->file_crc = crc32_compute_file(ctx->data, ctx->data_size); ctx->crc_valid = 1; }
    snprintf(out, out_size, "%08x", ctx->file_crc);
    return QZDB_OK;
}

const char** qzdb_get_field_names(qzdb_reader_t* ctx) { return ctx ? (const char**)ctx->field_names : NULL; }
int qzdb_get_field_count(qzdb_reader_t* ctx) { return ctx ? ctx->field_count : 0; }
int qzdb_has_field(qzdb_reader_t* ctx, const char* name) { return field_index_normalized(ctx, name) >= 0 ? 1 : 0; }
int qzdb_get_group_count(qzdb_reader_t* ctx) { return ctx ? ctx->actual_groups : 0; }
int qzdb_get_pool_count(qzdb_reader_t* ctx) { return ctx ? ctx->pool_count : 0; }

/* ========================================================================
 * IPv4-mapped IPv6 detection (spec §5.3)
 * ======================================================================== */
static int is_v4_mapped(const uint8_t* b) {
    if (!b) return 0;
    for (int i = 0; i < 10; i++) if (b[i] != 0) return 0;
    return b[10] == 0xFF && b[11] == 0xFF;
}

static uint32_t v4_from_mapped(const uint8_t* b) {
    return ((uint32_t)b[12] << 24) | ((uint32_t)b[13] << 16) | ((uint32_t)b[14] << 8) | (uint32_t)b[15];
}

/* ========================================================================
 * IP parsing
 * ======================================================================== */
static const uint8_t hex_lut[128] = {
    ['0']=0,1,2,3,4,5,6,7,8,9,
    ['a']=10,11,12,13,14,15,
    ['A']=10,11,12,13,14,15
};

static int fast_parse_ipv4(const char* s, uint32_t* out) {
    int n = 0; while (s[n]) n++;
    if (n == 0 || s[n-1] == '.') return 0;
    uint32_t result = 0, val = 0; int dots = 0, start = 0;
    for (int i = 0; i <= n; i++) {
        char c = i < n ? s[i] : '.';
        if (c == '.') {
            int seg_len = i - start;
            if (seg_len == 0 || seg_len > 3) return 0;
            if (seg_len > 1 && s[start] == '0') return 0;
            val = 0;
            for (int j = start; j < i; j++) { char d = s[j]; if (d < '0' || d > '9') return 0; val = val * 10 + (uint32_t)(d - '0'); }
            if (val > 255) return 0;
            result = (result << 8) | val; dots++; start = i + 1;
        }
    }
    if (dots != 4) return 0;
    *out = result; return 1;
}

static int split_hextets(const char* src, int src_len, char parts[][16], int max_parts) {
    if (src_len < 0) return -1;
    if (src_len == 0) return 0;
    int count = 0; int i = 0;
    while (i <= src_len) {
        int start = i;
        while (i < src_len && src[i] != ':') i++;
        int seglen = i - start;
        if (seglen == 0) return -1;
        if (count >= max_parts) return -1;
        if (seglen > 15) return -1;
        memcpy(parts[count], src + start, (size_t)seglen); parts[count][seglen] = '\0'; count++;
        if (i >= src_len) break; i++;
    }
    return count;
}

typedef struct { uint8_t v6[16]; uint32_t v4; int is_v4; } parse_result_t;

static int fast_parse_ip(const char* s, parse_result_t* res) {
    if (!s) return 0;
    int n = 0;
    while (s[n]) { unsigned char c = (unsigned char)s[n];
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f') return 0;
        n++; }
    if (n == 0 || n > 45) return 0;
    int has_colon = 0;
    for (int i = 0; i < n; i++) { if (s[i] == ':') { has_colon = 1; break; } }
    if (!has_colon) { uint32_t v4; if (!fast_parse_ipv4(s, &v4)) return 0; res->v4 = v4; res->is_v4 = 1; return 1; }
    for (int i = 0; i < n; i++) { if (s[i] == '%') return 0; }
    const char* dc_ptr = NULL;
    for (int i = 0; i < n - 1; i++) { if (s[i] == ':' && s[i+1] == ':') { if (dc_ptr) return 0; dc_ptr = s + i; } }
    const char* rgt = dc_ptr ? dc_ptr + 2 : s + n;
    int lft_len = (int)(dc_ptr ? dc_ptr - s : n);
    int rgt_len = (int)(dc_ptr ? (s + n) - (dc_ptr + 2) : 0);
    if (lft_len >= 64 || rgt_len >= 64) return 0;
    char lg_parts[8][16], rg_parts[8][16];
    int lg_count = 0, rg_count = 0;
    if (lft_len > 0) { lg_count = split_hextets(s, lft_len, lg_parts, 8); if (lg_count < 0) return 0; }
    if (rgt_len > 0) { rg_count = split_hextets(rgt, rgt_len, rg_parts, 8); if (rg_count < 0) return 0; }
    char allg[16][16]; int ng = 0;
    for (int i = 0; i < lg_count; i++) { strncpy(allg[ng], lg_parts[i], 15); allg[ng][15] = 0; ng++; }
    for (int i = 0; i < rg_count; i++) { strncpy(allg[ng], rg_parts[i], 15); allg[ng][15] = 0; ng++; }
    int has_v4 = 0; uint32_t v4_int = 0;
    if (ng > 0) {
        int last = ng - 1; int has_dot = 0;
        for (int j = 0; allg[last][j]; j++) { if (allg[last][j] == '.') { has_dot = 1; break; } }
        if (has_dot) { if (!fast_parse_ipv4(allg[last], &v4_int)) return 0; has_v4 = 1; ng--; }
    }
    int v4_slots = has_v4 ? 2 : 0;
    if (dc_ptr) { if (ng + v4_slots > 7) return 0; } else { if (ng + v4_slots != 8) return 0; }
    for (int i = 0; i < ng; i++) { int gl = 0; while (allg[i][gl]) gl++;
        if (gl == 0 || gl > 4) return 0;
        for (int j = 0; j < gl; j++) { unsigned char cc = (unsigned char)allg[i][j];
            if (cc >= 128 || (hex_lut[cc] == 0 && cc != '0')) return 0; } }
    int zeros = 8 - ng - v4_slots;
    uint8_t buf[16]; memset(buf, 0, 16); int off = 0;
    for (int i = 0; i < lg_count; i++) { uint16_t v = 0;
        for (int j = 0; lg_parts[i][j]; j++) v = (v << 4) | hex_lut[(unsigned char)lg_parts[i][j]];
        buf[off] = (uint8_t)(v >> 8); buf[off+1] = (uint8_t)v; off += 2; }
    off += zeros * 2;
    for (int i = 0; i < rg_count; i++) { uint16_t v = 0;
        for (int j = 0; rg_parts[i][j]; j++) v = (v << 4) | hex_lut[(unsigned char)rg_parts[i][j]];
        buf[off] = (uint8_t)(v >> 8); buf[off+1] = (uint8_t)v; off += 2; }
    if (has_v4) { buf[12] = (uint8_t)(v4_int >> 24); buf[13] = (uint8_t)(v4_int >> 16); buf[14] = (uint8_t)(v4_int >> 8); buf[15] = (uint8_t)v4_int; }
    if (buf[10] == 0xff && buf[11] == 0xff && buf[0] == 0 && buf[1] == 0 && buf[2] == 0 && buf[3] == 0 &&
        buf[4] == 0 && buf[5] == 0 && buf[6] == 0 && buf[7] == 0 && buf[8] == 0 && buf[9] == 0) {
        res->v4 = ((uint32_t)buf[12] << 24) | ((uint32_t)buf[13] << 16) | ((uint32_t)buf[14] << 8) | buf[15];
        res->is_v4 = 1; return 1; }
    memcpy(res->v6, buf, 16); res->is_v4 = 0; return 1;
}

/* ========================================================================
 * CIDR reverse lookup
 * ======================================================================== */
static int v4_walk_depth(const qzdb_reader_t* ctx, uint32_t ip_int, uint32_t start_idx, int start_depth, int max_depth) {
    if (start_depth >= max_depth) return -1;
    uint32_t idx = start_idx;
    for (int depth = start_depth; depth < max_depth; depth++) {
        if (idx >= ctx->v4_node_count) return -1;
        int bit = (ip_int >> (31 - depth)) & 1;
        uint32_t child = get_v4_child(ctx, idx, bit);
        if (child == 0) return -1;
        if (child & QZDB_SENTINEL) return depth + 1;
        idx = child;
    }
    return -1;
}

static int lookup_v4_prefix_len(const qzdb_reader_t* ctx, uint32_t ip_int) {
    if (!ctx->has_v4 || ctx->off_v4_jump <= 0) return -1;
    uint32_t ptr;
    if (safe_read_u32(ctx->data, ctx->data_size, ctx->off_v4_jump + ((ip_int >> 16) & 0xFFFF) * 4, &ptr) != QZDB_OK) return -1;
    if (ptr == 0) return -1;
    if (ptr & QZDB_SENTINEL) return v4_walk_depth(ctx, ip_int, 0, 0, 16);
    return v4_walk_depth(ctx, ip_int, ptr, 16, 32);
}

static int v6_walk_depth(const qzdb_reader_t* ctx, const uint8_t* ip, uint32_t start_idx, int start_depth, int max_depth) {
    if (start_depth >= max_depth) return -1;
    uint32_t idx = start_idx;
    for (int depth = start_depth; depth < max_depth; depth++) {
        if (idx >= ctx->v6_node_count) return -1;
        int bit = (ip[depth >> 3] >> (7 - (depth & 7))) & 1;
        uint32_t child = get_v6_child(ctx, idx, bit);
        if (child == 0) return -1;
        if (child & QZDB_SENTINEL) return depth + 1;
        idx = child;
    }
    return -1;
}

static int lookup_v6_prefix_len(const qzdb_reader_t* ctx, const uint8_t* ip) {
    if (!ctx->has_v6 || ctx->off_v6_jump <= 0) return -1;
    int jb = ctx->v6_jump_bits; uint32_t pref = 0;
    for (int i = 0; i < jb; i++) { int bit = (ip[i >> 3] >> (7 - (i & 7))) & 1; pref = (pref << 1) | (uint32_t)bit; }
    uint32_t ptr;
    if (safe_read_u32(ctx->data, ctx->data_size, ctx->off_v6_jump + (uint64_t)pref * 4, &ptr) != QZDB_OK) return -1;
    if (ptr == 0) return -1;
    if (ptr & QZDB_SENTINEL) return v6_walk_depth(ctx, ip, 0, 0, jb);
    return v6_walk_depth(ctx, ip, ptr, jb, 128);
}

static void format_v4_cidr(uint32_t ip, int n, char* out, size_t sz) {
    // 注意：C 中 `x << 32` 属未定义行为，故用 `n>=32` 短路避免移位量达到类型宽度。
    uint32_t mask = (n <= 0 || n >= 32) ? 0u : (0xFFFFFFFFu << (32 - n));
    uint32_t net = ip & mask;
    snprintf(out, sz, "%u.%u.%u.%u/%d", (net >> 24) & 0xFF, (net >> 16) & 0xFF, (net >> 8) & 0xFF, net & 0xFF, n);
}

static void format_v6_cidr(const uint8_t* ip, int n, char* out, size_t sz) {
    uint8_t net[16]; memcpy(net, ip, 16);
    for (int bit = n; bit < 128; bit++) net[bit >> 3] &= (uint8_t)~(1 << (7 - (bit & 7)));
    int g[8]; for (int i = 0; i < 8; i++) g[i] = ((net[2*i] & 0xFF) << 8) | (net[2*i+1] & 0xFF);
    int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
    for (int i = 0; i < 8; i++) {
        if (g[i] == 0) { if (curStart < 0) { curStart = i; curLen = 1; } else curLen++; }
        else { if (curLen > bestLen) { bestStart = curStart; bestLen = curLen; } curStart = -1; curLen = 0; }
    }
    if (curLen > bestLen) { bestStart = curStart; bestLen = curLen; }
    char tmp[48]; int p = 0;
    if (bestLen >= 2) {
        for (int i = 0; i < bestStart; i++) { if (i > 0) tmp[p++] = ':'; p += snprintf(tmp + p, sizeof(tmp) - p, "%x", g[i]); }
        tmp[p++] = ':'; tmp[p++] = ':';
        int first = 1;
        for (int i = bestStart + bestLen; i < 8; i++) { if (!first) tmp[p++] = ':'; p += snprintf(tmp + p, sizeof(tmp) - p, "%x", g[i]); first = 0; }
    } else { for (int i = 0; i < 8; i++) { if (i > 0) tmp[p++] = ':'; p += snprintf(tmp + p, sizeof(tmp) - p, "%x", g[i]); } }
    snprintf(out, sz, "%s/%d", tmp, n);
}

char* qzdb_lookup_cidr(qzdb_reader_t* ctx, const char* ip_str, char* out, size_t out_size) {
    if (!ctx || !ip_str || !out || out_size == 0) return NULL;
    parse_result_t res; if (!fast_parse_ip(ip_str, &res)) return NULL;
    if (res.is_v4) return qzdb_lookup_cidr_uint(ctx, res.v4, out, out_size);
    int n = lookup_v6_prefix_len(ctx, res.v6); if (n < 0) return NULL;
    format_v6_cidr(res.v6, n, out, out_size); return out;
}

char* qzdb_lookup_cidr_uint(qzdb_reader_t* ctx, uint32_t ip_int, char* out, size_t out_size) {
    if (!ctx || !out || out_size == 0) return NULL;
    int n = lookup_v4_prefix_len(ctx, ip_int); if (n < 0) return NULL;
    format_v4_cidr(ip_int, n, out, out_size); return out;
}

char* qzdb_lookup_cidr_bytes(qzdb_reader_t* ctx, const uint8_t* ip_bytes, int len, char* out, size_t out_size) {
    if (!ctx || !ip_bytes || !out || out_size == 0) return NULL;
    if (len == 16) {
        if (is_v4_mapped(ip_bytes)) return qzdb_lookup_cidr_uint(ctx, v4_from_mapped(ip_bytes), out, out_size);
        int n = lookup_v6_prefix_len(ctx, ip_bytes); if (n < 0) return NULL;
        format_v6_cidr(ip_bytes, n, out, out_size); return out;
    }
    if (len == 4) { uint32_t v4 = ((uint32_t)ip_bytes[0] << 24) | ((uint32_t)ip_bytes[1] << 16) |
        ((uint32_t)ip_bytes[2] << 8) | (uint32_t)ip_bytes[3]; return qzdb_lookup_cidr_uint(ctx, v4, out, out_size); }
    return NULL;
}

/* ========================================================================
 * ChainedReader (spec §9) — Fallback / Merge / MergeOverride
 * ======================================================================== */
struct qzdb_chain {
    qzdb_reader_t** readers;
    int             count;
    int             mode;       /* QZDB_CHAIN_FALLBACK | MERGE | MERGE_OVERRIDE */
};

qzdb_chain_t* qzdb_chain_new(qzdb_reader_t** ctxs, int count, int mode) {
    if (!ctxs || count <= 0) return NULL;
    if (mode < QZDB_CHAIN_FALLBACK || mode > QZDB_CHAIN_MERGE_OVERRIDE) return NULL;
    qzdb_chain_t* chain = calloc(1, sizeof(qzdb_chain_t));
    if (!chain) return NULL;
    chain->readers = malloc((size_t)count * sizeof(qzdb_reader_t*));
    if (!chain->readers) { free(chain); return NULL; }
    memcpy(chain->readers, ctxs, (size_t)count * sizeof(qzdb_reader_t*));
    chain->count = count;
    chain->mode = mode;
    return chain;
}

/* Merge two GeoInfos: "first-registered wins" (default) or "last wins" (override) */
static void merge_geo_info(qzdb_geo_info_t* dst, const qzdb_geo_info_t* src, int override) {
    for (int i = 0; i < QZDB_MAX_FIELDS; i++) {
        if (override) {
            if (src->values[i] && src->values[i][0]) {
                if (dst->values_mask & (1u << i)) free(dst->values[i]);
                dst->values[i] = strdup(src->values[i]);
                dst->values_mask |= (1u << i);
            }
        } else {
            if (!(dst->values_mask & (1u << i)) && src->values[i] && src->values[i][0]) {
                dst->values[i] = strdup(src->values[i]);
                dst->values_mask |= (1u << i);
            }
        }
    }
}

int qzdb_chain_find(qzdb_chain_t* chain, const char* ip, qzdb_geo_info_t* out) {
    if (!chain || !ip || !out) return QZDB_ERR_INVALID_PARAM;
    memset(out, 0, sizeof(*out));
    if (chain->mode == QZDB_CHAIN_FALLBACK) {
        for (int i = 0; i < chain->count; i++) {
            int rc = qzdb_find(chain->readers[i], ip, out);
            if (rc == QZDB_OK) return QZDB_OK;       /* found */
            if (rc != QZDB_ERR_NOT_FOUND) return rc;  /* format error -> immediate stop (spec §9.1) */
            memset(out, 0, sizeof(*out));             /* not found -> try next */
        }
        return QZDB_ERR_NOT_FOUND;
    }
    /* Merge modes */
    int any_found = 0;
    for (int i = 0; i < chain->count; i++) {
        qzdb_geo_info_t tmp; memset(&tmp, 0, sizeof(tmp));
        int rc = qzdb_find(chain->readers[i], ip, &tmp);
        if (rc == QZDB_OK) { any_found = 1; merge_geo_info(out, &tmp, chain->mode == QZDB_CHAIN_MERGE_OVERRIDE); free_geo_info(&tmp); }
        else if (rc != QZDB_ERR_NOT_FOUND) { free_geo_info(&tmp); return rc; }
        else free_geo_info(&tmp);
    }
    return any_found ? QZDB_OK : QZDB_ERR_NOT_FOUND;
}

int qzdb_chain_find_uint(qzdb_chain_t* chain, uint32_t ip, qzdb_geo_info_t* out) {
    if (!chain || !out) return QZDB_ERR_INVALID_PARAM;
    memset(out, 0, sizeof(*out));
    if (chain->mode == QZDB_CHAIN_FALLBACK) {
        for (int i = 0; i < chain->count; i++) {
            int rc = qzdb_find_uint(chain->readers[i], ip, out);
            if (rc == QZDB_OK) return QZDB_OK;
            if (rc != QZDB_ERR_NOT_FOUND) return rc;
            memset(out, 0, sizeof(*out));
        }
        return QZDB_ERR_NOT_FOUND;
    }
    int any_found = 0;
    for (int i = 0; i < chain->count; i++) {
        qzdb_geo_info_t tmp; memset(&tmp, 0, sizeof(tmp));
        int rc = qzdb_find_uint(chain->readers[i], ip, &tmp);
        if (rc == QZDB_OK) { any_found = 1; merge_geo_info(out, &tmp, chain->mode == QZDB_CHAIN_MERGE_OVERRIDE); free_geo_info(&tmp); }
        else if (rc != QZDB_ERR_NOT_FOUND) { free_geo_info(&tmp); return rc; }
        else free_geo_info(&tmp);
    }
    return any_found ? QZDB_OK : QZDB_ERR_NOT_FOUND;
}

int qzdb_chain_find_bytes(qzdb_chain_t* chain, const uint8_t ip16[16], qzdb_geo_info_t* out) {
    if (!chain || !out) return QZDB_ERR_INVALID_PARAM;
    memset(out, 0, sizeof(*out));
    if (chain->mode == QZDB_CHAIN_FALLBACK) {
        for (int i = 0; i < chain->count; i++) {
            int rc = qzdb_find_bytes(chain->readers[i], ip16, out);
            if (rc == QZDB_OK) return QZDB_OK;
            if (rc != QZDB_ERR_NOT_FOUND) return rc;
            memset(out, 0, sizeof(*out));
        }
        return QZDB_ERR_NOT_FOUND;
    }
    int any_found = 0;
    for (int i = 0; i < chain->count; i++) {
        qzdb_geo_info_t tmp; memset(&tmp, 0, sizeof(tmp));
        int rc = qzdb_find_bytes(chain->readers[i], ip16, &tmp);
        if (rc == QZDB_OK) { any_found = 1; merge_geo_info(out, &tmp, chain->mode == QZDB_CHAIN_MERGE_OVERRIDE); free_geo_info(&tmp); }
        else if (rc != QZDB_ERR_NOT_FOUND) { free_geo_info(&tmp); return rc; }
        else free_geo_info(&tmp);
    }
    return any_found ? QZDB_OK : QZDB_ERR_NOT_FOUND;
}

int qzdb_chain_find_str(qzdb_chain_t* chain, const char* ip, char* buf, size_t size) {
    if (!chain || !ip || !buf || size == 0) return QZDB_ERR_INVALID_PARAM;
    buf[0] = '\0';
    qzdb_geo_info_t info; memset(&info, 0, sizeof(info));
    int rc = qzdb_chain_find(chain, ip, &info);
    if (rc != QZDB_OK) return rc;
    return qzdb_geo_info_to_pipe(chain->readers[0], &info, buf, size);
}

int qzdb_chain_find_batch(qzdb_chain_t* chain, const char** ips, int count, qzdb_batch_result_t* results) {
    if (!chain || !ips || !results || count <= 0) return QZDB_ERR_INVALID_PARAM;
    for (int i = 0; i < count; i++) {
        results[i].info.values_mask = 0;
        results[i].error_code = qzdb_chain_find(chain, ips[i], &results[i].info);
    }
    return QZDB_OK;
}

const char** qzdb_chain_editions(qzdb_chain_t* chain, int* count) {
    static const char* edits[32];
    if (!chain || !count || chain->count > 32) { if (count) *count = 0; return NULL; }
    for (int i = 0; i < chain->count; i++) edits[i] = qzdb_get_edition(chain->readers[i]);
    *count = chain->count; return edits;
}

const char** qzdb_chain_scopes(qzdb_chain_t* chain, int* count) {
    static const char* scps[32];
    if (!chain || !count || chain->count > 32) { if (count) *count = 0; return NULL; }
    for (int i = 0; i < chain->count; i++) scps[i] = qzdb_get_scope(chain->readers[i]);
    *count = chain->count; return scps;
}

const char** qzdb_chain_data_months(qzdb_chain_t* chain, int* count) {
    static const char* months[32];
    if (!chain || !count || chain->count > 32) { if (count) *count = 0; return NULL; }
    for (int i = 0; i < chain->count; i++) months[i] = qzdb_get_data_month(chain->readers[i]);
    *count = chain->count; return months;
}

void qzdb_chain_free(qzdb_chain_t* chain) {
    if (!chain) return;
    free(chain->readers);   /* does not own the readers themselves (spec §9.4) */
    free(chain);
}

/* ========================================================================
 * Registry (spec §3.2) — simple name→reader map
 * ======================================================================== */
#define REG_INIT_BUCKETS 16

typedef struct reg_entry {
    char*            name;
    qzdb_reader_t*   reader;
    struct reg_entry* next;
} reg_entry_t;

struct qzdb_registry {
    reg_entry_t** buckets;
    uint32_t      cap;
    uint32_t      count;
    pthread_mutex_t lock;
};

qzdb_registry_t* qzdb_registry_new(void) {
    qzdb_registry_t* reg = calloc(1, sizeof(qzdb_registry_t));
    if (!reg) return NULL;
    reg->cap = REG_INIT_BUCKETS;
    reg->buckets = calloc(reg->cap, sizeof(reg_entry_t*));
    if (!reg->buckets) { free(reg); return NULL; }
    pthread_mutex_init(&reg->lock, NULL);
    return reg;
}

static uint32_t reg_hash(const char* s) {
    uint32_t h = 2166136261u; for (; *s; s++) { h ^= (uint8_t)*s; h *= 16777619u; }
    return h ? h : 1;
}

int qzdb_registry_register(qzdb_registry_t* reg, const char* name, const char* path) {
    if (!reg || !name || !path) return QZDB_ERR_INVALID_PARAM;
    qzdb_reader_t* reader = calloc(1, sizeof(qzdb_reader_t));
    if (!reader) return QZDB_ERR_OUT_OF_MEMORY;
    int rc = qzdb_init(reader, path);
    if (rc != QZDB_OK) { free(reader); return rc; }
    pthread_mutex_lock(&reg->lock);
    uint32_t idx = reg_hash(name) & (reg->cap - 1);
    reg_entry_t* e = calloc(1, sizeof(reg_entry_t));
    e->name = strdup(name); e->reader = reader; e->next = reg->buckets[idx];
    reg->buckets[idx] = e; reg->count++;
    pthread_mutex_unlock(&reg->lock);
    return QZDB_OK;
}

int qzdb_registry_register_buffer(qzdb_registry_t* reg, const char* name, const uint8_t* buffer, size_t size) {
    if (!reg || !name || !buffer || size == 0) return QZDB_ERR_INVALID_PARAM;
    qzdb_reader_t* reader = calloc(1, sizeof(qzdb_reader_t));
    if (!reader) return QZDB_ERR_OUT_OF_MEMORY;
    int rc = qzdb_init_buffer(reader, buffer, size, 1);
    if (rc != QZDB_OK) { free(reader); return rc; }
    pthread_mutex_lock(&reg->lock);
    uint32_t idx = reg_hash(name) & (reg->cap - 1);
    reg_entry_t* e = calloc(1, sizeof(reg_entry_t));
    e->name = strdup(name); e->reader = reader; e->next = reg->buckets[idx];
    reg->buckets[idx] = e; reg->count++;
    pthread_mutex_unlock(&reg->lock);
    return QZDB_OK;
}

qzdb_reader_t* qzdb_registry_get(qzdb_registry_t* reg, const char* name) {
    if (!reg || !name) return NULL;
    pthread_mutex_lock(&reg->lock);
    uint32_t idx = reg_hash(name) & (reg->cap - 1);
    for (reg_entry_t* e = reg->buckets[idx]; e; e = e->next)
        if (strcmp(e->name, name) == 0) { pthread_mutex_unlock(&reg->lock); return e->reader; }
    pthread_mutex_unlock(&reg->lock);
    return NULL;
}

void qzdb_registry_unregister(qzdb_registry_t* reg, const char* name) {
    if (!reg || !name) return;
    pthread_mutex_lock(&reg->lock);
    uint32_t idx = reg_hash(name) & (reg->cap - 1);
    reg_entry_t** pp = &reg->buckets[idx];
    while (*pp) {
        if (strcmp((*pp)->name, name) == 0) {
            reg_entry_t* del = *pp; *pp = del->next;
            qzdb_free(del->reader); free(del->reader); free(del->name); free(del);
            reg->count--; break;
        }
        pp = &(*pp)->next;
    }
    pthread_mutex_unlock(&reg->lock);
}

int qzdb_registry_count(qzdb_registry_t* reg) {
    return reg ? (int)reg->count : 0;
}

void qzdb_registry_free(qzdb_registry_t* reg) {
    if (!reg) return;
    for (uint32_t i = 0; i < reg->cap; i++) {
        reg_entry_t* e = reg->buckets[i];
        while (e) { reg_entry_t* next = e->next; qzdb_free(e->reader); free(e->reader); free(e->name); free(e); e = next; }
    }
    free(reg->buckets); pthread_mutex_destroy(&reg->lock); free(reg);
}

/* ========================================================================
 * String pool loading
 * ======================================================================== */
static void ensure_pools_loaded(qzdb_reader_t* ctx) {
    if (ctx->pools_loaded) return;
    ctx->pools_loaded = 1;
    ctx->group_pools = calloc(ctx->actual_groups, sizeof(char***));
    ctx->group_pool_counts = calloc(ctx->actual_groups, sizeof(int*));
    ctx->pool_arena = NULL;
    if (ctx->off_pools <= 0) return;

    uint64_t pool_cursor = ctx->off_pools;
    uint64_t pool_end = ctx->off_meta > 0 ? ctx->off_meta : ctx->data_size;
    uint8_t* d = ctx->data;

    typedef struct { uint32_t count; uint32_t* offsets; uint64_t data_base; uint32_t tail; } pool_scan_t;
    pool_scan_t** scans = calloc(ctx->actual_groups, sizeof(pool_scan_t*));
    if (!scans) return;

    size_t arena_need = 0;
    for (int g = 0; g < ctx->actual_groups; g++) {
        int field_count = ctx->group_field_counts[g];
        ctx->group_pools[g] = calloc(field_count, sizeof(char**));
        ctx->group_pool_counts[g] = calloc(field_count, sizeof(int));
        scans[g] = calloc(field_count, sizeof(pool_scan_t));
        for (int f = 0; f < field_count; f++) {
            if (ctx->group_field_native[g][f]) continue;
            if (pool_cursor + 4 > pool_end) continue;
            uint32_t count;
            if (safe_read_u32(d, ctx->data_size, pool_cursor, &count) != QZDB_OK) break;
            pool_cursor += 4;
            if (ctx->off_row_schema > 0) pool_cursor += 4;
            if (count == 0 || count > 16000000) continue;
            ctx->group_pool_counts[g][f] = (int)count;
            uint32_t* offsets = malloc((count + 1) * sizeof(uint32_t));
            if (!offsets) continue;
            int offsets_ok = 1;
            for (uint32_t o = 0; o <= count; o++) {
                if (safe_read_u32(d, ctx->data_size, pool_cursor, &offsets[o]) != QZDB_OK) { offsets_ok = 0; break; }
                pool_cursor += 4;
            }
            if (!offsets_ok) { free(offsets); ctx->group_pool_counts[g][f] = 0; continue; }
            /* 偏移表是累积结构：offsets[i+1] >= offsets[i]，末项 tail 为字符串区总字节数。
             * 单调性必须强制校验 —— 仅判断 data_base+end <= data_size 时，伪造表可让每一项
             * 都横跨整个 section，arena_need 会累加成 count × section 长度（GB 级 malloc；
             * 且在 32 位 size_t 上会回绕，导致后续 memcpy 堆溢出）。
             * 有 start >= prev_end && end <= tail 后各段互不重叠且落在 [0, tail]，
             * arena_need <= tail + count 必定有界。两趟循环使用完全相同的判定以保持一致。 */
            uint64_t limit = pool_end < ctx->data_size ? pool_end : ctx->data_size;
            uint64_t avail = pool_cursor < limit ? limit - pool_cursor : 0;
            uint32_t tail = offsets[count];
            if ((uint64_t)tail > avail) { free(offsets); ctx->group_pool_counts[g][f] = 0; continue; }
            scans[g][f].count = count;
            scans[g][f].offsets = offsets;
            scans[g][f].data_base = pool_cursor;
            scans[g][f].tail = tail;
            ctx->group_pools[g][f] = calloc(count, sizeof(char*));
            uint32_t prev_end = 0;
            for (uint32_t s = 0; s < count; s++) {
                uint32_t start = offsets[s]; uint32_t end = offsets[s+1];
                if (start < prev_end || end < start || end > tail) continue;
                prev_end = end;
                arena_need += (size_t)(end - start) + 1;
            }
            pool_cursor += tail;
        }
    }

    char* arena = NULL;
    size_t arena_off = 0;
    if (arena_need > 0) {
        arena = malloc(arena_need);
        if (!arena) {
            for (int g = 0; g < ctx->actual_groups; g++) { if (!scans[g]) continue;
                for (int f = 0; f < ctx->group_field_counts[g]; f++) free(scans[g][f].offsets);
                free(scans[g]); }
            free(scans); return;
        }
        ctx->pool_arena = arena;
    }

    for (int g = 0; g < ctx->actual_groups; g++) {
        if (!scans[g]) continue;
        int field_count = ctx->group_field_counts[g];
        for (int f = 0; f < field_count; f++) {
            pool_scan_t* sc = &scans[g][f];
            if (!sc->offsets || !ctx->group_pools[g][f]) { free(sc->offsets); continue; }
            uint32_t prev_end2 = 0;
            for (uint32_t s = 0; s < sc->count; s++) {
                uint32_t start = sc->offsets[s]; uint32_t end = sc->offsets[s+1];
                /* 判定必须与上方 arena_need 预算循环逐字一致，否则 arena 会写越界 */
                if (start < prev_end2 || end < start || end > sc->tail) { ctx->group_pools[g][f][s] = NULL; continue; }
                prev_end2 = end;
                uint32_t length = end - start;
                char* dst = arena + arena_off;
                if (length > 0) memcpy(dst, d + sc->data_base + start, length);
                dst[length] = '\0';
                ctx->group_pools[g][f][s] = dst;
                arena_off += (size_t)length + 1;
            }
            free(sc->offsets);
        }
        free(scans[g]);
    }
    free(scans);
}

/* ========================================================================
 * Initialization (header parsing)
 * ======================================================================== */
int qzdb_init(qzdb_reader_t* ctx, const char* db_path) {
    return qzdb_init_ex(ctx, db_path, 1);
}

/* Common initialization: parse header, validate, load pools, init cache.
 * ctx->data and ctx->data_size must be set by the caller.
 * is_heap indicates whether ctx->data needs free() (heap) vs munmap() (mmap). */
static int init_from_buffer(qzdb_reader_t* ctx, int is_heap, int verify_crc) {
    setlocale(LC_NUMERIC, "C");

    uint8_t* d = ctx->data;
    if (ctx->data_size < 192) { return QZDB_ERR_BAD_HEADER; }
    if (memcmp(d, "QZDB", 4) != 0) { return QZDB_ERR_BAD_MAGIC; }

    int fmt_ver = d[4];
    if (fmt_ver != 1) { return QZDB_ERR_UNSUPPORTED; }

    /* VersionMask（offset 6）是档次判定的权威来源，必须在 flags 之前读出。 */
    ctx->version_mask = READ_LE16(d + 6);
    ctx->flags = READ_LE16(d + 8);
    ctx->has_v4 = (ctx->flags & 1) != 0;
    ctx->has_v6 = (ctx->flags & 2) != 0;
    ctx->v4_node_24 = (ctx->flags & 0x10) != 0;
    ctx->v6_node_24 = (ctx->flags & 0x20) != 0;
    ctx->v6_jump_bits = d[11];
    if (ctx->v6_jump_bits == 0) ctx->v6_jump_bits = 16;
    if (ctx->v6_jump_bits < 8 || ctx->v6_jump_bits > 20) { return QZDB_ERR_BAD_HEADER; }
    ctx->pool_count = d[12];
    ctx->pool_idx_size = d[13];
    if (ctx->pool_idx_size != 2 && ctx->pool_idx_size != 3) { return QZDB_ERR_BAD_HEADER; }
    ctx->geo_count = READ_LE16(d + 14);
    ctx->row_count = READ_LE32(d + 20);
    ctx->build_date = READ_LE32(d + 32);
    ctx->v4_rec_count = READ_LE32(d + 24);
    ctx->v6_rec_count = READ_LE32(d + 28);
    uint32_t hs = READ_LE32(d + 36);
    if (hs != 192) { return QZDB_ERR_CORRUPTED; }

    ctx->off_row_schema = READ_LE64(d + 40);
    ctx->off_group_schema = READ_LE64(d + 48);
    ctx->off_v4_jump = READ_LE64(d + 64);
    ctx->off_v4_nodes = READ_LE64(d + 72);
    ctx->off_v6_jump = READ_LE64(d + 80);
    ctx->off_v6_nodes = READ_LE64(d + 88);
    ctx->off_ip_row = READ_LE64(d + 96);
    ctx->off_geo_entries = READ_LE64(d + 104);
    ctx->off_pools = READ_LE64(d + 136);
    ctx->off_meta = READ_LE64(d + 144);
    ctx->v4_node_count = READ_LE32(d + 152);
    ctx->v6_node_count = READ_LE32(d + 156);
    ctx->ip_row_size = READ_LE32(d + 160);
    if (ctx->ip_row_size < 1 || ctx->ip_row_size > 64) { return QZDB_ERR_BAD_HEADER; }
    ctx->geo_entry_group_count = READ_LE32(d + 164);
    if (ctx->geo_entry_group_count < 1 || ctx->geo_entry_group_count > 255) { return QZDB_ERR_BAD_HEADER; }

    /* Bounds validation for section offsets */
    {
        uint64_t v4_ns = ctx->v4_node_24 ? 6 : 8;
        uint64_t v6_ns = ctx->v6_node_24 ? 6 : 8;
        uint64_t v6_jump_size = ((uint64_t)1 << ctx->v6_jump_bits) * 4;
        if (ctx->off_v4_jump > 0 && ctx->off_v4_jump + 65536 * 4 > ctx->data_size) { return QZDB_ERR_BOUNDS; }
        if (ctx->off_v4_nodes > 0 && ctx->off_v4_nodes + (uint64_t)ctx->v4_node_count * v4_ns > ctx->data_size) { return QZDB_ERR_BOUNDS; }
        if (ctx->off_v6_jump > 0 && ctx->off_v6_jump + v6_jump_size > ctx->data_size) { return QZDB_ERR_BOUNDS; }
        if (ctx->off_v6_nodes > 0 && ctx->off_v6_nodes + (uint64_t)ctx->v6_node_count * v6_ns > ctx->data_size) { return QZDB_ERR_BOUNDS; }
        if (ctx->off_ip_row > 0 && ctx->off_ip_row + (uint64_t)ctx->row_count * ctx->ip_row_size > ctx->data_size) { return QZDB_ERR_BOUNDS; }
        if (ctx->off_geo_entries > 0 && ctx->off_geo_entries + 16 > ctx->data_size) { return QZDB_ERR_BOUNDS; }
        if (ctx->off_group_schema > 0 && ctx->off_group_schema + 2 > ctx->data_size) { return QZDB_ERR_BOUNDS; }
        if (ctx->off_row_schema > 0 && ctx->off_row_schema >= ctx->data_size) { return QZDB_ERR_BOUNDS; }
        if (ctx->off_pools > 0 && ctx->off_pools >= ctx->data_size) { return QZDB_ERR_BOUNDS; }
        if (ctx->off_meta > 0 && ctx->off_meta > ctx->data_size) { return QZDB_ERR_BOUNDS; }
    }

    /* ---- 其余解析逻辑保持不变，但不再调用 munmap ---- */
    /* 错误路径改为仅释放已分配资源，由调用方决定 data 的释放方式 */

    ctx->row_geo_width = 3; ctx->row_asn_width = 3; ctx->row_usage_width = 0;
    if (ctx->off_row_schema > 0) {
        uint64_t sp = ctx->off_row_schema;
        uint8_t f_count = d[sp]; uint8_t stride = d[sp+1];
        if (f_count >= 1 && f_count <= 8 && sp + 4 + (uint64_t)f_count * 4 <= ctx->data_size && stride == ctx->ip_row_size) {
            uint64_t wpos = sp + 4;
            int geo_w = 0, asn_w = 0, usage_w = 0, total = 0, ok = 1;
            for (uint8_t i = 0; i < f_count; i++) {
                uint8_t fid = d[wpos]; uint8_t w = d[wpos+1];
                if (fid == 0) geo_w = w; else if (fid == 1) asn_w = w; else if (fid == 2) usage_w = w;
                wpos += 4; total += w; if (w < 1 || w > 4) ok = 0;
            }
            if (ok && total == (int)ctx->ip_row_size) { ctx->row_geo_width = geo_w; ctx->row_asn_width = asn_w; ctx->row_usage_width = usage_w; }
        }
    }

    ctx->group_entry_offsets = malloc(4 * sizeof(uint64_t));
    if (!ctx->group_entry_offsets) { return QZDB_ERR_OUT_OF_MEMORY; }
    for (int i = 0; i < 4; i++) ctx->group_entry_offsets[i] = READ_LE48(d + 168 + i * 6);

    uint64_t gm_off = ctx->off_geo_entries;
    int group_count = d[gm_off]; gm_off++;
    ctx->actual_groups = group_count < 1 ? 1 : group_count;
    if (ctx->geo_entry_group_count > 0 && ctx->geo_entry_group_count < ctx->actual_groups) ctx->actual_groups = ctx->geo_entry_group_count;
    if (ctx->actual_groups > 4) ctx->actual_groups = 4;

    ctx->group_field_counts = malloc(ctx->actual_groups * sizeof(int));
    ctx->group_entry_counts = malloc(ctx->actual_groups * sizeof(uint32_t));
    ctx->group_dim_masks = malloc(ctx->actual_groups * sizeof(uint16_t));
    if (!ctx->group_field_counts || !ctx->group_entry_counts || !ctx->group_dim_masks) {
        free(ctx->group_field_counts); free(ctx->group_entry_counts); free(ctx->group_dim_masks);
        free(ctx->group_entry_offsets); return QZDB_ERR_OUT_OF_MEMORY;
    }

    for (int gi = 0; gi < ctx->actual_groups; gi++) {
        ctx->group_field_counts[gi] = d[gm_off]; gm_off++;
        ctx->group_entry_counts[gi] = READ_LE32(d + gm_off); gm_off += 4;
        ctx->group_dim_masks[gi] = READ_LE16(d + gm_off); gm_off += 2;
    }

    ctx->group_strides = calloc(ctx->actual_groups, sizeof(int));
    ctx->group_field_widths = calloc(ctx->actual_groups, sizeof(int*));
    ctx->group_field_offsets = calloc(ctx->actual_groups, sizeof(int*));
    ctx->group_field_native = calloc(ctx->actual_groups, sizeof(int*));
    ctx->group_field_native_type = calloc(ctx->actual_groups, sizeof(int*));
    ctx->group_ids = calloc(ctx->actual_groups, sizeof(uint16_t));
    ctx->group_pool_section_ids = calloc(ctx->actual_groups, sizeof(uint32_t*));
    if (!ctx->group_strides || !ctx->group_field_widths || !ctx->group_field_offsets || !ctx->group_field_native ||
        !ctx->group_field_native_type || !ctx->group_ids || !ctx->group_pool_section_ids) {
        free(ctx->group_strides); free(ctx->group_field_widths); free(ctx->group_field_offsets);
        free(ctx->group_field_native); free(ctx->group_field_native_type); free(ctx->group_ids);
        free(ctx->group_pool_section_ids); free(ctx->group_field_counts); free(ctx->group_entry_counts);
        free(ctx->group_dim_masks); free(ctx->group_entry_offsets);
        return QZDB_ERR_OUT_OF_MEMORY;
    }

    int schema_fld_count[4];
    for (int i = 0; i < 4; i++) schema_fld_count[i] = -1;

    if (ctx->off_group_schema > 0 && ctx->off_group_schema + 2 <= ctx->data_size) {
        uint64_t sp = ctx->off_group_schema;
        int gs_group_count = READ_LE16(d + sp); sp += 2;
        int max_gs_groups = gs_group_count < ctx->actual_groups ? gs_group_count : ctx->actual_groups;
        for (int gi = 0; gi < max_gs_groups; gi++) {
            if (sp + 16 > ctx->data_size) break;
            if (gi < ctx->actual_groups) ctx->group_ids[gi] = READ_LE16(d + sp);
            sp += 2;
            int fld_count = READ_LE16(d + sp); sp += 2;
            sp += 4;
            int stride = READ_LE32(d + sp); sp += 4;
            sp += 4;
            if (fld_count < 0 || (uint64_t)fld_count * 12 > ctx->data_size - sp) break;
            if (gi < ctx->actual_groups) {
                schema_fld_count[gi] = fld_count;
                ctx->group_strides[gi] = stride;
                ctx->group_field_widths[gi] = malloc(fld_count * sizeof(int));
                ctx->group_field_offsets[gi] = malloc(fld_count * sizeof(int));
                ctx->group_field_native[gi] = malloc(fld_count * sizeof(int));
                ctx->group_field_native_type[gi] = malloc(fld_count * sizeof(int));
                ctx->group_pool_section_ids[gi] = malloc(fld_count * sizeof(uint32_t));
                for (int fi = 0; fi < fld_count; fi++) {
                    sp += 2;
                    ctx->group_field_widths[gi][fi] = d[sp]; sp++;
                    int field_flags = d[sp]; sp++;
                    ctx->group_field_native[gi][fi] = (field_flags & 0x01) != 0;
                    ctx->group_field_native_type[gi][fi] = (field_flags >> 1) & 0x03;
                    ctx->group_field_offsets[gi][fi] = READ_LE32(d + sp); sp += 4;
                    ctx->group_pool_section_ids[gi][fi] = READ_LE32(d + sp); sp += 4;
                }
            } else { sp += fld_count * 12; }
        }
    }

    for (int g = 0; g < ctx->actual_groups; g++) {
        int fc = ctx->group_field_counts[g];
        if (schema_fld_count[g] >= 0 && schema_fld_count[g] != fc) {
            free(ctx->group_field_widths[g]);       ctx->group_field_widths[g] = NULL;
            free(ctx->group_field_offsets[g]);      ctx->group_field_offsets[g] = NULL;
            free(ctx->group_field_native[g]);       ctx->group_field_native[g] = NULL;
            free(ctx->group_field_native_type[g]);  ctx->group_field_native_type[g] = NULL;
            free(ctx->group_pool_section_ids[g]);   ctx->group_pool_section_ids[g] = NULL;
            ctx->group_strides[g] = 0;
        }
        if (ctx->group_strides[g] == 0) ctx->group_strides[g] = fc * ctx->pool_idx_size;
        if (!ctx->group_field_widths[g]) { ctx->group_field_widths[g] = malloc((fc ? fc : 1) * sizeof(int));
            for (int i = 0; i < fc; i++) ctx->group_field_widths[g][i] = ctx->pool_idx_size; }
        if (!ctx->group_field_offsets[g]) { ctx->group_field_offsets[g] = malloc((fc ? fc : 1) * sizeof(int));
            for (int i = 0; i < fc; i++) ctx->group_field_offsets[g][i] = i * ctx->pool_idx_size; }
        if (!ctx->group_field_native[g]) ctx->group_field_native[g] = calloc(fc ? fc : 1, sizeof(int));
        if (!ctx->group_field_native_type[g]) ctx->group_field_native_type[g] = calloc(fc ? fc : 1, sizeof(int));
        if (!ctx->group_pool_section_ids[g]) ctx->group_pool_section_ids[g] = calloc(fc ? fc : 1, sizeof(uint32_t));
    }

    char*  meta_primary    = NULL;
    char** meta_names      = NULL;
    int    meta_name_count = 0;
    if (ctx->flags & 4 && ctx->off_meta > 0 && ctx->off_meta + 4 <= ctx->data_size) {
        uint64_t pos = ctx->off_meta;
        while (pos + 4 <= ctx->data_size) {
            int t = d[pos]; int length = READ_LE16(d + pos + 2);
            if (t == 0 || length == 0) break;
            if (pos + 4 + (uint64_t)length > ctx->data_size) break;
            char* val = malloc((size_t)length + 1);
            if (!val) break;
            memcpy(val, d + pos + 4, (size_t)length); val[length] = '\0';
            if (t == 1) { free(ctx->version_name); ctx->version_name = val; }
            else if (t == 2) {
                for (int i = 0; i < meta_name_count; i++) free(meta_names[i]);
                free(meta_names); meta_names = NULL; meta_name_count = 0;
                int cnt = 1;
                for (const char* q = val; *q; q++) if (*q == '|') cnt++;
                meta_names = calloc((size_t)cnt, sizeof(char*));
                if (meta_names) {
                    const char* seg = val; int idx = 0;
                    while (idx < cnt) {
                        const char* q = seg; while (*q && *q != '|') q++;
                        size_t tok_len = (size_t)(q - seg);
                        char* token = malloc(tok_len + 1);
                        if (!token) break;
                        memcpy(token, seg, tok_len); token[tok_len] = '\0';
                        meta_names[idx++] = token;
                        if (*q == '|') seg = q + 1; else break;
                    }
                    meta_name_count = idx;
                }
                free(val);
            }
            else if (t == 3) { free(ctx->description); ctx->description = val; }
            else if (t == 4) { free(meta_primary); meta_primary = val; }
            else free(val);
            pos += 4 + (uint64_t)length;
        }
    }

    char meta_edition[64]; meta_edition[0] = '\0';
    if (meta_primary && meta_primary[0]) {
        if (!single_version_token(meta_primary, meta_edition, sizeof(meta_edition)))
            meta_edition[0] = '\0';
    } else if (ctx->version_name && ctx->version_name[0]) {
        if (!single_version_token(ctx->version_name, meta_edition, sizeof(meta_edition)))
            meta_edition[0] = '\0';
    }

    ctx->group_field_names     = calloc((size_t)ctx->actual_groups, sizeof(char**));
    ctx->group_editions        = calloc((size_t)ctx->actual_groups, sizeof(const char*));
    ctx->group_edition_sources = calloc((size_t)ctx->actual_groups, sizeof(const char*));
    ctx->group_name_sources    = calloc((size_t)ctx->actual_groups, sizeof(const char*));
    if (!ctx->group_field_names || !ctx->group_editions ||
        !ctx->group_edition_sources || !ctx->group_name_sources) {
        for (int i = 0; i < meta_name_count; i++) free(meta_names[i]);
        free(meta_names); free(meta_primary);
        return QZDB_ERR_OUT_OF_MEMORY;
    }

    for (int g = 0; g < ctx->actual_groups; g++) {
        int nf = ctx->group_field_counts[g];

        uint16_t mask = ctx->group_ids[g] ? ctx->group_ids[g] : ctx->version_mask;
        const char* edition = qzdb_edition_from_mask(mask);
        const char* source  = QZDB_EDITION_SOURCE_VERSION_MASK;
        if (!edition[0] && meta_edition[0]) {
            for (int i = 0; i < 5; i++) {
                if (strcmp(meta_edition, EDITION_BY_BIT[i]) == 0) {
                    edition = EDITION_BY_BIT[i];
                    source  = QZDB_EDITION_SOURCE_METADATA;
                    break;
                }
            }
        }
        if (!edition[0]) {
            edition = edition_by_field_count(nf);
            source  = edition[0] ? QZDB_EDITION_SOURCE_INFERRED : QZDB_EDITION_SOURCE_UNKNOWN;
        }

        char** names = calloc((size_t)(nf + 1), sizeof(char*));
        if (!names) {
            for (int i = 0; i < meta_name_count; i++) free(meta_names[i]);
            free(meta_names); free(meta_primary);
            return QZDB_ERR_OUT_OF_MEMORY;
        }
        int canon_n = 0;
        const char* const* canon = edition_field_names(edition, &canon_n);
        const char* name_source;
        if (meta_names && meta_name_count == nf && nf > 0) {
            for (int i = 0; i < nf; i++) names[i] = strdup(meta_names[i]);
            name_source = QZDB_FIELD_NAMES_SOURCE_METADATA;
        } else if (canon && canon_n == nf) {
            for (int i = 0; i < nf; i++) names[i] = strdup(canon[i]);
            name_source = QZDB_FIELD_NAMES_SOURCE_EDITION;
        } else {
            for (int i = 0; i < nf; i++) {
                char b[32]; snprintf(b, sizeof(b), "field_%d", i);
                names[i] = strdup(b);
            }
            name_source = QZDB_FIELD_NAMES_SOURCE_SYNTHETIC;
        }

        ctx->group_field_names[g]     = names;
        ctx->group_editions[g]        = edition;
        ctx->group_edition_sources[g] = source;
        ctx->group_name_sources[g]    = name_source;
    }
    for (int i = 0; i < meta_name_count; i++) free(meta_names[i]);
    free(meta_names);
    free(meta_primary);

    if (apply_group_meta(ctx, ctx->group_index) != QZDB_OK) {
        return QZDB_ERR_OUT_OF_MEMORY;
    }

    if (ctx->build_date > 0) {
        int y = ctx->build_date / 10000; int m = (ctx->build_date / 100) % 100; int dd = ctx->build_date % 100;
        char b1[32], b2[32];
        snprintf(b1, sizeof(b1), "%04d-%02d", y, m); snprintf(b2, sizeof(b2), "%04d-%02d-%02d", y, m, dd);
        ctx->data_month = strdup(b1); ctx->build_time_str = strdup(b2);
    } else { ctx->data_month = strdup(""); ctx->build_time_str = strdup(""); }

    for (int g = 0; g < ctx->actual_groups; g++) {
        if (ctx->group_dim_masks[g] != 0) continue;
        int has_asn = 0;
        char** gn = ctx->group_field_names[g];
        for (int fi = 0; gn && fi < ctx->group_field_counts[g]; fi++) {
            char norm[64];
            normalize_field_name(gn[fi] ? gn[fi] : "", norm, sizeof(norm));
            if (strcmp(norm, "asn") == 0) { has_asn = 1; break; }
        }
        ctx->group_dim_masks[g] = has_asn ? 0x02 : 0x01;
    }

    ctx->pools_loaded = 0; ctx->group_pools = NULL; ctx->group_pool_counts = NULL;
    ensure_pools_loaded(ctx);
    geo_cache_init(ctx);

    switch (ctx->pool_count) { case 6: ctx->version_code = 1; break; case 7: ctx->version_code = 2; break; case 25: ctx->version_code = 3; break; default: ctx->version_code = 3; break; }

    if (verify_crc) {
        int rc_crc = qzdb_verify_crc(ctx);
        if (rc_crc != QZDB_OK) { return QZDB_ERR_CORRUPTED; }
    }
    return QZDB_OK;
}

int qzdb_init_ex(qzdb_reader_t* ctx, const char* db_path, int verify_crc) {
    if (!ctx || !db_path) return QZDB_ERR_INVALID_PARAM;
    memset(ctx, 0, sizeof(*ctx));
    int fd = open(db_path, O_RDONLY);
    if (fd < 0) return QZDB_ERR_CORRUPTED;
    struct stat st;
    if (fstat(fd, &st) != 0) { close(fd); return QZDB_ERR_CORRUPTED; }
    ctx->data_size = st.st_size;
    ctx->data = mmap(NULL, ctx->data_size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (ctx->data == MAP_FAILED) { ctx->data = NULL; return QZDB_ERR_OUT_OF_MEMORY; }
    madvise(ctx->data, ctx->data_size, MADV_RANDOM);
    ctx->data_is_heap = 0;
    int rc = init_from_buffer(ctx, 0, verify_crc);
    if (rc != QZDB_OK) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL;
    }
    return rc;
}

/* Buffer-based loading — default copy semantics via temp file + mmap.
 * The buffer is written to a secure temp file which is then mmapped;
 * the original buffer can be freed immediately after return. */
int qzdb_init_buffer(qzdb_reader_t* ctx, const uint8_t* buf, size_t len, int verify_crc) {
    if (!ctx || !buf || len == 0) return QZDB_ERR_INVALID_PARAM;
    memset(ctx, 0, sizeof(*ctx));
    char tmpl[] = "/tmp/qzdb_buf_XXXXXX";
    int fd = mkstemp(tmpl);
    if (fd < 0) return QZDB_ERR_OUT_OF_MEMORY;
    ssize_t w = write(fd, buf, len);
    if (w != (ssize_t)len) { close(fd); unlink(tmpl); return QZDB_ERR_CORRUPTED; }
    close(fd);
    int rc = qzdb_init_ex(ctx, tmpl, verify_crc);
    unlink(tmpl);
    return rc;
}

/* Buffer-based loading — zero-copy variant: caller keeps buf alive.
 * ctx->data points directly into the caller's buffer (no temp file, no mmap).
 * ASAN can detect out-of-bounds reads from the original buffer. */
int qzdb_init_buffer_borrowed(qzdb_reader_t* ctx, const uint8_t* buf, size_t len, int verify_crc) {
    if (!ctx || !buf || len == 0) return QZDB_ERR_INVALID_PARAM;
    memset(ctx, 0, sizeof(*ctx));
    ctx->data = (uint8_t*)buf;
    ctx->data_size = len;
    ctx->data_is_heap = 0;
    return init_from_buffer(ctx, 0, verify_crc);
}
void qzdb_free(qzdb_reader_t* ctx) {
    if (!ctx) return;
    if (!ctx->data) return;
    free(ctx->pool_arena); ctx->pool_arena = NULL;
    if (ctx->group_pools) {
        for (int g = 0; g < ctx->actual_groups; g++) {
            if (ctx->group_pools[g]) { for (int f = 0; f < ctx->group_field_counts[g]; f++) free(ctx->group_pools[g][f]); free(ctx->group_pools[g]); }
            free(ctx->group_pool_counts[g]);
        }
        free(ctx->group_pools); free(ctx->group_pool_counts);
    }
    free(ctx->group_entry_offsets);
    for (int g = 0; g < ctx->actual_groups; g++) {
        free(ctx->group_field_widths[g]); free(ctx->group_field_offsets[g]);
        free(ctx->group_field_native[g]); free(ctx->group_field_native_type[g]);
        free(ctx->group_pool_section_ids[g]);
        /* 每组字段名表（field_names 只是其中一行的借用，不重复释放） */
        if (ctx->group_field_names && ctx->group_field_names[g]) {
            for (int i = 0; i < ctx->group_field_counts[g]; i++) free(ctx->group_field_names[g][i]);
            free(ctx->group_field_names[g]);
        }
    }
    free(ctx->group_field_counts); free(ctx->group_entry_counts); free(ctx->group_dim_masks); free(ctx->group_strides);
    free(ctx->group_field_widths); free(ctx->group_field_offsets); free(ctx->group_field_native);
    free(ctx->group_field_native_type); free(ctx->group_ids); free(ctx->group_pool_section_ids);
    free(ctx->group_field_names);
    /* group_editions / *_sources 指向静态字符串，只释放外层指针数组 */
    free(ctx->group_editions); free(ctx->group_edition_sources); free(ctx->group_name_sources);
    ctx->field_names = NULL;
    free(ctx->float_field_flags); free(ctx->version_name); free(ctx->description);
    free(ctx->edition); free(ctx->data_month); free(ctx->build_time_str);
    if (ctx->norm_field_names) { for (int i = 0; i < ctx->field_count; i++) free(ctx->norm_field_names[i]); free(ctx->norm_field_names); }
    norm_map_free(ctx);
    geo_cache_free(ctx);
    if (ctx->data_is_heap) free(ctx->data);
    else if (ctx->data) munmap(ctx->data, ctx->data_size);
    memset(ctx, 0, sizeof(*ctx));
}

int qzdb_verify_crc(qzdb_reader_t* ctx) {
    if (!ctx) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->data || ctx->data_size < 20) return QZDB_ERR_CORRUPTED;
    uint32_t stored = READ_LE32(ctx->data + 16);
    uint32_t computed = crc32_compute_file(ctx->data, ctx->data_size);
    ctx->file_crc = computed; ctx->crc_valid = 1;  /* NIT-2: cache result */
    return stored == computed ? QZDB_OK : QZDB_ERR_CORRUPTED;
}

int qzdb_reload_buffer(qzdb_reader_t* ctx, const uint8_t* buf, size_t len) {
    if (!ctx || !buf || len == 0) return QZDB_ERR_INVALID_PARAM;
    qzdb_reader_t new_ctx;
    memset(&new_ctx, 0, sizeof(new_ctx));
    int rc = qzdb_init_buffer(&new_ctx, buf, len, 1);
    if (rc != QZDB_OK) return rc;
    qzdb_free(ctx);
    memcpy(ctx, &new_ctx, sizeof(*ctx));
    return QZDB_OK;
}

/* ========================================================================
 * Reload (spec §4.3 — build shadow, then atomic swap)
 * ======================================================================== */
int qzdb_reload(qzdb_reader_t* ctx, const char* db_path) {
    if (!ctx || !db_path) return QZDB_ERR_INVALID_PARAM;
    qzdb_reader_t new_ctx;
    memset(&new_ctx, 0, sizeof(new_ctx));
    int result = qzdb_init(&new_ctx, db_path);  /* reload: CRC always enforced (spec §4.2) */
    if (result != QZDB_OK) return result;
    qzdb_free(ctx);
    memcpy(ctx, &new_ctx, sizeof(*ctx));
    return QZDB_OK;
}

/* ========================================================================
 * Singleton instance
 * ======================================================================== */
/* ========================================================================
 * Group index setter
 * ======================================================================== */
int qzdb_set_group_index(qzdb_reader_t* ctx, int group_index) {
    if (!ctx) return QZDB_ERR_INVALID_PARAM;
    if (group_index < 0 || group_index >= ctx->actual_groups) return QZDB_ERR_INVALID_PARAM;
    if (group_index == ctx->group_index) return QZDB_OK;
    /* 字段名/归一化索引/edition 都是按组解析的，切组必须同步重绑，
     * 否则 get_field_names() 与实际读取的组会错位。 */
    int rc = apply_group_meta(ctx, group_index);
    if (rc != QZDB_OK) return rc;
    ctx->group_index = group_index;
    return QZDB_OK;
}

/* ========================================================================
 * Query implementations
 * ======================================================================== */
int qzdb_find_uint(qzdb_reader_t* ctx, uint32_t ip_int, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->has_v4) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v4(ctx, ip_int);
    if (row_id == 0) return QZDB_ERR_NOT_FOUND;
    return resolve_row_id_cached(ctx, row_id, ctx->group_index, result);
}

int qzdb_find_v6(qzdb_reader_t* ctx, const uint8_t* ip_bin, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->has_v6) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v6(ctx, ip_bin);
    if (row_id == 0) return QZDB_ERR_NOT_FOUND;
    return resolve_row_id_cached(ctx, row_id, ctx->group_index, result);
}

int qzdb_find_bytes(qzdb_reader_t* ctx, const uint8_t ip_bin[16], qzdb_geo_info_t* result) {
    if (!ctx || !ip_bin || !result) return QZDB_ERR_INVALID_PARAM;
    if (is_v4_mapped(ip_bin)) return qzdb_find_uint(ctx, v4_from_mapped(ip_bin), result);
    return qzdb_find_v6(ctx, ip_bin, result);
}

int qzdb_find(qzdb_reader_t* ctx, const char* ip_str, qzdb_geo_info_t* result) {
    if (!ctx || !ip_str || !result) return QZDB_ERR_INVALID_PARAM;
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) return QZDB_ERR_INVALID_PARAM;
    if (res.is_v4) return qzdb_find_uint(ctx, res.v4, result);
    return qzdb_find_v6(ctx, res.v6, result);
}

int qzdb_parse_ip(const char* s, uint32_t* v4_out, uint8_t v6_out[16], int* is_v4) {
    parse_result_t res;
    if (!fast_parse_ip(s, &res)) return 0;
    if (is_v4) *is_v4 = res.is_v4;
    if (res.is_v4) { if (v4_out) *v4_out = res.v4; }
    else { if (v6_out) memcpy(v6_out, res.v6, 16); }
    return 1;
}

uint32_t qzdb_lookup_row_id(qzdb_reader_t* ctx, const char* ip_str) {
    if (!ip_str || !ctx) return 0;
    parse_result_t res; if (!fast_parse_ip(ip_str, &res)) return 0;
    if (res.is_v4) return ctx->has_v4 ? trie_walk_v4(ctx, res.v4) : 0;
    return ctx->has_v6 ? trie_walk_v6(ctx, res.v6) : 0;
}

uint32_t qzdb_lookup_row_id_uint(qzdb_reader_t* ctx, uint32_t ip_int) {
    if (!ctx->has_v4) return 0;
    return trie_walk_v4(ctx, ip_int);
}

uint32_t qzdb_lookup_row_id_v6(qzdb_reader_t* ctx, const uint8_t* ip_bin) {
    if (!ctx->has_v6) return 0;
    return trie_walk_v6(ctx, ip_bin);
}

uint32_t qzdb_lookup_row_id_bytes(qzdb_reader_t* ctx, const uint8_t* ip_bytes, int len) {
    if (!ctx || !ip_bytes) return 0;
    if (len == 16) {
        if (is_v4_mapped(ip_bytes)) return ctx->has_v4 ? trie_walk_v4(ctx, v4_from_mapped(ip_bytes)) : 0;
        return ctx->has_v6 ? trie_walk_v6(ctx, ip_bytes) : 0;
    }
    if (len == 4) { uint32_t v4 = ((uint32_t)ip_bytes[0] << 24) | ((uint32_t)ip_bytes[1] << 16) |
        ((uint32_t)ip_bytes[2] << 8) | (uint32_t)ip_bytes[3]; return ctx->has_v4 ? trie_walk_v4(ctx, v4) : 0; }
    return 0;
}

int qzdb_lookup_ids(qzdb_reader_t* ctx, uint32_t row_id, qzdb_ids_t* out) {
    if (!ctx || !out) return QZDB_ERR_INVALID_PARAM;
    memset(out, 0, sizeof(*out));
    if (row_id == 0 || row_id >= (uint32_t)ctx->row_count) return QZDB_ERR_INVALID_PARAM;
    uint32_t geo_id = 0, asn_id = 0, usage_id = 0;
    int err = read_ip_row(ctx, row_id, &geo_id, &asn_id, &usage_id);
    if (err != QZDB_OK) return err;
    out->geo_id = geo_id; out->asn_id = asn_id; out->usage_id = usage_id;
    return QZDB_OK;
}

/* === find_str (WARN-8 fix: preserve distinct error codes) === */
int qzdb_find_str(qzdb_reader_t* ctx, const char* ip_str, char* out, size_t out_size) {
    if (!ctx || !ip_str || !out || out_size == 0) return QZDB_ERR_INVALID_PARAM;
    qzdb_geo_info_t info;
    int result = qzdb_find(ctx, ip_str, &info);
    if (result != QZDB_OK) { if (out_size > 0) out[0] = '\0'; return result; }  /* preserve error code */
    size_t pos = 0;
    int field_count = ctx->group_field_counts[ctx->group_index];
    for (int i = 0; i < field_count && i < QZDB_MAX_FIELDS; i++) {
        if (i > 0 && pos < out_size - 1) out[pos++] = '|';
        const char* val = info.values[i] ? info.values[i] : "";
        size_t len = strlen(val);
        if (pos + len >= out_size) { if (out_size > pos) { memcpy(out + pos, val, out_size - pos - 1); pos = out_size - 1; } break; }
        memcpy(out + pos, val, len); pos += len;
    }
    out[pos] = '\0';
    free_geo_info(&info);
    return QZDB_OK;
}

/* === Caller-buffer query variants === */
int qzdb_find_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int, char** values, char (*bufs)[64], int buf_size) {
    if (!ctx || !values || !bufs) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->has_v4) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v4(ctx, ip_int);
    if (row_id == 0) return 0;
    int count = 0;
    int rc = resolve_row_id_buf(ctx, row_id, ctx->group_index, values, bufs, buf_size, &count);
    return rc == 0 ? count : QZDB_ERR_CORRUPTED;
}

int qzdb_find_v6_buf(qzdb_reader_t* ctx, const uint8_t* ip_bin, char** values, char (*bufs)[64], int buf_size) {
    if (!ctx || !values || !bufs) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->has_v6) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v6(ctx, ip_bin);
    if (row_id == 0) return 0;
    int count = 0;
    int rc = resolve_row_id_buf(ctx, row_id, ctx->group_index, values, bufs, buf_size, &count);
    return rc == 0 ? count : QZDB_ERR_CORRUPTED;
}

/* === Field-projection with BUG-2 fix (entry_id==0 → NOT_FOUND) === */
static int resolve_row_id_fields(qzdb_reader_t* ctx, uint32_t row_id, int group_index,
                                  const char** field_names, int field_count,
                                  char** values, char (*bufs)[64], int buf_size) {
    if (!ctx || !field_names || !values || !bufs) return QZDB_ERR_INVALID_PARAM;
    if (row_id <= 0 || row_id >= (uint32_t)ctx->row_count) return QZDB_ERR_INVALID_PARAM;
    uint32_t geo_id = 0, asn_id = 0, usage_id = 0;
    if (read_ip_row(ctx, row_id, &geo_id, &asn_id, &usage_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    uint16_t mask = group_index < ctx->actual_groups ? ctx->group_dim_masks[group_index] : 0;
    uint32_t entry_id = geo_id;
    if (mask & 0x02) entry_id = asn_id;
    else if (mask & 0x04) entry_id = usage_id;
    if (entry_id == 0) return QZDB_ERR_NOT_FOUND;  /* BUG-2 fix */
    if (group_index < 0 || group_index >= ctx->actual_groups) return QZDB_ERR_INVALID_PARAM;
    if (entry_id >= ctx->group_entry_counts[group_index]) return QZDB_ERR_INVALID_PARAM;
    int total_field_count = ctx->group_field_counts[group_index];
    if (total_field_count <= 0) return QZDB_ERR_CORRUPTED;

    int indices[QZDB_MAX_FIELDS]; int idx_count = 0;
    for (int fi = 0; fi < field_count && field_names[fi] != NULL; fi++) {
        int i = field_index_normalized(ctx, field_names[fi]);
        if (i >= 0) indices[idx_count++] = i;
    }
    if (idx_count == 0) return QZDB_ERR_NOT_FOUND;

    uint64_t group_entry_start = ctx->off_geo_entries + ctx->group_entry_offsets[group_index];
    int stride = ctx->group_strides[group_index];
    uint64_t entry_offset = group_entry_start + (uint64_t)entry_id * stride;
    int* widths = ctx->group_field_widths[group_index];
    int* base_offsets = ctx->group_field_offsets[group_index];
    int* natives = ctx->group_field_native[group_index];
    int* nat_types = ctx->group_field_native_type[group_index];

    for (int ki = 0; ki < idx_count; ki++) {
        int i = indices[ki]; if (i < 0 || i >= total_field_count) continue;
        int w = widths[i]; uint64_t fo = entry_offset + base_offsets[i]; int is_native = natives[i];
        if (is_native) {
            int t = nat_types[i];
            if (t == 1) {
                if (w == 4) { union { uint32_t u; float f; } u;
                    if (safe_read_u32(ctx->data, ctx->data_size, fo, &u.u) != QZDB_OK) return QZDB_ERR_BOUNDS;
                    format_float32_value(u.f, bufs[i], buf_size); }
                else { union { uint64_t u; double d; } u;
                    if (safe_read_u64(ctx->data, ctx->data_size, fo, &u.u) != QZDB_OK) return QZDB_ERR_BOUNDS;
                    format_float_value(u.d, bufs[i], buf_size); }
            } else { uint32_t val;
                if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &val) != QZDB_OK) return QZDB_ERR_BOUNDS;
                snprintf(bufs[i], buf_size, "%lu", (unsigned long)val); }
            values[i] = bufs[i];
        } else { uint32_t idx;
            if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &idx) != QZDB_OK) return QZDB_ERR_BOUNDS;
            if (ctx->group_pools[group_index] && ctx->group_pools[group_index][i] && (int)idx < ctx->group_pool_counts[group_index][i])
                values[i] = ctx->group_pools[group_index][i][idx];
            else values[i] = ""; }
    }
    return total_field_count;
}

int qzdb_find_fields_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int,
                               const char** field_names, char** values, char (*bufs)[64], int buf_size) {
    if (!ctx || !values || !bufs) return QZDB_ERR_INVALID_PARAM;
    if (field_names == NULL) return qzdb_find_uint_buf(ctx, ip_int, values, bufs, buf_size);
    if (!ctx->has_v4) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v4(ctx, ip_int);
    if (row_id == 0) return 0;
    return resolve_row_id_fields(ctx, row_id, ctx->group_index, field_names, QZDB_MAX_FIELDS, values, bufs, buf_size);
}

/* BUG-1 fix: when field_names is NULL, fill caller buffers instead of just returning 1 */
int qzdb_find_fields_buf(qzdb_reader_t* ctx, const char* ip_str,
                          const char** field_names, char** values, char (*bufs)[64], int buf_size) {
    if (!ctx || !ip_str || !values || !bufs) return QZDB_ERR_INVALID_PARAM;
    if (field_names == NULL || field_names[0] == NULL) {
        /* Equivalent to find_uint_buf / find_v6_buf — fill all fields */
        parse_result_t res;
        if (!fast_parse_ip(ip_str, &res)) return QZDB_ERR_INVALID_PARAM;
        if (res.is_v4) return qzdb_find_uint_buf(ctx, res.v4, values, bufs, buf_size);
        if (!ctx->has_v6) return QZDB_ERR_NOT_FOUND;
        uint32_t row_id = trie_walk_v6(ctx, res.v6);
        if (row_id == 0) return 0;
        int count = 0;
        int rc = resolve_row_id_buf(ctx, row_id, ctx->group_index, values, bufs, buf_size, &count);
        return rc == 0 ? count : QZDB_ERR_CORRUPTED;
    }
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) return QZDB_ERR_INVALID_PARAM;
    if (res.is_v4) return qzdb_find_fields_uint_buf(ctx, res.v4, field_names, values, bufs, buf_size);
    if (!ctx->has_v6) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v6(ctx, res.v6);
    if (row_id == 0) return 0;
    return resolve_row_id_fields(ctx, row_id, ctx->group_index, field_names, QZDB_MAX_FIELDS, values, bufs, buf_size);
}

int qzdb_find_fields_uint(qzdb_reader_t* ctx, uint32_t ip_int,
                           const char** fields, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    if (fields == NULL) return qzdb_find_uint(ctx, ip_int, result);
    if (!ctx->has_v4) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v4(ctx, ip_int);
    if (row_id == 0) return QZDB_ERR_NOT_FOUND;
    uint32_t geo_id, asn_id, usage_id;
    if (read_ip_row(ctx, row_id, &geo_id, &asn_id, &usage_id) != QZDB_OK) return QZDB_ERR_CORRUPTED;
    uint16_t mask = ctx->group_index < ctx->actual_groups ? ctx->group_dim_masks[ctx->group_index] : 0;
    uint32_t entry_id = geo_id;
    if (mask & 0x02) entry_id = asn_id;
    else if (mask & 0x04) entry_id = usage_id;
    if (entry_id == 0) return QZDB_ERR_NOT_FOUND;
    char bufs[QZDB_MAX_FIELDS][64]; char* vals[QZDB_MAX_FIELDS]; int cnt = 0;
    if (get_geo_info_buf(ctx, entry_id, ctx->group_index, vals, bufs, 64, &cnt) != QZDB_OK) return QZDB_ERR_CORRUPTED;
    memset(result, 0, sizeof(*result));
    for (int i = 0; i < QZDB_MAX_FIELDS; i++) result->values[i] = "";
    for (int fi = 0; fields[fi] != NULL; fi++) {
        int fidx = field_index_normalized(ctx, fields[fi]);
        if (fidx >= 0 && fidx < cnt && fidx < QZDB_MAX_FIELDS) {
            result->values[fidx] = strdup(vals[fidx] ? vals[fidx] : "");
            result->values_mask |= (1u << fidx);
        }
    }
    return QZDB_OK;
}

int qzdb_find_fields(qzdb_reader_t* ctx, const char* ip_str,
                      const char** fields, qzdb_geo_info_t* result) {
    if (!ctx || !ip_str || !result) return QZDB_ERR_INVALID_PARAM;
    if (fields == NULL) return qzdb_find(ctx, ip_str, result);
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) return QZDB_ERR_INVALID_PARAM;
    if (res.is_v4) return qzdb_find_fields_uint(ctx, res.v4, fields, result);
    if (!ctx->has_v6) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v6(ctx, res.v6);
    if (row_id == 0) return QZDB_ERR_NOT_FOUND;
    uint32_t geo_id, asn_id, usage_id;
    if (read_ip_row(ctx, row_id, &geo_id, &asn_id, &usage_id) != QZDB_OK) return QZDB_ERR_CORRUPTED;
    uint16_t mask = ctx->group_index < ctx->actual_groups ? ctx->group_dim_masks[ctx->group_index] : 0;
    uint32_t entry_id = geo_id;
    if (mask & 0x02) entry_id = asn_id;
    else if (mask & 0x04) entry_id = usage_id;
    if (entry_id == 0) return QZDB_ERR_NOT_FOUND;
    char bufs[QZDB_MAX_FIELDS][64]; char* vals[QZDB_MAX_FIELDS]; int cnt = 0;
    if (get_geo_info_buf(ctx, entry_id, ctx->group_index, vals, bufs, 64, &cnt) != QZDB_OK) return QZDB_ERR_CORRUPTED;
    memset(result, 0, sizeof(*result));
    for (int i = 0; i < QZDB_MAX_FIELDS; i++) result->values[i] = "";
    for (int fi = 0; fields[fi] != NULL; fi++) {
        int fidx = field_index_normalized(ctx, fields[fi]);
        if (fidx >= 0 && fidx < cnt && fidx < QZDB_MAX_FIELDS) {
            result->values[fidx] = strdup(vals[fidx] ? vals[fidx] : "");
            result->values_mask |= (1u << fidx);
        }
    }
    return QZDB_OK;
}
