/**
 * QZDB C SDK — Tier1 unit tests (API_CONTRACT §10).
 *
 * Compile (no DB needed for the strict-parsing / normalization / resource tests):
 *   gcc -O2 -o test_main qzdb_reader.c test_main.c
 * Run:
 *   ./test_main [path/to/qqzeng_ip_std_china.qzdb]   (optional DB path)
 *
 * Most assertions (IP parsing, Mapped downgrade at parse level, field
 * normalization helpers, resource release) run WITHOUT any database. DB-backed
 * assertions (CIDR, CRC, Reload, lookup_ids) are executed only when a .qzdb
 * file is found and silently skipped otherwise.
 */
#include "qzdb_reader.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <fcntl.h>
#include <unistd.h>
#include <pthread.h>

static int g_total = 0, g_pass = 0, g_fail = 0;

#define ASSERT(cond, msg) do { \
    g_total++; \
    if (cond) { g_pass++; } \
    else { g_fail++; fprintf(stderr, "  FAIL: %s  (%s:%d)\n", msg, __FILE__, __LINE__); } \
} while (0)

#define ASSERT_EQ_HEX(a, b, msg) do { \
    g_total++; \
    if ((a) == (b)) { g_pass++; } \
    else { g_fail++; fprintf(stderr, "  FAIL: %s  got=0x%x exp=0x%x\n", msg, (unsigned)(a), (unsigned)(b)); } \
} while (0)

static const char* locate_db(int argc, char** argv) {
    if (argc > 1 && argv[1][0]) return argv[1];
    static const char* cand[] = {
        "qqzeng_ip_std_china.qzdb",
        "../data/qqzeng_ip_std_china.qzdb",
        "data/qqzeng_ip_std_china.qzdb",
        "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/data/qqzeng_ip_std_china.qzdb",
        NULL
    };
    for (int i = 0; cand[i]; i++) {
        FILE* f = fopen(cand[i], "rb");
        if (f) { fclose(f); return cand[i]; }
    }
    return NULL;
}

/* ---- Category 1: strict IPv4 parsing (no DB) ---- */
static void test_ipv4_parse(void) {
    uint32_t v4; uint8_t v6[16]; int is_v4;
    ASSERT(qzdb_parse_ip("0.0.0.0", &v4, v6, &is_v4) == 1, "parse 0.0.0.0");
    ASSERT(is_v4 == 1 && v4 == 0, "0.0.0.0 -> v4 0");
    ASSERT(qzdb_parse_ip("255.255.255.255", &v4, v6, &is_v4) == 1, "parse 255.255.255.255");
    ASSERT(is_v4 == 1 && v4 == 0xFFFFFFFFu, "255.255.255.255 -> 0xFFFFFFFF");
    ASSERT(qzdb_parse_ip("192.168.1.1", &v4, v6, &is_v4) == 1, "parse 192.168.1.1");
    ASSERT(is_v4 == 1 && v4 == 0xC0A80101u, "192.168.1.1 -> 0xC0A80101");
    ASSERT(qzdb_parse_ip("1.2.3.4", &v4, v6, &is_v4) == 1, "parse 1.2.3.4");

    /* rejected: missing segment */
    ASSERT(qzdb_parse_ip("1.2.3", NULL, NULL, NULL) == 0, "reject 1.2.3 (3 segs)");
    ASSERT(qzdb_parse_ip("1.2.3.4.5", NULL, NULL, NULL) == 0, "reject 1.2.3.4.5");
    ASSERT(qzdb_parse_ip("", NULL, NULL, NULL) == 0, "reject empty");
    /* rejected: octet > 255 */
    ASSERT(qzdb_parse_ip("256.1.1.1", NULL, NULL, NULL) == 0, "reject 256.x");
    ASSERT(qzdb_parse_ip("1.2.3.999", NULL, NULL, NULL) == 0, "reject .999");
    /* rejected: leading zero */
    ASSERT(qzdb_parse_ip("01.1.1.1", NULL, NULL, NULL) == 0, "reject leading zero");
    ASSERT(qzdb_parse_ip("1.02.3.4", NULL, NULL, NULL) == 0, "reject mid leading zero");
    /* rejected: >3 digits per octet */
    ASSERT(qzdb_parse_ip("1.2.3.1234", NULL, NULL, NULL) == 0, "reject 4-digit octet");
    /* rejected: whitespace */
    ASSERT(qzdb_parse_ip("1.2.3.4 ", NULL, NULL, NULL) == 0, "reject trailing space");
    ASSERT(qzdb_parse_ip(" 1.2.3.4", NULL, NULL, NULL) == 0, "reject leading space");
    ASSERT(qzdb_parse_ip("1.2.3. 4", NULL, NULL, NULL) == 0, "reject inner space");
    /* rejected: CIDR suffix */
    ASSERT(qzdb_parse_ip("1.2.3.4/24", NULL, NULL, NULL) == 0, "reject CIDR suffix");
    /* rejected: trailing dot */
    ASSERT(qzdb_parse_ip("1.2.3.4.", NULL, NULL, NULL) == 0, "reject trailing dot");
}

/* ---- Category 1b: strict IPv6 parsing (no DB) ---- */
static void test_ipv6_parse(void) {
    uint8_t v6[16]; int is_v4;
    ASSERT(qzdb_parse_ip("::1", NULL, v6, &is_v4) == 1, "parse ::1");
    ASSERT(is_v4 == 0, "::1 is v6");
    ASSERT(qzdb_parse_ip("::", NULL, v6, &is_v4) == 1, "parse ::");
    ASSERT(qzdb_parse_ip("2001:db8::1", NULL, v6, &is_v4) == 1, "parse 2001:db8::1");
    ASSERT(qzdb_parse_ip("fe80::1", NULL, v6, &is_v4) == 1, "parse fe80::1");
    ASSERT(qzdb_parse_ip("2001:0db8:0000:0000:0000:0000:0000:0001", NULL, v6, &is_v4) == 1, "parse full-zeros v6");

    /* rejected: double :: */
    ASSERT(qzdb_parse_ip("1::2::3", NULL, NULL, NULL) == 0, "reject double ::");
    /* rejected: invalid hex */
    ASSERT(qzdb_parse_ip("gg::1", NULL, NULL, NULL) == 0, "reject bad hex");
    /* rejected: too many groups */
    ASSERT(qzdb_parse_ip("1:2:3:4:5:6:7:8:9", NULL, NULL, NULL) == 0, "reject 9 groups");
    /* rejected: zone id */
    ASSERT(qzdb_parse_ip("fe80::1%eth0", NULL, NULL, NULL) == 0, "reject zone id");
    /* rejected: >4 hex digits per group */
    ASSERT(qzdb_parse_ip("20010::1", NULL, NULL, NULL) == 0, "reject 5-digit group");
    /* rejected: empty group not via :: */
    ASSERT(qzdb_parse_ip("1::2:", NULL, NULL, NULL) == 0, "reject trailing empty group");
}

/* ---- Category 2: IPv4-mapped IPv6 automatic downgrade (parse level, no DB) ---- */
static void test_mapped_downgrade(void) {
    uint32_t v4; uint8_t v6[16]; int is_v4;
    ASSERT(qzdb_parse_ip("::ffff:8.8.8.8", &v4, v6, &is_v4) == 1, "parse ::ffff:8.8.8.8");
    ASSERT(is_v4 == 1, "::ffff:8.8.8.8 downgrades to v4");
    ASSERT(v4 == 0x08080808u, "::ffff:8.8.8.8 -> 8.8.8.8");

    ASSERT(qzdb_parse_ip("::ffff:1.2.3.4", &v4, v6, &is_v4) == 1, "parse ::ffff:1.2.3.4");
    ASSERT(is_v4 == 1 && v4 == 0x01020304u, "::ffff:1.2.3.4 -> 1.2.3.4");

    ASSERT(qzdb_parse_ip("::ffff:0102:0304", &v4, v6, &is_v4) == 1, "parse ::ffff:0102:0304 (hex)");
    ASSERT(is_v4 == 1 && v4 == 0x01020304u, "::ffff:0102:0304 -> 1.2.3.4");
}

/* ---- Category: resource release / double-free safety (no DB) ---- */
static void test_resource_release(void) {
    qzdb_reader_t ctx;
    /* free on an uninitialized (zeroed) context must be safe (idempotent). */
    memset(&ctx, 0, sizeof(ctx));
    qzdb_free(&ctx);
    qzdb_free(&ctx);

    /* parse-only init path: a non-existent file must fail cleanly. */
    int rc = qzdb_init(&ctx, "/nonexistent/path/qzdb_does_not_exist.qzdb");
    ASSERT(rc != QZDB_OK, "init on missing file fails");
    qzdb_free(&ctx); /* must be safe even after a failed init */
}

/* ---- Category 5/6: corrupt file Fail-Closed (no valid DB needed) ---- */
static void test_fail_closed(void) {
    char path[] = "/tmp/qzdb_bad_XXXXXX";
    int fd = mkstemp(path);
    ASSERT(fd >= 0, "create temp bad file");
    if (fd >= 0) {
        /* Bad magic */
        uint8_t buf[200];
        memset(buf, 0, sizeof(buf));
        memcpy(buf, "XXXX", 4);
        write(fd, buf, sizeof(buf));
        close(fd);

        qzdb_reader_t ctx;
        int rc = qzdb_init(&ctx, path);
        ASSERT(rc == QZDB_ERR_BAD_MAGIC, "bad magic -> BAD_MAGIC (Fail-Closed)");
        qzdb_free(&ctx);

        /* Truncated file (QZDB magic + header but < 192 bytes) */
        int fd2 = open(path, O_WRONLY | O_TRUNC);
        uint8_t h[64];
        memset(h, 0, sizeof(h));
        memcpy(h, "QZDB", 4);
        h[4] = 1; /* header version */
        write(fd2, h, sizeof(h));
        close(fd2);
        int rc2 = qzdb_init(&ctx, path);
        ASSERT(rc2 != QZDB_OK, "truncated file -> fail (Fail-Closed)");
        qzdb_free(&ctx);

        unlink(path);
    }
}

/* ---- Category: qzdb_free(NULL) safety (no DB) ---- */
static void test_free_null_safety(void) {
    /* qzdb_free must handle NULL ctx without crashing (bug #3). */
    qzdb_free(NULL);
    ASSERT(1, "qzdb_free(NULL) does not crash");
}

/* ---- Category: CRC caching (OPT #4) ---- */
static void test_crc_caching(void) {
    qzdb_reader_t ctx;
    int rc = qzdb_init_ex(&ctx, "multi-lang/c/qqzeng_ip_std_china.qzdb", 1);
    ASSERT(rc == QZDB_OK, "crc_cache init OK");
    if (rc != QZDB_OK) return;

    /* First call computes CRC */
    char hash1[16];
    rc = qzdb_get_file_hash(&ctx, hash1, sizeof(hash1));
    ASSERT(rc == QZDB_OK, "crc_cache first call OK");
    ASSERT(strlen(hash1) == 8, "crc_cache hash len 8");

    /* Second call returns cached value */
    char hash2[16];
    rc = qzdb_get_file_hash(&ctx, hash2, sizeof(hash2));
    ASSERT(rc == QZDB_OK, "crc_cache second call OK");
    ASSERT(strcmp(hash1, hash2) == 0, "crc_cache consistent");

    /* verify_crc must match */
    ASSERT(qzdb_verify_crc(&ctx) == QZDB_OK, "crc_cache verify matches");

    qzdb_free(&ctx);
}

/* ---- Category: concurrent stress (multi-threaded lookups) ---- */
#define CONCURRENT_THREADS 4
#define CONCURRENT_LOOPS 1000

typedef struct {
    qzdb_reader_t* ctx;
    const char* ip;
    int thread_id;
    int errors;
    int found;
} stress_arg_t;

static void* stress_worker(void* arg) {
    stress_arg_t* a = (stress_arg_t*)arg;
    char out[1024];
    for (int i = 0; i < CONCURRENT_LOOPS; i++) {
        int rc = qzdb_find_str(a->ctx, a->ip, out, sizeof(out));
        if (rc == QZDB_OK && out[0] != '\0') a->found++;
        else if (rc != QZDB_OK) a->errors++;
    }
    return NULL;
}

static void test_concurrent_stress(void) {
    qzdb_reader_t ctx;
    int rc = qzdb_init_ex(&ctx, "multi-lang/c/qqzeng_ip_std_china.qzdb", 1);
    ASSERT(rc == QZDB_OK, "concurrent init OK");
    if (rc != QZDB_OK) return;

    /* Verify single-threaded first */
    char ref[1024];
    rc = qzdb_find_str(&ctx, "119.51.194.142", ref, sizeof(ref));
    ASSERT(rc == QZDB_OK && ref[0] != '\0', "concurrent reference hit");

    /* Launch threads */
    pthread_t threads[CONCURRENT_THREADS];
    stress_arg_t args[CONCURRENT_THREADS];
    int total_errors = 0, total_found = 0;

    for (int t = 0; t < CONCURRENT_THREADS; t++) {
        args[t].ctx = &ctx;
        args[t].ip = "119.51.194.142";
        args[t].thread_id = t;
        args[t].errors = 0;
        args[t].found = 0;
        pthread_create(&threads[t], NULL, stress_worker, &args[t]);
    }
    for (int t = 0; t < CONCURRENT_THREADS; t++) {
        pthread_join(threads[t], NULL);
        total_errors += args[t].errors;
        total_found += args[t].found;
    }

    ASSERT(total_errors == 0, "concurrent no errors");
    ASSERT(total_found == CONCURRENT_THREADS * CONCURRENT_LOOPS, "concurrent all found");

    /* Verify result is still correct after concurrent access */
    char after[1024];
    rc = qzdb_find_str(&ctx, "119.51.194.142", after, sizeof(after));
    ASSERT(rc == QZDB_OK && strcmp(ref, after) == 0, "concurrent result unchanged");

    qzdb_free(&ctx);
}

/* ---- Category 3: find_fields_buf / find_fields_uint_buf parity with find_str ----
 * BUG #1 regression: resolve_row_id_fields used hardcoded safe_read_u24,
 * ignoring ctx->row_geo_width / row_asn_width / row_usage_width from the
 * ROW_SCHEMA. On databases with non-default widths (e.g. std_china has
 * geo_width=2, asn_width=1, stride=3), the buggy code reads wrong bytes
 * for geo_id/asn_id, producing wrong field values in find_fields_buf.
 *
 * This test cross-checks find_fields_buf output against find_str output.
 * If the bug exists, the reconstructed pipe will differ from find_str.
 */
static void test_find_fields_buf_consistency(const char* dbpath) {
    qzdb_reader_t ctx;
    int rc = qzdb_init_ex(&ctx, dbpath, 1);
    ASSERT(rc == QZDB_OK, "fields_buf init OK");
    if (rc != QZDB_OK) return;

    /* Get all field names from the context */
    const char** names = qzdb_get_field_names(&ctx);
    ASSERT(names != NULL, "field names not NULL");
    int fc = qzdb_get_field_count(&ctx);
    ASSERT(fc > 0 && fc < QZDB_MAX_FIELDS, "field count sane");

    /* Known IPs from golden_vectors.json std_china (with non-empty results) */
    const char* test_ips[] = {
        "119.51.194.142",
        NULL
    };

    char pipe_str[4096];
    char buf_pipe[4096];
    char bufs[QZDB_MAX_FIELDS][64];
    char* values[QZDB_MAX_FIELDS];

    for (int ti = 0; test_ips[ti]; ti++) {
        const char* ip = test_ips[ti];

        /* Reference: find_str (uses correct read_ip_row path) */
        int n = qzdb_find_str(&ctx, ip, pipe_str, sizeof(pipe_str));
        ASSERT(n >= 0 && pipe_str[0] != '\0', "find_str hits for test IP");
        if (n < 0 || pipe_str[0] == '\0') continue;

        /* Under test: find_fields_buf (uses resolve_row_id_fields which has the bug) */
        /* Build a NULL-terminated field_names array from ctx field names */
        const char* field_names[QZDB_MAX_FIELDS];
        int ni = 0;
        for (int i = 0; names[i] != NULL && i < QZDB_MAX_FIELDS-1; i++) {
            field_names[ni++] = names[i];
        }
        field_names[ni] = NULL;

        int nf = qzdb_find_fields_buf(&ctx, ip, field_names, values, bufs, 64);
        ASSERT(nf > 0, "find_fields_buf returns positive count");

        /* Reconstruct pipe from values */
        buf_pipe[0] = '\0';
        for (int i = 0; i < ni; i++) {
            if (i > 0) strncat(buf_pipe, "|", sizeof(buf_pipe) - strlen(buf_pipe) - 1);
            const char* v = values[i] ? values[i] : "";
            strncat(buf_pipe, v, sizeof(buf_pipe) - strlen(buf_pipe) - 1);
        }

        /* The reconstructed pipe must match find_str output */
        ASSERT(strcmp(pipe_str, buf_pipe) == 0,
               "find_fields_buf pipe matches find_str for test IP");
    }

    qzdb_free(&ctx);
    qzdb_free(&ctx); /* double-free safety */
}

/* ---- DB-backed tests ---- */
static void test_db_backed(const char* dbpath) {
    qzdb_reader_t ctx;
    int rc = qzdb_init_ex(&ctx, dbpath, 1);
    ASSERT(rc == QZDB_OK, "init valid DB (CRC on)");
    if (rc != QZDB_OK) return;

    /* field normalization (case/underscore/hyphen insensitive) */
    ASSERT(qzdb_get_field_count(&ctx) > 0, "field count > 0");
    ASSERT(qzdb_has_field(&ctx, "country_code") == 1, "has country_code");
    ASSERT(qzdb_has_field(&ctx, "countryCode") == 1, "has countryCode (norm)");
    ASSERT(qzdb_has_field(&ctx, "COUNTRY-CODE") == 1, "has COUNTRY-CODE (norm)");
    ASSERT(qzdb_has_field(&ctx, "nonexistent_field_xyz") == 0, "has nonexistent -> 0");

    /* metadata introspection */
    ASSERT(strlen(qzdb_get_version(&ctx)) > 0, "version non-empty");
    ASSERT(qzdb_get_edition(&ctx) != NULL, "edition not null");
    char hash[16];
    ASSERT(qzdb_get_file_hash(&ctx, hash, sizeof(hash)) == QZDB_OK, "fileHash ok");
    ASSERT(strlen(hash) == 8, "fileHash is 8 hex chars");
    for (int i = 0; i < 8; i++)
        ASSERT((hash[i] >= '0' && hash[i] <= '9') || (hash[i] >= 'a' && hash[i] <= 'f'),
               "fileHash lowercase hex");
    ASSERT(qzdb_verify_crc(&ctx) == QZDB_OK, "verifyCrc true");

    /* group_index setter */
    ASSERT(qzdb_set_group_index(&ctx, 0) == QZDB_OK, "set group 0 ok");
    ASSERT(qzdb_set_group_index(&ctx, 999) != QZDB_OK, "set invalid group -> error");

    /* find + GeoInfo access on a well-known IP (boundary_v4 from golden) */
    char out[1024];
    ASSERT(qzdb_find_str(&ctx, "114.114.114.114", out, sizeof(out)) == QZDB_OK, "find 114.114.114.114");
    ASSERT(out[0] != '\0', "find 114.114.114.114 non-empty");
    qzdb_geo_info_t info;
    ASSERT(qzdb_find(&ctx, "114.114.114.114", &info) == QZDB_OK, "find struct ok");
    const char* cc = qzdb_geo_info_get(&ctx, &info, "country_code");
    ASSERT(cc && strcmp(cc, "CN") == 0, "country_code == CN");
    const char* cc2 = qzdb_geo_info_get(&ctx, &info, "countryCode");
    ASSERT(cc2 && strcmp(cc2, "CN") == 0, "countryCode norm == CN");
    ASSERT(strcmp(qzdb_geo_info_get_cidr(), "") == 0, "getCidr always empty");
    qzdb_free_geo_info(&info);

    /* lookupRowId + lookupIds (dynamic IPRow width) */
    uint32_t rid = qzdb_lookup_row_id(&ctx, "114.114.114.114");
    ASSERT(rid > 0, "lookupRowId > 0");
    qzdb_ids_t ids;
    ASSERT(qzdb_lookup_ids(&ctx, rid, &ids) == QZDB_OK, "lookupIds ok");
    ASSERT(ids.geo_id > 0, "lookupIds geo_id > 0");
    ASSERT(qzdb_lookup_ids(&ctx, 0, &ids) != QZDB_OK, "lookupIds(0) -> error");
    ASSERT(qzdb_lookup_ids(&ctx, 999999999u, &ids) != QZDB_OK, "lookupIds(oob) -> error");

    /* CIDR reverse lookup */
    char cidr[64];
    ASSERT(qzdb_lookup_cidr(&ctx, "114.114.114.114", cidr, sizeof(cidr)) != NULL, "lookupCidr returns");
    ASSERT(strchr(cidr, '/') != NULL, "lookupCidr contains '/'");
    ASSERT(qzdb_lookup_cidr(&ctx, "not_an_ip", cidr, sizeof(cidr)) == NULL, "lookupCidr invalid -> NULL");
    ASSERT(qzdb_lookup_cidr_uint(&ctx, 0x72727272u, cidr, sizeof(cidr)) != NULL, "lookupCidrUint 114.114.114.114");

    /* findFields projection */
    const char* proj[] = { "country_code", "city", NULL };
    qzdb_geo_info_t fi;
    ASSERT(qzdb_find_fields(&ctx, "114.114.114.114", proj, &fi) == QZDB_OK, "findFields ok");
    ASSERT(strcmp(qzdb_geo_info_get(&ctx, &fi, "country_code"), "CN") == 0, "findFields country_code");
    qzdb_free_geo_info(&fi);

    /* findBytes (IPv4-mapped downgrade) */
    uint8_t mapped[16] = {0,0,0,0,0,0,0,0,0,0,0xFF,0xFF,114,114,114,114};
    qzdb_geo_info_t fb;
    ASSERT(qzdb_find_bytes(&ctx, mapped, &fb) == QZDB_OK, "findBytes mapped -> ok");
    qzdb_free_geo_info(&fb);

    /* Lock-free reload atomicity: reload with same file, value unchanged */
    char before[1024], after[1024];
    qzdb_find_str(&ctx, "223.5.5.5", before, sizeof(before));
    ASSERT(qzdb_reload(&ctx, dbpath) == QZDB_OK, "reload ok");
    qzdb_find_str(&ctx, "223.5.5.5", after, sizeof(after));
    ASSERT(strcmp(before, after) == 0, "reload atomic: value unchanged");

    /* After reload the context is still usable */
    ASSERT(qzdb_find_str(&ctx, "114.114.114.114", out, sizeof(out)) == QZDB_OK, "usable after reload");

    qzdb_free(&ctx);
    /* double free safety */
    qzdb_free(&ctx);
}

/* ---- BUG-1 regression: find_fields_buf(NULL) must fill caller buffers ---- */
static void test_find_fields_buf_null(const char* dbpath) {
    qzdb_reader_t ctx;
    int rc = qzdb_init_ex(&ctx, dbpath, 1);
    ASSERT(rc == QZDB_OK, "fields_buf_null init OK");
    if (rc != QZDB_OK) return;

    char bufs[QZDB_MAX_FIELDS][64];
    char* values[QZDB_MAX_FIELDS];

    /* NULL field_names must fill all fields */
    int nf = qzdb_find_fields_buf(&ctx, "114.114.114.114", NULL, values, bufs, 64);
    ASSERT(nf > 0, "fields_buf_null returns positive count");
    ASSERT(values[0] != NULL && values[0][0] != '\0', "fields_buf_null values[0] filled");

    /* Must match find_str output */
    char pipe[1024];
    qzdb_find_str(&ctx, "114.114.114.114", pipe, sizeof(pipe));
    char reconstructed[1024];
    reconstructed[0] = '\0';
    int fc = qzdb_get_field_count(&ctx);
    for (int i = 0; i < fc; i++) {
        if (i > 0) strncat(reconstructed, "|", sizeof(reconstructed) - strlen(reconstructed) - 1);
        const char* v = values[i] ? values[i] : "";
        strncat(reconstructed, v, sizeof(reconstructed) - strlen(reconstructed) - 1);
    }
    ASSERT(strcmp(pipe, reconstructed) == 0, "fields_buf_null matches find_str");

    /* Empty array (first element NULL) must also work */
    const char* empty_fields[] = { NULL };
    int nf2 = qzdb_find_fields_buf(&ctx, "114.114.114.114", empty_fields, values, bufs, 64);
    ASSERT(nf2 > 0, "fields_buf empty array returns positive count");

    qzdb_free(&ctx);
}

/* ---- BUG-2 regression: entry_id==0 returns NOT_FOUND not CORRUPTED ---- */
static void test_entry_id_zero(const char* dbpath) {
    qzdb_reader_t ctx;
    int rc = qzdb_init_ex(&ctx, dbpath, 1);
    ASSERT(rc == QZDB_OK, "entry_id_zero init OK");
    if (rc != QZDB_OK) return;

    /* Use find_fields_buf with a valid IP — should never return CORRUPTED */
    const char* fields[] = { "country", NULL };
    char bufs[QZDB_MAX_FIELDS][64];
    char* values[QZDB_MAX_FIELDS];
    int nf = qzdb_find_fields_buf(&ctx, "114.114.114.114", fields, values, bufs, 64);
    ASSERT(nf > 0, "entry_id_zero find_fields_buf ok");

    /* Directly test resolve path: an IP not in DB should give NOT_FOUND */
    qzdb_geo_info_t info;
    memset(&info, 0, sizeof(info));
    int r = qzdb_find(&ctx, "0.0.0.0", &info);
    ASSERT(r == QZDB_ERR_NOT_FOUND, "entry_id_zero not-found gives NOT_FOUND");

    qzdb_free(&ctx);
}

/* ---- WARN-8 regression: find_str preserves distinct error codes ---- */
static void test_find_str_errors(const char* dbpath) {
    qzdb_reader_t ctx;
    int rc = qzdb_init_ex(&ctx, dbpath, 1);
    ASSERT(rc == QZDB_OK, "find_str_errors init OK");
    if (rc != QZDB_OK) return;

    char buf[256];
    memset(buf, 0, sizeof(buf));
    /* Invalid IP should return INVALID_PARAM, not NOT_FOUND */
    int r1 = qzdb_find_str(&ctx, "not_an_ip", buf, sizeof(buf));
    ASSERT(r1 == QZDB_ERR_INVALID_PARAM, "find_str invalid IP -> INVALID_PARAM");
    ASSERT(buf[0] == '\0', "find_str invalid IP -> empty buf");

    /* Valid IP not in DB should return NOT_FOUND */
    int r2 = qzdb_find_str(&ctx, "0.0.0.0", buf, sizeof(buf));
    ASSERT(r2 == QZDB_ERR_NOT_FOUND, "find_str not-found -> NOT_FOUND");

    /* Valid IP in DB should return OK */
    int r3 = qzdb_find_str(&ctx, "114.114.114.114", buf, sizeof(buf));
    ASSERT(r3 == QZDB_OK, "find_str valid hit -> OK");

    qzdb_free(&ctx);
}

static void test_batch_cb(int idx, const qzdb_batch_result_t* res, void* ud) {
    (void)idx; int* c = (int*)ud; (*c)++;
    qzdb_free_geo_info((qzdb_geo_info_t*)&res->info);
}

/* ---- Batch API (spec §8) ---- */
static void test_batch_api(const char* dbpath) {
    qzdb_reader_t ctx;
    int rc = qzdb_init_ex(&ctx, dbpath, 1);
    ASSERT(rc == QZDB_OK, "batch init OK");
    if (rc != QZDB_OK) return;

    const char* ips[] = { "114.114.114.114", "8.8.8.8", "not_an_ip", "0.0.0.0" };
    int count = 4;
    qzdb_batch_result_t results[4];

    rc = qzdb_find_batch(&ctx, ips, count, results);
    ASSERT(rc == QZDB_OK, "find_batch returns OK");

    /* 114.114.114.114 should be found */
    ASSERT(results[0].error_code == QZDB_OK, "batch[0] found");
    ASSERT(results[0].info.values[0] != NULL, "batch[0] has data");

    /* not_an_ip should be INVALID_PARAM */
    ASSERT(results[2].error_code == QZDB_ERR_INVALID_PARAM, "batch[2] invalid param");

    /* 0.0.0.0 should be NOT_FOUND */
    ASSERT(results[3].error_code == QZDB_ERR_NOT_FOUND, "batch[3] not found");

    /* Free all results */
    for (int i = 0; i < count; i++) qzdb_free_geo_info(&results[i].info);

    /* find_each callback test */
    int cb_count = 0;
    rc = qzdb_find_each(&ctx, ips, count, test_batch_cb, &cb_count);
    ASSERT(rc == QZDB_OK, "find_each returns OK");
    ASSERT(cb_count == count, "find_each called for each IP");

    qzdb_free(&ctx);
}

/* ---- ChainedReader (spec §9) ---- */
static void test_chained_reader(const char* dbpath) {
    qzdb_reader_t ctx;
    int rc = qzdb_init_ex(&ctx, dbpath, 1);
    ASSERT(rc == QZDB_OK, "chain init OK");
    if (rc != QZDB_OK) return;

    /* Fallback mode: single reader */
    qzdb_reader_t* readers[] = { &ctx };
    qzdb_chain_t* chain = qzdb_chain_new(readers, 1, QZDB_CHAIN_FALLBACK);
    ASSERT(chain != NULL, "chain_new ok");

    qzdb_geo_info_t info;
    memset(&info, 0, sizeof(info));
    rc = qzdb_chain_find(chain, "114.114.114.114", &info);
    ASSERT(rc == QZDB_OK, "chain find hit");
    qzdb_free_geo_info(&info);

    /* Not found */
    memset(&info, 0, sizeof(info));
    rc = qzdb_chain_find(chain, "0.0.0.0", &info);
    ASSERT(rc == QZDB_ERR_NOT_FOUND, "chain find not-found");

    /* Format error stops immediately */
    memset(&info, 0, sizeof(info));
    rc = qzdb_chain_find(chain, "not_an_ip", &info);
    ASSERT(rc == QZDB_ERR_INVALID_PARAM, "chain find format error");

    /* Chain metadata */
    int ed_count = 0;
    const char** eds = qzdb_chain_editions(chain, &ed_count);
    ASSERT(ed_count == 1, "chain editions count");
    ASSERT(eds != NULL, "chain editions not null");

    /* Chain find_str */
    char buf[256];
    rc = qzdb_chain_find_str(chain, "114.114.114.114", buf, sizeof(buf));
    ASSERT(rc == QZDB_OK && buf[0] != '\0', "chain find_str ok");

    /* Chain find_uint */
    memset(&info, 0, sizeof(info));
    rc = qzdb_chain_find_uint(chain, 0x72727272u, &info);  /* 114.114.114.114 */
    ASSERT(rc == QZDB_OK, "chain find_uint ok");
    qzdb_free_geo_info(&info);

    /* Chain find_bytes (IPv4-mapped) */
    uint8_t mapped[16] = {0,0,0,0,0,0,0,0,0,0,0xFF,0xFF,114,114,114,114};
    memset(&info, 0, sizeof(info));
    rc = qzdb_chain_find_bytes(chain, mapped, &info);
    ASSERT(rc == QZDB_OK, "chain find_bytes mapped ok");
    qzdb_free_geo_info(&info);

    /* Chain batch */
    const char* ips[] = { "114.114.114.114", "8.8.8.8" };
    qzdb_batch_result_t results[2];
    rc = qzdb_chain_find_batch(chain, ips, 2, results);
    ASSERT(rc == QZDB_OK, "chain find_batch ok");
    for (int i = 0; i < 2; i++) qzdb_free_geo_info(&results[i].info);

    qzdb_chain_free(chain);
    qzdb_free(&ctx);
}

/* ---- Registry (spec §3.2) ---- */
static void test_registry(const char* dbpath) {
    qzdb_registry_t* reg = qzdb_registry_new();
    ASSERT(reg != NULL, "registry_new ok");

    int rc = qzdb_registry_register(reg, "default", dbpath);
    ASSERT(rc == QZDB_OK, "registry register ok");
    ASSERT(qzdb_registry_count(reg) == 1, "registry count == 1");

    qzdb_reader_t* reader = qzdb_registry_get(reg, "default");
    ASSERT(reader != NULL, "registry get ok");

    /* Query through registry */
    qzdb_geo_info_t info;
    memset(&info, 0, sizeof(info));
    rc = qzdb_find(reader, "114.114.114.114", &info);
    ASSERT(rc == QZDB_OK, "registry query ok");
    qzdb_free_geo_info(&info);

    /* Unregister */
    qzdb_registry_unregister(reg, "default");
    ASSERT(qzdb_registry_count(reg) == 0, "registry count after unregister == 0");
    ASSERT(qzdb_registry_get(reg, "default") == NULL, "registry get after unregister == NULL");

    qzdb_registry_free(reg);
}

/* ---- UsageType helpers (spec §6.4) ---- */
static void test_usage_type_helpers(void) {
    ASSERT(qzdb_usage_type_is_known("Broadband") == 1, "usage Broadband known");
    ASSERT(qzdb_usage_type_is_known("VPN") == 1, "usage VPN known");
    ASSERT(qzdb_usage_type_is_known("Unknown") == 1, "usage Unknown known");
    ASSERT(qzdb_usage_type_is_known("FakeType") == 0, "usage FakeType unknown");
    ASSERT(qzdb_usage_type_is_known(NULL) == 0, "usage NULL unknown");

    ASSERT(strcmp(qzdb_usage_type_display_zh("Broadband"), "宽带") == 0, "usage Broadband zh");
    ASSERT(strcmp(qzdb_usage_type_display_en("VPN"), "VPN") == 0, "usage VPN en");
    ASSERT(strcmp(qzdb_usage_type_display_zh("FakeType"), "未知") == 0, "usage unknown zh fallback");
}

/* ---- Buffer loading (spec §4.1) ---- */
static void test_buffer_loading(const char* dbpath) {
    /* Read file into memory */
    FILE* f = fopen(dbpath, "rb");
    ASSERT(f != NULL, "buffer test open file");
    if (!f) return;
    fseek(f, 0, SEEK_END);
    long fsize = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t* buf = malloc((size_t)fsize);
    ASSERT(buf != NULL, "buffer test malloc");
    if (!buf) { fclose(f); return; }
    fread(buf, 1, (size_t)fsize, f);
    fclose(f);

    /* Test init_buffer (copy semantics) */
    qzdb_reader_t ctx;
    int rc = qzdb_init_buffer(&ctx, buf, (size_t)fsize, 1);
    ASSERT(rc == QZDB_OK, "init_buffer ok");

    /* Modify original buffer — should not affect ctx */
    memset(buf, 0, (size_t)fsize);
    qzdb_geo_info_t info;
    memset(&info, 0, sizeof(info));
    rc = qzdb_find(&ctx, "114.114.114.114", &info);
    ASSERT(rc == QZDB_OK, "init_buffer copy semantics preserved");
    qzdb_free_geo_info(&info);
    qzdb_free(&ctx);

    /* Re-read for reload_buffer test */
    f = fopen(dbpath, "rb");
    ASSERT(f != NULL, "buffer test re-open");
    if (!f) { free(buf); return; }
    fread(buf, 1, (size_t)fsize, f);
    fclose(f);

    /* Test reload_buffer */
    qzdb_reader_t ctx2;
    memset(&ctx2, 0, sizeof(ctx2));
    rc = qzdb_init_buffer(&ctx2, buf, (size_t)fsize, 1);
    ASSERT(rc == QZDB_OK, "init_buffer for reload test ok");
    rc = qzdb_reload_buffer(&ctx2, buf, (size_t)fsize);
    ASSERT(rc == QZDB_OK, "reload_buffer ok");
    memset(&info, 0, sizeof(info));
    rc = qzdb_find(&ctx2, "114.114.114.114", &info);
    ASSERT(rc == QZDB_OK, "reload_buffer still works");
    qzdb_free_geo_info(&info);
    qzdb_free(&ctx2);

    free(buf);
}

/* ---- Field normalization O(1) (spec §6.1) ---- */
static void test_field_norm_o1(const char* dbpath) {
    qzdb_reader_t ctx;
    int rc = qzdb_init_ex(&ctx, dbpath, 1);
    ASSERT(rc == QZDB_OK, "norm_o1 init OK");
    if (rc != QZDB_OK) return;

    /* All these should match the same field */
    ASSERT(qzdb_has_field(&ctx, "country_code") == 1, "norm country_code");
    ASSERT(qzdb_has_field(&ctx, "countryCode") == 1, "norm countryCode");
    ASSERT(qzdb_has_field(&ctx, "COUNTRY_CODE") == 1, "norm COUNTRY_CODE");
    ASSERT(qzdb_has_field(&ctx, "Country-Code") == 1, "norm Country-Code");
    ASSERT(qzdb_has_field(&ctx, "countrycode") == 1, "norm countrycode");

    /* get() with different casings returns same value */
    qzdb_geo_info_t info;
    memset(&info, 0, sizeof(info));
    rc = qzdb_find(&ctx, "114.114.114.114", &info);
    ASSERT(rc == QZDB_OK, "norm_o1 find ok");
    const char* v1 = qzdb_geo_info_get(&ctx, &info, "country_code");
    const char* v2 = qzdb_geo_info_get(&ctx, &info, "countryCode");
    const char* v3 = qzdb_geo_info_get(&ctx, &info, "COUNTRY-CODE");
    ASSERT(strcmp(v1, v2) == 0, "norm case-insensitive match");
    ASSERT(strcmp(v1, v3) == 0, "norm hyphen-insensitive match");
    qzdb_free_geo_info(&info);

    qzdb_free(&ctx);
}

int main(int argc, char** argv) {
    test_ipv4_parse();
    test_ipv6_parse();
    test_mapped_downgrade();
    test_resource_release();
    test_fail_closed();
    test_free_null_safety();
    test_crc_caching();
    test_usage_type_helpers();

    const char* db = locate_db(argc, argv);
    if (db) {
        fprintf(stderr, "[Tier1] using DB: %s\n", db);
        test_db_backed(db);
        test_find_fields_buf_null(db);
        test_entry_id_zero(db);
        test_find_str_errors(db);
        test_batch_api(db);
        test_chained_reader(db);
        test_registry(db);
        test_buffer_loading(db);
        test_field_norm_o1(db);
    } else {
        fprintf(stderr, "[Tier1] no DB found; skipping DB-backed assertions\n");
    }

    printf("Tier1 assertions: total=%d pass=%d fail=%d\n", g_total, g_pass, g_fail);
    if (g_fail == 0) printf("TIER1_PASS\n");
    else printf("TIER1_FAIL\n");
    return g_fail == 0 ? 0 : 1;
}
