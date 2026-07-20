/**
 * Batch IP query runner for C
 * Compile: clang -O3 -o batch_c batch_query.c ../c/qzdb_searcher.c -lm
 * Usage: ./batch_c <database_path> <v4_test> <v4_output> <v6_test> <v6_output>
 *
 * Reads test IPs from files, queries the QZDB database, writes results.
 */
#include "../c/qzdb_searcher.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

/* Helper: format geo info to pipe string */
static void geo_to_pipe(const qzdb_searcher_t* ctx, const qzdb_geo_info_t* r, char* buf, size_t size) {
    if (!r) { if (size > 0) buf[0] = '\0'; return; }
    buf[0] = '\0';
    size_t pos = 0;
    int nfields = ctx->field_count > 0 ? ctx->field_count : 0;
    for (int i = 0; i < nfields && pos < size; i++) {
        if (i > 0 && pos < size) buf[pos++] = '|';
        const char* v = r->values[i] ? r->values[i] : "";
        if (ctx->float_field_flags && ctx->float_field_flags[i] && v[0]) {
            double f = atof(v);
            int n = snprintf(buf + pos, size - pos, "%.6f", f);
            if (n > 0) pos += (size_t)(n < (int)(size - pos) ? n : (int)(size - pos - 1));
        } else {
            size_t vlen = strlen(v);
            size_t to_copy = vlen < (size - pos - 1) ? vlen : (size - pos - 1);
            memcpy(buf + pos, v, to_copy);
            pos += to_copy;
        }
    }
    buf[pos] = '\0';
}

static int process_v4(const qzdb_searcher_t* ctx, const char* test_path, const char* out_path) {
    FILE* f = fopen(test_path, "r");
    if (!f) { fprintf(stderr, "  C: Cannot open %s\n", test_path); return 0; }
    
    FILE* out = fopen(out_path, "w");
    if (!out) { fclose(f); fprintf(stderr, "  C: Cannot write %s\n", out_path); return 0; }
    
    char line[64];
    int count = 0;
    while (fgets(line, sizeof(line), f)) {
        char* nl = strchr(line, '\n');
        if (nl) *nl = '\0';
        if (line[0] == '\0') continue;
        
        uint32_t ip = (uint32_t)atol(line);
        qzdb_geo_info_t result;
        char pipe_buf[4096];
        
        if (qzdb_find_uint(ctx, ip, &result) == 0)
            geo_to_pipe(ctx, &result, pipe_buf, sizeof(pipe_buf));
        else
            pipe_buf[0] = '\0';
        
        fprintf(out, "%s|%s\n", line, pipe_buf);
        count++;
    }
    fclose(f);
    fclose(out);
    fprintf(stderr, "  C V4: %d queries\n", count);
    return count;
}

static int process_v6(const qzdb_searcher_t* ctx, const char* test_path, const char* out_path) {
    FILE* f = fopen(test_path, "r");
    if (!f) return 0;
    
    FILE* out = fopen(out_path, "w");
    if (!out) { fclose(f); return 0; }
    
    char line[128];
    int count = 0;
    while (fgets(line, sizeof(line), f)) {
        char* nl = strchr(line, '\n');
        if (nl) *nl = '\0';
        if (line[0] == '\0') continue;
        
        uint64_t high, low;
        sscanf(line, "%llu:%llu", &high, &low);

        /* SDK expects a 16-byte big-endian IPv6 address (high:low, 8 bytes each) */
        uint8_t ip_bin[16];
        for (int i = 0; i < 8; i++)
            ip_bin[i] = (uint8_t)((high >> (8 * (7 - i))) & 0xFF);
        for (int i = 0; i < 8; i++)
            ip_bin[8 + i] = (uint8_t)((low >> (8 * (7 - i))) & 0xFF);

        qzdb_geo_info_t result;
        char pipe_buf[4096];

        if (qzdb_find_v6(ctx, ip_bin, &result) == 0)
            geo_to_pipe(ctx, &result, pipe_buf, sizeof(pipe_buf));
        else
            pipe_buf[0] = '\0';
        
        fprintf(out, "%s|%s\n", line, pipe_buf);
        count++;
    }
    fclose(f);
    fclose(out);
    fprintf(stderr, "  C V6: %d queries\n", count);
    return count;
}

int main(int argc, char* argv[]) {
    if (argc < 5) {
        fprintf(stderr, "Usage: %s <db_path> <v4_test> <v4_out> <v6_test> <v6_out>\n", argv[0]);
        return 1;
    }
    
    qzdb_searcher_t ctx;
    if (qzdb_init(&ctx, argv[1]) != 0) {
        fprintf(stderr, "  C: Failed to load database: %s\n", argv[1]);
        return 1;
    }
    
    process_v4(&ctx, argv[2], argv[3]);
    process_v6(&ctx, argv[4], argv[5]);
    
    qzdb_free(&ctx);
    fprintf(stderr, "  C DONE\n");
    return 0;
}
