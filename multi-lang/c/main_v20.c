#include "qzdb_searcher_v20.h"
#include <stdio.h>

int main(int argc, char** argv) {
    if (argc < 2) {
        fprintf(stderr, "Usage: %s <qzdb_v20_file>\n", argv[0]);
        return 1;
    }
    qzdb_searcher_v20_t ctx;
    if (qzdb_v20_init(&ctx, argv[1]) != 0) {
        fprintf(stderr, "Failed to load V20 DB: %s\n", argv[1]);
        return 1;
    }

    char buf[512];
    const char* tests[] = {"114.114.114.114", "223.5.5.5", "8.8.8.8", "2606:4700:4700::1111", NULL};
    for (int i = 0; tests[i]; i++) {
        if (qzdb_v20_find_str(&ctx, tests[i], buf, sizeof(buf)) == 0)
            printf("%s -> %s\n", tests[i], buf);
        else
            printf("%s -> NOT FOUND\n", tests[i]);
    }

    printf("CRC verify: %s\n", qzdb_v20_verify_crc(&ctx) ? "PASS" : "FAIL");
    qzdb_v20_free(&ctx);
    return 0;
}
