# QZDB C SDK（qqzeng-ip）

> 纯离线、零第三方依赖的 **QZDB IP 地理定位数据库**官方 C 语言 SDK（支持 IPv4 / IPv6 双栈）。

- **定位**：离线解析 `.qzdb` 二进制数据库文件，不依赖任何外部网络请求
- **特性**：无锁快照（lock-free snapshot）+ per-snapshot 有界 `GeoInfo` 解码缓存，并发查询互不阻塞
- **依赖**：仅依赖系统 `libc` / `libpthread`（POSIX 线程用于缓存写入互斥）
- **许可**：MIT
- **跨语言规范**：以仓库根 [`API_CONTRACT.md`](../../API_CONTRACT.md) 为唯一事实来源（SSOT）

---

## 目录

1. [环境要求](#1-环境要求)
2. [编译与集成](#2-编译与集成)
3. [快速开始](#3-快速开始)
4. [加载数据库](#4-加载数据库)
5. [查询 API](#5-查询-api)
6. [结果对象 `qzdb_geo_info_t`](#6-结果对象-qzdb_geo_info_t)
7. [字段投影 `find_fields`](#7-字段投影-find_fields)
8. [行号 / ID 反查与 CIDR](#8-行号--id-反查与-cidr)
9. [批量与流式查询](#9-批量与流式查询)
10. [链式多库查询 `ChainedReader`](#10-链式多库查询-chainedreader)
11. [命名注册表 `QzdbRegistry`](#11-命名注册表-qzdbregistry)
12. [元数据访问器](#12-元数据访问器)
13. [错误处理](#13-错误处理)
14. [并发与性能](#14-并发与性能)
15. [完整 API 参考](#15-完整-api-参考)
16. [项目结构](#16-项目结构)

---

## 1. 环境要求

| 项 | 要求 |
|----|------|
| 编译器 | GCC ≥ 4.8 或 Clang ≥ 3.5（`-std=c99` 及以上均可） |
| 系统 | Linux / macOS / *BSD / Windows(MSYS2 MinGW) 均可 |
| 依赖 | `libc` + `libpthread`（POSIX 线程） |
| 数据库文件 | `.qzdb` 格式（由官方数据构建工具生成，含所需分组的二进制数据） |

---

## 2. 编译与集成

SDK 为**单头 + 单源**结构（`qzdb_reader.h` + `qzdb_reader.c`），直接加入工程即可，无构建系统耦合。

```bash
# 作为库编译（推荐，生成静态库或目标文件）
gcc -O2 -c qzdb_reader.c -o qzdb_reader.o
ar rcs libqzdb.a qzdb_reader.o

# 或直接与你的源文件一起编译
gcc -O2 -o myapp myapp.c qzdb_reader.c -lpthread
```

> **提示**：本仓库在沙箱/受限环境下编译时若遇到内存压力，可临时使用 `-O0 -g`（仅影响编译期内存占用，不影响运行时正确性）。

---

## 3. 快速开始

```c
#include "qzdb_reader.h"
#include <stdio.h>

int main(void) {
    qzdb_reader_t ctx;
    if (qzdb_init(&ctx, "qqzeng_ip_std_china.qzdb") != QZDB_OK) {
        fprintf(stderr, "加载失败\n");
        return 1;
    }

    /* 单次查询：未命中或 IP 非法时返回 QZDB_ERR_NOT_FOUND，不崩溃 */
    qzdb_geo_info_t info;
    if (qzdb_find(&ctx, "114.114.114.114", &info) == QZDB_OK) {
        char pipe[1024];
        qzdb_geo_info_to_pipe(&ctx, &info, pipe, sizeof(pipe));
        printf("pipe = %s\n", pipe);                 /* 国家|省份|城市|ISP|... */
        printf("country = %s\n",
               qzdb_geo_info_get(&ctx, &info, "country"));
        qzdb_free_geo_info(&info);                   /* 释放堆内字符串 */
    }

    /* 直接拿到管道字符串；未命中返回 ""（适合落库/日志） */
    char out[1024];
    qzdb_find_str(&ctx, "240e:390:1:1::1", out, sizeof(out));
    printf("str  = %s\n", out);

    /* 仅取内部行号（最轻量，不涉及字段解析） */
    uint32_t row_id = qzdb_lookup_row_id(&ctx, "8.8.8.8");
    printf("row_id = %u\n", row_id);

    qzdb_free(&ctx);
    return 0;
}
```

> **查询语义约定**：`qzdb_find` / `qzdb_find_uint` / `qzdb_find_bytes` 等**在 IP 未命中或格式非法时返回 `QZDB_ERR_NOT_FOUND`，不抛异常、不崩溃**。只有数据库文件损坏、格式不支持、CRC 校验失败等**加载期错误**才会在 `qzdb_init*` 阶段返回负错误码（见[第 13 节](#13-错误处理)）。

---

## 4. 加载数据库

所有加载入口都返回 `int` 错误码：`QZDB_OK`（0）成功，负值表示失败（**Fail-Closed**：魔数/头部/CRC/截断异常一律拒绝初始化，不会加载半个损坏文件）。

### 4.1 从文件路径加载

```c
qzdb_reader_t ctx;

/* 最简：默认校验 CRC、加载第 0 分组 */
if (qzdb_init(&ctx, "ip_china.qzdb") != QZDB_OK) { /* fail */ }

/* 等价展开：verify_crc=1 开启 CRC32 校验 */
if (qzdb_init_ex(&ctx, "ip_china.qzdb", 1) != QZDB_OK) { /* fail */ }

/* 关闭 CRC 校验（仅在你已离线校验过、追求极限加载速度时） */
qzdb_init_ex(&ctx, "ip_china.qzdb", 0);
```

### 4.2 从内存缓冲区加载（嵌入式 / 网络下载）

适用于把 `.qzdb` 作为嵌入式资源，或运行时从内存直接解析、避免落盘：

```c
qzdb_reader_t ctx;
/* buf 为完整的 .qzdb 文件字节；内部会拷贝，调用后 buf 可立即释放 */
if (qzdb_init_buffer(&ctx, buf, buf_len, 1) != QZDB_OK) { /* fail */ }
```

### 4.3 关于分组（GroupIndex）

一个 `.qzdb` 文件可内嵌多个数据分组（如 `std` / `asn` / `max` / `ult` 等不同维度，由 `dimensionMask` 区分）。`qzdb_set_group_index` 选择加载哪一个；不调用时默认第 `0` 组。

```c
qzdb_set_group_index(&ctx, 0);   /* 主分组（默认） */
/* ASN 分组通常 dimMask=0x02，索引需按文件实际分组数查询：
   qzdb_get_group_count(&ctx) 获取分组数，越界返回 QZDB_ERR_INVALID_PARAM */
qzdb_set_group_index(&ctx, 2);   /* 例如 ASN 分组，必须 < qzdb_get_group_count(&ctx) */
```

> 必须在任何查询/查找调用**之前**设置 `group_index`；它影响全部 `find*` / `lookup*` API。

### 4.4 原子热更新（无需重启进程）

数据库文件更新后，调用 `qzdb_reload` 会**完整构建新快照**后原地替换上下文；旧数据在新快照构建完成前继续可用：

```c
/* 重新从文件加载（CRC 始终校验）；失败时旧 ctx 不变，返回错误码 */
if (qzdb_reload(&ctx, "ip_china_new.qzdb") != QZDB_OK) { /* 旧数据仍有效 */ }
```

> 对「零中断 + 多读线程」场景，推荐调用方用原子指针持有 `qzdb_reader_t*`，把 `qzdb_reload` 的结果换入新 ctx 再 `swap` 指针，避免更新瞬间旧数据被释放。

### 4.5 零拷贝借用式加载（`qzdb_init_buffer_borrowed`）

`qzdb_init_buffer` 会把传入缓冲区的内容**拷贝**进内部管理的内存；如果你已经用 mmap、共享内存、或其他方式持有一块生命周期明确的只读缓冲区，可以用借用式加载跳过这次拷贝：

```c
qzdb_reader_t ctx;
int rc = qzdb_init_buffer_borrowed(&ctx, mmap_ptr, mmap_len, /*verify_crc=*/1);
if (rc != QZDB_OK) {
    fprintf(stderr, "load failed: %s\n", qzdb_strerror(rc));
}
// ... 使用 ctx 正常查询 ...
qzdb_free(&ctx);  // 仅释放内部解析出的辅助结构，不会 free/munmap 传入的 mmap_ptr
```

**调用约定（必须遵守，否则未定义行为）**：
- 调用方必须保证 `mmap_ptr` 指向的内存在 `qzdb_free(&ctx)` 调用之前**始终有效且不被修改**。
- `qzdb_free` 不会释放/`munmap` 这块缓冲区——归还它是调用方的责任。
- 适用场景：数据库文件已由上层框架（自定义资源加载器、共享内存 IPC、`embed` 打包）映射好，只想复用这块内存做解析，不想再产生一份堆拷贝。

---

## 5. 查询 API

下表列出全部公开查询函数。输入形态多样：字符串 / IPv4 `uint32`（主机序）/ 16 字节二进制（IPv6 或 IPv4 映射）。

| 函数 | 返回 | 说明 |
|------|------|------|
| `int qzdb_find(ctx, ip_str, *info)` | `QZDB_OK` / `QZDB_ERR_NOT_FOUND` | 按字符串查（IPv4 / IPv6 / IPv4 映射地址） |
| `int qzdb_find_uint(ctx, ip_int, *info)` | 同上 | 按 IPv4 主机序 `uint32` 查 |
| `int qzdb_find_v6(ctx, ip_bin[16], *info)` | 同上 | 按 16 字节 IPv6 查 |
| `int qzdb_find_bytes(ctx, ip_bin[16], *info)` | 同上 | 4/16 字节；IPv4 映射地址自动降级到 V4 trie |
| `int qzdb_find_str(ctx, ip_str, out, sz)` | 写入长度 / `-1` | 直接返回 `to_pipe()` 结果；未命中写入 `""` |
| `int qzdb_find_fields(ctx, ip_str, fields[], *info)` | `QZDB_OK` / `NOT_FOUND` | 只解析指定字段 |
| `int qzdb_find_fields_uint(ctx, ip_int, fields[], *info)` | 同上 | `find_fields` 的 uint 形态 |

### 5.1 IP 输入约定

- **`qzdb_find(str)`**：接受点分十进制（`1.2.3.4`）与完整/压缩 IPv6（`2001:db8::1`）、IPv4 映射地址（`::ffff:1.2.3.4`）；非法格式返回 `QZDB_ERR_NOT_FOUND`。
- **`qzdb_find_uint(ip_int)`**：`ip_int` 为 IPv4 地址的**主机序 `uint32`**（即 `(a<<24)|(b<<16)|(c<<8)|d`）。若手上是网络序字节，请先转换或改用 `qzdb_find_bytes`。
- **`qzdb_find_bytes(ip_bin)`**：前 12 字节为 0 且 `10..11` 字节为 `0xFF 0xFF` 时按 IPv4 映射处理，降级到 V4 trie。

### 5.2 调用方缓冲（零拷贝）查询

对延迟极敏感或需避免 `malloc` 的场景，提供 `*_buf` 系列：调用方预分配 `char bufs[N][64]` 与 `char* values[N]`，SDK 直接写入该缓冲，**不分配堆内字符串**（此时 `values_mask` 全 0，无需 `qzdb_free_geo_info`）：

```c
char bufs[32][64];
char* values[32];
int n = qzdb_find_uint_buf(&ctx, ip_int, values, bufs, 32);
for (int i = 0; i < n; i++) printf("%s\n", values[i]);
```

> `bufs` 每行需至少 64 字节；`buf_size` 为字段数上限（一般取 `qzdb_get_field_count(&ctx)`）。

---

## 6. 结果对象 `qzdb_geo_info_t`

`qzdb_find*` 系列（非 `find_str`）通过 `qzdb_geo_info_t` 返回结果，内部为字段字符串数组：

```c
typedef struct {
    char* values[QZDB_MAX_FIELDS];   /* 字段值（顺序与 field_names 一致） */
    uint32_t values_mask;            /* 位 i=1 表示 values[i] 为堆内分配，需释放 */
} qzdb_geo_info_t;
```

### 6.1 通用取字段 `get(name)`

```c
const char* isp = qzdb_geo_info_get(&ctx, &info, "isp");
```

字段名匹配**忽略大小写和下划线/连字符**（例如 `country_code`、`CountryCode`、`countrycode` 等价）。未命中返回 `""`，**永不返回 NULL、永不抛异常**。

### 6.2 序列化输出 `to_pipe()`

```c
char pipe[1024];
qzdb_geo_info_to_pipe(&ctx, &info, pipe, sizeof(pipe));
```

`to_pipe()` 用 `|` 连接所有字段值（已是正确格式的字符串，**原样拼接不重新解析**）。`find_str` 等价于「`qzdb_find` + `to_pipe`」，但省去中间对象、未命中直接返回 `""`。

### 6.3 释放与 `get_cidr()`

```c
qzdb_free_geo_info(&info);   /* 释放被标记为堆内的字符串；可重复调用 / 空对象安全 */
const char* cidr = qzdb_geo_info_get_cidr();  /* 永远返回 ""（CIDR 非存储字段） */
```

> **数值格式化约定**（与参考实现一致）：整数值输出为无小数点（如 `"116"`）；非整数浮点固定 6 位小数（如 `"116.400000"`）；`NaN` / `Inf` 输出为 `""`。**不存在最短路/最短表示**。

---

## 7. 字段投影 `find_fields`

当你只需要部分字段时，用 `find_fields` 只解析指定字段，减少不必要的字符串工作：

```c
const char* want[] = { "country", "city", "isp", NULL };
qzdb_geo_info_t info;
if (qzdb_find_fields(&ctx, "114.114.114.114", want, &info) == QZDB_OK) {
    /* info.values 中仅 want 指定的字段被填充（按字段规范索引落位） */
    qzdb_free_geo_info(&info);
}
```

`fields` 为 **NULL 终止**的字段名数组；传 `NULL` 或空数组等价于 `find`。字段名大小写/下划线/连字符不敏感。

---

## 8. 行号 / ID 反查与 CIDR

### 8.1 仅取行号（Layer 1，仅 trie 遍历）

```c
uint32_t row = qzdb_lookup_row_id(&ctx, "8.8.8.8");     /* 字符串 */
uint32_t row = qzdb_lookup_row_id_uint(&ctx, ip_int);   /* IPv4 uint */
uint32_t row = qzdb_lookup_row_id_v6(&ctx, ip_bin);     /* IPv6 16 字节 */
uint32_t row = qzdb_lookup_row_id_bytes(&ctx, ip_bytes, len); /* 4 或 16 字节 */
```

返回 **1 基的行号**；未命中/非法返回 `0`。

### 8.2 反查 Geo/ASN/Usage 三类 ID（Layer 2）

```c
qzdb_ids_t ids;
if (qzdb_lookup_ids(&ctx, row_id, &ids) == 0) {
    printf("geo=%u asn=%u usage=%u\n", ids.geo_id, ids.asn_id, ids.usage_id);
}
```

`row_id` 来自 8.1；成功返回 `0`，错误返回 `-1`。

### 8.3 CIDR 反向查询

由 IP 反推其所属网段（网络来自 trie 叶子深度；IPv6 遵循 RFC 5952）：

```c
char cidr[64];
if (qzdb_lookup_cidr(&ctx, "1.0.1.0", cidr, sizeof(cidr)))
    printf("%s\n", cidr);   /* 例如 "1.0.1.0/24" */
if (qzdb_lookup_cidr_uint(&ctx, ip_int, cidr, sizeof(cidr))) { /* IPv4 uint */ }
if (qzdb_lookup_cidr_bytes(&ctx, ip_bytes, len, cidr, sizeof(cidr))) { /* 4/16 字节 */ }
```

IP 不在库内或非法时返回 `NULL`，`out` 不被修改。

### 8.4 独立严格 IP 解析（无需数据库）

```c
uint32_t v4; uint8_t v6[16]; int is_v4;
if (qzdb_parse_ip("114.114.114.114", &v4, v6, &is_v4)) {
    /* 合法：is_v4=1 时看 v4（主机序）；否则 v6[16] 为地址 */
}
```

`qzdb_parse_ip` 严格拒绝前导零、`>255`、缺段、CIDR 后缀、空白、zone-id 等非法格式；合法返回 `1`，非法返回 `0`。

---

## 9. 批量与流式查询

`qzdb_find_batch` 和 `qzdb_find_each` 都是**顺序执行**（内部不建线程池、不做并行调度），区别在于结果的返回方式：

### 9.1 一次性批量：`qzdb_find_batch`

```c
const char* ips[] = {"114.114.114.114", "223.5.5.5", "8.8.8.8"};
qzdb_batch_result_t results[3];

qzdb_find_batch(&ctx, ips, 3, results);

for (int i = 0; i < 3; i++) {
    if (results[i].error_code == QZDB_OK) {
        printf("%s => %s\n", ips[i], qzdb_geo_info_get(&ctx, &results[i].info, "country"));
    } else {
        printf("%s => error: %s\n", ips[i], qzdb_strerror(results[i].error_code));
    }
    qzdb_free_geo_info(&results[i].info);  /* 逐条释放 */
}
```

`results` 数组由调用方分配（栈上或堆上均可），大小需 ≥ `count`；每个 `qzdb_batch_result_t` 含 `info`（`qzdb_geo_info_t`）和 `error_code`（单条查询的独立错误码，一条失败不影响其余条目）。

### 9.2 回调流式：`qzdb_find_each`

不预分配结果数组，每查完一条立即回调，内存占用恒定，适合大批量 IP 扫描：

```c
void on_result(int index, const qzdb_batch_result_t* result, void* user_data) {
    if (result->error_code == QZDB_OK) {
        printf("[%d] %s\n", index, qzdb_geo_info_get(NULL, &result->info, "country"));
    }
    /* 回调内不会自动释放 result->info，如需长期持有字段字符串，
       请自行 qzdb_free_geo_info() 或拷贝所需字段后再返回 */
}

qzdb_find_each(&ctx, ips, 3, on_result, /*user_data=*/NULL);
```

回调签名：`void (*qzdb_find_callback)(int index, const qzdb_batch_result_t* result, void* user_data)`；`user_data` 原样透传，用于携带上下文（如输出文件句柄、计数器等）。

---

## 10. 链式多库查询 `ChainedReader`

用于合并多个已加载的 `qzdb_reader_t`（比如标准版+ASN版、国内库+国际库），按顺序查询直到命中。支持三种模式：

| 模式 | 常量 | 行为 |
|---|---|---|
| Fallback（默认推荐） | `QZDB_CHAIN_FALLBACK` | 按顺序查询，**返回第一个命中的完整结果**，不合并字段 |
| Merge | `QZDB_CHAIN_MERGE` | 依次查询所有 reader，**逐字段合并**，同名字段以**先注册的 reader 为准**（不覆盖已有值） |
| MergeOverride | `QZDB_CHAIN_MERGE_OVERRIDE` | 同 Merge，但同名字段以**后注册的 reader 为准**（后者覆盖前者） |

```c
qzdb_reader_t std_ctx, asn_ctx;
qzdb_init(&std_ctx, "qqzeng_ip_std.qzdb");
qzdb_init(&asn_ctx, "qqzeng_ip_asn.qzdb");

qzdb_reader_t* readers[] = { &std_ctx, &asn_ctx };
qzdb_chain_t* chain = qzdb_chain_new(readers, 2, QZDB_CHAIN_MERGE);

qzdb_geo_info_t info;
int rc = qzdb_chain_find(chain, "114.114.114.114", &info);
if (rc == QZDB_OK) {
    printf("country=%s asn=%s\n",
           qzdb_geo_info_get(&std_ctx, &info, "country"),
           qzdb_geo_info_get(&asn_ctx, &info, "asn"));
}
qzdb_free_geo_info(&info);

// 批量版本
qzdb_batch_result_t results[3];
qzdb_chain_find_batch(chain, ips, 3, results);

qzdb_chain_free(chain);  // 只释放 chain 自身，不会释放传入的各个 reader
```

**注意事项**：
- `qzdb_chain_new` 不会接管传入 reader 的生命周期——调用方需要自行在释放 chain 前后分别 `qzdb_free` 每个 reader。
- Fallback 模式下，如果某个 reader 查询返回的不是“未命中”而是真实错误（如损坏数据），链式查询会**立即停止并返回该错误**，不会静默跳到下一个 reader。
- 提供 `qzdb_chain_find_uint`（IPv4 整数入参）、`qzdb_chain_find_bytes`（16 字节 IPv6/v4-mapped 入参）、`qzdb_chain_find_str`（直接输出竖线字符串）等变体，用法与 `qzdb_find_*` 系列对应。

---

## 11. 命名注册表 `QzdbRegistry`

管理多个带名字的 reader 实例（比如按业务线/按版本命名），提供线程安全的注册、查找、注销。内部为哈希表+互斥锁实现，注册/注销有锁，但通过 `qzdb_registry_get` 拿到的 `qzdb_reader_t*` 之后的查询本身仍是无锁的。

```c
qzdb_registry_t* reg = qzdb_registry_new();

qzdb_registry_register(reg, "std", "qqzeng_ip_std.qzdb");
qzdb_registry_register(reg, "ult", "qqzeng_ip_ult.qzdb");
// 也可以从内存缓冲注册（CRC 校验固定开启）：
// qzdb_registry_register_buffer(reg, "asn", buf, buf_len);

qzdb_reader_t* r = qzdb_registry_get(reg, "ult");
if (r) {
    char out[256];
    qzdb_find_str(r, "114.114.114.114", out, sizeof(out));
    printf("%s\n", out);
}

printf("registered: %d\n", qzdb_registry_count(reg));

qzdb_registry_unregister(reg, "std");  // 内部会 qzdb_free 对应 reader 并释放
qzdb_registry_free(reg);               // 释放 registry 及其持有的全部 reader
```

**注意事项**：
- `qzdb_registry_register` / `_register_buffer` 内部会加载数据库，**加载失败时不会注册该条目**，函数直接返回对应错误码。
- `qzdb_registry_free` 会级联释放所有已注册的 reader，调用方**不应该**再对通过 `qzdb_registry_get` 拿到的指针单独调用 `qzdb_free`（会造成 double free）。
- 与 `qzdb_chain_t` 的区别：Registry 是“按名字取用单个 reader”，Chain 是“按顺序合并查询多个 reader”，两者可以组合使用。

---

## 12. 元数据访问器

加载后即可读取文件元数据，**全部永不返回 NULL**（缺失时返回 `""` 或合理默认值）：

| 函数 | 返回 | 说明 |
|------|------|------|
| `qzdb_get_version(ctx)` | `const char*` | 版本列表（meta type=1） |
| `qzdb_get_data_month(ctx)` | `const char*` | 数据月份 `"yyyy-MM"`（取自 Header BuildDate） |
| `qzdb_get_edition(ctx)` | `const char*` | 版本/版式（meta type=4，否则按字段数推断） |
| `qzdb_get_scope(ctx)` | `const char*` | 始终 `""`（暂无 scope 字段） |
| `qzdb_get_build_time(ctx)` | `const char*` | 构建时间 `"yyyy-MM-dd"` |
| `qzdb_get_description(ctx)` | `const char*` | 文件描述（meta type=3） |
| `qzdb_get_file_hash(ctx, out, sz)` | `int` | CRC32 十六进制（8 位小写）写入 `out`，返回 0 |
| `qzdb_get_field_names(ctx)` | `const char**` | 字段名数组（NULL 终止） |
| `qzdb_get_field_count(ctx)` | `int` | 字段数 |
| `qzdb_has_field(ctx, name)` | `int` | 是否存在该字段（`1`/`0`） |
| `qzdb_get_group_count(ctx)` | `int` | 分组数 |
| `qzdb_get_pool_count(ctx)` | `int` | 字符串池数 |

```c
printf("edition=%s month=%s fields=%d\n",
       qzdb_get_edition(&ctx),
       qzdb_get_data_month(&ctx),
       qzdb_get_field_count(&ctx));

char hash[16];
if (qzdb_get_file_hash(&ctx, hash, sizeof(hash)) == 0)
    printf("crc32=%s\n", hash);
```

### 12.1 版本档次判定与用途分类

**档次（Edition）判定来源**：`qzdb_get_edition()` 返回的档次字符串（`std`/`asn`/`pro`/`max`/`ult`）有多种推断来源，可用 `qzdb_get_edition_source()` 查看当前是按哪种方式判定的：

```c
uint16_t mask = qzdb_get_version_mask(&ctx);        // Header offset 6 的原始 one-hot 掩码
const char* edition = qzdb_edition_from_mask(mask); // 掩码 → 档次名，非 one-hot 或越界返回 ""
const char* source = qzdb_get_edition_source(&ctx); // "version_mask" / "metadata" / "inferred" / "unknown"
const char* names_src = qzdb_get_field_names_source(&ctx); // "metadata" / "edition" / "synthetic"
```

`version_mask` 是档次判定的**权威来源**（bit0=std bit1=asn bit2=pro bit3=max bit4=ult，one-hot 编码）；不可用或非法 one-hot 值时才降级到 metadata 推断或 synthetic 兜底。

**用途分类（UsageType）**：如果数据库带 `usage_type` 字段（不同档次库不一定都有）：

```c
qzdb_geo_info_t info;
qzdb_find(&ctx, "1.2.3.4", &info);

const char* raw = qzdb_geo_usage_type(&ctx, &info);  // 如 "IDC" / "Mobile" / "VPN" 等原始标签
if (qzdb_usage_type_is_known(raw)) {
    printf("%s / %s\n",
           qzdb_usage_type_display_zh(raw),   // 中文展示名，如"数据中心"
           qzdb_usage_type_description(raw));
} else {
    printf("未知用途类型: %s\n", raw);
}
qzdb_free_geo_info(&info);
```

已知类型覆盖：`Government`（政府）、`ISP`、`IXP`（交换中心）、`IoT`、`Mobile`、`Reserved`（保留地址）、`Satellite`（卫星互联网）、`Spider`（爬虫）、`Streaming`（流媒体）、`Unknown`、`VPN` 等；`qzdb_usage_type_display_zh/en` 对未收录标签分别兜底返回 `"未知"` / `"Unknown"`。

---

## 13. 错误处理

加载/解析期错误以**负错误码**返回（不抛异常、不 `longjmp`）。`qzdb_strerror(code)` 给出可读描述：

```c
int rc = qzdb_init(&ctx, "ip_china.qzdb");
if (rc != QZDB_OK) {
    fprintf(stderr, "加载失败 [%d]: %s\n", rc, qzdb_strerror(rc));
    return 1;
}
```

错误码枚举（`< 0` 为错误）：

| 宏 | 值 | 触发场景 |
|----|----|---------|
| `QZDB_OK` | `0` | 成功 |
| `QZDB_ERR_NOT_FOUND` | `-1` | 查询未命中 / IP 格式非法 |
| `QZDB_ERR_CORRUPTED` | `-2` | 分区越界、分组数为 0、CRC32 不匹配（数据损坏/截断） |
| `QZDB_ERR_OUT_OF_MEMORY` | `-3` | 内存不足 |
| `QZDB_ERR_INVALID_PARAM` | `-4` | 参数为 NULL / `group_index` 越界 / 字段宽度非法 |
| `QZDB_ERR_BAD_HEADER` | `-5` | 文件头尺寸异常 |
| `QZDB_ERR_BAD_MAGIC` | `-6` | 文件头不是 `QZDB` 魔数 |
| `QZDB_ERR_UNSUPPORTED` | `-7` | 格式版本不受支持 |
| `QZDB_ERR_BOUNDS` | `-8` | 越界访问 |

> **Fail-Closed（加载期）**：魔数/头部/CRC/截断任何一项异常，`qzdb_init*` 立即返回错误码，绝不加载半损坏文件。
> **查询期**：普通「未命中」与「IP 格式非法」通过返回 `QZDB_ERR_NOT_FOUND` 表达，不报错、不崩溃。

---

## 14. 并发与性能

- **无锁快照读取**：加载完成后，所有 `find*` / `lookup*` / 元数据 API 均为只读，多个线程可同时调用同一 `ctx`，互不阻塞。
- **per-snapshot 有界 `GeoInfo` 解码缓存**：GeoInfo 解码缓存默认为固定 **16384 槽位**（`1 << 14`）、只填不淘汰的开放寻址表（可通过编译期宏 `-DQZDB_GEO_CACHE_CAP=N` 自行调整容量，需为 2 的幂）。快照不可变 → 同一 `(group<<40 | entry_id)` 永远解析出同一组字段字符串。热点 IP 直接命中缓存，**命中路径零分配、零锁争用**。超出容量后新查询到的条目走非缓存路径解码，不影响正确性但会降低该条目的吞吐；如数据库 distinct 地理条目数明显超过该值，命中率会相应下降。
- **SENTINEL 截断**：字段索引的最高位 sentinel 位（`0x80000000` / `0x800000`）在解析前被剥离，避免误判为越界。
- **零堆分配查询路径**：`*_buf` 系列与 `find_str` 全程使用调用方缓冲，`malloc` 次数趋近于 0。
- **IPv4-Mapped 降级**：`::ffff:a.b.c.d` 自动降级到 V4 trie，无需调用方特判。

> 实际吞吐随 CPU、数据规模、查询分布而变；上述设计为量级说明，非 SLA。

---

## 15. 完整 API 参考

### 15.1 加载与生命周期

```c
int  qzdb_init(qzdb_reader_t* ctx, const char* db_path);
int  qzdb_init_ex(qzdb_reader_t* ctx, const char* db_path, int verify_crc);
int  qzdb_init_buffer(qzdb_reader_t* ctx, const uint8_t* buf, size_t len, int verify_crc);
int  qzdb_init_buffer_borrowed(qzdb_reader_t* ctx, const uint8_t* buf, size_t len, int verify_crc);
void qzdb_free(qzdb_reader_t* ctx);
int  qzdb_reload(qzdb_reader_t* ctx, const char* db_path);
int  qzdb_set_group_index(qzdb_reader_t* ctx, int group_index);
const char* qzdb_strerror(int error_code);
int  qzdb_verify_crc(qzdb_reader_t* ctx);              /* 手动重新校验 CRC，返回错误码 */
```

> **无单例设计**：v2.4 起 C SDK 不提供任何进程级单例。每个 `qzdb_reader_t` 由调用方在栈/堆上持有，自行决定生命周期与复用策略（如存入全局指针或线程局部变量）。多文件/多版本请用多个 `qzdb_reader_t` 实例分别 `qzdb_init`，互不干扰。

### 15.2 查询

```c
int  qzdb_find(qzdb_reader_t* ctx, const char* ip_str, qzdb_geo_info_t* result);
int  qzdb_find_uint(qzdb_reader_t* ctx, uint32_t ip_int, qzdb_geo_info_t* result);
int  qzdb_find_v6(qzdb_reader_t* ctx, const uint8_t* ip_bin, qzdb_geo_info_t* result);
int  qzdb_find_bytes(qzdb_reader_t* ctx, const uint8_t ip_bin[16], qzdb_geo_info_t* result);
int  qzdb_find_str(qzdb_reader_t* ctx, const char* ip_str, char* out, size_t out_size);
int  qzdb_find_fields(qzdb_reader_t* ctx, const char* ip_str,
                      const char** fields, qzdb_geo_info_t* result);
int  qzdb_find_fields_uint(qzdb_reader_t* ctx, uint32_t ip_int,
                           const char** fields, qzdb_geo_info_t* result);

/* 调用方缓冲（零堆分配） */
int  qzdb_find_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int,
                        char** values, char (*bufs)[64], int buf_size);
int  qzdb_find_v6_buf(qzdb_reader_t* ctx, const uint8_t* ip_bin,
                      char** values, char (*bufs)[64], int buf_size);
int  qzdb_find_fields_buf(qzdb_reader_t* ctx, const char* ip_str,
                          const char** field_names,
                          char** values, char (*bufs)[64], int buf_size);
int  qzdb_find_fields_uint_buf(qzdb_reader_t* ctx, uint32_t ip_int,
                               const char** field_names,
                               char** values, char (*bufs)[64], int buf_size);
```

### 15.3 批量与流式查询

```c
int  qzdb_find_batch(qzdb_reader_t* ctx, const char** ips, int count,
                     qzdb_batch_result_t* results);
int  qzdb_find_each(qzdb_reader_t* ctx, const char** ips, int count,
                    qzdb_find_callback callback, void* user_data);
```

### 15.4 链式多库查询与命名注册表

```c
/* ChainedReader */
qzdb_chain_t* qzdb_chain_new(qzdb_reader_t** ctxs, int count, int mode);
int           qzdb_chain_find(qzdb_chain_t* chain, const char* ip, qzdb_geo_info_t* out);
int           qzdb_chain_find_uint(qzdb_chain_t* chain, uint32_t ip, qzdb_geo_info_t* out);
int           qzdb_chain_find_bytes(qzdb_chain_t* chain, const uint8_t ip16[16], qzdb_geo_info_t* out);
int           qzdb_chain_find_batch(qzdb_chain_t* chain, const char** ips, int count,
                                    qzdb_batch_result_t* results);
int           qzdb_chain_find_str(qzdb_chain_t* chain, const char* ip, char* buf, size_t size);
const char**  qzdb_chain_editions(qzdb_chain_t* chain, int* count);
const char**  qzdb_chain_scopes(qzdb_chain_t* chain, int* count);
const char**  qzdb_chain_data_months(qzdb_chain_t* chain, int* count);
void          qzdb_chain_free(qzdb_chain_t* chain);

/* QzdbRegistry */
qzdb_registry_t* qzdb_registry_new(void);
void             qzdb_registry_free(qzdb_registry_t* reg);
int              qzdb_registry_register(qzdb_registry_t* reg, const char* name, const char* path);
int              qzdb_registry_register_buffer(qzdb_registry_t* reg, const char* name,
                                               const uint8_t* buf, size_t len);
qzdb_reader_t*   qzdb_registry_get(qzdb_registry_t* reg, const char* name);
void             qzdb_registry_unregister(qzdb_registry_t* reg, const char* name);
int              qzdb_registry_count(qzdb_registry_t* reg);
```

### 15.5 行号 / ID / CIDR / 解析

```c
uint32_t qzdb_lookup_row_id(qzdb_reader_t* ctx, const char* ip_str);
uint32_t qzdb_lookup_row_id_uint(qzdb_reader_t* ctx, uint32_t ip_int);
uint32_t qzdb_lookup_row_id_v6(qzdb_reader_t* ctx, const uint8_t* ip_bin);
uint32_t qzdb_lookup_row_id_bytes(qzdb_reader_t* ctx, const uint8_t* ip_bytes, int len);
int  qzdb_lookup_ids(qzdb_reader_t* ctx, uint32_t row_id, qzdb_ids_t* out);
int  qzdb_parse_ip(const char* s, uint32_t* v4_out, uint8_t v6_out[16], int* is_v4);

char* qzdb_lookup_cidr(qzdb_reader_t* ctx, const char* ip_str, char* out, size_t out_size);
char* qzdb_lookup_cidr_uint(qzdb_reader_t* ctx, uint32_t ip_int, char* out, size_t out_size);
char* qzdb_lookup_cidr_bytes(qzdb_reader_t* ctx, const uint8_t* ip_bytes, int len,
                             char* out, size_t out_size);
```

### 15.6 结果对象访问

```c
const char* qzdb_geo_info_get(qzdb_reader_t* ctx, const qzdb_geo_info_t* info, const char* name);
int  qzdb_geo_info_to_pipe(qzdb_reader_t* ctx, const qzdb_geo_info_t* info,
                           char* out, size_t out_size);
const char* qzdb_geo_info_get_cidr(void);   /* 永远返回 "" */
void qzdb_free_geo_info(qzdb_geo_info_t* info);
```

### 15.7 元数据访问与自省

```c
const char* qzdb_get_version(qzdb_reader_t* ctx);
const char* qzdb_get_data_month(qzdb_reader_t* ctx);
const char* qzdb_get_edition(qzdb_reader_t* ctx);
const char* qzdb_get_edition_source(qzdb_reader_t* ctx);
const char* qzdb_get_field_names_source(qzdb_reader_t* ctx);
uint16_t    qzdb_get_version_mask(qzdb_reader_t* ctx);
const char* qzdb_edition_from_mask(uint16_t mask);
const char* qzdb_get_scope(qzdb_reader_t* ctx);       /* 永远返回 "" */
const char* qzdb_get_build_time(qzdb_reader_t* ctx);
const char* qzdb_get_description(qzdb_reader_t* ctx);
int  qzdb_get_file_hash(qzdb_reader_t* ctx, char* out, size_t out_size);
const char** qzdb_get_field_names(qzdb_reader_t* ctx); /* NULL 终止 */
int  qzdb_get_field_count(qzdb_reader_t* ctx);
int  qzdb_has_field(qzdb_reader_t* ctx, const char* name);
int  qzdb_get_group_count(qzdb_reader_t* ctx);
int  qzdb_get_pool_count(qzdb_reader_t* ctx);

/* UsageType */
int         qzdb_usage_type_is_known(const char* raw);
const char* qzdb_usage_type_display_zh(const char* raw);
const char* qzdb_usage_type_display_en(const char* raw);
const char* qzdb_usage_type_description(const char* raw);
```

### 15.8 数据结构

```c
typedef struct {
    char* values[QZDB_MAX_FIELDS];  /* 字段值（顺序与 field_names 一致） */
    uint32_t values_mask;           /* 位 i=1 表示 values[i] 需 qzdb_free_geo_info 释放 */
} qzdb_geo_info_t;

typedef struct {
    uint32_t geo_id;
    uint32_t asn_id;
    uint32_t usage_id;
} qzdb_ids_t;

typedef struct {
    qzdb_geo_info_t info;
    int error_code;
} qzdb_batch_result_t;

typedef void (*qzdb_find_callback)(int index, const qzdb_batch_result_t* result, void* user_data);

/* 错误码见第 13 节；QZDB_OK=0，其余为负。 */
typedef enum { QZDB_OK=0, QZDB_ERR_NOT_FOUND=-1, QZDB_ERR_CORRUPTED=-2,
               QZDB_ERR_OUT_OF_MEMORY=-3, QZDB_ERR_INVALID_PARAM=-4,
               QZDB_ERR_BAD_HEADER=-5, QZDB_ERR_BAD_MAGIC=-6,
               QZDB_ERR_UNSUPPORTED=-7, QZDB_ERR_BOUNDS=-8 } qzdb_error_t;
```

---

## 16. 项目结构

`multi-lang/c/` 目录：

| 文件 | 职责 |
|------|------|
| `qzdb_reader.h` | 公共头：全部函数声明、数据结构、`QZDB_*` 宏与错误码 |
| `qzdb_reader.c` | 核心实现：加载、trie 遍历（V4/V6）、查询、解码缓存、CRC、元数据、CIDR、热更新、ChainedReader、Registry |
| `test_main.c` | Tier1 单元测试（≥50 断言、无需数据库即可覆盖合同 §10 九大类） |
| `golden_check.c` | Tier2 黄金校验：加载 `std`/`ult` 库，对 `golden_vectors.json` 断言 `to_pipe()` 一致（强制 0 失败） |
| `main.c` / `batch_query.c` / `batch_cli.c` | 示例与批量查询 CLI |
| `bench_qps.c` | 吞吐基准 |

跨语言完整 API 规范见仓库根 [`API_CONTRACT.md`](../../API_CONTRACT.md)。

---

## License

[MIT](https://opensource.org/licenses/MIT)

<!-- commit: c: C SDK（零拷贝 mmap 读取，单文件集成） sync=1787949479 -->
