/**
 * QZDB C SDK — 独立 CSV 地面真值 Oracle（与 Python/Go/PHP/Rust 同标准）。
 *
 * 以 `.qzdb` 的源数据 test_data_202608/{std,ult}/china/*_range.csv 为裁判
 * （带 start_ip_num/end_ip_num + 地理字段），对 std_china / ult_china 两个库
 * 各自做：区间内随机 + 全局随机 共约 11000 样本，比对 qzdb_find() 的
 * country/province/city/isp 与 CSV 一致。证明 SDK "答得对"（非自洽）。
 *
 * 解析器逐字段处理双引号与内嵌逗号（ult 的 languages 字段即 "a,b,c"），
 * 并按表头列名定位字段（std 10 列 / ult 29 列 列序不同）。
 *
 * Compile:
 *   gcc -O2 -o csv_oracle qzdb_reader.c csv_oracle.c -lpthread
 * Run (from c/):
 *   ./csv_oracle            # 默认 ../data 与 ../test_data_202608
 *   ./csv_oracle <data_dir> <testdata_dir>
 */

#include "qzdb_reader.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#define MAX_FIELDS 32
static char EMPTY[] = "";

/* Parse one CSV line in-place (may modify buf). Fills fields[] with pointers
 * into buf; returns field count. Handles double-quoted fields with embedded
 * commas and "" escapes. Pads fields[] up to ncols with EMPTY. */
static int parse_csv_line(char* buf, char* fields[], int ncols) {
    int nf = 0;
    char* p = buf;
    while (*p && nf < MAX_FIELDS) {
        char* fs;
        if (*p == '"') {
            p++;                 /* skip opening quote */
            fs = p;              /* unescaped content starts here */
            char* dst = p;
            while (*p) {
                if (*p == '"') {
                    if (*(p + 1) == '"') { *dst++ = '"'; p += 2; }
                    else { p++; break; }   /* closing quote */
                } else {
                    *dst++ = *p++;
                }
            }
            *dst = '\0';
            fields[nf++] = fs;
            if (*p == ',') p++;   /* skip delimiter */
        } else {
            fs = p;
            while (*p && *p != ',' && *p != '\n' && *p != '\r') p++;
            if (*p == ',') { *p = '\0'; p++; }
            else { *p = '\0'; }
            fields[nf++] = fs;
        }
    }
    while (nf < ncols) fields[nf++] = EMPTY;
    return nf;
}

typedef struct {
    uint32_t start, end;
    char* country;
    char* province;
    char* city;
    char* isp;
} range_t;

static range_t* g_ranges = NULL;
static int g_nranges = 0, g_cap = 0;
static int g_idx_start = -1, g_idx_end = -1, g_idx_country = -1,
           g_idx_prov = -1, g_idx_city = -1, g_idx_isp = -1;
static int g_ncols = 0;

static void add_range(uint32_t s, uint32_t e, const char* c, const char* pr,
                      const char* ci, const char* is) {
    if (g_nranges >= g_cap) {
        g_cap = g_cap ? g_cap * 2 : 1024;
        g_ranges = (range_t*)realloc(g_ranges, (size_t)g_cap * sizeof(range_t));
    }
    range_t* r = &g_ranges[g_nranges++];
    r->start = s; r->end = e;
    r->country = strdup(c ? c : "");
    r->province = strdup(pr ? pr : "");
    r->city = strdup(ci ? ci : "");
    r->isp = strdup(is ? is : "");
}

static void free_ranges(void) {
    for (int i = 0; i < g_nranges; i++) {
        free(g_ranges[i].country);
        free(g_ranges[i].province);
        free(g_ranges[i].city);
        free(g_ranges[i].isp);
    }
    g_nranges = 0;
}

static int load_ranges(const char* csv_path) {
    FILE* f = fopen(csv_path, "rb");
    if (!f) { fprintf(stderr, "cannot open %s\n", csv_path); return -1; }
    char line[8192];
    int first = 1;
    while (fgets(line, sizeof(line), f)) {
        int L = (int)strlen(line);
        while (L > 0 && (line[L - 1] == '\n' || line[L - 1] == '\r')) line[--L] = '\0';
        if (first) {
            first = 0;
            char* hdr[MAX_FIELDS];
            g_ncols = parse_csv_line(line, hdr, MAX_FIELDS);
            for (int i = 0; i < g_ncols; i++) {
                char* h = hdr[i];
                int H = (int)strlen(h);
                while (H > 0 && (h[H - 1] == '\r' || h[H - 1] == '\n')) h[--H] = '\0';
                if (!strcmp(h, "start_ip_num")) g_idx_start = i;
                else if (!strcmp(h, "end_ip_num")) g_idx_end = i;
                else if (!strcmp(h, "country")) g_idx_country = i;
                else if (!strcmp(h, "province")) g_idx_prov = i;
                else if (!strcmp(h, "city")) g_idx_city = i;
                else if (!strcmp(h, "isp")) g_idx_isp = i;
            }
            if (g_idx_start < 0 || g_idx_end < 0 || g_idx_country < 0 ||
                g_idx_prov < 0 || g_idx_city < 0 || g_idx_isp < 0) {
                fprintf(stderr, "missing required columns in %s\n", csv_path);
                fclose(f);
                return -1;
            }
            continue;
        }
        if (line[0] == '\0') continue;
        char* flds[MAX_FIELDS];
        int nf = parse_csv_line(line, flds, g_ncols);
        const char* s = (g_idx_start < nf) ? flds[g_idx_start] : EMPTY;
        const char* e = (g_idx_end < nf) ? flds[g_idx_end] : EMPTY;
        const char* co = (g_idx_country < nf) ? flds[g_idx_country] : EMPTY;
        const char* pr = (g_idx_prov < nf) ? flds[g_idx_prov] : EMPTY;
        const char* ci = (g_idx_city < nf) ? flds[g_idx_city] : EMPTY;
        const char* is = (g_idx_isp < nf) ? flds[g_idx_isp] : EMPTY;
        uint64_t start = strtoull(s, NULL, 10);
        uint64_t end = strtoull(e, NULL, 10);
        if (start == 0 && end == 0) continue;
        /* Skip IPv6 ranges: this oracle only emits IPv4 query strings, so we
         * validate the database's IPv4 coverage here (matching the v4 portion
         * of the other languages' CSV oracles). The 128-bit start/end would
         * otherwise overflow uint32_t and poison the sorted-range binary search. */
        if (start > 0xFFFFFFFFULL || end > 0xFFFFFFFFULL) continue;
        add_range((uint32_t)start, (uint32_t)end, co, pr, ci, is);
    }
    fclose(f);
    return 0;
}

/* binary search: largest start <= ip; -1 if ip not within that interval. */
static int find_range(uint32_t ip) {
    int lo = 0, hi = g_nranges - 1, ans = -1;
    while (lo <= hi) {
        int mid = (lo + hi) / 2;
        if (g_ranges[mid].start <= ip) { ans = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    if (ans >= 0 && g_ranges[ans].end >= ip) return ans;
    return -1;
}

static void u32_to_ip(uint32_t ip, char* buf) {
    sprintf(buf, "%u.%u.%u.%u",
            (ip >> 24) & 255, (ip >> 16) & 255, (ip >> 8) & 255, ip & 255);
}

static const char* trim_start(const char* s) {
    while (*s == ' ' || *s == '\t') s++;
    return s;
}
static int eq_trim(const char* a, const char* b) {
    a = trim_start(a); b = trim_start(b);
    int la = (int)strlen(a);
    while (la > 0 && (a[la - 1] == ' ' || a[la - 1] == '\t' ||
                      a[la - 1] == '\r' || a[la - 1] == '\n')) la--;
    int lb = (int)strlen(b);
    while (lb > 0 && (b[lb - 1] == ' ' || b[lb - 1] == '\t' ||
                      b[lb - 1] == '\r' || b[lb - 1] == '\n')) lb--;
    if (la != lb) return 0;
    return strncmp(a, b, (size_t)la) == 0;
}

static int run_one(const char* data_dir, const char* testdata_dir,
                   const char* ver, uint64_t* out_checked, uint64_t* out_mism) {
    char db_path[2048], csv_path[2048];
    snprintf(db_path, sizeof(db_path), "%s/qqzeng_ip_%s_china.qzdb", data_dir, ver);
    snprintf(csv_path, sizeof(csv_path), "%s/%s/china/qqzeng_ip_%s_china_range.csv",
             testdata_dir, ver, ver);

    free_ranges();
    if (load_ranges(csv_path) != 0) return -1;
    if (g_nranges == 0) { fprintf(stderr, "no ranges in %s\n", csv_path); return -1; }

    qzdb_reader_t ctx;
    if (qzdb_init_ex(&ctx, db_path, 1) != QZDB_OK) {
        fprintf(stderr, "cannot load %s\n", db_path);
        return -1;
    }

    uint64_t checked = 0, mism = 0;
    const uint64_t IN_RANGE_N = 6000, GLOBAL_N = 5000;

    /* in-range random sampling (strict) */
    for (uint64_t i = 0; i < IN_RANGE_N; i++) {
        int ri = (int)(rand() % g_nranges);
        range_t* rg = &g_ranges[ri];
        uint64_t len = (uint64_t)rg->end - rg->start + 1;
        uint64_t off;
        if (len > (1ULL << 20)) {
            int k = rand() % 3;
            off = (k == 0) ? 0 : (k == 1 ? len / 2 : len - 1);
        } else {
            off = (uint64_t)(rand() % len);
        }
        uint32_t ip = rg->start + (uint32_t)off;
        char ipstr[64]; u32_to_ip(ip, ipstr);
        qzdb_geo_info_t info;
        int rc = qzdb_find(&ctx, ipstr, &info);
        if (rc == QZDB_OK) {
            checked++;
            const char* c = qzdb_geo_info_get(&ctx, &info, "country");
            const char* pr = qzdb_geo_info_get(&ctx, &info, "province");
            const char* ci = qzdb_geo_info_get(&ctx, &info, "city");
            const char* is = qzdb_geo_info_get(&ctx, &info, "isp");
            if (!eq_trim(c, rg->country) || !eq_trim(pr, rg->province) ||
                !eq_trim(ci, rg->city) || !eq_trim(is, rg->isp)) {
                mism++;
                if (mism <= 10)
                    fprintf(stderr,
                            "  MISMATCH[in-range] ip=%s expect(c=%s,p=%s,ci=%s,i=%s) got(c=%s,p=%s,ci=%s,i=%s)\n",
                            ipstr, rg->country, rg->province, rg->city, rg->isp,
                            c, pr, ci, is);
            }
            qzdb_free_geo_info(&info);
        } else {
            mism++;
            if (mism <= 10)
                fprintf(stderr, "  MISMATCH[in-range] ip=%s expected geo but SDK None\n", ipstr);
        }
    }

    /* global random: hit must match; miss must be None (catch false positives) */
    for (uint64_t i = 0; i < GLOBAL_N; i++) {
        uint32_t ip = ((uint32_t)rand() << 20) ^ ((uint32_t)rand() << 10) ^ (uint32_t)rand();
        int idx = find_range(ip);
        char ipstr[64]; u32_to_ip(ip, ipstr);
        if (idx >= 0) {
            range_t* rg = &g_ranges[idx];
            qzdb_geo_info_t info;
            int rc = qzdb_find(&ctx, ipstr, &info);
            if (rc == QZDB_OK) {
                checked++;
                const char* c = qzdb_geo_info_get(&ctx, &info, "country");
                const char* pr = qzdb_geo_info_get(&ctx, &info, "province");
                const char* ci = qzdb_geo_info_get(&ctx, &info, "city");
                const char* is = qzdb_geo_info_get(&ctx, &info, "isp");
                if (!eq_trim(c, rg->country) || !eq_trim(pr, rg->province) ||
                    !eq_trim(ci, rg->city) || !eq_trim(is, rg->isp)) {
                    mism++;
                    if (mism <= 10)
                        fprintf(stderr, "  MISMATCH[global] ip=%s expect(c=%s,p=%s,ci=%s,i=%s) got(c=%s,p=%s,ci=%s,i=%s)\n",
                                ipstr, rg->country, rg->province, rg->city, rg->isp, c, pr, ci, is);
                }
                qzdb_free_geo_info(&info);
            } else {
                mism++;
                if (mism <= 10)
                    fprintf(stderr, "  MISMATCH[global] ip=%s expected geo but SDK None\n", ipstr);
            }
        } else {
            qzdb_geo_info_t info;
            if (qzdb_find(&ctx, ipstr, &info) == QZDB_OK) {
                mism++;
                if (mism <= 10)
                    fprintf(stderr, "  MISMATCH[global-miss] ip=%s SDK returned geo but CSV none\n", ipstr);
                qzdb_free_geo_info(&info);
            }
        }
    }

    qzdb_free(&ctx);
    *out_checked = checked;
    *out_mism = mism;
    return 0;
}

int main(int argc, char** argv) {
    const char* data_dir = (argc > 1) ? argv[1] : "..";
    const char* testdata_dir = (argc > 2) ? argv[2] : "..";
    srand(12345);

    const char* vers[2] = { "std", "ult" };
    int total_fail = 0;
    for (int v = 0; v < 2; v++) {
        uint64_t checked = 0, mism = 0;
        if (run_one(data_dir, testdata_dir, vers[v], &checked, &mism) != 0) {
            fprintf(stderr, "csv_oracle %s_china: SETUP FAIL\n", vers[v]);
            total_fail++;
            continue;
        }
        printf("csv_oracle %s_china: checked=%llu mism=%llu\n",
               vers[v], (unsigned long long)checked, (unsigned long long)mism);
        if (mism > 0) total_fail++;
    }

    free_ranges();
    free(g_ranges);
    if (total_fail == 0) { printf("CSV_ORACLE_PASS\n"); return 0; }
    printf("CSV_ORACLE_FAIL\n");
    return 1;
}
