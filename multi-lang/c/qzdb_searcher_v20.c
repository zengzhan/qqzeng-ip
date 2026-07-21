#define _DARWIN_C_SOURCE
#include "qzdb_searcher_v20.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>
#include <arpa/inet.h>

#define V20_SENTINEL 0x80000000u

static uint32_t crc32_tbl[256];
static int crc32_rdy = 0;

static void crc32_init(void) {
    for (uint32_t i = 0; i < 256; i++) {
        uint32_t c = i;
        for (int j = 0; j < 8; j++)
            if (c & 1) c = (c >> 1) ^ 0xEDB88320;
            else c >>= 1;
        crc32_tbl[i] = c;
    }
    crc32_rdy = 1;
}

static uint32_t crc32_calc(const uint8_t* buf, size_t len) {
    if (!crc32_rdy) crc32_init();
    uint32_t crc = 0xFFFFFFFF;
    for (size_t i = 0; i < len; i++)
        crc = crc32_tbl[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFF;
}

#define READ_LE16(p) ((uint16_t)(p)[0] | ((uint16_t)(p)[1] << 8))
#define READ_LE32(p) ((uint32_t)(p)[0] | ((uint32_t)(p)[1] << 8) | ((uint32_t)(p)[2] << 16) | ((uint32_t)(p)[3] << 24))
#define READ_LE64(p) ((uint64_t)READ_LE32(p) | ((uint64_t)READ_LE32((p) + 4) << 32))
#define READ_BE64(p) ((uint64_t)(p)[0] << 56 | (uint64_t)(p)[1] << 48 | (uint64_t)(p)[2] << 40 | (uint64_t)(p)[3] << 32 \
                    | (uint64_t)(p)[4] << 24 | (uint64_t)(p)[5] << 16 | (uint64_t)(p)[6] << 8 | (uint64_t)(p)[7])

static uint32_t read_u24(const uint8_t* p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16);
}

static uint64_t read_u48(const uint8_t* p) {
    return (uint64_t)p[0] | ((uint64_t)p[1] << 8) | ((uint64_t)p[2] << 16)
         | ((uint64_t)p[3] << 24) | ((uint64_t)p[4] << 32) | ((uint64_t)p[5] << 40);
}

static int parse_metadata_fields_v20(const uint8_t* data, size_t data_size, uint64_t off,
                                      char*** out_field_names, uint8_t** out_float_flags, int expect_count) {
    uint64_t end = data_size;
    char** field_names = NULL;
    while (off + 4 <= end) {
        uint8_t typ = data[off];
        if (typ == 0) break;
        uint16_t len = READ_LE16(data + off + 2);
        if (off + 4 + len > end || len == 0) break;
        if (typ == 2) {
            const char* raw = (const char*)data + off + 4;
            int count = 1;
            for (uint16_t j = 0; j < len; j++) if (raw[j] == '|') count++;
            if (count != expect_count) { off += 4 + len; continue; }
            field_names = (char**)malloc(count * sizeof(char*));
            if (!field_names) return -1;
            int start = 0, idx = 0;
            for (uint16_t j = 0; j <= len && idx < count; j++) {
                if (j == len || raw[j] == '|') {
                    int flen = j - start;
                    field_names[idx] = (char*)malloc(flen + 1);
                    if (field_names[idx]) {
                        memcpy(field_names[idx], raw + start, flen);
                        field_names[idx][flen] = '\0';
                    }
                    start = j + 1;
                    idx++;
                }
            }
            uint8_t* float_flags = (uint8_t*)calloc(count, 1);
            if (float_flags) {
                for (int i = 0; i < count; i++)
                    if (strcmp(field_names[i], "longitude") == 0 || strcmp(field_names[i], "latitude") == 0)
                        float_flags[i] = 1;
            }
            *out_field_names = field_names;
            *out_float_flags = float_flags;
            return 0;
        } else if (typ == 1) {
            // version_name - will be set by caller
        }
        off += 4 + len;
    }
    return -1;
}

static int init_pools_v20(qzdb_searcher_v20_t* ctx) {
    if (ctx->pools_loaded) return 0;
    ctx->pools_loaded = 1;

    int group_count = ctx->actual_groups;
    if (group_count <= 0) return 0;

    // Allocate group_pools[group][field] pointers
    ctx->group_pools = (char****)calloc(group_count, sizeof(char***));
    ctx->group_pool_counts = (int*)calloc(group_count * QZDB_V20_MAX_FIELDS, sizeof(int));
    ctx->group_field_counts_arr = (int*)malloc(group_count * sizeof(int));
    if (!ctx->group_pools || !ctx->group_pool_counts || !ctx->group_field_counts_arr) return -1;

    for (int g = 0; g < group_count; g++)
        ctx->group_field_counts_arr[g] = ctx->group_field_counts[g];

    if (ctx->off_pools <= 0) return 0;

    uint64_t pool_end = ctx->off_meta > 0 ? ctx->off_meta : ctx->data_size;
    uint64_t cursor = ctx->off_pools;
    const uint8_t* d = ctx->data;

    for (int g = 0; g < group_count; g++) {
        int field_count = ctx->group_field_counts[g];
        if (field_count <= 0) continue;

        ctx->group_pools[g] = (char***)calloc(field_count, sizeof(char**));
        if (!ctx->group_pools[g]) return -1;

        for (int f = 0; f < field_count; f++) {
            if (cursor + 4 > pool_end) { ctx->group_pools[g][f] = NULL; continue; }
            int count = (int)READ_LE32(d + cursor);
            cursor += 4;
            if (count == 0) { ctx->group_pools[g][f] = NULL; continue; }

            uint32_t* offsets = (uint32_t*)malloc((count + 1) * sizeof(uint32_t));
            if (!offsets) return -1;
            for (int j = 0; j <= count; j++) {
                offsets[j] = READ_LE32(d + cursor);
                cursor += 4;
            }

            char** strings = (char**)calloc(count, sizeof(char*));
            if (!strings) { free(offsets); return -1; }
            for (int si = 0; si < count; si++) {
                int start = (int)offsets[si];
                int end = (int)offsets[si + 1];
                if (end > start) {
                    strings[si] = (char*)malloc(end - start + 1);
                    if (strings[si]) {
                        memcpy(strings[si], d + cursor + start, end - start);
                        strings[si][end - start] = '\0';
                    }
                }
            }
            cursor += offsets[count];
            free(offsets);

            // Store in flat group_pool_counts
            ctx->group_pool_counts[g * QZDB_V20_MAX_FIELDS + f] = count;
            ctx->group_pools[g][f] = strings;
        }
    }
    return 0;
}

static void free_pools_v20(qzdb_searcher_v20_t* ctx) {
    if (!ctx->group_pools) return;
    for (int g = 0; g < ctx->actual_groups; g++) {
        if (!ctx->group_pools[g]) continue;
        int fc = ctx->group_field_counts[g];
        for (int f = 0; f < fc; f++) {
            char** pool = ctx->group_pools[g][f];
            if (!pool) continue;
            int count = ctx->group_pool_counts[g * QZDB_V20_MAX_FIELDS + f];
            for (int i = 0; i < count; i++)
                if (pool[i]) free(pool[i]);
            free(pool);
        }
        free(ctx->group_pools[g]);
    }
    free(ctx->group_pools);
    free(ctx->group_pool_counts);
    free(ctx->group_field_counts_arr);
    ctx->group_pools = NULL;
    ctx->group_pool_counts = NULL;
    ctx->group_field_counts_arr = NULL;
}

int qzdb_v20_init(qzdb_searcher_v20_t* ctx, const char* db_path) {
    return qzdb_v20_init_group(ctx, db_path, 0);
}

int qzdb_v20_init_group(qzdb_searcher_v20_t* ctx, const char* db_path, int group_index) {
    memset(ctx, 0, sizeof(*ctx));
    ctx->group_index = group_index;

    int fd = open(db_path, O_RDONLY);
    if (fd < 0) return -1;
    struct stat st;
    if (fstat(fd, &st) != 0) { close(fd); return -1; }
    ctx->data_size = st.st_size;
    ctx->data = (uint8_t*)mmap(NULL, ctx->data_size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (ctx->data == MAP_FAILED) { ctx->data = NULL; return -1; }
    madvise(ctx->data, ctx->data_size, MADV_RANDOM);

    const uint8_t* d = ctx->data;
    if (ctx->data_size < 192 || memcmp(d, "QZ20", 4) != 0) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return -1;
    }

    ctx->fmt_ver = d[4];
    if (ctx->fmt_ver != 2 && ctx->fmt_ver != 3 && ctx->fmt_ver != 4) {
        munmap(ctx->data, ctx->data_size); ctx->data = NULL; return -1;
    }

    ctx->flags = READ_LE16(d + 8);
    ctx->has_v4 = (ctx->flags & 1) ? 1 : 0;
    ctx->has_v6 = (ctx->flags & 2) ? 1 : 0;

    ctx->v6_jump_bits = d[11];
    if (ctx->v6_jump_bits == 0) ctx->v6_jump_bits = 16;

    ctx->pool_count = d[12];
    ctx->pool_idx_size = d[13];
    ctx->geo_count = READ_LE16(d + 14);
    ctx->row_count = READ_LE32(d + 20);
    ctx->v4_node_count = READ_LE32(d + 152);
    ctx->v6_node_count = READ_LE32(d + 156);
    ctx->ip_row_size = READ_LE32(d + 160);
    ctx->geo_entry_group_count = READ_LE32(d + 164);

    uint32_t hs = READ_LE32(d + 36);
    if (hs != 192) { munmap(ctx->data, ctx->data_size); ctx->data = NULL; return -1; }

    ctx->off_v4_jump = READ_LE64(d + 64);
    ctx->off_v4_nodes = READ_LE64(d + 72);
    ctx->off_v6_jump = READ_LE64(d + 80);
    ctx->off_v6_nodes = READ_LE64(d + 88);
    ctx->off_ip_row = READ_LE64(d + 96);
    ctx->off_geo_entries = READ_LE64(d + 104);
    ctx->off_pools = READ_LE64(d + 136);
    ctx->off_meta = READ_LE64(d + 144);

    // Bounds validation for section offsets
    if (ctx->off_v4_jump + 65536 * 4 > ctx->data_size) return -1;
    if (ctx->off_v4_nodes + (uint64_t)ctx->v4_node_count * 8 > ctx->data_size) return -1;
    if (ctx->off_pools > ctx->data_size) return -1;
    if (ctx->off_meta > ctx->data_size) return -1;

    // GeoEntryOffsets[4]
    for (int i = 0; i < 4; i++)
        ctx->group_entry_offsets[i] = read_u48(d + 168 + i * 6);

    // Parse GroupMetadataTable
    uint64_t gmOff = ctx->off_geo_entries;
    int groupCount = (int)d[gmOff++];

    ctx->actual_groups = groupCount < 1 ? 1 : groupCount;
    if (ctx->geo_entry_group_count > 0 && (int)ctx->geo_entry_group_count < ctx->actual_groups)
        ctx->actual_groups = (int)ctx->geo_entry_group_count;
    if (ctx->actual_groups > 4) ctx->actual_groups = 4;

    for (int gi = 0; gi < ctx->actual_groups && gi < QZDB_V20_MAX_GROUPS; gi++) {
        ctx->group_field_counts[gi] = d[gmOff++];
        if (ctx->fmt_ver >= 4) {
            ctx->group_entry_counts[gi] = READ_LE32(d + gmOff);
            gmOff += 4;
        } else {
            ctx->group_entry_counts[gi] = READ_LE16(d + gmOff);
            gmOff += 2;
        }
        if (ctx->fmt_ver >= 3) {
            ctx->group_dim_masks[gi] = READ_LE16(d + gmOff);
            gmOff += 2;
        } else {
            ctx->group_dim_masks[gi] = (gi != 2) ? 0x01 : 0x02;
        }
    }

    // Fallback for empty group
    if (ctx->actual_groups == 1 && groupCount == 0) {
        ctx->group_field_counts[0] = ctx->pool_count;
        ctx->group_entry_counts[0] = ctx->geo_count;
        ctx->group_dim_masks[0] = 0x01;
        ctx->group_entry_offsets[0] = 0;
    }

    // Read metadata
    if ((ctx->flags & 4) && ctx->off_meta > 0 && ctx->off_meta + 4 <= ctx->data_size) {
        uint64_t pos = ctx->off_meta;
        uint64_t end = ctx->data_size;
        while (pos + 4 <= end) {
            uint8_t typ = d[pos];
            if (typ == 0) break;
            uint16_t len = READ_LE16(d + pos + 2);
            if (pos + 4 + len > end || len == 0) break;
            if (typ == 1) {
                ctx->version_name = (char*)malloc(len + 1);
                if (ctx->version_name) {
                    memcpy(ctx->version_name, d + pos + 4, len);
                    ctx->version_name[len] = '\0';
                }
            }
            pos += 4 + len;
        }
        if (parse_metadata_fields_v20(ctx->data, ctx->data_size, ctx->off_meta,
                                       &ctx->field_names, &ctx->float_field_flags,
                                       ctx->group_field_counts[0]) == 0) {
            ctx->field_names_count = ctx->group_field_counts[0];
        }
    }

    return 0;
}

void qzdb_v20_free(qzdb_searcher_v20_t* ctx) {
    if (!ctx->data) return;
    free_pools_v20(ctx);
    if (ctx->field_names) {
        for (int i = 0; i < ctx->field_names_count; i++)
            if (ctx->field_names[i]) free(ctx->field_names[i]);
        free(ctx->field_names);
    }
    free(ctx->float_field_flags);
    free(ctx->version_name);
    munmap(ctx->data, ctx->data_size);
    memset(ctx, 0, sizeof(*ctx));
}

void qzdb_v20_set_group(qzdb_searcher_v20_t* ctx, int group_index) {
    ctx->group_index = group_index;
}

// ── PATRICIA Trie V4 ──

static uint32_t trie_walk_v4(qzdb_searcher_v20_t* ctx, uint32_t ip_int) {
    const uint8_t* d = ctx->data;
    uint32_t hi16 = (ip_int >> 16) & 0xFFFF;
    uint32_t ptr = READ_LE32(d + ctx->off_v4_jump + hi16 * 4);
    if (ptr == 0) return 0;
    if (ptr & V20_SENTINEL) return ptr & 0x7FFFFFFF;

    uint32_t idx = ptr;
    uint32_t suffix = (ip_int & 0xFFFF) << 16;
    uint64_t nodes_off = ctx->off_v4_nodes;
    uint32_t steps = 0;

    while (1) {
        if (++steps > 32) return 0;
        if (idx >= ctx->v4_node_count) return 0;
        uint32_t bit = (suffix >> 31) & 1;
        uint32_t child = READ_LE32(d + nodes_off + idx * 8 + bit * 4);
        if (child == 0) return 0;
        if (child & V20_SENTINEL) return child & 0x7FFFFFFF;
        idx = child;
        suffix <<= 1;
    }
}

// ── PATRICIA Trie V6 ──

static uint32_t trie_walk_v6(qzdb_searcher_v20_t* ctx, uint64_t ip_hi, uint64_t ip_lo) {
    const uint8_t* d = ctx->data;
    int shift = 128 - ctx->v6_jump_bits;

    uint64_t idx_jump;
    if (shift >= 64)
        idx_jump = ip_hi >> (shift - 64);
    else
        idx_jump = (ip_hi << (64 - shift)) | (ip_lo >> shift);
    uint64_t mask = (1ULL << ctx->v6_jump_bits) - 1;
    idx_jump &= mask;

    uint32_t ptr = READ_LE32(d + ctx->off_v6_jump + idx_jump * 4);
    if (ptr == 0) return 0;
    if (ptr & V20_SENTINEL) return ptr & 0x7FFFFFFF;

    uint32_t idx = ptr;
    int depth = ctx->v6_jump_bits;
    uint64_t nodes_off = ctx->off_v6_nodes;

    while (depth < 128) {
        if (idx >= ctx->v6_node_count) return 0;
        int bit_pos = 127 - depth;
        int bit = bit_pos >= 64
            ? (int)((ip_hi >> (bit_pos - 64)) & 1)
            : (int)((ip_lo >> bit_pos) & 1);

        uint32_t child = READ_LE32(d + nodes_off + idx * 8 + (uint32_t)bit * 4);
        if (child == 0) return 0;
        if (child & V20_SENTINEL) return child & 0x7FFFFFFF;
        idx = child;
        depth++;
    }
    return 0;
}

// ── IPRow Reading ──

static void read_ip_row(qzdb_searcher_v20_t* ctx, uint32_t row_id, uint32_t* geo, uint32_t* asn, uint32_t* usage) {
    *geo = 0; *asn = 0; *usage = 0;
    if (row_id <= 0 || row_id >= ctx->row_count) return;
    uint64_t off = ctx->off_ip_row + (uint64_t)row_id * ctx->ip_row_size;
    const uint8_t* d = ctx->data + off;
    *geo = (uint32_t)d[0] | ((uint32_t)d[1] << 8) | ((uint32_t)d[2] << 16);
    *asn = (uint32_t)d[3] | ((uint32_t)d[4] << 8) | ((uint32_t)d[5] << 16);
    if (ctx->ip_row_size >= 9)
        *usage = (uint32_t)d[6] | ((uint32_t)d[7] << 8) | ((uint32_t)d[8] << 16);
}

// ── GeoEntry Resolution ──

static int resolve_geo(qzdb_searcher_v20_t* ctx, uint32_t entry_id, int group_index, qzdb_geo_info_v20_t* result) {
    if (group_index >= ctx->actual_groups) return -1;
    if (entry_id < 0 || entry_id >= ctx->group_entry_counts[group_index]) return -1;

    if (init_pools_v20(ctx) != 0) return -1;

    int field_count = ctx->group_field_counts[group_index];
    if (field_count <= 0) return -1;

    uint64_t group_entry_start = ctx->off_geo_entries + ctx->group_entry_offsets[group_index];
    uint64_t entry_off = group_entry_start + (uint64_t)entry_id * field_count * ctx->pool_idx_size;
    const uint8_t* d = ctx->data;

    uint32_t pool_idxs[QZDB_V20_MAX_FIELDS];
    for (int i = 0; i < field_count && i < QZDB_V20_MAX_FIELDS; i++) {
        switch (ctx->pool_idx_size) {
            case 2: pool_idxs[i] = READ_LE16(d + entry_off); break;
            case 3: pool_idxs[i] = read_u24(d + entry_off); break;
            default: pool_idxs[i] = READ_LE32(d + entry_off); break;
        }
        entry_off += ctx->pool_idx_size;
    }

    if (!ctx->group_pools || !ctx->group_pools[group_index]) return -1;

    char*** group_pool = ctx->group_pools[group_index];
    memset(result, 0, sizeof(*result));

    for (int i = 0; i < field_count && i < QZDB_V20_MAX_FIELDS; i++) {
        uint32_t idx = pool_idxs[i];
        if (group_pool[i] && (int)idx < ctx->group_pool_counts[group_index * QZDB_V20_MAX_FIELDS + i]) {
            result->values[i] = group_pool[i][idx];
        }
    }
    return 0;
}

static int resolve_row_id(qzdb_searcher_v20_t* ctx, uint32_t row_id, int group_index, qzdb_geo_info_v20_t* result) {
    uint32_t geo_id, asn_id, usage_id;
    read_ip_row(ctx, row_id, &geo_id, &asn_id, &usage_id);

    uint16_t mask = (group_index >= 0 && group_index < ctx->actual_groups)
        ? ctx->group_dim_masks[group_index] : 0;
    uint32_t entry_id = (mask & 0x02) ? asn_id : ((mask & 0x04) ? usage_id : geo_id);
    if (entry_id == 0) return -1;
    return resolve_geo(ctx, entry_id, group_index, result);
}

// ── Public API ──

int qzdb_v20_find_uint(qzdb_searcher_v20_t* ctx, uint32_t ip_int, qzdb_geo_info_v20_t* result) {
    if (!ctx->has_v4) return -1;
    uint32_t row_id = trie_walk_v4(ctx, ip_int);
    if (row_id == 0) return -1;
    return resolve_row_id(ctx, row_id, ctx->group_index, result);
}

int qzdb_v20_find_v6(qzdb_searcher_v20_t* restrict ctx, uint64_t ip_high, uint64_t ip_low,
                     qzdb_geo_info_v20_t* restrict result) {
    if (!ctx->has_v6) return -1;
    uint32_t row_id = trie_walk_v6(ctx, ip_high, ip_low);
    if (row_id == 0) return -1;
    return resolve_row_id(ctx, row_id, ctx->group_index, result);
}

int qzdb_v20_find(qzdb_searcher_v20_t* ctx, const char* ip_str, qzdb_geo_info_v20_t* result) {
    if (!ip_str || !*ip_str) return -1;

    // Parse group specifier "ip!group"
    int group_idx = ctx->group_index;
    const char* bang = strchr(ip_str, '!');
    char ip_buf[64];
    if (bang) {
        int g = atoi(bang + 1);
        if (g > 0 || (bang[1] == '0')) group_idx = g;
        size_t prefix_len = (size_t)(bang - ip_str);
        if (prefix_len >= sizeof(ip_buf)) prefix_len = sizeof(ip_buf) - 1;
        memcpy(ip_buf, ip_str, prefix_len);
        ip_buf[prefix_len] = '\0';
        ip_str = ip_buf;
    }

    if (strchr(ip_str, ':')) {
        struct in6_addr addr;
        if (inet_pton(AF_INET6, ip_str, &addr) != 1) return -1;
        // Embedded IPv4
        if (addr.s6_addr[0] == 0 && addr.s6_addr[1] == 0 && addr.s6_addr[2] == 0 &&
            addr.s6_addr[3] == 0 && addr.s6_addr[4] == 0 && addr.s6_addr[5] == 0 &&
            addr.s6_addr[6] == 0 && addr.s6_addr[7] == 0 && addr.s6_addr[8] == 0 &&
            addr.s6_addr[9] == 0 && addr.s6_addr[10] == 0xff && addr.s6_addr[11] == 0xff) {
            uint32_t ip_int = ((uint32_t)addr.s6_addr[12] << 24) |
                              ((uint32_t)addr.s6_addr[13] << 16) |
                              ((uint32_t)addr.s6_addr[14] << 8)  |
                              (uint32_t)addr.s6_addr[15];
            uint32_t row_id = trie_walk_v4(ctx, ip_int);
            if (row_id == 0) return -1;
            return resolve_row_id(ctx, row_id, group_idx, result);
        }
        uint64_t high = ((uint64_t)addr.s6_addr[0] << 56) | ((uint64_t)addr.s6_addr[1] << 48) |
                        ((uint64_t)addr.s6_addr[2] << 40) | ((uint64_t)addr.s6_addr[3] << 32) |
                        ((uint64_t)addr.s6_addr[4] << 24) | ((uint64_t)addr.s6_addr[5] << 16) |
                        ((uint64_t)addr.s6_addr[6] << 8)  | (uint64_t)addr.s6_addr[7];
        uint64_t low  = ((uint64_t)addr.s6_addr[8]  << 56) | ((uint64_t)addr.s6_addr[9]  << 48) |
                        ((uint64_t)addr.s6_addr[10] << 40) | ((uint64_t)addr.s6_addr[11] << 32) |
                        ((uint64_t)addr.s6_addr[12] << 24) | ((uint64_t)addr.s6_addr[13] << 16) |
                        ((uint64_t)addr.s6_addr[14] << 8)  | (uint64_t)addr.s6_addr[15];
        uint32_t row_id = trie_walk_v6(ctx, high, low);
        if (row_id == 0) return -1;
        return resolve_row_id(ctx, row_id, group_idx, result);
    }

    uint32_t ip_int = 0, val = 0;
    int dots = 0;
    const char* p = ip_str;
    while (*p) {
        if (*p >= '0' && *p <= '9') val = val * 10 + (unsigned)(*p - '0');
        else if (*p == '.') { ip_int = (ip_int << 8) | val; val = 0; dots++; }
        else return -1;
        p++;
    }
    if (dots != 3) return -1;
    ip_int = (ip_int << 8) | val;

    uint32_t row_id = trie_walk_v4(ctx, ip_int);
    if (row_id == 0) return -1;
    return resolve_row_id(ctx, row_id, group_idx, result);
}

int qzdb_v20_find_str(qzdb_searcher_v20_t* ctx, const char* ip_str, char* out, size_t out_size) {
    qzdb_geo_info_v20_t info;
    if (qzdb_v20_find(ctx, ip_str, &info) != 0) {
        if (out_size > 0) out[0] = '\0';
        return -1;
    }

    // Determine field count
    int field_count = ctx->group_field_counts[ctx->group_index];

    size_t pos = 0;
    for (int i = 0; i < field_count && i < QZDB_V20_MAX_FIELDS; i++) {
        if (i > 0 && pos < out_size - 1) out[pos++] = '|';
        const char* val = info.values[i];
        if (!val || !*val) continue;

        char float_buf[64];
        if (ctx->float_field_flags && i < ctx->field_names_count && ctx->float_field_flags[i]) {
            double f = atof(val);
            int flen = snprintf(float_buf, sizeof(float_buf), "%.6f", f);
            val = float_buf;
            size_t wlen = (size_t)flen < out_size - pos - 1 ? (size_t)flen : out_size - pos - 1;
            if (wlen > 0) { memcpy(out + pos, val, wlen); pos += wlen; }
            continue;
        }
        size_t len = strlen(val);
        if (pos + len >= out_size) {
            if (out_size > pos) { memcpy(out + pos, val, out_size - pos - 1); pos = out_size - 1; }
            break;
        }
        memcpy(out + pos, val, len);
        pos += len;
    }
    out[pos] = '\0';
    return 0;
}

int qzdb_v20_verify_crc(qzdb_searcher_v20_t* ctx) {
    if (!ctx->data || ctx->data_size < 20) return 0;
    uint32_t stored = READ_LE32(ctx->data + 16);
    uint8_t orig[4];
    memcpy(orig, ctx->data + 16, 4);
    uint32_t computed = crc32_calc(ctx->data, ctx->data_size);
    return stored == computed ? 1 : 0;
}
