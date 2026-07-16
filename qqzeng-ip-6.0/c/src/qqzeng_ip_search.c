#include "qqzeng_ip_search.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define INDEX_START_INDEX 0x30004
#define END_MASK 0x800000
#define COMPL_MASK (~END_MASK)

// 内部辅助函数
static uint32_t read_int24(const uint8_t *data, uint32_t offset) {
    return (data[offset] << 16) | (data[offset+1] << 8) | data[offset+2];
}

static int fast_parse_ip(const char *ip, uint32_t *out_val) {
    uint32_t val = 0;
    uint32_t result = 0;
    int shift = 24;
    const char *p = ip;
    
    while (*p) {
        if (*p >= '0' && *p <= '9') {
            val = val * 10 + (*p - '0');
        } else if (*p == '.') {
            if (val > 255) return -1;
            result |= (val << shift);
            val = 0;
            shift -= 8;
        } else {
            return -1;
        }
        p++;
    }
    
    if (val > 255 || shift != 0) return -1;
    result |= val;
    *out_val = result;
    return 0;
}

static void split_string(char *str, char ***arr, uint32_t *count) {
    uint32_t capacity = 10000;
    *arr = (char**)malloc(sizeof(char*) * capacity);
    *count = 0;
    
    char *token = str;
    char *next = NULL;
    
    while (token) {
        char *tab = strchr(token, '\t');
        if (tab) {
            *tab = '\0';
            next = tab + 1;
        } else {
            next = NULL;
        }
        
        if (*count >= capacity) {
            capacity *= 2;
            *arr = (char**)realloc(*arr, sizeof(char*) * capacity);
        }
        
        (*arr)[*count] = token;
        (*count)++;
        
        token = next;
    }
}

ipdb_search_t* ipdb_init(const char *db_path) {
    FILE *f = fopen(db_path, "rb");
    if (!f) return NULL;
    
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    
    if (size < INDEX_START_INDEX) {
        fclose(f);
        return NULL;
    }
    
    ipdb_search_t *ctx = (ipdb_search_t*)calloc(1, sizeof(ipdb_search_t));
    if (!ctx) {
        fclose(f);
        return NULL;
    }
    
    ctx->data_size = (uint32_t)size;
    ctx->data = (uint8_t*)malloc(ctx->data_size + 1);
    if (!ctx->data) {
        free(ctx);
        fclose(f);
        return NULL;
    }
    
    if (fread(ctx->data, 1, ctx->data_size, f) != ctx->data_size) {
        free(ctx->data);
        free(ctx);
        fclose(f);
        return NULL;
    }
    ctx->data[ctx->data_size] = '\0';
    fclose(f);
    
    ctx->node_count = ctx->data[0] | (ctx->data[1] << 8) | (ctx->data[2] << 16) | (ctx->data[3] << 24);
    
    uint32_t string_area_offset = INDEX_START_INDEX + ctx->node_count * 6;
    if (string_area_offset > ctx->data_size) {
        ipdb_free(ctx);
        return NULL;
    }
    
    split_string((char*)(ctx->data + string_area_offset), &ctx->geoisp_arr, &ctx->geoisp_count);
    return ctx;
}

const char* ipdb_find(ipdb_search_t *ctx, const char *ip) {
    if (!ctx || !ip) return "";
    uint32_t ip_val;
    if (fast_parse_ip(ip, &ip_val) != 0) return "";
    return ipdb_find_uint(ctx, ip_val);
}

const char* ipdb_find_uint(ipdb_search_t *ctx, uint32_t ip_int) {
    uint16_t prefix = (uint16_t)(ip_int >> 16);
    uint16_t suffix = (uint16_t)(ip_int & 0xFFFF);
    
    uint32_t record = read_int24(ctx->data, 4 + prefix * 3);
    
    while ((record & END_MASK) != END_MASK) {
        int bit = (suffix >> 15) & 1;
        uint32_t offset = INDEX_START_INDEX + record * 6 + bit * 3;
        record = read_int24(ctx->data, offset);
        suffix <<= 1;
    }
    
    uint32_t index = record & (~0x800000 & 0xFFFFFF); 
    
    if (index < ctx->geoisp_count) {
        return ctx->geoisp_arr[index];
    }
    return "";
}

void ipdb_free(ipdb_search_t *ctx) {
    if (ctx) {
        if (ctx->data) free(ctx->data);
        if (ctx->geoisp_arr) free(ctx->geoisp_arr);
        free(ctx);
    }
}
