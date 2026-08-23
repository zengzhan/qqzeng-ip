# 补丁评审报告 — `files/` 优化代码

日期：2026-08-10 ｜ 范围：`files/qzdb_reader.{c,h}`、`files/QzdbReader.java`、`files/boundary_test.c`、`files/qzdb_patched.zip`

---

## 结论速览

| # | 补丁项 | 判定 | 处置 |
|---|---|---|---|
| 1 | C 缓存改条带锁（每槽一把 mutex） | ❌ **拒绝** — 引入高频 use-after-free | 重写为无锁原子发布 |
| 2 | `qzdb_strerror` 负错误码修复 | ✅ 真 Bug 修复 | 已采纳 |
| 3 | `_DEFAULT_SOURCE` 特性宏 | ✅ Linux 可移植性修复 | 已采纳 |
| 4 | Java `AtomicReferenceArray` 解码缓存 | ✅ 设计正确 | 已采纳并加固 |
| 5 | `boundary_test.c` 边界/fuzz harness | ⚠️ 有构建缺陷 | 修复后收入仓库 |
| 6 | "仓库没有 fuzz 测试" 的论断 | ❌ 事实错误 | 见下文 |

审计报告对 **问题的定位是准确的**（C 的全局锁确实让 README 的 "lock-free" 名不副实），但 **给出的修法是错的**，而且错得比原状更危险。

---

## 1. ❌ C 条带锁方案：把罕见的 UAF 放大成高频 UAF

### 根因不在锁，在**生命周期契约**

`resolve_row_id_cached()` 把缓存槽内的字符串指针**借**给调用方，且**不置 `values_mask`**：

```c
for (int i = 0; i < cnt && i < QZDB_MAX_FIELDS; i++)
    result->values[i] = cached[i];   // 借用，qzdb_free_geo_info 不会释放
```

这条借用只有在「字符串不会被别人释放」时才成立。**任何淘汰机制都会击穿它**——无论淘汰是被一把全局锁保护还是被每槽一把锁保护。锁只解决数据竞争，不解决悬垂指针。

补丁把淘汰条件从「16384 个槽全满」改成「哈希碰撞即淘汰」。按生日悖论，16384 槽在约 **181 个不同 entry 时就开始碰撞**——于是 UAF 从"极难触发"变成"必然触发"。

### 实证

```
$ cc -std=c11 -g -O1 -fsanitize=address ... && ./repro_patched ult_china.qzdb
==66597==ERROR: AddressSanitizer: heap-use-after-free
READ of size 2 at 0x602000009690
    #1 main repro_uaf.c:38
freed by thread T0 here:
    #1 resolve_row_id_cached patched.c:780
    #3 qzdb_find_batch patched.c:813
```

触发代码就是 `qzdb_find_batch()` 的**正常用法**（先批量查完，再统一读结果）：

| 版本 | 4000 IP / china 库 | 60000 IP / global 库 |
|---|---|---|
| 原版（全局锁） | ✅ 通过 | ❌ **UAF** |
| 补丁版（条带锁） | ❌ **UAF** | ❌ UAF |
| 现修复版 | ✅ 通过 | ✅ 通过 |

注意中间一列：**原版也有这个洞**，只是要等 16384 槽填满才暴露。所以这不只是"补丁引入回归"，而是**原本就存在、被审计漏掉的 P0**。

### 采用的修法：不可变条目 + CAS 发布 + 永不淘汰

`multi-lang/c/qzdb_reader.h` 新增 `qzdb_cache_entry_t`，并把契约写死在注释里：

* **读路径**：每槽一次 `__atomic_load_n(..., __ATOMIC_ACQUIRE)`，**完全无锁、无原子写**，多核线性扩展。
* **写路径**：条目**先构造完整**，再用一次 release CAS 发布。读者只会看到 `NULL` 或一个完整不可变的条目，不存在撕裂。
* **淘汰**：**没有**。探测窗口（4 槽）用尽即报未命中，回退到 `get_geo_info()`——它返回**自有内存**并正确置位 `values_mask`，所有权干净。
* 头文件保持 C++ 可用：`_Atomic` 不进公共头，原子性由 `.c` 里的 `__atomic_*` 内建实现。

顺带修掉一个原版的**内存泄漏**：`geo_cache == NULL`（calloc 失败）时原版返回堆内存却不置 mask，必漏；现在统一走回退路径。

### 扩展性实测（std_global，命中率 100%，M1 Mac）

| 线程 | 原版（全局锁） | 修复版（无锁） | 提升 |
|---|---|---|---|
| 1 | 21.81M QPS | 22.18M QPS | 1.0× |
| 2 | 15.26M QPS | 41.57M QPS | 2.7× |
| 4 | 7.80M QPS | 79.90M QPS | 10.2× |
| 8 | **7.02M QPS** | **153.70M QPS** | **21.9×** |

原版是**负扩展**（越多核越慢，典型锁护航）；修复版 8 线程 7.05× 近线性。README 里 "C 无锁并发" 这句话现在才是真的。

---

## 2 & 3. ✅ 两处真修复，已采纳

**`qzdb_strerror`**：错误码是 `0` 或负数（`QZDB_ERR_* = -1..-8`），表按取负索引，但守卫写的是 `error_code >= 0` —— **除 `QZDB_OK` 外所有错误码都返回 "Unknown error"**。修复后 `boundary_test` 的输出立刻从一片 "Unknown error" 变成 "Bad magic" / "Unsupported format"。

**`_DEFAULT_SOURCE`**：glibc 在 `-std=c11` 严格模式下隐藏 `strdup()`，会退化成隐式 `int` 返回声明 → LP64 上指针截断 UB，且 `MADV_RANDOM` 未定义。本机 macOS 不触发，Linux 上是真雷。

---

## 4. ✅ Java 缓存：设计正确，已采纳并加固

审计说"8 语言里唯独 Java 没有解码缓存"——属实。补丁的 `AtomicReferenceArray<CacheEntry>` 方案是对的：

* `CacheEntry` 全 final 字段 + volatile 写发布 → 安全发布，无撕裂，无需锁；
* `GeoInfo.values()` / `fieldNames()` 均返回 `clone()`，实例可安全跨线程共享；
* Java 有 GC，碰撞覆盖**不会**产生 C 那样的 UAF——**同一套"碰撞即淘汰"的写法，在 Java 安全、在 C 致命**。这正是不能跨语言照搬的地方。

**加固**：缓存以 `entryId` 单独作键，正确性依赖「`groupIndex` 在单个 Snapshot 内不变」。该字段原为非 final，我改成 `final int groupIndex` 并写明理由，让编译器守住这个不变量（切换分组本来就走重建 Snapshot 的路径）。

行为变化（可接受）：字段投影模式现在先解全字段再切片，未命中时比原来多解几个字段，命中时更快。

**吞吐实测**（std_global）：

| 线程 | 补丁前 | 补丁后 | 提升 |
|---|---|---|---|
| 1 | 6.79M QPS | 8.04M QPS | +18% |
| 4 | 22.10M QPS | 27.49M QPS | +24% |
| 8 | 39.68M QPS | 47.59M QPS | +20% |

（本基准每次都调 `toPipeString()`，其自身分配稀释了增益；真实收益主要在 GC 压力下降。）

---

## 5 & 6. fuzz harness：有价值，但审计的前提错了

**审计说"没看到 fuzz/边界测试"——不成立。** 仓库早有 `multi-lang/c/failclosed.c`，基于真实 `.qzdb` 做四类变异：截断扫描、192B 头逐字节 4 位模式穷举（752 例）、前 512KB 随机翻位（2000 次）、随机截断（500 次），要求 ASan 零报错。

不过 `boundary_test.c` 仍有独立价值：它**完全不依赖数据文件**，纯合成畸形缓冲区，因此能在没有 `.qzdb` 的 CI 里跑——这恰好命中审计真正想说的问题。已收入 `multi-lang/c/fuzz/boundary_test.c`。

**修掉的构建缺陷**：文件头注释教人用 `-fsanitize=fuzzer -DQZDB_LIBFUZZER` 构建，但 `main()` 无条件定义，与 libFuzzer 自带的 `main` **符号冲突，照文档必然链接失败**。已用 `#ifndef QZDB_LIBFUZZER` 把 `main()` 与 `check_rejects()` 一并圈起（后者不圈会在 fuzzer 模式下报 unused-function 告警——本项目把测试代码的告警当信号，历史上正是靠它揪出两个休眠测试）。

`qzdb_patched.zip` 里附带的 1.3MB 编译产物 `c/fuzz/boundary_test` 未入库。

---

## 验证矩阵（全部在本机实跑）

| 项目 | 结果 |
|---|---|
| C 编译 `-Wall -Wextra` | 0 warning |
| C 单元测试 `test_main.c` | **167/167** 断言 |
| C golden 逐字节对拍 | **4102/4102** |
| C `failclosed` (3252 畸形例, ASan+UBSan) | 全过，零报错 |
| C `boundary_test` (ASan+UBSan) | 0 failure |
| C libFuzzer 模式编译 | 0 warning，无 `main` 符号冲突 |
| C 7 个可执行程序 | 全部编译通过 |
| C UAF 复现（60000 IP, ASan+UBSan） | 通过（原版此处崩） |
| C ThreadSanitizer（8 线程 ×20k） | 无竞态报告 |
| C 内存泄漏（`leaks --atExit`） | **0 leaks** |
| Java 编译 `-Xlint:all` | 0 warning |
| Java 测试套件 | **47/47**（198 断言，含 16 线程并发 + 热重载） |
| Java golden 逐字节对拍 | **4102/4102** |

C 与 Java 都对同一份 `golden_vectors.json` 逐字节通过，跨语言一致性未被本次改动破坏。

---

## 变更文件

* `multi-lang/c/qzdb_reader.h` — `qzdb_cache_slot_t` → `qzdb_cache_entry_t`；删除 `pthread_mutex_t geo_cache_lock`；补 LIFETIME CONTRACT 注释
* `multi-lang/c/qzdb_reader.c` — 缓存整体重写；`qzdb_strerror` 修复；`_DEFAULT_SOURCE`
* `multi-lang/java/.../QzdbReader.java` — 无锁解码缓存；`groupIndex` 改 final
* `multi-lang/c/fuzz/boundary_test.c` — 新增（已修 libFuzzer 构建缺陷）

## 遗留建议

1. **CI**：审计提的"打包一个几百条的最小 `.qzdb` 进仓库 + GitHub Actions matrix"是对的，仓库目前确实没有 `.github/workflows`。建议 PR 触发：8 语言 golden 对拍 + `boundary_test`（无需数据）+ `failclosed`（需最小库）。
2. **探测窗口**：`QZDB_CACHE_PROBE = 4` 是保守取值。若实测工作集远大于 16384，可调大槽数而非窗口——窗口变大只会拉长最坏读路径。
3. `files/` 是你的投放目录，未纳入 git；如不需要可删除或加入 `.gitignore`。
