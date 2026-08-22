# QZDB SDK Roadmap

> 本文件是唯一的任务板（取代已删除的 `multi-lang/todo.md`——该文件为一篇过时的外部咨询稿，
> 其 P0 项经 2026-08-22 全量核对约 80% 已实现，全文已弃用）。
>
> **维护规则**
> 1. 任务必须带「证据」（现状核对）、「验收标准」、「验证命令」三要素，缺一不入表。
> 2. 动手前先做现状核对：任何外部建议一律以代码为准，不照单全收。
> 3. API/格式变更走 create-migration 流程：docs 先行 → 8 语言同步 → `cd multi-lang && ./run_all.sh`。

---

## 现状基线（2026-08-22 全量核对）

| 能力 | 状态 | 证据 |
|---|---|---|
| Batch API ×8 语言 | ✅ 完成 | C `qzdb_find_batch` / Rust `find_batch` / Go `batch.go` / C# `LookupBatch` / Java `findBatch` / JS `findBatch` / Py `find_batch` / PHP `findBatch` |
| 字段投影 `find_fields` ×8 语言 | ✅ 完成（原稿列为 P1"Field Mask"，实际早已落地） | Go `qzdb.go:1249`、C# `QzdbReader.cs:1189` 等 |
| Scalar/lazy 查询 `lookup_row_id` / `lookup_ids` / `lookup_cidr` ×8 语言 | ✅ 完成 | 各语言 Reader；Rust 返回借用视图 `&str`（零拷贝语义已在位） |
| Golden 向量跨语言测试 | ⚠️ 覆盖不全 | Runner 已有：C `golden_check.c`、Rust `tests/golden.rs`、Go `golden_test.go`、Py `test_golden.py`、PHP `tier2_golden.php`；向量源 `tools/golden_vectors.json`。**Java 无独立 runner、.NET 未消费该向量文件** |
| Fail-closed / 恶意文件测试 | ⚠️ 分散、无单一事实源 | 已有：C `fuzz/boundary_test.c`、Rust `tests/{failclosed,edge_cases,tier1}.rs`、Go `tier1_test.go`、C# `Tier1.cs`、Py `test_tier1.py`、PHP `tier2_golden.php`('invalid')。**Java 无独立套件** |
| Benchmark 契约 | ✅ 完成（原稿建议已被超越） | `docs/BENCH_CONTRACT.md`：4 分布 × 双栈三元模式 × p50/p99 指标 × JSON schema × CI 回归门禁；实现 `bench_contract.{py,js,php}`、`bench_qps.{c,rs}` |
| Trie walk 终止保护 | ⚠️ 策略不统一 | Node.js 硬编码 `MAX_TRIE_WALK_STEPS = 1000`（qzdb.js:24）；其余语言为逐步 bounds-check + 结构校验，无统一推导式 |

---

## 开放任务

### T1 · Java 补齐 Tier-1 fail-closed 套件 【P0】✅ 2026-08-22

- **现状**：`multi-lang/java/src/test/java/com/qqzeng/qzdb/` 仅 `QzdbReaderTest` / `DualStackBenchmark` / `FullAccuracyAndPerfTester`，无恶意文件用例。8 语言中唯一缺口。
- **做法**：断言集对标 Rust `tests/failclosed.rs` 与 Go `tier1_test.go`——截断 header、offset 越界、CRC 不匹配、Trie node 自环、Pool offset 溢出等 hostile 输入，一律抛异常或返回空结果，**禁止返回看似合法的错误数据**。
- **验收**：≥10 个 hostile case 全部 fail-closed；`mvn test` 通过。
- **验证**：`cd multi-lang/java && mvn -q test`
- **完成**：新增 `FailClosedHostileTest.java`（自研零依赖 JSON 解析 + 10 种变异 op 引擎），消费 T3 向量文件 29/29 全部 fail-closed（含 CRC 有效重算的查询期行级攻击）；已接入 `run_all_tests.sh` 为 `Java-FailClosed` 门禁项。已知差异：Java 查询期对越界 entryId 选择优雅空而非抛异常（fail-closed 成立，记录于套件 DIVERGENT 报告）。

### T2 · .NET 接入 golden_vectors.json 【P0】✅ 2026-08-22

- **现状**：`multi-lang/netcore.Tests/` 只有 `Tier1.cs`，未消费共享向量源；跨语言一致性矩阵在 .NET 上断链。
- **做法**：新增 golden 测试类，读取 `tools/golden_vectors.json`，逐条比对 Find 结果与 pipe 字符串（对齐 Python 基线行为）。
- **验收**：golden 全量 PASS；纳入 `run_all_tests.sh` 的 .NET 测试入口。
- **验证**：`cd multi-lang && ./run_all_tests.sh`
- **完成**：新增 `GoldenTests.cs`，4102/4102 通过（std+ult 双库 × random/boundary/invalid 五类），失败计入 `ALL TIERS PASSED` 门禁。

### T3 · Hostile 向量单一事实源 【P1】◐ 数据文件已建（2026-08-22），存量消费者迁移待做

- **现状**：6 个语言各自内联构造恶意输入，无共享文件；新语言接入时需重写一遍。
- **做法**：新建 `tools/hostile_vectors.json`（case id、篡改字段、期望失败模式），各语言 fail-closed 测试改为消费该文件；保留语言特有的内存安全用例（如 C/Rust 的 ASAN 场景）。
- **验收**：≥5 语言消费同一向量文件；`run_all.sh` L1 层通过。
- **依赖**：T1 完成后 Java 直接接入。
- **进展**：文件已产出（29 用例 × 10 类场景，配方经真实文件头核验）；Java 已作为首个消费者接入。剩余：C/Rust/Go/C#/Py 存量 hostile 测试迁移至同一向量源。

### T4 · Trie walk 终止保护策略统一 【P1】

- **现状**：Node.js 用 magic number 1000；其余语言靠每步 bounds-check。两种策略都安全，但无文档说明，且 magic number 无法表达"超过即文件异常"的语义。
- **做法**：统一为推导式上限 `max(IPBits + 8, 配置下限)`（IPv4≈40 / IPv6≈136），替换硬编码；若某语言保留现有机制，须在 `QZDB_SYNC_GUIDE.md` 记录理由。
- **验收**：8 语言 walk 上限来源可追溯（常量或推导式），SYNC_GUIDE 有对照表；正常数据全量查询无回归。
- **验证**：`cd multi-lang && ./run_all.sh`

### T6 · CI 编译门禁 【P0】✅ 2026-08-22

- **现状（已修复的缺陷）**：原 `verify.yml` 在无数据步骤硬性 `exit 1`，而 `.qzdb` 因安全规则不入库 → 托管 runner 上该工作流**从未可能通过**，形同虚设；`188ca29` 的坏构建因此在 main 上存活一周。
- **做法**：两层设计。Tier 1 `compile-gate` 永远执行——8 语言编译/语法门禁 + C ASan/UBSan 无数据 hostile 套件（`boundary_test.c` 文件头注释预定的用途），全部命令经本地实测；Tier 2 `full-verification` 仅当配置 `DATA_DOWNLOAD_TOKEN` secret 且存在私有 release（默认 tag `test-data`，含 .qzdb 资产）时拉取数据跑完整 L1-L4，否则优雅跳过。
- **验收**：新 workflow YAML 结构校验通过；gate 内全部命令在本地逐条实测绿；verify.yml 移除。
- **验证**：push 后观察 Actions 首跑；本地等价命令见 `.github/workflows/ci.yml` 各步骤。
- **完成**：`.github/workflows/ci.yml` 替代 verify.yml。Tier 2 待仓库管理员配置 secret + test-data release 后激活。

### T5 · Validation 分级演进 【P2·可选】

- **现状**：`init_ex(verify_crc)` 已提供开关雏形；Strict/Normal/Fast 三级仅在出现真实启动耗时痛点时才有价值。
- **触发条件**：Benchmark 显示大文件 Open() 耗时成为部署瓶颈时再立项；在此之前不做。

---

## 已否决项（勿再提议）

| 提议 | 否决理由 |
|---|---|
| Python 增加 C/Rust 原生扩展 (`qzdb_fast`) | 破坏本项目核心分发模式（用户拷贝单个 `.py` 文件即用）；数据分析场景由 `find_batch` / `find_stream` 承接 |
| 8 语言统一 `ReaderOptions(Mode×Cache×Validation)` 大一统矩阵 | 各语言运行时差异是特性不是缺陷（PHP-FPM 请求生命周期、Node Buffer、C mmap 各有最优解）；README 已按语言记录加载后端，保持原生 |
| 优先投入 SIMD (AVX2/NEON) | Trie walk 存在数据依赖链，不适合向量化；远期仅考虑 batch IP parse / prefetch 方向 |
| 为 SDK 性能修改 QZDB v1 格式 | 格式冻结；缓存属 runtime，不入文件 |

---

## 验证命令

```bash
cd multi-lang && ./run_all.sh        # L1 冒烟 / L2 跨语言 / L3 CSV 回归 / L4 深度准确率
```
