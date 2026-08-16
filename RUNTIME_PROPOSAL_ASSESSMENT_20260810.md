# Runtime 优化提案 · 落地评估（只读审查）

日期：2026-08-10｜性质：**纯评估，未改动任何代码**
评估对象：一份 82 节 / 75 条的「QZDB Runtime Optimization Specification」
评估基线：当前工作区 8 语言 SDK 实况（含 `multi-lang/API_CONTRACT.md` v2.4）

---

## 0. 一句话结论

提案里 **约 60% 的条目本项目已经实现了**，剩下的里面 **真正值得做的只有 4 条**，
而提案 **完全没提、但本项目最缺的那一条（统一基准口径）才是唯一的 P0**。

提案最大的盲点：它是一份「.NET 单语言优化方案」，
而本项目的核心资产是 **8 语言逐字对称的行为契约**。
任何新增 API（FieldId / RawResult / Projection 枚举 / Span 返回值）
都必须回答「其余 7 种语言怎么办」——否则 `API_CONTRACT.md` 当场失效。

---

## 1. 项目里已经沉淀的「精华」（提案没看到的部分）

这些是本仓库真正稀缺、值得向外复用的工程资产，按价值排序。

### 1.1 跨语言行为契约作为 Single Source of Truth ★★★★★

`multi-lang/API_CONTRACT.md`。8 语言 SDK 项目最常见的死法是「每种语言各自演化」，
这份文档用契约把它钉死了，而且处理了契约与实现冲突时的仲裁规则
（§4 口径修订：文档是 SSoT，但**过时的文字以实现为准并立即修订**，不许改文档迁就 bug）。

其中 §8「正确性强制规则」7 条是血泪条款，每条背后都是一次真实事故：

| 条款 | 背后的事故 |
|------|-----------|
| SENTINEL 高位剥离 | `find_fields` 曾全库返回 None |
| 浮点必须 6 位小数（禁 `%g`/`str(float)`） | 跨语言 pipe 串失配 |
| `to_pipe()` 逐字拼接、禁止重新解析 | `116.400000` 被打回 `116.4` |
| 缺失数值字段严禁哨兵 `0` | `0` 是合法业务值，用它表示缺失 → 字段级比对失配 |

### 1.2 golden 裁判制 ★★★★★

`multi-lang/tools/golden_vectors.json`（370 KB，`std_china` + `ult_china` 两组，4102 向量），
配 8 语言统一 runner CLI 协议，逐字节 0 偏差。提案第 56 条「Cross-language Golden Test」
在这里**已经是既成事实**，而且做得比提案描述的更严（提案只列了几个样例 IP）。

### 1.3 Fail-Closed 不变量族 ★★★★★

- POOLS 偏移表单调性校验（该事故 **8 语言全中招**，1 字节头篡改 → 7.2 GB OOM）
- 双字段数源对账：`groupFieldCounts[g]` vs `fld_count`
- 条目表延展对账：加载期把 `entry_count` 收敛到文件放得下的范围，热路径因此**无需逐次判边界**
- section 边界一律用溢出安全式 `required > dataLen - offset`
- trie walk 全部是**有界 for 循环**（`depth < 32` / `depth < 128`），不存在 `while(true)`
  → 提案第 55 条「Trie 最大深度保护」**已天然满足**，无需新增

### 1.4 C 缓存的「不可变条目 + CAS 发布 + 永不淘汰」 ★★★★★

`PATCH_REVIEW_20260810.md` 记录的判断过程比结论更值钱：
条带锁方案被 ASan 实证否掉，因为 `resolve_row_id_cached()` 返回**借用指针且不置 `values_mask`**
——这是生命周期问题，不是竞态问题，**加锁解决不了**。
结论：C 语言侧缓存加任何淘汰路径都是 UAF。实测 8 线程 7.02M → 153.70M QPS（21.9×）。

这条同时也是对提案第 22/23/63 条的**反例**（详见 §4）。

### 1.5 不可变快照 + 原子替换（全语言已落地） ★★★★

| 语言 | 机制 | 证据 |
|------|------|------|
| C# | `Volatile.Read/Write` + `Interlocked.Exchange` | `QzdbReader.cs:14,157` |
| Rust | `ArcSwap::from_pointee` | `lib.rs:2130` |
| Go | `atomic.Pointer[Snapshot]` + **refCount 防 munmap 竞态** | `qzdb.go:106,181,1118` |
| Java | volatile snapshot + `final groupIndex` | `QzdbReader.java:27` |

Go 的 `refCount` 正是提案第 34 条要求的「reader 全部离开才释放 mmap」，**已经做完了**。

### 1.6 解码缓存键选得比提案好 ★★★★

现有缓存键是 **`entryId`**（C# 16K 槽 / Go 256K / Node 64K / PHP 64K / Rust 16K / C 16K）。
提案第 21 条建议改成「IP hash → EntryId」。**这是两个不同的层，不是替代关系**：

```
IP ──[trie walk]──> rowId ──[row 解析]──> entryId ──[decode]──> GeoInfo
     提案想缓存这段                              现有缓存的是这段
     （便宜：jump table + patricia）              （昂贵：多字段池读取 + 字符串构造）
```

按提案改是**性能倒退**——把缓存从重活挪到轻活上，而且 IP 基数远大于 entry 基数，命中率会崩。

### 1.7 自描述格式的版本判定链 ★★★★

`VersionMask`(Header@6, one-hot) → Metadata → 字段数唯一匹配 → `""`，
配 `EditionSource*` 四种溯源标记（`version_mask`/`metadata`/`inferred`/`unknown`），
8 语言用**同样的字符串常量**，跨语言比对可直接比。
新增一个档位 = 追加一个 bit + 一行字段名表，**零解析器改动**（`QzdbReader.cs:24-44`）。

### 1.8 「看起来在测、其实不会失败」的坑清单 ★★★★

`RELEASE_READINESS_REPORT.md` §3.7/§3.8 + 项目记忆里的三条，是极稀有的元知识：

1. **C 是唯一手动在 `main()` 注册测试的语言** → 靠 `-Wall -Wextra` 的 unused function 检出休眠测试
2. `rust/examples/failclosed.rs` 只打印 `[PANIC!!]`，**退出码恒 0**，CI 永远绿
3. `rustfmt.toml` 曾写 `indent_style="Spaces"`（合法值仅 `Block`/`Visual`）→ `cargo fmt` 解析失败**却仍 exit 0**，长期静默不生效
4. `.gitignore` 的 `bin/` 规则吞掉 `rust/src/bin/` 全部 `[[bin]]` 源文件 → 工作区能编译、fresh clone 硬失败

由此固化出的**铁律**：发布前必做纯快照构建验证
（`git write-tree` + `git archive` 解到临时目录，在快照里逐语言构建 + 与 Python 基准 `cmp`）。

---

## 2. 提案条目 × 实况对照表

### 2.1 已实现（提案里其实是「已完成项」，占比最大）

| 提案条 | 内容 | 实况 |
|-------|------|------|
| 2 | Snapshot 抽象 | ✅ 8 语言全有 |
| 15 | Projection | ✅ `findFields(ip, fields)`，**契约强制全语言提供** |
| 18/45 | `TryFindIPv4(uint)` | ✅ `findUint` / `lookupRowIdUint` |
| 19/46 | IPv6 16-byte API | ✅ `findBytes` / `lookupRowIdBytes`，且不强制过 `IPAddress` |
| 19/47 | Batch API | ✅ `findBatch` / `findBatchFields` / `findStream`（内存恒定） |
| 21/23 | EntryId 缓存 + 直接映射 | ✅ 且键选得比提案好（见 §1.6） |
| 26 | 边界检查前移到加载期 | ✅ 条目表延展对账已做，热路径不逐次判边界 |
| 28/29/64 | CRC 只在 open + `verifyCrc` 开关 | ✅ Builder `.verifyCrc(bool)` |
| 32/33 | Atomic swap / lock-free reload | ✅ 见 §1.5；且新快照 CRC 失败时**旧快照继续服务** |
| 34 | Snapshot 生命周期 | ✅ Go refCount / Rust Arc / C 永不淘汰 |
| 55 | Trie 最大深度保护 | ✅ 天然满足（有界 for 循环） |
| 56 | Cross-language Golden | ✅ 4102 向量 |
| 57 | API Contract Test | ✅ `API_CONTRACT.md` + Tier1（≥50 断言） |
| 58/60 | Diagnostics / Dataset Metadata | ✅ `getVersion`/`getEdition`/`getFileHash`/`getPoolCount`/`getBuildTime`… |
| 70 | Endianness 显式 LE | ✅ `BinaryPrimitives.ReadUInt32LittleEndian` |
| 74 | 异常不作正常流 | ✅ `TryFind` / `findStr` 返回 `""` |

### 2.2 部分实现 / 语言间不对等

| 提案条 | 内容 | 缺口 |
|-------|------|------|
| 3 | mmap | ✅ C / Go / Java / Python；❌ **C# / Rust / Node / PHP** |
| 54 | Fuzz | 部分：`c/failclosed.c`（4 类变异 3252 用例）、`c/fuzz/boundary_test.c`、`rust/tests/failclosed.rs`（7 项）；**其余 6 语言无** |

### 2.3 真缺口（值得吸收）

见 §3。

---

## 3. 真正值得吸收的 4 条（按 ROI 排序）

### P0 — 统一基准口径（提案第 50/51/52 条，但项目现状比提案想的更糟）

**这是唯一无争议的 P0，也是其余一切优化的前提。**

现状极度不对称：

| 语言 | bench 行数 | 指标覆盖 |
|------|-----------|---------|
| **Java** `DualStackBenchmark.java` | **390** | QPS + **P50/P99** + 4/8/16 线程 + 冷热对比 + JSON 报告 |
| C `bench_qps.c` | 76 | 裸 QPS |
| Go `cmd/bench` | 55 | 裸 QPS |
| PHP `bench_all.php` | 53 | 裸 QPS |
| Python `bench_qps.py` | 51 | 裸 QPS（仅有 random 分布） |
| Rust `bench_qps.rs` | 45 | 裸 QPS |
| Node `bench_all.js` | 37 | 裸 QPS |

**建议**：把 Java 的 `DualStackBenchmark` 提升为 `BENCH_CONTRACT.md`，
像 `API_CONTRACT.md` 约束行为一样约束基准，8 语言统一：

1. 统一指标：QPS / P50 / P95 / P99 / 分配量 / 线程扩展（1→2→4→8→16→32）
2. **统一四种 IP 分布：random / hot / sequential / real-world**
   —— 这条对本项目尤其关键：`entryId` 缓存对分布**极度敏感**，
   现在各语言用随机 IP，会**系统性低估**缓存收益；用纯热点又会高估。
   没有这四态，任何"优化了 X%"的结论都不可证伪。
3. 统一 JSON 输出 schema → 才能做基线对比

### P0 — 性能回归 CI（提案第 53 条）

`.github/workflows/verify.yml` 现在只跑 L1–L4 **正确性**，无任何性能门禁。
上一条做完后即可挂上去。注意 CI 上还有个已知坑：`Install formatters` 那步用了
`|| true` 吞掉 pre-commit 失败（`verify.yml:123`），性能门禁不要重蹈覆辙。

### P1 — Rust 解码缓存去 Mutex（提案第 22/23 条的精神）

**唯一一个热路径还在加锁的语言。**

```rust
// rust/src/lib.rs:762
geo_cache: Vec<std::sync::Mutex<CacheSlot>>,   // 16384 个 Mutex
// :1516 注释写的是「有界无锁缓存解析」——注释与实现不符
```

`resolve_geo()`（`:1516-1533`）命中路径要 `slot.lock().unwrap()`，
未命中路径再 lock 一次写回。Rust 有 `Arc`，**不受 C 的 UAF 约束**（铁律 3 只管 C/C++），
完全可以换成 `ArcSwapOption<GeoInfo>` 之类的原子指针，对标 C 侧 21.9× 的量级。

**但必须先有 P0 的 bench 才能证明**，否则无从判断无竞争 Mutex 的实际开销。
顺带：那句「有界无锁」的注释无论改不改代码都该修正。

### P1 — C# / Rust 的加载期内存

两处独立问题，`data/` 里最大的库 **`qqzeng_ip_ult_global.qzdb` = 122 MB**，全套 516 MB，量级不可忽略。

**C#（提案第 3 条命中）**：`Snapshot.FromPath`（`QzdbReader.cs:251-260`）
`GC.AllocateUninitializedArray<byte>(len)` + `ReadExactly` —— 整个 122 MB 进托管堆 LOH，
且多进程无法共享物理页。Go / Java / Python / C 都已 mmap，C# 是明确的短板。

**Rust（提案没提，但更容易修）**：
```rust
// lib.rs:2120
pub fn from_file(path: &str) -> ... {
    let bytes = fs::read(path)?;          // 第 1 份：122 MB
    Self::from_bytes(&bytes, 0, true)     // 内部 bytes.to_vec() → 第 2 份：再 122 MB
}
```
`from_bytes` 第 2127 行 `Arc::new(bytes.to_vec())` 造成**加载峰值 2× 文件大小**。
给 `from_file` 走一条直接 `Arc::new(bytes)` 的路径即可消除，改动量极小、无契约影响。

### P2 — 原生数值直读旁路（提案第 12 条，证据确凿但要打折）

往返是真实存在的：

```
文件里的 float32
  → 解码时格式化成 "116.400000"（API_CONTRACT §8.2 强制，to_pipe 契约依赖它）
  → GeoInfo.GetLongitude() 再 double.TryParse 解析回来   ← GeoInfo.cs:252-257
```

`GetGeoId` / `GetAsn` / `GetLatitude` 同理（`GeoInfo.cs:245-275`）。

**但不能按提案「替换」，只能「并存」**：字符串形态是 pipe 契约的硬要求，删不得。
可行做法是解码时同时保留 native 值。代价：`GeoInfo` 变大 → **每 snapshot 的缓存内存翻倍**
（C# 16K 槽、Go 256K 槽）。**先用 P0 的 bench 测出这几个 getter 的实际调用占比再决定。**

---

## 4. 提案里在本项目语境下「错的 / 必须打折」的条目

| 提案条 | 问题 |
|-------|------|
| **21** 「缓存 EntryId 而非 GeoInfo」 | **方向性错误**。见 §1.6：会把缓存从重活（decode）挪到轻活（trie walk），是性能倒退 |
| **22** 「L0 ThreadLocal 两级缓存」 | 与铁律 3 冲突。C 侧缓存返回**借用指针**，加 ThreadLocal 层重新引入生命周期问题；而 entryId 缓存命中本就只是一次数组索引，L0 省不下什么 |
| **26** 「查询期不再做边界检查」 | **零收益、高风险，且已实测过**。Rust 侧结论：热路径偏移全程 `saturating` 编译成 cmov，**实测零 QPS 成本**。拿安全换 0% 性能没有意义 |
| **63** 「PerformanceMode.Ultra / No cache validation」 | 直接制造数据错误。`cached.Key == entryId` 这个校验是「碰撞只重算、绝不返回错值」的**唯一**保障，去掉就是返回别人的地理位置 |
| **75** 「拆成 Qzdb.Core / Qzdb / Qzdb.AspNetCore 三包」 | `PackageId=QQZeng.Qzdb` 已发布到 NuGet（v1.0.4），拆核心包是破坏性变更；且 8 语言只有 C# 这么拆会破坏对称性。**只加 `QQZeng.Qzdb.AspNetCore` 附加包，不拆核心** |
| **8/9/10/13/16** RawResult / FieldId / UTF-8 Span 一族 | 本项目 ROI 最低：结果已被 entryId 缓存兜住，命中即数组索引，RawResult 只能优化未命中的那部分；而要保持契约对称就得 8 语言同步（Span 语义在 PHP/Python/Node 根本不存在）。**门槛：先测出 `BuildGeo` 在总耗时占比，<15% 就不做** |
| **41/42** NativeAOT / Trim | 方向对。注意现在 `AllowUnsafeBlocks=true`，AOT 前要先确认 unsafe 边界是否集中（提案第 69 条） |
| **43** 固定 `LangVersion` | 有效缺陷，确认命中：`QQZeng.Qzdb.csproj:11` 是 `<LangVersion>latest</LangVersion>`，与同文件已有的 `<Deterministic>true</Deterministic>` 自相矛盾。但改时要兼顾 `net8.0;net9.0;net10.0` 三目标 |
| **30/31** CRC32C / SHA256 分职 | 会改变 `getFileHash()` 的返回口径（现为 CRC32 八位小写十六进制，写在契约 §5 里）。**属于契约变更，不是「不改格式」的无痛项** |

---

## 5. 建议的最小可执行路径

```
第 1 步（唯一 P0，无争议）
  BENCH_CONTRACT.md：把 Java DualStackBenchmark 的口径抽成 8 语言统一规范
  + 四种 IP 分布 + 统一 JSON 输出
        │
        ▼
第 2 步（拿数据当决策门，只有测出来的瓶颈才准进入优化）
  ├── Rust 去 Mutex        ← 若 16 线程下同槽争用可见
  ├── C# mmap / Rust 双拷贝 ← 若 122 MB 库的加载峰值可见
  └── native numeric 旁路   ← 若 getter 占比 > 15%
        │
        ▼
第 3 步
  性能回归基线挂进 verify.yml（别用 `|| true`）
        │
        ▼
（暂不做）RawResult / FieldId / UTF-8 Span / SIMD / Prefetch / Source Generator
```

**核心判断**：提案说「不要动 `.qzdb`」是对的，但它给出的理由不完整。
真正不该动的不止是**数据格式**，还有 **`API_CONTRACT.md` 这份 8 语言行为契约**——
它才是这个项目最难重建的资产。格式冻结靠自律，契约冻结靠 4102 条 golden 向量，
而**性能目前没有任何东西冻结它**。这就是为什么 P0 是基准口径，而不是 mmap。
