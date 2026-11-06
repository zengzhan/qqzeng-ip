# QZDB Multi-Language SDK API 规范契约 (API Contract v2.5)

> 本契约是 **QZDB 全语言 SDK 行为一致性的唯一事实来源 (Single Source of Truth)**。  
> 覆盖语言：C / Go / Java / .NET (C#) / Node.js / PHP / Python / Rust。

---

## 一、 核心架构原则

1. **去单例化设计**：所有语言 SDK 核心类统一命名为 **`QzdbReader`**（C 语言为 `qzdb_reader_t`），通过实例持有，支持依赖注入与多文件/多版本并发共存。
2. **三层体系架构**：
   - **Layer 1: `QzdbReader`** —— 单个 `.qzdb` 数据库的核心解析引擎。
   - **Layer 2: `QzdbRegistry`** —— 线程安全的命名多库管理容器。
   - **Layer 3: `ChainedReader`** —— 多库复合联合查询引擎（支持 Fallback 降级、Merge 字段合并、MergeOverride 覆盖）。
3. **无锁快照并发**：
   - 加载后快照不可变（Immutable Snapshot），多线程/协程并发查询零锁争用。
   - 热更新（`reload` / `reloadBuffer`）采用原子指针替换，新快照构建成功后瞬间生效，旧快照平滑下线，绝不阻塞正在执行的并发读请求。

---

## 二、 查询方法与返回语义

### 1. 单条查询核心方法矩阵
| 方法签名概念 | 返回类型概念 | 未命中行为 | IP 非法行为 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `find(ip)` | `GeoInfo?` | 返回 `null` / `None` / `QZDB_ERR_NOT_FOUND` | 返回 `null` / `None` / `QZDB_ERR_NOT_FOUND` (托管语言可抛 `InvalidIp`) | 完整实体查询 |
| `find_str(ip)` | `string` | 返回 `""` (空字符串) | 返回 `""` (空字符串) | 直接输出 `ToPipe()` 管道字符串 |
| `find_fields(ip, fields)` | `GeoInfo?` | 返回 `null` / `None` | 返回 `null` / `None` | 指定字段投影，减少多余解码 |
| `lookup_row_id(ip)` | `uint32` | 返回 `0` | 返回 `0` | 最轻量 Layer 1 查询，仅做 Trie 遍历 |
| `lookup_cidr(ip)` | `string` | 返回 `""` / `null` | 返回 `""` / `null` | 反查所属 CIDR 网段（如 `1.0.1.0/24`） |

### 2. 批量与流式查询精度三态（三态保留原则）
在批量（`find_batch`）与流式（`find_stream`）查询中，返回的条目必须严格区分以下三态，**禁止将非法 IP 降级为未命中**：
1. **命中 (Success)**：`geo_info != null`，`error == null`
2. **未命中 (Not Found)**：`geo_info == null`，`error == null`（合法公网/私网 IP 但库内无记录）
3. **参数非法 (Invalid IP)**：`geo_info == null`，`error != null`（格式错误、含非法字符等）

### 2.5 缺失字段的语义 Getter 两层约定(v2.5.1,跨语言审计确认)

语义 Getter 存在两层,缺失字段行为**必须**遵循:

1. **类型化 Getter**(如 `GetLongitude()` / `getLongitude()` / `longitude` 属性):
   字符串字段缺失 → 空串 `""`;数值字段缺失 → 语言原生 optional
   (C# `null` / Java `null` / Python `None`),**绝不返回其他字段的值**。
   历史教训:C# `BindStandardIndices` 曾因 `TryGetValue` 缺键置 0 而误命中
   首字段(已修,见 CHANGELOG)。
2. **动态取值**(`get(name)` / `Get(name)` / `geo_info_get`):一律返回空串 `""`,
   绝不抛错(既有约定,重申)。

跨语言矩阵验证:`ip-qzdb-sdk/cross_api_verify.py`(cidr + row_id,8/8 语言——
Go/Rust/C# 经 batch 二进制 `--cidr` 模式)+ `ip-qzdb-sdk/cross_lang_verify.py`(pipe)。

### 3. 字段投影语义（`find_fields` / `findFields`，v2.5 对齐 Java golden）
以 Java 实现为认证参考，8 语言投影行为逐字一致：
1. **字段顺序**：输出 GeoInfo 的字段名与顺序 = 调用方输入原样（含重复字段、未知字段）。
2. **未知字段**：在该位置补空串 `""`，**不跳过、不报错**。
3. **重复字段**：保留重复项，不去重。
4. **全部未知**：仍返回非空 GeoInfo（字段值全为 `""`），不得返回 `null`/`None`。
5. **数据来源**：优先从解码缓存的全字段结果切片（骑缓存，勿绕过）。
6. **IP 语义**：未命中/非法 IP 的返回与 `find` 完全一致（各语言沿用自身单条口径）。

### 4. 零拷贝共享查询（语言扩展层，非强制）
- **Rust**：`find_shared() -> Option<Arc<GeoInfo>>`（缓存命中零堆分配）、`find_ref() -> Option<GeoInfoRef>`（借用视图；`_snap` Arc 保活快照，reader reload 不影响已返回引用。字段值自 v2.5 起为 `'static`：池串是装载期 leaked 副本、数字/浮点是内联栈值，`to_geo_info()` 退化为纯拷贝）与 `ToIp` 惯用入参族（`Ipv4Addr`/`Ipv6Addr`/`IpAddr`/`u32`/`u128`/`str`）。
- **C**：`qzdb_find_values_fast` / `qzdb_find_values_uint_fast` / `qzdb_find_values_v6_fast`（类型化极速直取）。
  - **索引预解析**：`qzdb_resolve_field_indices` 负责将字段名称预解析为整数索引数组。严格遵循 **Fail-Closed** 原则：遇到任何未知字段名或非法参数直接返回 `QZDB_ERR_INVALID_PARAM`。
  - **生命周期与热更新契约 (Lifetime & Concurrency Contract)**：
    - 返回的 `qzdb_value_t` 结构中：文本字段借用当前 Reader 只读字符串池指针（无堆分配、无拷贝）；原生数值字段直接返回类型化值（ASN 为 `uint32_t`，经纬度为 `double`）。
    - **借用指针使用范围**：`qzdb_value_t` 里的字符串指针**仅限在当前这次查询/函数调用上下文内使用**。若业务方需要长期保存或跨异步任务传递，必须自行拷贝（如 `strdup`）；借用指针不得跨越 7 次以上的 registry 轮换。
    - **禁止原地并发 Reload**：快速接口直接借用只读内存池指针，因此**同一个 reader 实例严禁在查询进行时并发调用 `qzdb_reload`、`qzdb_reload_buffer` 或 `qzdb_free`**（原地更新会析构并释放只读池，导致在途指针悬空）。
    - **Registry 容器生命周期说明**：`qzdb_registry_t` 内部采用固定 8 槽位的环形隔离队列（quarantine ring）。发生同名重新注册（替换实例）时，旧 reader 放入隔离队列，在后续又发生 8 次轮换时会被直接销毁释放，**不会等待在途持有者释放完毕**。因此在快速连续轮换场景下最多容忍 7 次后续轮换，若在此期间持续持有旧指针将导致 use-after-free。
    - **热更新安全警告**：调用方若自行实现原子指针交换轮换，**严禁在交换后立即调用 `qzdb_free`**（这必将引发 use-after-free），必须确保所有在途并发查询全部安全退出后再释放旧实例。
- **强制约束**：扩展 API 的 `to_pipe()` / `to_json()` 输出必须与 owned `find()` 结果**逐字节一致**（由 `tests/zero_copy_ref.rs` parity 断言背书）。
- **ChainedReader::find_ref**：仅 Fallback 模式可用；Merge/MergeOverride 模式因跨库合并需所有权拼接，不支持零拷贝视图（计划 v1.0.7 起改为显式 panic，避免与"未命中"混淆）。

---

## 三、 字段归一化与数据规范

### 1. 字段名称归一化算法 (Key Normalization)
所有通过 `geo_info.get("field_name")` 查询字段时，必须应用如下归一化算法：
- **算法**：忽略所有下划线 `_`、连字符 `-`，并将所有 ASCII 字符转为小写。
- **等价性**：`country_code` ≡ `CountryCode` ≡ `country-code` ≡ `COUNTRYCODE`。
- **未收录字段**：查询不存在的字段时统一返回 `""` 或 `null`，严禁抛出越界异常。

### 2. 管道序列化 (`ToPipe()`)
- 所有字段原样以竖线 `|` 连接，字段顺序严格遵循对应版本组的物理 Schema 顺序。
- 原生浮点字段（经纬度）按 **FORMAT §10.5 统一契约**格式化：整数值无小数点（`116.0 → "116"`）、非整数固定 6 位小数（`116.4 → "116.400000"`）、NaN / Inf 输出为空字符串 `""`；Pool 字符串存储的浮点原样透传、不再格式化。

### 3. UsageType 官方场景枚举（21 类）
支持以下官方场景的标准解析与中英文转换：
`AICrawler`, `Backbone`, `Broadband`, `Business`, `CDN`, `Cloud`, `DNS`, `DataCenter`, `Education`, `Finance`, `Government`, `ISP`, `IXP`, `IoT`, `Mobile`, `Reserved`, `Satellite`, `Spider`, `Streaming`, `Unknown`, `VPN`。未收录的自定义标签自动归类为 `Unknown`。

---

## 四、 生产环境运维与安全准则

1. **实例长期持有与复用**：
   - 初始化 `QzdbReader` 涉及文件映射、CRC 完整性校验、字符串索引池预置，属于高开销操作。
   - **禁止在每次 Web 请求处理中临时创建 Reader 实例**；必须在应用启动期单例初始化并全生命周期复用。
2. **防止段错误的原子热更新机制**：
   - 依赖 `mmap` 的语言（C / Go / Java / .NET / Rust）由操作系统维护物理页映射。
   - **严禁在线上直接原地覆写（In-place Overwrite）或截断正在被进程打开的 `.qzdb` 数据库文件**，否则会导致总线错误（SIGBUS）或段错误（SIGSEGV）。
   - **正确热更流程**：先写入临时文件（如 `qqzeng_ip_new.qzdb.tmp`）并校验完整性，再通过原子重命名（`rename`）覆盖目标文件，最后触发 SDK 的 `reload()` 方法。

---

## 五、已裁决行为口径登记（Divergence Register，v2.5）

以下分歧已裁决并冻结；任何语言改动这些行为前必须先修订本表并同步 8 语言：

| # | 口径 | 裁决 | 依据 |
|---|------|------|------|
| 1 | **跳表哨兵语义** | 跳表条目带 SENTINEL = 终止叶子，`find`/`lookup_row_id` 直接返回低 31 位 row_id；CIDR 反查需前缀长度，**必须从根重走求真实前缀**（跳表深度不是真实前缀，短范围行挂在 trie 浅层；全 8 语言一致实现） | FORMAT §4 SearchV4/V6 + 8 语言 cross_api 对拍（2026-09-02 裁决，Rust/PHP/Python 已对齐；回归测试 `jump_sentinel_test*` / rust `v6_cidr_jump_sentinel_reports_true_prefix`） |
| 2 | **IP 前后空白字符** | Java `trim()` 接受 `" 1.2.3.4 "`；Go/Node 显式拒绝。**保留现状**，Java 为 golden；Go/Node 的严格口径为 SSRF 防护场景的推荐实现 | 2026-09-02 审查登记 |
| 3 | **IP 字符串解析口径** | 非法 IP：托管语言（Java/C#）抛 `InvalidIp`；Go/Node/PHP/Rust 返回 null/零值。单条口径随语言，**批量路径三态为强制**（§二.3） | 契约 §二 |
| 4 | **`getScope()` / `scope`** | 当前格式无 scope 字段，8 语言一律返回 `""`。`"cn"\|"global"` 为格式迁移后的目标契约（见 QZDB_SDK_API.md 前置依赖注记） | 2026-09-02 裁决 |
| 5 | **dimensionMask 双位置位**（畸形文件） | 按 `0x02(asn) → 0x04(usage) → 0x01(geo)` 优先级链选维（Java 语义）；Go 已对齐计划中，合法文件不受影响 | 2026-09-02 审查登记 |
| 6 | **快照生命周期（Go）** | refCount 引用计数已由 `runtime.SetFinalizer` + GC 可达性托管：读者持引用即免 munmap，查询路径仅一次 atomic Load | 2026-09 实现（`-race` 全绿背书） |
| 7 | **性能基线** | CI `perf-gate` job 以绝对下限（floors）拦截数量级回退；细粒度回归由本地 `tools/perf_gate.py --baseline --tol 0.25` 承担 | BENCH_CONTRACT §门禁 |

---

## 六、 下一代扩展层契约：零分配位掩码与 AI Agent MCP 工具声明（v2.6 增量扩展）

在保持存量 8 语言核心 API 100% 兼容的基础上，Rust 核心引擎及派生扩展包（`@qqzeng/qzdb-wasm`、`qqzeng-ip-rust`、`@qqzeng/qzdb-native`）新增以下标准扩展契约：

### 1. 零分配特征位掩码 (`IpAttrFlags` / `inspect_flags`)
`inspect_flags(ip) -> uint32` 通过快照级惰性预计算表（`entry_id -> u32`）提供零堆分配、零字符串解码的 O(1) 场景与地域判定，其 32 位无符号整型位布局冻结如下：

| 位区间 (Bit) | 常量标识 | 掩码值 | 严格判定语义（与 `GeoInfo` 100% 自洽） |
| :--- | :--- | :--- | :--- |
| `Bit 0` | `HIT` | `1 << 0` | 库内命中有效记录（`row_id > 0` 且 `entry_id > 0`） |
| `Bit 1` | `DOMESTIC_CN` | `1 << 1` | 中国大陆境内（`country_code == "CN"` 且省份非香港/澳门/台湾） |
| `Bit 2` | `GREATER_CN` | `1 << 2` | 大中华区（`country_code` 为 `CN`/`HK`/`MO`/`TW` 或 `country == "中国"`） |
| `Bit 3` | `PRIVATE_RESERVED` | `1 << 3` | 私有保留或环回地址（`country == "局域网"` 或 `UsageType == Reserved`） |
| `Bit 4` | `DATACENTER` | `1 << 4` | 数据中心 / 云服务 / CDN（`UsageType` 为 `DataCenter` \| `Cloud` \| `CDN`） |
| `Bit 5` | `AI_CRAWLER` | `1 << 5` | AI 训练与搜索爬虫（`UsageType == AICrawler`） |
| `Bit 6` | `SPIDER` | `1 << 6` | 通用搜索引擎爬虫（`UsageType == Spider`） |
| `Bit 7` | `MOBILE` | `1 << 7` | 蜂窝移动网络（`UsageType == Mobile`） |
| `Bit 8` | `BROADBAND` | `1 << 8` | 家庭或企业宽带接入（`UsageType == Broadband`） |
| `Bit 9` | `VPN_PROXY` | `1 << 9` | VPN 或代理出口（`UsageType == VPN`） |
| `Bit 10` | `BACKBONE_IXP` | `1 << 10` | 骨干网或交换中心（`UsageType` 为 `Backbone` \| `IXP`） |
| `Bit 11` | `SATELLITE` | `1 << 11` | 卫星互联网接入（`UsageType == Satellite`） |
| `Bit 12` | `DNS_ANYCAST` | `1 << 12` | 公共 DNS 基础设施（`UsageType == DNS`） |
| `Bit 13` | `EDU_GOV_FIN` | `1 << 13` | 高校教育 / 政务 / 金融网络（`UsageType` 为 `Education` \| `Government` \| `Finance`） |
| `Bits 16..20` | `USAGE_CODE` | `(bits >> 16) & 0x1F` | 编码 21 类官方 `KnownUsage` 的枚举序号（`1..=21`，`0` 表示未知或无此字段） |

便捷布尔方法契约：
- `is_datacenter(ip)` ≡ `(inspect_flags(ip) & DATACENTER) != 0`
- `is_domestic(ip)` ≡ `(inspect_flags(ip) & DOMESTIC_CN) != 0`
- `is_crawler(ip)` ≡ `(inspect_flags(ip) & (AI_CRAWLER | SPIDER)) != 0`
- `is_ai_crawler(ip)` ≡ `(inspect_flags(ip) & AI_CRAWLER) != 0`
- `is_mobile(ip)` ≡ `(inspect_flags(ip) & MOBILE) != 0`
- `is_vpn(ip)` ≡ `(inspect_flags(ip) & VPN_PROXY) != 0`

### 2. AI Agent / MCP 工具清单导出 (`export_mcp_tool_manifest`)
`export_mcp_tool_manifest() -> string` 输出符合 JSON Schema Draft 2020-12 与 Model Context Protocol (`2025-06-18`) 的工具描述 JSON，内含：
- `database_meta`：实时反映当前快照的 `edition`、`version`、`data_month`、`build_time`、`crc32_hash` 与 `available_fields`。
- `tools`：声明 `qzdb_inspect_ip_fast`（零分配位掩码批量嗅探）与 `qzdb_lookup_geo_fields`（支持 `fields` 按需字段投影以最小化 LLM Token 消耗）。

### 3. 应用场景类型权威枚举 (`usage_type`)
QZDB 官方定义 21 类网络应用场景枚举。在含有 `usage_type` 字段的版本（`MAX` 15 维、`ULT` 25 维、`ASN` 8 维）中，取值为以下原始英文字符串；若命中未收录场景则安全兜底为 `Unknown`，不崩溃。

| 序号 | 原始值 (`RawValue`) | 中文名称 | 场景含义与典型特征 | 是否出现在公开 Demo 库 |
| :---: | :--- | :--- | :--- | :---: |
| 1 | `AICrawler` | AI 爬虫 | AI 训练 / AI 搜索爬虫（GPTBot、ClaudeBot 等） | 否 |
| 2 | `Backbone` | 骨干网 | 运营商骨干传输网 / 国际出口路由 | 是 |
| 3 | `Broadband` | 宽带 | 家庭/企业宽带接入（xDSL、FTTH、Cable、拨号等） | 是 |
| 4 | `Business` | 企业 | 企业专线 / 企业内部组网 | 是 |
| 5 | `CDN` | CDN | 内容分发网络缓存与加速节点 | 是 |
| 6 | `Cloud` | 云服务 | 公有云 / 托管云主机（AWS、阿里云、Azure、腾讯云等） | 是 |
| 7 | `DNS` | DNS | DNS 基础设施 / Anycast 公共 DNS 节点 | 否 |
| 8 | `DataCenter` | 数据中心 | IDC 机房托管 / 服务器租用 | 是 |
| 9 | `Education` | 教育网 | 高校 / 科研网（如 CERNET、Internet2 等） | 是 |
| 10 | `Finance` | 金融 | 银行 / 证券 / 保险等专有金融机构内网 | 是 |
| 11 | `Government` | 政府 | 政务内外网 / 公共管理机构网络 | 是 |
| 12 | `ISP` | 互联网提供商 | 未细分场景的通用网络运营商基础设施 | 否 |
| 13 | `IXP` | 交换中心 | 互联网交换中心（Internet Exchange Point） | 否 |
| 14 | `IoT` | 物联网 | 物联网设备 / 智能硬件蜂窝接入 | 否 |
| 15 | `Mobile` | 移动网络 | 蜂窝移动网络（2G/3G/4G/5G 基站流量） | 是 |
| 16 | `Reserved` | 保留地址 | RFC 1918 / RFC 6890 保留或未分配私网网段 | 否 |
| 17 | `Satellite` | 卫星互联网 | 低轨星座 / 卫星互联网接入（Starlink、OneWeb 等） | 否 |
| 18 | `Spider` | 爬虫 | 通用商业搜索引擎网络爬虫（Googlebot、Baiduspider 等） | 否 |
| 19 | `Streaming` | 流媒体 | 音视频 / 游戏直播等流媒体专用加速节点 | 否 |
| 20 | `Unknown` | 未知 | 无法明确判定用途的场景兜底 | 是 |
| 21 | `VPN` | VPN/代理 | 数据中心代理 / VPN / 匿名隐私网络出口 | 是 |
