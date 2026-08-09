/**
 * QzdbReader - C SDK calling example
 *
 * Usage: gcc main.c qzdb_reader.c -o demo && ./demo
 * Place qqzeng_ip_std_china.qzdb in the same directory or specify the path.
 */

#include <stdio.h>
#include "qzdb_reader.h"

int main() {
    /* --- Try to locate database file --- */
    const char *paths[] = {
        "qqzeng_ip_std_china.qzdb",
        "../data/qqzeng_ip_std_china.qzdb",
        "data/qqzeng_ip_std_china.qzdb",
    };
    const char *db_path = NULL;
    for (int i = 0; i < 3; i++) {
        FILE *f = fopen(paths[i], "rb");
        if (f) { fclose(f); db_path = paths[i]; break; }
    }
    if (!db_path) {
        fprintf(stderr, "Database file not found\n");
        return 1;
    }

    /* --- Load database --- */
    qzdb_reader_t reader;
    qzdb_reader_t *searcher = &reader;
    if (qzdb_init(searcher, db_path) != QZDB_OK) {
        fprintf(stderr, "Failed to load database\n");
        return 1;
    }

    printf("Edition: %s (source=%s, version_mask=%u), pools: %d\n",
           qzdb_get_edition(searcher), qzdb_get_edition_source(searcher),
           (unsigned)qzdb_get_version_mask(searcher), searcher->pool_count);
    printf("Fields (%d, source=%s):", searcher->field_count,
           qzdb_get_field_names_source(searcher));
    for (int i = 0; i < searcher->field_count && searcher->field_names[i]; i++)
        printf(" %s", searcher->field_names[i]);
    printf("\n\n");

    /* --- Query sample IPs --- */
    const char *v4_ips[] = {"114.114.114.114", "223.5.5.5", "8.8.8.8"};
    char buf[512];
    for (int i = 0; i < 3; i++) {
        qzdb_find_str(searcher, v4_ips[i], buf, sizeof(buf));
        printf("find(\"%-16s\") => %s\n", v4_ips[i], buf);
    }

    /* --- Query a V6 IP --- */
    qzdb_find_str(searcher, "2408:8000:9000::1", buf, sizeof(buf));
    printf("find(\"2408:8000:9000::1\") => %s\n", buf);

    /* --- Get structured fields --- */
    printf("\n--- Structured fields for 114.114.114.114 ---\n");
    qzdb_geo_info_t loc;
    if (qzdb_find(searcher, "114.114.114.114", &loc) == 0) {
        for (int i = 0; i < searcher->field_count && searcher->field_names[i]; i++)
            printf("  %s: %s\n", searcher->field_names[i],
                   loc.values[i] ? loc.values[i] : "");
        qzdb_free_geo_info(&loc);
    }

    qzdb_free(searcher);
    printf("TEST_PASS\n");
    return 0;
}
