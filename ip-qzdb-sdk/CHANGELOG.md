# 更新日志

本文件记录 QZDB 多语言 SDK 的用户可见变更。格式参照 Keep a Changelog；语义化版本。

## [Unreleased]

### Fixed

- **Java SDK：`Builder.build()` 失败路径泄漏 mmap 映射**（`QzdbReader.java`）。
  `new Snapshot(...)` 抛异常（CRC 校验失败 / 格式损坏 / 截断）时没有任何清理，`MmapSource`
  （及其 FFM `Arena`）只剩 GC 可达性这一条释放途径——在"坏文件反复重试加载"的服务
  （健康检查轮询、配置热更新）里映射会持续占用地址空间，Windows 上还会锁住该文件不让
  删除/替换。现统一在失败路径 `source.close()`。同批把 `reload()` 的
  `catch (QzdbException)` 放宽到 `Throwable`：`requireSnapshot()` 对已 close 的 reader 抛
  `IllegalStateException`，走原路径同样会漏掉映射。

- **Java SDK：`retiring` 由"晚一代"改为带宽限期的退休队列**（`QzdbReader.java`）。
  原实现用 `AtomicReference` 只保留一个待释放快照，两次 `reload()` 紧邻发生时会在第二次
  reload 的调用线程上**同步**释放，此时距第一次 reload 可能只过了几微秒，在途查询毫无保护
  ——JDK 22+ 的 Arena 路径下表现为查询偶发抛 `IllegalStateException`。现为 FIFO 队列 +
  最短 2 秒宽限期，并设 8 个快照的兜底上限（每个 Snapshot 自带 512 KB 解码缓存，不能无界
  累积）。`close()` 仍立即排空队列。
  注：该缺陷在 JDK 17~21 上不可复现——legacy 路径的 `Unsafe.invokeCleaner` 作用在
  `duplicate()` 上会静默失败，映射**从未真正释放**，因此撞不上（这正是 `MmapSource`
  要修的另一个问题）。

- **C# SDK：`FindStr` 3.6x 性能回退修复**（`ip-qzdb-sdk/netcore/QzdbReader.cs`）。
  `b7015b9` 引入的手写展开 `TryParseV4`（~5.5KB）带 `[MethodImpl(AggressiveInlining)]`，
  RyuJIT 将其强制内联进 `TryParseIp` → `FindStr`/`Find` 等全部调用方，撑爆内联预算与
  I-cache。同 worktree A/B 实测：回退为 `41dc4d3` 的简单循环解析后 `find_str` 口径 C
  从 **5.3M 恢复到 15.9–19.8M QPS**（`tools/perf_gate.py --langs netcore`）。
  仅摘掉 `AggressiveInlining` 不够（unrolled 仍 5.8M）——必须换回简单循环。
  `b7015b9` 的安全边界检查与批量路径全部保留。详见 `docs/ROADMAP.md` T11。

- **Java SDK：`findStr` 非法 IP 返回 `""`**（`QzdbReader.java`）。
  此前是 8 语言中唯一对非法 IP 抛 `QzdbException` 的实现，违反 API 契约
  `find_str` 条款（非法与未命中均应返回空串）。现已与其余 7 语言对齐。

- **Python SDK：`find_stream` 改为三态 `BatchResult`**（`qzdb.py`）。
  ⚠️ **行为变更**：此前 `find_stream` 对非法 IP 静默 yield `None`（与未命中不可区分），
  违反契约 §4 三态要求。现为 `find_iter` 的别名，逐条 yield `BatchResult`
  （`geo_info` / `error` 二选一）。迁移：`for gi in r.find_stream(ips)` 改为
  `for b in r.find_stream(ips)` 后用 `b.geo_info` / `b.error` 分流。
  与其余 7 语言的 `findStream` 入口对齐。

- **Python SDK：`chain_merge` / `chain_merge_override` 实现真字段级合并**（`qzdb.py`）。
  ⚠️ **行为变更**：此前 `chain_merge` 仅是 `chain`（FALLBACK）的别名，
  `chain_merge_override` 只是反转列表——字段级并集完全缺失。契约 §9.5 的典型场景
  「CN-pro 链 Global-ASN」会静默丢失 ASN 字段。现对齐 Node/Go/Java/C#/Rust/C：
  `MERGE` 最早非空优先，`MERGE_OVERRIDE` 最新非空优先，字段序为首次出现序。

### Changed

- **Java SDK：`BenchContract` 从 `src/main/java` 迁到 `src/test/java`**（`BenchContract.java`）。
  ⚠️ **发布物变更**：此前它属于运行时源集，会随 Maven Central 发布包一起发给用户
  （占 `qzdb.jar` 未压缩体积约 26%），并被 `tools/sync_to_github.py` 同步进公开仓库
  （此前已存在于 `ip-qzdb-sdk/java/src/main/java/.../BenchContract.java`）。它是基准
  harness、不是运行时 API，现与其余 4 个 harness 一致地放在测试源。同步脚本同步加固：
  排除清单 + 清理发布仓库里的孤儿文件。
  新调用方式：`mvn -q test-compile && java -cp target/classes:target/test-classes com.qqzeng.qzdb.BenchContract`。

- **Java SDK：`ChainedReader` 三个查询入口共用同一内核**（`ChainedReader.java`）。
  `find` / `findUint` / `findBytes` 统一走 `resolve(Lookup)`。此前 MERGE / MERGE_OVERRIDE
  模式下，`findUint` 会先 `String.format` 成点分文本、`findBytes` 会先经 `InetAddress`
  转成文本，再交给每个库重新解析一遍；现直接调用各库原生入口。
  ⚠️ **行为变更**：MERGE 模式下 `findBytes` 此前把**任意** reader 异常都重包成
  `INVALID_IP`；现与 `find`/`findUint` 对齐——只有 `INVALID_IP` 立即终止，其余错误跳过该库。
  实测：3 种模式 × 3 个入口 × 15 个 IP × 2 组库 + 全部错误路径，480 行输出与改动前逐字节相同。

- **Java SDK：`QzdbRegistry` 退休队列容量判断改原子计数**（`QzdbRegistry.java`）。
  原用 `ConcurrentLinkedQueue.size()`——该方法在 CLQ 上是 O(n) 全遍历（CLQ 不维护计数），
  而每次注册/注销都会走一遍；且判断非原子，并发 `register` 可短暂超过容量上限。

- **Java SDK：jar manifest 补 `Automatic-Module-Name: com.qqzeng.qzdb`**（`pom.xml`）。
  不写的话模块名由文件名推导，产物改名即破坏下游 `module` 声明。刻意不引入
  `module-info.java`（那会牵动测试源与用户侧模块路径）。

- **性能门禁下限重标定**（`tools/perf_gate.py`）。
  各语言 floors 从「实测 ~1/10」上调到「实测 ~1/3」，使 3x 以上回退无法静默通过。
  netcore 因 T11 修复从 1.5M 上调到 6M。c/go/rust/node 原余量 13–57x，
  正是 T11 能潜伏的制度原因。

- **C SDK：IPv4 解析改 Go 同构八位组展开单遍**（`qzdb_reader.c`）。
  `find_str` 口径实测 10.6M → 12.8–13.0M QPS。IPv4 热路径免去空白/冒号预扫描
  与冗余 strlen；严格性由展开逻辑本身保证（前导零 / ≤255 / 非数字符 / 段数）。
  `ip_strict_test` 10/10 与 Tier1 172/172 全过。

- **C SDK：`find_str` 缓存 miss 复用已解析 `entry_id`**（`qzdb_reader.c`）。
  此前 miss 时回退 `qzdb_find(ip_str)` 会重做一遍 parse + trie walk；现直接
  `get_geo_info(entry_id)`。错误语义逐项不变。C 缓存仍为 fill-only（借用指针
  生命周期契约不允许简单淘汰）。

- **PHP SDK：`GeoInfo::toPipe()` 结果 memoize**（`QzdbReader.php`）。
  对象不可变，`implode` 只做一次；对齐 Python `_pipe` 缓存与 Node 构造期预编码。
  geo 缓存命中流量下 `findStr` 免去每次全量拼接。

- **Python SDK：geo 缓存改直接映射覆盖式**（`qzdb.py`）。
  此前 dict 满 64K 后永不再收，长跑进程永久冷路径；现 `slot = hash & mask`
  碰撞覆盖单槽，与 Node/PHP 一致。内存上界不变，碰撞重算不返回错值。

- **Python SDK：`struct` 热路径预绑定 u16/u64/f32/f64 + `_norm_idx` 按组预构建**（`qzdb.py`）。
  此前只有 `'<I'` 做了模块级绑定；`safe_read_u16` 与 `_decode_native` 每次调用
  都重建 `Struct`。`_norm_idx` 此前每次 GeoInfo 构造都重跑 `_norm_key()`，
  现与 `_group_name_idx` 一样按组预构建一次（Node `_geoMetaCache` 同款）。

- **Node.js SDK：IPv4 解析改单遍扫描 + 有界解析记忆表，`find(ip_str)` 提速约 2.6x**（`ip-qzdb-sdk/nodejs/qzdb.js`）。
  动机：`find(str)` 的 ~104ns 里约 75ns 花在 `_fastParseIPv4`——原实现每个段要扫两遍
  （外层先定段边界，内层再逐位累加）。改为单遍扫描后解析本身 75.2→38.8ns。
  另删除 `fastParseIp` 里那趟独立的逐字符空白预扫描：它完全冗余——v4 单遍解析只接受
  数字与点，v6 的十六进制组校验 `(cc >= 128 || (_HEX[cc] === 0 && cc !== 48))`
  会拒绝 ord=32 等所有空白（PHP 侧此前已按同一理由删除）。
  再叠一层有界记忆表（16K 条、满则清空、记录经 `Object.freeze` 冻结以挡住外部改写
  污染缓存——`QzdbReader.parseIp` 是公开导出，命中返回的是同一个共享对象）。
  实测（M4 Max，std_china，6 万次）：`find(str)` IP 全不重复 104→39.4ns（2.6x）、
  Zipf 重复语料 103→43.5ns（2.4x）；`findUint(int)` 无变化（实测存在 JIT 双峰，
  前后都能测到 22ns 与 11ns，不作为收益）。
  备选方案已实测否决：正则式解析（形状正则 99ns / 全范围正则 103ns）**比逐字符循环更慢**，
  V8 对 charCodeAt 循环的 JIT 优于正则引擎。

- **PHP SDK：IPv4 解析改用 C 层原语，`find(ip_str)` 提速 2.38x**（`ip-qzdb-sdk/php/QzdbReader.php`）。
  动机：PHP 是全语言吞吐垫底（bench_reports 0.72M QPS，C 的 1/96）。基线实测
  `find(str)` 903ns 中解析占 619ns（69%）——`fastParseIpv4` 是 PHP 层逐字符解释循环。
  改为 `filter_var($s, FILTER_VALIDATE_IP, FILTER_FLAG_IPV4)` + `ip2long($s)`
  两个 C 调用，解析 619→83ns（7.5x），`find(str)` 903→380ns（2.38x）。
  **不使用 inet_pton**：实测 PHP 的 inet_pton **接受前导零**（`010.1.1.1`、`0177.0.0.1`
  均返回 4 字节），严格性必须额外手写；而 filter_var 本身即严格，少一个被漏掉的风险面。
  刻意不设 `FILTER_FLAG_NO_PRIV_RANGE` / `NO_RES_RANGE`——私有段与保留段
  （`0.0.0.0` / `10.0.0.1` / `224.0.0.1` / `255.255.255.255`）都是合法查询目标。

- **Rust SDK：`GeoInfo.pipe` 由 `String` 改为 `Arc<str>`，owned `find()` 提速 1.46x**
  （`ip-qzdb-sdk/rust/src/lib.rs`）。owned `find()` 在 geo 缓存命中时仍要
  `(*arc).clone()` 整个 `GeoInfo`，其中 `pipe: String` 每次克隆都要一次堆分配 + memcpy。
  `pipe` 是私有字段，`to_pipe()` 签名与返回值不变，**非破坏性改动**。
  实测（std_global，6 万次，release）：`find()` 141.5→97.1ns、`find_shared()` 78.8→63.2ns、
  `find_ref()` 149.3→139.0ns、`find_str()` 103.1→101.8ns。
  注：`values: Vec<Arc<str>>` 若要一并改为 `Arc<[Arc<str>]>` 可再省约 34ns，
  但属破坏性公开 API 变更，需 major 版本，本次未动。

- **Python SDK：IPv4 字符串解析改用 C 层原语，并新增有界解析记忆表**（`ip-qzdb-sdk/python/qzdb.py`）。
  动机：`find(ip_str)` 的 1208ns 里有 623ns（约 52%）耗在 `_fast_parse_ipv4` 的纯 Python
  逐段循环上——同一查询走 `find_uint(int)` 只要 320ns。
  做法：长度 / 段数 / 字符白名单 / 段非空 / 前导零全部改用 C 实现的 `str` 方法判定，
  数值与 0-255 范围交给 `socket.inet_aton`，Python 只做一次 `bytes → int`；
  inet_aton 原生宽松（接受 `1.2.3`、八进制 `010.1.1.1`、`0x7f.0.0.1`、空段），
  已由前置校验全部挡在调用之前，传入的必是「4 段非空十进制、每段 ≤255」，
  该语义在 glibc / BSD libc / musl 上一致，不依赖具体实现对宽松形式的宽严差异。
  另叠一层 `_fast_parse_ip` 记忆表（上限 16384 条、满则整体清空、峰值约 1.7MB），
  命中即跳过整段解析——真实业务 IP 高度重复，这一层收益最大。
  实测（M4 Max，Python 3.13，std_china，6 万次查询）：
  `find(str)` IP 全不重复的**最坏情况** 1144→878ns（1.30×，无回退）；
  Zipf 重复语料 990→317ns（3.12×，已触及 `find_uint` 的 328ns 下界）；
  `find_uint` 不变。

### Added

- `ip-qzdb-sdk/nodejs/ip_strict_test.js`：759 条表驱动断言（含空白对 v4/v6 逐位置注入），
  钉死单遍解析与「删除空白预扫描」后的严格性；已注册进 `run_all_tests.sh` 的 `Node-IpStrict`。
- `ip-qzdb-sdk/php/ip_strict_test.php`：119 条表驱动断言，钉死 `filter_var` 方案的严格性；
  已注册进 `run_all_tests.sh` 的 `PHP-IpStrict`。
- `ip-qzdb-sdk/rust/src/bin/owned_vs_shared.rs`：量化 owned `find()` 与零拷贝
  `find_shared()` / `find_ref()` 差距的内部基准。放在 `src/bin/` 是因为该目录已在
  `Cargo.toml` 的 `exclude` 中（内部工具不随包发布）。
- `ip-qzdb-sdk/python/test_ip_parse_strict.py`：107 条表驱动断言，钉死解析器的**严格性**
  （4 段 / 非空 / 纯 ASCII 十进制 / 0-255 / 禁前导零 / 禁 0x 与八进制 / 禁空白·SSRF 变体 /
  禁非 ASCII 数字 / 记忆表有界）。解析器改用 C 原语后，严格性不再由 Python 循环天然保证，
  必须由测试固化。等价性另有 12 万条随机 + 敌对语料的差分验证（0 差异）背书。

### Fixed

- `_fast_parse_ipv4('')` 由抛 `IndexError` 改为返回 `None`。该路径经 `_fast_parse_ip`
  （`n == 0` 先返回）与 `_fast_parse_ipv6`（空组已被拒绝）均不可达，属潜在脆弱点加固，
  无用户可见行为变化。

### 已实测否决（勿再提议）

- C# T11 归因过程中否决：Resolve 边界检查、批量路径大方法、仅摘 `AggressiveInlining`
  保留 unrolled。根因是「大 unrolled 方法 + 强制内联」的组合，详见 `docs/ROADMAP.md` T11。

## [2026-09-20] - Java 1.0.8

仅 Java 发版（Maven Central `com.qqzeng:qzdb` 1.0.7 → 1.0.8）。tag 形态为 `v-java-1.0.8`，
只命中 Maven Central 发布 workflow，不触发 PyPI / crates.io（PUBLISHING.md §0）。

### Fixed

- **Java SDK：JDK 22+ 的 mmap 释放不再依赖 `sun.misc.Unsafe.invokeCleaner`**（新增 `MmapSource`）。
  原实现在 `Snapshot.unmapIfMapped()` 里反射调用 `sun.misc.Unsafe.invokeCleaner`；该方法正随
  JEP 471/498 的 Unsafe 内存访问退役路线被移除，且实测在强封装 / `--illegal-access=deny`
  环境下**直接抛异常**——即"释放失败但静默"，堆外映射泄漏。现按运行时能力探测分流：

  | 运行时 | 映射 | 释放 |
  |:---|:---|:---|
  | JDK < 22 | `FileChannel.map` | 原 best-effort（行为不变） |
  | JDK ≥ 22 | `FileChannel.map(mode, off, size, Arena)` | `Arena.close()` 确定性 munmap |

  采用**单源码 + MethodHandles 运行时探测**而非 MRJAR：本项目 CI 与手工构建都是
  `javac $(find src -name '*.java')` 整树一次编译 + `--release 17`，MRJAR 的第二套源码根
  会让两份同名类一起进编译而直接失败。反射只发生在 open/close，查询热路径完全不碰。
  显式加 `Runtime.version().feature() >= 22` 门槛——JDK 21 上 `java.lang.foreign` 是 preview，
  反射虽可调通，但 preview 不保证跨版本稳定，不进生产依赖链。

  同时修掉实施中暴露的两个问题：`asByteBuffer()` 实际返回 `DirectByteBufferR`（它是
  `MappedByteBuffer` 的子类），故路径判定改为 `Snapshot.source != null`；
  `close()` 二次调用加 `released` 幂等守卫，避免对已释放缓冲再调 `invokeCleaner`。

- **构建/CI：`mvn test` 不再空转**。`surefire` 此前被 `skipTests=true` 关闭，CI 的 java-matrix
  也只编译不运行——Java 在 CI 上从未执行过任何用例。现把数据无关用例迁到 JUnit 5
  （`DataFreeCases` 为唯一定义源，`DataFreeUnitTest` 为 JUnit 入口），并让 java-matrix 在
  **17/21/25/27** 上真正执行。新增数据无关用例 P13（伪造 magic / 截断头部 / 空缓冲 fail-closed）。

- **CI 路径失效**：`publish-maven-central.yml` / `publish-pypi.yml` / `publish-crates.yml`
  仍有 7 处 `multi-lang/...`（该目录在两个仓库都不存在）→ 全部改为 `ip-qzdb-sdk/...`；
  `ci.yml` compile-gate 的 Java 步骤改走 `mvn test-compile`（测试源含 JUnit，裸 javac 会失败）。

- **`perf_gate.py` / `cross_lang_verify.py` / `cross_lang_verify_v6.py` 的 Java 腿**：
  原先整树编译 `java/src`，会连 JUnit 测试源一起编入而失败 → 收窄为只编 `java/src/main`。

### Performance

- 真实用法端到端（`find` + 8 个语义 getter）2.46 M → **8.18 M ops/s（≈3.3×）**；
  查询 QPS 与堆占用在噪声内持平。口径与数据见 `docs/JAVA27_JAVA_SDK_ASSESSMENT.md`。

### 发布前验证（全部通过）

| 项 | 结果 |
|:---|:---|
| 本地全量门禁 `run_all_tests.sh` | **24 passed / 0 failed / 0 skipped**（新增 Java-Unit 层） |
| 跨语言 pipe 对拍 | **441/441 一致**，8 语言（Python/Node/PHP/C/Rust/Java/C#/Go） |
| 跨语言 cidr + row_id 对拍 | **882/882 一致**，8 语言 |
| 跨语言 IPv6 对拍 | **48/48 一致**，5 语言 |
| `mvn test`（数据无关 JUnit 5） | **14 passed / 0 failed**，106 断言 |
| `QzdbReaderTest`（含私有数据） | **50 passed / 0 failed**，241 断言，`TEST_PASS` |
| 性能门禁（java 腿） | **11.34 M QPS**，floor 3.9 M → `PERF_GATE_PASS` |

## [2026-09-19] - PHP 1.2.0

仅 PHP 发版（Packagist `qqzeng/qzdb` 1.1.0 → 1.2.0）。tag 形态为**不带 `v` 的 `1.2.0`**，
不命中任何 `v[0-9]*` 发布 workflow，其余语言本次无版本变更（PUBLISHING.md §0）。

### 发布前验证（全部通过）

本轮改动了 PHP 的**输出路径**（`trim()` 字符集、`Fail-Closed` 闸门、分页 LRU），
而 PHP 输出受**跨语言逐字节契约**约束，因此必须重跑跨语言对拍。三个维度、8 语言全覆盖：

| 对拍脚本 | 覆盖维度 | 结果 |
|----------|----------|------|
| `cross_lang_verify.py` | pipe 输出（`find`） | **441/441** × Python/Node/PHP/C/Rust/Java/C#/Go |
| `cross_api_verify.py` | `lookup_cidr` + `lookup_row_id` | **882/882** × 同上 8 语言 |
| `cross_lang_verify_v6.py` | IPv6 | **48/48** × Python/Node/PHP/C/Java |

> 复现注意：`cross_lang_verify.py` 用裸 `php` 调用，而本机 PATH 里没有 php —— 需
> `export PATH="/opt/homebrew/opt/php/bin:$PATH"`，否则 PHP 会被静默 SKIP
> （输出 `[SKIP PHP: No such file or directory: 'php']`），**恰好跳过最该验证的那个语言**。
> 同理 C 腿需要 `export DEVELOPER_DIR=/Library/Developer/CommandLineTools`，
> 否则 `cc --version` 因 Xcode license 返回 69 而被跳过。

### 静态分析（PHPStan 2.2.14）

| level | 结果 |
|-------|------|
| 0 / 1 / 3 | **零错误** |
| 5 | 3 条（全部为冗余但无害的防御性检查，见下） |

首跑即抓到并已清理 **1 处真实死代码**：`QzdbReader::parseIpv6Raw()`。
它是 v1.1.0 修复 `lookupCidr` 的 IPv4-mapped 降级缺陷时留下的遗留——`lookupCidr`
改走 `fastParseIp` 统一分流后，该方法不再有任何调用方，但 docblock 仍写着
「仅用于 CIDR」。已删除（全仓库搜索确认无引用，含反射路径）。

剩余 3 条均为**刻意保留的防御性检查**，不是缺陷：

| 位置 | 警告 | 为何保留 |
|------|------|----------|
| `loadStream()` | `isset($stat['size'])` 恒存在 | `fstat` 失败时返回 `false`，已由 `$stat &&` 覆盖；显式 `isset` 防的是未来 stub 变化 |
| `loadStream()` | `$meta &&` 左侧恒真 | 上游已 `is_resource()` 校验；保留以防 `stream_get_meta_data` 行为变化 |
| `fastParseIp()` | `$gl === 0` 恒假 | 上游循环已拒绝空组；保留以防该前置校验被后续重构移除 |

> 若要给 CI 加 PHPStan 门禁：**level 3 可零配置直接上**；level 5 需为上述 3 条写
> `ignoreErrors`（带原因注释）或生成 baseline。

### Added

- **PHP 静态跨版本兼容性验证（PHPCompatibility）**。工具链：
  PHP_CodeSniffer **4.0.4** phar + PHPCompatibility(`develop`) + PHPCSUtils(`develop`)。
  两个必须知道的坑：PHPCompatibility 的 GitHub latest release 停在 2019 年的 **9.3.5**
  （10.x 只在 develop 分支）；且它要求 **PHPCS 4.x**（3.x 缺 `Tokens::EMPTY_TOKENS`，
  直接 Fatal）。另需把 `PHPCSAliases.php` 放到标准目录的**上一级**（ruleset 里写的是
  `./../PHPCSAliases.php`）。

  区间扫描结果：

  | `testVersion` | 结果 |
  |---------------|------|
  | `7.4-8.5` | 零问题 |
  | `7.4-8.6` | 零问题（修复 `trim()` 后；修复前 6 处错误） |
  | `8.0-8.6` | 零问题 |

  这条工具链把「本机没有 PHP 7.4、无法真机验证」从**能力缺口**降级为**已知残余风险**：
  静态层面已证明 SDK 未使用任何 7.4 之后才有的语法 / 函数 / 常量 / ini 指令。
  真机验证仍建议由 CI 矩阵补（见下条）。

- **发布仓库 CI 新增两个 PHP 门禁**（`.github/workflows/ci.yml`，手工维护，不随
  `sync_to_github.py` 覆盖）：
  - `php-version-matrix`：PHP **7.4 / 8.0 / 8.1 / 8.2 / 8.3 / 8.4 / 8.5** 七档矩阵，
    每档跑 `php -l` + 在 `error_reporting=E_ALL` 下做类加载，**出现任何
    deprecat / warning / notice / fatal 即判失败**。历史教训：`offsetGet()` 弃用告警
    发生时全部功能测试仍是绿的，只有 `E_ALL` 类加载能抓到它。
  - `php-compatibility`：PHPCompatibility `testVersion 7.4-8.6`。上限取 8.6 而非 8.5，
    因为 `composer.json` 的 `>=7.4` 不设上限，而 8.6 恰好改变了 `trim()` 默认字符集。

- **PHP SDK：`declare(strict_types=1)`**。PHP 的 `strict_types` 是 **per-call-site** 语义——
  只约束 `QzdbReader.php` **文件内部**发起的调用，不改变消费者调用本 SDK 公开方法时的宽松
  转换，因此对使用者零破坏。实测 7 套测试全绿；热路径无性能代价（std_china `find`：
  2.49–2.57M QPS，与关闭时同处噪声区间，各跑 3 轮）。

- **PHP SDK：流式/低内存模式的 Fail-Closed 完整性闸门**（`streamPage()`）。
  流式句柄打开后若底层文件被截断/替换（NFS 抖动、存储故障、被其它进程重写），此前
  `readBytes()` 短读返回 `''` 会被 `poolString()` 当作"空字段"继续拼接，产出
  **地理字段正确、ISP/ASN 字段为空的伪命中记录**。用未截断库做基准逐条对拍实测
  （`max_global` 截断到 35% 后查 6000 次）：5582 次抛异常、**329 次返回与基准不符的记录**。
  现改为：页起始位置本应落在 `fileSize` 内、却读不满一整页且未抵达文件末尾 ⇒ 抛
  `QzdbException(ERROR_CORRUPTED)`。真·越界（`pageOffset >= fileSize`，或短页恰好收在
  EOF）仍保持返回空串的原语义。修复后错值 **329 → 0**，异常 5582 → 5966。

- **PHP 黄金测试前置条件门禁**（`tier2_golden.php`）。新增 `goldenPrecondition()`：
  向量文件不存在 / 不可读 / JSON 非法 / 空对象 / 引用了无对应数据库的库 / 最终
  `TOTAL === 0` 六类情形一律硬失败（exit 1）。此前这些情形会输出
  `TOTAL=0 FAIL=0` + `TIER2_OK` 并 **exit 0**——数据路径写错、CI 未检出向量文件都会被
  伪装成"黄金校验通过"。空跑通过比失败更危险。四种故障场景已逐一实测退出码为 1。

### Changed

- **PHP SDK 流式/低内存模式查询吞吐提升 5.8–18.2×**（`QzdbReader.php`）。
  分页缓存由「单页 256KB」改为「**有界多页 LRU**」（`STREAM_PAGE_SIZE` 64KB ×
  `STREAM_PAGE_CACHE_PAGES` 64 页 = **4MB 硬上限**）。根因：一次 `resolveGeo` 要触达
  Trie 跳表、节点表、IP 行表、geo 条目表以及多张池的偏移表与字符串区，这些区域在文件里
  彼此远离，单页缓存下几乎每次访问都退化成 `fseek`+`fread`。实测（50k 次随机 IPv4）：

  | 库 | 缓冲模式 | 流式（修复前） | 流式（修复后） | 提升 |
  |----|----------|----------------|----------------|------|
  | `std_china` 8.2 MB | 2.28M QPS | 112K QPS | 652K QPS | 5.8× |
  | `max_global` 111.7 MB | 0.37M QPS | 1.5K QPS | 25.7K QPS | **16.4×** |
  | `ult_global` 116.6 MB | 0.78M QPS | 0.97K QPS | 17.7K QPS | **18.2×** |

  定位过程（三组补丁分别验证）：池偏移表常驻内存仅 +40%（非主因）；16 页 LRU +296%
  （主因确认）。顺带否证「加大页」方案——256KB→1MB 反而从 1617 掉到 457 QPS，
  因为每次缺页拷贝成本变大而缺页次数未减少，**必须是多页而非大页**。
  淘汰用插入序队列（刻意不用 PHP 7.3 才有的 `array_key_first()`，与 7.4 下限一致）。
  缓冲模式（`$this->data !== null`）路径零改动。**等价性对拍：20,019 条输入 × 4 个库，
  MISMATCH=0**（含非法 IP / 边界地址 / CIDR 反查 / 全部元信息接口 / `toJson` / `toPipe`）。

- **PHP 最低版本声明由 `>=8.1` 下调为 `>=7.4`**（根 `composer.json` +
  `tools/publish_meta/composer.json`）。此前 README 承诺 7.2+ 而 `composer.json` 声明
  `>=8.1`，**Packagist 只读 `composer.json`**，导致 7.2–8.0 环境 `composer require` 被直接
  拒绝。经 `token_get_all()` 逐项核对全部 56 个内置函数调用，SDK 无任何 PHP 8 专属语法，
  静态最低门槛为 **PHP 7.1**（`private const` / `?Type` / `iterable` / `void` /
  `unpack` 带 offset）。取 7.4 作下限以避开已 EOL 的 7.2/7.3。
  同时补上被遗漏的扩展依赖 `ext-filter`（`filter_var`，IPv4 校验）与 `ext-hash`
  （`hash_init('crc32b')`，CRC 校验）——原文档写「仅需 Core / json」，但 `json_encode`
  实际未被使用。已过 `composer validate --strict`。

### Fixed

- **PHP SDK：`trim()` 隐式默认字符集导致跨版本行为漂移（PHP 8.6 隐患）**。
  6 处裸 `trim($s)` 改为显式传入字符集常量（`UsageType::TRIM_CHARS` /
  `QzdbReader::TRIM_CHARS = " \n\r\t\v\x00"`，即 PHP 8.6 **之前**的默认值）。
  PHP 8.6 起 `trim()` 默认字符集新增换页符 `\f`（0x0C），裸调用会让同一份输入在
  8.5 与 8.6 上产生不同结果——破坏跨版本行为确定性，而跨语言逐字节契约正建立在此之上。
  显式写死后在 ≤8.5 上是**纯 no-op**（字符集与默认值完全相同），全量 7 套测试无变化。
  由 PHPCompatibility 检出，详见下方「静态兼容性验证」。

- **PHP SDK：PHP 8.1+ 每次类加载抛 `E_DEPRECATED`**（`GeoInfo::offsetGet()`）。
  `ArrayAccess::offsetGet()` 在 8.1+ 声明了 `mixed` 返回类型，缺声明即触发弃用告警；
  写 `: mixed` 会把最低版本抬到 8.0，与 7.4 下限冲突。恢复 v1.1.0 已发布版本中存在的
  `#[\ReturnTypeWillChange]`——**独占一行的属性在 PHP < 8.0 会被整行当作行注释忽略**
  （`#` 是行注释符，见 php.watch/versions/8.0/attributes），是唯一两头兼顾的写法。
  修复后 `E_ALL` 下零告警输出。开发环境（`display_errors=On`）此前该告警会直接打进
  响应体，可能污染 JSON 输出。

- **PHP `tier1_test.php` 兼容门禁判据错误**。原判据为 `strpos($source, '#[') === false`，
  注释称「PHP 8 属性语法会在旧版本解析阶段直接失败」——该前提不成立：只有**行内**属性
  （`f(#[A] $x)`）才 Parse error，且 `#[A] function f(){}` 虽不报错但会把整行注释掉，
  造成**静默的函数丢失**。新判据只拦截「属性前后还有其它代码」的行，允许独占一行的单行
  属性。正反向自检通过（注入行内属性可正确报 FAIL 并给出行号）。

- **PHP `test.php` 死代码**：`if (version_compare(PHP_VERSION, '8.1.0', '<')) { $m->setAccessible(true); }`
  写在 `$m` 定义之前，在 PHP < 8.1 上会因未定义变量中断；且
  `ReflectionMethod::setAccessible()` 自 8.1 起已是空操作（8.5 起本身被弃用）。已删除。

- **PHP 文档版本表述统一**：README 第 8/37 行的「推荐 8.1+」与第 623 行的「建议 8.2+」
  不一致，统一为「最低 7.4 / 推荐 8.2+」；扩展依赖说明由「Core / json」更正为
  「Core / filter / hash」；流式性能说明（原文称"每一步子节点读取都对应一次
  fseek+fread 系统调用"）与内存模式章节按本轮实测数据重写。
- **`composer.json` 的 `branch-alias` 修正**：`dev-main` 由 `1.0.x-dev` 改为 `1.2.x-dev`。
  原值落后两个 minor（最新 tag 已是 v1.1.0，本次发 1.2.0），导致 `dev-main` 在
  `^1.2@dev` 类约束下无法解析。

### 已知限制（未解决，非本次回归）

- **PHP 7.4 尚未真机跑过**：本机只有 PHP 8.5.10。本次已用 PHPCompatibility 做静态
  区间验证（7.4–8.6 零问题），并把 `php-version-matrix`（7.4–8.5 七档）加进发布仓库 CI，
  但**矩阵的首次真实运行要等这次推送触发后才有结果**。在 CI 首次全绿之前，
  `>=7.4` 应视为「静态已验证、运行时待确认」。
- **`toJson()` 与 `json_encode` 存在两处行为差异**：不转义 `/`（`json_encode` 默认输出
  `<\/script>`，因此可安全内联进 HTML `<script>` 块）；非法 UTF-8 字段值会产出无效 JSON
  （`json_encode` 此时返回 `false`，至少失败得响亮）。两者都受**跨语言逐字节契约**约束，
  需 8 个语言一起改并重跑跨语言对拍，属独立议题。

## [2026-09-07] - Rust 2.0.0 / PHP 1.1.0 / C# 1.0.8 / Java 1.0.7 / Python 1.0.6 / Go 1.0.6 多平台集成版发布

### Added

- **CI 性能门禁（perf-gate job）**：`ip-qzdb-sdk/tools/perf_gate.py` + C/Go/Rust/Node/Python 五语言驱动器，基于公共 demo 样本（数据无关、可在托管 runner 运行）。绝对下限（floors）拦截数量级回退，对 runner 硬件代际免疫；`--baseline --tol` 支持本地细粒度对比。挂入 `.github/workflows/ci.yml`（产出 30 天 perf 报告 artifact）。
- **API_CONTRACT 升级 v2.5**：新增 §二.3 字段投影语义（对齐 Java golden：未知字段补空串/保留重复/全未知返回非空）、§二.4 零拷贝共享查询（Rust `find_shared`/`find_ref`/`ToIp` 扩展层 + 逐字节 parity 强制约束）、§五 已裁决行为口径登记（跳表哨兵/空白字符/getScope/dimensionMask 双位/Go finalizer 生命周期/性能基线 7 条）。
- **CSV 新鲜度检查**：`tools/csv_freshness_check.py` 逐 edition 抽样对比 CIDR 真值与 DB（advisory；上游生成器改为同步产出 CIDR CSV 后可用 `--strict` 升级为硬门禁）。实测确认 10 个数据集过期（与 Node tier2_csv_verify 的 86618 偏差判别一致，属数据层问题）。
- Rust SDK 1.0.7（crates.io 发布准备）：
  - `ChainedReader::find_ref` 在 Merge/MergeOverride 模式改为 panic（原静默 `None` 会被调用方误判为"IP 未命中"；API_CONTRACT §二.4）。
  - 代码审查修复：两处 unsafe 补 SAFETY 注释并收敛；`resolve_geo_ref` 加 `debug_assert!(fc <= MAX_GEO_FIELDS)` 拦截超限静默截断；`tests/zero_copy_ref.rs` 数据缺失改硬失败；新增 **reload 存活测试**（持有 GeoInfoRef 跨热更新旧引用必须原样可读——unsafe 借用延长的行为级验证）与 find_ref_bytes 非法长度边界。

### Changed

- **Rust SDK 2.0.0（破坏性）：`GeoInfo.values` 改为 `Vec<Arc<str>>`**。
  动机：owned `find()` 每查询克隆 29 个 `String`（29 次堆分配 + 29 次释放），
  是大库随机查询距 C 的根因；`Arc<str>` 池直供后，owned 克隆变为 1 次 Vec
  分配 + 29 次原子引用计数增量。实测（M4 Max，contract 契约基准）：
  max_global hot.mixed 3.17→8.52M(+169%)、random 2.73→4.42M(+62%)，
  std_china random 41.8→55.4M(+32%)、hot 46.0→61.5M(+34%)。
  **迁移**：直接索引比较 `info.values[0] == "x"` 改为
  `info.values[0].as_ref() == "x"`，或改用 `get()` / `to_pipe()` / `to_map()`
  （签名不变）；`values()` 字段读取类型随字段变更。其余公开 API（`get` /
  `to_pipe` / `find` 族 / `find_shared` / `find_ref`）签名与输出逐字节不变
  （`tests/zero_copy_ref.rs` parity 全绿）。

- **Rust 解码缓存自适应容量（修正并行 agent 的一刀切方案）**：并行 agent 将缓存从 2^14
  一刀切扩至 2^18——max_global random +46~64% 属实，但 std_china random.v4 一度 -28%
  （2MB 缓存阵列污染小库热路径），且其配对的 pools Arc<str> 改造为**无效优化**
  （GeoInfo.values 仍为 Vec<String>，build_geo 的 to_string() 照样逐字段堆分配）。
  修正为按快照实际条目总数自适应：slots = next_pow2(条目数×2)，钳制 [2^12, 2^18]
  （std 10.5K 条目→2^15/256KB；max 全球库→2^18/2MB）。实测：max_global random
  1.69→2.73M(+62%)、real_world 1.73→2.99M(+73%)，std_china 全指标回到基线噪声带内。
  **剩余瓶颈已定位**：owned find() 每查询 clone 29 个 String（缓存命中也照付），是
  max random 距 C(13.7M) 的根因——解锁需 GeoInfo.values 改 Vec<Arc<str>>（公开 API
  破坏性变更，待定版本决策）。
- **PHP（find_str +28~35%）**：分阶段剖析证实字符串解析占 findStr 的 93%（1250/1336ns，走查仅 86ns）。`fastParseIpv4` 重写为 `explode`+`strspn`（C 级，逐条语义等价：恰 4 段/段长/禁前导零/数字白名单/≤255）；`fastParseIp` 删除逐字符空白预扫描（空白必被下游校验拒绝，对合法地址白付一趟）；v6 组校验改 strspn 十六进制白名单、组值改 `hexdec`；v4 组显式从来源数组弹出——旧实现 v4 点分串残留在 rg/lg 被当作 hex 组读入，恰好写入 buf[12..13] 后被结尾 v4 块覆写而侥幸正确（未定义式巧合），现已彻底消除。bench STRING：std_china 182K→250K qps、max_global 51.5K→55.2K。49 个解析语义用例（v4 19 + v6 15 + v4-in-v6 15）逐条核对，与 Python 裁决一致。
- **CI 性能门禁覆盖补齐至 8/8 语言**：新增 `perf_gate_php.php` / `perf_gate_java/PerfGate.java`（现场 javac 全 SDK 树，JAVA_HOME/bin 优先——CI setup-java 与本机 homebrew 布局均无 PATH javac）/ `perf_gate_cs`（SetTargetFramework 单 TFM 引用，避免为门禁编译库全部 4 个目标框架）三驱动器（协议与既有五腿一致：逗号 IP argv + best-of-3 + 全未命中哨兵）；FLOORS 按本机真实值 ~1/10 标定（php 40K / java 1M / netcore 1.5M）；ci.yml perf-gate job 补 setup-java(temurin 21)/setup-dotnet(10.x)/setup-php(8.3)，`--langs` 默认扩至 8 语言。实测 php 429K / java 12.0M / netcore 14.1M qps。
- **【测量缺陷修复】CI perf gate 的 C/Go/Rust 三腿自创建以来一直在测「非法 IP 快速失败路径」**：perf_gate.py 误将 JSON 数组文本（`["1.2.3.4",...]`）当作逗号分隔裸 IP 串传给三个驱动，切分后每段带引号/方括号全部判非法——此前报告的 c 306M / go 113M / rust 389M qps 均为非物理数字（真实查询不可能 <3ns/op），仅 node/python（内联 JSON 数组，形态正确）测的是真实查询。修复：`_ips_csv()` 供 argv 形态；四驱动补全未命中哨兵（sink≤0 或 0 命中即报错，C 原占位检查 `sink == -1` 永假且其 sink 累加的是恒为 0 的成功返回码）；FLOORS 按真实数值重校准（c 2M→500K、go 1M→800K、rust 2M→150K，node/python 维持）。`perf_baseline_reference.json` 同步以真实数字重写。
- **【find_str 全语言管道预编码】**对齐 C# 既有 `_pipe` 缓存模式，修复后真实基线下 find_str 是各语言与 uint API 差距最大的路径：
  - **Rust（5.6×，1.48M → 8.33M qps）**：根因是 owned 路径每次调用 `(*a).clone()` 整个 GeoInfo（29 字段 = 29 次 String 分配），即使缓存命中也照克隆。`GeoInfo` 新增 `pipe` 字段（`build_geo` 解码期一次性预编码），`find_str/find_str_ip` 改走 `find_shared/find_shared_ip` 免克隆路径 + 预编码直取；投影/合并路径留空走回退 join，`zero_copy_ref` 逐字节 parity 守卫通过。探针实测单次 find_str 650 → 123 ns。
  - **C（2×，5.2M → 10.68M qps）**：`qzdb_cache_entry_t` 新增 `pipe`（构建条目时一次性连接，OOM 降级 NULL 回退不变，随条目释放——leak_regress delta=0）；`qzdb_find_str` 快路径经 dimensionMask 选维直探缓存 pipe 单次 memcpy，错误码与截断语义逐项对齐慢路径。
  - **Go（FindStr 微基准 -35%，97 → 63 ns/op）**：`GeoInfo.pipe` 解码期预编码，`ToPipe` 零分配直返；投影/合并按次构造的实体走回退现场计算（不回写，共享实体免锁）。contract bench 的 string_roundtrip 段实测的是 Find 结构体 API 不含 ToPipe，该指标不受影响。
  - **Java**：`toPipeString` 惰性记忆化（非 volatile 单引用惰性写，String.hashCode 同型安全模式）；公共构造器对数组做防御性拷贝（与 `values()` 克隆语义对齐，亦是记忆化正确性前提）。
  - Node/Python/C# 上轮或既有已记忆化；PHP 的 STRING/uint 比值 1.2×（瓶颈在对象机制非管道），不动。
- **Go**：解码缓存键 rowID → entryId（同一 GeoEntry 被 N 个相邻 CIDR row 共享时只解码一次占一个槽，命中率提升；对齐 Java/C#/Node 语义）；`fastParseIp` 改值返回（21 字节结构体走栈，热路径零堆分配）；dimensionMask 双位（畸形文件）选维对齐 Java 优先级链 asn > usage > geo。
- **Go（安全审查 P1）**：GROUP_SCHEMA 字段偏移加载期校验 `offsets[fi] + width <= stride`，越界整组回退默认布局——此前畸形文件可让查询期触发不可 recover 的 boundsPanic。
- **C#**：`BuildGeo` 原生浮点旁路（解码时同步保留 double，`GetLongitude/GetLatitude` 免 `"116.400000"` → TryParse 往返；字符串契约形态不变）；退役快照释放改为 GC 可达性模型（移除一代隔离环：查询栈 root 住 Snapshot 时绝不 unmap，与 Go finalizer/Rust Arc 同模型，消除快速 Reload 与慢查询并发的 AccessViolation 窗口）。
- **C# ToJson 投影路径补 numeric 标记**（与 Go 修复同款跨语言一致性）。
- **Node.js（perf）**：`toPipe()` 构造期预编码并随对象冻结（对齐 C#/Python `_pipe` 缓存语义，重复查询零分配）；跳表与 32 位节点段 `Uint32Array` 视图直查（视口 4 字节对齐 + 文件 <2GB + 小端三条件守卫，否则回退原 safeRead 路径；24 位节点段不动）；V6 走查预读 4×BE u32 成 word 数组取位（对齐 C# ulong hi/lo）。`perf_gate` find_str **1,069,894 → 5,406,522 qps（5.05×）**。Node 版本 1.0.5 → 1.0.6。
- **Python（perf）**：V4/V6/CIDR 走查 while+steps 计数改有界 `for range`（合法 trie 步数天然有界，可观测行为不变）；`struct.Struct('<I').unpack_from` 模块级预绑定；GeoInfo 缓存键 `(group, entry)` tuple 改整型键 + 单次 `dict.get`。`perf_gate` find_str 263,929 → 268,745 qps（+1.8%，cProfile 证实解释器瓶颈均摊、无单点热点）。Python 版本 1.0.5 → 1.0.6。
- **负结果存档（防后人重蹈）**：V6 走查 hi/lo 两相位拆分在 Go（±0%）、Rust（-10%，三轮 A/B）、C（+1%，噪声内）均无收益——编译器对逐字节取位形态已优化到位，维持原实现；Go 解码缓存 2^18→2^16 缩容实测 hot.mixed **-10~13%**（触碰 BENCH_CONTRACT §9 门禁），撤销。候选淘汰：Python `memoryview.cast('I')`（仅 +10% 但引入 mmap BufferError 生命周期风险）、Python V6 hi/lo 拆分（CPython PyLong 双字位移已高效，0.96×）。

### Fixed

- **跨 API 验证层补齐至 8/8 语言（882/882 全过）**:`cross_api_verify.py` 接入
  Go / Rust / C#(此前只覆盖 5 语言 / 504 对比)。三个 batch 二进制新增
  `--cidr` 模式,输出改为 `key|cidr|row_id`——pipe 串自身以 `|` 分隔无法追加
  字段,故整行切换格式(同源注释三份一致)。首跑即抓到 2 处真实分歧(见下)。
- **Go batch runner 未对 IPv4-mapped 降级(L1 缺陷,P1)**:`cmd/batch_go` 的
  v6 路径用 `FindV6Uint`(纯 v6 走查),而 `::ffff:a.b.c.d` 按契约 §5.2 必须
  降级走 V4 Trie——V6 trie 存有 `::ffff:0:0/96` 保留行,不降级会返回
  "保留地址/Reserved" 而非真实归属地。改 `FindBytes`(与 Python/C#/Rust/PHP
  runner 一致)。此缺陷在 pipe 层(cross_lang_verify)实测暴露:
  `::ffff:114.114.114.114` Go 独错,其余 7 语言正确。
- **Rust `trie_walk_v6` 跳表哨兵误报深度 0(P1,与上轮 Python 同类)**:
  CIDR 反查在 v6 跳表哨兵命中时直接返回前缀长度 0,实测
  `fe80::1` → `::/0`(真值 `fe80::/10`)。改从根重走 `[0, jump_bits)` 求真实
  前缀,与 Go `lookupV6PrefixLen` / C `lookup_v6_prefix_len` 逐字对齐
  (`trie_walk_v4` 早已如此,本轮补齐 v6 对称面)。回归测试
  `tests/jump_sentinel.rs::v6_cidr_jump_sentinel_reports_true_prefix`。
  注:find / lookup_row_id 路径仍按 §4 直接返回哨兵 row_id(不改,
  既有 `v6_jump_sentinel_returns_leaf_row_directly` 测试守护)。
- **`cross_api_verify.py` Java 腿在非交互 shell 静默降级(P1,工具)**:只认
  `JAVA_HOME`/裸 `javac`,而 Homebrew JDK 不在该 PATH——Java 腿整体缺席却
  仍以"通过"收尾(4 语言 378 对比冒充全量)。改复用
  `cross_lang_verify._find_java_home`(与 run_batch_test_suite 同一探测)。
- **新增跨 API 一致性验证层 `cross_api_verify.py`(审计基建)**:
  同一 IP 横向比对 `lookup_cidr` + `lookup_row_id`(此前 cross_lang_verify 只比
  pipe,/32 类错误不可见)。首跑即在 4 语言间抓到 26 处真实分歧,全部修复后
  全过(Python 参照 + Node/PHP/Java/C,63 IP 含 mapped 降级与
  /32 单段)。TEST_IPS 补 V4-mapped 降级路径(`::ffff:` 前缀 2 条)。
- **PHP `lookupCidr` IPv4-mapped 降级失效修复(P1)**:`parseIpv6Raw` 的
  "不降级返回 16 字节"前提被解析期 mapped 降级(§8 规则 4)破坏——mapped
  地址在此永远拿不到 v6 字节,lookupCidr 的 mapped 查询恒为 null。
  改走与 find() 相同的解析分流;非 mapped 路径逐字节不变。
- **Python `lookup_cidr` 跳表哨兵前缀错误(P1,v4+v6)**:跳表级哨兵
  (短于跳表位数的范围,如多播 224.0.0.0/4、链路本地 fe80::/10)的 CIDR
  前缀误报为跳表深度。改从根重走恢复真实前缀(对齐 C/Go/Node 设计)。
  实测:224.0.0.1 /16→/4、fe80::1 /20→/10,与真值 CSV 一致。
- **C `format_v6_cidr` 尾部空压缩缺 NUL(P1,UB)**:零压缩覆盖到末尾时
  (如 /37 的 `::`)tmp 无 NUL 终止,`%s` 读越界栈内存输出垃圾字节
  (实测 `2408:8000:9000::1` → `2408:8000:9000::p:G.../37`)。补 `tmp[p]='\0'`。
- **Java `CrossApiProbe` 探针**(验证基建):临时目录编译,不污染源码树。

- **C# 语义 Getter 缺失字段误命中首字段（P0,准确性行为修复）**:
  `BindStandardIndices` 用 `map.TryGetValue(key, out idx)` 绑定 20 个语义字段索引,
  TryGetValue 缺键时 out 被置为 default(int)=0,而 Getter 以 `_idxX >= 0` 判存在——
  **字段不存在的 edition 上语义 Getter 返回 values[0]（首字段值）而非空串**。
  影响:std 版(6 字段)GetDistrict/GetLongitude/GetLatitude/GetAsn/GetTimezone/
  GetAsName 等返回大洲值;与契约"缺失字段返回空串"相悖,且其余 7 语言均正确
  (Java/Python/Node/PHP/Go/Rust 的 get(name) 模式天然安全,C# 是唯一的
  pre-bound index 实现)。修复:IndexOf 助手缺键返回 -1;Getter 回落 Get(name)
  返回空串/null。Tier1 新增 8 条缺失字段回归断言(std 版 GetDistrict==""
  等——修复前该断言会失败,即回归证明)。
- **Go 维度掩码兜底推断跨组误判（P1,多组文件边界）**:
  dimMask=0 的兜底推断用读取组单一的 `normalizedMap["asn"]` 判定**所有** group——
  多组 geo/asn 混合文件会跨组误判(读取组含 asn 则全部组误标 0x02)。
  Python/C#/Java/Rust/Node/PHP 均为按组字段名推断,Go 是唯一例外。
  修复:字段名派生提取为 `deriveGroupFieldNames(g, ...)` 按组执行并存入
  `groupFieldNames [][]string`,兜底推断改按各组自己的名字判定;读取组的
  fieldNames/fieldNamesSource 逐字节保持与旧实现一致(10 个商业库均为单组
  且掩码显式,零行为变化;多组 dimMask=0 夹具测试待多组合成库生成器支持)。

- **Java 土耳其语 I 硬化 + 代码卫生(并行 agent 产出,已验证收编)**:
  - `KnownUsageType` 建表与 `fromRaw` 改 `toLowerCase(Locale.ROOT)`——原实现两侧
    同变换虽内部自洽,但依赖 JVM 默认 locale(类加载期与调用期若环境不同或
    `Locale.setDefault` 被宿主变更即分叉);实证 tr-TR 下修复后 6/6 命中;
  - `QzdbReader` 删除死字段 `loadedFile` 及其级联死变量 `fileRef`(唯一赋值点
    随之失效)、重复孤儿 Javadoc;`QzdbReader`/`ChainedReader` 加 `final`
    (均无 public 构造器以外的扩展设计);`QzdbRegistry` 删除冗余空构造函数
    (隐式构造等价);
  - `javac -Xlint:all` 0 错误;TEST_PASS(P6 UsageType 21 场景)、
    FAILCLOSED 29/29、tr-TR 实证 6/6。 Turkish-I 审计同时确认其余 7 语言
    归一化均为 locale 无关(C# OrdinalIgnoreCase、PHP 两侧同变换、
    Go/Node/Python/Rust 原生 Unicode 折叠、C 无归一化路径)——Java 是唯一暴露点。

- **Java 契约基准落地（8/8 语言基准集补齐）**：新增 `BenchContract.java`（splitmix64 /
  四分布 / 双栈三模式 / 冷热 / 分位数 / 1-16 线程扩展 / 16×10 万并发门禁），
  FNV-1a 指纹对拍 12/12 流与其他 7 语言逐字节一致。首份权威数据（M4 Max）：
  std_china random 48.1M / hot 64.3M，max_global random 13.9M，STRING 2.0~3.1M。
- **C `qzdb_find_each` 结果泄漏修复（P1）**：缓存未命中时 `get_geo_info` 的堆字符串
  从未释放，每条未命中查询泄漏一次；现回调后 `free_geo_info`（缓存命中时为空操作）。
- **C `apply_group_meta` OOM 原子切换**：先分配新资源再释放旧资源（含逐项回滚），
  消除 OOM 中途读者处于新旧不一致状态的窗口。
- **C# 组表数量不一致容忍化（对齐 7 语言）**：`tableGroups != gCount` 由抛异常改为
  `min(tableGroups, gCount, 4)` 截断。
- **C `to_pipe` 补 group_index 边界防护**。
- **Node/PHP 契约基准自身降噪**：Node v4 流改 Number 直传（免 BigInt/Number 混转）
  与 v6 预分配 Buffer 复用（FNV 对拍 12/12 不变）；PHP gmp 常量预计算。

- **C /32 CIDR 网络地址错误（P1,跨语言比对发现）**:`format_v4_cidr` 将 `n<=0` 与 `n>=32`
  合并短路为 mask=0,单 IP 段(如 114.114.114.114/32)被输出为 `0.0.0.0/32`;其余 7 语言
  均正确(C# 用 uint 移 0、Java 用 long 移位、Rust n==0 分支)。现对 n==32 显式取全掩码;
  test_main.c 补 `/32 == ip 本身` 回归断言(旧断言仅检查含 '/',放过了该 bug)。
- **C chain 元数据数组静态存储竞态(P2)**:`qzdb_chain_editions/scopes/data_months` 使用
  函数级 `static` 数组,多 chain/多线程并发调用互相覆盖;改为 per-chain 存储。
  生命周期语义变化:返回指针 valid until `qzdb_chain_free`。
- **6 语言 TLV Type 2 字段表逗号回退(对齐 Java golden)**:字段名表仅一段时按 ','
  再分割(C/C#/Node/PHP/Python/Rust;Java 的 splitFieldNames 本就支持)。
- **C# GROUP_SCHEMA 组数不一致由抛异常改为容忍截断(对齐 Go/Rust/C)**:畸形文件
  行为从 fail-closed 收敛为与其余语言一致的 min(groups) 容忍解析;hostile 全套通过。
- **Go buildSnapshot 移除 storedCrc 重复赋值**(parseHeader 已设置);PHP 移除重复
  越界分支;Rust to_pipe/build_geo 管道拼接改 with_capacity 预分配(消除增量扩容)。
- **C qzdb_geo_info_t 新增 value_count 字段**(防御性):记录 values[] 有效条数,
  to_pipe 优先使用(现全路径与旧回退值相等,无行为变化;结构体尺寸 +4,源码分发
  重编译即可)。

- **C（安全审查 P1）**：GEO_ENTRIES 组元数据表加载期校验实际读取字节数（1 + groups×7）——此前仅校验 16 字节，畸形文件可使 mmap 路径越页 SIGBUS。
- Go `FindFields` 投影结果补 numeric 标记（此前 `ToJson` 把 longitude 输出为字符串，与 C#/PHP 分叉）。
- **C**：`qzdb_lookup_row_id_uint/_v6` 补 `!ctx`（及 `_v6` 的 `!ip_bin`）空指针防护，与兄弟函数一致——NULL 入参不再解引用崩溃；`fuzz/boundary_test.c` 新增 NULL-guard 回归探针（CI ASan 门禁覆盖）。caller-buffer 家族返回值计数约定（>0 计数 / 0=OK 未命中 / <0 错误码）在 `qzdb_reader.h` 文档化；`test_main.c` 修 buf 元素尺寸违约（64B → QZDB_VALUE_BUF_SIZE）。
- **C#**：trie 跳表/节点 8 处 `(uint*)` 强转解引用改 `Unsafe.ReadUnaligned<uint>`——分区 64B 对齐是构建方约定而非加载期校验项，敌意文件的未对齐偏移原属形式 UB；JIT 仍发射单条 mov，热路径零开销。
- **Rust 1.0.8**：group field count > MAX_GEO_FIELDS(64) 由查询期 `fc.min(64)` 静默截断改为加载期 fail-closed（`ErrorCode::Unsupported`）——release 构建丢字段、to_pipe 域数与 C#/Java 分叉；`tests/failclosed.rs` 新增加载期拒绝回归。
- **Go**：`buildNormalizedMap` 每字段单次归一化（原同一行调用两次 `normalizeKey`，加载期白做一半）；geoCache 注释过期键名（row_id → entryId）修正。
- **Java**：pom 1.0.5 → 1.0.6 对齐 Maven Central 已发布版本——`sync_to_github.py` 原样拷贝 pom，漂移会把发布仓版本回退、阻塞下个 Maven 版本。
- **tools/release.sh**：版本解析改为 BSD/GNU 通用 sed——旧 `grep -oP` 双重失效（-oP 是 GNU 专有 + lookbehind 模式在现行契约里不存在），任何平台都恒回退 `0.0.0` 生成 `release: v0.0.1` 提交。

## [1.0.6] - 2026-09-02

### Added

- Rust SDK 1.0.6：
  - **零拷贝借用视图 `GeoInfoRef<'a>` 与 `find_ref` 系列 API**：引入零堆分配查询（Zero Allocation），字符串字段直接借用只读快照底层 `&'a str`，包含 `find_ref`、`find_ref_v4`、`find_ref_v6`、`find_ref_bytes`、`find_ref_ip`。
  - **人体工程学强类型 IP 查询接口（`ToIp` Trait）**：支持 `std::net::IpAddr`、`Ipv4Addr`、`Ipv6Addr`、`&str`、`u32`、`[u8; 16]` 原生直接查询（`find_ip` / `find_ref_ip` / `lookup_row_id_ip`）。
  - 116 项单元/对拍/Fuzz/并发回归测试与 0 warning clippy 审查保障。

### 1.0.6 发布链路补遗

#### Added

- Rust SDK **已发布到 crates.io**：`cargo add qzdb`（1.0.5，2026-08-29 上线，<https://crates.io/crates/qzdb>）。crate 名 `qzdb_reader` → **`qzdb`**（与 PyPI 的 `qzdb` 对齐；C 语言的 `qzdb_reader_t` / `qzdb_reader.h` 不受影响）；`Cargo.toml` 补全 crates.io 必需的 `description` / `license` / `repository` / `homepage` / `documentation` / `keywords` / `categories` / `rust-version=1.74`；新增 `ip-qzdb-sdk/rust/LICENSE`；新增 `.github/workflows/publish-crates.yml`（tag `v-rust-*` 触发，先 dry-run 后 publish）。
- PHP SDK 具备 Packagist 发布条件：发布仓库根新增 `composer.json`（包 **`qqzeng/qzdb`**，classmap 指向 `ip-qzdb-sdk/php/QzdbReader.php`，源头在 `tools/publish_meta/`）。因 Packagist 只认仓库根的 `composer.json`，同时新增根 `.gitattributes`，用 `export-ignore` 把 GitHub 归档裁剪到 5 个文件 / **44 KB**（未裁剪时 2.5 MB，会连带下载另外 7 种语言源码、4.3 MB demo 数据库与另外 3 条产品线）。包已建好、`v1.0.5` tag 已推送，`1.0.5` 正式版待在 Packagist 页面触发 Update 生成。
- 多语言 monorepo 的 git tag 发布约定（`v<ver>` 全平台 / `v-python-*` / `v-java-*` / `v-rust-*` 单平台），见 `PUBLISHING.md` §0。

#### Changed

- Node.js `QzdbRegistry`：`register()`/`registerBuffer()`/`unregister()` 对被替换/移除的 reader 改为进入容量 8 的退休队列延迟关闭（对齐 Go/Java/netcore），消除 await 让出期间并发热更新导致在途调用静默返回 null 的隐蔽问题；`clear()` 语义不变（立即关闭并冲刷退休队列）。附 7 条行为回归断言。
- Rust `serde_json` 由 `[dependencies]` 移入 `[dev-dependencies]`：lib 不使用该依赖，此前会无谓地进入下游依赖树。
- Rust 发行包范围收紧：`src/bin/*`、`src/main.rs`、`tests/*`、`bench_qps.rs` 经 `exclude` 排除。其中 `src/bin/metaprobe.rs` 依赖 dev-only 的 `serde_json`，若随包发布会导致下游 `cargo install qzdb` 编译失败；`tests/*` 依赖未随包发布的 `.qzdb` 数据。`cargo package` 时打印的 16 行 `ignoring ...` warning 属预期。

#### Fixed

- 发布 workflow 的 tag 过滤器过宽：`publish-pypi.yml` 与 `publish-maven-central.yml` 原本都是 `tags: ['v*']`，在多语言 monorepo 下会误匹配 `v-rust-*` / `v-java-*`，造成单语言发版时**连带触发其它平台的发布**。现均收紧为 `v[0-9]*`（纯版本号，全平台）+ 各自语言前缀。

#### Fixed

- 8 语言 IP 解析器严格性对齐：拒绝 `"a.b.c.d::"` 形态（嵌入 IPv4 点分四元组落在 `::` 压缩缺口左侧且右侧为空，如 `0.0.0.0::`、`1.2.3.4::`、`2001:db8:1.2.3.4::`）。此前 Python/Node/PHP/C/Rust/Java 六语言错误接受（Node 对 `1.2.3.4::` 还会产出错乱字节），与 Go SDK 及 Go 标准库 `netip.ParseAddr` 行为不一致；C# 经审计本就正确。十行行为契约表已作为永久回归落至各语言测试套件（Go fuzz 差分对拍发现，netip 为裁判）。

## [1.0.7] - 2026-08-28

### Added

- .NET / C# 新增 `net11.0` 目标框架（现为 `net8.0;net9.0;net10.0;net11.0` 四目标）；`System.IO.Hashing` 显式引用（net11 定位包同样不内置 Crc32）。构建需 .NET 11 SDK，`global.json` 改为 `rollForward: latestMajor` + `allowPrerelease: true`，.NET 11 GA 后自动回落稳定版 SDK。
- .NET / C# 全部公开 API 补齐 XML 文档（CS1591 归零，包内 XML 13KB→56KB）。

### Changed

- .NET / C# 显式开启严格静态分析：`AnalysisMode=All` + `TreatWarningsAsErrors`（配套逐规则豁免见 `ip-qzdb-sdk/netcore/.editorconfig`）；`EnforceCodeStyleInBuild` 刻意不开启（被目录外 ProjectReference 消费时不可移植）。
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
- C 注入式回归测试 `ip-qzdb-sdk/c/tlv_meta_test.c`：真实库注入 TLV 后校验权威/回落/重复条目 last-wins 三路径，兼作 scope 字符串所有权 UAF 回归守卫（ASan 下验证）。
- `docs/QZDB_SYNC_GUIDE.md` 新增文档同步规范（§六）与语言 README 统一章节骨架（§6.1）。

### Fixed

- Java `formatNativeFloat` 违反 FORMAT §10.5 精确展开契约：`String.format("%.0f")` 按最短 round-trip 数字补零（如 2^63 输出 9223372036854776000），改为 `new BigDecimal(v).toPlainString()` 并以 E300 硬编码字面量断言锁定。
- PHP `repairDimMasks` 由「当前组顶替」改为逐组按自身字段名推导，修复多组库的维度掩码修复错位。
- C 缓冲区查询 API 未命中统一返回 `QZDB_ERR_NOT_FOUND`（fail-closed，不再返回未初始化内容）。
- `tools/batch_query.go` 导入路径漂移（旧模块名 `qzdb_reader/qzdb`），恢复 build_all.sh Go 步骤可用。
- 测试基建：C# Tier1 改流式读取数 GB CSV 基准（常量内存）；PHP 合成夹具补 `poolIdxSize@13`、PHP 8.0 反射补 `setAccessible(true)`；L2 脚本支持 JAVA_HOME 环境变量与 clang/gcc/cc 编译器回退。

### Changed

- Trie 游走终止保护跨语言统一为按 IP 位宽派生的上限（Node.js/Go/C/PHP/Python 以命名常量替换魔法常量 1000；Rust/Java/C# 本就构造性有界，仅登记机制）。良构文件行为不变。
- 文档专业化整改：根 README 与 ip-qzdb-sdk README 重写（示例全部来自可运行代码与实测输出、fail-closed 路径完整）、SDK_API 头部去叙事化、9 篇过程稿归档至 `docs/archive/`。

### Compatibility

- 不含 type=5/6 条目的既有文件行为零变化（回归断言全绿）。
- 查询 API 签名无变化；仅错误路径语义收紧（C buf API miss 由实现定义值统一为 NOT_FOUND）。

### Verification

- 8 语言单语言套件全绿 + L2 跨语言一致性 427/427（8 语言 × 61 IP 管道输出全同）+ L3 批量回归全 10 库 28,602,081 节点 0 错误 + 敌对向量测试通过。明细见 `docs/ROADMAP.md` T7/T8。

## [1.0.4] 及更早

见对应 git tag 与历史提交记录。
