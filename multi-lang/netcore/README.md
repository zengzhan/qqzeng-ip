# QQZeng.Qzdb

> 纯离线、零依赖、高性能的 **QZDB IP 地理定位数据库**官方 .NET SDK（支持 IPv4 / IPv6 双栈）。

- **官方品牌**：`QQZeng.Qzdb`（NuGet 包 ID 与根命名空间一致）
- **定位**：离线解析 `.qzdb` 二进制数据库文件，不依赖任何外部网络请求
- **架构**：无锁快照（lock-free snapshot）——并发查询互不阻塞，`Reload` 原子切换（`Interlocked.Exchange`）
- **目标框架**：`net8.0`、`net10.0`
- **许可**：MIT

---

## 目录

1. [环境要求](#1-环境要求)
2. [安装](#2-安装)
3. [快速开始](#3-快速开始)
4. [加载数据库](#4-加载数据库)
5. [查询 API](#5-查询-api)
6. [结果对象 `GeoInfo`](#6-结果对象-geoinfo)
7. [批量与流式查询](#7-批量与流式查询)
8. [链式多库查询 `ChainedReader`](#8-链式多库查询-chainedreader)
9. [命名注册表 `QzdbRegistry`](#9-命名注册表-qzdbregistry)
10. [热更新与生命周期](#10-热更新与生命周期)
11. [错误处理](#11-错误处理)
12. [性能说明](#12-性能说明)
13. [维护与升级](#13-维护与升级)
14. [项目结构](#14-项目结构)

---

## 1. 环境要求

| 项 | 要求 |
|----|------|
| .NET 运行时 | `.NET 8.0` 或 `.NET 10.0`（SDK 不限版本，引用方任选其一即可） |
| 操作系统 | Windows / Linux / macOS 均可 |
| 数据库文件 | `.qzdb` 格式（由官方数据构建工具生成，含所需分组的二进制数据） |
| 依赖 | 无第三方运行时依赖（零依赖） |

---

## 2. 安装

通过 NuGet 安装：

```bash
dotnet add package QQZeng.Qzdb
```

或在 `.csproj` 中直接引用：

```xml
<PackageReference Include="QQZeng.Qzdb" Version="1.0.0" />
```

C# 代码统一使用命名空间：

```csharp
using QQZeng.Qzdb;
```

---

## 3. 快速开始

```csharp
using QQZeng.Qzdb;

// 通过文件路径加载（默认：校验 CRC、加载第 0 个分组）
using var reader = new QzdbReader.Builder("ip_china.qzdb").Build();

// 单次查询：未命中或 IP 非法时返回 null（不会抛异常）
GeoInfo? info = reader.Find("114.114.114.114");
if (info != null)
{
    Console.WriteLine(info.ToPipe());                              // 管道符分隔：国家|省份|城市|ISP|...
    Console.WriteLine(info.ToJson());                              // 紧凑 JSON 字符串
    Console.WriteLine($"{info.GetCountry()} / {info.GetProvince()} / {info.GetCity()}");
    Console.WriteLine($"ISP={info.GetIsp()}, ASN={info.GetAsn()}");
}

// 管道符格式（未命中返回空字符串，适合直接落库/日志）
string pipe = reader.FindStr("240e:390:1:1::1");

// 仅取内部行号（最轻量，不涉及字段解析）
uint rowId = reader.LookupRowId("8.8.8.8");
```

> **查询语义约定**：`Find` / `FindUint` / `FindBytes` 等**在 IP 未命中或格式非法时返回 `null` / `0`，不抛异常**。只有数据库文件损坏、格式不支持、CRC 校验失败等**加载期错误**才会抛出 `QzdbException`（见[第 11 节](#11-错误处理)）。

---

## 4. 加载数据库

所有加载入口都通过 **`QzdbReader.Builder`** 构造。Builder 不可变链式调用，`Build()` 返回 `QzdbReader`（实现 `IDisposable`）。

### 4.1 从文件路径加载

```csharp
// 最简：路径 + 默认第 0 分组 + 校验 CRC
using var reader = new QzdbReader.Builder("ip_china.qzdb").Build();

// 完整选项
var reader = new QzdbReader.Builder("ip_all.qzdb")
    .GroupIndex(1)        // 选择数据库中的第 N 个分组（多分组文件时用于选不同数据维度）
    .VerifyCrc(false)     // 关闭 CRC32 校验（仅在你已离线校验过、追求极限加载速度时关闭）
    .Build();
```

### 4.2 从内存缓冲区加载（Serverless / 内嵌资源）

适用于把 `.qzdb` 作为嵌入式资源、或运行时从对象存储/网络下载到内存后直接解析，避免落盘：

```csharp
byte[] bytes = await File.ReadAllBytesAsync("ip_china.qzdb");
using var reader = new QzdbReader.Builder(bytes).Build();
// 同样支持 .GroupIndex(...) / .VerifyCrc(...)
```

### 4.3 关于分组（GroupIndex）

一个 `.qzdb` 文件可内嵌多个数据分组（如 `std` / `asn` / `max` 等不同维度）。`GroupIndex` 指定加载哪一个；不传时默认 `0`。加载后可通过 `reader.Edition` / `reader.FieldNames` 确认实际加载的维度与字段。

### 4.4 加载异常处理

`Build()` 可能在以下情况抛出 `QzdbException`（务必在启动期 `try/catch`，避免程序崩溃）：

| ErrorCode | 触发场景 |
|-----------|---------|
| `FileNotFound` | 文件不存在或无读取权限 |
| `BadMagic` | 文件头不是 `QZDB` 魔数 |
| `BadHeader` / `Unsupported` | 文件头尺寸异常或格式版本不受支持 |
| `Corrupted` | 分区越界、分组数为 0，或 **CRC32 校验不匹配**（数据损坏/被截断） |
| `InvalidParam` | `GroupIndex` 超出范围、字段宽度非法等 |

---

## 5. 查询 API

`QzdbReader` 提供多种输入形态的查询。下表列出**全部公开方法**：

| 方法 | 签名 | 返回 | 说明 |
|------|------|------|------|
| 字符串查询 | `GeoInfo? Find(string ipStr)` | `GeoInfo?` | 按字符串查（IPv4 / IPv6 / IPv4 映射地址均可） |
| 字节查询 | `GeoInfo? FindBytes(byte[]? ipBytes)` | `GeoInfo?` | 按 4 字节（IPv4）或 16 字节（IPv6）原始字节查 |
| 整数查询 | `GeoInfo? FindUint(uint ipInt)` | `GeoInfo?` | 按 IPv4 的 `uint` 整型查（网络序/主机序见下方说明） |
| 字段子集 | `GeoInfo? FindFields(string ipStr, string[]? fields)` | `GeoInfo?` | 只解析指定字段，减少不必要的字符串分配 |
| 管道字符串 | `string FindStr(string ipStr)` | `string` | 直接返回 `ToPipe()` 结果；未命中返回 `""` |
| 行号查询 | `uint LookupRowId(string ipStr)` | `uint` | 仅返回内部行号（不含字段，最轻） |
| 行号（整数） | `uint LookupRowIdUint(uint ipInt)` | `uint` | `FindUint` 的轻量版，只返回行号 |
| 行号（字节） | `uint LookupRowIdBytes(byte[]? ipBytes)` | `uint` | `FindBytes` 的轻量版，只返回行号 |
| 反查 ID | `RowIds LookupIds(uint rowId)` | `RowIds` | 由行号反查 Geo/ASN/Usage 三类索引 ID |
| 批量查询 | `BatchResult[] FindBatch(IEnumerable<string> ipStrs)` | `BatchResult[]` | 批量字符串查询，逐条容错 |
| 批量字段 | `BatchResult[] FindBatchFields(IEnumerable<string> ipStrs, IEnumerable<string>? fields)` | `BatchResult[]` | 批量 + 字段子集 |
| 流式查询 | `IEnumerable<BatchResult> FindStream(IEnumerable<string> ipStrs)` | `IEnumerable<BatchResult>` | 惰性 `yield` 流式逐条产出 |

### 5.1 IP 输入约定

- **`Find(string)`**：接受点分十进制（`1.2.3.4`）与完整/压缩 IPv6（`2001:db8::1`）、IPv4 映射地址（`::ffff:1.2.3.4`）；非法格式直接返回 `null`。
- **`FindUint(uint ipInt)`**：`ipInt` 应为 IPv4 地址的 **主机序 `uint`**（即 `（a<<24）|（b<<16）|（c<<8）| d`）。若你手上是网络序字节，请先转换或改用 `FindBytes`。
- **`FindBytes(byte[])`**：4 字节按网络序（高位在前）解析为 IPv4；16 字节解析为 IPv6。

### 5.2 行号与 `RowIds`

`LookupIds(uint rowId)` 返回 `RowIds` 只读结构，包含：

```csharp
public readonly record struct RowIds(int GeoId, int AsnId, int UsageId);
```

常用于：你想自己持有"行号"做缓存/批处理，再按需用 `Find*` 取完整字段，或构造跨维度关联。

---

## 6. 结果对象 `GeoInfo`

`Find*` 系列返回 `GeoInfo?`。它是**不可变**的结果对象，提供三种读取形态：

### 6.1 通用取字段 `Get(name)`

```csharp
string country = info.Get("country");        // 大小写、下划线不敏感：country / Country / country_code 等价
string isp     = info.Get("isp");
```

字段名匹配会**忽略大小写和下划线**（例如 `country_code`、`CountryCode`、`countrycode` 等同）。未命中返回 `""`。

### 6.2 强类型便捷方法

| 方法 | 返回类型 | 含义 |
|------|---------|------|
| `GetCountry()` / `GetCountryEn()` / `GetCountryAlpha2()` / `GetCountryAlpha3()` | `string` | 国家 |
| `GetProvince()` / `GetProvinceEn()` | `string` | 省份 |
| `GetCity()` / `GetCityEn()` / `GetDistrict()` | `string` | 城市 / 区县 |
| `GetIsp()` / `GetIspEn()` | `string` | 运营商 |
| `GetAsn()` | `ulong?` | ASN（自治域号） |
| `GetAsName()` / `GetAsDomain()` | `string` | ASN 名称 / 域名 |
| `GetGeoId()` | `ulong?` | 地理 ID |
| `GetLongitude()` / `GetLatitude()` | `double?` | 经纬度 |
| `GetTimezone()` | `string` | 时区 |
| `GetUsageType()` | `UsageType` | 用途分类（见下） |
| `GetCurrencyCode()` / `GetPhonePrefix()` / `GetEmojiFlag()` / `GetLanguages()` | `string` | 货币 / 电话区号 / 国旗 / 语言 |

### 6.3 序列化输出

| 方法 | 说明 |
|------|------|
| `ToPipe()` | 用 `|` 连接所有字段（惰性缓存，重复调用零重建） |
| `ToJson()` | 紧凑 JSON；数值字段（asn/经纬度等）自动输出为数字或 `null` |
| `ToMap()` | `Dictionary<string,string>`，便于反射/泛型消费 |
| `FieldNames` / `Values` | 字段名数组 / 值数组（均做了克隆，安全外传） |

### 6.4 用途分类 `UsageType`

`GetUsageType()` 返回 `UsageType`：

```csharp
UsageType u = info.GetUsageType();
bool known = u.IsKnown;            // 是否为预定义分类
string zh = u.DisplayZh();         // 中文名：如 "云服务"
string en = u.DisplayEn();         // 英文名：如 "Cloud"
string desc = u.Description();     // 详细描述
```

预定义分类（部分）：`AICrawler`、`Backbone`、`Broadband`、`Business`、`CDN`、`Cloud`、`DNS`、`DataCenter`、`Education`、`Finance`、`Government`、`ISP`、`IoT`、`Mobile`、`Reserved`、`Satellite`、`Spider`、`Streaming`、`Unknown`、`VPN` 等。未命中预定义值时 `IsKnown == false`，`RawValue` 保留原始字符串。

---

## 7. 批量与流式查询

```csharp
// 批量：一次性返回数组，单条异常不影响其它条目
BatchResult[] results = reader.FindBatch(new[] { "1.1.1.1", "8.8.8.8", "bad-ip" });
foreach (var r in results)
{
    if (r.IsSuccess) Console.WriteLine(r.Info!.ToPipe());
    else if (r.IsNotFound) Console.WriteLine("未命中");
    else Console.WriteLine($"错误: {r.Error!.Message}");
}

// 流式：惰性产出，适合超大数据集 / 管道消费
foreach (var r in reader.FindStream(hugeIpList))
{
    if (r.IsSuccess) Process(r.Info!);
}
```

`BatchResult` 是只读结构：

```csharp
public readonly record struct BatchResult(GeoInfo? Info, QzdbException? Error)
{
    public bool IsSuccess => Error == null && Info != null;
    public bool IsNotFound => Error == null && Info == null;
    public bool HasError   => Error != null;
}
```

> **性能提示**：`FindFields(ip, fields)` 与 `FindBatch` 配合，可只对需要的字段做解析，减少大批量查询下的字符串分配压力。

---

## 8. 链式多库查询 `ChainedReader`

当你有多个 `.qzdb`（例如"国内库 + 全球库"、"基础库 + 精细库"），可用 `ChainedReader` 把多个 `QzdbReader` 组合成一个逻辑查询器，支持三种合并模式：

| 工厂方法 | 模式 | 行为 |
|----------|------|------|
| `ChainedReader.Chain(...)` | `Fallback` | 依次查询，返回**第一个命中**的结果 |
| `ChainedReader.ChainMerge(...)` | `Merge` | 合并所有命中；字段空缺才由后库补充（先库优先） |
| `ChainedReader.ChainMergeOverride(...)` | `MergeOverride` | 合并所有命中；后库的值**覆盖**先库 |

```csharp
using var china = new QzdbReader.Builder("ip_china.qzdb").Build();
using var global = new QzdbReader.Builder("ip_global.qzdb").Build();

// 国内优先，未命中回退全球
var chained = ChainedReader.Chain(china, global);
GeoInfo? info = chained.Find("8.8.8.8");

// 字段级合并（精细库补全基础库缺省字段）
var merged = ChainedReader.ChainMerge(china, global);
```

支持的方法：`Find` / `FindUint` / `FindBytes` / `FindFields` / `FindBatch` / `FindBatchFields` / `FindStream`。

> **资源说明**：`ChainedReader.Dispose()` **不会关闭**其下的底层 `QzdbReader`（由各 reader 自行管理生命周期）。`Dispose` 仅释放聚合状态。

---

## 9. 命名注册表 `QzdbRegistry`

用于按名字管理多个 reader（例如在不同模块间共享同一实例）。提供**实例级**与**进程全局级**两套 API：

```csharp
// 实例级
var reg = new QzdbRegistry();
reg.Register("china", "ip_china.qzdb");
QzdbReader? r = reg.Get("china");
reg.Unregister("china");
reg.Clear();

// 进程全局（静态快捷方式）
QzdbRegistry.RegisterGlobal("global", "ip_global.qzdb");
QzdbReader? g = QzdbRegistry.GetGlobal("global");
QzdbRegistry.UnregisterGlobal("global");
```

`Register` 会自动 `Dispose` 旧实例（同名覆盖时），避免句柄泄漏。

---

## 10. 热更新与生命周期

### 10.1 原子热更新（无需重启进程）

数据库文件更新后，只需调用 `Reload` / `ReloadBuffer`，**旧数据在整个加载过程中继续提供服务**；只有新快照完整构建成功后才原子切换：

```csharp
// 重新从文件加载（CRC 始终强制校验）
reader.Reload("ip_china_new.qzdb");

// 从内存缓冲加载
reader.ReloadBuffer(newBytes);
```

> 注意：`Reload` / `ReloadBuffer` **始终强制 CRC 校验**（与构造时 `VerifyCrc` 选项无关），确保热更新不会加载损坏数据。若新文件损坏，旧快照继续服务，方法会抛出 `QzdbException`。

### 10.2 释放与并发安全

- `QzdbReader` 实现 `IDisposable`：用 `using` 或显式 `Dispose()` 释放内存（快照）。
- **并发安全**：多个线程可同时调用任意查询方法，互不阻塞（无锁读取快照）。
- 已 `Dispose()` 的 reader 再查询会抛出 `ObjectDisposedException`。

---

## 11. 错误处理

所有加载/解析期错误以 `QzdbException` 抛出，携带 `ErrorCode` 枚举：

```csharp
try
{
    using var reader = new QzdbReader.Builder("ip_china.qzdb").Build();
}
catch (QzdbException ex)
{
    Console.WriteLine($"加载失败 [{ex.ErrorCode}]: {ex.Message}");
}
```

`ErrorCode` 取值：`FileNotFound`、`BadMagic`、`BadHeader`、`Unsupported`、`Corrupted`、`InvalidParam`、`NotFound`、`InvalidIp`。

> **查询期**：普通"未命中"和"IP 格式非法"通过返回 `null` / `0` 表达，**不抛异常**（见[第 3 节](#3-快速开始)说明）。只有底层数据异常（如分组越界）才会抛出，批量接口会将其封装进 `BatchResult.Error` 而不中断整体。

---

## 12. 性能说明

本 SDK 在查询热路径上做了极致优化：

- **无锁快照架构**：查询只读 `Volatile` 快照引用，多线程零竞争；`Reload` 用 `Interlocked.Exchange` 原子切换。
- **零分配 trie 遍历**：核心 `TrieWalkV4/V6` 使用 `unsafe` + `fixed` 指针、绕过边界检查，单次查询不分配托管内存。
- **per-snapshot 有界无锁缓存**：快照不可变 → 同一 `entryId` 永远解析出同一 `GeoInfo`。对热点 IP（同段/邻近客户端、批量扫段）直接命中缓存，**命中路径零分配、零 GC 压力**。缓存约 196 KB/快照，碰撞仅触发重算、绝不返回错值。
- **零分配 IP 解析**：IPv4 直接解析；IPv6 用 `stackalloc` 缓冲，避免堆分配。
- **加载优化**：用 `GC.AllocateUninitializedArray` 预分配数据缓冲，避免二次拷贝。

### 参考性能（随包测试套件，基于参考数据集）

| 场景 | 吞吐 | 说明 |
|------|------|------|
| 单线程 IPv4 查询 | ~7.8M QPS | 50 万随机散布 IP（缓存最不利情形） |
| 单线程 IPv6 查询 | ~8.5M QPS | 同上 |
| 16 线程并发 | 安全无锁 | 0 错误，无竞争退化 |
| 热点 IP 命中缓存 | ~60M QPS / 0 分配 | 同 IP 重复查询，GC 压力归零 |

> 实际吞吐随 CPU、数据规模、查询分布而变；上述数字用于说明量级，非 SLA。

---

## 13. 维护与升级

### 13.1 更新数据（最频繁的操作）

不需要重新编译或重启进程：

1. 从官方渠道获取新的 `.qzdb` 文件（注意 `DataMonth` / `BuildTime` 是否更新）。
2. 调用 `reader.Reload(newPath)` 或 `reader.ReloadBuffer(newBytes)` 原子热更新。
3. 用 `reader.DataMonth` / `reader.BuildTime` / `reader.Version` 确认已加载的数据版本。

### 13.2 升级 NuGet 包

```bash
dotnet add package QQZeng.Qzdb --version x.y.z
```

版本遵循 **SemVer**：

| 变更类型 | 版本位 | 影响 |
|----------|--------|------|
| 破坏性 API 变更 | 主版本 `x` | 需改调用代码 |
| 向后兼容的功能新增 | 次版本 `y` | 直接升级 |
| Bug 修复 / 性能优化 | 补丁 `z` | 直接升级（建议始终跟进） |

### 13.3 调试符号（Source Link）

发布包同时包含 `.snupkg` 符号包。在 Visual Studio / `dotnet` 中开启 **"启用源链接 / Enable Source Link"** 后，可逐步步入 SDK 源码，便于排查疑难问题。

### 13.4 兼容性注意

- 包 ID 与命名空间一致（`QQZeng.Qzdb`），升级不会造成命名空间漂移。
- 目标框架 `net8.0` / `net10.0`：引用方至少需其一；长期支持（LTS）优选 `net8.0`。

---

## 14. 项目结构

`netcore/` 目录（本库源码）：

| 文件 | 职责 |
|------|------|
| `QzdbReader.cs` | 核心读取器：加载、trie 遍历、查询、热更新、CRC、生命周期 |
| `GeoInfo.cs` | 查询结果对象：字段解析、序列化（`ToPipe`/`ToJson`/`ToMap`）、强类型取值 |
| `QzdbRegistry.cs` | 命名 reader 注册表（实例级 + 全局级） |
| `ChainedReader.cs` | 多库链式组合（Fallback / Merge / MergeOverride） |
| `BatchResult.cs` | 批量查询的三态结果结构（`Info` / `Error` / 状态位） |
| `RowIds.cs` | 行号反查结构（`GeoId` / `AsnId` / `UsageId`） |
| `UsageType.cs` | 用途分类枚举与中英映射 |
| `QzdbException.cs` | 异常类型与 `ErrorCode` 枚举 |
| `QQZeng.Qzdb.csproj` | SDK 风格项目文件（多目标框架 + NuGet 元数据） |

相邻项目（同 `multi-lang/` 下）：

- `netcore.samples/` —— 控制台示例（演示完整用法，`IsPackable=false`）
- `netcore.Tests/` —— 测试套件（正确性 + 性能基准，含 `test_data_202608/`）
- `tools/batch_csharp/` —— C# 批量查询工具示例

跨语言完整 API 规范见仓库根：`docs/QZDB_SDK_API.md`。

---

## License

[MIT](https://opensource.org/licenses/MIT)
