# QQZeng.Qzdb — PHP SDK

> 纯离线、零依赖、高性能的 **QZDB IP 地理定位数据库**官方 PHP SDK（支持 IPv4 / IPv6 双栈）。

- **官方命名空间**：`Qqzeng\Ip`（与参考实现 Java `com.qqzeng.qzdb` / .NET 包 `Qzdb` 保持跨语言品牌一致）
- **定位**：离线解析 `.qzdb` 二进制数据库文件，不依赖任何外部网络请求
- **架构**：不可变快照（immutable snapshot）——并发查询互不阻塞，`reload` 原子切换
- **运行要求**：**PHP 8.1+**（推荐 8.5.x；使用了返回类型声明、只读快照、`\Generator` 等特性）
- **许可**：MIT

---

## 目录

1. [环境要求](#1-环境要求)
2. [安装](#2-安装)
3. [快速开始](#3-快速开始)
4. [加载数据库](#4-加载数据库)
5. [查询 API](#5-查询-api)
6. [结果对象 `GeoInfo`](#6-结果对象-geoinfo)
7. [CIDR 网段反查](#7-cidr-网段反查)
8. [批量与流式查询](#8-批量与流式查询)
9. [链式多库查询 `ChainedReader`](#9-链式多库查询-chainedreader)
10. [命名注册表 `QzdbRegistry`](#10-命名注册表-qzdbregistry)
11. [热更新与生命周期](#11-热更新与生命周期)
12. [错误处理](#12-错误处理)
13. [性能说明](#13-性能说明)
14. [维护与升级](#14-维护与升级)
15. [项目结构](#15-项目结构)

---

## 1. 环境要求

| 项 | 要求 |
|----|------|
| PHP | **8.1 或更高**（推荐 8.5.x） |
| 扩展 | 仅需标准 `Core` / `json`（`json` 随 PHP 默认编译）；无第三方扩展依赖 |
| 操作系统 | Windows / Linux / macOS 均可 |
| 数据库文件 | `.qzdb` 格式（由官方数据构建工具生成，含所需分组的二进制数据） |
| 依赖 | **无任何第三方运行时依赖**（单文件 `QzdbReader.php`，零 Composer 依赖） |

---

## 2. 安装

本 SDK 以**单文件**形式提供（所有类都位于 `QzdbReader.php` 内，命名空间 `Qqzeng\Ip`）。无需 Composer，直接 `require` 即可：

```php
require_once '/path/to/QzdbReader.php';
use Qqzeng\Ip\QzdbReader;
use Qqzeng\Ip\QzdbBuilder;
```

若你希望接入 Composer 自动加载，可在 `composer.json` 中声明 PSR-4 映射（SDK 本身无 `composer.json` 依赖）：

```json
{
  "autoload": {
    "psr-4": { "Qqzeng\\Ip\\": "multi-lang/php/" }
  }
}
```

> 说明：仓库内 `QzdbReader.php` 自包含全部类（`QzdbException` / `GeoInfo` / `QzdbReader` / `QzdbBuilder` / `QzdbRegistry` / `ChainedReader` / `RowIds` / `BatchResult` / `UsageType` 系列），无需引用其它文件。

---

## 3. 快速开始

```php
require_once __DIR__ . '/QzdbReader.php';
use Qqzeng\Ip\QzdbBuilder;
use Qqzeng\Ip\QzdbReader;
use Qqzeng\Ip\GeoInfo;

// 通过文件路径加载（默认：校验 CRC、加载第 0 个分组）
$reader = QzdbBuilder::path('qqzeng_ip_std_china.qzdb')->build();

// 单次查询：命中返回 GeoInfo，未命中或 IP 非法时返回 null（不会抛异常）
$info = $reader->find('114.114.114.114');
if ($info !== null) {
    echo $info->toPipe() . "\n";                       // 管道符分隔：国家|省份|城市|ISP|...
    echo $info->toJson() . "\n";                       // 紧凑 JSON 字符串
    echo $info->getCountry() . ' / ' . $info->getProvince() . ' / ' . $info->getCity() . "\n";
    echo 'ISP=' . $info->getIsp() . ', ASN=' . $info->getAsn() . "\n";
}

// 管道符格式（未命中返回空字符串 ""，适合直接落库 / 日志）
$pipe = $reader->findStr('240e:390:1:1::1');

// 仅取内部行号（最轻量，不涉及字段解析）
$rowId = $reader->lookupRowId('8.8.8.8');
```

> **注意：查询语义约定（与 Java 不同，与 C# 一致）**
>
> - `find*` 系列方法在 **IP 未命中** 或 **IP 格式非法** 时均返回 `null` / `0`，**不抛异常**（Fail-Soft 查询）。
> - 只有**数据库文件损坏、格式不支持、CRC 校验失败等加载期错误**，`build()` / `load()` / `reload()` 才会抛出 `QzdbException`（见[第 12 节](#12-错误处理)）。
>
> 因此：对来自外部的不可信 IP 文本，可直接用 `find()` 而无需 try/catch；若需区分"未命中"与"格式非法"，请用 `findBatch` / `findStream` 的 `BatchResult`（仅格式非法才会进入 `hasError()`，见[第 8 节](#8-批量与流式查询)）。

---

## 4. 加载数据库

所有加载入口推荐使用 **`QzdbBuilder`**（不可变链式调用，`build()` 返回 `QzdbReader`）。仍保留旧式 `new QzdbReader($path)` 构造器以保证向后兼容，但新代码请统一使用 Builder。

### 4.1 从文件路径加载

```php
// 最简：路径 + 默认第 0 分组 + 校验 CRC
$reader = QzdbBuilder::path('qqzeng_ip_std_china.qzdb')->build();

// 完整选项
$reader = QzdbBuilder::path('qqzeng_ip_all.qzdb')
    ->groupIndex(1)        // 选择数据库中的第 N 个分组（多分组文件时用于选不同数据维度）
    ->verifyCrc(false)      // 关闭 CRC32 校验（仅在你已离线校验过、追求极限加载速度时关闭）
    ->build();
```

### 4.2 从内存缓冲区 / 流加载（Serverless / 内嵌资源）

适用于把 `.qzdb` 作为嵌入式资源、或运行时从对象存储 / 网络下载到内存后直接解析，避免落盘：

```php
// 字节串（Builder 内部会拷贝，调用方可自由修改 / 释放原字符串）
$bytes = file_get_contents('qqzeng_ip_std_china.qzdb');
$reader = QzdbBuilder::bytes($bytes)->build();

// 已打开的流句柄（fopen 返回的资源；内部按需 fseek/fread，适合超大文件）
$fh = fopen('qqzeng_ip_std_china.qzdb', 'rb');
$reader = QzdbBuilder::stream($fh)->build();
```

### 4.3 关于分组（GroupIndex）

一个 `.qzdb` 文件可内嵌多个数据分组（如 `std` / `asn` / `max` 等不同维度）。`groupIndex` 指定加载哪一个；不传时默认 `0`。加载后可通过 `$reader->getEdition()` / `$reader->getFieldNames()` 确认实际加载的维度与字段。

### 4.4 加载异常处理

`build()` / `load()` 可能在以下情况抛出 `QzdbException`（务必在启动期 `try/catch`，避免程序崩溃）：

| 错误码（`QzdbException->getCode()`） | 常量 | 触发场景 |
|-----------|------|---------|
| `6` | `QzdbReader::ERROR_BAD_MAGIC` | 文件头不是 `QZDB` 魔数 |
| `5` | `QzdbReader::ERROR_BAD_HEADER` | 文件头尺寸异常 |
| `7` | `QzdbReader::ERROR_UNSUPPORTED` | 格式版本不受支持 |
| `2` | `QzdbReader::ERROR_CORRUPTED` | 分区越界、分组数为 0，或 **CRC32 校验不匹配**（数据损坏 / 被截断） |
| `4` | `QzdbReader::ERROR_INVALID_PARAM` | `groupIndex` 超出范围、字段宽度非法等 |
| `1` | `QzdbReader::ERROR_NOT_FOUND` | 文件不存在或无读取权限 |

### 4.5 大文件自适应流式模式

PHP SDK 会根据文件大小与 `memory_limit` 自动选择加载方式：

- **触发条件**：`filesize(db) > memory_limit * 0.5`（且 `memory_limit` 非 `-1` 无限制）时，自动切换为**流式模式**（`fopen` + 按需 `fseek`/`fread`）；否则一次性 `file_get_contents` 读入内存（**缓冲模式**）。
- **两种模式解析结果完全一致**，共用同一套 `readBytes()` 读取入口，唯一区别是性能特征。

```php
// 不需要手动指定，SDK 会自动判断；如需强制观察当前走的是哪种模式，可通过反射或
// 提前对比 filesize() 与 ini_get('memory_limit') 自行预判
$reader = new QzdbReader('qqzeng_ip_ult_global.qzdb');
```

**性能提示**：流式模式下，Trie 遍历的**每一步子节点读取都对应一次 `fseek`+`fread` 系统调用**（IPv4 最多 16 步、IPv6 最多 `v6_jump_bits` 之后 128-N 步），相比缓冲模式（纯内存访问）会有**数量级级别**的延迟差距。

**生产环境建议**：
1. 如果部署环境允许，优先调高 `memory_limit`，让常用数据库文件走缓冲模式；
2. 如果必须使用流式模式（容器内存受限等场景），务必提前用 `php` 自带的 `microtime()` 或 `hrtime()` 对目标数据库文件做一次真实 QPS 基准测试，确认延迟是否满足业务 SLA，不要直接照搬本文档缓冲模式下的性能数字做容量规划；
3. 流式模式更适合**低频、单次查询**的脚本化场景（如 CLI 批处理工具），持续高 QPS 服务场景建议评估是否能保证走缓冲模式。

---

## 5. 查询 API

`QzdbReader` 提供多种输入形态的查询。下表列出**全部公开方法**：

| 方法 | 签名 | 返回 | 说明 |
|------|------|------|------|
| 字符串查询 | `?GeoInfo find($ipStr)` | `GeoInfo\|null` | 按字符串查（IPv4 / IPv6 / IPv4 映射地址均可）；未命中 / 非法返回 `null` |
| 整数查询 | `?GeoInfo findUint(int $ipInt)` | `GeoInfo\|null` | 按 IPv4 的 `uint32` 整型查（主机序，即 `(a<<24)\|(b<<16)\|(c<<8)\|d`） |
| 字节查询 | `?GeoInfo findBytes(string $bytes)` | `GeoInfo\|null` | 按 4 字节（IPv4，网络序）或 16 字节（IPv6）原始字节查 |
| IPv6 二进制 | `?GeoInfo findV6Bin(string $ipBin)` | `GeoInfo\|null` | 按 16 字节二进制 IPv6 查（纯 V6 Trie 遍历，**不会**自动降级到 V4；若持有 IPv4-mapped 地址请用 `findBytes`） |
| 字段子集 | `?GeoInfo findFields($ipStr, $fieldNames = null)` | `GeoInfo\|null` | 只解析指定字段，减少不必要的字符串分配 |
| 管道字符串 | `string findStr($ipStr)` | `string` | 直接返回 `toPipe()` 结果；未命中返回 `""` |
| 行号查询 | `int lookupRowId($ipStr)` | `int` | 仅返回内部行号（不含字段，最轻）；非法 / 未命中返回 `0` |
| 行号（整数） | `int lookupRowIdUint(int $ipInt)` | `int` | `findUint` 的轻量版，只返回行号 |
| 行号（字节） | `int lookupRowIdBytes(string $bytes)` | `int` | `findBytes` 的轻量版，只返回行号；非 4/16 字节返回 `0` |
| 行号（IPv6） | `int lookupRowIdV6(string $ipBin)` | `int` | 16 字节二进制 IPv6 的行号查询 |
| 反查 ID | `?RowIds lookupIds(int $rowId)` | `RowIds\|null` | 由行号反查 Geo / ASN / Usage 三类索引 ID；越界返回 `null` |
| 批量查询 | `array findBatch(array $ips)` | `BatchResult[]` | 批量字符串查询，逐条容错 |
| 批量字段 | `array findBatchFields(array $ips, $fields)` | `BatchResult[]` | 批量 + 字段子集 |
| 流式查询 | `Generator findStream(iterable $ips)` | `Generator<BatchResult>` | 惰性 `yield` 流式逐条产出，内存恒定 |
| 流式查询（规范名） | `Generator findIter(iterable $ips)` | `Generator<BatchResult>` | 同 `findStream`，规范 A.7 要求的方法名 |

### 5.1 IP 输入约定

- **`find($ipStr)`**：接受点分十进制（`1.2.3.4`）与完整 / 压缩 IPv6（`2001:db8::1`）、IPv4 映射地址（`::ffff:1.2.3.4`、`::ffff:102:304`）；非法格式**返回 `null`**（不抛异常）。
- **`findUint(int $ipInt)`**：`$ipInt` 应为 IPv4 地址的 **主机序 `uint32`**（即 `(a<<24)|(b<<16)|(c<<8)|d`）。若你手上是网络序字节，请先转换或改用 `findBytes`。
- **`findBytes(string $bytes)`** / **`findV6Bin(string $ipBin)`**：4 字节按网络序（高位在前）解析为 IPv4；16 字节解析为 IPv6。

### 5.2 行号与 `RowIds`

`lookupIds(int $rowId)` 返回 `RowIds` 只读对象，包含：

```php
class RowIds {
    public int $geoId;
    public int $asnId;
    public int $usageId;
}
```

常用于：你自己持有"行号"做缓存 / 批处理，再按需用 `lookupIds` 反查各维度 ID，或构造跨维度关联。

---

## 6. 结果对象 `GeoInfo`

`find*` 系列返回 `GeoInfo|null`。它是**不可变**的结果对象（`ArrayAccess` 只读，写入/删除抛 `\RuntimeException`），提供三种读取形态：

### 6.1 通用取字段 `get(name)`

```php
$country = $info->get('country');        // 大小写、下划线 / 连字符不敏感：country / Country / country_code 等价
$isp     = $info->get('isp');
```

字段名匹配会**忽略大小写和下划线 / 连字符**（例如 `country_code`、`CountryCode`、`country-code`、`COUNTRY_CODE` 等同）。未命中返回 `""`，**不会抛异常**。还支持 `ArrayAccess`（`$info['country']`）。

### 6.2 强类型便捷方法

| 方法 | 返回类型 | 含义 |
|------|---------|------|
| `getCountry()` / `getCountryEn()` / `getCountryAlpha2()` / `getCountryAlpha3()` | `string` | 国家 |
| `getProvince()` / `getProvinceEn()` | `string` | 省份 |
| `getCity()` / `getCityEn()` / `getDistrict()` | `string` | 城市 / 区县 |
| `getIsp()` / `getIspEn()` | `string` | 运营商 |
| `getAsn()` | `?int` | ASN（自治域号），缺省返回 `null` |
| `getAsName()` / `getAsDomain()` | `string` | ASN 名称 / 域名 |
| `getGeoId()` | `?int` | 地理 ID |
| `getLongitude()` / `getLatitude()` | `?float` | 经纬度，缺省返回 `null` |
| `getTimezone()` | `string` | 时区 |
| `getUsageType()` | `UsageType` | 用途分类（见下） |
| `getCurrencyCode()` / `getCurrencyName()` / `getPhonePrefix()` / `getEmojiFlag()` / `getLanguages()` | `string` | 货币 / 名称 / 电话区号 / 国旗 / 语言 |

> **注意 `getCidr()`**：`GeoInfo` 的字段来自数据库，**不含 `cidr` 字段**，因此 `getCidr()` 恒返回空字符串 `""`。如需网段信息，请使用 `$reader->lookupCidr($ip)`（见[第 7 节](#7-cidr-网段反查)）。

### 6.3 序列化输出

| 方法 | 说明 |
|------|------|
| `toPipe()` / `toPipeString()` / `__toString()` | 用 `\|` 连接所有字段（带惰性缓存，重复调用零重建） |
| `toJson()` | 紧凑 JSON；数值字段（asn / 经纬度 / geo_id 等）自动输出为 JSON 数字，缺省为 `null`，键名保持原始 `snake_case` |
| `toMap()` | `array<string,string>`，便于泛型消费 |

### 6.4 用途分类 `UsageType`

`getUsageType()` 返回 `UsageType`（抽象基类）：

```php
$u = $info->getUsageType();
$known = $u->isKnown();            // 是否为预定义分类
$zh    = $u->getDisplayZh();      // 中文名：如 "云服务"
$en    = $u->getDisplayEn();      // 英文名：如 "Cloud"
$desc  = $u->getDescription();    // 详细描述
$raw   = $u->rawValue();          // 原始编码字符串（未知场景时保留原始值）
```

预定义分类（共 21 个）：`AICrawler`(AI 爬虫)、`Backbone`(骨干网)、`Broadband`(宽带)、`Business`(企业)、`CDN`、`Cloud`(云服务)、`DNS`、`DataCenter`(数据中心)、`Education`(教育网)、`Finance`(金融)、`Government`(政府)、`ISP`、`IXP`(交换中心)、`IoT`(物联网)、`Mobile`(移动网络)、`Reserved`(保留地址)、`Satellite`(卫星互联网)、`Spider`(爬虫)、`Streaming`(流媒体)、`Unknown`(未知)、`VPN`(VPN/代理)。

未命中预定义值时 `isKnown() == false`，返回一个 `UnknownUsageType` 安全兜底（**不崩溃**），`rawValue()` 保留原始字符串。可用 `KnownUsageType::fromRaw($raw)` 把原始字符串解析为已知实例（未知时返回 `null`）。

> **浮点格式约定**：经纬度等浮点字段按契约 §4 以 **6 位小数** 渲染（整数值无小数点，如 `118.767410`；`NaN` / `Inf` 返回 `""`）。本 SDK 在构造时执行 `setlocale(LC_NUMERIC, 'C')`，保证浮点格式与系统 locale 无关。

---

## 7. CIDR 网段反查

QZDB 数据库本身**不存储 CIDR**，但 Trie 每个叶子对应构建时的一条 CIDR 记录，叶子深度 = 前缀长度 N。本 SDK 提供从 IP **反查其所属最具体网段**的能力（由 Trie 匹配深度重建），IP 未覆盖时返回 `null`：

| 方法 | 签名 | 返回 | 说明 |
|------|------|------|------|
| 字符串反查 | `?string lookupCidr($ipStr)` | `string\|null` | 如 `"1.0.1.0/24"`、`"2001:218::/32"`；未覆盖返回 `null` |
| 整数反查 | `?string lookupCidrUint(int $ipInt)` | `string\|null` | IPv4 `uint32` 入口；未覆盖返回 `null` |
| 字节反查 | `?string lookupCidrBytes(string $bytes)` | `string\|null` | 4 字节（V4）/ 16 字节（V6、含 mapped）；长度非 4/16 返回 `null` |

```php
$reader = QzdbBuilder::path('qqzeng_ip_std_china.qzdb')->build();
$cidr4 = $reader->lookupCidr('223.5.5.5');     // 例如 "223.5.5.0/24"
$cidr6 = $reader->lookupCidr('2001:218::1');   // 例如 "2001:218::/32"，无 V6 数据时可能为 null
if ($cidr4 !== null) echo $cidr4 . "\n";
```

> IPv4-mapped IPv6（如 `::ffff:223.5.5.5`）按规范剥离后走 V4 Trie，返回 V4 CIDR。IPv6 地址按 RFC 5952 压缩输出。

---

## 8. 批量与流式查询

```php
// 批量：一次性返回数组，单条异常不影响其它条目
$results = $reader->findBatch(['1.1.1.1', '8.8.8.8', 'bad-ip']);
foreach ($results as $r) {
    if ($r->isSuccess())  echo $r->info->toPipe() . "\n";
    elseif ($r->isNotFound()) echo "未命中\n";
    else echo '错误: ' . $r->error->getMessage() . "\n";
}

// 流式：惰性 yield 产出，适合超大数据集 / 管道消费（内存恒定）
foreach ($reader->findStream($hugeIpList) as $r) {
    if ($r->isSuccess()) process($r->result);
}
```

`BatchResult` 是只读对象：

```php
class BatchResult {
    public string     $input;   // 原始输入
    public ?GeoInfo   $info;    // 命中结果（否则 null）
    public ?QzdbException $error; // 仅当输入格式非法 / 底层故障时非空
    public function isSuccess(): bool;   // error === null 且 命中
    public function isNotFound(): bool;  // error === null 且 未命中
    public function hasError():   bool;  // input 格式非法 或 底层故障
}
```

> **性能提示**：`findFields($ip, $fields)` 与 `findBatch` 配合，可只对需要的字段做解析，减少大批量查询下的字符串分配压力。

---

## 9. 链式多库查询 `ChainedReader`

当你有多个 `.qzdb`（例如"国内库 + 全球库"、"基础库 + 精细库"），可用 `ChainedReader` 把多个 `QzdbReader` 组合成一个逻辑查询器，支持三种合并模式：

| 工厂方法 | 模式 | 行为 |
|----------|------|------|
| `ChainedReader::chain(...)` | `FALLBACK` | 依次查询，返回**第一个命中**的结果 |
| `ChainedReader::chainMerge(...)` | `MERGE` | 合并所有命中；字段空缺才由后库补充（先库优先） |
| `ChainedReader::chainMergeOverride(...)` | `MERGE_OVERRIDE` | 合并所有命中；后库的非空值**覆盖**先库 |

```php
use Qqzeng\Ip\ChainedReader;

$china  = QzdbBuilder::path('qqzeng_ip_std_china.qzdb')->build();
$global = QzdbBuilder::path('qqzeng_ip_ult_china.qzdb')->build();

// 国内优先，未命中回退全球
$chained = ChainedReader::chain($china, $global);
$info = $chained->find('8.8.8.8');

// 字段级合并（精细库补全基础库缺省字段）
$merged = ChainedReader::chainMerge($china, $global);
```

支持的方法：`find` / `findUint` / `findBytes` / `findFields` / `findBatch` / `findBatchFields` / `findStream` / `findIter`；以及聚合元信息 `getEditions()` / `getScopes()` / `getDataMonths()` / `getReaders()`（同 `editions()` / `scopes()` / `dataMonths()` / `readers()`）。

> **资源说明**：`ChainedReader` **不会关闭**其下的底层 `QzdbReader`，下层各 reader 需自行 `close()`（建议各自用 `try/finally` 或 `register_shutdown_function` 释放）。

---

## 10. 命名注册表 `QzdbRegistry`

用于按名字管理多个 reader（例如在不同模块间共享同一实例）。提供**实例级**与**进程全局级**两套 API：

```php
use Qqzeng\Ip\QzdbRegistry;

// 实例级
$reg = new QzdbRegistry();
$reg->register('china', 'qqzeng_ip_std_china.qzdb');        // 按路径注册（自动 build）
$reg->registerBuffer('embedded', $bytes);                  // 按内存字节注册
$r = $reg->get('china');
$reg->unregister('china');                                // 取消并 close 旧实例
$reg->clear();                                             // 关闭并清空全部

// 进程全局（静态快捷方式）
QzdbRegistry::registerGlobal('global', 'qqzeng_ip_ult_china.qzdb');
$g = QzdbRegistry::getGlobal('global');
QzdbRegistry::unregisterGlobal('global');
```

`register` / `registerGlobal` 会自动 `close()` 旧实例（同名覆盖时），避免句柄泄漏。

---

## 11. 热更新与生命周期

### 11.1 原子热更新（无需重启进程）

数据库文件更新后，只需调用 `reload` / `reloadBuffer`，**旧数据在整个加载过程中继续提供服务**；只有新快照完整构建成功后才原子切换：

```php
// 重新从文件加载（CRC 始终强制校验）
$reader->reload('qqzeng_ip_std_china_new.qzdb');

// 从内存缓冲加载
$reader->reloadBuffer($newBytes);
```

> 注意：`reload` / `reloadBuffer` **始终强制 CRC 校验**（与构造时 `verifyCrc` 选项无关），确保热更新不会加载损坏数据。若新文件损坏，旧快照继续服务，方法会抛出 `QzdbException`。

### 11.2 释放与并发安全

- `QzdbReader` 实现析构（`__destruct`）自动释放；也可显式调用 `$reader->close()` 释放内存引用。`close()` 幂等，可重复调用。
- 可用 `$reader->isClosed()` 检查是否已关闭；已关闭的 reader 再查询会抛出 `QzdbException`。
- **并发安全**：多个请求 / 协程可同时调用任意查询方法，互不阻塞（无锁读取快照）。`reload` 用原子引用替换，旧快照在 GC 回收前继续服务。

---

## 12. 错误处理

所有加载 / 解析期错误以 **`QzdbException`（`\Exception` 子类）** 抛出，携带整数错误码（见 [4.4](#44-加载异常处理) 表）：

```php
try {
    $reader = QzdbBuilder::path('qqzeng_ip_std_china.qzdb')->build();
} catch (\Qqzeng\Ip\QzdbException $ex) {
    echo '加载失败 [' . $ex->getCode() . ']: ' . $ex->getMessage() . "\n";
}
```

> **查询期**：普通"未命中"和"IP 格式非法"通过返回 `null` / `0` 表达，**不抛异常**（见[第 3 节](#3-快速开始)说明）。只有底层数据异常（如分组越界）才会抛出，批量 / 流式接口会将其封装进 `BatchResult->error` 而不中断整体。

---

## 13. 性能说明

本 SDK 在查询热路径上采用与 Java/.NET 实现同架构的热路径优化：

- **不可变快照架构**：查询只读快照引用指向的不可变状态，多请求零竞争；`reload` 重建全部状态后原子替换，旧快照在 GC 回收前继续服务。
- **流式 / 缓冲自适应**：文件大小超过 `memory_limit * 0.5` 时自动走 `fopen` + `fseek/fread` 流式读取；否则 `file_get_contents` 缓冲。两种模式共用 `readBytes()` 单一读取入口，解析结果**逐字节一致**。
- **per-snapshot 有界无锁 GeoInfo 缓存**：快照不可变 → 同一 `entryId` 永远解析出同一 `GeoInfo`。以 `groupIndex:entryId` 为键（开放寻址哈希），上限 `GEO_CACHE_LIMIT = 1 << 16`（约 196 KB / 快照）；**碰撞只重算、绝不返回错值**。对热点 IP（同段 / 邻近客户端、批量扫段）直接命中，减少重复字段解析与字符串分配（命中路径近零分配）。
- **零分配 IP 解析**：IPv4 直接整数运算；IPv6 严格解析（拒绝前导 0 / zone id / 双 `::`）。
- **SENTINEL 高位哨兵位（`0x80000000` / `0x800000`）在解析前剥离**，保证 trie 遍历正确还原叶子行号 / 前缀深度。

### 参考性能（同架构理论量级，实际随 PHP 版本 / CPU / 数据规模 / 查询分布而变）

| 场景 | 量级 | 说明 |
|------|------|------|
| 单线程 IPv4 / IPv6 查询 | 数百万 QPS 量级 | 50 万随机散布 IP（缓存最不利情形），与 Java / .NET 实现同架构 |
| 多请求并发 | 安全无锁 | 并发查询零竞争退化 |
| 热点 IP 重复查询 | 吞吐进一步放大、分配趋零 | 同 IP 重复查询，GC 压力显著下降 |

> 上述数字用于说明量级，非 SLA；以你自身环境的基准测试为准（仓库内 `bench_all.php`）。

---

## 14. 维护与升级

### 14.1 更新数据（最频繁的操作）

不需要重新编译或重启进程：

1. 从官方渠道获取新的 `.qzdb` 文件（注意 `getDataMonth()` / `getBuildTime()` 是否更新）。
2. 调用 `$reader->reload($newPath)` 或 `$reader->reloadBuffer($newBytes)` 原子热更新。
3. 用 `$reader->getDataMonth()` / `$reader->getBuildTime()` / `$reader->getVersion()` 确认已加载的数据版本。

### 14.2 升级 SDK

本 SDK 单文件发布，升级即替换 `QzdbReader.php`。版本遵循 **SemVer**：

| 变更类型 | 版本位 | 影响 |
|----------|--------|------|
| 破坏性 API 变更 | 主版本 `x` | 需改调用代码 |
| 向后兼容的功能新增 | 次版本 `y` | 直接升级 |
| Bug 修复 / 性能优化 | 补丁 `z` | 直接升级（建议始终跟进） |

### 14.3 校验数据完整性

```php
// 重新计算全文件 CRC32 并与 Header 存储值比对（只读操作）
$ok = $reader->verifyCrc();
// 或查看文件指纹（8 位小写十六进制）
$hash = $reader->getFileHash();
```

### 14.4 兼容性注意

- 命名空间固定为 `Qqzeng\Ip`，升级不会造成命名空间漂移。
- 最低 PHP 8.1；建议 8.5.x 以获得最佳性能。

---

## 15. 项目结构

`php/` 目录（本 SDK 源码，全部集中于单文件）：

| 文件 | 职责 |
|------|------|
| `QzdbReader.php` | 核心读取器与全部类：`QzdbReader`（加载 / Trie / 查询 / 热更新 / CRC / 元信息 / CIDR）、`GeoInfo`（字段归一化 / 序列化 / 语义 Getter）、`QzdbBuilder`（加载入口）、`QzdbRegistry`（实例级 + 全局级注册表）、`ChainedReader`（多库链式组合）、`BatchResult`（批量三态结果）、`RowIds`（行号三元组）、`UsageType` + `KnownUsageType` + `UnknownUsageType`（21 已知 + 未知兜底）、`QzdbException`（异常类型与错误码常量） |
| `test.php` | 调用示例（演示 Builder 加载、查询、语义 Getter、CIDR、元信息；`php test.php` 可跑通） |
| `bench_all.php` | 双栈吞吐基准（IPv4 走 `findUint`，IPv6 走 `findV6Bin`） |
| `batch_cli.php` | 命令行批量查询工具示例 |
| `tier1_test.php` | 单元测试（无 DB，105 断言，覆盖解析 / 归一化 / 浮点 / Fail-Closed / 缓存等） |
| `tier2_golden.php` | 黄金校验（读取 `golden_vectors.json`，对真实库断言 `find(ip)->toPipe() === expected`，0 失败；向量由被测代码自身生成，只证跨语言一致） |
| `csv_oracle_test.php` | **独立真值校验**（以源数据 `test_data_202608/<edition>/china/*_range.csv` 为裁判，全局 + 区间内抽样共 22000 样本比对 country/province/city/isp，0 失配；证明对*真值*答得对） |

运行测试：

```bash
php tier1_test.php                         # 无数据库依赖的单元测试
php tier2_golden.php                       # 需仓库根 test_data_202608/ 真实库，做黄金向量校验
php csv_oracle_test.php                    # 独立真值校验（需源 CSV + 真实库）
```

跨语言完整 API 规范见仓库根：`multi-lang/API_CONTRACT.md`（v2.4，单一事实来源）。

---

## License

[MIT](https://opensource.org/licenses/MIT)

<!-- commit: php: PHP SDK（纯 PHP 实现，缓冲与流式双模式） sync=1787463805 -->
