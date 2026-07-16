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

static uint32_t crc32_table[256];
static int crc32_ready = 0;
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

static uint32_t crc32_compute(const uint8_t* buf, size_t len) {
    if (!crc32_ready) crc32_init();
    uint32_t crc = 0xFFFFFFFF;
    for (size_t i = 0; i < len; i++) {
        crc = crc32_table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
    }
    return crc ^ 0xFFFFFFFF;
}

#define READ_LE16(p) ((uint16_t)(p)[0] | ((uint16_t)(p)[1] << 8))
#define READ_LE32(p) ((uint32_t)(p)[0] | ((uint32_t)(p)[1] << 8) | ((uint32_t)(p)[2] << 16) | ((uint32_t)(p)[3] << 24))
#define READ_LE64(p) ((uint64_t)READ_LE32(p) | ((uint64_t)READ_LE32((p) + 4) << 32))

static uint32_t read_u24(const uint8_t* p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16);
}

static uint64_t read_u48(const uint8_t* p) {
    return (uint64_t)p[0] | ((uint64_t)p[1] << 8) | ((uint64_t)p[2] << 16) |
           ((uint64_t)p[3] << 24) | ((uint64_t)p[4] << 32) | ((uint64_t)p[5] << 40);
}

static uint32_t read_uint_width(const uint8_t* p, int w) {
    if (w <= 1) return p[0];
    else if (w == 2) return READ_LE16(p);
    else if (w == 3) return read_u24(p);
    else return READ_LE32(p);
}

int qzdb_init(qzdb_searcher_t* ctx, const char* db_path) {
    memset(ctx, 0, sizeof(*ctx));
    int fd = open(db_path, O_RDONLY);
    if (fd < 0) return -1;
    struct stat st;
    if (fstat(fd, &st) != 0) {
        close(fd);
        return -1;
    }
    ctx->data_size = st.st_size;
    ctx->data = mmap(NULL, ctx->data_size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (ctx->data == MAP_FAILED) {
        ctx->data = NULL;
        return -1;
    }

    uint8_t* d = ctx->data;
    if (ctx->data_size < 192) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return -1;
    }
    if (memcmp(d, "QZDB", 4) != 0) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return -1;
    }

    int fmt_ver = d[4];
    if (fmt_ver < 1 || fmt_ver > 6) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return -1;
    }

    ctx->flags = READ_LE16(d + 8);
    ctx->has_v4 = (ctx->flags & 1) != 0;
    ctx->has_v6 = (ctx->flags & 2) != 0;
    ctx->v4_node_24 = (ctx->flags & 0x10) != 0;
    ctx->v6_node_24 = (ctx->flags & 0x20) != 0;

    ctx->v6_jump_bits = d[11];
    if (ctx->v6_jump_bits == 0) ctx->v6_jump_bits = 16;

    ctx->pool_count = d[12];
    ctx->pool_idx_size = d[13];
    ctx->geo_count = READ_LE16(d + 14);
    ctx->row_count = READ_LE32(d + 20);
    ctx->v4_rec_count = READ_LE32(d + 24);
    ctx->v6_rec_count = READ_LE32(d + 28);

    uint32_t hs = READ_LE32(d + 36);
    if (hs != 192) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return -1;
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
    ctx->geo_entry_group_count = READ_LE32(d + 164);

    ctx->group_entry_offsets = malloc(4 * sizeof(uint64_t));
    for (int i = 0; i < 4; i++) {
        ctx->group_entry_offsets[i] = read_u48(d + 168 + i * 6);
    }

    uint64_t gm_off = ctx->off_geo_entries;
    int group_count = d[gm_off];
    gm_off++;

    ctx->actual_groups = group_count < 1 ? 1 : group_count;
    if (ctx->geo_entry_group_count > 0 && ctx->geo_entry_group_count < ctx->actual_groups) {
        ctx->actual_groups = ctx->geo_entry_group_count;
    }

    ctx->group_field_counts = malloc(ctx->actual_groups * sizeof(int));
    ctx->group_entry_counts = malloc(ctx->actual_groups * sizeof(uint32_t));
    ctx->group_dim_masks = malloc(ctx->actual_groups * sizeof(uint16_t));

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
                for (int fi = 0; fi < fld_count; fi++) {
                    sp += 2; // skip fieldId
                    ctx->group_field_widths[gi][fi] = d[sp];
                    sp++;
                    int field_flags = d[sp];
                    sp++;
                    ctx->group_field_native[gi][fi] = (field_flags & 0x01) != 0;
                    ctx->group_field_native_type[gi][fi] = (field_flags >> 1) & 0x03;
                    ctx->group_field_offsets[gi][fi] = READ_LE32(d + sp);
                    sp += 4;
                    sp += 4; // skip poolSectionId
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
                ctx->field_names = malloc(ctx->group_field_counts[0] * sizeof(char*));
                ctx->float_field_flags = calloc(ctx->group_field_counts[0], sizeof(int));
                ctx->field_count = ctx->group_field_counts[0];
                int idx = 0;
                char* token = strtok(val, "|");
                while (token && idx < ctx->group_field_counts[0]) {
                    ctx->field_names[idx] = strdup(token);
                    if (strcmp(token, "longitude") == 0 || strcmp(token, "latitude") == 0) {
                        ctx->float_field_flags[idx] = 1;
                    }
                    idx++;
                    token = strtok(NULL, "|");
                }
                free(val);
            } else {
                free(val);
            }
            pos += 4 + length;
        }
    }

    if (!ctx->field_names) {
        ctx->field_names = malloc(ctx->group_field_counts[0] * sizeof(char*));
        ctx->float_field_flags = calloc(ctx->group_field_counts[0], sizeof(int));
        ctx->field_count = ctx->group_field_counts[0];
        for (int i = 0; i < ctx->group_field_counts[0]; i++) {
            char buf[32];
            sprintf(buf, "field_%d", i);
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

    return 0;
}

static void ensure_pools_loaded(qzdb_searcher_t* ctx) {
    if (ctx->pools_loaded) return;
    ctx->pools_loaded = 1;

    ctx->group_pools = calloc(ctx->actual_groups, sizeof(char***));
    ctx->group_pool_counts = calloc(ctx->actual_groups, sizeof(int*));

    if (ctx->off_pools <= 0) return;

    uint64_t pool_cursor = ctx->off_pools;
    uint64_t pool_end = ctx->off_meta > 0 ? ctx->off_meta : ctx->data_size;
    uint8_t* d = ctx->data;

    for (int g = 0; g < ctx->actual_groups; g++) {
        int field_count = ctx->group_field_counts[g];
        ctx->group_pools[g] = calloc(field_count, sizeof(char**));
        ctx->group_pool_counts[g] = calloc(field_count, sizeof(int));

        for (int f = 0; f < field_count; f++) {
            if (ctx->group_field_native[g][f]) {
                continue;
            }
            if (pool_cursor + 4 > pool_end) {
                continue;
            }
            uint32_t count = READ_LE32(d + pool_cursor);
            pool_cursor += 4;
            if (ctx->off_row_schema > 0) {
                pool_cursor += 4;
            }
            if (count == 0) {
                continue;
            }

            ctx->group_pool_counts[g][f] = count;
            uint32_t* offsets = malloc((count + 1) * sizeof(uint32_t));
            for (uint32_t o = 0; o <= count; o++) {
                offsets[o] = READ_LE32(d + pool_cursor);
                pool_cursor += 4;
            }

            ctx->group_pools[g][f] = malloc(count * sizeof(char*));
            for (uint32_t s = 0; s < count; s++) {
                uint32_t start = offsets[s];
                uint32_t end = offsets[s + 1];
                int length = end - start;
                ctx->group_pools[g][f][s] = malloc(length + 1);
                if (length > 0) {
                    memcpy(ctx->group_pools[g][f][s], d + pool_cursor + start, length);
                }
                ctx->group_pools[g][f][s][length] = '\0';
            }
            pool_cursor += offsets[count];
            free(offsets);
        }
    }
}

static uint32_t get_v4_child(const qzdb_searcher_t* ctx, uint32_t node_idx, uint32_t bit) {
    if (ctx->v4_node_24) {
        uint64_t node_offset = ctx->off_v4_nodes + (uint64_t)node_idx * 6;
        uint64_t offset = bit == 0 ? node_offset : node_offset + 3;
        uint32_t val = (uint32_t)ctx->data[offset] | ((uint32_t)ctx->data[offset + 1] << 8) | ((uint32_t)ctx->data[offset + 2] << 16);
        if (val & 0x800000) {
            return (val & 0x7FFFFF) | 0x80000000;
        }
        return val;
    } else {
        uint64_t child_off = ctx->off_v4_nodes + (uint64_t)node_idx * 8 + (uint64_t)bit * 4;
        return READ_LE32(ctx->data + child_off);
    }
}

static uint32_t get_v6_child(const qzdb_searcher_t* ctx, uint32_t node_idx, uint32_t bit) {
    if (ctx->v6_node_24) {
        uint64_t node_offset = ctx->off_v6_nodes + (uint64_t)node_idx * 6;
        uint64_t offset = bit == 0 ? node_offset : node_offset + 3;
        uint32_t val = (uint32_t)ctx->data[offset] | ((uint32_t)ctx->data[offset + 1] << 8) | ((uint32_t)ctx->data[offset + 2] << 16);
        if (val & 0x800000) {
            return (val & 0x7FFFFF) | 0x80000000;
        }
        return val;
    } else {
        uint64_t child_off = ctx->off_v6_nodes + (uint64_t)node_idx * 8 + (uint64_t)bit * 4;
        return READ_LE32(ctx->data + child_off);
    }
}

static uint32_t trie_walk_v4(const qzdb_searcher_t* ctx, uint32_t ip_int) {
    uint32_t hi16 = (ip_int >> 16) & 0xFFFF;
    uint32_t ptr = READ_LE32(ctx->data + ctx->off_v4_jump + hi16 * 4);

    if (ptr == 0) return 0;
    if (ptr & 0x80000000) return ptr & 0x7FFFFFFF;

    uint32_t idx = ptr;
    uint32_t suffix = (ip_int & 0xFFFF) << 16;

    while (1) {
        uint32_t bit = (suffix >> 31) & 1;
        uint32_t child = get_v4_child(ctx, idx, bit);

        if (child == 0) return 0;
        if (child & 0x80000000) return child & 0x7FFFFFFF;

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

    uint32_t ptr = READ_LE32(ctx->data + ctx->off_v6_jump + idx_jump * 4);
    if (ptr == 0) return 0;
    if (ptr & 0x80000000) return ptr & 0x7FFFFFFF;

    uint32_t idx = ptr;
    int depth = v6_jump_bits;

    while (depth < 128) {
        int byte_idx = depth / 8;
        int bit_idx = 7 - (depth % 8);
        uint32_t bit = (ip_bin[byte_idx] >> bit_idx) & 1;

        uint32_t child = get_v6_child(ctx, idx, bit);
        if (child == 0) return 0;
        if (child & 0x80000000) return child & 0x7FFFFFFF;

        idx = child;
        depth++;
    }

    return 0;
}

static int get_geo_info(qzdb_searcher_t* ctx, uint32_t entry_id, int group_index, qzdb_geo_info_t* result) {
    if (group_index < 0 || group_index >= ctx->actual_groups) return -1;
    if (entry_id >= ctx->group_entry_counts[group_index]) return -1;

    ensure_pools_loaded(ctx);

    int field_count = ctx->group_field_counts[group_index];
    if (field_count <= 0) return -1;

    uint64_t group_entry_start = ctx->off_geo_entries + ctx->group_entry_offsets[group_index];
    int stride = ctx->group_strides[group_index];
    uint64_t entry_offset = group_entry_start + (uint64_t)entry_id * stride;
    uint8_t* d = ctx->data;

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
                    union { uint32_t u; float f; } u;
                    u.u = READ_LE32(d + fo);
                    sprintf(buf, "%f", u.f);
                } else {
                    union { uint64_t u; double d; } u;
                    u.u = READ_LE64(d + fo);
                    sprintf(buf, "%f", u.d);
                }
            } else {
                uint32_t val = read_uint_width(d + fo, w);
                sprintf(buf, "%u", val);
            }
            result->values[i] = strdup(buf);
        } else {
            uint32_t idx = read_uint_width(d + fo, w);
            if (ctx->group_pools[group_index] && ctx->group_pools[group_index][i] && (int)idx < ctx->group_pool_counts[group_index][i]) {
                result->values[i] = strdup(ctx->group_pools[group_index][i][idx]);
            } else {
                result->values[i] = strdup("");
            }
        }
    }
    return 0;
}

static void free_geo_info(qzdb_geo_info_t* info) {
    for (int i = 0; i < QZDB_MAX_FIELDS; i++) {
        if (info->values[i]) {
            free(info->values[i]);
            info->values[i] = NULL;
        }
    }
}

static int resolve_row_id(qzdb_searcher_t* ctx, uint32_t row_id, int group_index, qzdb_geo_info_t* result) {
    if (row_id <= 0 || row_id >= (uint32_t)ctx->row_count) return -1;
    uint64_t off = ctx->off_ip_row + (uint64_t)row_id * ctx->ip_row_size;
    uint32_t geo_id = read_u24(ctx->data + off);
    uint32_t asn_id = read_u24(ctx->data + off + 3);
    uint32_t usage_id = 0;
    if (ctx->ip_row_size >= 9) {
        usage_id = read_u24(ctx->data + off + 6);
    }

    uint16_t mask = group_index < ctx->actual_groups ? ctx->group_dim_masks[group_index] : 0;
    uint32_t entry_id = geo_id;
    if (mask & 0x02) {
        entry_id = asn_id;
    } else if (mask & 0x04) {
        entry_id = usage_id;
    }

    if (entry_id == 0) return -1;
    return get_geo_info(ctx, entry_id, group_index, result);
}

int qzdb_find_uint(qzdb_searcher_t* ctx, uint32_t ip_int, qzdb_geo_info_t* result) {
    if (!ctx->has_v4) return -1;
    uint32_t row_id = trie_walk_v4(ctx, ip_int);
    if (row_id == 0) return -1;
    return resolve_row_id(ctx, row_id, ctx->group_index, result);
}

int qzdb_find_v6(qzdb_searcher_t* ctx, const uint8_t* ip_bin, qzdb_geo_info_t* result) {
    if (!ctx->has_v6) return -1;
    uint32_t row_id = trie_walk_v6(ctx, ip_bin);
    if (row_id == 0) return -1;
    return resolve_row_id(ctx, row_id, ctx->group_index, result);
}

static uint32_t fast_parse_ip(const char* ip, int* ok) {
    uint32_t val = 0, result = 0;
    int dots = 0;
    const char* p = ip;
    while (*p) {
        if (*p >= '0' && *p <= '9') {
            val = val * 10 + (unsigned)(*p - '0');
            if (val > 255) { *ok = 0; return 0; }
        } else if (*p == '.') {
            if (p == ip || *(p-1) == '.') { *ok = 0; return 0; }
            result = (result << 8) | val;
            val = 0;
            dots++;
        } else {
            *ok = 0; return 0;
        }
        p++;
    }
    if (dots != 3) { *ok = 0; return 0; }
    *ok = 1;
    return (result << 8) | val;
}

int qzdb_find(qzdb_searcher_t* ctx, const char* ip_str, qzdb_geo_info_t* result) {
    if (!ip_str) return -1;
    if (strchr(ip_str, ':')) {
        uint8_t ip_bin[16];
        if (inet_pton(AF_INET6, ip_str, ip_bin) != 1) return -1;
        // Check for IPv4-mapped IPv6
        if (memcmp(ip_bin, "\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\xff\xff", 12) == 0) {
            uint32_t ip_int = ((uint32_t)ip_bin[12] << 24) | ((uint32_t)ip_bin[13] << 16) |
                              ((uint32_t)ip_bin[14] << 8) | (uint32_t)ip_bin[15];
            return qzdb_find_uint(ctx, ip_int, result);
        }
        return qzdb_find_v6(ctx, ip_bin, result);
    }
    int ok;
    uint32_t ip_int = fast_parse_ip(ip_str, &ok);
    if (!ok) return -1;
    return qzdb_find_uint(ctx, ip_int, result);
}

int qzdb_find_str(qzdb_searcher_t* ctx, const char* ip_str, char* out, size_t out_size) {
    qzdb_geo_info_t info;
    if (qzdb_find(ctx, ip_str, &info) != 0) {
        if (out_size > 0) out[0] = '\0';
        return -1;
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
    return 0;
}

static qzdb_searcher_t g_instance;
static int g_instance_inited = 0;

qzdb_searcher_t* qzdb_instance(const char* db_path) {
    if (!g_instance_inited) {
        if (qzdb_init(&g_instance, db_path) != 0) return NULL;
        g_instance_inited = 1;
    }
    return &g_instance;
}

int qzdb_instance_load(const char* db_path) {
    if (g_instance_inited) {
        qzdb_free(&g_instance);
        g_instance_inited = 0;
    }
    if (qzdb_init(&g_instance, db_path) != 0) return -1;
    g_instance_inited = 1;
    return 0;
}

void qzdb_free(qzdb_searcher_t* ctx) {
    if (!ctx->data) return;
    if (ctx->group_pools) {
        for (int g = 0; g < ctx->actual_groups; g++) {
            if (ctx->group_pools[g]) {
                for (int f = 0; f < ctx->group_field_counts[g]; f++) {
                    if (ctx->group_pools[g][f]) {
                        for (int s = 0; s < ctx->group_pool_counts[g][f]; s++) {
                            free(ctx->group_pools[g][f][s]);
                        }
                        free(ctx->group_pools[g][f]);
                    }
                }
                free(ctx->group_pools[g]);
            }
            free(ctx->group_pool_counts[g]);
        }
        free(ctx->group_pools);
        free(ctx->group_pool_counts);
    }
    free(ctx->group_entry_offsets);
    free(ctx->group_field_counts);
    free(ctx->group_entry_counts);
    free(ctx->group_dim_masks);
    free(ctx->group_strides);

    for (int g = 0; g < ctx->actual_groups; g++) {
        free(ctx->group_field_widths[g]);
        free(ctx->group_field_offsets[g]);
        free(ctx->group_field_native[g]);
        free(ctx->group_field_native_type[g]);
    }
    free(ctx->group_field_widths);
    free(ctx->group_field_offsets);
    free(ctx->group_field_native);
    free(ctx->group_field_native_type);

    if (ctx->field_names) {
        for (int i = 0; i < ctx->group_field_counts[0]; i++) {
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
    if (!ctx->data || ctx->data_size < 20) return 0;
    uint32_t stored = READ_LE32(ctx->data + 16);
    uint8_t orig[4];
    memcpy(orig, ctx->data + 16, 4);
    memset(ctx->data + 16, 0, 4);
    uint32_t computed = crc32_compute(ctx->data, ctx->data_size);
    memcpy(ctx->data + 16, orig, 4);
    return stored == computed ? 1 : 0;
}
