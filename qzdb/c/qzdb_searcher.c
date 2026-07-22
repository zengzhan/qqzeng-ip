#define _DARWIN_C_SOURCE
#include "qzdb_searcher.h"
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

static const char* error_messages[] = {
    "Success",
    "Not found",
    "Corrupted data",
    "Out of memory",
    "Invalid parameter",
    "Bad header",
    "Bad magic",
    "Unsupported format",
    "Bounds check failed"
};

const char* qzdb_strerror(int error_code) {
    if (error_code >= 0 && error_code < (int)(sizeof(error_messages) / sizeof(error_messages[0]))) {
        return error_messages[error_code];
    }
    return "Unknown error";
}

static uint32_t crc32_table[256];
static int crc32_ready = 0;
static pthread_mutex_t g_instance_mutex = PTHREAD_MUTEX_INITIALIZER;
static void ensure_pools_loaded(qzdb_searcher_t* ctx);

static void crc32_init(void) {
    for (uint32_t i = 0; i < 256; i++) {
        uint32_t c = i;
        for (int j = 0; j < 8; j++) {
            if (c & 1) c = (c >> 1) ^ 0xEDB88320;
            else c >>= 1;
        }
        crc32_table[i] = c;
    }
    crc32_ready = 1;
}

static uint32_t crc32_update(uint32_t crc, const uint8_t* buf, size_t len) {
    if (!crc32_ready) crc32_init();
    for (size_t i = 0; i < len; i++) {
        crc = crc32_table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
    }
    return crc;
}

/* CRC over whole file with the 4-byte CRC field treated as zero — never mutates mapped memory. */
static uint32_t crc32_compute_file(const uint8_t* d, size_t size) {
    if (size < 20) return 0;
    uint32_t crc = 0xFFFFFFFF;
    crc = crc32_update(crc, d, 16);              /* [0, 16) */
    uint8_t zeros[4] = {0, 0, 0, 0};
    crc = crc32_update(crc, zeros, 4);           /* CRC field counted as zero */
    crc = crc32_update(crc, d + 20, size - 20);  /* [20, end) */
    return crc ^ 0xFFFFFFFF;
}

/* SEC-02: Safe read helpers with bounds checking */
static int safe_read_u16(const uint8_t* data, size_t data_size, uint64_t off, uint16_t* out) {
    if (off + 2 > data_size) return QZDB_ERR_BOUNDS;
    *out = (uint16_t)data[off] | ((uint16_t)data[off + 1] << 8);
    return QZDB_OK;
}

static int safe_read_u24(const uint8_t* data, size_t data_size, uint64_t off, uint32_t* out) {
    if (off + 3 > data_size) return QZDB_ERR_BOUNDS;
    *out = (uint32_t)data[off] | ((uint32_t)data[off + 1] << 8) | ((uint32_t)data[off + 2] << 16);
    return QZDB_OK;
}

static int safe_read_u32(const uint8_t* data, size_t data_size, uint64_t off, uint32_t* out) {
    if (off + 4 > data_size) return QZDB_ERR_BOUNDS;
    *out = (uint32_t)data[off] | ((uint32_t)data[off + 1] << 8) | ((uint32_t)data[off + 2] << 16) | ((uint32_t)data[off + 3] << 24);
    return QZDB_OK;
}

static int safe_read_u64(const uint8_t* data, size_t data_size, uint64_t off, uint64_t* out) {
    if (off + 8 > data_size) return QZDB_ERR_BOUNDS;
    uint32_t lo, hi;
    int r1 = safe_read_u32(data, data_size, off, &lo);
    int r2 = safe_read_u32(data, data_size, off + 4, &hi);
    if (r1 != QZDB_OK || r2 != QZDB_OK) return QZDB_ERR_BOUNDS;
    *out = (uint64_t)lo | ((uint64_t)hi << 32);
    return QZDB_OK;
}

/* Legacy macros for header parsing (already bounds-validated in qzdb_init) */
#define READ_LE16(p) ((uint16_t)(p)[0] | ((uint16_t)(p)[1] << 8))
#define READ_LE32(p) ((uint32_t)(p)[0] | ((uint32_t)(p)[1] << 8) | ((uint32_t)(p)[2] << 16) | ((uint32_t)(p)[3] << 24))
#define READ_LE64(p) ((uint64_t)READ_LE32(p) | ((uint64_t)READ_LE32((p) + 4) << 32))
#define READ_LE48(p) ((uint64_t)READ_LE32(p) | ((uint64_t)(p)[4] << 32) | ((uint64_t)(p)[5] << 40))

static int safe_read_uint_width(const uint8_t* data, size_t data_size, uint64_t off, int w, uint32_t* out) {
    if (w <= 1) {
        if (off >= data_size) return QZDB_ERR_BOUNDS;
        *out = data[off];
    } else if (w == 2) {
        uint16_t v;
        int r = safe_read_u16(data, data_size, off, &v);
        if (r != QZDB_OK) return r;
        *out = v;
    } else if (w == 3) {
        return safe_read_u24(data, data_size, off, out);
    } else {
        return safe_read_u32(data, data_size, off, out);
    }
    return QZDB_OK;
}

int qzdb_init(qzdb_searcher_t* ctx, const char* db_path) {
    if (!ctx || !db_path) return QZDB_ERR_INVALID_PARAM;
    memset(ctx, 0, sizeof(*ctx));
    setlocale(LC_NUMERIC, "C");
    int fd = open(db_path, O_RDONLY);
    if (fd < 0) return QZDB_ERR_CORRUPTED;
    struct stat st;
    if (fstat(fd, &st) != 0) {
        close(fd);
        return QZDB_ERR_CORRUPTED;
    }
    ctx->data_size = st.st_size;
    ctx->data = mmap(NULL, ctx->data_size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (ctx->data == MAP_FAILED) {
        ctx->data = NULL;
        return QZDB_ERR_OUT_OF_MEMORY;
    }
    madvise(ctx->data, ctx->data_size, MADV_RANDOM);

    uint8_t* d = ctx->data;
    if (ctx->data_size < 192) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BAD_HEADER;
    }
    if (memcmp(d, "QZDB", 4) != 0) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BAD_MAGIC;
    }

    int fmt_ver = d[4];
    if (fmt_ver < 1 || fmt_ver > 6) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_UNSUPPORTED;
    }

    ctx->flags = READ_LE16(d + 8);
    ctx->has_v4 = (ctx->flags & 1) != 0;
    ctx->has_v6 = (ctx->flags & 2) != 0;
    ctx->v4_node_24 = (ctx->flags & 0x10) != 0;
    ctx->v6_node_24 = (ctx->flags & 0x20) != 0;

    ctx->v6_jump_bits = d[11];
    if (ctx->v6_jump_bits == 0) ctx->v6_jump_bits = 16;
    if (ctx->v6_jump_bits < 16 || ctx->v6_jump_bits > 20) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BAD_HEADER;
    }

    ctx->pool_count = d[12];
    ctx->pool_idx_size = d[13];
    if (ctx->pool_idx_size != 2 && ctx->pool_idx_size != 3) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BAD_HEADER;
    }
    ctx->geo_count = READ_LE16(d + 14);
    ctx->row_count = READ_LE32(d + 20);
    ctx->v4_rec_count = READ_LE32(d + 24);
    ctx->v6_rec_count = READ_LE32(d + 28);

    uint32_t hs = READ_LE32(d + 36);
    if (hs != 192) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_CORRUPTED;
    }

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
    if (ctx->ip_row_size < 1 || ctx->ip_row_size > 64) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BAD_HEADER;
    }
    ctx->geo_entry_group_count = READ_LE32(d + 164);
    if (ctx->geo_entry_group_count < 1 || ctx->geo_entry_group_count > 255) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BAD_HEADER;
    }

    // Bounds validation for section offsets
    {
        uint64_t v4_ns = ctx->v4_node_24 ? 6 : 8;
        uint64_t v6_ns = ctx->v6_node_24 ? 6 : 8;
        uint64_t v6_jump_size = ((uint64_t)1 << ctx->v6_jump_bits) * 4;

        if (ctx->off_v4_jump > 0 && ctx->off_v4_jump + 65536 * 4 > ctx->data_size) {
            munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BOUNDS;
        }
        if (ctx->off_v4_nodes > 0 && ctx->off_v4_nodes + (uint64_t)ctx->v4_node_count * v4_ns > ctx->data_size) {
            munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BOUNDS;
        }
        if (ctx->off_v6_jump > 0 && ctx->off_v6_jump + v6_jump_size > ctx->data_size) {
            munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BOUNDS;
        }
        if (ctx->off_v6_nodes > 0 && ctx->off_v6_nodes + (uint64_t)ctx->v6_node_count * v6_ns > ctx->data_size) {
            munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BOUNDS;
        }
        if (ctx->off_ip_row > 0 && ctx->off_ip_row + (uint64_t)ctx->row_count * ctx->ip_row_size > ctx->data_size) {
            munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BOUNDS;
        }
        if (ctx->off_geo_entries > 0 && ctx->off_geo_entries >= ctx->data_size) {
            munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BOUNDS;
        }
        if (ctx->off_pools > 0 && ctx->off_pools >= ctx->data_size) {
            munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BOUNDS;
        }
        if (ctx->off_meta > 0 && ctx->off_meta > ctx->data_size) {
            munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_BOUNDS;
        }
    }

    ctx->group_entry_offsets = malloc(4 * sizeof(uint64_t));
    if (!ctx->group_entry_offsets) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_OUT_OF_MEMORY;
    }
    for (int i = 0; i < 4; i++) {
        ctx->group_entry_offsets[i] = READ_LE48(d + 168 + i * 6);
    }

    uint64_t gm_off = ctx->off_geo_entries;
    int group_count = d[gm_off];
    gm_off++;

    ctx->actual_groups = group_count < 1 ? 1 : group_count;
    if (ctx->geo_entry_group_count > 0 && ctx->geo_entry_group_count < ctx->actual_groups) {
        ctx->actual_groups = ctx->geo_entry_group_count;
    }
    if (ctx->actual_groups > 4) ctx->actual_groups = 4;

    ctx->group_field_counts = malloc(ctx->actual_groups * sizeof(int));
    ctx->group_entry_counts = malloc(ctx->actual_groups * sizeof(uint32_t));
    ctx->group_dim_masks = malloc(ctx->actual_groups * sizeof(uint16_t));
    if (!ctx->group_field_counts || !ctx->group_entry_counts || !ctx->group_dim_masks) {
        free(ctx->group_field_counts); free(ctx->group_entry_counts); free(ctx->group_dim_masks);
        free(ctx->group_entry_offsets);
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return QZDB_ERR_OUT_OF_MEMORY;
    }

    for (int gi = 0; gi < ctx->actual_groups; gi++) {
        ctx->group_field_counts[gi] = d[gm_off];
        gm_off++;
        if (fmt_ver == 1 || fmt_ver >= 4) {
            ctx->group_entry_counts[gi] = READ_LE32(d + gm_off);
            gm_off += 4;
        } else {
            ctx->group_entry_counts[gi] = READ_LE16(d + gm_off);
            gm_off += 2;
        }
        if (fmt_ver == 1 || fmt_ver >= 3) {
            ctx->group_dim_masks[gi] = READ_LE16(d + gm_off);
            gm_off += 2;
        } else {
            ctx->group_dim_masks[gi] = (gi != 2) ? 0x01 : 0x02;
        }
    }

    ctx->group_strides = calloc(ctx->actual_groups, sizeof(int));
    ctx->group_field_widths = calloc(ctx->actual_groups, sizeof(int*));
    ctx->group_field_offsets = calloc(ctx->actual_groups, sizeof(int*));
    ctx->group_field_native = calloc(ctx->actual_groups, sizeof(int*));
    ctx->group_field_native_type = calloc(ctx->actual_groups, sizeof(int*));
    ctx->group_field_ids = calloc(ctx->actual_groups, sizeof(uint16_t*));
    ctx->group_pool_section_ids = calloc(ctx->actual_groups, sizeof(uint32_t*));

    if (ctx->off_group_schema > 0) {
        uint64_t sp = ctx->off_group_schema;
        int gs_group_count = READ_LE16(d + sp);
        sp += 2;
        int max_gs_groups = gs_group_count < ctx->actual_groups ? gs_group_count : ctx->actual_groups;
    for (int gi = 0; gi < max_gs_groups; gi++) {
        sp += 2; // skip groupId
        int fld_count = READ_LE16(d + sp);
        sp += 2;
        sp += 4; // skip entryCount
        int stride = READ_LE32(d + sp);
        sp += 4;
        sp += 4; // skip flags

        if (gi < ctx->actual_groups) {
            ctx->group_strides[gi] = stride;
            ctx->group_field_widths[gi] = malloc(fld_count * sizeof(int));
            ctx->group_field_offsets[gi] = malloc(fld_count * sizeof(int));
            ctx->group_field_native[gi] = malloc(fld_count * sizeof(int));
            ctx->group_field_native_type[gi] = malloc(fld_count * sizeof(int));
            ctx->group_field_ids[gi] = malloc(fld_count * sizeof(uint16_t));
            ctx->group_pool_section_ids[gi] = malloc(fld_count * sizeof(uint32_t));
            for (int fi = 0; fi < fld_count; fi++) {
                ctx->group_field_ids[gi][fi] = READ_LE16(d + sp);
                sp += 2;
                ctx->group_field_widths[gi][fi] = d[sp];
                sp++;
                int field_flags = d[sp];
                sp++;
                ctx->group_field_native[gi][fi] = (field_flags & 0x01) != 0;
                ctx->group_field_native_type[gi][fi] = (field_flags >> 1) & 0x03;
                ctx->group_field_offsets[gi][fi] = READ_LE32(d + sp);
                sp += 4;
                ctx->group_pool_section_ids[gi][fi] = READ_LE32(d + sp);
                sp += 4;
            }
        } else {
            sp += fld_count * 12;
        }
    }
    }

    for (int g = 0; g < ctx->actual_groups; g++) {
        if (ctx->group_strides[g] == 0) {
            ctx->group_strides[g] = ctx->group_field_counts[g] * ctx->pool_idx_size;
        }
        if (!ctx->group_field_widths[g]) {
            ctx->group_field_widths[g] = malloc(ctx->group_field_counts[g] * sizeof(int));
            for (int i = 0; i < ctx->group_field_counts[g]; i++) {
                ctx->group_field_widths[g][i] = ctx->pool_idx_size;
            }
        }
        if (!ctx->group_field_offsets[g]) {
            ctx->group_field_offsets[g] = malloc(ctx->group_field_counts[g] * sizeof(int));
            for (int i = 0; i < ctx->group_field_counts[g]; i++) {
                ctx->group_field_offsets[g][i] = i * ctx->pool_idx_size;
            }
        }
        if (!ctx->group_field_native[g]) {
            ctx->group_field_native[g] = calloc(ctx->group_field_counts[g], sizeof(int));
        }
        if (!ctx->group_field_native_type[g]) {
            ctx->group_field_native_type[g] = calloc(ctx->group_field_counts[g], sizeof(int));
        }
    }

    // Dynamic field names from meta
    ctx->field_names = NULL;
    ctx->float_field_flags = NULL;
    ctx->version_name = NULL;
    if (ctx->flags & 4 && ctx->off_meta > 0 && ctx->off_meta + 4 <= ctx->data_size) {
        uint64_t pos = ctx->off_meta;
        while (pos + 4 <= ctx->data_size) {
            int t = d[pos];
            int length = READ_LE16(d + pos + 2);
            if (t == 0 || length == 0) break;
            if (pos + 4 + length > ctx->data_size) break;
            char* val = malloc(length + 1);
            memcpy(val, d + pos + 4, length);
            val[length] = '\0';
            if (t == 1) {
                ctx->version_name = val;
            } else if (t == 2) {
                ctx->field_names = calloc(ctx->group_field_counts[0], sizeof(char*));
                ctx->float_field_flags = calloc(ctx->group_field_counts[0], sizeof(int));
                ctx->field_count = ctx->group_field_counts[0];
                int idx = 0;
                const char* p = val;
                while (idx < ctx->group_field_counts[0]) {
                    const char* start = p;
                    while (*p && *p != '|') p++;
                    size_t tok_len = (size_t)(p - start);
                    char* token = malloc(tok_len + 1);
                    if (!token) break;
                    memcpy(token, start, tok_len);
                    token[tok_len] = '\0';
                    ctx->field_names[idx] = token;
                    if (strcmp(token, "longitude") == 0 || strcmp(token, "latitude") == 0) {
                        ctx->float_field_flags[idx] = 1;
                    }
                    idx++;
                    if (*p == '|') p++;
                    else break;
                }
                free(val);
            } else {
                free(val);
            }
            pos += 4 + length;
        }
    }

    if (!ctx->field_names) {
        ctx->field_names = calloc(ctx->group_field_counts[0], sizeof(char*));
        ctx->float_field_flags = calloc(ctx->group_field_counts[0], sizeof(int));
        ctx->field_count = ctx->group_field_counts[0];
        for (int i = 0; i < ctx->group_field_counts[0]; i++) {
            char buf[32];
            snprintf(buf, sizeof(buf), "field_%d", i);
            ctx->field_names[i] = strdup(buf);
        }
    }

    ctx->pools_loaded = 0;
    ctx->group_pools = NULL;
    ctx->group_pool_counts = NULL;
    ensure_pools_loaded(ctx);

    switch (ctx->pool_count) {
        case 6: ctx->version_code = 1; break;
        case 7: ctx->version_code = 2; break;
        case 25: ctx->version_code = 3; break;
        default: ctx->version_code = 3; break;
    }

    return QZDB_OK;
}

static void ensure_pools_loaded(qzdb_searcher_t* ctx) {
    if (ctx->pools_loaded) return;
    ctx->pools_loaded = 1;

    ctx->group_pools = calloc(ctx->actual_groups, sizeof(char***));
    ctx->group_pool_counts = calloc(ctx->actual_groups, sizeof(int*));
    ctx->pool_arena = NULL;

    if (ctx->off_pools <= 0) return;

    uint64_t pool_cursor = ctx->off_pools;
    uint64_t pool_end = ctx->off_meta > 0 ? ctx->off_meta : ctx->data_size;
    uint8_t* d = ctx->data;

    /* Pass 1: measure total null-terminated string bytes + build pointer tables */
    size_t arena_need = 0;
    typedef struct {
        uint32_t count;
        uint32_t* offsets;
        uint64_t data_base; /* absolute offset of string blob in file */
    } pool_scan_t;

    pool_scan_t** scans = calloc(ctx->actual_groups, sizeof(pool_scan_t*));
    if (!scans) return;

    for (int g = 0; g < ctx->actual_groups; g++) {
        int field_count = ctx->group_field_counts[g];
        ctx->group_pools[g] = calloc(field_count, sizeof(char**));
        ctx->group_pool_counts[g] = calloc(field_count, sizeof(int));
        scans[g] = calloc(field_count, sizeof(pool_scan_t));

        for (int f = 0; f < field_count; f++) {
            if (ctx->group_field_native[g][f]) {
                continue;
            }
            if (pool_cursor + 4 > pool_end) {
                continue;
            }
            uint32_t count;
            if (safe_read_u32(d, ctx->data_size, pool_cursor, &count) != QZDB_OK) break;
            pool_cursor += 4;
            if (ctx->off_row_schema > 0) {
                pool_cursor += 4;
            }
            if (count == 0 || count > 16000000) {
                continue;
            }

            ctx->group_pool_counts[g][f] = (int)count;
            uint32_t* offsets = malloc((count + 1) * sizeof(uint32_t));
            if (!offsets) continue;
            int offsets_ok = 1;
            for (uint32_t o = 0; o <= count; o++) {
                if (safe_read_u32(d, ctx->data_size, pool_cursor, &offsets[o]) != QZDB_OK) {
                    offsets_ok = 0;
                    break;
                }
                pool_cursor += 4;
            }
            if (!offsets_ok) {
                free(offsets);
                continue;
            }

            scans[g][f].count = count;
            scans[g][f].offsets = offsets;
            scans[g][f].data_base = pool_cursor;

            ctx->group_pools[g][f] = calloc(count, sizeof(char*));
            for (uint32_t s = 0; s < count; s++) {
                uint32_t start = offsets[s];
                uint32_t end = offsets[s + 1];
                if (end < start || pool_cursor + end > ctx->data_size) {
                    continue;
                }
                arena_need += (size_t)(end - start) + 1; /* + NUL */
            }
            pool_cursor += offsets[count];
        }
    }

    /* Pass 2: single arena allocation, copy all strings once */
    char* arena = NULL;
    size_t arena_off = 0;
    if (arena_need > 0) {
        arena = malloc(arena_need);
        if (!arena) {
            for (int g = 0; g < ctx->actual_groups; g++) {
                if (!scans[g]) continue;
                for (int f = 0; f < ctx->group_field_counts[g]; f++) {
                    free(scans[g][f].offsets);
                }
                free(scans[g]);
            }
            free(scans);
            return;
        }
        ctx->pool_arena = arena;
    }

    for (int g = 0; g < ctx->actual_groups; g++) {
        if (!scans[g]) continue;
        int field_count = ctx->group_field_counts[g];
        for (int f = 0; f < field_count; f++) {
            pool_scan_t* sc = &scans[g][f];
            if (!sc->offsets || !ctx->group_pools[g][f]) {
                free(sc->offsets);
                continue;
            }
            for (uint32_t s = 0; s < sc->count; s++) {
                uint32_t start = sc->offsets[s];
                uint32_t end = sc->offsets[s + 1];
                if (end < start || sc->data_base + end > ctx->data_size) {
                    ctx->group_pools[g][f][s] = NULL;
                    continue;
                }
                uint32_t length = end - start;
                char* dst = arena + arena_off;
                if (length > 0) {
                    memcpy(dst, d + sc->data_base + start, length);
                }
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

static uint32_t get_v4_child(const qzdb_searcher_t* ctx, uint32_t node_idx, uint32_t bit) {
    if (node_idx >= ctx->v4_node_count) return 0;
    if (ctx->v4_node_24) {
        uint64_t node_offset = ctx->off_v4_nodes + (uint64_t)node_idx * 6;
        uint64_t offset = bit == 0 ? node_offset : node_offset + 3;
        uint32_t val;
        if (safe_read_u24(ctx->data, ctx->data_size, offset, &val) != QZDB_OK) return 0;
        if (val & 0x800000u) {
            return (val & QZDB_SENTINEL_MASK_24) | QZDB_SENTINEL;
        }
        return val;
    } else {
        uint64_t child_off = ctx->off_v4_nodes + (uint64_t)node_idx * 8 + (uint64_t)bit * 4;
        uint32_t val;
        if (safe_read_u32(ctx->data, ctx->data_size, child_off, &val) != QZDB_OK) return 0;
        return val;
    }
}

static uint32_t get_v6_child(const qzdb_searcher_t* ctx, uint32_t node_idx, uint32_t bit) {
    if (node_idx >= ctx->v6_node_count) return 0;
    if (ctx->v6_node_24) {
        uint64_t node_offset = ctx->off_v6_nodes + (uint64_t)node_idx * 6;
        uint64_t offset = bit == 0 ? node_offset : node_offset + 3;
        uint32_t val;
        if (safe_read_u24(ctx->data, ctx->data_size, offset, &val) != QZDB_OK) return 0;
        if (val & 0x800000u) {
            return (val & QZDB_SENTINEL_MASK_24) | QZDB_SENTINEL;
        }
        return val;
    } else {
        uint64_t child_off = ctx->off_v6_nodes + (uint64_t)node_idx * 8 + (uint64_t)bit * 4;
        uint32_t val;
        if (safe_read_u32(ctx->data, ctx->data_size, child_off, &val) != QZDB_OK) return 0;
        return val;
    }
}

static uint32_t trie_walk_v4(const qzdb_searcher_t* ctx, uint32_t ip_int) {
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

static uint32_t trie_walk_v6(const qzdb_searcher_t* ctx, const uint8_t* ip_bin) {
    int v6_jump_bits = ctx->v6_jump_bits;
    uint32_t idx_jump = 0;
    int bits_collected = 0;
    for (int i = 0; i < 16; i++) {
        uint8_t b = ip_bin[i];
        int bits_left = v6_jump_bits - bits_collected;
        if (bits_left <= 0) break;
        if (bits_left >= 8) {
            idx_jump = (idx_jump << 8) | b;
            bits_collected += 8;
        } else {
            idx_jump = (idx_jump << bits_left) | (b >> (8 - bits_left));
            bits_collected += bits_left;
            break;
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

static int get_geo_info(qzdb_searcher_t* ctx, uint32_t entry_id, int group_index, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    if (group_index < 0 || group_index >= ctx->actual_groups) return QZDB_ERR_INVALID_PARAM;
    if (entry_id >= ctx->group_entry_counts[group_index]) return QZDB_ERR_INVALID_PARAM;

    int field_count = ctx->group_field_counts[group_index];
    if (field_count <= 0) return QZDB_ERR_CORRUPTED;

    uint64_t group_entry_start = ctx->off_geo_entries + ctx->group_entry_offsets[group_index];
    int stride = ctx->group_strides[group_index];
    uint64_t entry_offset = group_entry_start + (uint64_t)entry_id * stride;

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
                    uint32_t bits;
                    if (safe_read_u32(ctx->data, ctx->data_size, fo, &bits) != QZDB_OK) return QZDB_ERR_BOUNDS;
                    union { uint32_t u; float f; } u;
                    u.u = bits;
                    snprintf(buf, sizeof(buf), "%.6f", u.f);
                } else {
                    uint64_t bits;
                    if (safe_read_u64(ctx->data, ctx->data_size, fo, &bits) != QZDB_OK) return QZDB_ERR_BOUNDS;
                    union { uint64_t u; double d; } u;
                    u.u = bits;
                    snprintf(buf, sizeof(buf), "%.6f", u.d);
                }
            } else {
                uint32_t val;
                if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &val) != QZDB_OK) return QZDB_ERR_BOUNDS;
                snprintf(buf, sizeof(buf), "%u", val);
            }
            result->values[i] = strdup(buf);
            result->values_mask |= (1u << i);
        } else {
            uint32_t idx;
            if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &idx) != QZDB_OK) return QZDB_ERR_BOUNDS;
            if (ctx->group_pools[group_index] && ctx->group_pools[group_index][i] && (int)idx < ctx->group_pool_counts[group_index][i]) {
                result->values[i] = ctx->group_pools[group_index][i][idx];
            } else {
                result->values[i] = "";
            }
        }
    }
    return QZDB_OK;
}

static void free_geo_info(qzdb_geo_info_t* info) {
    for (int i = 0; i < QZDB_MAX_FIELDS; i++) {
        if (info->values_mask & (1u << i)) {
            free(info->values[i]);
            info->values[i] = NULL;
            info->values_mask &= ~(1u << i);
        }
    }
}

static int get_geo_info_buf(qzdb_searcher_t* ctx, uint32_t entry_id, int group_index,
                              char** values, char (*bufs)[64], int buf_size, int* out_count) {
    if (!ctx || !values || !bufs || !out_count) return QZDB_ERR_INVALID_PARAM;
    if (group_index < 0 || group_index >= ctx->actual_groups) return QZDB_ERR_INVALID_PARAM;
    if (entry_id >= ctx->group_entry_counts[group_index]) return QZDB_ERR_INVALID_PARAM;

    int field_count = ctx->group_field_counts[group_index];
    if (field_count <= 0) return QZDB_ERR_CORRUPTED;

    uint64_t group_entry_start = ctx->off_geo_entries + ctx->group_entry_offsets[group_index];
    int stride = ctx->group_strides[group_index];
    uint64_t entry_offset = group_entry_start + (uint64_t)entry_id * stride;

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
                    uint32_t bits;
                    if (safe_read_u32(ctx->data, ctx->data_size, fo, &bits) != QZDB_OK) { values[i] = ""; continue; }
                    union { uint32_t u; float f; } u;
                    u.u = bits;
                    snprintf(bufs[i], buf_size, "%.6f", u.f);
                } else {
                    uint64_t bits;
                    if (safe_read_u64(ctx->data, ctx->data_size, fo, &bits) != QZDB_OK) { values[i] = ""; continue; }
                    union { uint64_t u; double d; } u;
                    u.u = bits;
                    snprintf(bufs[i], buf_size, "%.6f", u.d);
                }
            } else {
                uint32_t val;
                if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &val) != QZDB_OK) { values[i] = ""; continue; }
                snprintf(bufs[i], buf_size, "%u", val);
            }
            values[i] = bufs[i];
        } else {
            uint32_t idx;
            if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &idx) != QZDB_OK) { values[i] = ""; continue; }
            if (ctx->group_pools[group_index] && ctx->group_pools[group_index][i] && (int)idx < ctx->group_pool_counts[group_index][i]) {
                values[i] = ctx->group_pools[group_index][i][idx];
            } else {
                values[i] = "";
            }
        }
    }
    *out_count = field_count;
    return QZDB_OK;
}

static int resolve_row_id_buf(qzdb_searcher_t* ctx, uint32_t row_id, int group_index,
                                char** values, char (*bufs)[64], int buf_size, int* out_count) {
    if (!ctx || !values || !bufs || !out_count) return QZDB_ERR_INVALID_PARAM;
    if (row_id <= 0 || row_id >= (uint32_t)ctx->row_count) return QZDB_ERR_INVALID_PARAM;
    uint64_t off = ctx->off_ip_row + (uint64_t)row_id * ctx->ip_row_size;
    uint32_t geo_id, asn_id;
    if (safe_read_u24(ctx->data, ctx->data_size, off, &geo_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    if (safe_read_u24(ctx->data, ctx->data_size, off + 3, &asn_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    uint32_t usage_id = 0;
    if (ctx->ip_row_size >= 9) {
        if (safe_read_u24(ctx->data, ctx->data_size, off + 6, &usage_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    }

    uint16_t mask = group_index < ctx->actual_groups ? ctx->group_dim_masks[group_index] : 0;
    uint32_t entry_id = geo_id;
    if (mask & 0x02) {
        entry_id = asn_id;
    } else if (mask & 0x04) {
        entry_id = usage_id;
    }

    if (entry_id == 0) return QZDB_ERR_CORRUPTED;
    return get_geo_info_buf(ctx, entry_id, group_index, values, bufs, buf_size, out_count);
}

static int resolve_row_id(qzdb_searcher_t* ctx, uint32_t row_id, int group_index, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    if (row_id <= 0 || row_id >= (uint32_t)ctx->row_count) return QZDB_ERR_INVALID_PARAM;
    uint64_t off = ctx->off_ip_row + (uint64_t)row_id * ctx->ip_row_size;
    uint32_t geo_id, asn_id;
    if (safe_read_u24(ctx->data, ctx->data_size, off, &geo_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    if (safe_read_u24(ctx->data, ctx->data_size, off + 3, &asn_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    uint32_t usage_id = 0;
    if (ctx->ip_row_size >= 9) {
        if (safe_read_u24(ctx->data, ctx->data_size, off + 6, &usage_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    }

    uint16_t mask = group_index < ctx->actual_groups ? ctx->group_dim_masks[group_index] : 0;
    uint32_t entry_id = geo_id;
    if (mask & 0x02) {
        entry_id = asn_id;
    } else if (mask & 0x04) {
        entry_id = usage_id;
    }

    if (entry_id == 0) return QZDB_ERR_CORRUPTED;
    return get_geo_info(ctx, entry_id, group_index, result);
}

int qzdb_find_uint(qzdb_searcher_t* ctx, uint32_t ip_int, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->has_v4) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v4(ctx, ip_int);
    if (row_id == 0) return QZDB_ERR_NOT_FOUND;
    return resolve_row_id(ctx, row_id, ctx->group_index, result);
}

int qzdb_find_v6(qzdb_searcher_t* ctx, const uint8_t* ip_bin, qzdb_geo_info_t* result) {
    if (!ctx || !result) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->has_v6) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v6(ctx, ip_bin);
    if (row_id == 0) return QZDB_ERR_NOT_FOUND;
    return resolve_row_id(ctx, row_id, ctx->group_index, result);
}

int qzdb_find_uint_buf(qzdb_searcher_t* ctx, uint32_t ip_int,
                        char** values, char (*bufs)[64], int buf_size) {
    if (!ctx || !values || !bufs) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->has_v4) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v4(ctx, ip_int);
    if (row_id == 0) return 0;
    int count = 0;
    int rc = resolve_row_id_buf(ctx, row_id, ctx->group_index, values, bufs, buf_size, &count);
    return rc == 0 ? count : QZDB_ERR_CORRUPTED;
}

int qzdb_find_v6_buf(qzdb_searcher_t* ctx, const uint8_t* ip_bin,
                      char** values, char (*bufs)[64], int buf_size) {
    if (!ctx || !values || !bufs) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->has_v6) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v6(ctx, ip_bin);
    if (row_id == 0) return 0;
    int count = 0;
    int rc = resolve_row_id_buf(ctx, row_id, ctx->group_index, values, bufs, buf_size, &count);
    return rc == 0 ? count : QZDB_ERR_CORRUPTED;
}

typedef struct { uint8_t v6[16]; uint32_t v4; int is_v4; } parse_result_t;
static int fast_parse_ip(const char* s, parse_result_t* res);

uint32_t qzdb_lookup_row_id(qzdb_searcher_t* ctx, const char* ip_str) {
    if (!ip_str || !ctx) return 0;
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) return 0;
    if (res.is_v4) return ctx->has_v4 ? trie_walk_v4(ctx, res.v4) : 0;
    return ctx->has_v6 ? trie_walk_v6(ctx, res.v6) : 0;
}

uint32_t qzdb_lookup_row_id_uint(qzdb_searcher_t* ctx, uint32_t ip_int) {
    if (!ctx->has_v4) return 0;
    return trie_walk_v4(ctx, ip_int);
}

uint32_t qzdb_lookup_row_id_v6(qzdb_searcher_t* ctx, const uint8_t* ip_bin) {
    if (!ctx->has_v6) return 0;
    return trie_walk_v6(ctx, ip_bin);
}

int qzdb_lookup_ids(qzdb_searcher_t* ctx, uint32_t row_id, qzdb_ids_t* out) {
    if (!ctx || !out) return QZDB_ERR_INVALID_PARAM;
    memset(out, 0, sizeof(*out));
    if (row_id == 0 || row_id >= (uint32_t)ctx->row_count) return QZDB_ERR_INVALID_PARAM;

    uint64_t off = ctx->off_ip_row + (uint64_t)row_id * ctx->ip_row_size;
    if (safe_read_u24(ctx->data, ctx->data_size, off, &out->geo_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    if (safe_read_u24(ctx->data, ctx->data_size, off + 3, &out->asn_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    if (ctx->ip_row_size >= 9) {
        if (safe_read_u24(ctx->data, ctx->data_size, off + 6, &out->usage_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    }
    return QZDB_OK;
}

static const uint8_t hex_lut[128] = {
    ['0']=0,1,2,3,4,5,6,7,8,9,
    ['a']=10,11,12,13,14,15,
    ['A']=10,11,12,13,14,15
};

static int fast_parse_ipv4(const char* s, uint32_t* out) {
    int n = 0; while (s[n]) n++;
    if (n == 0 || s[n - 1] == '.') return 0;
    uint32_t result = 0, val = 0;
    int dots = 0, start = 0;
    for (int i = 0; i <= n; i++) {
        char c = i < n ? s[i] : '.';
        if (c == '.') {
            int seg_len = i - start;
            if (seg_len == 0 || seg_len > 3) return 0;
            if (seg_len > 1 && s[start] == '0') return 0;
            val = 0;
            for (int j = start; j < i; j++) {
                char d = s[j];
                if (d < '0' || d > '9') return 0;
                val = val * 10 + (uint32_t)(d - '0');
            }
            if (val > 255) return 0;
            result = (result << 8) | val;
            dots++; start = i + 1;
        }
    }
    if (dots != 4) return 0;
    *out = result;
    return 1;
}

/* Split colon-separated hextets into parts[][16]. Returns count or -1 on error. */
static int split_hextets(const char* src, int src_len, char parts[][16], int max_parts) {
    if (src_len < 0) return -1;
    if (src_len == 0) return 0;
    int count = 0;
    int i = 0;
    while (i <= src_len) {
        int start = i;
        while (i < src_len && src[i] != ':') i++;
        int seglen = i - start;
        if (seglen == 0) return -1; /* empty group (consecutive :: handled outside) */
        if (count >= max_parts) return -1;
        if (seglen > 15) return -1;
        memcpy(parts[count], src + start, (size_t)seglen);
        parts[count][seglen] = '\0';
        count++;
        if (i >= src_len) break;
        i++; /* skip ':' */
    }
    return count;
}

static int fast_parse_ip(const char* s, parse_result_t* res) {
    if (!s) return 0;
    int n = 0;
    while (s[n]) {
        unsigned char c = (unsigned char)s[n];
        /* Fail-closed: reject any whitespace (SSRF-safe, cross-lang consistent) */
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f') return 0;
        n++;
    }
    if (n == 0 || n > 45) return 0;
    int has_colon = 0;
    for (int i = 0; i < n; i++) { if (s[i] == ':') { has_colon = 1; break; } }
    if (!has_colon) {
        uint32_t v4;
        if (!fast_parse_ipv4(s, &v4)) return 0;
        res->v4 = v4; res->is_v4 = 1;
        return 1;
    }
    for (int i = 0; i < n; i++) { if (s[i] == '%') return 0; }
    const char* dc_ptr = NULL;
    for (int i = 0; i < n - 1; i++) {
        if (s[i] == ':' && s[i+1] == ':') {
            if (dc_ptr) return 0;
            dc_ptr = s + i;
        }
    }
    const char* rgt = dc_ptr ? dc_ptr + 2 : s + n;
    int lft_len = (int)(dc_ptr ? dc_ptr - s : n);
    int rgt_len = (int)(dc_ptr ? (s + n) - (dc_ptr + 2) : 0);
    if (lft_len >= 64 || rgt_len >= 64) return 0;
    char lg_parts[8][16], rg_parts[8][16];
    int lg_count = 0, rg_count = 0;
    if (lft_len > 0) {
        lg_count = split_hextets(s, lft_len, lg_parts, 8);
        if (lg_count < 0) return 0;
    }
    if (rgt_len > 0) {
        rg_count = split_hextets(rgt, rgt_len, rg_parts, 8);
        if (rg_count < 0) return 0;
    }
    char allg[10][16];
    int ng = 0;
    for (int i = 0; i < lg_count; i++) { 
        strncpy(allg[ng], lg_parts[i], 15);
        allg[ng][15] = 0;
        ng++;
    }
    for (int i = 0; i < rg_count; i++) { 
        strncpy(allg[ng], rg_parts[i], 15);
        allg[ng][15] = 0;
        ng++;
    }
    int has_v4 = 0;
    uint32_t v4_int = 0;
    if (ng > 0) {
        int last = ng - 1;
        int has_dot = 0;
        for (int j = 0; allg[last][j]; j++) { if (allg[last][j] == '.') { has_dot = 1; break; } }
        if (has_dot) {
            if (!fast_parse_ipv4(allg[last], &v4_int)) return 0;
            has_v4 = 1;
            ng--;
        }
    }
    int v4_slots = has_v4 ? 2 : 0;
    if (dc_ptr) { if (ng + v4_slots > 7) return 0; }
    else { if (ng + v4_slots != 8) return 0; }
    for (int i = 0; i < ng; i++) {
        int gl = 0; while (allg[i][gl]) gl++;
        if (gl == 0 || gl > 4) return 0;
        for (int j = 0; j < gl; j++) {
            unsigned char cc = (unsigned char)allg[i][j];
            if (cc >= 128 || (hex_lut[cc] == 0 && cc != '0')) return 0;
        }
    }
    int zeros = 8 - ng - v4_slots;
    uint8_t buf[16];
    memset(buf, 0, 16);
    int off = 0;
    for (int i = 0; i < lg_count; i++) {
        uint16_t v = 0;
        for (int j = 0; lg_parts[i][j]; j++) v = (v << 4) | hex_lut[(unsigned char)lg_parts[i][j]];
        buf[off] = (uint8_t)(v >> 8); buf[off + 1] = (uint8_t)v;
        off += 2;
    }
    off += zeros * 2;
    for (int i = 0; i < rg_count; i++) {
        uint16_t v = 0;
        for (int j = 0; rg_parts[i][j]; j++) v = (v << 4) | hex_lut[(unsigned char)rg_parts[i][j]];
        buf[off] = (uint8_t)(v >> 8); buf[off + 1] = (uint8_t)v;
        off += 2;
    }
    if (has_v4) { buf[12] = (uint8_t)(v4_int >> 24); buf[13] = (uint8_t)(v4_int >> 16); buf[14] = (uint8_t)(v4_int >> 8); buf[15] = (uint8_t)v4_int; }
    if (buf[10] == 0xff && buf[11] == 0xff
        && buf[0] == 0 && buf[1] == 0 && buf[2] == 0 && buf[3] == 0
        && buf[4] == 0 && buf[5] == 0 && buf[6] == 0 && buf[7] == 0
        && buf[8] == 0 && buf[9] == 0) {
        res->v4 = ((uint32_t)buf[12] << 24) | ((uint32_t)buf[13] << 16) | ((uint32_t)buf[14] << 8) | buf[15];
        res->is_v4 = 1;
        return 1;
    }
    memcpy(res->v6, buf, 16);
    res->is_v4 = 0;
    return 1;
}

int qzdb_find(qzdb_searcher_t* ctx, const char* ip_str, qzdb_geo_info_t* result) {
    if (!ctx || !ip_str || !result) return QZDB_ERR_INVALID_PARAM;
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) return QZDB_ERR_INVALID_PARAM;
    if (res.is_v4) return qzdb_find_uint(ctx, res.v4, result);
    return qzdb_find_v6(ctx, res.v6, result);
}

int qzdb_find_str(qzdb_searcher_t* ctx, const char* ip_str, char* out, size_t out_size) {
    if (!ctx || !ip_str || !out || out_size == 0) return QZDB_ERR_INVALID_PARAM;
    qzdb_geo_info_t info;
    int result = qzdb_find(ctx, ip_str, &info);
    if (result != QZDB_OK) {
        if (out_size > 0) out[0] = '\0';
        return QZDB_ERR_NOT_FOUND;
    }
    size_t pos = 0;
    int field_count = ctx->group_field_counts[ctx->group_index];
    for (int i = 0; i < field_count && i < QZDB_MAX_FIELDS; i++) {
        if (i > 0 && pos < out_size - 1) out[pos++] = '|';
        const char* val = info.values[i];
        if (!val) continue;
        char float_buf[64];
        if (ctx->float_field_flags && ctx->float_field_flags[i] && val[0] != '\0') {
            double f = atof(val);
            int flen = snprintf(float_buf, sizeof(float_buf), "%.6f", f);
            val = float_buf;
            size_t wlen = (size_t)flen < out_size - pos - 1 ? (size_t)flen : out_size - pos - 1;
            if (wlen > 0) {
                memcpy(out + pos, val, wlen);
                pos += wlen;
            }
            continue;
        }
        size_t len = strlen(val);
        if (pos + len >= out_size) {
            if (out_size > pos) {
                memcpy(out + pos, val, out_size - pos - 1);
                pos = out_size - 1;
            }
            break;
        }
        memcpy(out + pos, val, len);
        pos += len;
    }
    out[pos] = '\0';
    free_geo_info(&info);
    return QZDB_OK;
}

static qzdb_searcher_t g_instance;
static int g_instance_inited = 0;

qzdb_searcher_t* qzdb_instance(const char* db_path) {
    if (!db_path) return NULL;
    pthread_mutex_lock(&g_instance_mutex);
    if (!g_instance_inited) {
        int result = qzdb_init(&g_instance, db_path);
        if (result != QZDB_OK) {
            pthread_mutex_unlock(&g_instance_mutex);
            return NULL;
        }
        g_instance_inited = 1;
    }
    pthread_mutex_unlock(&g_instance_mutex);
    return &g_instance;
}

int qzdb_instance_load(const char* db_path) {
    if (!db_path) return QZDB_ERR_INVALID_PARAM;
    /* Build complete new context first so a failed load leaves the old instance intact. */
    qzdb_searcher_t tmp;
    memset(&tmp, 0, sizeof(tmp));
    int result = qzdb_init(&tmp, db_path);
    if (result != QZDB_OK) return result;

    pthread_mutex_lock(&g_instance_mutex);
    if (g_instance_inited) {
        qzdb_free(&g_instance);
    }
    memcpy(&g_instance, &tmp, sizeof(g_instance));
    g_instance_inited = 1;
    pthread_mutex_unlock(&g_instance_mutex);
    /* Note: concurrent queries holding a prior qzdb_instance() pointer during reload
     * are still racy; prefer per-thread qzdb_init / qzdb_reload contexts under load. */
    return QZDB_OK;
}

void qzdb_free(qzdb_searcher_t* ctx) {
    if (!ctx->data) return;
    /* Pool C-strings live in a single arena — free pointer tables, then the arena. */
    free(ctx->pool_arena);
    ctx->pool_arena = NULL;
    if (ctx->group_pools) {
        for (int g = 0; g < ctx->actual_groups; g++) {
            if (ctx->group_pools[g]) {
                for (int f = 0; f < ctx->group_field_counts[g]; f++) {
                    free(ctx->group_pools[g][f]); /* array of pointers into pool_arena */
                }
                free(ctx->group_pools[g]);
            }
            free(ctx->group_pool_counts[g]);
        }
        free(ctx->group_pools);
        free(ctx->group_pool_counts);
    }
    free(ctx->group_entry_offsets);
    int gfc0 = ctx->group_field_counts ? ctx->group_field_counts[0] : 0;
    free(ctx->group_field_counts);
    free(ctx->group_entry_counts);
    free(ctx->group_dim_masks);
    free(ctx->group_strides);

    for (int g = 0; g < ctx->actual_groups; g++) {
        free(ctx->group_field_widths[g]);
        free(ctx->group_field_offsets[g]);
        free(ctx->group_field_native[g]);
        free(ctx->group_field_native_type[g]);
        free(ctx->group_field_ids[g]);
        free(ctx->group_pool_section_ids[g]);
    }
    free(ctx->group_field_widths);
    free(ctx->group_field_offsets);
    free(ctx->group_field_native);
    free(ctx->group_field_native_type);
    free(ctx->group_field_ids);
    free(ctx->group_pool_section_ids);

    if (ctx->field_names) {
        for (int i = 0; i < gfc0; i++) {
            free(ctx->field_names[i]);
        }
        free(ctx->field_names);
    }
    free(ctx->float_field_flags);
    free(ctx->version_name);

    munmap(ctx->data, ctx->data_size);
    memset(ctx, 0, sizeof(*ctx));
}

int qzdb_verify_crc(qzdb_searcher_t* ctx) {
    if (!ctx) return QZDB_ERR_INVALID_PARAM;
    if (!ctx->data || ctx->data_size < 20) return QZDB_ERR_CORRUPTED;
    uint32_t stored = READ_LE32(ctx->data + 16);
    uint32_t computed = crc32_compute_file(ctx->data, ctx->data_size);
    return stored == computed ? QZDB_OK : QZDB_ERR_CORRUPTED;
}

static int resolve_row_id_fields(qzdb_searcher_t* ctx, uint32_t row_id, int group_index,
                                   const char** field_names, int field_count,
                                   char** values, char (*bufs)[64], int buf_size) {
    if (!ctx || !field_names || !values || !bufs) return QZDB_ERR_INVALID_PARAM;
    if (row_id <= 0 || row_id >= (uint32_t)ctx->row_count) return QZDB_ERR_INVALID_PARAM;
    uint64_t off = ctx->off_ip_row + (uint64_t)row_id * ctx->ip_row_size;
    uint32_t geo_id, asn_id;
    if (safe_read_u24(ctx->data, ctx->data_size, off, &geo_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    if (safe_read_u24(ctx->data, ctx->data_size, off + 3, &asn_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    uint32_t usage_id = 0;
    if (ctx->ip_row_size >= 9) {
        if (safe_read_u24(ctx->data, ctx->data_size, off + 6, &usage_id) != QZDB_OK) return QZDB_ERR_BOUNDS;
    }

    uint16_t mask = group_index < ctx->actual_groups ? ctx->group_dim_masks[group_index] : 0;
    uint32_t entry_id = geo_id;
    if (mask & 0x02) entry_id = asn_id;
    else if (mask & 0x04) entry_id = usage_id;

    if (entry_id == 0) return QZDB_ERR_CORRUPTED;

    if (group_index < 0 || group_index >= ctx->actual_groups) return QZDB_ERR_INVALID_PARAM;
    if (entry_id >= ctx->group_entry_counts[group_index]) return QZDB_ERR_INVALID_PARAM;

    int total_field_count = ctx->group_field_counts[group_index];
    if (total_field_count <= 0) return QZDB_ERR_CORRUPTED;

    // Build name→index lookup
    int indices[QZDB_MAX_FIELDS];
    int idx_count = 0;
    for (int fi = 0; fi < field_count && field_names[fi] != NULL; fi++) {
        for (int i = 0; i < ctx->field_count; i++) {
            if (strcmp(ctx->field_names[i], field_names[fi]) == 0) {
                indices[idx_count++] = i;
                break;
            }
        }
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
        int i = indices[ki];
        if (i < 0 || i >= total_field_count) continue;
        int w = widths[i];
        uint64_t fo = entry_offset + base_offsets[i];
        int is_native = natives[i];

        if (is_native) {
            int t = nat_types[i];
            if (t == 1) {
                if (w == 4) {
                    union { uint32_t u; float f; } u;
                    if (safe_read_u32(ctx->data, ctx->data_size, fo, &u.u) != QZDB_OK) return QZDB_ERR_BOUNDS;
                    snprintf(bufs[i], buf_size, "%.6f", (double)u.f);
                } else {
                    union { uint64_t u; double d; } u;
                    if (safe_read_u64(ctx->data, ctx->data_size, fo, &u.u) != QZDB_OK) return QZDB_ERR_BOUNDS;
                    snprintf(bufs[i], buf_size, "%.6f", u.d);
                }
            } else {
                uint32_t val;
                if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &val) != QZDB_OK) return QZDB_ERR_BOUNDS;
                snprintf(bufs[i], buf_size, "%u", val);
            }
            values[i] = bufs[i];
        } else {
            uint32_t idx;
            if (safe_read_uint_width(ctx->data, ctx->data_size, fo, w, &idx) != QZDB_OK) return QZDB_ERR_BOUNDS;
            if (ctx->group_pools[group_index] && ctx->group_pools[group_index][i] &&
                (int)idx < ctx->group_pool_counts[group_index][i]) {
                values[i] = ctx->group_pools[group_index][i][idx];
            } else {
                values[i] = "";
            }
        }
    }
    return total_field_count;
}

int qzdb_find_fields_uint_buf(qzdb_searcher_t* ctx, uint32_t ip_int,
                                const char** field_names,
                                char** values, char (*bufs)[64], int buf_size) {
    if (!ctx || !values || !bufs) return QZDB_ERR_INVALID_PARAM;
    if (field_names == NULL) {
        return qzdb_find_uint_buf(ctx, ip_int, values, bufs, buf_size);
    }
    if (!ctx->has_v4) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v4(ctx, ip_int);
    if (row_id == 0) return 0;
    return resolve_row_id_fields(ctx, row_id, ctx->group_index, field_names,
                                  QZDB_MAX_FIELDS, values, bufs, buf_size);
}

int qzdb_find_fields_buf(qzdb_searcher_t* ctx, const char* ip_str,
                         const char** field_names,
                         char** values, char (*bufs)[64], int buf_size) {
    if (!ctx || !ip_str || !values || !bufs) return QZDB_ERR_INVALID_PARAM;
    if (field_names == NULL || field_names[0] == NULL) {
        qzdb_geo_info_t tmp;
        return qzdb_find(ctx, ip_str, &tmp) == QZDB_OK ? 1 : QZDB_ERR_NOT_FOUND;
    }
    parse_result_t res;
    if (!fast_parse_ip(ip_str, &res)) return QZDB_ERR_INVALID_PARAM;
    if (res.is_v4) {
        return qzdb_find_fields_uint_buf(ctx, res.v4, field_names, values, bufs, buf_size);
    }
    if (!ctx->has_v6) return QZDB_ERR_NOT_FOUND;
    uint32_t row_id = trie_walk_v6(ctx, res.v6);
    if (row_id == 0) return 0;
    return resolve_row_id_fields(ctx, row_id, ctx->group_index, field_names,
                                  QZDB_MAX_FIELDS, values, bufs, buf_size);
}

int qzdb_reload(qzdb_searcher_t* ctx, const char* db_path) {
    if (!ctx || !db_path) return QZDB_ERR_INVALID_PARAM;
    qzdb_searcher_t new_ctx;
    memset(&new_ctx, 0, sizeof(new_ctx));
    int result = qzdb_init(&new_ctx, db_path);
    if (result != QZDB_OK) {
        return result;
    }
    qzdb_free(ctx);
    memcpy(ctx, &new_ctx, sizeof(*ctx));
    return QZDB_OK;
}
