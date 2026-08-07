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

int main(int argc, char** argv) {
    test_ipv4_parse();
    test_ipv6_parse();
    test_mapped_downgrade();
    test_resource_release();
    test_fail_closed();

    const char* db = locate_db(argc, argv);
    if (db) {
        fprintf(stderr, "[Tier1] using DB: %s\n", db);
        test_db_backed(db);
    } else {
        fprintf(stderr, "[Tier1] no DB found; skipping DB-backed assertions\n");
    }

    printf("Tier1 assertions: total=%d pass=%d fail=%d\n", g_total, g_pass, g_fail);
    if (g_fail == 0) printf("TIER1_PASS\n");
    else printf("TIER1_FAIL\n");
    return g_fail == 0 ? 0 : 1;
}
