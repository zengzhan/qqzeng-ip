# 更新日志 (CHANGELOG)

本文件记录 QZDB 多语言 SDK 的用户可见变更。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)，实行语义化版本控制。

## 各语言 SDK 独立版本矩阵

| 语言 SDK | 当前版本 | 仓库子路径 | 官方包坐标与引入方式 |
| :--- | :---: | :--- | :--- |
| **Java** | `1.0.11` | `ip-qzdb-sdk/java` | `com.qqzeng:qzdb:1.0.11` |
| **.NET** | `1.0.11` | `ip-qzdb-sdk/netcore` | `QQZeng.Qzdb` (`1.0.11`) |
| **Rust** | `2.0.3` | `ip-qzdb-sdk/rust` | `qzdb = "2.0.3"` |
| **Python** | `1.0.9` | `ip-qzdb-sdk/python` | `pip install qzdb==1.0.9` |
| **Node.js** | `1.0.9` | `ip-qzdb-sdk/nodejs` | `npm install @qqzengip/qzdb@1.0.9` |
| **PHP** | `1.2.3` | `ip-qzdb-sdk/php` | `composer require qqzeng/qzdb:1.2.3` |
| **Go** | `v1.0.9` | `ip-qzdb-sdk/go` | `go get github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go@v1.0.9` |
| **C / C++** | 单文件源码 | `ip-qzdb-sdk/c` | 源码拷贝 `qzdb_reader.c` 与 `qzdb_reader.h` |

---

## [Unreleased] - 工程审查修复（未发版）

### Fixed
- **Go SDK**: 修复 `Reload()` 与 `Close()` 并发时快照复活竞态：Close 后槽位改用哨兵标记（不再回写 nil），发布路径改为 CAS——关闭后 Reload 发布必然失败并显式释放新快照的 mmap；新增确定性屏障回归测试与锤击测试（`TestReloadAfterCloseDoesNotResurrect` / `TestCloseVsReloadHammer`，-race 下运行）。查询路径仍为单次原子 Load，无锁无变。
- **Python SDK / Node.js SDK**: IP 解析记忆表由「满载整体清空」改为两代有界轮换（generational FIFO）：当前代满 8K 条整体轮换为上代（O(1) 引用交换），上代命中即晋升——高基数流量下热点 IP 跨代存活，峰值内存不变（两代合计 16K 条）；Python 另对齐 Node.js：非法解析结果不再入缓存，杜绝负缓存污染。热路径命中开销零增加。
- **Rust SDK**: 线程本地 GeoInfo 缓存表由整表预分配（≈2MB/线程）改为行体按需懒分配（基础表 128KB/线程，触及的行才分配 128B）：内存 ∝ 实际工作集而非线程数，热点负载下 64 线程不再放大到 ~128MB；行索引与命中行为不变。
- **C SDK**: GeoInfo 解码缓存条目由「每条目 ~cnt+4 次 malloc/strdup」改为单块连续分配（entry + values[] + 字符串区 + pipe 区同块，构建期至多 3 次 realloc），`geo_cache_entry_destroy` 单次 free 整体回收；无锁 CAS 发布与指针生命周期契约不变。
- **Java SDK**: `findBatch`/`findBatchFields` 改为整批只获取并固定一次快照（旧实现逐条 acquire/releasePin），逐条三态语义保持；批内共享同一快照视图（对齐 C/Go 批量语义）。

### Performance
- **Java SDK**: IPv6 Trie 热路径（`lookupV6Bytes`）机器字预读：纯 Trie 查询实测 39 → 35 ns/op（-10%，std_global 库热池+混合流微基准）。Go/C 同型改造经实测无收益（Go 噪声内持平；C 反而 22 → 26 ns/op），已回退不合并。
- **Java SDK**: 批量路径单快照复用：混合负载实测约 3-5% 提升（收益主要在语义对齐，生命周期开销本就非主导）。

---

## [2026-11-23] - v1.2.3 (全语言 Patch 版本发布)

### Changed
- **全语言 SDK**: 权威收口 `usage_type` 字段为官方 18 类应用场景枚举；全语言统一对齐“无值统一输出为空字符串 `""`”（全面移除旧版残留的 `"Unknown"` 与 `"未知"`）。
- **README / 文档**: 更新应用场景枚举说明与最新全语言安装版本矩阵。

### Fixed
- **Rust SDK (`2.0.3`)**: 移除已废弃的静态内部池与 `Box::leak`，改用安全引用计数字符串（`Arc<str>`），彻底杜绝热更与长时间运行下的内存泄漏；清理冗余注释与死代码。
- **C SDK**: 引入 `qzdb_registry_acquire()` 与 `qzdb_registry_release()` 引用计数租约机制，实现双轨安全无锁回收；修复 `swap_ctx_atomic` 非原子覆写尾部并发同步字段的数据竞争隐患；`GROUP_SCHEMA` 截断解析时 Fail-Closed 返回 `QZDB_ERR_CORRUPTED`。
- **Go SDK (`v1.0.9`)**: 新增 `getOrDecodeGeoInfo()` 复用入口，消除 `FindFields` 投影时多余的 `cloneGeoInfo` 堆分配与 GC 压力；合并 `extractGeoPipe` 复用。
- **Java SDK (`1.0.11`)** / **.NET SDK (`1.0.11`)** / **Python SDK (`1.0.9`)** / **Node.js SDK (`1.0.9`)** / **PHP SDK (`1.2.3`)**: 同步更新并严密对齐 18 类应用场景与空值规约，100% 通过跨语言 Golden Vectors 门禁。

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
