# 更新日志

本文件记录 QZDB 多语言 SDK 的用户可见变更。格式参照 Keep a Changelog；语义化版本。

## [Unreleased]

### Changed

- Node.js `QzdbRegistry`：`register()`/`registerBuffer()`/`unregister()` 对被替换/移除的 reader 改为进入容量 8 的退休队列延迟关闭（对齐 Go/Java/netcore），消除 await 让出期间并发热更新导致在途调用静默返回 null 的隐蔽问题；`clear()` 语义不变（立即关闭并冲刷退休队列）。附 7 条行为回归断言。

### Fixed

- 8 语言 IP 解析器严格性对齐：拒绝 `"a.b.c.d::"` 形态（嵌入 IPv4 点分四元组落在 `::` 压缩缺口左侧且右侧为空，如 `0.0.0.0::`、`1.2.3.4::`、`2001:db8:1.2.3.4::`）。此前 Python/Node/PHP/C/Rust/Java 六语言错误接受（Node 对 `1.2.3.4::` 还会产出错乱字节），与 Go SDK 及 Go 标准库 `netip.ParseAddr` 行为不一致；C# 经审计本就正确。十行行为契约表已作为永久回归落至各语言测试套件（Go fuzz 差分对拍发现，netip 为裁判）。

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
