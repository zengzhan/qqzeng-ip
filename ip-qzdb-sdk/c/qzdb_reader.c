/* Feature-test macros: _DARWIN_C_SOURCE unlocks strdup()/madvise() on macOS.
 * On glibc, -std=c11 (strict ISO C) hides those same declarations, which makes
 * strdup() fall back to an implicit int-returning declaration -> pointer
 * truncation on LP64, and leaves MADV_RANDOM undefined. _DEFAULT_SOURCE is the
 * glibc equivalent; defining both keeps this file clean on macOS and Linux
 * without depending on a compiler's default GNU dialect. */
#define _DARWIN_C_SOURCE
#define _DEFAULT_SOURCE
#ifndef _POSIX_C_SOURCE
#define _POSIX_C_SOURCE 200809L
#endif
#include "qzdb_reader.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
/* 平台说明：本文件依赖 POSIX（mmap/open/pthread/newlocale），
 * 在 Windows/MSVC 下不可直接编译。Windows 用户请使用其他语言的 SDK
 * （Go / Java / C# / Node.js / Python 均支持 Windows），或基于
 * qzdb_init_buffer* 自行移植（内存数据路径本身与平台无关，但本文件内的
 * locale/pthread/mmap 部分需替换为平台实现）。 */
#include <sys/mman.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>
#include <math.h>
#include <pthread.h>
#include <sched.h>
#include <locale.h>
#if defined(__APPLE__)
/* macOS 15 及更早 SDK 的 <locale.h> 不声明 locale_t/newlocale/uselocale/freelocale
 * （直到 macOS 26 SDK 的 _locale.h 才引入 _locale_posix2008.h），需显式引入 xlocale.h。 */
#include <xlocale.h>
#endif
#if defined(__aarch64__) || defined(_M_ARM64)
#if defined(__ARM_FEATURE_CRC32)
#include <arm_acle.h>
#endif
#endif

/* ========================================================================
 * Error messages
 * ======================================================================== */
static const char* error_messages[] = {
    "Success", "Not found", "Corrupted data", "Out of memory",
    "Invalid parameter", "Bad header", "Bad magic", "Unsupported format",
    "Bounds check failed"
};

const char* qzdb_strerror(int error_code) {
    /* qzdb_error_t is 0 (QZDB_OK) or negative (QZDB_ERR_* = -1..-8); the table
     * above is indexed by the *negated* code. The previous `error_code >= 0`
     * guard meant every real error fell through to "Unknown error" and only
     * QZDB_OK ever rendered correctly. */
    int idx = error_code <= 0 ? -error_code : -1;
    if (idx >= 0 && idx < (int)(sizeof(error_messages)/sizeof(error_messages[0])))
        return error_messages[idx];
    return "Unknown error";
}

/* ========================================================================
 * CRC32 (Hardware-accelerated with software fallback)
 * ======================================================================== */
#if !((defined(__aarch64__) || defined(_M_ARM64)) && defined(__ARM_FEATURE_CRC32))
static uint32_t crc32_table[256];
static pthread_once_t crc32_once = PTHREAD_ONCE_INIT;

static void crc32_init_once(void) {
    for (uint32_t i = 0; i < 256; i++) {
        uint32_t c = i;
        for (int j = 0; j < 8; j++)
            c = (c & 1) ? (c >> 1) ^ 0xEDB88320 : c >> 1;
        crc32_table[i] = c;
    }
}
#endif

/* 分支预测提示：仅标注热循环中的罕见退出路径（GCC/Clang 扩展，其它编译器退化为普通 if） */
#if defined(__GNUC__) || defined(__clang__)
#define QZDB_UNLIKELY(x) (__builtin_expect(!!(x), 0))
#define QZDB_UNUSED __attribute__((unused))
#else
#define QZDB_UNLIKELY(x) (x)
#define QZDB_UNUSED
#endif

static uint32_t crc32_update(uint32_t crc, const uint8_t* buf, size_t len) {
#if (defined(__aarch64__) || defined(_M_ARM64)) && defined(__ARM_FEATURE_CRC32)
    while (len >= 8 && ((uintptr_t)buf & 7) == 0) {
        crc = __crc32d(crc, *(const uint64_t*)buf);
        buf += 8; len -= 8;
    }
    while (len > 0) {
        crc = __crc32b(crc, *buf);
        buf++; len--;
    }
    return crc;
#else
    /* pthread_once makes the lazy lookup table initialization race-free while
     * keeping the hot path read-only after the first call. */
    (void)pthread_once(&crc32_once, crc32_init_once);
    for (size_t i = 0; i < len; i++)
        crc = crc32_table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
    return crc;
#endif
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
    if (off > (uint64_t)data_size || (uint64_t)data_size - off < 2) return QZDB_ERR_BOUNDS;
    *out = (uint16_t)data[off] | ((uint16_t)data[off+1] << 8);
    return QZDB_OK;
}

static int safe_read_u24(const uint8_t* data, size_t data_size, uint64_t off, uint32_t* out) {
    if (off > (uint64_t)data_size || (uint64_t)data_size - off < 3) return QZDB_ERR_BOUNDS;
    *out = (uint32_t)data[off] | ((uint32_t)data[off+1] << 8) | ((uint32_t)data[off+2] << 16);
    return QZDB_OK;
}

static int safe_read_u32(const uint8_t* data, size_t data_size, uint64_t off, uint32_t* out) {
    if (off > (uint64_t)data_size || (uint64_t)data_size - off < 4) return QZDB_ERR_BOUNDS;
    *out = (uint32_t)data[off] | ((uint32_t)data[off+1] << 8) | ((uint32_t)data[off+2] << 16) | ((uint32_t)data[off+3] << 24);
    return QZDB_OK;
}

static int safe_read_u64(const uint8_t* data, size_t data_size, uint64_t off, uint64_t* out) {
    if (off > (uint64_t)data_size || (uint64_t)data_size - off < 8) return QZDB_ERR_BOUNDS;
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
    if (w < 1 || w > 4) return QZDB_ERR_BOUNDS;
    if (w == 1) {
        if (off >= data_size) return QZDB_ERR_BOUNDS;
        *out = data[off];
    } else if (w == 2) {
        uint16_t v; int r = safe_read_u16(data, data_size, off, &v);
        if (r != QZDB_OK) return r;
        *out = v;
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

/* Build O(1) normalized-name → index hash table (spec §6.1 performance mandate).
 * Empty buckets are marked by hash == 0; note FNV-1a is *not* guaranteed to
 * never yield 0 for non-empty strings (the hash is 0 iff an intermediate
 * state equals the next input byte, since the FNV prime is odd/invertible
 * mod 2^32) — both build and lookup map 0 -> 1, so the sentinel stays
 * unambiguous. */
static int norm_map_build_values(const char* const* names, int n, qzdb_norm_map_t* map) {
    if (!map || n < 0 || n > QZDB_MAX_FIELDS) return QZDB_ERR_INVALID_PARAM;
    memset(map, 0, sizeof(*map));
    map->cap = 1;
    while (map->cap < (uint32_t)(n * 4)) map->cap <<= 1;
    map->mask = map->cap - 1;
    map->buckets = calloc(map->cap, sizeof(qzdb_norm_entry_t));
    if (!map->buckets) {
        map->cap = 0;
        map->mask = 0;
        return QZDB_ERR_OUT_OF_MEMORY;
    }
    for (int i = 0; i < n; i++) {
        const char* nn = names ? names[i] : NULL;
        if (!nn) continue;
        uint32_t h = fnv1a(nn);
        if (h == 0) h = 1;
        uint32_t idx = h & map->mask;
        while (map->buckets[idx].hash != 0) idx = (idx + 1) & map->mask;
        map->buckets[idx].hash = h;
        map->buckets[idx].index = i;
        map->count++;
    }
    return QZDB_OK;
}

static void norm_map_destroy(qzdb_norm_map_t* map) {
    if (!map) return;
    free(map->buckets);
    memset(map, 0, sizeof(*map));
}

static void norm_map_free(qzdb_reader_t* ctx) {
    if (ctx) norm_map_destroy(&ctx->norm_map);
}

/* 把 group g 的字段名视图绑定到 ctx->field_names / 归一化索引 / float 标志。 */
static int apply_group_meta(qzdb_reader_t* ctx, int g) {
    if (!ctx || g < 0 || g >= ctx->actual_groups || !ctx->group_field_names ||
        !ctx->group_field_counts || !ctx->group_editions || !ctx->group_edition_sources ||
        !ctx->group_name_sources) return QZDB_ERR_INVALID_PARAM;
    int nf = ctx->group_field_counts[g];
    if (nf <= 0 || nf > QZDB_MAX_FIELDS) return QZDB_ERR_CORRUPTED;

    char* new_edition = strdup(ctx->group_editions[g] ? ctx->group_editions[g] : "");
    if (!new_edition) return QZDB_ERR_OUT_OF_MEMORY;

    int* new_flags = calloc((size_t)nf, sizeof(int));
    char** new_norms = calloc((size_t)nf, sizeof(char*));
    qzdb_norm_map_t new_map;
    memset(&new_map, 0, sizeof(new_map));
    if (!new_flags || !new_norms) {
        free(new_edition);
        free(new_flags);
        free(new_norms);
        return QZDB_ERR_OUT_OF_MEMORY;
    }

    for (int i = 0; i < nf; i++) {
        const char* fn = ctx->group_field_names[g][i] ? ctx->group_field_names[g][i] : "";
        if (strcmp(fn, "longitude") == 0 || strcmp(fn, "latitude") == 0)
            new_flags[i] = 1;
        size_t fn_len = strlen(fn);
        char* n = (char*)malloc(fn_len + 1);
        if (!n) {
            free(new_edition);
            free(new_flags);
            for (int k = 0; k < i; k++) free(new_norms[k]);
            free(new_norms);
            return QZDB_ERR_OUT_OF_MEMORY;
        }
        size_t j = 0;
        for (size_t k = 0; k < fn_len; k++) {
            char c = fn[k];
            if (c == '_' || c == '-') continue;
            if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
            n[j++] = c;
        }
        n[j] = '\0';
        new_norms[i] = n;
    }

    if (norm_map_build_values((const char* const*)new_norms, nf, &new_map) != QZDB_OK) {
        free(new_edition);
        free(new_flags);
        for (int i = 0; i < nf; i++) free(new_norms[i]);
        free(new_norms);
        return QZDB_ERR_OUT_OF_MEMORY;
    }

    if (ctx->norm_field_names) {
        for (int i = 0; i < ctx->field_count; i++) free(ctx->norm_field_names[i]);
        free(ctx->norm_field_names);
    }
    norm_map_free(ctx);
    free(ctx->float_field_flags);
    free(ctx->edition);

    ctx->field_names = ctx->group_field_names[g];
    ctx->field_count = nf;
    ctx->edition = new_edition;
    ctx->edition_source = ctx->group_edition_sources[g];
    ctx->field_names_source = ctx->group_name_sources[g];
    ctx->float_field_flags = new_flags;
    ctx->norm_field_names = new_norms;
    ctx->norm_map = new_map;
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
 * UsageType (spec §6.4) — 18 official scenarios
 * ======================================================================== */
static const struct { const char* raw; const char* zh; const char* en; const char* desc; } usage_types[] = {
    {"AICrawler",  "AI 爬虫",     "AICrawler",  "AI 训练 / AI 搜索爬虫（GPTBot、ClaudeBot 等）"},
    {"Backbone",   "骨干网",      "Backbone",   "运营商骨干传输网 / 国际出口"},
    {"Broadband",  "宽带",        "Broadband",  "家庭/企业宽带接入（xDSL、光纤、Cable、拨号等）"},
    {"Business",   "企业",        "Business",   "企业专线 / 企业组网"},
    {"CDN",        "CDN",         "CDN",        "内容分发网络"},
    {"Cloud",      "云服务",      "Cloud",      "公有云 / 托管云（AWS、阿里云、Azure 等）"},
    {"DNS",        "DNS",         "DNS",        "DNS 基础设施 / Anycast DNS"},
    {"DataCenter", "数据中心",    "DataCenter", "IDC / 机房托管"},
    {"Education",  "教育网",      "Education",  "高校 / 科研网（CERNET 等）"},
    {"Finance",    "金融",        "Finance",    "银行 / 证券 / 保险等金融机构"},
    {"Government", "政府",        "Government", "政务 / 公共机构网络"},
    {"IoT",        "物联网",      "IoT",        "物联网设备接入网络"},
    {"Mobile",     "移动网络",    "Mobile",     "蜂窝移动网络（2G/3G/4G/5G）"},
    {"Reserved",   "保留地址",    "Reserved",   "保留 / 未分配地址"},
    {"Satellite",  "卫星互联网",  "Satellite",  "卫星 / 低轨星座接入（Starlink 等）"},
    {"Spider",     "爬虫",        "Spider",     "通用搜索引擎 / 通用网络爬虫"},
    {"Streaming",  "流媒体",      "Streaming",  "音视频 / 直播流媒体平台"},
    {"Unknown",    "",            "",           "无法判定用途"},
    {"VPN",        "VPN/代理",    "VPN",        "VPN / 代理 / 隐私网络出口"},
    {NULL, NULL, NULL, NULL}
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
    if (!raw || !*raw) return 0;
    for (int i = 0; usage_types[i].raw; i++)
        if (strcmp(raw, usage_types[i].raw) == 0) return 1;
    return 0;
}

const char* qzdb_usage_type_display_zh(const char* raw) {
    if (!raw || !*raw) return "";
    for (int i = 0; usage_types[i].raw; i++)
        if (strcmp(raw, usage_types[i].raw) == 0) return usage_types[i].zh;
    return "未知";
}

const char* qzdb_usage_type_display_en(const char* raw) {
    if (!raw || !*raw) return "";
    for (int i = 0; usage_types[i].raw; i++)
        if (strcmp(raw, usage_types[i].raw) == 0) return usage_types[i].en;
    return "Unknown";
}

const char* qzdb_usage_type_description(const char* raw) {
    if (!raw) return "";
    for (int i = 0; usage_types[i].raw; i++)
        if (strcmp(raw, usage_types[i].raw) == 0) return usage_types[i].desc;
    return "";
}

/* ========================================================================
 * Fast integer & float formatting — canonical cross-language (spec §10.5)
 * 整数值无小数点；非整数固定 6 位小数；NaN/Inf 为 ""。
 * 完全消除全局 locale 锁与 uselocale 线程上下文开销。
 * ======================================================================== */
static inline int format_u64_fast(uint64_t val, char* buf, size_t buf_size) {
    char tmp[24];
    int len = 0;
    if (val == 0) tmp[len++] = '0';
    else {
        while (val > 0) {
            tmp[len++] = (char)('0' + (val % 10));
            val /= 10;
        }
    }
    if ((size_t)len >= buf_size) return -1;
    for (int i = 0; i < len; i++) buf[i] = tmp[len - 1 - i];
    buf[len] = '\0';
    return len;
}

static inline int format_i64_fast(int64_t val, char* buf, size_t buf_size) {
    if (val < 0) {
        if (buf_size < 2) return -1;
        buf[0] = '-';
        uint64_t u = (uint64_t)(-(val + 1)) + 1;
        int n = format_u64_fast(u, buf + 1, buf_size - 1);
        if (n < 0) return -1;
        return n + 1;
    }
    return format_u64_fast((uint64_t)val, buf, buf_size);
}

static void format_float_value_locale(locale_t loc, double dv, char* buf, size_t buf_size) {
    (void)loc;
    if (buf_size == 0) return;
    if (isnan(dv) || isinf(dv)) {
        buf[0] = '\0';
        return;
    }
    if (dv == floor(dv)) {
        if (dv >= -9223372036854775808.0 && dv < 9223372036854775808.0) {
            format_i64_fast((int64_t)dv, buf, buf_size);
            return;
        }
    }
    /* 跨平台定点格式化：整数与 6 位小数拼接，不依赖 snprintf_l 与 locale 锁 */
    int neg = signbit(dv) ? 1 : 0;
    double abs_dv = neg ? -dv : dv;
    if (abs_dv < 1e12) {
        double scaled = abs_dv * 1e6;
        /* round-half-even（契约 §10.5）：round() 是半数远离零，tie 偶底会错进一位；
         * nearbyint 按当前舍入模式（默认 nearest-even）舍入，不抛 FE_INEXACT。 */
        uint64_t total = (uint64_t)nearbyint(scaled);
        uint64_t int_part = total / 1000000ULL;
        uint32_t frac_part = (uint32_t)(total % 1000000ULL);

        /* 预先计算完整格式化所需最小缓冲区容量，严禁因微小缓冲静默丢负号或部分截断 */
        size_t int_digits = (int_part == 0) ? 1 : 0;
        uint64_t tmp_i = int_part;
        while (tmp_i > 0) { int_digits++; tmp_i /= 10; }
        size_t needed = (neg ? 1 : 0) + int_digits + 1 + 6 + 1; /* 负号 + 整数 + 小数点 + 6位小数 + '\0' */
        if (buf_size < needed) {
            buf[0] = '\0';
            return;
        }

        size_t pos = 0;
        if (neg) {
            buf[pos++] = '-';
        }
        int n = format_u64_fast(int_part, buf + pos, buf_size - pos);
        if (n < 0) { buf[0] = '\0'; return; }
        pos += (size_t)n;
        buf[pos++] = '.';
        for (int i = 5; i >= 0; i--) {
            buf[pos + i] = (char)('0' + (frac_part % 10));
            frac_part /= 10;
        }
        pos += 6;
        buf[pos] = '\0';
    } else {
        /* 防御性兜底：QZDB 经纬度坐标范围为 [-180.0, 180.0]，业务值恒小于 1e12；
         * 若外部极端传入 >= 1e12 浮点数，使用 snprintf 作为兜底保障。
         * 整数值（>= 2^63 走不了 int64 快路）用 %.0f 输出整值，否则 %.6f
         * 会多出 ".000000"（FORMAT §10.5：整数值无小数点）。 */
        const char *fmt = (dv == floor(dv)) ? "%.0f" : "%.6f";
        snprintf(buf, buf_size, fmt, dv);
    }
}

static QZDB_UNUSED void format_float_value(double dv, char* buf, size_t buf_size) {
    format_float_value_locale((locale_t)0, dv, buf, buf_size);
}

static void format_float32_value_locale(locale_t loc, float fv, char* buf, size_t buf_size) {
    format_float_value_locale(loc, (double)fv, buf, buf_size);
}

static QZDB_UNUSED void format_float32_value(float fv, char* buf, size_t buf_size) {
    format_float32_value_locale((locale_t)0, fv, buf, buf_size);
}

/* ========================================================================
 * Forward declarations
 * ======================================================================== */
static int ensure_pools_loaded(qzdb_reader_t* ctx);
static int  read_ip_row(qzdb_reader_t* ctx, uint32_t row_id, uint32_t* geo_id,
                        uint32_t* asn_id, uint32_t* usage_id);
static int  get_geo_info(qzdb_reader_t* ctx, uint32_t entry_id, int group_index,
                         qzdb_geo_info_t* result);
static int  decode_geo_field_buf(qzdb_reader_t* ctx, uint32_t entry_id, int group_index,
                                 int field_index, char* buf, int buf_size, char** out_value);
static int  get_geo_info_buf(qzdb_reader_t* ctx, uint32_t entry_id, int group_index,
                             char** values, char (*bufs)[QZDB_VALUE_BUF_SIZE], int buf_size, int* out_count);
static void geo_cache_init(qzdb_reader_t* ctx);
static void geo_cache_free(qzdb_reader_t* ctx);
static char** geo_cache_lookup(qzdb_reader_t* ctx, int group, uint32_t entry_id, int* out_count, char** out_pipe);
static int  resolve_row_id_cached(qzdb_reader_t* ctx, uint32_t row_id, int group_index,
                                  qzdb_geo_info_t* result);
static void free_geo_info(qzdb_geo_info_t* info);
static void free_heap_state(qzdb_reader_t* ctx);
static inline void reader_pin(qzdb_reader_t* ctx);
static inline void reader_unpin(qzdb_reader_t* ctx);

#ifdef QZDB_TESTING
static int qzdb_test_projection_strdup_fail_at = -1;
static int qzdb_test_projection_strdup_calls;

void qzdb_test_projection_set_strdup_fail_at(int call) {
    qzdb_test_projection_strdup_fail_at = call;
    qzdb_test_projection_strdup_calls = 0;
}

static char* qzdb_projection_strdup(const char* s) {
    int call = qzdb_test_projection_strdup_calls++;
    if (qzdb_test_projection_strdup_fail_at == call) return NULL;
    size_t n = strlen(s) + 1;
    char* p = (char*)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static int qzdb_test_pool_alloc_fail_at = -1;
static int qzdb_test_pool_alloc_calls;

void qzdb_test_set_pool_alloc_fail_at(int call) {
    qzdb_test_pool_alloc_fail_at = call;
    qzdb_test_pool_alloc_calls = 0;
}

static int qzdb_test_pool_should_fail(void) {
    int call = qzdb_test_pool_alloc_calls++;
    return qzdb_test_pool_alloc_fail_at == call;
}

static void* qzdb_test_pool_malloc(size_t size) {
    if (qzdb_test_pool_should_fail()) return NULL;
    return malloc(size);
}

static void* qzdb_test_pool_calloc(size_t count, size_t size) {
    if (qzdb_test_pool_should_fail()) return NULL;
    return calloc(count, size);
}
#else
static char* qzdb_projection_strdup(const char* s) {
    return strdup(s);
}
static void* qzdb_test_pool_malloc(size_t size) { return malloc(size); }
static void* qzdb_test_pool_calloc(size_t count, size_t size) { return calloc(count, size); }
#endif

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
/* 热路径子节点读取：init 已验证节点段整体在界内（off + count*size <= data_size），
 * 入口的 node_idx < node_count 检查保证本次读取落在段内，因此直读基址指针，
 * 省去 safe_read_* 每步 2-3 条比较/分支。损坏链仍被 node_idx 检查 fail-closed。 */
static uint32_t get_v4_child(const qzdb_reader_t* ctx, uint32_t node_idx, uint32_t bit) {
    if (node_idx >= ctx->v4_node_count) return 0;
    uint32_t val;
    if (ctx->v4_node_24) {
        /* 24 位紧凑节点：left = [0,3)，right = [3,6) */
        const uint8_t* p = ctx->v4_nodes_base + (size_t)node_idx * 6 + (bit ? 3u : 0u);
        val = (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16);
        if (val & 0x800000u) return (val & 0x7FFFFFu) | QZDB_SENTINEL;
        return val;
    }
    /* 32 位节点：left = [0,4)，right = [4,8) */
    {
        const uint8_t* p = ctx->v4_nodes_base + (size_t)node_idx * 8 + (size_t)bit * 4;
        val = (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
        return val;
    }
}

static uint32_t get_v6_child(const qzdb_reader_t* ctx, uint32_t node_idx, uint32_t bit) {
    if (node_idx >= ctx->v6_node_count) return 0;
    uint32_t val;
    if (ctx->v6_node_24) {
        const uint8_t* p = ctx->v6_nodes_base + (size_t)node_idx * 6 + (bit ? 3u : 0u);
        val = (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16);
        if (val & 0x800000u) return (val & 0x7FFFFFu) | QZDB_SENTINEL;
        return val;
    }
    {
        const uint8_t* p = ctx->v6_nodes_base + (size_t)node_idx * 8 + (size_t)bit * 4;
        val = (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
        return val;
    }
}

/* 构建 IPv4 8-bit 步长二级查找表（加载期一次性构建，将 /17../24 路由压缩为 2 次扁平数组读取）。
 * 内存上界受非哨兵 /16 块数约束（demo 库约 209 KB，全球库最坏 <= 64 MB）。OOM 时自动回退原生逐位走查。 */
/* 构建 IPv4 8-bit 步长二级查找表（加载期一次性构建，将 /17../24 路由压缩为 2 次扁平数组读取）。
 * 防放大加固（H2）：
 *   1. 按非哨兵节点 ptr 去重：DAG 共享子树共用同一 L2 block，65536 槽指向同一节点仅占 1 KB；
 *   2. 严格内存预算上界：unique_blocks * 1 KB <= min(64 MB, max(1 MB, 4 * 文件大小))，超限即
 *      回退原生逐位走查，防止恶意小文件触发加载期 OOM/CPU 放大攻击。 */
static void build_v4_l2_table(qzdb_reader_t* ctx) {
    ctx->v4_l1 = NULL;
    ctx->v4_l2 = NULL;
    ctx->v4_l2_blocks = 0;
    if (!ctx->has_v4 || ctx->off_v4_jump == 0 || ctx->v4_node_count == 0) return;
    if (ctx->off_v4_jump > ctx->data_size || (uint64_t)65536 * 4 > ctx->data_size - ctx->off_v4_jump) return;

    const uint32_t HT_CAP = 131072u; /* 2^17 >= 2 * 65536，保证负载因子 <= 0.5 */
    const uint32_t HT_MASK = HT_CAP - 1u;
    typedef struct { uint32_t ptr; uint32_t blk_plus1; } l2_ht_entry_t;
    l2_ht_entry_t* ht = (l2_ht_entry_t*)calloc(HT_CAP, sizeof(l2_ht_entry_t));
    if (!ht) return;

    size_t max_blocks = (ctx->data_size / 256u); /* 4 * data_size / 1024 */
    if (max_blocks < 1024u) max_blocks = 1024u;
    if (max_blocks > 65536u) max_blocks = 65536u;

    const uint8_t* jump = ctx->data + ctx->off_v4_jump;
    uint32_t unique_blocks = 0;
    for (uint32_t hi = 0; hi < 65536u; hi++) {
        uint32_t ptr = READ_LE32(jump + (size_t)hi * 4);
        if (ptr == 0 || (ptr & QZDB_SENTINEL)) continue;
        uint32_t slot = ((ptr * 2654435761u) >> 15) & HT_MASK;
        for (;;) {
            if (ht[slot].ptr == 0) {
                unique_blocks++;
                if (unique_blocks > (uint32_t)max_blocks) { free(ht); return; }
                ht[slot].ptr = ptr;
                ht[slot].blk_plus1 = unique_blocks; /* 1-based block id */
                break;
            }
            if (ht[slot].ptr == ptr) break;
            slot = (slot + 1u) & HT_MASK;
        }
    }

    uint32_t* l1 = (uint32_t*)malloc(65536u * sizeof(uint32_t));
    if (!l1) { free(ht); return; }
    uint32_t* l2 = NULL;
    uint8_t* expanded = NULL;
    if (unique_blocks > 0) {
        l2 = (uint32_t*)malloc((size_t)unique_blocks * 256u * sizeof(uint32_t));
        expanded = (uint8_t*)calloc(unique_blocks, 1);
        if (!l2 || !expanded) { free(expanded); free(l2); free(l1); free(ht); return; }
    }
    for (uint32_t hi = 0; hi < 65536u; hi++) {
        uint32_t ptr = READ_LE32(jump + (size_t)hi * 4);
        if (ptr == 0 || (ptr & QZDB_SENTINEL)) {
            l1[hi] = ptr;
            continue;
        }
        uint32_t slot = ((ptr * 2654435761u) >> 15) & HT_MASK;
        while (ht[slot].ptr != ptr) slot = (slot + 1u) & HT_MASK;
        uint32_t blk_plus1 = ht[slot].blk_plus1;
        l1[hi] = blk_plus1; /* 非 0 且不含 QZDB_SENTINEL */
        uint32_t blk = blk_plus1 - 1u;
        if (expanded[blk]) continue; /* DAG 共享节点：直接复用已展开块 */
        expanded[blk] = 1;
        uint32_t* dst = l2 + ((size_t)blk << 8);
        for (uint32_t b = 0; b < 256u; b++) {
            uint32_t idx = ptr;
            uint32_t out = 0;
            for (int k = 7; k >= 0; k--) {
                uint32_t child = get_v4_child(ctx, idx, (b >> k) & 1u);
                if (child == 0) { out = 0; break; }
                if (child & QZDB_SENTINEL) { out = child; break; }
                idx = child;
                if (k == 0) out = idx;
            }
            dst[b] = out;
        }
    }
    free(expanded);
    free(ht);
    ctx->v4_l1 = l1;
    ctx->v4_l2 = l2;
    ctx->v4_l2_blocks = unique_blocks;
}

static uint32_t trie_walk_v4(const qzdb_reader_t* ctx, uint32_t ip_int) {
    if (ctx->v4_l1) {
        uint32_t e1 = ctx->v4_l1[(ip_int >> 16) & 0xFFFFu];
        if (e1 == 0) return 0;
        if (e1 & QZDB_SENTINEL) return e1 & QZDB_SENTINEL_MASK_31;
        uint32_t e2 = ctx->v4_l2[((size_t)(e1 - 1u) << 8) | ((ip_int >> 8) & 0xFFu)];
        if (e2 == 0) return 0;
        if (e2 & QZDB_SENTINEL) return e2 & QZDB_SENTINEL_MASK_31;
        uint32_t idx = e2;
        uint32_t lo8 = ip_int & 0xFFu;
        for (int k = 7; k >= 0; k--) {
            uint32_t child = get_v4_child(ctx, idx, (lo8 >> k) & 1u);
            if (QZDB_UNLIKELY(child == 0)) return 0;
            if (child & QZDB_SENTINEL) return child & QZDB_SENTINEL_MASK_31;
            idx = child;
        }
        return 0;
    }
    uint32_t hi16 = (ip_int >> 16) & 0xFFFF;
    uint32_t ptr;
    if (safe_read_u32(ctx->data, ctx->data_size, ctx->off_v4_jump + hi16 * 4, &ptr) != QZDB_OK) return 0;
    if (ptr == 0) return 0;
    if (ptr & QZDB_SENTINEL) return ptr & QZDB_SENTINEL_MASK_31;
    uint32_t idx = ptr;
    uint32_t suffix = (ip_int & 0xFFFF) << 16;
    uint32_t steps = 0;
    while (1) {
        if (QZDB_UNLIKELY(++steps >= QZDB_MAX_TRIE_WALK_STEPS_V4)) return 0;
        uint32_t bit = (suffix >> 31) & 1;
        uint32_t child = get_v4_child(ctx, idx, bit);
        if (QZDB_UNLIKELY(child == 0)) return 0;
        if (child & QZDB_SENTINEL) return child & QZDB_SENTINEL_MASK_31;
        idx = child;
        suffix <<= 1;
    }
}

static uint32_t trie_walk_v6(const qzdb_reader_t* ctx, const uint8_t* ip_bin) {
    int v6_jump_bits = ctx->v6_jump_bits;
    uint32_t idx_jump = 0;
    if (v6_jump_bits <= 32 && v6_jump_bits > 0) {
        uint32_t hi32 = ((uint32_t)ip_bin[0] << 24) | ((uint32_t)ip_bin[1] << 16) |
                        ((uint32_t)ip_bin[2] << 8)  | (uint32_t)ip_bin[3];
        idx_jump = hi32 >> (32 - v6_jump_bits);
    } else {
        int bits_collected = 0;
        for (int i = 0; i < 16; i++) {
            uint8_t b = ip_bin[i];
            int bits_left = v6_jump_bits - bits_collected;
            if (bits_left <= 0) break;
            if (bits_left >= 8) { idx_jump = (idx_jump << 8) | b; bits_collected += 8; }
            else { idx_jump = (idx_jump << bits_left) | (b >> (8 - bits_left)); bits_collected += bits_left; break; }
        }
    }
    uint32_t ptr;
    if (safe_read_u32(ctx->data, ctx->data_size, ctx->off_v6_jump + idx_jump * 4, &ptr) != QZDB_OK) return 0;
    if (ptr == 0) return 0;
    if (ptr & QZDB_SENTINEL) return ptr & QZDB_SENTINEL_MASK_31;
    uint32_t idx = ptr;
    int depth = v6_jump_bits;
    int steps = 0;
    while (depth < 128) {
        if (QZDB_UNLIKELY(++steps >= QZDB_MAX_TRIE_WALK_STEPS_V6)) return 0;
        if (QZDB_UNLIKELY(idx >= ctx->v6_node_count)) return 0;
        int byte_idx = depth / 8;
        int bit_idx = 7 - (depth % 8);
        uint32_t bit = (ip_bin[byte_idx] >> bit_idx) & 1;
        uint32_t child = get_v6_child(ctx, idx, bit);
        if (QZDB_UNLIKELY(child == 0)) return 0;
        if (child & QZDB_SENTINEL) return child & QZDB_SENTINEL_MASK_31;
        idx = child;
        depth++;
    }
    return 0;
}

/* ========================================================================
 * GeoInfo decode cache (per-snapshot bounded)
 * ======================================================================== */
/* Bounded probe window. Keeping it small caps the worst-case read cost at a
 * handful of acquire loads; keys that don't land within it are simply reported
 * as a miss and served by the caller-owned decode path. */
#define QZDB_CACHE_PROBE 4
#ifndef QZDB_GEO_CACHE_CAP
#define QZDB_GEO_CACHE_CAP 16384u
#endif

#if (QZDB_GEO_CACHE_CAP & (QZDB_GEO_CACHE_CAP - 1)) != 0
#error "QZDB_GEO_CACHE_CAP must be a power of two!"
#endif

static void geo_cache_init(qzdb_reader_t* ctx) {
    ctx->geo_cache_cap = QZDB_GEO_CACHE_CAP;   /* power of two; compile-time configurable */
    ctx->geo_cache = calloc(ctx->geo_cache_cap, sizeof(qzdb_cache_entry_t*));
    if (!ctx->geo_cache) ctx->geo_cache_cap = 0;   /* degrade to no-cache, never NULL-deref */
}

static void geo_cache_entry_destroy(qzdb_cache_entry_t* e) {
    /* 单块分配（values/pipe 均指向块内）：单次 free 整体回收。 */
    free(e);
}

/* Only called from qzdb_free(), i.e. after all queries have quiesced. */
static void geo_cache_free(qzdb_reader_t* ctx) {
    if (!ctx->geo_cache) return;
    for (uint32_t i = 0; i < ctx->geo_cache_cap; i++)
        geo_cache_entry_destroy(ctx->geo_cache[i]);
    free(ctx->geo_cache);
    ctx->geo_cache = NULL;
    ctx->geo_cache_cap = 0;
}

/* Decode (group, entry_id) into a freshly built single-block cache entry.
 * 单块连续分配：entry 结构 + values[] 指针数组 + 字符串区 + pipe 区同块内存。
 * 旧实现每条目 ~cnt+4 次 malloc/strdup/realloc（指针数组 + 逐字段 strdup +
 * entry + pipe 两次分配），填充期 allocator 开销与堆碎片随条目数放大；现在
 * 字符串只解码一次，各段先记栈上偏移、末尾统一落指针，realloc 移动 base
 * 无需修复指针，初始 1024B arena 覆盖常规 6~25 字段条目（0 次扩容 realloc），
 * 仅超长条目按倍增扩容，末尾收紧到实际大小，destroy 单次 free。
 * pipe 始终建入块内（OOM 则整块放弃报 miss，回退无缓存路径，fail-closed）。
 * 语义不变：构建到完整才返回，不做缓存交互。 */
static qzdb_cache_entry_t* geo_cache_build(qzdb_reader_t* ctx, int group,
                                           uint32_t entry_id, uint64_t key, int* out_count) {
    *out_count = 0;
    if (!ctx || group < 0 || group >= ctx->actual_groups || !ctx->group_field_counts) return NULL;
    int cnt = ctx->group_field_counts[group];
    if (cnt <= 0 || cnt > QZDB_MAX_FIELDS) return NULL;

    size_t hdr = sizeof(qzdb_cache_entry_t) + (size_t)cnt * sizeof(char*);
    size_t acap = 1024;                    /* arena 初始估算：典型条目 ~300B */
    char* base = malloc(hdr + acap);
    if (!base) return NULL;
    uint32_t offs[QZDB_MAX_FIELDS];        /* 各字段串在 arena 内偏移（栈上，realloc 不受影响） */
    char buf[QZDB_VALUE_BUF_SIZE];
    size_t aused = 0, pipe_need = (size_t)(cnt - 1) + 1;   /* 分隔符 + NUL */
    for (int i = 0; i < cnt; i++) {
        char* val = NULL;
        if (decode_geo_field_buf(ctx, entry_id, group, i, buf, QZDB_VALUE_BUF_SIZE, &val) != QZDB_OK) {
            free(base);
            return NULL;
        }
        size_t l = strlen(val ? val : "") + 1;
        pipe_need += l - 1;
        if (aused + l > acap) {
            while (aused + l > acap) acap <<= 1;
            char* grown = realloc(base, hdr + acap);
            if (!grown) { free(base); return NULL; }
            base = grown;
        }
        offs[i] = (uint32_t)aused;
        memcpy(base + hdr + aused, val ? val : "", l);
        aused += l;
    }
    if (aused + pipe_need > acap) {
        char* grown = realloc(base, hdr + aused + pipe_need);
        if (!grown) { free(base); return NULL; }
        base = grown;
    }
    size_t pipe_off = aused, pos = aused;
    for (int i = 0; i < cnt; i++) {       /* to_pipe：字段串已就位，非重叠拷贝 */
        if (i > 0) base[hdr + pos++] = '|';
        size_t l = strlen(base + hdr + offs[i]);
        memcpy(base + hdr + pos, base + hdr + offs[i], l);
        pos += l;
    }
    base[hdr + pos] = '\0';

    /* 收紧到实际大小（失败无妨，保留略大块）；随后统一落指针。 */
    char* tight = realloc(base, hdr + aused + pipe_need);
    if (tight) base = tight;
    qzdb_cache_entry_t* e = (qzdb_cache_entry_t*)base;
    e->key = key;
    e->count = cnt;
    e->values = (char**)(base + sizeof(qzdb_cache_entry_t));
    for (int i = 0; i < cnt; i++) e->values[i] = base + hdr + offs[i];
    e->pipe = base + hdr + pipe_off;
    *out_count = cnt;
    return e;
}

/*
 * Lock-free, fill-only decode cache.
 *
 * Read path  : one __ATOMIC_ACQUIRE load per probed slot, no mutex at all.
 *              Multiple threads scale linearly; this is what the README's
 *              "lock-free concurrent queries" line for C is supposed to mean.
 * Write path : build the entry to completion, then publish it with a single
 *              release CAS. A reader therefore never observes a half-built
 *              entry — it sees either NULL or a fully populated, immutable one.
 * Eviction   : none, ever. See the LIFETIME CONTRACT note in qzdb_reader.h.
 *              Losing a publish race or exhausting the probe window returns
 *              NULL, and resolve_row_id_cached() falls back to get_geo_info(),
 *              which produces caller-owned strings with values_mask set.
 *
 * Returns borrowed pointers (valid until qzdb_free) on hit, NULL on miss.
 */
static char** geo_cache_lookup(qzdb_reader_t* ctx, int group, uint32_t entry_id, int* out_count, char** out_pipe) {
    *out_count = 0;
    *out_pipe = NULL;
    if (!ctx->geo_cache || ctx->geo_cache_cap == 0) return NULL;

    uint64_t key = ((uint64_t)group << 40) | (uint64_t)entry_id;
    uint32_t cap = ctx->geo_cache_cap;
    uint32_t mask = cap - 1u;
    /* Fibonacci 乘法哈希取高位（M1）：高位混合充分，避免低位聚集冲突。 */
    uint32_t h = (uint32_t)(key ^ (key >> 32)) * 2654435761u;
#if defined(__GNUC__) || defined(__clang__)
    uint32_t shift = 32u - (uint32_t)__builtin_ctz(cap);
#else
    uint32_t shift = 32u;
    for (uint32_t c = cap; c > 1u; c >>= 1) shift--;
#endif
    uint32_t base_idx = (shift < 32u) ? (h >> shift) : 0u;

    uint32_t free_idx = UINT32_MAX;
    for (uint32_t i = 0; i < QZDB_CACHE_PROBE; i++) {
        uint32_t idx = (base_idx + i) & mask;
        qzdb_cache_entry_t* e = __atomic_load_n(&ctx->geo_cache[idx], __ATOMIC_ACQUIRE);
        if (!e) {
            if (free_idx == UINT32_MAX) free_idx = idx;
            continue;
        }
        if (e->key == key) { *out_count = e->count; *out_pipe = e->pipe; return e->values; }
    }
    if (free_idx == UINT32_MAX) return NULL;   /* window full — never evict */

    int cnt = 0;
    qzdb_cache_entry_t* e = geo_cache_build(ctx, group, entry_id, key, &cnt);
    if (!e) return NULL;

    qzdb_cache_entry_t* expected = NULL;
    if (__atomic_compare_exchange_n(&ctx->geo_cache[free_idx], &expected, e,
                                    0 /* strong */, __ATOMIC_RELEASE, __ATOMIC_ACQUIRE)) {
        *out_count = cnt;
        *out_pipe = e->pipe;
        return e->values;
    }
    /* Lost the race: `expected` is now the winner's entry. Ours was never
     * reachable by any other thread, so destroying it here is safe. */
    geo_cache_entry_destroy(e);
    if (expected && expected->key == key) { *out_count = expected->count; *out_pipe = expected->pipe; return expected->values; }
    return NULL;
}

/* ========================================================================
 * GeoInfo decode
 * ======================================================================== */
static void geo_info_clear(qzdb_geo_info_t* info) {
    if (!info) return;
    memset(info, 0, sizeof(*info));
    for (int i = 0; i < QZDB_MAX_FIELDS; i++) {
        info->values[i] = "";
        info->field_indices[i] = -1;
    }
}

static void geo_info_set_full(qzdb_geo_info_t* info, int count) {
    geo_info_clear(info);
    if (count < 0) count = 0;
    if (count > QZDB_MAX_FIELDS) count = QZDB_MAX_FIELDS;
    info->value_count = count;
    for (int i = 0; i < count; i++) info->field_indices[i] = (int8_t)i;
}

static void geo_info_set_projection(qzdb_geo_info_t* info, int count,
                                    const int* indices) {
    geo_info_clear(info);
    if (count < 0) count = 0;
    if (count > QZDB_MAX_FIELDS) count = QZDB_MAX_FIELDS;
    info->value_count = count;
    info->projection_mode = 1;
    for (int i = 0; i < count; i++)
        info->field_indices[i] = (indices && indices[i] >= 0 && indices[i] < QZDB_MAX_FIELDS)
                               ? (int8_t)indices[i] : -1;
}

static int valid_caller_buf_size(int buf_size) {
    return buf_size > 0 && buf_size <= QZDB_VALUE_BUF_SIZE;
}

static const char* pool_value_at(qzdb_reader_t* ctx, int group_index, int field_index, uint32_t value_index) {
    if (!ctx->group_pools || !ctx->group_pool_counts || !ctx->group_pools[group_index] ||
        !ctx->group_pool_counts[group_index] || !ctx->group_pools[group_index][field_index] ||
        value_index >= (uint32_t)ctx->group_pool_counts[group_index][field_index]) return "";
    const char* value = ctx->group_pools[group_index][field_index][value_index];
    return value ? value : "";
}

static int decode_geo_field_buf(qzdb_reader_t* ctx, uint32_t entry_id, int group_index,
                                 int field_index, char* buf, int buf_size, char** out_value) {
    if (!ctx || !out_value || !valid_caller_buf_size(buf_size)) return QZDB_ERR_INVALID_PARAM;
    if (group_index < 0 || group_index >= ctx->actual_groups || !ctx->group_field_counts ||
        !ctx->group_entry_counts || !ctx->group_entry_offsets || !ctx->group_strides ||
        !ctx->group_field_widths || !ctx->group_field_offsets || !ctx->group_field_native ||
        !ctx->group_field_native_type) return QZDB_ERR_INVALID_PARAM;
    int field_count = ctx->group_field_counts[group_index];
    if (field_count <= 0 || field_count > QZDB_MAX_FIELDS || field_index < 0 || field_index >= field_count)
        return QZDB_ERR_CORRUPTED;
    if (entry_id >= ctx->group_entry_counts[group_index]) return QZDB_ERR_INVALID_PARAM;
    int stride = ctx->group_strides[group_index];
    if (stride <= 0) return QZDB_ERR_CORRUPTED;
    uint64_t group_offset = ctx->group_entry_offsets[group_index];
    if (ctx->off_geo_entries > UINT64_MAX - group_offset) return QZDB_ERR_BOUNDS;
    uint64_t group_entry_start = ctx->off_geo_entries + group_offset;
    if (group_entry_start > (uint64_t)ctx->data_size ||
        (uint64_t)entry_id > ((uint64_t)ctx->data_size - group_entry_start) / (uint64_t)stride)
        return QZDB_ERR_BOUNDS;
    uint64_t entry_offset = group_entry_start + (uint64_t)entry_id * (uint64_t)stride;
    if ((uint64_t)stride > (uint64_t)ctx->data_size - entry_offset) return QZDB_ERR_BOUNDS;
    int* widths = ctx->group_field_widths[group_index];
    int* base_offsets = ctx->group_field_offsets[group_index];
    int* natives = ctx->group_field_native[group_index];
    int* nat_types = ctx->group_field_native_type[group_index];
    if (!widths || !base_offsets || !natives || !nat_types || base_offsets[field_index] < 0)
        return QZDB_ERR_CORRUPTED;
    uint64_t base_offset = (uint64_t)base_offsets[field_index];
    if (entry_offset > UINT64_MAX - base_offset) return QZDB_ERR_BOUNDS;
    uint64_t field_offset = entry_offset + base_offset;
    int width = widths[field_index];
    if (width < 1 || width > 8) return QZDB_ERR_CORRUPTED;
    if (natives[field_index]) {
        int type = nat_types[field_index];
        if (type == 1) {
            if (width == 4) {
                uint32_t bits;
                int rc = safe_read_u32(ctx->data, ctx->data_size, field_offset, &bits);
                if (rc != QZDB_OK) return rc;
                union { uint32_t u; float f; } value;
                value.u = bits;
                format_float32_value_locale((locale_t)ctx->numeric_locale, value.f, buf, (size_t)buf_size);
            } else if (width == 8) {
                uint64_t bits;
                int rc = safe_read_u64(ctx->data, ctx->data_size, field_offset, &bits);
                if (rc != QZDB_OK) return rc;
                union { uint64_t u; double d; } value;
                value.u = bits;
                format_float_value_locale((locale_t)ctx->numeric_locale, value.d, buf, (size_t)buf_size);
            } else {
                return QZDB_ERR_CORRUPTED;
            }
        } else {
            uint32_t value;
            int rc = safe_read_uint_width(ctx->data, ctx->data_size, field_offset, width, &value);
            if (rc != QZDB_OK) return rc;
            int n = format_u64_fast((uint64_t)value, buf, (size_t)buf_size);
            if (n < 0 || (size_t)n >= (size_t)buf_size) return QZDB_ERR_BOUNDS;
        }
        *out_value = buf;
        return QZDB_OK;
    }

    uint32_t value_index;
    int rc = safe_read_uint_width(ctx->data, ctx->data_size, field_offset, width, &value_index);
    if (rc != QZDB_OK) return rc;
    *out_value = (char*)pool_value_at(ctx, group_index, field_index, value_index);
    return QZDB_OK;
}

static int get_geo_info(qzdb_reader_t* ctx, uint32_t entry_id, int group_index, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    if (group_index < 0 || group_index >= ctx->actual_groups || !ctx->group_field_counts ||
        !ctx->group_entry_counts || !ctx->group_entry_offsets || !ctx->group_strides ||
        !ctx->group_field_widths || !ctx->group_field_offsets || !ctx->group_field_native ||
        !ctx->group_field_native_type) return QZDB_ERR_INVALID_PARAM;
    if (entry_id >= ctx->group_entry_counts[group_index]) return QZDB_ERR_INVALID_PARAM;
    int field_count = ctx->group_field_counts[group_index];
    if (field_count <= 0 || field_count > QZDB_MAX_FIELDS) return QZDB_ERR_CORRUPTED;
    int stride = ctx->group_strides[group_index];
    if (stride <= 0) return QZDB_ERR_CORRUPTED;
    uint64_t group_offset = ctx->group_entry_offsets[group_index];
    if (ctx->off_geo_entries > UINT64_MAX - group_offset) return QZDB_ERR_BOUNDS;
    uint64_t group_entry_start = ctx->off_geo_entries + group_offset;
    if (group_entry_start > (uint64_t)ctx->data_size ||
        (uint64_t)entry_id > ((uint64_t)ctx->data_size - group_entry_start) / (uint64_t)stride)
        return QZDB_ERR_BOUNDS;
    uint64_t entry_offset = group_entry_start + (uint64_t)entry_id * (uint64_t)stride;
    if ((uint64_t)stride > (uint64_t)ctx->data_size - entry_offset) return QZDB_ERR_BOUNDS;

    geo_info_set_full(result, field_count);
    int* widths = ctx->group_field_widths[group_index];
    int* base_offsets = ctx->group_field_offsets[group_index];
    int* natives = ctx->group_field_native[group_index];
    int* nat_types = ctx->group_field_native_type[group_index];
    for (int i = 0; i < field_count; i++) {
        if (!widths || !base_offsets || !natives || !nat_types || base_offsets[i] < 0 ||
            widths[i] < 1 || widths[i] > 8) {
            free_geo_info(result);
            return QZDB_ERR_CORRUPTED;
        }
        uint64_t base_offset = (uint64_t)base_offsets[i];
        if (entry_offset > UINT64_MAX - base_offset) {
            free_geo_info(result);
            return QZDB_ERR_BOUNDS;
        }
        uint64_t field_offset = entry_offset + base_offset;
        if (natives[i]) {
            char buf[QZDB_VALUE_BUF_SIZE];
            int type = nat_types[i];
            if (type == 1) {
                if (widths[i] == 4) {
                    uint32_t bits;
                    if (safe_read_u32(ctx->data, ctx->data_size, field_offset, &bits) != QZDB_OK) {
                        free_geo_info(result);
                        return QZDB_ERR_BOUNDS;
                    }
                    union { uint32_t u; float f; } value;
                    value.u = bits;
                    format_float32_value_locale((locale_t)ctx->numeric_locale, value.f, buf, sizeof(buf));
                } else if (widths[i] == 8) {
                    uint64_t bits;
                    if (safe_read_u64(ctx->data, ctx->data_size, field_offset, &bits) != QZDB_OK) {
                        free_geo_info(result);
                        return QZDB_ERR_BOUNDS;
                    }
                    union { uint64_t u; double d; } value;
                    value.u = bits;
                    format_float_value_locale((locale_t)ctx->numeric_locale, value.d, buf, sizeof(buf));
                } else {
                    free_geo_info(result);
                    return QZDB_ERR_CORRUPTED;
                }
            } else {
                uint32_t value;
                if (safe_read_uint_width(ctx->data, ctx->data_size, field_offset, widths[i], &value) != QZDB_OK) {
                    free_geo_info(result);
                    return QZDB_ERR_BOUNDS;
                }
                int n = format_u64_fast((uint64_t)value, buf, sizeof(buf));
                if (n < 0 || (size_t)n >= sizeof(buf)) {
                    free_geo_info(result);
                    return QZDB_ERR_BOUNDS;
                }
            }
            result->values[i] = strdup(buf);
            if (!result->values[i]) {
                free_geo_info(result);
                return QZDB_ERR_OUT_OF_MEMORY;
            }
            result->values_mask |= (1ULL << i);
        } else {
            uint32_t value_index;
            if (safe_read_uint_width(ctx->data, ctx->data_size, field_offset, widths[i], &value_index) != QZDB_OK) {
                free_geo_info(result);
                return QZDB_ERR_BOUNDS;
            }
            result->values[i] = (char*)pool_value_at(ctx, group_index, i, value_index);
        }
    }
    return QZDB_OK;
}

static int get_geo_info_buf(qzdb_reader_t* ctx, uint32_t entry_id, int group_index,
                             char** values, char (*bufs)[QZDB_VALUE_BUF_SIZE], int buf_size, int* out_count) {
    if (!ctx || !values || !bufs || !out_count) return QZDB_ERR_INVALID_PARAM;
    *out_count = 0;
    if (!valid_caller_buf_size(buf_size)) return QZDB_ERR_INVALID_PARAM;
    if (group_index < 0 || group_index >= ctx->actual_groups || !ctx->group_field_counts)
        return QZDB_ERR_INVALID_PARAM;
    int field_count = ctx->group_field_counts[group_index];
    if (field_count <= 0 || field_count > QZDB_MAX_FIELDS) return QZDB_ERR_CORRUPTED;
    for (int i = 0; i < field_count; i++) values[i] = "";
    for (int i = 0; i < field_count; i++) {
        int rc = decode_geo_field_buf(ctx, entry_id, group_index, i, bufs[i], buf_size, &values[i]);
        if (rc != QZDB_OK) {
            *out_count = 0;
            return rc;
        }
    }
    *out_count = field_count;
    return QZDB_OK;
}

static void free_geo_info(qzdb_geo_info_t* info) {
    if (!info) return;
    for (int i = 0; i < QZDB_MAX_FIELDS; i++) {
        if (info->values_mask & (1ULL << i)) free(info->values[i]);
        info->values[i] = NULL;
        info->field_indices[i] = -1;
    }
    info->values_mask = 0;
    info->value_count = 0;
    info->projection_mode = 0;
}

/* ========================================================================
 * IPRow reading (dynamic widths)
 * ======================================================================== */
static int read_ip_row(qzdb_reader_t* ctx, uint32_t row_id, uint32_t* geo_id, uint32_t* asn_id, uint32_t* usage_id) {
    if (!ctx || row_id == 0 || row_id >= (uint32_t)ctx->row_count) return QZDB_ERR_INVALID_PARAM;
    uint64_t off = ctx->off_ip_row + (uint64_t)row_id * ctx->ip_row_size;
    *geo_id = 0; *asn_id = 0; *usage_id = 0;
    if (ctx->off_row_schema > 0) {
        if (safe_read_uint_width(ctx->data, ctx->data_size, off + (uint64_t)ctx->row_geo_offset, ctx->row_geo_width, geo_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
        if (ctx->row_asn_width > 0) {
            if (safe_read_uint_width(ctx->data, ctx->data_size, off + (uint64_t)ctx->row_asn_offset, ctx->row_asn_width, asn_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
        }
        if (ctx->row_usage_width > 0) {
            if (safe_read_uint_width(ctx->data, ctx->data_size, off + (uint64_t)ctx->row_usage_offset, ctx->row_usage_width, usage_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
        }
    } else {
        if (safe_read_u24(ctx->data, ctx->data_size, off, geo_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
        if (safe_read_u24(ctx->data, ctx->data_size, off + 3, asn_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
        if (ctx->ip_row_size >= 9)
            if (safe_read_u24(ctx->data, ctx->data_size, off + 6, usage_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    }
    return QZDB_OK;
}

/* 按组 dimensionMask 从 IPRow 选出目标维度的 entry_id（热路径复用，消除 5 处重复）。 */
static inline int select_entry_id(qzdb_reader_t* ctx, uint32_t row_id, int group_index, uint32_t* out_entry_id) {
    uint32_t geo_id = 0, asn_id = 0, usage_id = 0;
    int err = read_ip_row(ctx, row_id, &geo_id, &asn_id, &usage_id);
    if (err != QZDB_OK) return err;
    uint16_t mask = (group_index >= 0 && group_index < ctx->actual_groups && ctx->group_dim_masks)
                  ? ctx->group_dim_masks[group_index] : 0;
    uint32_t entry_id = (mask & 0x02) ? asn_id : ((mask & 0x04) ? usage_id : geo_id);
    if (entry_id == 0) return QZDB_ERR_NOT_FOUND;
    *out_entry_id = entry_id;
    return QZDB_OK;
}

/* ========================================================================
 * Resolve row_id → GeoInfo (cache-backed)
 * ======================================================================== */
static int resolve_row_id_cached(qzdb_reader_t* ctx, uint32_t row_id, int group_index, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    uint32_t entry_id = 0;
    int err = select_entry_id(ctx, row_id, group_index, &entry_id);
    if (err != QZDB_OK) return err;
    int cnt = 0;
    char* pipe_unused = NULL;
    char** cached = geo_cache_lookup(ctx, group_index, entry_id, &cnt, &pipe_unused);
    if (cached) {
        geo_info_set_full(result, cnt);
        for (int i = 0; i < cnt && i < QZDB_MAX_FIELDS; i++) result->values[i] = cached[i];
        return QZDB_OK;
    }
    return get_geo_info(ctx, entry_id, group_index, result);
}

/* ========================================================================
 * Resolve row_id → caller-buffer (used by find_*_buf variants)
 * ======================================================================== */
static int resolve_row_id_buf(qzdb_reader_t* ctx, uint32_t row_id, int group_index,
                              char** values, char (*bufs)[QZDB_VALUE_BUF_SIZE], int buf_size, int* out_count) {
    if (!ctx || !values || !bufs || !out_count || !valid_caller_buf_size(buf_size))
        return QZDB_ERR_INVALID_PARAM;
    uint32_t entry_id = 0;
    int err = select_entry_id(ctx, row_id, group_index, &entry_id);
    if (err != QZDB_OK) return err;
    return get_geo_info_buf(ctx, entry_id, group_index, values, bufs, buf_size, out_count);
}

/* ========================================================================
 * Batch & streaming (spec §8)
 * ======================================================================== */
/* qzdb_find_batch 定义移至 qzdb_find 下方，复用单次 reader_pin 与分块预取快路径（N7）。 */

int qzdb_find_each(qzdb_reader_t* ctx, const char** ips, int count,
                    qzdb_find_callback cb, void* user_data) {
    if (!ctx || !ips || !cb || count <= 0) return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    for (int i = 0; i < count; i++) {
        qzdb_batch_result_t res;
        memset(&res.info, 0, sizeof(res.info));
        res.error_code = qzdb_find(ctx, ips[i], &res.info);
        cb(i, &res, user_data);
        free_geo_info(&res.info);  /* 释放缓存未命中时 get_geo_info 分配的堆字符串；
                                    * 缓存命中时 values_mask=0，此调用为空操作。 */
    }
    reader_unpin(ctx);
    return QZDB_OK;
}

/* ========================================================================
 * GeoInfo access
 * ======================================================================== */
const char* qzdb_geo_info_get(qzdb_reader_t* ctx, const qzdb_geo_info_t* info, const char* name) {
    if (!ctx || !info || !name) return "";
    reader_pin(ctx);
    int schema_index = field_index_normalized(ctx, name);
    const char* ret = "";
    if (info->projection_mode) {
        if (schema_index >= 0) {
            for (int i = 0; i < info->value_count && i < QZDB_MAX_FIELDS; i++) {
                if (info->field_indices[i] == schema_index) {
                    ret = info->values[i] ? info->values[i] : "";
                    break;
                }
            }
        }
        reader_unpin(ctx);
        return ret;
    }
    if (schema_index >= 0 && schema_index < QZDB_MAX_FIELDS &&
        !(info->value_count > 0 && schema_index >= info->value_count) &&
        !(info->value_count == 0 && schema_index >= ctx->field_count)) {
        ret = info->values[schema_index] ? info->values[schema_index] : "";
    }
    reader_unpin(ctx);
    return ret;
}

int qzdb_geo_info_to_pipe(qzdb_reader_t* ctx, const qzdb_geo_info_t* info, char* out, size_t out_size) {
    if (!ctx || !info || !out || out_size == 0) return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    if (ctx->group_index < 0 || ctx->group_index >= ctx->actual_groups ||
        !ctx->group_field_counts) {
        reader_unpin(ctx);
        return QZDB_ERR_INVALID_PARAM;
    }
    int fc = info->value_count;
    if (!info->projection_mode && fc == 0) fc = ctx->group_field_counts[ctx->group_index];
    if (fc < 0) fc = 0;
    if (fc > QZDB_MAX_FIELDS) fc = QZDB_MAX_FIELDS;
    size_t pos = 0;
    for (int i = 0; i < fc; i++) {
        if (i > 0 && pos < out_size - 1) out[pos++] = '|';
        const char* v = info->values[i] ? info->values[i] : "";
        size_t len = strlen(v);
        if (pos + len >= out_size) {
            if (out_size > pos) {
                memcpy(out + pos, v, out_size - pos - 1);
                pos = out_size - 1;
            }
            break;
        }
        memcpy(out + pos, v, len);
        pos += len;
    }
    out[pos] = '\0';
    reader_unpin(ctx);
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
/* Metadata TLV type=6（v2.4 权威；无条目 ""，FORMAT §8.2） */
const char* qzdb_get_scope(qzdb_reader_t* ctx) { return ctx && ctx->scope ? ctx->scope : ""; }
const char* qzdb_get_build_time(qzdb_reader_t* ctx) { return ctx && ctx->build_time_str ? ctx->build_time_str : ""; }
const char* qzdb_get_description(qzdb_reader_t* ctx) { return ctx && ctx->description ? ctx->description : ""; }

int qzdb_get_file_hash(qzdb_reader_t* ctx, char* out, size_t out_size) {
    if (!ctx || !out || out_size < 9) return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    if (!ctx->data || ctx->data_size < 20) { reader_unpin(ctx); return QZDB_ERR_CORRUPTED; }
    int valid = __atomic_load_n(&ctx->crc_valid, __ATOMIC_ACQUIRE);
    if (!valid) {
        uint32_t computed = crc32_compute_file(ctx->data, ctx->data_size);
        __atomic_store_n(&ctx->file_crc, computed, __ATOMIC_RELEASE);
        int expected = 0;
        (void)__atomic_compare_exchange_n(&ctx->crc_valid, &expected, 1, 0,
                                          __ATOMIC_RELEASE, __ATOMIC_ACQUIRE);
    }
    uint32_t value = __atomic_load_n(&ctx->file_crc, __ATOMIC_ACQUIRE);
    snprintf(out, out_size, "%08x", value);
    reader_unpin(ctx);
    return QZDB_OK;
}

const char** qzdb_get_field_names(qzdb_reader_t* ctx) { return ctx ? (const char**)ctx->field_names : NULL; }
int qzdb_get_field_count(qzdb_reader_t* ctx) {
    if (!ctx) return 0;
    reader_pin(ctx);
    int fc = ctx->field_count;
    reader_unpin(ctx);
    return fc;
}
int qzdb_has_field(qzdb_reader_t* ctx, const char* name) {
    if (!ctx) return 0;
    reader_pin(ctx);
    int ok = field_index_normalized(ctx, name) >= 0 ? 1 : 0;
    reader_unpin(ctx);
    return ok;
}
int qzdb_get_group_count(qzdb_reader_t* ctx) {
    if (!ctx) return 0;
    reader_pin(ctx);
    int g = ctx->actual_groups;
    reader_unpin(ctx);
    return g;
}
int qzdb_get_pool_count(qzdb_reader_t* ctx) {
    if (!ctx) return 0;
    reader_pin(ctx);
    int p = ctx->pool_count;
    reader_unpin(ctx);
    return p;
}

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
/* ASCII → 十六进制值；0xFF = 非十六进制字符（含 NUL 与全部非 ASCII 字节）。 */
static const uint8_t hex_val[256] = {
#define X16 0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF
    X16, X16, X16,
    0,1,2,3,4,5,6,7,8,9,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,            /* 0x30 '0'..'9' */
    0xFF,10,11,12,13,14,15,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF, /* 0x40 'A'..'F' */
    X16,
    0xFF,10,11,12,13,14,15,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF, /* 0x60 'a'..'f' */
    X16, X16, X16, X16, X16, X16, X16, X16, X16
#undef X16
};

/* Strict IPv4 parse — single pass over a NUL-terminated string (aligned with
 * Go fastParseIpv4 / Python _fast_parse_ipv4 accept set).
 * Exactly 4 decimal octets of 1-3 ASCII digits, value <= 255, no leading zeros,
 * '.' separators, nothing after the 4th octet. Every read stops at the first
 * non-digit / non-'.' byte, so the terminating NUL is never passed.
 * Returns 1 on success. */
static int fast_parse_ipv4(const char* s, uint32_t* out) {
    uint32_t ip = 0;
    for (int oct = 0; oct < 4; oct++) {
        uint32_t v = (uint32_t)((unsigned char)*s - '0');
        if (v > 9) return 0;
        s++;
        uint32_t d = (uint32_t)((unsigned char)*s - '0');
        if (d <= 9) {
            if (v == 0) return 0;               /* leading zero */
            v = v * 10 + d; s++;
            d = (uint32_t)((unsigned char)*s - '0');
            if (d <= 9) {
                v = v * 10 + d; s++;
                if (v > 255) return 0;
            }
        }
        ip = (ip << 8) | v;
        if (oct < 3) {
            if (*s != '.') return 0;
            s++;
        }
    }
    if (*s != '\0') return 0;
    *out = ip;
    return 1;
}

typedef struct { uint8_t v6[16]; uint32_t v4; int is_v4; } parse_result_t;

/* 单遍 IPv6 解析（取代此前 split → strncpy → 多轮扫描的实现；接受集逐条不变，
 * 由 ~8 万条随机/敌对语料与 Python _fast_parse_ip 及旧实现差分 0 差异背书）：
 *   - 总长 1..45，不允许空白、'%'（zone id）及任何非十六进制/':'/'.' 字符；
 *   - 每组 1..4 个十六进制位，至多一个 "::"，且 "::" 至少替代一组；
 *   - 嵌入 IPv4 只能是最后一组（其后到串尾必须是严格 IPv4），且不能在 "::" 左侧
 *     （`1.2.3.4::` 非法，与 Go netip 一致）。
 * ::ffff:a.b.c.d / ::ffff:xxxx:xxxx 映射地址归一为 IPv4。 */
static int fast_parse_ip(const char* s, parse_result_t* res) {
    if (!s) return 0;
    /* IPv4 极速热路径：严格单遍解析，任何带空白、符号、字母、冒号或越界的输入
     * 均返回 0 后落入下方 IPv6 路径（与 Go fastParseIp 同构）。 */
    {
        uint32_t v4;
        if (fast_parse_ipv4(s, &v4)) { res->v4 = v4; res->is_v4 = 1; return 1; }
    }
    size_t n = strlen(s);
    if (n == 0 || n > 45) return 0;

    uint8_t buf[16];
    int bp = 0;          /* bytes written */
    int dc = -1;         /* byte position of "::", -1 = none */
    size_t i = 0;
    int has_colon = 0;
    if (s[0] == ':') {
        if (s[1] != ':') return 0;          /* leading single ':' */
        dc = 0; i = 2; has_colon = 1;
    }
    while (i < n) {
        size_t start = i;
        uint32_t v = 0;
        int digits = 0;
        uint8_t h;
        while ((h = hex_val[(unsigned char)s[i]]) != 0xFF) {
            if (++digits > 4) return 0;
            v = (v << 4) | h;
            i++;
        }
        if (s[i] == '.') {
            /* 嵌入 IPv4：从本组起点到串尾必须是严格 IPv4，且之后不得再有内容 */
            uint32_t v4;
            if (bp > 12 || !fast_parse_ipv4(s + start, &v4)) return 0;
            buf[bp] = (uint8_t)(v4 >> 24); buf[bp + 1] = (uint8_t)(v4 >> 16);
            buf[bp + 2] = (uint8_t)(v4 >> 8); buf[bp + 3] = (uint8_t)v4;
            bp += 4;
            break;
        }
        if (digits == 0 || bp >= 16) return 0;
        buf[bp] = (uint8_t)(v >> 8); buf[bp + 1] = (uint8_t)v;
        bp += 2;
        if (i == n) break;
        if (s[i] != ':') return 0;          /* invalid character */
        has_colon = 1;
        i++;
        if (s[i] == ':') {
            if (dc >= 0) return 0;          /* second "::" */
            dc = bp; i++;
        } else if (i == n) {
            return 0;                       /* trailing single ':' */
        }
    }
    if (!has_colon) return 0;
    if (dc >= 0) {
        if (bp > 14) return 0;              /* "::" must stand for at least one group */
        int tail = bp - dc;
        memmove(buf + 16 - tail, buf + dc, (size_t)tail);
        memset(buf + dc, 0, (size_t)(16 - tail - dc));
    } else if (bp != 16) {
        return 0;
    }
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
    if (jb <= 32 && jb > 0) {
        uint32_t hi32 = ((uint32_t)ip[0] << 24) | ((uint32_t)ip[1] << 16) |
                        ((uint32_t)ip[2] << 8)  | (uint32_t)ip[3];
        pref = hi32 >> (32 - jb);
    } else {
        for (int i = 0; i < jb; i++) { int bit = (ip[i >> 3] >> (7 - (i & 7))) & 1; pref = (pref << 1) | (uint32_t)bit; }
    }
    uint32_t ptr;
    if (safe_read_u32(ctx->data, ctx->data_size, ctx->off_v6_jump + (uint64_t)pref * 4, &ptr) != QZDB_OK) return -1;
    if (ptr == 0) return -1;
    if (ptr & QZDB_SENTINEL) return v6_walk_depth(ctx, ip, 0, 0, jb);
    return v6_walk_depth(ctx, ip, ptr, jb, 128);
}

static inline char* write_u8_dec(char* p, uint32_t v) {
    if (v >= 100u) { *p++ = (char)('0' + v / 100u); v %= 100u; *p++ = (char)('0' + v / 10u); *p++ = (char)('0' + v % 10u); }
    else if (v >= 10u) { *p++ = (char)('0' + v / 10u); *p++ = (char)('0' + v % 10u); }
    else { *p++ = (char)('0' + v); }
    return p;
}

static inline char* write_u16_hex(char* p, uint32_t v) {
    static const char hd[] = "0123456789abcdef";
    if (v >= 0x1000u) { *p++ = hd[(v >> 12) & 15u]; *p++ = hd[(v >> 8) & 15u]; *p++ = hd[(v >> 4) & 15u]; *p++ = hd[v & 15u]; }
    else if (v >= 0x100u) { *p++ = hd[(v >> 8) & 15u]; *p++ = hd[(v >> 4) & 15u]; *p++ = hd[v & 15u]; }
    else if (v >= 0x10u) { *p++ = hd[(v >> 4) & 15u]; *p++ = hd[v & 15u]; }
    else { *p++ = hd[v & 15u]; }
    return p;
}

static void format_v4_cidr(uint32_t ip, int n, char* out, size_t sz) {
    if (sz == 0) return;
    /* C 中 `x << 32` 属未定义行为，故对 n==0 和 n==32 单独处理。手写十进制避免 snprintf 开销（N6）。 */
    uint32_t mask = (n <= 0) ? 0u : (n >= 32) ? 0xFFFFFFFFu : (0xFFFFFFFFu << (32 - n));
    uint32_t net = ip & mask;
    char tmp[24];
    char* p = tmp;
    p = write_u8_dec(p, (net >> 24) & 0xFFu); *p++ = '.';
    p = write_u8_dec(p, (net >> 16) & 0xFFu); *p++ = '.';
    p = write_u8_dec(p, (net >> 8) & 0xFFu);  *p++ = '.';
    p = write_u8_dec(p, net & 0xFFu);         *p++ = '/';
    p = write_u8_dec(p, (uint32_t)(n < 0 ? 0 : (n > 32 ? 32 : n)));
    size_t len = (size_t)(p - tmp);
    if (len >= sz) len = sz - 1;
    memcpy(out, tmp, len);
    out[len] = '\0';
}

static void format_v6_cidr(const uint8_t* ip, int n, char* out, size_t sz) {
    if (sz == 0) return;
    uint8_t net[16]; memcpy(net, ip, 16);
    if (n <= 0) {
        memset(net, 0, 16);
    } else if (n < 128) {
        int full = n >> 3;
        int rem = n & 7;
        if (rem) { net[full] &= (uint8_t)(0xFFu << (8 - rem)); full++; }
        if (full < 16) memset(net + full, 0, (size_t)(16 - full));
    }
    uint32_t g[8];
    for (int i = 0; i < 8; i++) g[i] = ((uint32_t)net[2*i] << 8) | (uint32_t)net[2*i+1];
    int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
    for (int i = 0; i < 8; i++) {
        if (g[i] == 0) { if (curStart < 0) { curStart = i; curLen = 1; } else curLen++; }
        else { if (curLen > bestLen) { bestStart = curStart; bestLen = curLen; } curStart = -1; curLen = 0; }
    }
    if (curLen > bestLen) { bestStart = curStart; bestLen = curLen; }
    char tmp[48];
    char* p = tmp;
    if (bestLen >= 2) {
        for (int i = 0; i < bestStart; i++) { if (i > 0) *p++ = ':'; p = write_u16_hex(p, g[i]); }
        *p++ = ':'; *p++ = ':';
        int first = 1;
        for (int i = bestStart + bestLen; i < 8; i++) { if (!first) *p++ = ':'; p = write_u16_hex(p, g[i]); first = 0; }
    } else {
        for (int i = 0; i < 8; i++) { if (i > 0) *p++ = ':'; p = write_u16_hex(p, g[i]); }
    }
    *p++ = '/';
    p = write_u8_dec(p, (uint32_t)(n < 0 ? 0 : (n > 128 ? 128 : n)));
    size_t len = (size_t)(p - tmp);
    if (len >= sz) len = sz - 1;
    memcpy(out, tmp, len);
    out[len] = '\0';
}

char* qzdb_lookup_cidr(qzdb_reader_t* ctx, const char* ip_str, char* out, size_t out_size) {
    if (!ctx || !ip_str || !out || out_size == 0) return NULL;
    parse_result_t res; if (!fast_parse_ip(ip_str, &res)) return NULL;
    if (res.is_v4) return qzdb_lookup_cidr_uint(ctx, res.v4, out, out_size);
    reader_pin(ctx);
    int n = lookup_v6_prefix_len(ctx, res.v6);
    reader_unpin(ctx);
    if (n < 0) return NULL;
    format_v6_cidr(res.v6, n, out, out_size); return out;
}

char* qzdb_lookup_cidr_uint(qzdb_reader_t* ctx, uint32_t ip_int, char* out, size_t out_size) {
    if (!ctx || !out || out_size == 0) return NULL;
    reader_pin(ctx);
    int n = lookup_v4_prefix_len(ctx, ip_int);
    reader_unpin(ctx);
    if (n < 0) return NULL;
    format_v4_cidr(ip_int, n, out, out_size); return out;
}

char* qzdb_lookup_cidr_bytes(qzdb_reader_t* ctx, const uint8_t* ip_bytes, int len, char* out, size_t out_size) {
    if (!ctx || !ip_bytes || !out || out_size == 0) return NULL;
    if (len == 16) {
        if (is_v4_mapped(ip_bytes)) return qzdb_lookup_cidr_uint(ctx, v4_from_mapped(ip_bytes), out, out_size);
        reader_pin(ctx);
        int n = lookup_v6_prefix_len(ctx, ip_bytes);
        reader_unpin(ctx);
        if (n < 0) return NULL;
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
    /* Per-chain storage for metadata arrays (replaces static locals to avoid
     * data races when multiple chains or threads call these concurrently). */
    const char*     editions[32];
    const char*     scopes[32];
    const char*     data_months[32];
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

/* Merge two GeoInfos by normalized field name (aligning dst_reader schema slots):
 * "first-registered wins" (default) or "last wins" (override). */
static void merge_geo_info(qzdb_reader_t* dst_reader, qzdb_geo_info_t* dst,
                           qzdb_reader_t* src_reader, const qzdb_geo_info_t* src, int override) {
    int dst_fc = dst_reader && dst_reader->field_count > 0 ? dst_reader->field_count : src->value_count;
    if (dst_fc > QZDB_MAX_FIELDS) dst_fc = QZDB_MAX_FIELDS;
    if (dst_fc > dst->value_count) dst->value_count = dst_fc;
    for (int i = 0; i < dst_fc; i++) {
        int src_idx = i;
        if (dst_reader && src_reader && dst_reader != src_reader && dst_reader->field_names && dst_reader->field_names[i]) {
            src_idx = field_index_normalized(src_reader, dst_reader->field_names[i]);
        }
        if (src_idx < 0 || src_idx >= QZDB_MAX_FIELDS) continue;
        const char* sv = src->values[src_idx];
        if (!sv || !sv[0]) continue;
        if (override) {
            if (dst->values_mask & (1ULL << i)) free(dst->values[i]);
            dst->values[i] = strdup(sv);
            if (dst->values[i]) dst->values_mask |= (1ULL << i);
            else dst->values_mask &= ~(1ULL << i);
        } else {
            if (!(dst->values_mask & (1ULL << i)) && (!dst->values[i] || !dst->values[i][0])) {
                dst->values[i] = strdup(sv);
                if (dst->values[i]) dst->values_mask |= (1ULL << i);
            }
        }
    }
}

typedef int (*qzdb_chain_lookup_fn)(qzdb_reader_t* ctx, const void* key, qzdb_geo_info_t* out);

static int chain_lookup_str(qzdb_reader_t* ctx, const void* key, qzdb_geo_info_t* out) {
    return qzdb_find(ctx, (const char*)key, out);
}
static int chain_lookup_uint(qzdb_reader_t* ctx, const void* key, qzdb_geo_info_t* out) {
    return qzdb_find_uint(ctx, *(const uint32_t*)key, out);
}
static int chain_lookup_bytes(qzdb_reader_t* ctx, const void* key, qzdb_geo_info_t* out) {
    return qzdb_find_bytes(ctx, (const uint8_t*)key, out);
}

static int chain_find_common(qzdb_chain_t* chain, const void* key,
                             qzdb_chain_lookup_fn lookup, qzdb_geo_info_t* out) {
    memset(out, 0, sizeof(*out));
    if (chain->mode == QZDB_CHAIN_FALLBACK) {
        for (int i = 0; i < chain->count; i++) {
            int rc = lookup(chain->readers[i], key, out);
            if (rc == QZDB_OK) return QZDB_OK;       /* found */
            if (rc != QZDB_ERR_NOT_FOUND) return rc;  /* format error -> immediate stop (spec §9.1) */
            memset(out, 0, sizeof(*out));             /* not found -> try next */
        }
        return QZDB_ERR_NOT_FOUND;
    }
    /* Merge modes */
    int any_found = 0;
    int override = (chain->mode == QZDB_CHAIN_MERGE_OVERRIDE);
    for (int i = 0; i < chain->count; i++) {
        qzdb_geo_info_t tmp; memset(&tmp, 0, sizeof(tmp));
        int rc = lookup(chain->readers[i], key, &tmp);
        if (rc == QZDB_OK) {
            any_found = 1;
            merge_geo_info(chain->readers[0], out, chain->readers[i], &tmp, override);
            free_geo_info(&tmp);
        } else {
            free_geo_info(&tmp);
            if (rc != QZDB_ERR_NOT_FOUND) {
                free_geo_info(out);
                return rc;
            }
        }
    }
    return any_found ? QZDB_OK : QZDB_ERR_NOT_FOUND;
}

int qzdb_chain_find(qzdb_chain_t* chain, const char* ip, qzdb_geo_info_t* out) {
    if (!chain || !ip || !out) return QZDB_ERR_INVALID_PARAM;
    return chain_find_common(chain, ip, chain_lookup_str, out);
}

int qzdb_chain_find_uint(qzdb_chain_t* chain, uint32_t ip, qzdb_geo_info_t* out) {
    if (!chain || !out) return QZDB_ERR_INVALID_PARAM;
    return chain_find_common(chain, &ip, chain_lookup_uint, out);
}

int qzdb_chain_find_bytes(qzdb_chain_t* chain, const uint8_t ip16[16], qzdb_geo_info_t* out) {
    if (!chain || !ip16 || !out) return QZDB_ERR_INVALID_PARAM;
    return chain_find_common(chain, ip16, chain_lookup_bytes, out);
}

/* 前提：链上各 reader 应使用相同组 schema；merge 结果按 schema 槽位合并，
 * to_pipe 用 readers[0] 的字段数。若各 reader 的组字段布局不同，输出的字段
 * 位置/数量以 readers[0] 为准（与 merge_geo_info 的槽位语义一致）。 */
int qzdb_chain_find_str(qzdb_chain_t* chain, const char* ip, char* buf, size_t size) {
    if (!chain || !ip || !buf || size == 0) return QZDB_ERR_INVALID_PARAM;
    buf[0] = '\0';
    qzdb_geo_info_t info; memset(&info, 0, sizeof(info));
    int rc = qzdb_chain_find(chain, ip, &info);
    if (rc != QZDB_OK) {
        free_geo_info(&info);
        return rc;
    }
    rc = qzdb_geo_info_to_pipe(chain->readers[0], &info, buf, size);
    free_geo_info(&info);
    return rc;
}

int qzdb_chain_find_batch(qzdb_chain_t* chain, const char** ips, int count, qzdb_batch_result_t* results) {
    if (!chain || !ips || !results || count <= 0) return QZDB_ERR_INVALID_PARAM;
    for (int i = 0; i < count; i++) {
        memset(&results[i].info, 0, sizeof(results[i].info));
        results[i].error_code = qzdb_chain_find(chain, ips[i], &results[i].info);
    }
    return QZDB_OK;
}

const char** qzdb_chain_editions(qzdb_chain_t* chain, int* count) {
    if (!chain || !count || chain->count > 32) { if (count) *count = 0; return NULL; }
    for (int i = 0; i < chain->count; i++) chain->editions[i] = qzdb_get_edition(chain->readers[i]);
    *count = chain->count; return chain->editions;
}

const char** qzdb_chain_scopes(qzdb_chain_t* chain, int* count) {
    if (!chain || !count || chain->count > 32) { if (count) *count = 0; return NULL; }
    for (int i = 0; i < chain->count; i++) chain->scopes[i] = qzdb_get_scope(chain->readers[i]);
    *count = chain->count; return chain->scopes;
}

const char** qzdb_chain_data_months(qzdb_chain_t* chain, int* count) {
    if (!chain || !count || chain->count > 32) { if (count) *count = 0; return NULL; }
    for (int i = 0; i < chain->count; i++) chain->data_months[i] = qzdb_get_data_month(chain->readers[i]);
    *count = chain->count; return chain->data_months;
}

void qzdb_chain_free(qzdb_chain_t* chain) {
    if (!chain) return;
    free(chain->readers);   /* does not own the readers themselves (spec §9.4) */
    free(chain);
}

/* ========================================================================
 * Registry (spec §3.2) — thread-safe name→reader map with refcounted leases
 * ======================================================================== */
#define REG_INIT_BUCKETS 16

typedef struct reg_reader_slot {
    qzdb_reader_t reader;          /* 必须为首字段：&slot->reader == (qzdb_reader_t*)slot */
    int32_t       refcount;        /* 原子引用计数，初始 1（由 registry 持有） */
    uint8_t       legacy_borrowed; /* 若曾通过 qzdb_registry_get() 借出则置 1，延后至 registry_free 回收 */
} reg_reader_slot_t;

typedef struct reg_entry {
    char*              name;
    reg_reader_slot_t* slot;
    struct reg_entry*  next;
} reg_entry_t;

typedef struct reg_retired_node {
    reg_reader_slot_t*       slot;
    struct reg_retired_node* next;
} reg_retired_node_t;

struct qzdb_registry {
    reg_entry_t**       buckets;
    uint32_t            cap;
    uint32_t            count;
    pthread_mutex_t     lock;
    /* 仅存放曾被旧版 qzdb_registry_get() 借出的退休快照；
     * 纯 acquire/release 路径的旧快照在租约归零时立即回收，不进此链表。 */
    reg_retired_node_t* retired_head;
};

static void reg_slot_release(reg_reader_slot_t* slot) {
    if (!slot) return;
    if (__atomic_sub_fetch(&slot->refcount, 1, __ATOMIC_ACQ_REL) == 0) {
        qzdb_free(&slot->reader);
        free(slot);
    }
}

/* 把被替换/移除的旧快照按双轨策略处理：
 *   - 若曾通过 qzdb_registry_get() 借出，挂入 retired_head 延后至 qzdb_registry_free() 释放；
 *   - 否则返回给调用方在锁外执行 reg_slot_release()，租约归零立即 munmap/free。 */
static reg_reader_slot_t* reg_retire_locked(qzdb_registry_t* reg, reg_reader_slot_t* old) {
    if (!old) return NULL;
    if (!old->legacy_borrowed) return old;
    reg_retired_node_t* node = (reg_retired_node_t*)malloc(sizeof(*node));
    if (!node) return NULL; /* 宁可保守泄漏也不在未知借用期 UAF */
    node->slot = old;
    node->next = reg->retired_head;
    reg->retired_head = node;
    return NULL;
}

/* 在桶 idx 对应的链表里查找 name 的现有条目；未找到返回 NULL。
 * 调用方必须已持有 reg->lock。 */
static reg_entry_t* reg_find_locked(qzdb_registry_t* reg, uint32_t idx, const char* name) {
    for (reg_entry_t* e = reg->buckets[idx]; e; e = e->next)
        if (strcmp(e->name, name) == 0) return e;
    return NULL;
}

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

static int reg_install_slot(qzdb_registry_t* reg, const char* name, reg_reader_slot_t* slot) {
    reg_reader_slot_t* detach = NULL;
    pthread_mutex_lock(&reg->lock);
    uint32_t idx = reg_hash(name) & (reg->cap - 1);
    reg_entry_t* e = reg_find_locked(reg, idx, name);
    if (e) {
        reg_reader_slot_t* old = e->slot;
        e->slot = slot;
        detach = reg_retire_locked(reg, old);
    } else {
        e = calloc(1, sizeof(reg_entry_t));
        if (!e) { pthread_mutex_unlock(&reg->lock); reg_slot_release(slot); return QZDB_ERR_OUT_OF_MEMORY; }
        e->name = strdup(name);
        if (!e->name) { pthread_mutex_unlock(&reg->lock); free(e); reg_slot_release(slot); return QZDB_ERR_OUT_OF_MEMORY; }
        e->slot = slot;
        e->next = reg->buckets[idx];
        reg->buckets[idx] = e;
        __atomic_store_n(&reg->count, reg->count + 1u, __ATOMIC_RELEASE);
    }
    pthread_mutex_unlock(&reg->lock);
    if (detach) reg_slot_release(detach);
    return QZDB_OK;
}

int qzdb_registry_register(qzdb_registry_t* reg, const char* name, const char* path) {
    if (!reg || !name || !path) return QZDB_ERR_INVALID_PARAM;
    reg_reader_slot_t* slot = calloc(1, sizeof(reg_reader_slot_t));
    if (!slot) return QZDB_ERR_OUT_OF_MEMORY;
    int rc = qzdb_init(&slot->reader, path);
    if (rc != QZDB_OK) { free(slot); return rc; }
    slot->refcount = 1;
    return reg_install_slot(reg, name, slot);
}

int qzdb_registry_register_buffer(qzdb_registry_t* reg, const char* name, const uint8_t* buffer, size_t size) {
    if (!reg || !name || !buffer || size == 0) return QZDB_ERR_INVALID_PARAM;
    reg_reader_slot_t* slot = calloc(1, sizeof(reg_reader_slot_t));
    if (!slot) return QZDB_ERR_OUT_OF_MEMORY;
    int rc = qzdb_init_buffer(&slot->reader, buffer, size, 1);
    if (rc != QZDB_OK) { free(slot); return rc; }
    slot->refcount = 1;
    return reg_install_slot(reg, name, slot);
}

qzdb_reader_t* qzdb_registry_get(qzdb_registry_t* reg, const char* name) {
    if (!reg || !name) return NULL;
    pthread_mutex_lock(&reg->lock);
    uint32_t idx = reg_hash(name) & (reg->cap - 1);
    reg_entry_t* e = reg_find_locked(reg, idx, name);
    reg_reader_slot_t* slot = e ? e->slot : NULL;
    if (slot) slot->legacy_borrowed = 1;
    pthread_mutex_unlock(&reg->lock);
    return slot ? &slot->reader : NULL;
}

qzdb_reader_t* qzdb_registry_acquire(qzdb_registry_t* reg, const char* name) {
    if (!reg || !name) return NULL;
    pthread_mutex_lock(&reg->lock);
    uint32_t idx = reg_hash(name) & (reg->cap - 1);
    reg_entry_t* e = reg_find_locked(reg, idx, name);
    reg_reader_slot_t* slot = e ? e->slot : NULL;
    if (slot) __atomic_add_fetch(&slot->refcount, 1, __ATOMIC_RELAXED);
    pthread_mutex_unlock(&reg->lock);
    return slot ? &slot->reader : NULL;
}

void qzdb_registry_release(qzdb_reader_t* reader) {
    if (!reader) return;
    reg_slot_release((reg_reader_slot_t*)reader);
}

void qzdb_registry_unregister(qzdb_registry_t* reg, const char* name) {
    if (!reg || !name) return;
    reg_reader_slot_t* detach = NULL;
    pthread_mutex_lock(&reg->lock);
    uint32_t idx = reg_hash(name) & (reg->cap - 1);
    reg_entry_t** pp = &reg->buckets[idx];
    while (*pp) {
        if (strcmp((*pp)->name, name) == 0) {
            reg_entry_t* del = *pp; *pp = del->next;
            detach = reg_retire_locked(reg, del->slot);
            free(del->name); free(del);
            __atomic_store_n(&reg->count, reg->count - 1u, __ATOMIC_RELEASE);
            break;
        }
        pp = &(*pp)->next;
    }
    pthread_mutex_unlock(&reg->lock);
    if (detach) reg_slot_release(detach);
}

int qzdb_registry_count(qzdb_registry_t* reg) {
    return reg ? (int)__atomic_load_n(&reg->count, __ATOMIC_ACQUIRE) : 0;
}

void qzdb_registry_free(qzdb_registry_t* reg) {
    if (!reg) return;
    for (uint32_t i = 0; i < reg->cap; i++) {
        reg_entry_t* e = reg->buckets[i];
        while (e) {
            reg_entry_t* next = e->next;
            reg_slot_release(e->slot);
            free(e->name); free(e);
            e = next;
        }
    }
    /* 冲刷所有曾通过旧版 qzdb_registry_get() 借出的退休快照；
     * 若仍有 acquire 租约在途，refcount 未归零，将由最后一次 qzdb_registry_release() 释放。 */
    reg_retired_node_t* rn = reg->retired_head;
    while (rn) {
        reg_retired_node_t* next = rn->next;
        reg_slot_release(rn->slot);
        free(rn);
        rn = next;
    }
    reg->retired_head = NULL;
    free(reg->buckets); pthread_mutex_destroy(&reg->lock); free(reg);
}

/* ========================================================================
 * String pool loading
 * ======================================================================== */
static int ensure_pools_loaded(qzdb_reader_t* ctx) {
    if (!ctx) return QZDB_ERR_INVALID_PARAM;
    if (ctx->pools_loaded) return QZDB_OK;
    if (ctx->actual_groups <= 0 || !ctx->group_field_counts) return QZDB_ERR_CORRUPTED;

    ctx->pools_loaded = 0;
    ctx->pool_arena = NULL;
    ctx->group_pools = qzdb_test_pool_calloc((size_t)ctx->actual_groups, sizeof(char***));
    if (!ctx->group_pools) return QZDB_ERR_OUT_OF_MEMORY;
    ctx->group_pool_counts = qzdb_test_pool_calloc((size_t)ctx->actual_groups, sizeof(int*));
    if (!ctx->group_pool_counts) return QZDB_ERR_OUT_OF_MEMORY;
    if (ctx->off_pools <= 0) {
        ctx->pools_loaded = 1;
        return QZDB_OK;
    }

    typedef struct { uint32_t count; uint32_t* offsets; uint64_t data_base; uint32_t tail; } pool_scan_t;
    pool_scan_t** scans = qzdb_test_pool_calloc((size_t)ctx->actual_groups, sizeof(pool_scan_t*));
    if (!scans) return QZDB_ERR_OUT_OF_MEMORY;
    int result = QZDB_OK;
    uint64_t pool_cursor = ctx->off_pools;
    uint64_t pool_end = ctx->off_meta > 0 ? ctx->off_meta : ctx->data_size;
    if (pool_end > ctx->data_size) pool_end = ctx->data_size;
    uint8_t* d = ctx->data;
    size_t arena_need = 0;

    for (int g = 0; g < ctx->actual_groups && result == QZDB_OK; g++) {
        int field_count = ctx->group_field_counts[g];
        if (field_count <= 0 || field_count > QZDB_MAX_FIELDS ||
            !ctx->group_field_native || !ctx->group_field_native[g]) {
            result = QZDB_ERR_CORRUPTED;
            break;
        }
        ctx->group_pools[g] = qzdb_test_pool_calloc((size_t)field_count, sizeof(char**));
        ctx->group_pool_counts[g] = qzdb_test_pool_calloc((size_t)field_count, sizeof(int));
        scans[g] = qzdb_test_pool_calloc((size_t)field_count, sizeof(pool_scan_t));
        if (!ctx->group_pools[g] || !ctx->group_pool_counts[g] || !scans[g]) {
            result = QZDB_ERR_OUT_OF_MEMORY;
            break;
        }
        for (int f = 0; f < field_count; f++) {
            if (ctx->group_field_native[g][f]) continue;
            if (pool_cursor > pool_end || pool_end - pool_cursor < 4) continue;
            uint32_t count;
            if (safe_read_u32(d, ctx->data_size, pool_cursor, &count) != QZDB_OK) {
                result = QZDB_ERR_BOUNDS;
                break;
            }
            pool_cursor += 4;
            if (ctx->off_row_schema > 0) {
                if (pool_cursor > pool_end || pool_end - pool_cursor < 4) {
                    result = QZDB_ERR_BOUNDS;
                    break;
                }
                pool_cursor += 4;
            }
            if (count == 0 || count > 16000000) continue;
            uint32_t* offsets = qzdb_test_pool_malloc(((size_t)count + 1) * sizeof(uint32_t));
            if (!offsets) {
                result = QZDB_ERR_OUT_OF_MEMORY;
                break;
            }
            int offsets_ok = 1;
            for (uint32_t o = 0; o <= count; o++) {
                if (pool_cursor > pool_end || pool_end - pool_cursor < 4 ||
                    safe_read_u32(d, ctx->data_size, pool_cursor, &offsets[o]) != QZDB_OK) {
                    offsets_ok = 0;
                    break;
                }
                pool_cursor += 4;
            }
            if (!offsets_ok) {
                free(offsets);
                ctx->group_pool_counts[g][f] = 0;
                continue;
            }
            uint64_t limit = pool_end < ctx->data_size ? pool_end : ctx->data_size;
            uint64_t avail = pool_cursor < limit ? limit - pool_cursor : 0;
            uint32_t tail = offsets[count];
            if ((uint64_t)tail > avail) {
                free(offsets);
                ctx->group_pool_counts[g][f] = 0;
                continue;
            }
            ctx->group_pools[g][f] = qzdb_test_pool_calloc(count, sizeof(char*));
            if (!ctx->group_pools[g][f]) {
                free(offsets);
                result = QZDB_ERR_OUT_OF_MEMORY;
                break;
            }
            scans[g][f].count = count;
            scans[g][f].offsets = offsets;
            scans[g][f].data_base = pool_cursor;
            scans[g][f].tail = tail;
            ctx->group_pool_counts[g][f] = (int)count;
            uint32_t prev_end = 0;
            for (uint32_t s = 0; s < count; s++) {
                uint32_t start = offsets[s];
                uint32_t end = offsets[s+1];
                if (start < prev_end || end < start || end > tail) continue;
                prev_end = end;
                size_t add = (size_t)(end - start) + 1;
                if (add > SIZE_MAX - arena_need) {
                    result = QZDB_ERR_OUT_OF_MEMORY;
                    break;
                }
                arena_need += add;
            }
            if (result != QZDB_OK) break;
            pool_cursor += tail;
        }
    }

    char* arena = NULL;
    if (result == QZDB_OK && arena_need > 0) {
        arena = qzdb_test_pool_malloc(arena_need);
        if (!arena) result = QZDB_ERR_OUT_OF_MEMORY;
        else ctx->pool_arena = arena;
    }
    if (result == QZDB_OK) {
        size_t arena_off = 0;
        for (int g = 0; g < ctx->actual_groups; g++) {
            if (!scans[g]) continue;
            int field_count = ctx->group_field_counts[g];
            for (int f = 0; f < field_count; f++) {
                pool_scan_t* sc = &scans[g][f];
                if (!sc->offsets || !ctx->group_pools[g][f]) continue;
                int is_usage = 0;
                if (ctx->group_field_names && ctx->group_field_names[g] && ctx->group_field_names[g][f]) {
                    char norm[64];
                    normalize_field_name(ctx->group_field_names[g][f], norm, sizeof(norm));
                    if (strcmp(norm, "usagetype") == 0) is_usage = 1;
                }
                uint32_t prev_end = 0;
                for (uint32_t s = 0; s < sc->count; s++) {
                    uint32_t start = sc->offsets[s];
                    uint32_t end = sc->offsets[s+1];
                    if (start < prev_end || end < start || end > sc->tail) continue;
                    prev_end = end;
                    size_t length = (size_t)(end - start);
                    if (!arena || length + 1 > arena_need - arena_off) {
                        result = QZDB_ERR_BOUNDS;
                        break;
                    }
                    char* dst = arena + arena_off;
                    if (length > 0) memcpy(dst, d + sc->data_base + start, length);
                    dst[length] = '\0';
                    if (is_usage && length == 7 && memcmp(dst, "Unknown", 7) == 0) {
                        dst[0] = '\0';
                    }
                    ctx->group_pools[g][f][s] = dst;
                    arena_off += length + 1;
                }
                if (result != QZDB_OK) break;
            }
            if (result != QZDB_OK) break;
        }
    }

    for (int g = 0; g < ctx->actual_groups; g++) {
        if (!scans[g]) continue;
        int field_count = ctx->group_field_counts[g];
        for (int f = 0; f < field_count; f++) free(scans[g][f].offsets);
        free(scans[g]);
    }
    free(scans);
    if (result == QZDB_OK) ctx->pools_loaded = 1;
    return result;
}

/* ========================================================================
 * Initialization (header parsing)
 * ======================================================================== */
int qzdb_init(qzdb_reader_t* ctx, const char* db_path) {
    return qzdb_init_ex(ctx, db_path, 1);
}

/* 解析并校验 ROW_SCHEMA 段（Fail-Closed）。 */
static int parse_row_schema_section(qzdb_reader_t* ctx) {
    const uint8_t* d = ctx->data;
    ctx->row_geo_width = 3; ctx->row_asn_width = 3; ctx->row_usage_width = 0;
    ctx->row_geo_offset = 0; ctx->row_asn_offset = 3; ctx->row_usage_offset = 6;
    if (ctx->off_row_schema == 0) return QZDB_OK;
    uint64_t sp = ctx->off_row_schema;
    uint8_t f_count = d[sp]; uint8_t stride = d[sp + 1];
    if (f_count < 1 || f_count > 8 || stride != ctx->ip_row_size ||
        (uint64_t)f_count * 4 > ctx->data_size - (sp + 4)) {
        return QZDB_ERR_CORRUPTED;
    }
    uint64_t wpos = sp + 4;
    int geo_w = 0, asn_w = 0, usage_w = 0;
    int geo_off = 0, asn_off = 0, usage_off = 0;
    uint8_t seen_mask = 0;
    uint8_t used[64];
    memset(used, 0, sizeof(used));
    for (uint8_t i = 0; i < f_count; i++) {
        uint8_t fid = d[wpos]; uint8_t w = d[wpos + 1]; uint8_t foff = d[wpos + 2];
        if (w < 1 || w > 4 || (uint16_t)foff + (uint16_t)w > (uint16_t)stride) return QZDB_ERR_CORRUPTED;
        for (uint8_t b = foff; b < foff + w; b++) {
            if (used[b]) return QZDB_ERR_CORRUPTED;
            used[b] = 1;
        }
        if (fid < 8) {
            if (seen_mask & (uint8_t)(1u << fid)) return QZDB_ERR_CORRUPTED;
            seen_mask |= (uint8_t)(1u << fid);
        }
        if (fid == 0) { geo_w = w; geo_off = foff; }
        else if (fid == 1) { asn_w = w; asn_off = foff; }
        else if (fid == 2) { usage_w = w; usage_off = foff; }
        wpos += 4;
    }
    if (geo_w <= 0) return QZDB_ERR_CORRUPTED;
    ctx->row_geo_width = geo_w; ctx->row_asn_width = asn_w; ctx->row_usage_width = usage_w;
    ctx->row_geo_offset = geo_off; ctx->row_asn_offset = asn_off; ctx->row_usage_offset = usage_off;
    return QZDB_OK;
}

/* 解析 GroupMetadataTable + GROUP_SCHEMA，并对字段布局与组边界做全量 Fail-Closed 校验。 */
static int parse_groups_and_schema(qzdb_reader_t* ctx) {
    const uint8_t* d = ctx->data;
    ctx->group_entry_offsets = malloc(4 * sizeof(uint64_t));
    if (!ctx->group_entry_offsets) return QZDB_ERR_OUT_OF_MEMORY;
    for (int i = 0; i < 4; i++) ctx->group_entry_offsets[i] = READ_LE48(d + 168 + i * 6);

    uint64_t gm_off = ctx->off_geo_entries;
    if (ctx->data_size - gm_off < 1) return QZDB_ERR_BOUNDS;
    {
        int gc_probe = d[gm_off];
        if (gc_probe < 1) gc_probe = 1;
        if (gc_probe > 4) gc_probe = 4;
        if (gm_off + 1 + (uint64_t)gc_probe * 7 > ctx->data_size) return QZDB_ERR_BOUNDS;
    }
    int group_count = d[gm_off++];
    ctx->actual_groups = group_count < 1 ? 1 : group_count;
    if (ctx->geo_entry_group_count > 0 && ctx->geo_entry_group_count < ctx->actual_groups) ctx->actual_groups = ctx->geo_entry_group_count;
    if (ctx->actual_groups > 4) ctx->actual_groups = 4;

    ctx->group_field_counts = malloc((size_t)ctx->actual_groups * sizeof(int));
    ctx->group_entry_counts = malloc((size_t)ctx->actual_groups * sizeof(uint32_t));
    ctx->group_dim_masks = malloc((size_t)ctx->actual_groups * sizeof(uint16_t));
    if (!ctx->group_field_counts || !ctx->group_entry_counts || !ctx->group_dim_masks) return QZDB_ERR_OUT_OF_MEMORY;

    for (int gi = 0; gi < ctx->actual_groups; gi++) {
        ctx->group_field_counts[gi] = d[gm_off++];
        if (ctx->group_field_counts[gi] <= 0 || ctx->group_field_counts[gi] > QZDB_MAX_FIELDS) return QZDB_ERR_CORRUPTED;
        ctx->group_entry_counts[gi] = READ_LE32(d + gm_off); gm_off += 4;
        ctx->group_dim_masks[gi] = READ_LE16(d + gm_off); gm_off += 2;
    }

    ctx->group_strides = calloc((size_t)ctx->actual_groups, sizeof(int));
    ctx->group_field_widths = calloc((size_t)ctx->actual_groups, sizeof(int*));
    ctx->group_field_offsets = calloc((size_t)ctx->actual_groups, sizeof(int*));
    ctx->group_field_native = calloc((size_t)ctx->actual_groups, sizeof(int*));
    ctx->group_field_native_type = calloc((size_t)ctx->actual_groups, sizeof(int*));
    ctx->group_ids = calloc((size_t)ctx->actual_groups, sizeof(uint16_t));
    ctx->group_pool_section_ids = calloc((size_t)ctx->actual_groups, sizeof(uint32_t*));
    if (!ctx->group_strides || !ctx->group_field_widths || !ctx->group_field_offsets || !ctx->group_field_native ||
        !ctx->group_field_native_type || !ctx->group_ids || !ctx->group_pool_section_ids) return QZDB_ERR_OUT_OF_MEMORY;

    int schema_fld_count[4] = {-1, -1, -1, -1};
    if (ctx->off_group_schema > 0 && ctx->off_group_schema + 2 <= ctx->data_size) {
        uint64_t sp = ctx->off_group_schema;
        int gs_group_count = READ_LE16(d + sp); sp += 2;
        int max_gs_groups = gs_group_count < ctx->actual_groups ? gs_group_count : ctx->actual_groups;
        for (int gi = 0; gi < max_gs_groups; gi++) {
            if (sp + 16 > ctx->data_size) return QZDB_ERR_CORRUPTED;
            ctx->group_ids[gi] = READ_LE16(d + sp); sp += 2;
            int fld_count = READ_LE16(d + sp); sp += 6;
            uint32_t stride_u = READ_LE32(d + sp); sp += 8;
            if (fld_count < 0 || fld_count > QZDB_MAX_FIELDS ||
                (uint64_t)fld_count * 12 > ctx->data_size - sp) return QZDB_ERR_CORRUPTED;
            if (stride_u < 1u || stride_u > 4096u) return QZDB_ERR_CORRUPTED;
            schema_fld_count[gi] = fld_count;
            ctx->group_strides[gi] = (int)stride_u;
            ctx->group_field_widths[gi] = malloc((size_t)fld_count * sizeof(int));
            ctx->group_field_offsets[gi] = malloc((size_t)fld_count * sizeof(int));
            ctx->group_field_native[gi] = malloc((size_t)fld_count * sizeof(int));
            ctx->group_field_native_type[gi] = malloc((size_t)fld_count * sizeof(int));
            ctx->group_pool_section_ids[gi] = malloc((size_t)fld_count * sizeof(uint32_t));
            if (!ctx->group_field_widths[gi] || !ctx->group_field_offsets[gi] ||
                !ctx->group_field_native[gi] || !ctx->group_field_native_type[gi] ||
                !ctx->group_pool_section_ids[gi]) return QZDB_ERR_OUT_OF_MEMORY;
            for (int fi = 0; fi < fld_count; fi++) {
                sp += 2;
                ctx->group_field_widths[gi][fi] = d[sp++];
                int field_flags = d[sp++];
                ctx->group_field_native[gi][fi] = (field_flags & 0x01) != 0;
                ctx->group_field_native_type[gi][fi] = (field_flags >> 1) & 0x03;
                ctx->group_field_offsets[gi][fi] = READ_LE32(d + sp); sp += 4;
                ctx->group_pool_section_ids[gi][fi] = READ_LE32(d + sp); sp += 4;
            }
        }
    }

    for (int g = 0; g < ctx->actual_groups; g++) {
        int fc = ctx->group_field_counts[g];
        if (fc <= 0 || fc > QZDB_MAX_FIELDS) return QZDB_ERR_CORRUPTED;
        if (schema_fld_count[g] >= 0 && schema_fld_count[g] != fc) {
            free(ctx->group_field_widths[g]);       ctx->group_field_widths[g] = NULL;
            free(ctx->group_field_offsets[g]);      ctx->group_field_offsets[g] = NULL;
            free(ctx->group_field_native[g]);       ctx->group_field_native[g] = NULL;
            free(ctx->group_field_native_type[g]);  ctx->group_field_native_type[g] = NULL;
            free(ctx->group_pool_section_ids[g]);   ctx->group_pool_section_ids[g] = NULL;
            ctx->group_strides[g] = 0;
        }
        if (ctx->group_strides[g] <= 0) ctx->group_strides[g] = fc * ctx->pool_idx_size;
        if (!ctx->group_field_widths[g]) {
            ctx->group_field_widths[g] = malloc((size_t)fc * sizeof(int));
            if (!ctx->group_field_widths[g]) return QZDB_ERR_OUT_OF_MEMORY;
            for (int i = 0; i < fc; i++) ctx->group_field_widths[g][i] = ctx->pool_idx_size;
        }
        if (!ctx->group_field_offsets[g]) {
            ctx->group_field_offsets[g] = malloc((size_t)fc * sizeof(int));
            if (!ctx->group_field_offsets[g]) return QZDB_ERR_OUT_OF_MEMORY;
            for (int i = 0; i < fc; i++) ctx->group_field_offsets[g][i] = i * ctx->pool_idx_size;
        }
        if (!ctx->group_field_native[g]) {
            ctx->group_field_native[g] = calloc((size_t)fc, sizeof(int));
            if (!ctx->group_field_native[g]) return QZDB_ERR_OUT_OF_MEMORY;
        }
        if (!ctx->group_field_native_type[g]) {
            ctx->group_field_native_type[g] = calloc((size_t)fc, sizeof(int));
            if (!ctx->group_field_native_type[g]) return QZDB_ERR_OUT_OF_MEMORY;
        }
        if (!ctx->group_pool_section_ids[g]) {
            ctx->group_pool_section_ids[g] = calloc((size_t)fc, sizeof(uint32_t));
            if (!ctx->group_pool_section_ids[g]) return QZDB_ERR_OUT_OF_MEMORY;
        }
        for (int fi = 0; fi < fc; fi++) {
            int w = ctx->group_field_widths[g][fi];
            int off = ctx->group_field_offsets[g][fi];
            int valid_w = ctx->group_field_native[g][fi]
                        ? (ctx->group_field_native_type[g][fi] == 1 ? (w == 4 || w == 8) : (ctx->group_field_native_type[g][fi] == 0 && w >= 1 && w <= 4))
                        : (w >= 1 && w <= 4);
            if (!valid_w || off < 0 || (uint64_t)off + (uint64_t)w > (uint64_t)ctx->group_strides[g]) return QZDB_ERR_BOUNDS;
        }
        if (ctx->group_entry_counts[g] > 0) {
            uint64_t g_off = ctx->group_entry_offsets[g];
            if (ctx->off_geo_entries > UINT64_MAX - g_off) return QZDB_ERR_BOUNDS;
            uint64_t g_start = ctx->off_geo_entries + g_off;
            if (g_start > (uint64_t)ctx->data_size ||
                (uint64_t)ctx->group_entry_counts[g] > ((uint64_t)ctx->data_size - g_start) / (uint64_t)ctx->group_strides[g]) {
                return QZDB_ERR_BOUNDS;
            }
        }
    }
    return QZDB_OK;
}

/* 解析 Metadata TLV 段。 */
static int parse_metadata_section(qzdb_reader_t* ctx, char** out_primary, char** out_data_month,
                                  char** out_scope, char*** out_names, int* out_name_count) {
    const uint8_t* d = ctx->data;
    if (!(ctx->flags & 4) || ctx->off_meta == 0 || ctx->off_meta + 4 > ctx->data_size) return QZDB_OK;
    uint64_t pos = ctx->off_meta;
    while (pos + 4 <= ctx->data_size) {
        int t = d[pos]; int length = READ_LE16(d + pos + 2);
        if (t == 0 || length == 0) break;
        if (pos + 4 + (uint64_t)length > ctx->data_size) break;
        char* val = malloc((size_t)length + 1);
        if (!val) return QZDB_ERR_OUT_OF_MEMORY;
        memcpy(val, d + pos + 4, (size_t)length); val[length] = '\0';
        if (t == 1) { free(ctx->version_name); ctx->version_name = val; }
        else if (t == 2) {
            for (int i = 0; i < *out_name_count; i++) free((*out_names)[i]);
            free(*out_names); *out_names = NULL; *out_name_count = 0;
            char sep = '|';
            int cnt = 1;
            for (const char* q = val; *q; q++) if (*q == '|') cnt++;
            if (cnt == 1) { sep = ','; for (const char* q = val; *q; q++) if (*q == ',') cnt++; }
            char** names = calloc((size_t)cnt, sizeof(char*));
            if (!names) { free(val); return QZDB_ERR_OUT_OF_MEMORY; }
            const char* seg = val; int idx = 0;
            while (idx < cnt) {
                const char* q = seg; while (*q && *q != sep) q++;
                size_t tok_len = (size_t)(q - seg);
                char* token = malloc(tok_len + 1);
                if (!token) {
                    for (int k = 0; k < idx; k++) free(names[k]);
                    free(names); free(val);
                    return QZDB_ERR_OUT_OF_MEMORY;
                }
                memcpy(token, seg, tok_len); token[tok_len] = '\0';
                names[idx++] = token;
                if (*q == sep) seg = q + 1; else break;
            }
            *out_names = names; *out_name_count = idx;
            free(val);
        }
        else if (t == 3) { free(ctx->description); ctx->description = val; }
        else if (t == 4) { free(*out_primary); *out_primary = val; }
        else if (t == 5) { free(*out_data_month); *out_data_month = val; }
        else if (t == 6) { free(*out_scope); *out_scope = val; }
        else free(val);
        pos += 4 + (uint64_t)length;
    }
    return QZDB_OK;
}

/* 逐组解析 edition/字段名、绑定默认组视图、设置期号/范围并修复缺失 dimensionMask。 */
static int resolve_group_editions(qzdb_reader_t* ctx, const char* meta_primary,
                                  char* const* meta_names, int meta_name_count,
                                  char** meta_data_month_io, char** meta_scope_io) {
    char meta_edition[64]; meta_edition[0] = '\0';
    if (meta_primary && meta_primary[0]) {
        if (!single_version_token(meta_primary, meta_edition, sizeof(meta_edition))) meta_edition[0] = '\0';
    } else if (ctx->version_name && ctx->version_name[0]) {
        if (!single_version_token(ctx->version_name, meta_edition, sizeof(meta_edition))) meta_edition[0] = '\0';
    }

    ctx->group_field_names     = calloc((size_t)ctx->actual_groups, sizeof(char**));
    ctx->group_editions        = calloc((size_t)ctx->actual_groups, sizeof(const char*));
    ctx->group_edition_sources = calloc((size_t)ctx->actual_groups, sizeof(const char*));
    ctx->group_name_sources    = calloc((size_t)ctx->actual_groups, sizeof(const char*));
    if (!ctx->group_field_names || !ctx->group_editions ||
        !ctx->group_edition_sources || !ctx->group_name_sources) return QZDB_ERR_OUT_OF_MEMORY;

    for (int g = 0; g < ctx->actual_groups; g++) {
        int nf = ctx->group_field_counts[g];
        uint16_t mask = ctx->group_ids[g] ? ctx->group_ids[g] : ctx->version_mask;
        const char* edition = qzdb_edition_from_mask(mask);
        const char* source  = QZDB_EDITION_SOURCE_VERSION_MASK;
        if (!edition[0] && meta_edition[0]) {
            for (int i = 0; i < 5; i++) {
                if (strcmp(meta_edition, EDITION_BY_BIT[i]) == 0) {
                    edition = EDITION_BY_BIT[i]; source = QZDB_EDITION_SOURCE_METADATA; break;
                }
            }
        }
        if (!edition[0]) {
            edition = edition_by_field_count(nf);
            source  = edition[0] ? QZDB_EDITION_SOURCE_INFERRED : QZDB_EDITION_SOURCE_UNKNOWN;
        }

        char** names = calloc((size_t)(nf + 1), sizeof(char*));
        if (!names) return QZDB_ERR_OUT_OF_MEMORY;
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
        for (int i = 0; i < nf; i++) {
            if (names[i]) continue;
            for (int k = 0; k < nf; k++) free(names[k]);
            free(names);
            return QZDB_ERR_OUT_OF_MEMORY;
        }
        ctx->group_field_names[g]     = names;
        ctx->group_editions[g]        = edition;
        ctx->group_edition_sources[g] = source;
        ctx->group_name_sources[g]    = name_source;
    }

    int group_meta_rc = apply_group_meta(ctx, ctx->group_index);
    if (group_meta_rc != QZDB_OK) return group_meta_rc;

    if (ctx->build_date > 0) {
        char b2[32];
        snprintf(b2, sizeof(b2), "%04d-%02d-%02d", ctx->build_date / 10000, (ctx->build_date / 100) % 100, ctx->build_date % 100);
        ctx->build_time_str = strdup(b2);
    } else { ctx->build_time_str = strdup(""); }
    if (*meta_data_month_io) { free(ctx->data_month); ctx->data_month = *meta_data_month_io; *meta_data_month_io = NULL; }
    else {
        char b1[32];
        free(ctx->data_month);
        if (ctx->build_date > 0) {
            snprintf(b1, sizeof(b1), "%04d-%02d", ctx->build_date / 10000, (ctx->build_date / 100) % 100);
            ctx->data_month = strdup(b1);
        } else { ctx->data_month = strdup(""); }
    }
    if (*meta_scope_io) { ctx->scope = *meta_scope_io; *meta_scope_io = NULL; }
    else { ctx->scope = strdup(""); }
    if (!ctx->scope || !ctx->build_time_str || !ctx->data_month) return QZDB_ERR_OUT_OF_MEMORY;

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
    return QZDB_OK;
}

/* Common initialization: parse header, validate, load pools, init cache.
 * ctx->data and ctx->data_size must be set by the caller.
 * is_heap indicates whether ctx->data needs free() (heap) vs munmap() (mmap). */
static int init_from_buffer(qzdb_reader_t* ctx, int is_heap, int verify_crc) {
    (void)is_heap;
    int ret = QZDB_ERR_CORRUPTED;
    char*  meta_primary    = NULL;
    char*  meta_data_month = NULL;
    char*  meta_scope      = NULL;
    char** meta_names      = NULL;
    int    meta_name_count = 0;

    uint8_t* d = ctx->data;
    if (ctx->data_size < 192) { return QZDB_ERR_BAD_HEADER; }
    if (memcmp(d, "QZDB", 4) != 0) { return QZDB_ERR_BAD_MAGIC; }
    if (d[4] != 1) { return QZDB_ERR_UNSUPPORTED; }

    ctx->numeric_locale = (void*)newlocale(LC_NUMERIC_MASK, "C", (locale_t)0);
    if (!ctx->numeric_locale) { ret = QZDB_ERR_OUT_OF_MEMORY; goto fail; }

    ctx->version_mask = READ_LE16(d + 6);
    ctx->flags = READ_LE16(d + 8);
    ctx->has_v4 = (ctx->flags & 1) != 0;
    ctx->has_v6 = (ctx->flags & 2) != 0;
    ctx->v4_node_24 = (ctx->flags & 0x10) != 0;
    ctx->v6_node_24 = (ctx->flags & 0x20) != 0;
    ctx->v6_jump_bits = d[11] ? d[11] : 16;
    if (ctx->v6_jump_bits < 8 || ctx->v6_jump_bits > 20) { ret = QZDB_ERR_BAD_HEADER; goto fail; }
    ctx->pool_count = d[12];
    ctx->pool_idx_size = d[13];
    if (ctx->pool_idx_size != 2 && ctx->pool_idx_size != 3) { ret = QZDB_ERR_BAD_HEADER; goto fail; }
    ctx->geo_count = READ_LE16(d + 14);
    ctx->row_count = READ_LE32(d + 20);
    ctx->v4_rec_count = READ_LE32(d + 24);
    ctx->v6_rec_count = READ_LE32(d + 28);
    ctx->build_date = READ_LE32(d + 32);
    if (READ_LE32(d + 36) != 192) { ret = QZDB_ERR_CORRUPTED; goto fail; }

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
    if (ctx->ip_row_size < 1 || ctx->ip_row_size > 64) { ret = QZDB_ERR_BAD_HEADER; goto fail; }
    ctx->geo_entry_group_count = READ_LE32(d + 164);
    if (ctx->geo_entry_group_count < 1 || ctx->geo_entry_group_count > 255) { ret = QZDB_ERR_BAD_HEADER; goto fail; }

    /* Section offset bounds validation */
    {
        uint64_t v4_ns = ctx->v4_node_24 ? 6 : 8;
        uint64_t v6_ns = ctx->v6_node_24 ? 6 : 8;
        uint64_t v6_jump_size = ((uint64_t)1 << ctx->v6_jump_bits) * 4;
        if (ctx->has_v4 || ctx->off_v4_jump > 0) {
            if (ctx->off_v4_jump == 0 || ctx->off_v4_jump > ctx->data_size ||
                (uint64_t)65536 * 4 > ctx->data_size - ctx->off_v4_jump) { ret = QZDB_ERR_BOUNDS; goto fail; }
        }
        if (ctx->v4_node_count > 0 || ctx->off_v4_nodes > 0) {
            if (ctx->off_v4_nodes == 0 || ctx->off_v4_nodes > ctx->data_size ||
                (uint64_t)ctx->v4_node_count * v4_ns > ctx->data_size - ctx->off_v4_nodes) { ret = QZDB_ERR_BOUNDS; goto fail; }
        }
        if (ctx->has_v6 || ctx->v6_node_count > 0 || ctx->off_v6_jump > 0) {
            if (ctx->off_v6_jump == 0 || ctx->off_v6_jump > ctx->data_size ||
                v6_jump_size > ctx->data_size - ctx->off_v6_jump) { ret = QZDB_ERR_BOUNDS; goto fail; }
        }
        if (ctx->v6_node_count > 0 || ctx->off_v6_nodes > 0) {
            if (ctx->off_v6_nodes == 0 || ctx->off_v6_nodes > ctx->data_size ||
                (uint64_t)ctx->v6_node_count * v6_ns > ctx->data_size - ctx->off_v6_nodes) { ret = QZDB_ERR_BOUNDS; goto fail; }
        }
        if (ctx->off_ip_row == 0 || ctx->off_ip_row > ctx->data_size ||
            (uint64_t)ctx->row_count * ctx->ip_row_size > ctx->data_size - ctx->off_ip_row) { ret = QZDB_ERR_BOUNDS; goto fail; }
        if (ctx->off_geo_entries == 0 || ctx->off_geo_entries > ctx->data_size ||
            16 > ctx->data_size - ctx->off_geo_entries) { ret = QZDB_ERR_BOUNDS; goto fail; }
        if (ctx->off_pools == 0 || ctx->off_pools >= ctx->data_size) { ret = QZDB_ERR_BOUNDS; goto fail; }
        if (ctx->off_group_schema > 0 &&
            (ctx->off_group_schema > ctx->data_size || 2 > ctx->data_size - ctx->off_group_schema)) { ret = QZDB_ERR_BOUNDS; goto fail; }
        if (ctx->off_row_schema > 0 &&
            (ctx->off_row_schema > ctx->data_size || 4 > ctx->data_size - ctx->off_row_schema)) { ret = QZDB_ERR_BOUNDS; goto fail; }
        if (ctx->off_meta > 0 && ctx->off_meta > ctx->data_size) { ret = QZDB_ERR_BOUNDS; goto fail; }
    }

    ctx->v4_nodes_base = ctx->off_v4_nodes > 0 ? (ctx->data + ctx->off_v4_nodes) : NULL;
    ctx->v6_nodes_base = ctx->off_v6_nodes > 0 ? (ctx->data + ctx->off_v6_nodes) : NULL;

    if ((ret = parse_row_schema_section(ctx)) != QZDB_OK) goto fail;
    if ((ret = parse_groups_and_schema(ctx)) != QZDB_OK) goto fail;
    if ((ret = parse_metadata_section(ctx, &meta_primary, &meta_data_month, &meta_scope, &meta_names, &meta_name_count)) != QZDB_OK) goto fail;
    if ((ret = resolve_group_editions(ctx, meta_primary, meta_names, meta_name_count, &meta_data_month, &meta_scope)) != QZDB_OK) goto fail;
    for (int i = 0; i < meta_name_count; i++) free(meta_names[i]);
    free(meta_names); free(meta_primary);
    meta_names = NULL; meta_primary = NULL; meta_name_count = 0;

    ctx->pools_loaded = 0; ctx->group_pools = NULL; ctx->group_pool_counts = NULL;
    if ((ret = ensure_pools_loaded(ctx)) != QZDB_OK) goto fail;
    build_v4_l2_table(ctx);
    geo_cache_init(ctx);
    ctx->generation = 1;
    if (!ctx->reload_lock) {
        pthread_mutex_t* lk = (pthread_mutex_t*)malloc(sizeof(pthread_mutex_t));
        if (!lk) { ret = QZDB_ERR_OUT_OF_MEMORY; goto fail; }
        pthread_mutex_init(lk, NULL);
        ctx->reload_lock = lk;
    }
    if (!ctx->reader_stripes) {
        void* st = calloc(16u, 64u); /* 16 × 64B 缓存行对齐分片读计数（N4） */
        if (!st) { ret = QZDB_ERR_OUT_OF_MEMORY; goto fail; }
        ctx->reader_stripes = st;
    }
    __atomic_store_n(&ctx->active_readers, 0, __ATOMIC_RELAXED);
    __atomic_store_n(&ctx->reloading, 0, __ATOMIC_RELAXED);
    ctx->version_code = (ctx->pool_count == 6) ? 1 : ((ctx->pool_count == 7) ? 2 : 3);

    if (verify_crc && qzdb_verify_crc(ctx) != QZDB_OK) { ret = QZDB_ERR_CORRUPTED; goto fail; }
    return QZDB_OK;

fail:
    for (int i = 0; i < meta_name_count; i++) free(meta_names[i]);
    free(meta_names); free(meta_primary);
    free(meta_data_month); free(meta_scope);
    free_heap_state(ctx);
    return ret;
}

int qzdb_init_ex(qzdb_reader_t* ctx, const char* db_path, int verify_crc) {
    if (!ctx || !db_path) return QZDB_ERR_INVALID_PARAM;
    memset(ctx, 0, sizeof(*ctx));
    int fd = open(db_path, O_RDONLY);
    if (fd < 0) return QZDB_ERR_CORRUPTED;
    struct stat st;
    if (fstat(fd, &st) != 0) { close(fd); return QZDB_ERR_CORRUPTED; }
    /* 与 qzdb_init_buffer 口径一致：不足 192 字节头即 BAD_HEADER。此前 0 字节文件
     * 走到 mmap(len=0) 失败（EINVAL）被报成 QZDB_ERR_OUT_OF_MEMORY。 */
    if (st.st_size < 192) { close(fd); return QZDB_ERR_BAD_HEADER; }
    ctx->data_size = (size_t)st.st_size;
    ctx->data = mmap(NULL, ctx->data_size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (ctx->data == MAP_FAILED) { ctx->data = NULL; return QZDB_ERR_OUT_OF_MEMORY; }
    madvise(ctx->data, ctx->data_size, MADV_RANDOM);
    ctx->data_is_heap = 0;
    int rc = init_from_buffer(ctx, 0, verify_crc);
    if (rc != QZDB_OK) {
        munmap(ctx->data, ctx->data_size);
        ctx->data = NULL;
        memset(ctx, 0, sizeof(*ctx));
    }
    return rc;
}

/* Buffer-based loading — copy semantics: the caller's buffer is copied into a
 * heap allocation so the caller may free it immediately. Cross-platform, no /tmp
 * I/O. The copy is freed by qzdb_free() (data_is_heap = 1). */
int qzdb_init_buffer(qzdb_reader_t* ctx, const uint8_t* buf, size_t len, int verify_crc) {
    if (!ctx || !buf || len == 0) return QZDB_ERR_INVALID_PARAM;
    memset(ctx, 0, sizeof(*ctx));
    ctx->data = (uint8_t*)malloc(len);
    if (!ctx->data) return QZDB_ERR_OUT_OF_MEMORY;
    memcpy(ctx->data, buf, len);
    ctx->data_size = len;
    ctx->data_is_heap = 1;
    int rc = init_from_buffer(ctx, 1, verify_crc);
    if (rc != QZDB_OK) {
        free(ctx->data);
        ctx->data = NULL;
        memset(ctx, 0, sizeof(*ctx));
    }
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
    ctx->data_is_borrowed = 1;
    int rc = init_from_buffer(ctx, 0, verify_crc);
    if (rc != QZDB_OK) qzdb_free(ctx);
    return rc;
}
/* 释放 ctx 的全部堆态（不含 data 缓冲本身）。每个指针释放后置 NULL，幂等。
 * 供 qzdb_free 与 init_from_buffer 的失败路径共用：加载中途失败（CRC/OOM/
 * apply_group_meta）时由本函数收尾，调用方只负责 data 的 free/munmap。 */
static void free_heap_state(qzdb_reader_t* ctx) {
    if (!ctx) return;
    free(ctx->v4_l1); ctx->v4_l1 = NULL;
    free(ctx->v4_l2); ctx->v4_l2 = NULL;
    ctx->v4_l2_blocks = 0;
    if (ctx->numeric_locale) {
        freelocale((locale_t)ctx->numeric_locale);
        ctx->numeric_locale = NULL;
    }
    free(ctx->pool_arena);
    ctx->pool_arena = NULL;
    int groups = ctx->actual_groups;
    if (groups < 0 || groups > 4) groups = 0;
    if (ctx->group_pools) {
        for (int g = 0; g < groups; g++) {
            int fields = 0;
            if (ctx->group_field_counts && g < ctx->actual_groups) {
                fields = ctx->group_field_counts[g];
                if (fields < 0 || fields > QZDB_MAX_FIELDS) fields = 0;
            }
            if (ctx->group_pools[g]) {
                for (int f = 0; f < fields; f++) free(ctx->group_pools[g][f]);
                free(ctx->group_pools[g]);
                ctx->group_pools[g] = NULL;
            }
        }
    }
    free(ctx->group_pools);
    ctx->group_pools = NULL;
    if (ctx->group_pool_counts) {
        for (int g = 0; g < groups; g++) free(ctx->group_pool_counts[g]);
    }
    free(ctx->group_pool_counts);
    ctx->group_pool_counts = NULL;
    free(ctx->group_entry_offsets);
    ctx->group_entry_offsets = NULL;
    for (int g = 0; g < groups; g++) {
        if (ctx->group_field_widths) free(ctx->group_field_widths[g]);
        if (ctx->group_field_offsets) free(ctx->group_field_offsets[g]);
        if (ctx->group_field_native) free(ctx->group_field_native[g]);
        if (ctx->group_field_native_type) free(ctx->group_field_native_type[g]);
        if (ctx->group_pool_section_ids) free(ctx->group_pool_section_ids[g]);
        if (ctx->group_field_names && ctx->group_field_names[g]) {
            int fields = 0;
            if (ctx->group_field_counts) {
                fields = ctx->group_field_counts[g];
                if (fields < 0 || fields > QZDB_MAX_FIELDS) fields = 0;
            }
            for (int i = 0; i < fields; i++) free(ctx->group_field_names[g][i]);
            free(ctx->group_field_names[g]);
            ctx->group_field_names[g] = NULL;
        }
    }
    free(ctx->group_field_counts); ctx->group_field_counts = NULL;
    free(ctx->group_entry_counts); ctx->group_entry_counts = NULL;
    free(ctx->group_dim_masks); ctx->group_dim_masks = NULL;
    free(ctx->group_strides); ctx->group_strides = NULL;
    free(ctx->group_field_widths); ctx->group_field_widths = NULL;
    free(ctx->group_field_offsets); ctx->group_field_offsets = NULL;
    free(ctx->group_field_native); ctx->group_field_native = NULL;
    free(ctx->group_field_native_type); ctx->group_field_native_type = NULL;
    free(ctx->group_ids); ctx->group_ids = NULL;
    free(ctx->group_pool_section_ids); ctx->group_pool_section_ids = NULL;
    free(ctx->group_field_names); ctx->group_field_names = NULL;
    free(ctx->group_editions); ctx->group_editions = NULL;
    free(ctx->group_edition_sources); ctx->group_edition_sources = NULL;
    free(ctx->group_name_sources); ctx->group_name_sources = NULL;
    ctx->field_names = NULL;
    free(ctx->float_field_flags); ctx->float_field_flags = NULL;
    free(ctx->version_name); ctx->version_name = NULL;
    free(ctx->description); ctx->description = NULL;
    free(ctx->edition); ctx->edition = NULL;
    free(ctx->data_month); ctx->data_month = NULL;
    free(ctx->build_time_str); ctx->build_time_str = NULL;
    free(ctx->scope); ctx->scope = NULL;
    if (ctx->norm_field_names) {
        for (int i = 0; i < ctx->field_count; i++) free(ctx->norm_field_names[i]);
        free(ctx->norm_field_names);
        ctx->norm_field_names = NULL;
    }
    norm_map_free(ctx);
    geo_cache_free(ctx);
    ctx->actual_groups = 0;
    ctx->field_count = 0;
}

static void free_reader_data(qzdb_reader_t* ctx) {
    if (!ctx) return;
    if (ctx->data_is_borrowed) {
        /* caller-owned */
    } else if (ctx->data_is_heap == 1) {
        free(ctx->data);
    } else if (ctx->data) {
        munmap(ctx->data, ctx->data_size);
    }
    ctx->data = NULL;
    ctx->data_size = 0;
}

#define QZDB_READER_STRIPES 16u
typedef struct { int32_t count; char pad[60]; } qzdb_reader_stripe_t;
static __thread uint32_t qzdb_tls_stripe_plus1 = 0;

static inline uint32_t reader_stripe_index(void) {
    uint32_t s = qzdb_tls_stripe_plus1;
    if (QZDB_UNLIKELY(s == 0)) {
        uintptr_t tid = (uintptr_t)pthread_self();
        s = (uint32_t)((((uint32_t)(tid ^ (tid >> 16))) * 2654435761u) >> 28) & (QZDB_READER_STRIPES - 1u);
        qzdb_tls_stripe_plus1 = s + 1u;
        return s;
    }
    return s - 1u;
}

/* N1 + N4 修复：
 *   - 16×64B 分片读计数消除多核缓存行乒乓；
 *   - 读写两侧对 reloading 与 stripe->count 均使用 __ATOMIC_SEQ_CST（Dekker 全序），
 *     杜绝 ARM64 弱内存模型下 store→load 重排导致的 UAF。 */
#define QZDB_TL_PIN_SLOTS 8
typedef struct {
    const qzdb_reader_t* ctx;
    int depth;
} qzdb_tl_pin_t;
static __thread qzdb_tl_pin_t tl_pins[QZDB_TL_PIN_SLOTS];

static inline void reader_pin(qzdb_reader_t* ctx) {
    for (int i = 0; i < QZDB_TL_PIN_SLOTS; i++) {
        if (tl_pins[i].ctx == ctx) {
            tl_pins[i].depth++;
            return;
        }
    }
    qzdb_reader_stripe_t* stripes = (qzdb_reader_stripe_t*)ctx->reader_stripes;
    int32_t* cnt = stripes ? &stripes[reader_stripe_index()].count : &ctx->active_readers;
    for (;;) {
        if (QZDB_UNLIKELY(__atomic_load_n(&ctx->reloading, __ATOMIC_ACQUIRE))) {
            if (ctx->reload_lock) {
                pthread_mutex_lock((pthread_mutex_t*)ctx->reload_lock);
                pthread_mutex_unlock((pthread_mutex_t*)ctx->reload_lock);
            } else {
                sched_yield();
            }
            continue;
        }
        __atomic_fetch_add(cnt, 1, __ATOMIC_SEQ_CST);
        if (QZDB_UNLIKELY(__atomic_load_n(&ctx->reloading, __ATOMIC_SEQ_CST))) {
            __atomic_fetch_sub(cnt, 1, __ATOMIC_RELEASE);
            continue;
        }
        break;
    }
    for (int i = 0; i < QZDB_TL_PIN_SLOTS; i++) {
        if (tl_pins[i].ctx == NULL) {
            tl_pins[i].ctx = ctx;
            tl_pins[i].depth = 1;
            return;
        }
    }
}

static inline void reader_unpin(qzdb_reader_t* ctx) {
    for (int i = 0; i < QZDB_TL_PIN_SLOTS; i++) {
        if (tl_pins[i].ctx == ctx) {
            if (--tl_pins[i].depth > 0) return;
            tl_pins[i].ctx = NULL;
            break;
        }
    }
    qzdb_reader_stripe_t* stripes = (qzdb_reader_stripe_t*)ctx->reader_stripes;
    int32_t* cnt = stripes ? &stripes[reader_stripe_index()].count : &ctx->active_readers;
    __atomic_fetch_sub(cnt, 1, __ATOMIC_RELEASE);
}

void qzdb_read_begin(qzdb_reader_t* ctx) {
    if (ctx) reader_pin(ctx);
}

void qzdb_read_end(qzdb_reader_t* ctx) {
    if (ctx) reader_unpin(ctx);
}

void qzdb_free(qzdb_reader_t* ctx) {
    if (!ctx) return;
    free_heap_state(ctx);
    free_reader_data(ctx);
    if (ctx->reload_lock) {
        pthread_mutex_destroy((pthread_mutex_t*)ctx->reload_lock);
        free(ctx->reload_lock);
        ctx->reload_lock = NULL;
    }
    free(ctx->reader_stripes);
    ctx->reader_stripes = NULL;
    memset(ctx, 0, sizeof(*ctx));
}

int qzdb_verify_crc(qzdb_reader_t* ctx) {
    if (!ctx) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->data || ctx->data_size < 20) return QZDB_ERR_CORRUPTED;
    uint32_t stored = READ_LE32(ctx->data + 16);
    uint32_t computed = crc32_compute_file(ctx->data, ctx->data_size);
    __atomic_store_n(&ctx->file_crc, computed, __ATOMIC_RELEASE);
    __atomic_store_n(&ctx->crc_valid, 1, __ATOMIC_RELEASE);
    return stored == computed ? QZDB_OK : QZDB_ERR_CORRUPTED;
}

static int bind_reload_group(qzdb_reader_t* new_ctx, int group_index) {
    if (!new_ctx || group_index < 0 || group_index >= new_ctx->actual_groups)
        return QZDB_ERR_INVALID_PARAM;
    if (group_index == new_ctx->group_index) return QZDB_OK;
    return qzdb_set_group_index(new_ctx, group_index);
}

/* 原子静默切换快照：先在锁外完成 new_ctx 解析与预热，再以 SEQ_CST 置 reloading=1
 * 并等待全部分片读计数归零后原地替换，最后在锁外释放旧快照。 */
static void swap_ctx_atomic(qzdb_reader_t* ctx, qzdb_reader_t* new_ctx) {
    pthread_mutex_t* lk = (pthread_mutex_t*)ctx->reload_lock;
    qzdb_reader_stripe_t* stripes = (qzdb_reader_stripe_t*)ctx->reader_stripes;
    if (lk) pthread_mutex_lock(lk);
    __atomic_store_n(&ctx->reloading, 1, __ATOMIC_SEQ_CST);
    if (stripes) {
        for (uint32_t i = 0; i < QZDB_READER_STRIPES; i++) {
            while (__atomic_load_n(&stripes[i].count, __ATOMIC_SEQ_CST) > 0) {
                sched_yield();
            }
        }
    } else {
        while (__atomic_load_n(&ctx->active_readers, __ATOMIC_SEQ_CST) > 0) {
            sched_yield();
        }
    }
    uint32_t next_gen = ctx->generation + 1;
    /* 销毁 new_ctx 临时分配的 reload_lock / reader_stripes，保留原 ctx 并发句柄 */
    if (new_ctx->reload_lock) {
        pthread_mutex_destroy((pthread_mutex_t*)new_ctx->reload_lock);
        free(new_ctx->reload_lock);
        new_ctx->reload_lock = NULL;
    }
    free(new_ctx->reader_stripes);
    new_ctx->reader_stripes = NULL;

    qzdb_reader_t old_ctx;
    memset(&old_ctx, 0, sizeof(old_ctx));
    memcpy(&old_ctx, ctx, offsetof(qzdb_reader_t, reload_lock));

    new_ctx->generation = next_gen;
    /* 仅复制快照状态字段，绝不非原子覆写尾部并发控制字段（reload_lock /
     * reader_stripes / active_readers / reloading），消除 C11 形式数据竞争。 */
    memcpy(ctx, new_ctx, offsetof(qzdb_reader_t, reload_lock));
    __atomic_store_n(&ctx->reloading, 0, __ATOMIC_RELEASE);
    if (lk) pthread_mutex_unlock(lk);

    /* 在锁外释放旧快照，避免阻塞新查询 */
    free_heap_state(&old_ctx);
    free_reader_data(&old_ctx);
}

int qzdb_reload_buffer(qzdb_reader_t* ctx, const uint8_t* buf, size_t len) {
    if (!ctx || !buf || len == 0 || ctx->group_index < 0 ||
        ctx->group_index >= ctx->actual_groups) return QZDB_ERR_INVALID_PARAM;
    int group_index = ctx->group_index;
    qzdb_reader_t new_ctx;
    memset(&new_ctx, 0, sizeof(new_ctx));
    int rc = qzdb_init_buffer(&new_ctx, buf, len, 1);
    if (rc != QZDB_OK) {
        qzdb_free(&new_ctx);
        return rc;
    }
    rc = bind_reload_group(&new_ctx, group_index);
    if (rc != QZDB_OK) {
        qzdb_free(&new_ctx);
        return rc;
    }
    qzdb_warmup(&new_ctx);
    swap_ctx_atomic(ctx, &new_ctx);
    return QZDB_OK;
}

int qzdb_reload(qzdb_reader_t* ctx, const char* db_path) {
    if (!ctx || !db_path || ctx->group_index < 0 ||
        ctx->group_index >= ctx->actual_groups) return QZDB_ERR_INVALID_PARAM;
    int group_index = ctx->group_index;
    qzdb_reader_t new_ctx;
    memset(&new_ctx, 0, sizeof(new_ctx));
    int result = qzdb_init(&new_ctx, db_path);
    if (result != QZDB_OK) {
        qzdb_free(&new_ctx);
        return result;
    }
    result = bind_reload_group(&new_ctx, group_index);
    if (result != QZDB_OK) {
        qzdb_free(&new_ctx);
        return result;
    }
    qzdb_warmup(&new_ctx);
    swap_ctx_atomic(ctx, &new_ctx);
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
    reader_pin(ctx);
    int rc;
    if (!ctx->has_v4) { rc = QZDB_ERR_NOT_FOUND; }
    else {
        uint32_t row_id = trie_walk_v4(ctx, ip_int);
        rc = (row_id == 0) ? QZDB_ERR_NOT_FOUND : resolve_row_id_cached(ctx, row_id, ctx->group_index, result);
    }
    reader_unpin(ctx);
    return rc;
}

int qzdb_find_v6(qzdb_reader_t* ctx, const uint8_t* ip_bin, qzdb_geo_info_t* result) {
    if (!ctx || !ip_bin || !result) return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    int rc;
    if (!ctx->has_v6) { rc = QZDB_ERR_NOT_FOUND; }
    else {
        uint32_t row_id = trie_walk_v6(ctx, ip_bin);
        rc = (row_id == 0) ? QZDB_ERR_NOT_FOUND : resolve_row_id_cached(ctx, row_id, ctx->group_index, result);
    }
    reader_unpin(ctx);
    return rc;
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

int qzdb_find_n(qzdb_reader_t* ctx, const char* ip_ptr, size_t ip_len, qzdb_geo_info_t* result) {
    if (!ctx || !ip_ptr || !result || ip_len == 0 || ip_len > 45) return QZDB_ERR_INVALID_PARAM;
    if (memchr(ip_ptr, '\0', ip_len) != NULL) return QZDB_ERR_INVALID_PARAM;
    char tmp[46];
    memcpy(tmp, ip_ptr, ip_len);
    tmp[ip_len] = '\0';
    return qzdb_find(ctx, tmp, result);
}

/* N7 + P1-2：8 元素分块解析并预取 v4_l1 槽位；稳态无 reload 时整批零额外原子开销，
 * 每 256 条以一次 relaxed load 探测 reloading 标志，遇并发 reload 立即让出 pin 并刷新快照视图，
 * 避免大批次拖住 reload 导致全进程读请求长尾停顿。 */
int qzdb_find_batch(qzdb_reader_t* ctx, const char** ips, int count, qzdb_batch_result_t* results) {
    if (!ctx || !ips || !results || count <= 0) return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    int gi = ctx->group_index;
    const uint32_t* l1 = ctx->v4_l1;
    for (int base = 0; base < count; base += 8) {
        int chunk = (count - base < 8) ? (count - base) : 8;
        parse_result_t parsed[8];
        uint8_t valid[8];
        for (int k = 0; k < chunk; k++) {
            const char* s = ips[base + k];
            valid[k] = (s && fast_parse_ip(s, &parsed[k])) ? 1 : 0;
#if defined(__GNUC__) || defined(__clang__)
            if (valid[k] && parsed[k].is_v4 && l1) {
                __builtin_prefetch(&l1[(parsed[k].v4 >> 16) & 0xFFFFu], 0, 1);
            }
#endif
        }
        for (int k = 0; k < chunk; k++) {
            int idx = base + k;
            memset(&results[idx].info, 0, sizeof(results[idx].info));
            if (!valid[k]) {
                results[idx].error_code = QZDB_ERR_INVALID_PARAM;
                continue;
            }
            if (parsed[k].is_v4) {
                if (!ctx->has_v4) { results[idx].error_code = QZDB_ERR_NOT_FOUND; continue; }
                uint32_t rid = trie_walk_v4(ctx, parsed[k].v4);
                results[idx].error_code = (rid == 0) ? QZDB_ERR_NOT_FOUND
                                                     : resolve_row_id_cached(ctx, rid, gi, &results[idx].info);
            } else {
                if (!ctx->has_v6) { results[idx].error_code = QZDB_ERR_NOT_FOUND; continue; }
                uint32_t rid = trie_walk_v6(ctx, parsed[k].v6);
                results[idx].error_code = (rid == 0) ? QZDB_ERR_NOT_FOUND
                                                     : resolve_row_id_cached(ctx, rid, gi, &results[idx].info);
            }
        }
    }
    reader_unpin(ctx);
    return QZDB_OK;
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
    reader_pin(ctx);
    uint32_t rid = res.is_v4 ? (ctx->has_v4 ? trie_walk_v4(ctx, res.v4) : 0)
                             : (ctx->has_v6 ? trie_walk_v6(ctx, res.v6) : 0);
    reader_unpin(ctx);
    return rid;
}

uint32_t qzdb_lookup_row_id_uint(qzdb_reader_t* ctx, uint32_t ip_int) {
    if (!ctx) return 0;
    reader_pin(ctx);
    uint32_t rid = ctx->has_v4 ? trie_walk_v4(ctx, ip_int) : 0;
    reader_unpin(ctx);
    return rid;
}

uint32_t qzdb_lookup_row_id_v6(qzdb_reader_t* ctx, const uint8_t* ip_bin) {
    if (!ctx || !ip_bin) return 0;
    reader_pin(ctx);
    uint32_t rid = ctx->has_v6 ? trie_walk_v6(ctx, ip_bin) : 0;
    reader_unpin(ctx);
    return rid;
}

uint32_t qzdb_lookup_row_id_bytes(qzdb_reader_t* ctx, const uint8_t* ip_bytes, int len) {
    if (!ctx || !ip_bytes) return 0;
    if (len == 16) {
        reader_pin(ctx);
        uint32_t rid = is_v4_mapped(ip_bytes)
                     ? (ctx->has_v4 ? trie_walk_v4(ctx, v4_from_mapped(ip_bytes)) : 0)
                     : (ctx->has_v6 ? trie_walk_v6(ctx, ip_bytes) : 0);
        reader_unpin(ctx);
        return rid;
    }
    if (len == 4) {
        uint32_t v4 = ((uint32_t)ip_bytes[0] << 24) | ((uint32_t)ip_bytes[1] << 16) |
                      ((uint32_t)ip_bytes[2] << 8)  | (uint32_t)ip_bytes[3];
        reader_pin(ctx);
        uint32_t rid = ctx->has_v4 ? trie_walk_v4(ctx, v4) : 0;
        reader_unpin(ctx);
        return rid;
    }
    return 0;
}

int qzdb_lookup_ids(qzdb_reader_t* ctx, uint32_t row_id, qzdb_ids_t* out) {
    if (!ctx || !out) return QZDB_ERR_INVALID_PARAM;
    memset(out, 0, sizeof(*out));
    if (row_id == 0) return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    int err;
    if (row_id >= (uint32_t)ctx->row_count) {
        err = QZDB_ERR_INVALID_PARAM;
    } else {
        uint32_t geo_id = 0, asn_id = 0, usage_id = 0;
        err = read_ip_row(ctx, row_id, &geo_id, &asn_id, &usage_id);
        if (err == QZDB_OK) {
            out->geo_id = geo_id; out->asn_id = asn_id; out->usage_id = usage_id;
        }
    }
    reader_unpin(ctx);
    return err;
}

/* === find_str (WARN-8 fix: preserve distinct error codes) === */
int qzdb_find_str(qzdb_reader_t* ctx, const char* ip_str, char* out, size_t out_size) {
    if (!ctx || !ip_str || !out || out_size == 0) {
        if (out && out_size > 0) out[0] = '\0';
        return QZDB_ERR_INVALID_PARAM;
    }
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) { out[0] = '\0'; return QZDB_ERR_INVALID_PARAM; }

    reader_pin(ctx);
    int gi = ctx->group_index;
    uint32_t row_id = 0;
    if (res.is_v4) {
        if (!ctx->has_v4) { out[0] = '\0'; reader_unpin(ctx); return QZDB_ERR_NOT_FOUND; }
        row_id = trie_walk_v4(ctx, res.v4);
    } else {
        if (!ctx->has_v6) { out[0] = '\0'; reader_unpin(ctx); return QZDB_ERR_NOT_FOUND; }
        row_id = trie_walk_v6(ctx, res.v6);
    }
    if (row_id == 0) { out[0] = '\0'; reader_unpin(ctx); return QZDB_ERR_NOT_FOUND; }

    uint32_t entry_id = 0;
    int err = select_entry_id(ctx, row_id, gi, &entry_id);
    if (err != QZDB_OK) { out[0] = '\0'; reader_unpin(ctx); return err; }

    int cnt = 0;
    char* pipe = NULL;
    if (geo_cache_lookup(ctx, gi, entry_id, &cnt, &pipe) && pipe) {
        size_t plen = strlen(pipe);
        if (plen >= out_size) plen = out_size - 1;
        memcpy(out, pipe, plen);
        out[plen] = '\0';
        reader_unpin(ctx);
        return QZDB_OK;
    }

    /* Cache miss / OOM degrade: decode from the already-resolved entry_id. */
    qzdb_geo_info_t info;
    int result = get_geo_info(ctx, entry_id, gi, &info);
    if (result != QZDB_OK) { out[0] = '\0'; reader_unpin(ctx); return result; }
    size_t pos = 0;
    int field_count = ctx->group_field_counts[gi];
    for (int i = 0; i < field_count && i < QZDB_MAX_FIELDS; i++) {
        if (i > 0 && pos < out_size - 1) out[pos++] = '|';
        const char* val = info.values[i] ? info.values[i] : "";
        size_t len = strlen(val);
        if (pos + len >= out_size) { if (out_size > pos) { memcpy(out + pos, val, out_size - pos - 1); pos = out_size - 1; } break; }
        memcpy(out + pos, val, len); pos += len;
    }
    out[pos] = '\0';
    free_geo_info(&info);
    reader_unpin(ctx);
    return QZDB_OK;
}

/* === Caller-buffer query variants === */
int qzdb_find_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int, char** values, char (*bufs)[QZDB_VALUE_BUF_SIZE], int buf_size) {
    if (!ctx || !values || !bufs || !valid_caller_buf_size(buf_size)) return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    int ret;
    if (!ctx->has_v4) {
        ret = QZDB_ERR_NOT_FOUND;
    } else {
        uint32_t row_id = trie_walk_v4(ctx, ip_int);
        if (row_id == 0) {
            ret = 0;
        } else {
            int count = 0;
            int rc = resolve_row_id_buf(ctx, row_id, ctx->group_index, values, bufs, buf_size, &count);
            ret = (rc == QZDB_OK) ? count : rc;
        }
    }
    reader_unpin(ctx);
    return ret;
}

int qzdb_find_v6_buf(qzdb_reader_t* ctx, const uint8_t* ip_bin, char** values, char (*bufs)[QZDB_VALUE_BUF_SIZE], int buf_size) {
    if (!ctx || !ip_bin || !values || !bufs || !valid_caller_buf_size(buf_size)) return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    int ret;
    if (!ctx->has_v6) {
        ret = QZDB_ERR_NOT_FOUND;
    } else {
        uint32_t row_id = trie_walk_v6(ctx, ip_bin);
        if (row_id == 0) {
            ret = 0;
        } else {
            int count = 0;
            int rc = resolve_row_id_buf(ctx, row_id, ctx->group_index, values, bufs, buf_size, &count);
            ret = (rc == QZDB_OK) ? count : rc;
        }
    }
    reader_unpin(ctx);
    return ret;
}

/* === Field projection === */
static int resolve_entry_id_for_row(qzdb_reader_t* ctx, uint32_t row_id, int group_index,
                                    uint32_t* entry_id) {
    if (!ctx || !entry_id || row_id == 0 || row_id >= (uint32_t)ctx->row_count ||
        group_index < 0 || group_index >= ctx->actual_groups) return QZDB_ERR_INVALID_PARAM;
    uint32_t selected = 0;
    int rc = select_entry_id(ctx, row_id, group_index, &selected);
    if (rc != QZDB_OK) return rc;
    if (!ctx->group_entry_counts || selected >= ctx->group_entry_counts[group_index])
        return QZDB_ERR_INVALID_PARAM;
    *entry_id = selected;
    return QZDB_OK;
}

static int projection_field_count(const char** field_names) {
    if (!field_names) return 0;
    int count = 0;
    while (count < QZDB_MAX_FIELDS && field_names[count]) count++;
    if (field_names[count]) return -1;
    return count;
}

static int resolve_row_id_fields(qzdb_reader_t* ctx, uint32_t row_id, int group_index,
                                  const char** field_names, int field_count,
                                  char** values, char (*bufs)[QZDB_VALUE_BUF_SIZE], int buf_size) {
    (void)field_count;
    if (!ctx || !field_names || !values || !bufs || !valid_caller_buf_size(buf_size))
        return QZDB_ERR_INVALID_PARAM;
    int count = projection_field_count(field_names);
    if (count < 0) return QZDB_ERR_INVALID_PARAM;
    for (int i = 0; i < count; i++) values[i] = "";
    if (count == 0) return 0;
    uint32_t entry_id = 0;
    int rc = resolve_entry_id_for_row(ctx, row_id, group_index, &entry_id);
    if (rc != QZDB_OK) return rc;
    for (int i = 0; i < count; i++) {
        int index = field_index_normalized(ctx, field_names[i]);
        if (index < 0) {
            values[i] = "";
            continue;
        }
        rc = decode_geo_field_buf(ctx, entry_id, group_index, index, bufs[i], buf_size, &values[i]);
        if (rc != QZDB_OK) return rc;
    }
    return count;
}

static int find_fields_heap_for_entry(qzdb_reader_t* ctx, uint32_t entry_id, int group_index,
                                      const char** fields, qzdb_geo_info_t* result) {
    int count = projection_field_count(fields);
    if (count < 0) return QZDB_ERR_INVALID_PARAM;
    int indices[QZDB_MAX_FIELDS];
    for (int i = 0; i < count; i++) indices[i] = field_index_normalized(ctx, fields[i]);

    int source_count = 0;
    char* pipe_unused = NULL;
    char** source = geo_cache_lookup(ctx, group_index, entry_id, &source_count, &pipe_unused);
    qzdb_geo_info_t owned;
    int owned_ready = 0;
    if (!source) {
        int rc = get_geo_info(ctx, entry_id, group_index, &owned);
        if (rc != QZDB_OK) return rc;
        source = owned.values;
        source_count = owned.value_count;
        owned_ready = 1;
    }
    geo_info_set_projection(result, count, indices);
    for (int i = 0; i < count; i++) {
        int index = indices[i];
        if (index < 0 || index >= source_count) {
            result->values[i] = "";
            continue;
        }
        char* copy = qzdb_projection_strdup(source[index] ? source[index] : "");
        if (!copy) {
            free_geo_info(result);
            if (owned_ready) free_geo_info(&owned);
            return QZDB_ERR_OUT_OF_MEMORY;
        }
        result->values[i] = copy;
        result->values_mask |= (1ULL << i);
    }
    if (owned_ready) free_geo_info(&owned);
    return QZDB_OK;
}

int qzdb_find_fields_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int,
                               const char** field_names, char** values, char (*bufs)[QZDB_VALUE_BUF_SIZE], int buf_size) {
    if (!ctx || !values || !bufs || !valid_caller_buf_size(buf_size)) return QZDB_ERR_INVALID_PARAM;
    if (!field_names || !field_names[0]) return qzdb_find_uint_buf(ctx, ip_int, values, bufs, buf_size);
    reader_pin(ctx);
    int ret;
    if (!ctx->has_v4) {
        ret = QZDB_ERR_NOT_FOUND;
    } else {
        uint32_t row_id = trie_walk_v4(ctx, ip_int);
        ret = (row_id == 0) ? 0 : resolve_row_id_fields(ctx, row_id, ctx->group_index, field_names, QZDB_MAX_FIELDS, values, bufs, buf_size);
    }
    reader_unpin(ctx);
    return ret;
}

int qzdb_find_fields_buf(qzdb_reader_t* ctx, const char* ip_str,
                          const char** field_names, char** values, char (*bufs)[QZDB_VALUE_BUF_SIZE], int buf_size) {
    if (!ctx || !ip_str || !values || !bufs || !valid_caller_buf_size(buf_size)) return QZDB_ERR_INVALID_PARAM;
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) return QZDB_ERR_INVALID_PARAM;
    if (!field_names || !field_names[0]) {
        if (res.is_v4) return qzdb_find_uint_buf(ctx, res.v4, values, bufs, buf_size);
        return qzdb_find_v6_buf(ctx, res.v6, values, bufs, buf_size);
    }
    if (res.is_v4) return qzdb_find_fields_uint_buf(ctx, res.v4, field_names, values, bufs, buf_size);
    reader_pin(ctx);
    int ret;
    if (!ctx->has_v6) {
        ret = QZDB_ERR_NOT_FOUND;
    } else {
        uint32_t row_id = trie_walk_v6(ctx, res.v6);
        ret = (row_id == 0) ? 0 : resolve_row_id_fields(ctx, row_id, ctx->group_index, field_names, QZDB_MAX_FIELDS, values, bufs, buf_size);
    }
    reader_unpin(ctx);
    return ret;
}

int qzdb_find_fields_uint(qzdb_reader_t* ctx, uint32_t ip_int,
                           const char** fields, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    if (!fields || !fields[0]) return qzdb_find_uint(ctx, ip_int, result);
    reader_pin(ctx);
    int ret;
    if (!ctx->has_v4) {
        ret = QZDB_ERR_NOT_FOUND;
    } else {
        uint32_t row_id = trie_walk_v4(ctx, ip_int);
        if (row_id == 0) {
            ret = QZDB_ERR_NOT_FOUND;
        } else {
            uint32_t entry_id = 0;
            int rc = resolve_entry_id_for_row(ctx, row_id, ctx->group_index, &entry_id);
            ret = (rc != QZDB_OK) ? rc : find_fields_heap_for_entry(ctx, entry_id, ctx->group_index, fields, result);
        }
    }
    reader_unpin(ctx);
    return ret;
}

int qzdb_find_fields(qzdb_reader_t* ctx, const char* ip_str,
                      const char** fields, qzdb_geo_info_t* result) {
    if (!ctx || !ip_str || !result) return QZDB_ERR_INVALID_PARAM;
    if (!fields || !fields[0]) return qzdb_find(ctx, ip_str, result);
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) return QZDB_ERR_INVALID_PARAM;
    if (res.is_v4) return qzdb_find_fields_uint(ctx, res.v4, fields, result);
    reader_pin(ctx);
    int ret;
    if (!ctx->has_v6) {
        ret = QZDB_ERR_NOT_FOUND;
    } else {
        uint32_t row_id = trie_walk_v6(ctx, res.v6);
        if (row_id == 0) {
            ret = QZDB_ERR_NOT_FOUND;
        } else {
            uint32_t entry_id = 0;
            int rc = resolve_entry_id_for_row(ctx, row_id, ctx->group_index, &entry_id);
            ret = (rc != QZDB_OK) ? rc : find_fields_heap_for_entry(ctx, entry_id, ctx->group_index, fields, result);
        }
    }
    reader_unpin(ctx);
    return ret;
}

/* === Fast zero-copy projection implementations === */
int qzdb_resolve_field_indices(qzdb_reader_t* ctx, const char** field_names, int count, int* out_indices) {
    if (!ctx || !field_names || !out_indices || count < 0 || count > QZDB_MAX_FIELDS)
        return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    int ret = QZDB_OK;
    for (int i = 0; i < count; i++) {
        if (!field_names[i]) { ret = QZDB_ERR_INVALID_PARAM; break; }
        int idx = field_index_normalized(ctx, field_names[i]);
        if (idx < 0) { ret = QZDB_ERR_INVALID_PARAM; break; }  /* 严格 fail-closed，杜绝未知字段名 */
        out_indices[i] = idx;
    }
    reader_unpin(ctx);
    return ret;
}

static inline int fast_resolve_entry_base(qzdb_reader_t* ctx, uint32_t row_id, int group,
                                          const uint8_t** out_entry_base, int* out_stride) {
    if (group < 0 || group >= ctx->actual_groups || !ctx->data ||
        !ctx->group_entry_counts || !ctx->group_entry_offsets || !ctx->group_strides ||
        !ctx->group_field_counts || !ctx->group_field_offsets || !ctx->group_field_widths)
        return QZDB_ERR_INVALID_PARAM;
    if (row_id == 0) return 0;
    if (row_id >= (uint32_t)ctx->row_count) return QZDB_ERR_BOUNDS;

    uint32_t entry_id = 0;
    int rc = select_entry_id(ctx, row_id, group, &entry_id);
    if (rc == QZDB_ERR_NOT_FOUND) return 0;
    if (rc != QZDB_OK) return rc;
    if (entry_id >= (uint32_t)ctx->group_entry_counts[group]) return QZDB_ERR_BOUNDS;

    int stride = ctx->group_strides[group];
    if (stride <= 0) return QZDB_ERR_CORRUPTED;
    uint64_t group_off = ctx->group_entry_offsets[group];
    if (ctx->off_geo_entries > UINT64_MAX - group_off) return QZDB_ERR_BOUNDS;
    uint64_t base_off = ctx->off_geo_entries + group_off;
    if (base_off > (uint64_t)ctx->data_size ||
        (uint64_t)entry_id > ((uint64_t)ctx->data_size - base_off) / (uint64_t)stride)
        return QZDB_ERR_BOUNDS;
    uint64_t entry_off = base_off + (uint64_t)entry_id * (uint64_t)stride;
    if ((uint64_t)stride > (uint64_t)ctx->data_size - entry_off) return QZDB_ERR_BOUNDS;

    *out_entry_base = ctx->data + entry_off;
    *out_stride = stride;
    return 1;
}

static inline int resolve_row_id_fields_fast_impl(qzdb_reader_t* ctx, uint32_t row_id, int group,
                                                  const int* indices, int count, const char** out_ptrs) {
    for (int i = 0; i < count; i++) out_ptrs[i] = "";
    const uint8_t* entry_base = NULL;
    int stride = 0;
    int hit = fast_resolve_entry_base(ctx, row_id, group, &entry_base, &stride);
    if (hit <= 0) return hit;

    int group_fc = ctx->group_field_counts[group];
    const int* f_offsets = ctx->group_field_offsets[group];
    const int* f_widths = ctx->group_field_widths[group];
    const int* f_native = ctx->group_field_native[group];
    char*** pools = ctx->group_pools ? ctx->group_pools[group] : NULL;
    const int* pool_cnts = ctx->group_pool_counts ? ctx->group_pool_counts[group] : NULL;

    for (int i = 0; i < count; i++) {
        int idx = indices[i];
        if (idx < 0 || idx >= group_fc) return QZDB_ERR_INVALID_PARAM; /* 非法索引 fail-closed */
        if (f_native && f_native[idx]) {
            out_ptrs[i] = "";
            continue;
        }
        int w = f_widths[idx];
        int off = f_offsets[idx];
        if (w < 1 || w > 4 || off < 0 || (uint64_t)off + (uint64_t)w > (uint64_t)stride)
            return QZDB_ERR_BOUNDS;
        const uint8_t* val_ptr = entry_base + off;
        uint32_t pool_idx = 0;
        if (w == 1) pool_idx = val_ptr[0];
        else if (w == 2) pool_idx = (uint32_t)val_ptr[0] | ((uint32_t)val_ptr[1] << 8);
        else if (w == 3) pool_idx = (uint32_t)val_ptr[0] | ((uint32_t)val_ptr[1] << 8) | ((uint32_t)val_ptr[2] << 16);
        else if (w == 4) pool_idx = (uint32_t)val_ptr[0] | ((uint32_t)val_ptr[1] << 8) | ((uint32_t)val_ptr[2] << 16) | ((uint32_t)val_ptr[3] << 24);

        if (pools && pool_cnts && pools[idx] && pool_idx < (uint32_t)pool_cnts[idx]) {
            const char* s = pools[idx][pool_idx];
            out_ptrs[i] = s ? s : "";
        } else {
            out_ptrs[i] = "";
        }
    }
    return count;
}

static inline int resolve_row_id_values_fast_impl(qzdb_reader_t* ctx, uint32_t row_id, int group,
                                                  const int* indices, int count, qzdb_value_t* out_values) {
    for (int i = 0; i < count; i++) {
        out_values[i].type = QZDB_TYPE_STRING;
        out_values[i].has_value = 0;
        out_values[i].val.str.ptr = "";
        out_values[i].val.str.len = 0;
    }
    const uint8_t* entry_base = NULL;
    int stride = 0;
    int hit = fast_resolve_entry_base(ctx, row_id, group, &entry_base, &stride);
    if (hit <= 0) return hit;

    int group_fc = ctx->group_field_counts[group];
    const int* f_offsets = ctx->group_field_offsets[group];
    const int* f_widths = ctx->group_field_widths[group];
    const int* f_native = ctx->group_field_native[group];
    const int* f_native_type = ctx->group_field_native_type[group];
    char*** pools = ctx->group_pools ? ctx->group_pools[group] : NULL;
    const int* pool_cnts = ctx->group_pool_counts ? ctx->group_pool_counts[group] : NULL;

    for (int i = 0; i < count; i++) {
        int idx = indices[i];
        if (idx < 0 || idx >= group_fc) return QZDB_ERR_INVALID_PARAM;
        int w = f_widths[idx];
        int off = f_offsets[idx];
        if (w < 1 || w > 8 || off < 0 || (uint64_t)off + (uint64_t)w > (uint64_t)stride)
            return QZDB_ERR_BOUNDS;
        const uint8_t* val_ptr = entry_base + off;

        if (f_native && f_native[idx]) {
            int ntype = f_native_type ? f_native_type[idx] : 0;
            if (ntype == 1) {
                /* 原生浮点 (float32 / float64) */
                out_values[i].type = QZDB_TYPE_FLOAT;
                if (w == 4) {
                    uint32_t bits = (uint32_t)val_ptr[0] | ((uint32_t)val_ptr[1] << 8) | ((uint32_t)val_ptr[2] << 16) | ((uint32_t)val_ptr[3] << 24);
                    union { uint32_t u; float f; } uv;
                    uv.u = bits;
                    out_values[i].val.f64 = (double)uv.f;
                    out_values[i].has_value = 1;
                } else if (w == 8) {
                    uint64_t bits = 0;
                    for (int k = 0; k < 8; k++) bits |= ((uint64_t)val_ptr[k] << (k * 8));
                    union { uint64_t u; double d; } uv;
                    uv.u = bits;
                    out_values[i].val.f64 = uv.d;
                    out_values[i].has_value = 1;
                } else {
                    return QZDB_ERR_CORRUPTED;
                }
            } else {
                /* 原生整型 (如 ASN uint32) */
                if (w > 4) return QZDB_ERR_CORRUPTED;
                out_values[i].type = QZDB_TYPE_UINT;
                uint32_t uval = 0;
                if (w == 1) uval = val_ptr[0];
                else if (w == 2) uval = (uint32_t)val_ptr[0] | ((uint32_t)val_ptr[1] << 8);
                else if (w == 3) uval = (uint32_t)val_ptr[0] | ((uint32_t)val_ptr[1] << 8) | ((uint32_t)val_ptr[2] << 16);
                else if (w == 4) uval = (uint32_t)val_ptr[0] | ((uint32_t)val_ptr[1] << 8) | ((uint32_t)val_ptr[2] << 16) | ((uint32_t)val_ptr[3] << 24);
                out_values[i].val.u32 = uval;
                out_values[i].has_value = 1;
            }
        } else {
            /* 字符串池引用 */
            if (w > 4) return QZDB_ERR_CORRUPTED;
            out_values[i].type = QZDB_TYPE_STRING;
            uint32_t pool_idx = 0;
            if (w == 1) pool_idx = val_ptr[0];
            else if (w == 2) pool_idx = (uint32_t)val_ptr[0] | ((uint32_t)val_ptr[1] << 8);
            else if (w == 3) pool_idx = (uint32_t)val_ptr[0] | ((uint32_t)val_ptr[1] << 8) | ((uint32_t)val_ptr[2] << 16);
            else if (w == 4) pool_idx = (uint32_t)val_ptr[0] | ((uint32_t)val_ptr[1] << 8) | ((uint32_t)val_ptr[2] << 16) | ((uint32_t)val_ptr[3] << 24);

            if (pools && pool_cnts && pools[idx] && pool_idx < (uint32_t)pool_cnts[idx]) {
                const char* s = pools[idx][pool_idx];
                if (s && s[0] != '\0') {
                    out_values[i].val.str.ptr = s;
                    out_values[i].val.str.len = strlen(s);
                    out_values[i].has_value = 1;
                } else {
                    out_values[i].val.str.ptr = "";
                    out_values[i].val.str.len = 0;
                    out_values[i].has_value = 0;
                }
            } else {
                out_values[i].val.str.ptr = "";
                out_values[i].val.str.len = 0;
                out_values[i].has_value = 0;
            }
        }
    }
    return count;
}

int qzdb_find_values_uint_fast(qzdb_reader_t* ctx, uint32_t ip_int,
                                const int* indices, int count, qzdb_value_t* out_values) {
    if (!ctx || !indices || !out_values || count < 0 || count > QZDB_MAX_FIELDS)
        return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    int ret;
    if (!ctx->has_v4) {
        ret = QZDB_ERR_NOT_FOUND;
    } else {
        uint32_t row_id = trie_walk_v4(ctx, ip_int);
        ret = resolve_row_id_values_fast_impl(ctx, row_id, ctx->group_index, indices, count, out_values);
    }
    reader_unpin(ctx);
    return ret;
}

int qzdb_find_values_v6_fast(qzdb_reader_t* ctx, const uint8_t* ip_bin,
                              const int* indices, int count, qzdb_value_t* out_values) {
    if (!ctx || !ip_bin || !indices || !out_values || count < 0 || count > QZDB_MAX_FIELDS)
        return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    int ret;
    if (!ctx->has_v6) {
        ret = QZDB_ERR_NOT_FOUND;
    } else {
        uint32_t row_id = trie_walk_v6(ctx, ip_bin);
        ret = resolve_row_id_values_fast_impl(ctx, row_id, ctx->group_index, indices, count, out_values);
    }
    reader_unpin(ctx);
    return ret;
}

int qzdb_find_values_fast(qzdb_reader_t* ctx, const char* ip_str,
                           const int* indices, int count, qzdb_value_t* out_values) {
    if (!ctx || !ip_str || !indices || !out_values || count < 0 || count > QZDB_MAX_FIELDS)
        return QZDB_ERR_INVALID_PARAM;
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) return QZDB_ERR_INVALID_PARAM;
    if (res.is_v4) return qzdb_find_values_uint_fast(ctx, res.v4, indices, count, out_values);
    return qzdb_find_values_v6_fast(ctx, res.v6, indices, count, out_values);
}

int qzdb_find_fields_uint_fast(qzdb_reader_t* ctx, uint32_t ip_int,
                                const int* indices, int count, const char** out_ptrs) {
    if (!ctx || !indices || !out_ptrs || count < 0 || count > QZDB_MAX_FIELDS)
        return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    int ret;
    if (!ctx->has_v4) {
        for (int i = 0; i < count; i++) out_ptrs[i] = "";
        ret = QZDB_ERR_NOT_FOUND;
    } else {
        uint32_t row_id = trie_walk_v4(ctx, ip_int);
        ret = resolve_row_id_fields_fast_impl(ctx, row_id, ctx->group_index, indices, count, out_ptrs);
    }
    reader_unpin(ctx);
    return ret;
}

int qzdb_find_fields_v6_fast(qzdb_reader_t* ctx, const uint8_t* ip_bin,
                              const int* indices, int count, const char** out_ptrs) {
    if (!ctx || !ip_bin || !indices || !out_ptrs || count < 0 || count > QZDB_MAX_FIELDS)
        return QZDB_ERR_INVALID_PARAM;
    reader_pin(ctx);
    int ret;
    if (!ctx->has_v6) {
        for (int i = 0; i < count; i++) out_ptrs[i] = "";
        ret = QZDB_ERR_NOT_FOUND;
    } else {
        uint32_t row_id = trie_walk_v6(ctx, ip_bin);
        ret = resolve_row_id_fields_fast_impl(ctx, row_id, ctx->group_index, indices, count, out_ptrs);
    }
    reader_unpin(ctx);
    return ret;
}

int qzdb_find_fields_fast(qzdb_reader_t* ctx, const char* ip_str,
                           const int* indices, int count, const char** out_ptrs) {
    if (!ctx || !ip_str || !indices || !out_ptrs || count < 0 || count > QZDB_MAX_FIELDS)
        return QZDB_ERR_INVALID_PARAM;
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) {
        for (int i = 0; i < count; i++) out_ptrs[i] = "";
        return QZDB_ERR_INVALID_PARAM;
    }
    if (res.is_v4) return qzdb_find_fields_uint_fast(ctx, res.v4, indices, count, out_ptrs);
    return qzdb_find_fields_v6_fast(ctx, res.v6, indices, count, out_ptrs);
}

uint32_t qzdb_get_generation(const qzdb_reader_t* ctx) {
    return ctx ? ctx->generation : 0;
}

/* ========================================================================
 * Warmup: pre-fault the mmap'd data so the first queries don't take page
 * faults. Touches one byte per 4 KiB page across the V4/V6 jump tables and
 * node segments. Safe: all ranges are clamped to data_size, and the read
 * result is folded into touch_sum only to keep the loads observable (the
 * compiler cannot elide them), then discarded.
 * ======================================================================== */
void qzdb_warmup(qzdb_reader_t* reader) {
    if (reader == NULL) return;
    reader_pin(reader);
    if (reader->data == NULL || reader->data_size == 0) { reader_unpin(reader); return; }
    const size_t page = 4096;
    uint8_t touch_sum = 0;
    if (reader->has_v4 && reader->off_v4_jump > 0) {
        size_t start = (size_t)reader->off_v4_jump;
        size_t end = start + 65536UL * 4;
        if (end > reader->data_size) end = reader->data_size;
        for (size_t p = start; p < end; p += page) touch_sum ^= reader->data[p];
        if (reader->v4_node_count > 0 && reader->off_v4_nodes > 0) {
            size_t node_size = reader->v4_node_24 ? 6 : 8;
            size_t ns = (size_t)reader->off_v4_nodes;
            size_t ne = ns + (size_t)reader->v4_node_count * node_size;
            if (ne > reader->data_size) ne = reader->data_size;
            for (size_t p = ns; p < ne; p += page) touch_sum ^= reader->data[p];
        }
    }
    if (reader->has_v6 && reader->off_v6_jump > 0) {
        size_t start = (size_t)reader->off_v6_jump;
        size_t end = start + ((size_t)1 << reader->v6_jump_bits) * 4;
        if (end > reader->data_size) end = reader->data_size;
        for (size_t p = start; p < end; p += page) touch_sum ^= reader->data[p];
        if (reader->v6_node_count > 0 && reader->off_v6_nodes > 0) {
            size_t node_size = reader->v6_node_24 ? 6 : 8;
            size_t ns = (size_t)reader->off_v6_nodes;
            size_t ne = ns + (size_t)reader->v6_node_count * node_size;
            if (ne > reader->data_size) ne = reader->data_size;
            for (size_t p = ns; p < ne; p += page) touch_sum ^= reader->data[p];
        }
    }
    (void)touch_sum;
    reader_unpin(reader);
}
