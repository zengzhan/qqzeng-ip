# 更新日志

本文件记录 QZDB 多语言 SDK 的用户可见变更。格式参照 Keep a Changelog；语义化版本。

## [Unreleased]

### Added

- Rust SDK 具备 crates.io 发布条件：crate 名 `qzdb_reader` → **`qzdb`**（与 PyPI 的 `qzdb` 对齐；C 语言的 `qzdb_reader_t` / `qzdb_reader.h` 不受影响）；`Cargo.toml` 补全 crates.io 必需的 `description` / `license` / `repository` / `homepage` / `documentation` / `keywords` / `categories` / `rust-version=1.74`；新增 `multi-lang/rust/LICENSE`；新增 `.github/workflows/publish-crates.yml`（tag `v-rust-*` 触发，先 dry-run 后 publish）。
- PHP SDK 具备 Packagist 发布条件：仓库根新增 `composer.json`（包 **`qqzeng/qzdb`**，classmap 指向 `multi-lang/php/QzdbReader.php`）。因 Packagist 只认仓库根的 `composer.json`，同时新增根 `.gitattributes`，用 `export-ignore` 把 dist 裁剪到 5 个文件 / 160 KB（否则用户 `composer require` 会连带下载另外 7 种语言源码与 4.3 MB demo 数据库）。
- 多语言 monorepo 的 git tag 发布约定（`v<ver>` 全平台 / `v-python-*` / `v-java-*` / `v-rust-*` 单平台），见 `PUBLISHING.md` §0。

### Changed

- Node.js `QzdbRegistry`：`register()`/`registerBuffer()`/`unregister()` 对被替换/移除的 reader 改为进入容量 8 的退休队列延迟关闭（对齐 Go/Java/netcore），消除 await 让出期间并发热更新导致在途调用静默返回 null 的隐蔽问题；`clear()` 语义不变（立即关闭并冲刷退休队列）。附 7 条行为回归断言。
- Rust `serde_json` 由 `[dependencies]` 移入 `[dev-dependencies]`：lib 不使用该依赖，此前会无谓地进入下游依赖树。
- Rust 发行包范围收紧：`src/bin/*`、`src/main.rs`、`tests/*`、`bench_qps.rs` 经 `exclude` 排除。其中 `src/bin/metaprobe.rs` 依赖 dev-only 的 `serde_json`，若随包发布会导致下游 `cargo install qzdb` 编译失败；`tests/*` 依赖未随包发布的 `.qzdb` 数据。`cargo package` 时打印的 16 行 `ignoring ...` warning 属预期。

### Fixed

- 发布 workflow 的 tag 过滤器过宽：`publish-pypi.yml` 与 `publish-maven-central.yml` 原本都是 `tags: ['v*']`，在多语言 monorepo 下会误匹配 `v-rust-*` / `v-java-*`，造成单语言发版时**连带触发其它平台的发布**。现均收紧为 `v[0-9]*`（纯版本号，全平台）+ 各自语言前缀。

### Fixed

- 8 语言 IP 解析器严格性对齐：拒绝 `"a.b.c.d::"` 形态（嵌入 IPv4 点分四元组落在 `::` 压缩缺口左侧且右侧为空，如 `0.0.0.0::`、`1.2.3.4::`、`2001:db8:1.2.3.4::`）。此前 Python/Node/PHP/C/Rust/Java 六语言错误接受（Node 对 `1.2.3.4::` 还会产出错乱字节），与 Go SDK 及 Go 标准库 `netip.ParseAddr` 行为不一致；C# 经审计本就正确。十行行为契约表已作为永久回归落至各语言测试套件（Go fuzz 差分对拍发现，netip 为裁判）。

## [1.0.7] - 2026-08-28

### Added

- .NET / C# 新增 `net11.0` 目标框架（现为 `net8.0;net9.0;net10.0;net11.0` 四目标）；`System.IO.Hashing` 显式引用（net11 定位包同样不内置 Crc32）。构建需 .NET 11 SDK，`global.json` 改为 `rollForward: latestMajor` + `allowPrerelease: true`，.NET 11 GA 后自动回落稳定版 SDK。
- .NET / C# 全部公开 API 补齐 XML 文档（CS1591 归零，包内 XML 13KB→56KB）。

### Changed

- .NET / C# 显式开启严格静态分析：`AnalysisMode=All` + `TreatWarningsAsErrors`（配套逐规则豁免见 `multi-lang/netcore/.editorconfig`）；`EnforceCodeStyleInBuild` 刻意不开启（被目录外 ProjectReference 消费时不可移植）。
- .NET / C# 包验证基线由 1.0.5 提升至 1.0.6；ApiCompat 确认 1.0.7 无破坏性 API 变更。

### Fixed

- .NET / C# 真实代码缺陷：`QzdbException` 补齐 CA1032 标准构造面（默认 `ErrorCode` 由 `NotFound` 改为 `InvalidParam`）；`GeoInfo.BuildNormalizedMap` 增加 CA1062 空参校验；`ChainedReader._readers` 收敛为 `ReadOnlyCollection<QzdbReader>`（CA1859）；`UsageType` 移除未使用 `using`；`Tier1` 测试修复 CS8600。

## [1.0.6] - 2026-08-27

### Fixed

- .NET / C# `LookupRowIdUint` / `LookupRowIdBytes`：修正原代码中误用 `RequireSnapshot()` 导致 Dispose 之后抛出 `ObjectDisposedException` 的行为不一致瑕疵，改为使用 `_activeSnapshot` 直接读取，统一遵循 `Lookup*` 家族在 Dispose 后的**软失败返回 0** 契约（同时消除了原代码中永远无法命中的死代码分支）。
- .NET / C# `Snapshot.FromPath`：重构文件及内存映射资源生命周期为嵌套 `try/catch` 模式，修复 0 字节空文件或异常畸形文件在 `CreateFromFile` / `CreateViewAccessor` 异常时导致的 2 处潜在文件句柄/映射未及时释放的泄漏窗口（通过 Roslyn `AnalysisMode=All` 严苛审计与 12000 次压力测试验证零泄漏）。
- .NET / C# 异常提早校验：`FromPath` 针对 0 字节空文件增加显式拦截，抛出规范的 `"QZDB file is empty"` 异常。

### Changed

- .NET / C# 代码现代化：`RequireSnapshot()` 采用 `ObjectDisposedException.ThrowIf`；`QzdbRegistry.Unregister` 针对跨方法延迟安全释放队列补充有理有据的显式抑制与规范注释；消除冗余字段初始化赋值。

## [1.0.5] - 2026-08-23

### Added

- Metadata TLV type=5（data_month）/ type=6（scope）权威消费，8 语言（C/C#/Go/Java/Node/PHP/Python/Rust）getter 行为逐字对齐：带条目时 TLV 为权威，无条目时 data_month 回落 Header BuildDate、scope 返回空串。规范见 `docs/QZDB_FORMAT.md` §8.2 与 `docs/QZDB_SDK_API.md` §4.5。
- C 注入式回归测试 `multi-lang/c/tlv_meta_test.c`：真实库注入 TLV 后校验权威/回落/重复条目 last-wins 三路径，兼作 scope 字符串所有权 UAF 回归守卫（ASan 下验证）。
- `docs/QZDB_SYNC_GUIDE.md` 新增文档同步规范（§六）与语言 README 统一章节骨架（§6.1）。

### Fixed

- Java `formatNativeFloat` 违反 FORMAT §10.5 精确展开契约：`String.format("%.0f")` 按最短 round-trip 数字补零（如 2^63 输出 9223372036854776000），改为 `new BigDecimal(v).toPlainString()` 并以 E300 硬编码字面量断言锁定。
- PHP `repairDimMasks` 由「当前组顶替」改为逐组按自身字段名推导，修复多组库的维度掩码修复错位。
- C 缓冲区查询 API 未命中统一返回 `QZDB_ERR_NOT_FOUND`（fail-closed，不再返回未初始化内容）。
- `tools/batch_query.go` 导入路径漂移（旧模块名 `qzdb_reader/qzdb`），恢复 build_all.sh Go 步骤可用。
- 测试基建：C# Tier1 改流式读取数 GB CSV 基准（常量内存）；PHP 合成夹具补 `poolIdxSize@13`、PHP 8.0 反射补 `setAccessible(true)`；L2 脚本支持 JAVA_HOME 环境变量与 clang/gcc/cc 编译器回退。

### Changed

- Trie 游走终止保护跨语言统一为按 IP 位宽派生的上限（Node.js/Go/C/PHP/Python 以命名常量替换魔法常量 1000；Rust/Java/C# 本就构造性有界，仅登记机制）。良构文件行为不变。
- 文档专业化整改：根 README 与 multi-lang README 重写（示例全部来自可运行代码与实测输出、fail-closed 路径完整）、SDK_API 头部去叙事化、9 篇过程稿归档至 `docs/archive/`。

### Compatibility

- 不含 type=5/6 条目的既有文件行为零变化（回归断言全绿）。
- 查询 API 签名无变化；仅错误路径语义收紧（C buf API miss 由实现定义值统一为 NOT_FOUND）。

### Verification

- 8 语言单语言套件全绿 + L2 跨语言一致性 427/427（8 语言 × 61 IP 管道输出全同）+ L3 批量回归全 10 库 28,602,081 节点 0 错误 + 敌对向量测试通过。明细见 `docs/ROADMAP.md` T7/T8。

## [1.0.4] 及更早

见对应 git tag 与历史提交记录。
