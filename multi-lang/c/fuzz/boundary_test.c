/*
 * boundary_test.c — minimal boundary/fuzz harness for the C QZDB reader.
 *
 * This does NOT require a real .qzdb database file. It feeds
 * qzdb_init_buffer() a battery of malformed / adversarial byte buffers and
 * asserts two things for every one of them:
 *
 *   1. The call never crashes (no segfault, no ASan violation).
 *   2. It returns a proper qzdb_error_t instead of silently "succeeding"
 *      on garbage input.
 *
 * Build & run (catches heap/stack corruption effectively):
 *   cc -std=c11 -g -O1 -fsanitize=address,undefined \
 *      ../qzdb_reader.c boundary_test.c -o boundary_test -pthread -lm
 *   ./boundary_test
 *
 * Optional: build as a libFuzzer target for continuous fuzzing —
 *   clang -std=c11 -g -fsanitize=address,undefined,fuzzer \
 *      ../qzdb_reader.c boundary_test.c -o boundary_fuzzer -pthread -lm \
 *      -DQZDB_LIBFUZZER
 *   ./boundary_fuzzer -max_len=4096
 *
 * Recommended CI addition: run the ASan/UBSan build on every PR that touches
 * c/qzdb_reader.c, and run the libFuzzer build on a schedule (nightly) with a
 * corpus seeded from real .qzdb file headers.
 */
#include "../qzdb_reader.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

/* Everything down to the #endif is the standalone (non-libFuzzer) harness.
 * Keeping check_rejects() inside the guard as well avoids an -Wunused-function
 * warning in the libFuzzer build — this project treats warnings in test code
 * as signal, not noise (that is how two dormant C tests were found before). */
#ifndef QZDB_LIBFUZZER
static int g_failures = 0;

/* Runs qzdb_init_buffer on `buf` and checks it doesn't crash and returns an
 * error code (never QZDB_OK, since none of these buffers are valid). */
static void check_rejects(const char* name, const uint8_t* buf, size_t len) {
    qzdb_reader_t ctx;
    memset(&ctx, 0, sizeof(ctx));
    int rc = qzdb_init_buffer(&ctx, buf, len, /*verify_crc=*/1);
    if (rc == QZDB_OK) {
        fprintf(stderr, "[FAIL] %-40s expected an error, got QZDB_OK (accepted garbage input)\n", name);
        g_failures++;
        /* Exercise a query against the "successfully" loaded garbage state —
         * this is exactly the kind of input where a real parser bug would
         * turn into an out-of-bounds read during qzdb_find(). */
        qzdb_geo_info_t info;
        memset(&info, 0, sizeof(info));
        qzdb_find(&ctx, "8.8.8.8", &info);
        qzdb_free_geo_info(&info);
        qzdb_free(&ctx);
    } else {
        printf("[ OK ] %-40s -> %s\n", name, qzdb_strerror(rc));
        /* Even on the error path, ctx must be left in a state that is safe
         * to pass to qzdb_free() (no double-free, no use of uninitialized
         * pointers inside it). */
        qzdb_free(&ctx);
    }
}

int main(void) {
    /* 1. NULL / zero-length */
    check_rejects("NULL buffer", NULL, 0);
    {
        uint8_t empty[1] = {0};
        check_rejects("zero-length buffer", empty, 0);
    }

    /* 2. Too short to even contain a header (< 192 bytes) */
    for (size_t len = 0; len < 192; len += 31) {
        uint8_t buf[192];
        memset(buf, 0xAA, sizeof(buf));
        memcpy(buf, "QZDB", 4);
        char name[64];
        snprintf(name, sizeof(name), "truncated header (%zu bytes)", len);
        check_rejects(name, buf, len);
    }

    /* 3. Wrong magic */
    {
        uint8_t buf[192];
        memset(buf, 0, sizeof(buf));
        memcpy(buf, "XXXX", 4);
        check_rejects("wrong magic", buf, sizeof(buf));
    }

    /* 4. Correct magic, unsupported format version */
    {
        uint8_t buf[192];
        memset(buf, 0, sizeof(buf));
        memcpy(buf, "QZDB", 4);
        buf[4] = 0xFF; /* bogus format version */
        check_rejects("unsupported format version", buf, sizeof(buf));
    }

    /* 5. Correct magic + version, v6_jump_bits out of the valid [8,20] range */
    {
        uint8_t buf[192];
        memset(buf, 0, sizeof(buf));
        memcpy(buf, "QZDB", 4);
        buf[4] = 1;
        buf[11] = 200; /* invalid v6_jump_bits */
        check_rejects("v6_jump_bits out of range", buf, sizeof(buf));
    }

    /* 6. Correct magic + version, invalid pool_idx_size (must be 2 or 3) */
    {
        uint8_t buf[192];
        memset(buf, 0, sizeof(buf));
        memcpy(buf, "QZDB", 4);
        buf[4] = 1;
        buf[11] = 16;
        buf[13] = 99; /* invalid pool_idx_size */
        check_rejects("invalid pool_idx_size", buf, sizeof(buf));
    }

    /* 7. Header claims huge offsets/counts that overflow / exceed buffer —
     *    classic malformed-length-field attack surface for a binary parser. */
    {
        uint8_t buf[192];
        memset(buf, 0, sizeof(buf));
        memcpy(buf, "QZDB", 4);
        buf[4] = 1;
        buf[11] = 16;
        buf[13] = 2;
        /* row_count at offset 20 (LE32): set to UINT32_MAX */
        buf[20] = 0xFF; buf[21] = 0xFF; buf[22] = 0xFF; buf[23] = 0xFF;
        check_rejects("row_count = UINT32_MAX (overflow bait)", buf, sizeof(buf));
    }

    /* 8. Exactly-192-byte all-zero buffer with correct magic only */
    {
        uint8_t buf[192];
        memset(buf, 0, sizeof(buf));
        memcpy(buf, "QZDB", 4);
        buf[4] = 1;
        check_rejects("all-zero body, minimal valid magic+version", buf, sizeof(buf));
    }

    /* 9. Randomized garbage, several seeds — cheap smoke fuzz without libFuzzer */
    {
        uint8_t buf[512];
        for (int seed = 0; seed < 200; seed++) {
            unsigned s = (unsigned)seed * 2654435761u + 1u;
            for (size_t i = 0; i < sizeof(buf); i++) {
                s = s * 1103515245u + 12345u;
                buf[i] = (uint8_t)(s >> 16);
            }
            /* Half the seeds keep the magic so parsing gets further before
             * (correctly) rejecting on some later field. */
            if (seed % 2 == 0) memcpy(buf, "QZDB", 4);
            char name[32];
            snprintf(name, sizeof(name), "random seed %d", seed);
            check_rejects(name, buf, sizeof(buf));
        }
    }

    printf("\n%d failure(s)\n", g_failures);
    return g_failures == 0 ? 0 : 1;
}
#endif /* !QZDB_LIBFUZZER */

#ifdef QZDB_LIBFUZZER
int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    qzdb_reader_t ctx;
    memset(&ctx, 0, sizeof(ctx));
    int rc = qzdb_init_buffer(&ctx, data, size, 1);
    if (rc == QZDB_OK) {
        qzdb_geo_info_t info;
        memset(&info, 0, sizeof(info));
        qzdb_find(&ctx, "8.8.8.8", &info);
        qzdb_find(&ctx, "2001:4860:4860::8888", &info);
        qzdb_free_geo_info(&info);
    }
    qzdb_free(&ctx);
    return 0;
}
#endif
