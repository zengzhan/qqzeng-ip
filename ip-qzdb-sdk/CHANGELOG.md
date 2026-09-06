# 更新日志

本文件记录 QZDB 多语言 SDK 的用户可见变更。格式参照 Keep a Changelog；语义化版本。

## [Unreleased]

### Added

- **CI 性能门禁（perf-gate job）**：`multi-lang/tools/perf_gate.py` + C/Go/Rust/Node/Python 五语言驱动器，基于公共 demo 样本（数据无关、可在托管 runner 运行）。绝对下限（floors）拦截数量级回退，对 runner 硬件代际免疫；`--baseline --tol` 支持本地细粒度对比。挂入 `.github/workflows/ci.yml`（产出 30 天 perf 报告 artifact）。
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

- **新增跨 API 一致性验证层 `cross_api_verify.py`(审计基建)**:
  同一 IP 横向比对 `lookup_cidr` + `lookup_row_id`(此前 cross_lang_verify 只比
  pipe,/32 类错误不可见)。首跑即在 4 语言间抓到 26 处真实分歧,全部修复后
  **504/504 全过**(Python 参照 + Node/PHP/Java/C,63 IP 含 mapped 降级与
  /32 单段)。TEST_IPS 补 V4-mapped 降级路径(`::ffff:` 前缀 2 条)。
  Go/Rust/C# 的 batch 二进制 cidr 字段扩展留待下一轮(源码在 tools/)。
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

- Rust SDK **已发布到 crates.io**：`cargo add qzdb`（1.0.5，2026-08-29 上线，<https://crates.io/crates/qzdb>）。crate 名 `qzdb_reader` → **`qzdb`**（与 PyPI 的 `qzdb` 对齐；C 语言的 `qzdb_reader_t` / `qzdb_reader.h` 不受影响）；`Cargo.toml` 补全 crates.io 必需的 `description` / `license` / `repository` / `homepage` / `documentation` / `keywords` / `categories` / `rust-version=1.74`；新增 `multi-lang/rust/LICENSE`；新增 `.github/workflows/publish-crates.yml`（tag `v-rust-*` 触发，先 dry-run 后 publish）。
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
