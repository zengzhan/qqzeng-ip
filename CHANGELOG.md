# 更新日志 (CHANGELOG)

本文件记录 QZDB 多语言 SDK 的用户可见变更。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)，实行语义化版本控制。

## 各语言 SDK 独立版本矩阵

| 语言 SDK | 当前版本 | 仓库子路径 | 官方包坐标与引入方式 |
| :--- | :---: | :--- | :--- |
| **Java** | `1.0.10` | `ip-qzdb-sdk/java` | `com.qqzeng:qzdb:1.0.10` |
| **.NET** | `1.0.10` | `ip-qzdb-sdk/netcore` | `QQZeng.Qzdb` (`1.0.10`) |
| **Rust** | `2.0.2` | `ip-qzdb-sdk/rust` | `qzdb = "2.0.2"` *(⚠️ 含破坏性变更，详见下方说明与迁移建议)* |
| **Python** | `1.0.8` | `ip-qzdb-sdk/python` | `pip install qzdb==1.0.8` |
| **Node.js** | `1.0.8` | `ip-qzdb-sdk/nodejs` | `npm install @qqzengip/qzdb@1.0.8` |
| **PHP** | `1.2.2` | `ip-qzdb-sdk/php` | `composer require qqzeng/qzdb:1.2.2` |
| **Go** | `v1.0.8` | `ip-qzdb-sdk/go` | `go get github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go@v1.0.8` |
| **C / C++** | 单文件源码 | `ip-qzdb-sdk/c` | 源码拷贝 `qzdb_reader.c` 与 `qzdb_reader.h` |

> ⚠️ **关于 Rust SDK 2.0.2 语义化版本声明与 Yank 策略**：
> `qzdb` Rust 2.0.2 版本进行了高并发值语义去共享化重构，涉及 `GeoData.values` 类型（由 `Box<[Arc<str>]>` 改为 `Box<[FieldVal<'static>]>`）、`ErrorCode` 增加 `#[non_exhaustive]` 以及 `FieldVal::Inline` 封装私有化。处理策略如下：
> 1. **下游适配**：旧代码若直接解构 `Arc<str>`，请迁移为使用 `field.as_str()` 或标准 `PartialEq`；`ErrorCode` 匹配分支请补充 `_ => ...` 通配符；
> 2. **主版本演进建议**：建议官方后续升主版本至 `v3.0.0` 正式对齐语义化版本规范，并视社区依赖对 2.0.2 执行 `cargo yank`。

---

## [2026-10-24] - v1.2.2 (全语言发布)

### Breaking Changes
- **Rust SDK**: 查询结果表示由 `Box<[Arc<str>]>` 重构为 `Box<[FieldVal<'static>]>`，去除原子引用计数争用（16 线程并发达 233M+ QPS）；`ErrorCode` 增加 `#[non_exhaustive]` 防止未来扩充破坏穷举；`FieldVal::Inline` 内部封装隐藏。
- **Node.js SDK**: `loadBuffer(Buffer)` 采用零拷贝共享视图，Reader 存续期间调用方不得就地篡改原 Buffer 内容；`Uint8Array` / `ArrayBuffer` 等其他输入仍执行安全复制。
- **Python SDK**: `find_stream` 对齐契约升级为三态 `BatchResult`（`geo_info` / `error` 二选一），不再对非法 IP 静默返回 `None`。

### Added
- **C SDK**: 新增原生跨平台定点格式化算法，彻底摆脱系统 locale 依赖，保证线程安全与跨平台一致性。
- **Go SDK**: 新增单趟零分配 IPv6 解析器，大幅降低 IPv6 高并发垃圾回收压力。
- **多语言 CI**: 新增基于公开 demo 样本库的 27 组 Golden Vectors 跨语言一致性回归与 C/Node/Go/PHP 逐字节输出对拍门禁。

### Changed
- **PHP SDK**: 缓冲模式 Trie 树遍历内联子节点解析，避开深层方法调用栈开销，遍历提速 25%~38%。
- **.NET SDK**: 优化 `TryParseIp` 实现路径，恢复高内联预算与指令缓存亲和，吞吐显著回升。
- **文档体系**: 全面建立 21 类 `usage_type` 权威枚举契约，统一 25 维全要素 Schema 与 CSV 字段顺序。

### Fixed
- **C SDK**: 修复 Linux/glibc 环境下调用专有符号 `snprintf_l` 导致未决引用的链接缺陷；修复 `qzdb_free()` 误释放外部借用缓冲缺陷。
- **Java SDK**: 修复 `Builder.build()` 失败路径与快速重载时的 mmap 映射泄漏隐患；修复 `findStr` 在非法 IP 输入时偶发未捕获异常问题。
- **Python SDK**: 修复高并发读与原子热更新并发时的竞态条件。

---

## [2026-09-20] - v1.2.0 (性能与稳定性发布)

### Added
- **Java SDK**: 升级引入 JDK 22+ Foreign Function & Memory (FFM) 堆外内存映射加速路径，同时保持与旧版 Java 运行时兼容。
- **PHP SDK**: 新增流式低内存解析模式，适配 CLI 批处理与长生命周期常驻进程内存受限场景。

### Changed
- **全语言 SDK**: 统一 `find_str` / `FindStr` 在非法输入与未命中场景下的返回值规约（统一返回空串 `""`）。

### Fixed
- **Go SDK**: 修复多网卡高频并发热重载（`Reload`）时的读写锁饥饿问题。
- **Node.js SDK**: 修复 BigInt 在特定 V8 优化层级下的位运算符号扩展边界异常。

---

## [2026-09-07] - v1.1.0 (双栈引擎重构)

### Added
- **双栈检索核心**: 全面发布 PATRICIA Trie + 16-bit Jump Table 混合检索算法，原生支持全球 IPv4 与 IPv6 极速查询。
- **多语言生态**: 发布 Rust 2.0.0、PHP 1.1.0、C# 1.0.8、Java 1.0.7、Python 1.0.6、Go 1.0.6 统一架构版本。

### Changed
- **存储格式**: 正式升级 QZDB v2.4 二进制存储规格，字段寻址全面采用 RowSchema 偏移直取，消除全量反序列化开销。

---

## [2026-08-28] - v1.0.7

### Added
- 基础 QZDB 单文件解析器与多语言初期参考实现。
