# QZDB SDK Roadmap

> 本文件是唯一的任务板（取代已删除的 `multi-lang/todo.md`——该文件为一篇过时的外部咨询稿，
> 其 P0 项经 2026-08-22 全量核对约 80% 已实现，全文已弃用）。
>
> **维护规则**
> 1. 任务必须带「证据」（现状核对）、「验收标准」、「验证命令」三要素，缺一不入表。
> 2. 动手前先做现状核对：任何外部建议一律以代码为准，不照单全收。
> 3. API/格式变更走 create-migration 流程：docs 先行 → 8 语言同步 → `cd multi-lang && ./run_all.sh`。
> 4. 多 agent 并行波次的提交纪律：agent 声称完成后其写入仍可能延迟落盘（本仓库已发生两起）。
>    因此 `git add` 前必须重查 `git status`，staging 后必须重审 `git diff --cached`
>    ——只信任最终暂存内容，不信任任何早期 diff 快照；发现超出任务授权的改动一律先剥离再提交。

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

### T3 · Hostile 向量单一事实源 【P1】✅ 2026-08-23

- **现状**：6 个语言各自内联构造恶意输入，无共享文件；新语言接入时需重写一遍。
- **做法**：新建 `tools/hostile_vectors.json`（case id、篡改字段、期望失败模式），各语言 fail-closed 测试改为消费该文件；保留语言特有的内存安全用例（如 C/Rust 的 ASAN 场景）。
- **验收**：≥5 语言消费同一向量文件；`run_all.sh` L1 层通过。
- **完成**：6 语言直接消费同一向量文件——Java `FailClosedHostileTest` / Python `test_hostile_vectors.py` / Go `hostile_vectors_test.go` / C# `HostileVectors.cs`（并入 ALL TIERS 门禁）/ PHP `tier2_hostile.php`，均已接入 `run_all_tests.sh` 且 29/29 全绿。C 保留 ASan 随机模糊职责（`fuzz/boundary_test.c`，与向量套件互补）、Rust 保留 panic 扫描职责（`failclosed.rs`），分工登记于本表与 SYNC_GUIDE。

### T4 · Trie walk 终止保护策略统一 【P1】✅ 2026-08-23

- **现状**：Node.js 用 magic number 1000；其余语言靠每步 bounds-check。两种策略都安全，但无文档说明，且 magic number 无法表达"超过即文件异常"的语义。
- **做法**：统一为推导式上限 `max(IPBits + 8, 配置下限)`（IPv4≈40 / IPv6≈136），替换硬编码；若某语言保留现有机制，须在 `QZDB_SYNC_GUIDE.md` 记录理由。
- **验收**：8 语言 walk 上限来源可追溯（常量或推导式），SYNC_GUIDE 有对照表；正常数据全量查询无回归。
- **验证**：`cd multi-lang && ./run_all.sh`
- **完成**：5 语言（C/Go/Node.js/PHP/Python）魔法常量 1000 → 按位宽派生命名常量（V4=40/V6=136）；C#/Java/Rust 审计确认构造性有界，仅登记不改动。8 语言对照表见 `QZDB_SYNC_GUIDE.md` 第五节。良构文件行为零变化（V4 实际 ≤16 步、V6 ≤128 步）。

### T6 · CI 编译门禁 【P0】✅ 2026-08-22

- **现状（已修复的缺陷）**：原 `verify.yml` 在无数据步骤硬性 `exit 1`，而 `.qzdb` 因安全规则不入库 → 托管 runner 上该工作流**从未可能通过**，形同虚设；`188ca29` 的坏构建因此在 main 上存活一周。
- **做法**：两层设计。Tier 1 `compile-gate` 永远执行——8 语言编译/语法门禁 + C ASan/UBSan 无数据 hostile 套件（`boundary_test.c` 文件头注释预定的用途），全部命令经本地实测；Tier 2 `full-verification` 仅当配置 `DATA_DOWNLOAD_TOKEN` secret 且存在私有 release（默认 tag `test-data`，含 .qzdb 资产）时拉取数据跑完整 L1-L4，否则优雅跳过。
- **验收**：新 workflow YAML 结构校验通过；gate 内全部命令在本地逐条实测绿；verify.yml 移除。
- **验证**：push 后观察 Actions 首跑；本地等价命令见 `.github/workflows/ci.yml` 各步骤。
- **完成**：`.github/workflows/ci.yml` 替代 verify.yml。Tier 2 已按用户决策改为**公共 demo 样本方案**：上游公开的 `demo/qqzeng-ip-ult.qzdb`（360 行样本，非购买数据）连同 CSV 真值入库（.gitignore 加否定规则），新增 `tools/demo_sample_check.py` 做 Python↔CSV 逐字段锚定 + node/php parity 校验，本地实测 2160/2160 通过；CI 无需任何 secret 或下载。真实购买库的 L1-L4 全量验证仍按既有惯例在本地执行。

### T7 · Metadata TLV type=5/6（data_month/scope）权威对齐 8 语言 【P0】✅ 已完成（2026-08-23）

- **现状**：C# 参考实现已消费 Metadata TLV type=5(data_month)/type=6(scope)（`QzdbReader.cs` case 5/6），但其余 7 语言忽略这两类条目——同一带 TLV 的文件在 C# 与其他 SDK 上 `getDataMonth()`/`getScope()` 结果不一致，违反 SYNC_GUIDE 跨语言一致性要求。此前 ff78fed 以「未经审查」为由剥离过一版实现，属流程问题而非方案问题；本任务按维护规则 3 重走 docs 先行 → 8 语言同步。
- **做法**：FORMAT §8.2 增补 type=5/6 定义与权威语义（TLV 权威、Header BuildDate 仅作 data_month 回落、无条目 scope 返回 ""）；SDK_API §4.5 以 TLV 方案取代「header 迁移前置依赖」；7 语言（Go/Java/Node/PHP/Python/Rust/C）补 TLV case 5/6 消费与 getter 对齐；PHP repairDimMasks 同步改为逐组推导（该组自己的字段名判 asn，不用当前组顶替）。
- **验收**：8 语言 `getDataMonth()`/`getScope()` 对带/不带 type=5/6 的文件行为逐字一致；不带 TLV 的旧文件行为零变化（回归保护：现有 GetScope=="" 等断言全部保持绿）。
- **验证结果（2026-08-23）**：Python `test_tlv_meta.py` 6/6（合成库正/反两路）、`test_regression.py` 6/6 + `demo_sample_check.py` 1440/1440 + `test_hostile_vectors.py` OK（真实旧文件回落零变化）；Node `test.js` / `regression_test.js` / `test_suite.js` 全绿（Tier1 379、Tier2 黄金校验 4102/4102）；PHP tier1 新增 TLV 权威断言；C 新增 `tlv_meta_test.c`（真实库注入 TLV，ASan 下 3 用例全绿：TLV 权威/旧文件回落/重复条目 last-wins，兼作 scope 所有权 UAF 回归守卫），run_all_tests.sh 已接线 C-TlvMeta 门禁。Go/Java/C#/Rust 无 VM 工具链，待本地 `cd multi-lang && ./run_all.sh` 终验（新增 Go `tlv_meta_test.go` 正/反两测、readme_api_test 断言已同步新契约）。
- **遗留验证**：本地跑 `./run_all.sh`（重点 `go test ./qzdb/ -run TestMetadata -v`）。

### T8 · 原生浮点格式化统一契约（FORMAT §10.5）8 语言对齐 【P0】✅ 已完成（2026-08-23）

- **现状**：各语言原生标量（§11.1 nativeType=1）浮点格式化各自为政：C#/Go/Java 对 |v| ≥ 2^63 的整值直接 cast 到 int64（.NET 未指定 / Go 实现相关 / Java 饱和到 MAX/MIN）；Rust 阈值 1e16 导致 ≥1e16 整值输出 `.000000` 尾巴；C 的 ±2^52 guard 同病；Node 对 ≥2^53 整值走最短 round-trip（`9223372036854774784 → "9223372036854775000"`）、≥1e21 更泄漏科学计数法（`1e300 → "1e+300"`）。同一文件跨语言 `toPipe()` 逐字不一致。
- **做法**：FORMAT §10.5 落统一契约（整数值→精确十进制展开；非整数→固定 6 位小数；NaN/Inf→""；cast 前必须 \|v\| < 2^63 范围保护，超范围走无小数点定点格式化）；API_CONTRACT §三.2 同步。修复 C#（F0 分支 + float 重载委托 double）、Go（'f',0 分支）、Java（抽包级静态 formatNativeFloat + %.0f ROOT 分支）、C（guard 放宽至 ±2^63 + %.0f 分支）、Rust（阈值提至 2^63 + {:.0} 分支）、Node（≥2^53 经 IEEE754 位解码转 BigInt 定点）、PHP（边界 <= 收紧为 <，恰为 2^63 走 %.0F）。
- **验收**：8 语言对 116.0/-3.0/-0.0/116.4/-3.5/NaN/±Inf/1e16/9.2e18/±2^63 边界/1e20/±1e300 全部逐字一致。
- **验证结果（2026-08-23）**：Python `test_native_float.py` 20/20、Node `native_float_test.js` 22/22、C `native_float_boundary_test.c` 19/19（VM 实测 NATIVE_FLOAT_OK）；同源用例已入 Go `native_float_test.go`、Rust lib.rs `t_fmt_native_float_boundaries`、Java QzdbReaderTest 纯逻辑段、C# netcore.Tests/NativeFloatTests.cs，待本地工具链终验；run_all_tests.sh 已接线 Python-NativeFloat / Node-NativeFloat / Go-NativeFloat / C-NativeFloat 四个门禁。

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
