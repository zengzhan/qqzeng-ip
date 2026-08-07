/**
 * QZDB C SDK — Tier2 golden verification (API_CONTRACT §10).
 *
 * Loads qqzeng_ip_std_china.qzdb and qqzeng_ip_ult_china.qzdb, then for every
 * vector in golden_vectors.json asserts find(ip) == expected (to_pipe string).
 * Not-found / invalid IPs map to "" in the golden, which matches qzdb_find_str.
 *
 * Compile:
 *   gcc -O2 -o golden_check qzdb_reader.c golden_check.c
 * Run:
 *   ./golden_check <path/to/golden_vectors.json> <path/to/data_dir>
 *
 * Must report 0 failures.
 */
#include "qzdb_reader.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Extract the string value following "key": "..." on a line. Returns 1 on hit. */
static int extract_value(const char* line, const char* key, char* buf, size_t bufsz) {
    char pat[64];
    snprintf(pat, sizeof(pat), "\"%s\"", key);
    const char* p = strstr(line, pat);
    if (!p) return 0;
    p += strlen(pat);
    while (*p && *p != ':') p++;
    if (*p == ':') p++;
    while (*p == ' ' || *p == '\t') p++;
    if (*p != '"') return 0;
    p++;
    size_t i = 0;
    while (*p && *p != '"' && i + 1 < bufsz) buf[i++] = *p++;
    buf[i] = '\0';
    return 1;
}

static int g_total = 0, g_fail = 0;

/* Detect a top-level section start line such as  "std_china": {  */
static int match_section(const char* line, const char* name) {
    char pat[64];
    snprintf(pat, sizeof(pat), "\"%s\": {", name);
    return strstr(line, pat) != NULL;
}

static void check_db(qzdb_reader_t* ctx, FILE* jf, const char* dbkey) {
    char line[1024];
    char ip[512], expected[2048], got[2048];
    int in_target = 0;   /* only process the requested top-level section */
    int have_ip = 0;
    char pending_ip[512];
    while (fgets(line, sizeof(line), jf)) {
        /* Section boundaries: entering or leaving the target section. */
        if (match_section(line, "std_china") || match_section(line, "ult_china")) {
            in_target = match_section(line, dbkey);
            have_ip = 0;
            continue;
        }
        if (!in_target) continue;
        if (extract_value(line, "ip", ip, sizeof(ip))) {
            strcpy(pending_ip, ip);
            have_ip = 1;
            continue;
        }
        if (have_ip && extract_value(line, "expected", expected, sizeof(expected))) {
            have_ip = 0;
            g_total++;
            qzdb_find_str(ctx, pending_ip, got, sizeof(got));
            if (strcmp(got, expected) != 0) {
                g_fail++;
                if (g_fail <= 20) {
                    fprintf(stderr, "  MISMATCH [%s] ip=<%s>\n    expected=<%s>\n    got=     <%s>\n",
                            dbkey, pending_ip, expected, got);
                }
            }
        }
    }
}

int main(int argc, char** argv) {
    if (argc < 3) {
        fprintf(stderr, "Usage: %s <golden_vectors.json> <data_dir>\n", argv[0]);
        return 2;
    }
    const char* golden = argv[1];
    const char* datadir = argv[2];

    char std_path[1024], ult_path[1024];
    snprintf(std_path, sizeof(std_path), "%s/qqzeng_ip_std_china.qzdb", datadir);
    snprintf(ult_path, sizeof(ult_path), "%s/qqzeng_ip_ult_china.qzdb", datadir);

    FILE* jf = fopen(golden, "r");
    if (!jf) { fprintf(stderr, "Cannot open golden: %s\n", golden); return 2; }

    qzdb_reader_t std, ult;
    if (qzdb_init_ex(&std, std_path, 1) != QZDB_OK) {
        fprintf(stderr, "Cannot load %s\n", std_path); fclose(jf); return 2;
    }
    if (qzdb_init_ex(&ult, ult_path, 1) != QZDB_OK) {
        fprintf(stderr, "Cannot load %s\n", ult_path); fclose(jf); qzdb_free(&std); return 2;
    }

    /* Rewind and check std first, then ult (separate passes). */
    rewind(jf);
    check_db(&std, jf, "std_china");
    rewind(jf);
    check_db(&ult, jf, "ult_china");

    fclose(jf);
    qzdb_free(&std);
    qzdb_free(&ult);

    printf("Tier2 golden: total=%d fail=%d\n", g_total, g_fail);
    if (g_fail == 0) { printf("TIER2_PASS\n"); return 0; }
    printf("TIER2_FAIL\n");
    return 1;
}
