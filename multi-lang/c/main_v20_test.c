/**
 * C V20 SDK test helper - accepts DB path and IPs as arguments.
 * Usage: main_v20_test <qzdb_v20_file> <ip1> [ip2 ...]
 * Outputs one result per line, empty line for NOT FOUND.
 */
#include "qzdb_searcher_v20.h"
#include <stdio.h>

int main(int argc, char** argv) {
    if (argc < 3) {
        fprintf(stderr, "Usage: %s <qzdb_v20_file> <ip1> [ip2 ...]\n", argv[0]);
        return 1;
    }
    qzdb_searcher_v20_t ctx;
    if (qzdb_v20_init(&ctx, argv[1]) != 0) {
        fprintf(stderr, "Failed to load V20 DB: %s\n", argv[1]);
        return 1;
    }
    char buf[512];
    for (int i = 2; i < argc; i++) {
        if (qzdb_v20_find_str(&ctx, argv[i], buf, sizeof(buf)) == 0)
            printf("%s\n", buf);
        else
            printf("\n");
    }
    qzdb_v20_free(&ctx);
    return 0;
}
