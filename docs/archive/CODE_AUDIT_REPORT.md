# QZDB 多语言 SDK 代码深度体检与现代化审计报告

> 审计日期：2026-08-08 ｜ 范围：8 语言 SDK（C / Go / Rust / Python / Node / PHP / Java / C#）
> 方法：逐语言逐文件精读 + 真实数据（`data/qqzeng_ip_ult_china.qzdb` 等）实测复现 + 跨语言一致性比对
> 约束：**核心查找算法（哨兵位剥离 `SENTINEL`、trie walk、row 解析）已验证正确，本报告不改动其算法结果**，仅就其外围工程层、错误处理、语法现代化给出结论与改法。

---

## 一、结论速览

| 维度 | 结论 |
|---|---|
| **解析正确性** | ✅ 核心算法 8/8 正确（上一轮已逐行核对哨兵位剥离） |
| **现代语法应用度** | ⚠️ **普遍偏低**：Node 5/100、PHP 15/100、Go 0%（1.21+ 特性全未用）、C 未用 C11/C17、Rust 中等、Python 偏低、Java/C# 较好 |
| **安全/致命缺陷** | 🔴 发现 **10+ 项 P0**，其中 4 项已用真实数据复现为**致命故障**（PHP reload Fatal、GeoInfo 缓存投毒、批量三态违规、PHP u64 偏移绕过校验） |
| **跨语言一致性** | ⚠️ 多项 P1 偏差：浮点格式（C# 整数带 `.000000`）、`findStr` 契约（Java 抛异常）、`to_json` 可产非法 JSON |
| **是否可发布** | ⚠️ **不建议在原状态直接发布**。核心解析 OK，但外围有已复现的致命缺陷，应至少先修 P0 + 关键 P1 再发 |

---

## 二、各语言"最新语法应用度"评分

| 语言 | 运行时 | 现代特性应用度 | 关键缺失 |
|---|---|---|---|
| **C** | C11/C17 | 低 | 无 `static_assert` / `_Generic` / `designated initializer` / `restrict`；头文件无 `extern "C"` 守卫 |
| **Go** | 1.24（go.mod 1.21） | 0% | 泛型 / `slices`/`maps` / `min`/`max`/`clear` / `errors.Join`+`%w` / `range over int` / `log/slog` 全未用；`gofmt` 11 文件未格式化 |
| **Rust** | 1.96（edition 2021） | 中等 | `OnceLock` 已用；未用 `let-else` / `Cow` / `const fn` CRC 表 / `pub(crate)` / `#![forbid(unsafe_code)]`；36 处 `.unwrap()` |
| **Python** | 3.13 | 偏低 | `match`/`@dataclass`/`pathlib`/`typing` 使用次数≈0；仅 4 处类型注解；`from __future__ import annotations` 未加 |
| **Node** | 22 | **5/100** | 停留在 ES2015；`#private` / `?.` / `??` / `static{}` / `Symbol.dispose` / `DataView` / `Object.hasOwn` 全未用；无 ESM 包装、无 `.d.ts` |
| **PHP** | 8.5（实机） | **15/100** | 写法≈PHP 7.0；无 `declare(strict_types)` / 属性类型 / `enum` / `readonly` / `match` / `?->` / 构造器提升 |
| **Java** | 21 | 较好 | `record`/`sealed`/`switch` 表达式/`Optional`/`try-with-resources` 已用；缺 `var`/`text blocks` |
| **C#** | 8/9/10 | 较好 | `readonly record struct`/collection expr/文件作用域 namespace/`Span`/`Unsafe`/`ArgumentNullException.ThrowIfNull` 已用；可增强 `file`/`scoped`/`required`/`string.Create` |

**一句话**：除 Java/C# 外，其余 6 语言基本未用上各自 2021 年后的现代语法/标准库能力。

---

## 三、P0 — 安全 / 致命缺陷（发布前必须修）

> 标 🔴 者为已用真实数据复现的致命故障。

### 解析/加载层（Fail-Closed 失效）
1. 🔴 **[PHP] u64 偏移量符号溢出绕过全部 section 边界校验** — `QzdbReader.php:1916-1929` + `:1131-1145`：`unpack('P')` 对 ≥2⁶³ 返负数，`> 0` 判据为 false → 整条校验被跳过，损坏文件被判定"加载成功"。对照 Node（用 `Number` 大正数）能拦住。**改法**：加统一 `assertOffset(off, what)` 在 `parseHeader` 后立刻校验。
2. 🔴 **[Node] 池解析用文件内声明的 count 直接开数组** — `qzdb.js:895-916`：`count` 上限 6700 万，`new Array(count)` 可打爆内存（CRC 可被伪造、`verifyCrc:false` 文档支持）。PHP 侧用描述符+惰性取串天然免疫。**改法**：循环前做乘法校验 + 用 `Uint32Array`。
3. **[Node] `offGeoEntries` 是唯一漏掉边界校验的 section** — `qzdb.js:656-661`：越界抛原生 `RangeError` 而非 `QzdbError`（破坏契约 §7）。PHP `:1152-1158` 同构漏校验。**改法**：两语言都补 section 长度校验 + `actualGroups < 1` 显式拒绝。
4. **[Go] `safeReadU16/24/32/48/64` 名不副实，畸形 DB 可 panic** — `qzdb.go:117-129`：直接 `b[off:]`/索引无边界检查，损坏输入触发运行时 panic（C 端同样命名的函数有校验）。**改法**：改带 `(val, ok)` 返回，调用链统一 Fail-Closed。
5. **[Rust] 解析期 6+ 处 panic 向量（畸形文件 DoS）** — `lib.rs`：`check_offset` 申报长度远小于实际读取量（ROW_SCHEMA 校验 1 字节却读 2；组表校验 16 字节却读 28；GROUP_SCHEMA 校验 2 字节却按 `fld_count` 循环）；36 处 `.unwrap()`。**改法**：修正 `check_offset` 真实长度 + 解析路径 `.ok_or_else(ErrorCode::OutOfBounds)?`。
6. **[Rust] `safe_read_*` 整数溢出可退化 panic** — `lib.rs:124-169`：`off + 2 > d.len()` 在 `off≈usize::MAX` 回绕。**改法**：`d.get(off..off.checked_add(2)?)?` + `from_le_bytes`。
7. **[Java] 基础读取函数无内部边界校验** — `QzdbReader.java:1679-1723`：直接 `ByteBuffer.get*`，损坏文件抛原始 `IndexOutOfBoundsException` 逃逸。**改法**：各 `readX` 内收口校验转 `QzdbException`。
8. **[C#] `ReadUintWidth` 越界抛原始 CLR 异常** — `QzdbReader.cs:1065-1072`：入口无 `off+width <= s.Length` 校验。**改法**：入口统一校验转 `QzdbException`。

### 运行时契约 / 状态层
9. 🔴 **[Node+PHP] 批量路径把"非法 IP"并入"未命中"，违反契约 §4** — `qzdb.js:1223-1234` / `QzdbReader.php:899-911`：`find()` 对非法 IP 返回 null 不抛，`catch` 永不命中；且 `nodejs/test_suite.js:544` 把违规固化成期望。GeoInfo 三态（命中/未命中/参数错）被破坏。**改法**：批量入口先 `fastParseIp` 判定，非法即填 `BatchResult.Error`；更正测试断言。
10. 🔴 **[Node+PHP] GeoInfo 可变 + 缓存复用 → 一次误写永久污染整个快照** — `qzdb.js:127-132`（字段挂可写属性）+ `:1038-1056`（缓存返同引用）；PHP `:306-309` 有 `__get` 无 `__set`（动态属性在 PHP 9 将致命，且 `$info->country` 与 `get('country')` 结果打架）。**改法**：`Object.freeze(this)` + 冻结 `_vals/_fieldNames`；PHP 补 `__set`/`__unset` 抛异常或改 `readonly class`。
11. 🔴 **[PHP] 流式 `reload()` 关掉新快照的文件句柄，reader 报废 + 旧句柄泄漏** — `QzdbReader.php:755-765` + `:643-658`：`assign()` 把 `$src->stream` 复制给 `$this` 后 `$src` 析构 `fclose` 同一句柄 → 后续查询 `fseek()` Fatal；且旧句柄永不关。**改法**：所有权转移（`$src->stream=null;$src->closed=true`）+ 回收旧句柄；删除 `get_object_vars` 跳过静态属性的死代码。

---

## 四、P1 — 逻辑 / 一致性缺陷（应修）

1. **[跨语言] 浮点字段格式不一致** — C# `QzdbReader.cs:982-984,1043-1045` 用 `F6` 使整数输出 `"1.000000"`，而 Java（`QzdbReader.java:651`）、Go（`qzdb.go:916-924`）整数无小数点、NaN/Inf 返回空。破坏 `toPipe`/`toJson` 跨语言一致性。**改法**：抽 `FormatFloat6`（整数→无小数点、小数→F6、NaN/Inf→""）替换 C# 两处。
2. **[跨语言] `findStr` 非法 IP 行为不一致** — Java `QzdbReader.java:876-879` 抛异常，C# `QzdbReader.cs:677-689` 返回 ""；规范 §3 要求返回 ""。**改法**：Java `findStr` 包 `try/catch(QzdbException) return ""`。
3. **[Python] IPv6 校验绕过（真实 bug）** — `qzdb.py:139-146` 用 `int(g,16)` 校验却用 `_HEX` 表取值，非法输入 `+`/`-`/`_`/`0x`/空白被静默接受并塌缩为同一错误地址；Rust 侧正确拒绝。实测 `2001:+1::1` 等 4 例全部被接受。**改法**：删 `int()` 校验，改逐字符白名单（与 Rust 对齐）+ 补 golden 用例。
4. **[Python] `safe_read_*` 完全无边界检查，命名误导** — `qzdb.py:825-855`：越界抛 `IndexError`/`struct.error` 而非 `QzdbError`，穿透 `find_batch` 崩溃。
5. **[Python] `lookup_cidr` 缺 `_has_v4/_has_v6` 守卫** — `qzdb.py:1820-1836`：纯 IPv6 库会从文件头偏移 0 当跳表指针（Rust 两入口都有守卫）。
6. **[Python] `to_json()` 数值精度丢失 + 产非法 JSON** — `qzdb.py:428-437`：`float()` 使 `9007199254740993`→`...992`；`allow_nan` 默认产出 `Infinity`（非法 JSON）。
7. **[Python] `GeoInfo.__getattr__` 无限递归** — `qzdb.py:223-227`：未初始化时访问属性 → RecursionError；`copy`/`pickle` 直接崩。
8. **[Python] `verify_crc()` 全量拷贝整个文件** — `qzdb.py:1991-1998`：注释写"无拷贝"却 `d[20:]` 物化整个 mmap 副本（每次 load 500MB 库 → 500MB 瞬时峰值）。**改法**：用 `memoryview` 零拷贝（用完 `release()`）。
9. **[Python] `pyproject` 声称支持 3.8，但 `str | None` 注解需 3.10+** — `qzdb.py:197` vs `pyproject.toml:12` 矛盾。**改法**：加 `from __future__ import annotations` 或提 `requires-python>=3.10`。
10. **[PHP] `find()` 拒绝空白，`lookupCidr()` 却 `trim()` 接受** — `:955` vs `:1735`：同一对象两 public 方法对同一输入给相反合法性判定（校验旁路，SSRF 风险面）。
11. **[PHP] `unpack('f'/'d')` 是机器字节序非小端** — `:1971/:1986`：应改 `'g'`（LE float）/`'e'`（LE double）；整数已正确用 `v/V/P`。
12. **[PHP] `findFields` 对未知字段返回非空 GeoInfo（Node 返回 null）** — `:836-840`：调用方会误以为命中。
13. **[Node] `findFields` groupIndex 越界抛 TypeError（find 返回 null）** — `:1184-1195`：缺 `_resolveGeo` 那套守卫。
14. **[Node] `loadBuffer` `Buffer.from(ArrayBuffer)` 不拷贝 → 快照可被外部改写** — `:509`：违反契约 §9 不可变快照；且该行三元两分支完全相同（死代码）。**改法**：强制深拷贝。
15. **[Node] `getFileHash()` close 后返回空 buffer 的 CRC** — `:1510`：应判 `_closed`/`length<20` 返回 `''` 并缓存。
16. **[C] `format_v4_cidr` 32 位移位是 UB** — `qzdb_reader.c:887-890`：`n==0` 时 `uint32<<32` 是 UB，靠特判规避（脆弱）。**改法**：`(n==0)?0u:(0xFFFFFFFFu<<(32u-(unsigned)n))`。
17. **[C] `qzdb_reload` 整体 `memcpy` 含 `pthread_mutex_t` 结构体** — `qzdb_reader.c:1616-1639`：复制互斥锁是 POSIX UB，与时序重叠的读者产生数据竞争/死锁。Go 端用 `atomic.Pointer[Snapshot]` 才是正确的无锁切换。**改法**：指针间接 + 原子发布（RCU）。
18. **[C] 头文件缺 `extern "C"` 守卫** — `qzdb_reader.h` 全文：C++ 工程无法链接。
19. **[Java/C#] `ChainedReader` 释放语义不对称** — C# `ChainedReader.cs:131-135` `Dispose` 仅 `GC.KeepAlive` 不释放内部 reader；Java `ChainedReader.java` 未实现 `AutoCloseable`。**改法**：明确所有权，拥有则真正释放，否则移除 `IDisposable` 并注记。

---

## 五、P2 — 现代化 / 优雅性（可渐进，不阻塞发布）

**C**：`static_assert` 编译期锁死布局；`bool` 替代 int 标志；`restrict` 热路径；`snprintf` 统一替代 `strncpy`；`-Wconversion` 24 处显式转换。
**Go**：`gofmt -w`；`errors.Join`+`%w`+`errors.Is`；`slices`/`maps`/`min`/`max`；`sync.Once` 替代双重检查锁；清理 `var _ = errors.New` 占位。
**Rust**：`#![forbid(unsafe_code)]` + clippy 门禁；`let-else`/`Cow<str>` 消除 `normalize_key` 分配；`const fn` CRC 表；`pub(crate)` 收窄；`Arc<GeoInfo>` 消除深拷贝；删除不必要的 `Drop`；`arc-swap` 真无锁缓存替代 16384 个 `Mutex`。
**Python**：`from __future__ import annotations` + 补全类型注解；`BatchResult`/`RowIds` 改 `@dataclass(slots=True)`；`match`/`case` 替代 mask 分派；`pathlib` 替代 `os.path`；合并 10 份重复 trie 循环；删除 `_float_indices`/`SENTINEL_MASK_24` 死代码；`to_pipe`/`to_json` 简化。
**Node**：`#private` 字段；`?.`/`??`；`Object.hasOwn`；`Symbol.dispose`(`using`)；补 `package.json` 的 `exports`/`types`/`.d.ts`/ESM 包装；`TypedArray` 替代普通数组；提取重复的 trie/v6 派发函数。
**PHP**：`declare(strict_types=1)`；属性类型声明；`enum` 重写 `UsageType`（21-case switch → backed enum）；`readonly class`/`final`；`match` 替代 if-elseif；构造器属性提升。
**Java**：引入 `var`（解析路径可读性）；可选 text blocks。
**C#**：`file` 修饰符；`scoped` span 参数；`required` 成员推广；`string.Create`；`GeoInfo` 索引器 `this[string]`。

---

## 六、修复优先级与路线图

### 发布前必做（P0 + 关键 P1，约 15-20 项）
- 所有 P0（§三）— 尤其已复现的 4 项致命故障。
- P1 跨语言一致性：浮点 `FormatFloat6`、Java `findStr` 契约、批量三态、GeoInfo 不可变。
- 各语言边界校验统一收口（Java/C#/Go/Rust/Python/PHP 的 `safeRead*`）。
- 跨语言 `to_json` 数字文法统一（JSON 合法数字正则）。

### 发布后渐进（P2）
- 现代语法迁移（按上节逐语言清单）。
- `gofmt`、clippy、`declare(strict_types)`、`.d.ts` 等工程化。
- 测试增强：pytest 化、补 IPv6 边界 / 畸形文件 fuzz / 并发测试（Rust `edge_cases.rs` 当前零覆盖）。

---

## 七、重要说明
- 本报告的修复**均不触及核心 trie/sentinel 解析算法**（上一轮已验证正确且为发布前提）。
- 现代语法迁移（P2）是"优雅性/可维护性"收益，不改变外部行为；但每改一处都应跑对应语言测试（Python 有 Tier1/Golden 可立即回归）后再提交。
- 建议将修复拆成"安全/契约"（先发）与"现代化"（后续 PR）两批，避免一次大改引入回归风险。

---

*详细逐行证据与各语言完整清单见本轮 4 份子报告（C+Go / Rust+Python / Java+C# / Node+PHP），本文件为其综合索引。*
