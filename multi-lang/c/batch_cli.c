#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include "qzdb_searcher.h"

int main(int argc, char** argv) {
    if (argc < 2) return 1;
    qzdb_searcher_t ctx;
    if (qzdb_init(&ctx, argv[1]) != QZDB_OK) return 1;

    char line[256];
    char out_buf[1024];

    while (fgets(line, sizeof(line), stdin)) {
        // Strip trailing newline
        size_t len = strlen(line);
        while (len > 0 && (line[len - 1] == '\r' || line[len - 1] == '\n' || line[len - 1] == ' ')) {
            line[--len] = '\0';
        }
        if (len == 0) continue;

        if (qzdb_find_str(&ctx, line, out_buf, sizeof(out_buf)) == QZDB_OK) {
            printf("%s\n", out_buf);
        } else {
            printf("\n");
        }
        fflush(stdout);
    }

    qzdb_free(&ctx);
    return 0;
}
