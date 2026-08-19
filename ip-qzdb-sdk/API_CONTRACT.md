# QZDB Multi-Language SDK API 规范契约 (API Contract v2.4)

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

---

## 三、 字段归一化与数据规范

### 1. 字段名称归一化算法 (Key Normalization)
所有通过 `geo_info.get("field_name")` 查询字段时，必须应用如下归一化算法：
- **算法**：忽略所有下划线 `_`、连字符 `-`，并将所有 ASCII 字符转为小写。
- **等价性**：`country_code` ≡ `CountryCode` ≡ `country-code` ≡ `COUNTRYCODE`。
- **未收录字段**：查询不存在的字段时统一返回 `""` 或 `null`，严禁抛出越界异常。

### 2. 管道序列化 (`ToPipe()`)
- 所有字段原样以竖线 `|` 连接，字段顺序严格遵循对应版本组的物理 Schema 顺序。
- 浮点类型字段（经纬度）输出固定 6 位小数（如 `116.400000`）；NaN / Inf 输出为空字符串 `""`。

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
