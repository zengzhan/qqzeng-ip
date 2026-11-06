#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../../ip-qzdb-sdk/c/qzdb_reader.h"

int main(int argc, char** argv) {
    const char* db_path = (argc > 1) ? argv[1] : "demo/qqzeng-ip-ult.qzdb";
    const char* ip_file = (argc > 2) ? argv[2] : NULL;

    qzdb_reader_t reader;
    int rc = qzdb_init(&reader, db_path);
    if (rc != QZDB_OK) {
        fprintf(stderr, "qzdb_init failed: %d\n", rc);
        return 1;
    }

    /* Smoke test on known demo IP */
    char buf[4096];
    rc = qzdb_find_str(&reader, "118.115.99.0", buf, sizeof(buf));
    if (rc != QZDB_OK || strlen(buf) == 0) {
        fprintf(stderr, "qzdb_find_str smoke check failed: rc=%d\n", rc);
        qzdb_free(&reader);
        return 1;
    }
    printf("C smoke OK: 118.115.99.0 -> %s\n", buf);

    /* If IP list file provided, run through all lines */
    if (ip_file != NULL) {
        FILE* fp = fopen(ip_file, "r");
        if (!fp) {
            fprintf(stderr, "Cannot open IP list file: %s\n", ip_file);
            qzdb_free(&reader);
            return 1;
        }
        char line[256];
        int count = 0;
        while (fgets(line, sizeof(line), fp)) {
            char* p = strchr(line, '\r');
            if (p) *p = '\0';
            p = strchr(line, '\n');
            if (p) *p = '\0';
            if (line[0] == '\0' || line[0] == '#') continue;

            buf[0] = '\0';
            qzdb_find_str(&reader, line, buf, sizeof(buf));
            count++;
        }
        fclose(fp);
        printf("C SDK queried %d IPs without sanitizer alerts.\n", count);
    }

    qzdb_free(&reader);
    return 0;
}
