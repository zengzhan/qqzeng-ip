# QZDB 多语言 SDK 发行就绪度评估报告

> 评估时间：2026-08-08（第三轮复核已并入）｜ 评估范围：8 种语言 SDK + 文档 + 发布脚手架
> 结论：**核心 SDK 全部就绪，可推送 GitHub**。
> 第一轮修复 5 处致命脚本缺陷 + 10 处文档不一致 + 1 处规范不符；
> 第二轮（安装 JDK 21 后）补齐 Java 全量验证，并新发现修复 **1 类跨全部 8 语言的 OOM/DoS 安全漏洞** 与 **6 处对拍工具缺陷**（其中 Rust 从未真正参与过跨语言对拍）。
> 第三轮引入 **"纯 clone 快照"验证维度**（此前所有验证均在工作区进行，会被未入库文件掩盖），据此揪出并修复 **1 个 P0 发布阻断项**（`.gitignore` 吞掉 Rust 4 个 `[[bin]]` 源文件，导致 clone 后 `cargo build` 必然失败）与 **1 处休眠测试**（C 并发压测 + BUG#1 回归守卫从未被调用）。

---

## 一、核心类名统一（规范基线：v2.4 统一为 `QzdbReader`）

| 语言 | 核心类型/文件 | 状态 |
|------|-------------|------|
| C | `qzdb_reader_t` / `qzdb_reader.c/.h` | ✅ |
| C# | `QzdbReader` / `QQZeng.Qzdb` 命名空间 | ✅ |
| Go | `QzdbReader` / module `qzdb_reader` | ✅ |
| Java | `QzdbReader` / `com.qqzeng.qzdb` | ✅ |
| Node.js | `QzdbReader` | ✅ |
| PHP | `QzdbReader` / `Qqzeng\Ip` | ✅ |
| Python | `QzdbReader` | ✅ |
| Rust | `QzdbReader` / crate `qzdb_reader` | ✅ |

**旧名残留（`QzdbSearcher`/`DatabaseReader`/`qzdb_searcher`/`qzdb_ctx_t`）在全部 8 种语言源代码中已零匹配。**

---

## 二、编译 / 运行实测（8 种语言全部本机验证通过）

| 语言 | 工具链 | 回归测试 | 畸形文件 Fuzz | 跨语言逐字节对拍 |
|------|--------|---------|--------------|----------------|
| Python | 3.13.12 | ✅ Tier1 61 断言 / Golden 4102 | ✅ 0 失败 | ✅ 基准 |
| Node.js | 22.22.2 | ✅ Tier1 379 / Tier2 4102 | ✅ 0 失败 | ✅ 一致 |
| C | clang 21 (ASAN+UBSan) | ✅ 156/156 | ✅ 0 失败 | ✅ 一致 |
| PHP | 8.5.9 | ✅ Tier1 105 / Golden 4102 | ✅ 0 失败 | ✅ 一致 |
| Rust | 1.96.0 | ✅ 83 tests | ✅ 0 失败 | ✅ 一致 |
| C# | dotnet 10.0.302 | ✅ ALL TIERS PASSED（2831 万节点） | ✅ 0 失败 | ✅ 一致 |
| Go | 1.24.3 | ✅ `go build`+`go vet`+`go test` | ✅ 0 失败 | ✅ 一致 |
| Java | **openjdk 21**（本轮新装） | ✅ 47/47（196 断言）+ Tier2 3962 万节点 0 偏差 | ✅ 0/3293 | ✅ 一致 |

> **跨语言逐字节对拍（本轮新做）**：两套库布局（`ult_china` / `std_china`）× 8 语言 ×（V4 3505 例含 3000 命中 + V6 2304 例含 2000 命中）
> = 约 **9.3 万次查询，输出文件 `cmp` 逐字节完全相同，零差异**。这是本项目首次让 8 种语言在 **IPv6 路径**上完成真实对拍。

---

## 三、本次修复清单（致命 / 文档 / 规范）

### 3.1 致命缺陷（会导致测试或发布脚本失败）— 已全部修复
1. `multi-lang/run_all_tests.sh:127` — Java 测试调用 `DatabaseReaderTest`（已删除类），改为 `QzdbReaderTest`。
2. `multi-lang/tools/build_all.sh:79` — 引用已删除的 `IpLocation.java` 及错误路径 `qzdb/`，改为扫描 `com/qqzeng/qzdb/` 全部 `.java`。
3. `multi-lang/cross_lang_verify_v6.py:130-144` — Java v6 校验代码用 v1 旧 API（`qzdb.QzdbReader.getInstance()`/`IpLocation`/Gson），重写为 v2.4（`com.qqzeng.qzdb.QzdbReader.Builder`/`GeoInfo`/手工 JSON）。
4. `multi-lang/tools/BatchQuery.java` — 整文件用 v1 旧 API（`package qzdb`/`IpLocation`/`findV6Uint`），重写为 v2.4（`com.qqzeng.qzdb`/`GeoInfo`/`findUint`/`findBytes`）。
5. `multi-lang/run_batch_test_suite.py:191` — C# 批量适配器引用旧 DLL `qzdb-searcher.dll`，改为 `QQZeng.Qzdb.dll`。

### 3.2 文档 / 规范不一致 — 已全部修复
6. `README.md` / `CLAUDE.md` 标题 `qzdb-searcher` → `qzdb`；`README.md` Java 描述 `DatabaseReader` → `QzdbReader`；import 路径 `com.qqzeng.ip` → `com.qqzeng.qzdb`；C# `using Qqzeng` → `using QQZeng.Qzdb`。
7. `PUBLISHING.md:103/110` — 版本/框架描述（旧写 1.0.0 + net10.0 单目标）同步为实际 `1.0.2` + `net8.0;net9.0;net10.0` 多目标。
8. `multi-lang/README.md` — Java/C#/Node/PHP 代码示例改为 v2.4 构造方式（Builder / `new` / `Open`）；删除指向不存在的 `README_zh.md` 死链；Node 示例中 `require` 直接取类（非解构）。
9. `docs/QZDB_SYNC_GUIDE.md:125-128` — Java 同步路径改为 `com/qqzeng/qzdb/`，删除 `IpLocation.java` 引用。
10. `tools/release.sh` — 版本提取源 `FORMAT.md`（不存在）改为 `API_CONTRACT.md`；Co-authored-by `qzdb-searcher` → `qzdb`。
11. `multi-lang/c/qzdb_reader.h` — 头文件保护宏 `QZDB_IP_SEARCH_H` → `QZDB_READER_H`（旧名风格残留，不影响功能）。
12. `multi-lang/netcore/QzdbReader.cs` — `FindStr()` 原会向调用方抛异常；按规范 §3（findStr 对非法 IP 返回 `""`）改为 `try/catch` 返回空串。**已实测验证**：非法 IP 与空串均返回 `""`，不再抛异常。

### 3.3 【第二轮】安全漏洞：POOLS 偏移表非单调 → OOM/DoS（**全部 8 语言均中招**，已全部修复）

**成因**：POOLS 段的偏移表语义是"累进"的——`offsets[i+1] >= offsets[i]`，末位 `tail` 即该池总字节长度。
**8 种语言无一校验该单调性**。攻击者只需把每个 offset 都改成接近段尾，就能让 `count` 个字符串各自"横跨整段"，
形成 `count × 段长` 的放大分配。

**实测**：对 `qqzeng_ip_ult_china.qzdb`（12.16 MB）仅翻转文件头 1 个字节（`hdr[138]` 即 `offPools` → `0x01`），
即触发 138,977 个字符串 × 约 11 MB ≈ **7.2 GB 分配 → OutOfMemoryError**（Java 侧首次由 fuzz 捕获，
其余 7 语言经复核确认同样缺失该校验）。

**统一修复模式**（8 语言逐一落地）：

```
tail = offsets[count]                 // 末位 = 该池总字节长度
if tail > avail: 跳过/降级该池
prev = 0
for i in 0..count-1:
    start = offsets[i]; end = offsets[i+1]
    if start < prev || end < start || end > tail: 视为空串跳过
    prev = end
cursor = strBase + tail
```

| 语言 | 修改位置 | 复验 |
|------|---------|------|
| Java | `QzdbReader.java` `parsePools` | fuzz 0/3293 |
| Python | `qzdb.py:~1333` | fuzz 0 |
| Node.js | `qzdb.js:~980` | fuzz 0 |
| PHP | `QzdbReader.php:~1495` + `poolString:~1532`（惰性描述符，额外存 `total` 上界） | fuzz 0 |
| Go | `qzdb/qzdb.go:~721` | fuzz 0 |
| Rust | `src/lib.rs:~1718`（`saturating_add`） | fuzz 0 |
| C | `qzdb_reader.c:~1208`（两趟 arena 预算，`pool_scan_t` 新增 `tail`） | ASAN/UBSan 0 |
| C# | `QzdbReader.cs:~577`（最严格，逐条抛异常） | fuzz 0 |

> Fuzz 方法（8 语言同一套 4 类用例）：① 截断扫描 ② 192 字节文件头逐字节 4 种位模式穷举（752 例）
> ③ 前 512KB 内 2000 次随机翻位 ④ 500 次随机截断。判定标准：任何 panic / 未捕获异常 / OOM / 段错误 = FAIL。
> 修复后 8 语言全部 **0 失败**，且第二节的 9.3 万次正常查询对拍证明修复未改变合法文件的解析结果。

### 3.4 【第二轮】跨语言对拍工具链缺陷（6 处，已全部修复）

这些工具位于 `tools/`，不参与 `go build ./...` / 单测，长期无人触碰而全部腐化，
导致 `cross_verify.py` 声称的"7 语言对拍"实际大面积失效：

1. **`tools/batch_query.go`** — 调 `NewSearcher(dbPath, 0)`（实际需 3 参）且 `FindUint` 按 1 返回值接收（实为 2）；**根本无法编译**。已修正并补 `defer Close()`。
2. **`rust/src/bin/batch_rust.rs`** — 实现的是 **stdin 逐行**接口，而 `cross_verify.py` 按 `<db> <v4_in> <v4_out> <v6_in> <v6_out>` 五参文件式调用；进程会挂起等 stdin 直至被 SIGKILL（退出码 137）。**即 Rust 从未真正参与过任何一次跨语言对拍**。已按统一约定重写。
3. **`tools/BatchQuery.java`** — ① `findUint(long)` 类型不符导致编译失败；② V6 用 **十六进制** 解析 `high:low`，而其余 6 语言与 `cross_verify.py` 生成端均为 **十进制** —— 即使编译通过，Java 的 V6 结果也会静默错位；③ `args.length < 4` 却访问 `args[4]`。三项均已修复。
4. **`tools/batch_csharp/BatchQuery.cs`** — V6 分支把 `"high:low"` 整串直接喂给 `Find(string)`，必然 100% 落空。已补 `ParseV6Key()` 还原 16 字节大端地址后走 `FindBytes()`。
5. **`tools/batch_query.js` / `tools/batch_query.php`** — 均调用 v2.4 已删除的 `QzdbReader.getInstance()`，一运行即崩。分别改为 `QzdbReader.open()` / `new QzdbReader()`；JS 的 `args.length < 4` 一并改为 `< 5`。
6. **`tools/batch_query.c`** — `sscanf` 返回值未检查，解析失败时 `high`/`low` 保持未初始化即被使用（UB）；`process_v4/v6` 形参多加了 `const`，与会写内部行缓存的 `qzdb_find_*` 冲突而告警。均已修复，现 **0 warning** 编译。

附带修复 `tools/build_all.sh`：多处构建命令通过 `| tail -N` 截断输出，`set -e` 会被管道末端的 `tail`（恒 0）掩盖，
编译失败可静默通过。已补 `set -o pipefail`。

### 3.5 【第二轮】陈旧路径引用（会误导后续维护，已修复）

- `docs/QZDB_TEST_SPECIFICATION.md:121` — 仍要求测试"多次 `getInstance` / 单例行为"，而该 API 在 v2.4 已删除；改写为多实例互不干扰 + Registry 复用。
- `.claude/agents/code-reviewer.md` — 跨语言审查清单指向 `qzdb_searcher.c`/`QzdbSearcher.cs`/`QzdbSearcher.php` 三个**已删除文件**，且要求检查不存在的 `FORMAT.md`。已全部改为现行路径与 `docs/QZDB_FORMAT.md`。
- `.claude/skills/create-migration/SKILL.md` — 同上三处失效路径，已修正。

### 3.6 清理
- 删除 Rust `target/`（451MB）、Java `build/`、Python `__pycache__/`、C# `bin/obj/` 等可再生产物（均已被 `.gitignore` 覆盖，不入库）。
- 确认 `.gitignore` 已覆盖 `*.qzdb` / `target/` / `build/` / `bin/` / `obj/` / `__pycache__/` / `*.class` / `*.o` / `nupkgs/` / `test_runner_bin/` 等。

### 3.7 【第三轮·P0 发布阻断】`.gitignore` 吞掉 Rust 全部 `[[bin]]` 源文件

**问题**：`.gitignore` 第 7 行的 `bin/` 规则（本意屏蔽 .NET 构建输出）同时命中了 `multi-lang/rust/src/bin/` —— 而 Rust 的约定是 **`src/bin/` 存放二进制目标的源代码，不是构建产物**。结果 4 个源文件从未入库：

| 文件 | 用途 |
|---|---|
| `src/bin/batch_rust.rs` | 跨语言对拍 runner |
| `src/bin/demo.rs` | 快速上手示例 |
| `src/bin/dump_rust.rs` | 数据库转储工具 |
| `src/bin/regress_rust.rs` | 回归校验工具 |

**危害等级 P0**：`rust/Cargo.toml` 显式声明了这 4 个 `[[bin]]` 目标，因此任何人 `git clone` 后执行 `cargo build` **必然失败**（非降级、非告警，而是硬失败）：

```
error: can't find bin `batch_rust` at path `.../src/bin/batch_rust.rs`
error: can't find bin `demo` at path `.../src/bin/demo.rs`
error: can't find bin `dump_rust` at path `.../src/bin/dump_rust.rs`
error: can't find bin `regress_rust` at path `.../src/bin/regress_rust.rs`
error: could not compile due to 4 previous target resolution errors
```

**为什么前两轮没发现**：此前所有编译/测试/对拍验证都在**工作区**执行，工作区里这些文件是存在的（只是没被 git 跟踪），因此全绿 —— 缺陷被完全掩盖。

**修复**：在 `.gitignore` 中为该目录加 negation 例外并注明原因：

```gitignore
# Build artifacts
bin/
# Exception: Rust convention puts BINARY SOURCE files in src/bin/, not build output.
# Without this negation the `bin/` rule above silently drops the 4 [[bin]] source
# targets declared in rust/Cargo.toml, and `cargo build` fails on a fresh clone.
!multi-lang/rust/src/bin/
obj/
```

**新增验证维度：纯 clone 快照构建**（`git write-tree` + `git archive` 导出**仅含入库内容**的快照，在其上构建）。8 种语言全部通过：

| 语言 | 快照构建结果 |
|---|---|
| Rust | `cargo build --release` → `Finished release profile` ✅（修复前：4 targets 硬失败） |
| Go | `go build ./...` + `go vet ./...` 双双 exit 0 ✅ |
| C | `gcc -std=c11 -O2 -Wall -Wextra` 编译核心 + test_main + failclosed，**核心零告警** ✅ |
| Python | `import qzdb` OK ✅ |
| Node.js | `require('./qzdb.js')` OK（导出 Builder/GeoInfo/UsageType/RowIds/BatchResult/QzdbRegistry）✅ |
| PHP | `php -l QzdbReader.php` → No syntax errors ✅ |
| Java | `javac 21.0.12` 全量主源码编译通过 ✅ |
| C# | `dotnet build -c Release` → **0 Warning / 0 Error**（net9.0 + net10.0 双目标）✅ |

**闭环验证**：用**快照中编译出的** `batch_rust` 对 404 条 IPv4 + 301 条 IPv6 查询，与 Python 基准 `cmp` **逐字节一致** —— 证明救回的源码确实可用，而非仅能通过编译。

### 3.8 【第三轮】C 测试套件存在两个"休眠测试"（定义但从未被调用）

`gcc -Wall -Wextra` 在快照构建中报出 `unused function` 告警，暴露出 `multi-lang/c/test_main.c` 里两个测试函数从未进入 `main()`：

| 函数 | 本应守护的能力 | 实际状态 |
|---|---|---|
| `test_concurrent_stress()` | 多线程并发查询一致性压测 | 从未执行；且硬编码了**不存在**的路径 `multi-lang/c/qqzeng_ip_std_china.qzdb`（该目录下无任何 `.qzdb`），即使被调用也会 init 失败 |
| `test_find_fields_buf_consistency()` | **BUG #1 回归守卫** —— `resolve_row_id_fields` 曾硬编码 `safe_read_u24` 而忽略 ROW_SCHEMA 的 `row_geo_width`/`row_asn_width`，在非默认位宽库（如 std_china：geo=2/asn=1/stride=3）上会读错字节 | 从未执行，回归保护形同虚设 |

**修复**：`test_concurrent_stress` 改为接受 `dbpath` 形参（消除 CWD 相关的硬编码路径），并把两个测试接入 `main()` 的 DB-backed 测试块。

**结果**：C Tier1 断言数 **156 → 167，全部通过**（`TIER1_PASS`，fail=0），并发压测与 BUG#1 回归守卫首次真正生效。

> 顺带核查了其余语言的同类风险：Rust（80 个 `#[test]`）、Go（39 个 `TestXxx`）、Java（JUnit 注解）、C#（xUnit）、Python（pytest）均为框架自动发现，不存在漏注册；Node/PHP 测试文件中也未发现定义未调用的用例。**C 是唯一手动注册测试的语言，故为该类缺陷的唯一宿主。**

---

## 四、遗留事项（不影响 SDK 发行，按需跟进）

1. ~~**Java 本机编译验证缺失**~~ — **已解除**。本轮安装 openjdk 21 后完成全量验证：`QzdbReaderTest` 47/47（196 断言）、`FullAccuracyAndPerfTester` Tier2 3962 万节点 0 偏差、fuzz 0/3293。Maven 未安装，但项目零 Maven 依赖，直接 `javac` 编译通过；`mvn package` 仍建议由 CI 兜底。
2. **L3 批量框架 Java 适配器不匹配**：`run_batch_test_suite.py` 调用 `BatchMain` 类，但从未存在该类；现有 `tools/BatchQuery.java` 为文件 I/O 模式，与批量框架的流式（stdin/stdout）协议不同。此为批量验证脚手架（L3）遗留问题，不影响核心 SDK，需另立任务补齐流式适配器。
3. **PHP 无 `composer.json`**：当前 PHP SDK 以单文件拷贝方式分发（与 README 一致），`PUBLISHING.md` 仅登记 .NET 与 Java 为官方包。如需上 Packagist，补充 `composer.json`（vendor `qqzeng`/package `ip-qzdb`，autoload `Qqzeng\Ip`）。
4. **Go `NewSearcher` / Rust 局部变量 `searcher`**：属有意保留的兼容别名 / 命名风格残留，不影响编译与功能，可按需统一为 `NewReader` / `reader`。

---

## 五、上线建议

- ✅ 8 语言核心 SDK 类名统一、API 一致。
- ✅ **8 种语言全部本机编译 + 回归 + fuzz 通过**（Java 遗留缺口第二轮已补齐）。
- ✅ **8 种语言全部通过"纯 clone 快照"构建验证** —— 保证使用者 clone 下来即可构建，不依赖任何未入库的本地文件（第三轮新增维度）。
- ✅ **9.3 万次跨语言逐字节对拍零差异**，覆盖 2 套库布局 × IPv4/IPv6 双栈；快照版 Rust runner 复验 705 条查询同样逐字节一致。
- ✅ POOLS 非单调 OOM/DoS 漏洞在 8 语言全部封堵，Fail-Closed 语义一致。
- ✅ 致命脚本/工具缺陷已清零；对拍工具链 6 处腐化已修复（Rust 首次真正接入对拍）。
- ✅ P0 发布阻断项（`.gitignore` 吞 Rust `src/bin/`）已修复并实测闭环；C 两个休眠测试已激活（断言 156→167 全过）。
- ✅ 文档与 agent/skill 配置中的失效路径已全部对齐。
- ⚠️ 推送前确认：GitHub 远程仓库已配置（`git remote -v` 当前为空，需 `git remote add origin <url>`）；Java 包名所有权（com.qqzeng）与 NuGet 包 ID（QQZeng.Qzdb）可用性已确认；发布动作（tag/NuGet push/Maven deploy）按 PUBLISHING.md 由人工触发。

**结论：核心 SDK 已具备推送 GitHub 的条件，可上线。**
