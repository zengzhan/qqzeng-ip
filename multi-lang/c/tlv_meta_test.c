/**
 * QZDB C SDK — Metadata TLV type=5/6（data_month/scope）权威语义回归
 * （FORMAT §8.2 / ROADMAP T7）。
 *
 * 背景：审查曾发现 scope 所有权转移路径存在 use-after-free / double-free
 * （free(meta_scope) 后仍把悬垂指针存入 ctx->scope）。本测试以真实库文件为
 * 底座注入 TLV，走完整 init(强制 CRC)→getter→close 生命周期；在 ASan 下运行
 * 可捕获任何 UAF/double-free——作为该 blocker 的永久回归守卫。
 *
 * 编译（独立编译，勿与 main.c 同链接）：
 *   gcc -O1 -g -fsanitize=address -o tlv_meta_test tlv_meta_test.c qzdb_reader.c -lm
 *   ./tlv_meta_test && echo TLV_META_C_OK
 *
 * 用例语义与 python/test_tlv_meta.py、go/qzdb/tlv_meta_test.go 同源。
 */
#include "qzdb_reader.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

static int g_fail = 0;
static void check_str(const char *got, const char *want, const char *label) {
    if (strcmp(got ? got : "(null)", want) == 0) return;
    g_fail++;
    printf("  FAIL: %s: got \"%s\" want \"%s\"\n", label, got ? got : "(null)", want);
}

static void put_u16(uint8_t *p, uint16_t v) { p[0] = v & 0xFF; p[1] = v >> 8; }
static void put_u32(uint8_t *p, uint32_t v) {
    p[0] = v & 0xFF; p[1] = (v >> 8) & 0xFF; p[2] = (v >> 16) & 0xFF; p[3] = (v >> 24) & 0xFF;
}
static void put_u64(uint8_t *p, uint64_t v) {
    for (int i = 0; i < 8; i++) p[i] = (uint8_t)(v >> (8 * i));
}
static void append_tlv(uint8_t **buf, size_t *len, uint8_t type, const char *val) {
    size_t vl = strlen(val);
    uint8_t *nb = realloc(*buf, *len + 4 + vl);
    if (!nb) { perror("realloc"); exit(2); }
    *buf = nb;
    (*buf)[*len] = type; (*buf)[*len + 1] = 0;
    put_u16(*buf + *len + 2, (uint16_t)vl);
    memcpy(*buf + *len + 4, val, vl);
    *len += 4 + vl;
}

/* CRC-32/IEEE（与 SDK canonical 一致：@16~19 清零后对全文件计算） */
static uint32_t crc32_ieee(const uint8_t *d, size_t n) {
    static uint32_t table[256]; static int init = 0;
    if (!init) {
        for (uint32_t i = 0; i < 256; i++) {
            uint32_t c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        init = 1;
    }
    uint32_t crc = 0xFFFFFFFFu;
    for (size_t i = 0; i < n; i++) crc = table[(crc ^ d[i]) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
}

/* 读入真实文件并把 offMeta 指向「原 Metadata 原样 + 追加 extra TLV」的尾部副本，
 * 重算 CRC 后返回完整缓冲。返回 NULL 表示读文件失败。 */
static uint8_t *load_injected(const char *path, const uint8_t *extra, size_t extra_len,
                              size_t *out_len) {
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END); long sz = ftell(f); fseek(f, 0, SEEK_SET);
    if (sz < 192) { fclose(f); return NULL; }
    uint8_t *b = malloc((size_t)sz + extra_len);
    if (!b || fread(b, 1, (size_t)sz, f) != (size_t)sz) { fclose(f); free(b); return NULL; }
    fclose(f);

    uint64_t off_meta = 0;
    for (int i = 0; i < 8; i++) off_meta |= (uint64_t)b[144 + i] << (8 * i);
    if (off_meta == 0 || off_meta >= (uint64_t)sz) { free(b); return NULL; }

    /* 原 metadata 原样保留 + 追加 TLV → 写到文件尾。
     * 总长 = 原文件 + 原 metadata 段副本 + 追加 TLV */
    size_t old_meta_len = (size_t)sz - (size_t)off_meta;
    uint8_t *nb = realloc(b, (size_t)sz + old_meta_len + extra_len);
    if (!nb) { free(b); return NULL; }
    b = nb;
    memcpy(b + sz, b + off_meta, old_meta_len);
    memcpy(b + sz + old_meta_len, extra, extra_len);

    b[8] |= 0x04;                                  /* flags bit2 = hasMeta（原文件已置位则无害） */
    put_u64(b + 144, (uint64_t)sz);                /* offMeta → 尾部新段 */
    put_u32(b + 16, 0);                            /* CRC 字段清零 */
    put_u32(b + 16, crc32_ieee(b, (size_t)sz + old_meta_len + extra_len));
    if (out_len) *out_len = (size_t)sz + old_meta_len + extra_len;
    return b;
}

int main(int argc, char **argv) {
    const char *candidates[] = {
        argc > 1 ? argv[1] : NULL,
        "../data/qqzeng_ip_std_china.qzdb",
        "data/qqzeng_ip_std_china.qzdb",
        "../../data/qqzeng_ip_std_china.qzdb",
    };
    const char *path = NULL;
    for (int i = 0; i < 4; i++) {
        if (!candidates[i]) continue;
        FILE *f = fopen(candidates[i], "rb");
        if (f) { fclose(f); path = candidates[i]; break; }
    }
    if (!path) { printf("SKIP tlv_meta_test: no std_china DB found\n"); return 0; }

    /* --- 1) 带 type=5/6：TLV 权威（BuildDate 推算本应为 2026-08） -------- */
    {
        uint8_t *extra = NULL; size_t extra_len = 0;
        append_tlv(&extra, &extra_len, 5, "2026-07");
        append_tlv(&extra, &extra_len, 6, "global");
        size_t db_len;
        uint8_t *db = load_injected(path, extra, extra_len, &db_len);
        if (!db) { printf("  FAIL: load_injected case1\n"); return 1; }

        qzdb_reader_t ctx; memset(&ctx, 0, sizeof(ctx));
        int rc = qzdb_init_buffer(&ctx, db, db_len, 1);
        if (rc != QZDB_OK) { printf("  FAIL: init(tlv)=%d\n", rc); return 1; }
        check_str(qzdb_get_data_month(&ctx), "2026-07", "TLV type=5 权威 dataMonth");
        check_str(qzdb_get_scope(&ctx), "global", "TLV type=6 权威 scope");
        check_str(qzdb_get_build_time(&ctx), "2026-08-09", "buildTime 始终取自 BuildDate");
        /* 反复读取确认无 UAF 读 */
        for (int i = 0; i < 100; i++) {
            if (strcmp(qzdb_get_scope(&ctx), "global") != 0) { g_fail++; break; }
        }
        qzdb_close(&ctx);   /* close 会 free(ctx->scope)：所有权转移若有误，ASan 报 double-free */
        free(db); free(extra);
    }

    /* --- 2) 无 type=5/6：回落路径（真实旧文件，行为零变化） -------------- */
    {
        qzdb_reader_t ctx; memset(&ctx, 0, sizeof(ctx));
        int rc = qzdb_init_ex(&ctx, path, 1);
        if (rc != QZDB_OK) { printf("  FAIL: init(fallback)=%d\n", rc); return 1; }
        check_str(qzdb_get_data_month(&ctx), "2026-08", "dataMonth 回落 BuildDate");
        check_str(qzdb_get_scope(&ctx), "", "旧文件 scope 为 \"\"");
        check_str(qzdb_get_build_time(&ctx), "2026-08-09", "buildTime 取自 BuildDate");
        qzdb_close(&ctx);
    }

    /* --- 3) 重复 type=5/6 条目：后者覆盖前者，无泄漏/二次释放 ------------- */
    {
        uint8_t *extra = NULL; size_t extra_len = 0;
        append_tlv(&extra, &extra_len, 5, "2020-01");
        append_tlv(&extra, &extra_len, 5, "2026-07");   /* 覆盖 */
        append_tlv(&extra, &extra_len, 6, "cn");
        append_tlv(&extra, &extra_len, 6, "global");    /* 覆盖 */
        size_t db_len;
        uint8_t *db = load_injected(path, extra, extra_len, &db_len);
        if (!db) { printf("  FAIL: load_injected case3\n"); return 1; }
        qzdb_reader_t ctx; memset(&ctx, 0, sizeof(ctx));
        if (qzdb_init_buffer(&ctx, db, db_len, 1) != QZDB_OK) { printf("  FAIL: init(dup)\n"); return 1; }
        check_str(qzdb_get_data_month(&ctx), "2026-07", "重复 type=5 取最后值");
        check_str(qzdb_get_scope(&ctx), "global", "重复 type=6 取最后值");
        qzdb_close(&ctx);
        free(db); free(extra);
    }

    printf("TLV meta assertions: fail=%d\n", g_fail);
    if (g_fail == 0) { printf("TLV_META_C_OK\n"); return 0; }
    printf("TLV_META_C_FAIL\n");
    return 1;
}
