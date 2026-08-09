/* 元信息探针（C）：输出与 tools/meta_probe_python.py 完全同构的 JSON。
 *
 * 编译:
 *   gcc -std=c11 -O2 -I../multi-lang/c tools/meta_probe_c.c \
 *       multi-lang/c/qzdb_reader.c -o /tmp/meta_probe_c -lm -lpthread
 * 用法:
 *   /tmp/meta_probe_c a.qzdb b.qzdb ... > /tmp/meta_c.json
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "qzdb_reader.h"

static const char* basename_of(const char* p) {
    const char* s = strrchr(p, '/');
    return s ? s + 1 : p;
}

/* 最小 JSON 字符串转义，与其它语言 json 序列化的可比子集保持一致 */
static void put_json_string(const char* s) {
    putchar('"');
    if (s) {
        for (const unsigned char* p = (const unsigned char*)s; *p; ++p) {
            switch (*p) {
                case '"':  fputs("\\\"", stdout); break;
                case '\\': fputs("\\\\", stdout); break;
                case '\n': fputs("\\n", stdout);  break;
                case '\r': fputs("\\r", stdout);  break;
                case '\t': fputs("\\t", stdout);  break;
                default:
                    if (*p < 0x20) printf("\\u%04x", *p);
                    else putchar(*p);
            }
        }
    }
    putchar('"');
}

static void put_kv_str(const char* k, const char* v) {
    put_json_string(k);
    putchar(':');
    put_json_string(v ? v : "");
}

static void put_kv_int(const char* k, long v) {
    put_json_string(k);
    printf(":%ld", v);
}

int main(int argc, char** argv) {
    putchar('[');
    for (int i = 1; i < argc; ++i) {
        qzdb_reader_t reader;
        memset(&reader, 0, sizeof(reader));
        if (qzdb_init(&reader, argv[i]) != QZDB_OK) {
            fprintf(stderr, "open failed: %s\n", argv[i]);
            return 1;
        }
        qzdb_reader_t* r = &reader;

        if (i > 1) putchar(',');
        putchar('{');
        put_kv_str("file", basename_of(argv[i]));                 putchar(',');
        put_kv_str("lang", "c");                                  putchar(',');
        put_kv_str("edition", qzdb_get_edition(r));               putchar(',');
        put_kv_str("edition_source", qzdb_get_edition_source(r)); putchar(',');
        put_kv_int("version_mask", (long)qzdb_get_version_mask(r)); putchar(',');
        put_kv_str("field_names_source", qzdb_get_field_names_source(r)); putchar(',');

        put_json_string("field_names");
        putchar(':');
        putchar('[');
        {
            const char** names = qzdb_get_field_names(r);
            int n = qzdb_get_field_count(r);
            for (int j = 0; j < n; ++j) {
                if (j) putchar(',');
                put_json_string(names ? names[j] : "");
            }
        }
        putchar(']');
        putchar(',');

        put_kv_int("group_count", (long)qzdb_get_group_count(r)); putchar(',');
        put_kv_int("pool_count", (long)qzdb_get_pool_count(r));   putchar(',');
        put_kv_str("data_month", qzdb_get_data_month(r));
        putchar('}');

        qzdb_free(r);
    }
    putchar(']');
    return 0;
}
