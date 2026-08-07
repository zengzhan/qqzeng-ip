# qzdb (Node.js)

> 纯离线、零第三方依赖的 **QZDB IP 地理定位数据库**官方 Node.js SDK（支持 IPv4 / IPv6 双栈）。

- **模块名**：`qzdb`（单文件 `qzdb.js`，`module.exports = QzdbReader`）
- **定位**：离线解析 `.qzdb` 二进制数据库文件，不依赖任何外部网络请求
- **架构**：不可变快照（immutable snapshot）+ 有界 `GeoInfo` 解码缓存，并发查询互不阻塞；`reload` 原子替换
- **运行要求**：Node.js **14+**（使用了 `Buffer`、`class`、Generator 等特性；建议 18+）
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
| Node.js | **14 或更高**（推荐 18+） |
| 依赖 | **无任何第三方运行时依赖**（单文件 `qzdb.js`，零 npm 依赖） |
| 操作系统 | Windows / Linux / macOS 均可 |
| 数据库文件 | `.qzdb` 格式（由官方数据构建工具生成，含所需分组的二进制数据） |

---

## 2. 安装

本 SDK 以**单文件**形式提供（`qzdb.js`）。无需 npm 安装，直接 `require` 即可：

```js
const QzdbReader = require('./qzdb');

// 相关类型均挂载在 QzdbReader 上：
//   QzdbReader.Builder       构造器
//   QzdbReader.QzdbRegistry  命名注册表
//   QzdbReader.ChainedReader 链式多库
//   QzdbReader.QzdbError     异常类型
//   QzdbReader.GeoInfo / UsageType / RowIds / BatchResult
```

> 说明：`module.exports = QzdbReader`，`QzdbReader` 是主读取类；其余类型一律以**静态属性**形式挂在它下面，保持一致命名空间。

---

## 3. 快速开始

```js
const QzdbReader = require('./qzdb');

// 通过文件路径加载（默认：校验 CRC、加载第 0 个分组）
const reader = new QzdbReader('qqzeng_ip_std_china.qzdb');

// 单次查询：未命中或 IP 非法时返回 null（不会抛异常）
const info = reader.find('114.114.114.114');
if (info) {
  console.log(info.toPipe());                 // 管道符分隔：亚洲|CN|中国|江苏|南京|中国电信
  console.log(info.toJson());                 // 紧凑 JSON 字符串
  console.log(`${info.getCountry()} / ${info.getProvince()} / ${info.getCity()}`);
  console.log(`ISP=${info.getIsp()}, ASN=${info.getAsn()}`);
}

// 管道符格式（未命中返回空字符串 ""，适合直接落库 / 日志）
const pipe = reader.findStr('240e:390:1:1::1');
console.log(pipe);

// 仅取内部行号（最轻量，不涉及字段解析）
const rowId = reader.lookupRowId('8.8.8.8');
console.log(rowId);

reader.close();
```

> **查询语义约定（与 C# / Java 一致，Fail-Soft）**
>
> - `find*` 系列方法在 **IP 未命中** 或 **IP 格式非法** 时均返回 `null` / `0` / `""`，**不抛异常**。
> - 只有**数据库文件损坏、格式不支持、CRC 校验失败等加载期错误**，`new QzdbReader(...)` / `load()` / `reload()` 才会抛出 `QzdbError`（见[第 12 节](#12-错误处理)）。
>
> 因此：对来自外部的不可信 IP 文本，可直接用 `find()` 而无需 try/catch。

---

## 4. 加载数据库

所有加载入口都遵循 **Fail-Closed**：魔数 / 头部 / CRC / 截断异常一律拒绝初始化（构造期即抛 `QzdbError`），不会加载半个损坏文件。

### 4.1 构造即加载（最简）

```js
// 参数：dbPath?, groupIndex = 0, verifyCrc = true
// 传 dbPath 时构造内自动调用 load()
const reader = new QzdbReader('qqzeng_ip_std_china.qzdb');
const reader2 = new QzdbReader('qqzeng_ip_ult_china.qzdb', 0, true); // 显式分组 + CRC
```

### 4.2 延迟加载 / 从缓冲区加载

```js
// 先建空实例，再 load
const reader = new QzdbReader();
reader.load('qqzeng_ip_std_china.qzdb');              // 等价于构造时传路径
reader.load('qqzeng_ip_std_china.qzdb', false);      // 第二个参数可临时关闭 CRC

// 从内存字节（Buffer / Uint8Array）加载，适合 Serverless / 内嵌资源
const bytes = require('fs').readFileSync('qqzeng_ip_std_china.qzdb');
const reader3 = new QzdbReader();
reader3.loadBuffer(bytes);
```

### 4.3 构造器 `Builder`（链式）

```js
const reader = QzdbReader.Builder('qqzeng_ip_std_china.qzdb')
  .groupIndex(0)       // 选择分组（多分组文件时用于选不同数据维度）
  .verifyCrc(true)     // 默认开启；关闭仅用于受信数据 / 基准测试
  .build();            // 返回已加载的 QzdbReader
```

### 4.4 关于分组（GroupIndex）

一个 `.qzdb` 文件可内嵌多个数据分组（如 `std` / `ult` 等不同维度）。`groupIndex` 指定加载哪一个；不传时默认 `0`。加载后可通过 `reader.getEdition()` / `reader.getFieldNames()` 确认实际加载的维度与字段。

> 必须在任何查询调用**之前**确定 `group_index`；它影响全部 `find*` / `lookup*` API。

### 4.5 进程级单例

```js
const reader = QzdbReader.getInstance('qqzeng_ip_std_china.qzdb'); // 首次惰性构建，后续复用
```

---

## 5. 查询 API

`QzdbReader` 提供多种输入形态的查询。下表列出**全部公开查询方法**：

| 方法 | 签名 | 返回 | 说明 |
|------|------|------|------|
| 字符串查询 | `find(ipStr)` | `GeoInfo \| null` | 按字符串查（IPv4 / IPv6 / IPv4 映射地址）；未命中 / 非法返回 `null` |
| 整数查询 | `findUint(ipInt)` | `GeoInfo \| null` | 按 IPv4 主机序 `uint32` 查 |
| 字节查询 | `findBytes(ipBytes)` | `GeoInfo \| null` | 按 4 字节（IPv4，网络序）或 16 字节（IPv6）原始字节查 |
| 字段子集 | `findFields(ipStr, fields)` | `GeoInfo \| null` | 只解析指定字段，减少不必要的字符串分配 |
| 管道字符串 | `findStr(ipStr)` | `string` | 直接返回 `toPipe()` 结果；未命中返回 `""` |
| 行号查询 | `lookupRowId(ipStr)` | `number` | 仅返回内部行号；非法 / 未命中返回 `0` |
| 行号（整数） | `lookupRowIdUint(ipInt)` | `number` | `findUint` 的轻量版，只返回行号 |
| 行号（IPv6） | `lookupRowIdV6(ipBin)` | `number` | 16 字节二进制 IPv6 的行号查询 |
| 行号（字节） | `lookupRowIdBytes(ipBytes)` | `number` | 4 / 16 字节；长度非 4/16 返回 `0` |
| 反查 ID | `lookupIds(rowId)` | `RowIds \| null` | 由行号反查 Geo / ASN / Usage 三类索引 ID；越界返回 `null` |
| 批量查询 | `findBatch(ips)` | `BatchResult[]` | 批量字符串查询，逐条容错 |
| 批量字段 | `findBatchFields(ips, fields)` | `BatchResult[]` | 批量 + 字段子集 |
| 流式查询 | `findStream(ips)` | `Generator<BatchResult>` | 惰性产出，内存恒定 |

### 5.1 IP 输入约定

- **`find(ipStr)`**：接受点分十进制（`1.2.3.4`）与完整 / 压缩 IPv6（`2001:db8::1`）、IPv4 映射地址（`::ffff:1.2.3.4`、`::ffff:102:304`）；非法格式**返回 `null`**（不抛异常）。
- **`findUint(ipInt)`**：`ipInt` 应为 IPv4 地址的 **主机序 `uint32`**（即 `(a<<24)|(b<<16)|(c<<8)|d`）。若手上是网络序字节，请先转换或改用 `findBytes`。
- **`findBytes(ipBytes)`**：前 12 字节为 0 且 `10..11` 字节为 `0xFF 0xFF` 时按 IPv4 映射处理，降级到 V4 trie。

### 5.2 行号与 `RowIds`

`lookupIds(rowId)` 返回 `RowIds` 对象：

```js
const ids = reader.lookupIds(rowId);
// ids = { geoId: number, asnId: number, usageId: number }
```

常用于：自己持有"行号"做缓存 / 批处理，再按需用 `find*` 取完整字段，或构造跨维度关联。

---

## 6. 结果对象 `GeoInfo`

`find*` 系列返回 `GeoInfo | null`。它是**不可变**的结果对象，提供三种读取形态：

### 6.1 通用取字段 `get(name)`

```js
const country = info.get('country');   // 大小写、下划线 / 连字符不敏感：country / Country / country_code 等价
const isp     = info.get('isp');
```

字段名匹配会**忽略大小写和下划线 / 连字符**（例如 `country_code`、`CountryCode`、`country-code`、`COUNTRY_CODE` 等同）。未命中返回 `""`，**不会抛异常**。

### 6.2 强类型便捷方法

| 方法 | 返回类型 | 含义 |
|------|---------|------|
| `getCountry()` / `getCountryEn()` / `getCountryAlpha2()` / `getCountryAlpha3()` | `string` | 国家 |
| `getProvince()` / `getProvinceEn()` | `string` | 省份 |
| `getCity()` / `getCityEn()` / `getDistrict()` | `string` | 城市 / 区县 |
| `getIsp()` / `getIspEn()` | `string` | 运营商 |
| `getAsn()` | `number \| null` | ASN（自治域号），缺省返回 `null` |
| `getAsName()` / `getAsDomain()` | `string` | ASN 名称 / 域名 |
| `getGeoId()` | `number \| null` | 地理 ID |
| `getLongitude()` / `getLatitude()` | `number \| null` | 经纬度，缺省返回 `null` |
| `getTimezone()` | `string` | 时区 |
| `getUsageType()` | `UsageType` | 用途分类（见下） |
| `getCurrencyCode()` / `getCurrencyName()` / `getPhonePrefix()` / `getEmojiFlag()` / `getLanguages()` | `string` | 货币 / 名称 / 电话区号 / 国旗 / 语言 |
| `getCidr()` | `string` | 始终返回 `""`（CIDR 请使用 `reader.lookupCidr(ip)`） |

### 6.3 序列化输出

| 方法 | 说明 |
|------|------|
| `toPipe()` / `toPipeString()` / `toString()` | 用 `\|` 连接所有字段（直接拼接，不二次解析；数值字段为 6 位小数定点格式，如 `116.400000`） |
| `toJson()` | 紧凑 JSON；数值字段（asn / 经纬度 / geo_id）自动输出为数字或 `null` |
| `toMap()` / `toDict()` | `Object<string,string>`，便于泛型消费 |
| `values()` / `fieldNames()` | 字段值数组 / 字段名数组 |

> **数值格式约定**：经纬度 / 数值字段在解码阶段即格式化为 **6 位定点小数**（`{:.6}`），`toPipe` 直接拼接该字符串；`NaN` / `Inf` 输出为 `""`。这与契约 `§8` 及黄金用例一致。

### 6.4 用途分类 `UsageType`

`getUsageType()` 返回 `UsageType`：

```js
const u = info.getUsageType();
const known = u.isKnown();          // 是否为预定义分类
const zh    = u.getDisplayZh();     // 中文名：如 "云服务"
const en    = u.getDisplayEn();     // 英文名：如 "Cloud"
const desc  = u.getDescription();   // 详细描述
const raw   = u.rawValue();         // 原始编码字符串（未知场景时保留原始值）
```

预定义分类（共 21 个）：`AICrawler`(AI 爬虫)、`Backbone`(骨干网)、`Broadband`(宽带)、`Business`(企业)、`CDN`、`Cloud`(云服务)、`DNS`、`DataCenter`(数据中心)、`Education`(教育网)、`Finance`(金融)、`Government`(政府)、`ISP`、`IXP`(交换中心)、`IoT`(物联网)、`Mobile`(移动网络)、`Reserved`(保留地址)、`Satellite`(卫星互联网)、`Spider`(爬虫)、`Streaming`(流媒体)、`Unknown`(未知)、`VPN`(VPN/代理)。

未命中预定义值时 `isKnown() === false`，返回一个安全兜底（**不崩溃**），`rawValue()` 保留原始字符串。

---

## 7. CIDR 网段反查

数据库本身**不存储 CIDR**，由 Trie 叶子深度重建网络地址（叶子深度 = 前缀长度 N；网络地址 = IP 高 N 位清零；V6 按 RFC 5952 压缩）：

```js
const cidr4 = reader.lookupCidr('223.5.5.5');        // 例如 "223.5.5.0/24"
const cidr6 = reader.lookupCidr('2001:218::1');      // 例如 "2001:218::/32"，无 V6 数据时可能为 null
const c1 = reader.lookupCidrUint(0x01020304);       // IPv4 uint32
const c2 = reader.lookupCidrBytes(buf16);           // 4 / 16 字节
```

| 方法 | 签名 | 返回 | 说明 |
|------|------|------|------|
| 字符串反查 | `lookupCidr(ipStr)` | `string \| null` | IP 未覆盖 / 非法返回 `null` |
| 整数反查 | `lookupCidrUint(ipInt)` | `string \| null` | IPv4 `uint32` 入口 |
| 字节反查 | `lookupCidrBytes(ipBytes)` | `string \| null` | 4 字节（V4）/ 16 字节（V6、含 mapped） |

> IPv4-mapped IPv6（如 `::ffff:223.5.5.5`）按规范剥离后走 V4 Trie，返回 V4 CIDR。

---

## 8. 批量与流式查询

```js
// 批量：一次性返回数组，单条异常不影响其它条目
const results = reader.findBatch(['1.1.1.1', '8.8.8.8', 'bad-ip']);
for (const r of results) {
  if (r.isSuccess())  console.log(`${r.input} => ${r.result.toPipe()}`);
  else if (r.isNotFound()) console.log(`${r.input} => 未命中`);
  else console.log(`${r.input} => 错误: ${r.error.message}`);
}

// 流式：惰性产出，适合超大数据集 / 管道消费（内存恒定）
for (const r of reader.findStream(hugeIpList)) {
  if (r.isSuccess()) process(r.result);
}
```

`BatchResult` 对象：

```js
// { input: string, result: GeoInfo | null, error: QzdbError | null }
//   isSuccess()  : error === null 且 命中
//   isNotFound() : error === null 且 未命中
//   hasError()   : input 格式非法 或 底层故障
```

> **性能提示**：`findFields(ip, fields)` 与 `findBatchFields` 配合，可只对需要的字段做解析，减少大批量查询下的字符串分配压力。

---

## 9. 链式多库查询 `ChainedReader`

当你有多个 `.qzdb`（例如"国内库 + 全球库"、"基础库 + 精细库"），可用 `ChainedReader` 把多个 `QzdbReader` 组合成一个逻辑查询器，支持三种合并模式：

| 工厂方法 | 模式 | 行为 |
|----------|------|------|
| `ChainedReader.chain(...readers)` | `FALLBACK` | 依次查询，返回**第一个命中**的结果 |
| `ChainedReader.chainMerge(...readers)` | `MERGE` | 合并所有命中；字段空缺才由后库补充（先库优先） |
| `ChainedReader.chainMergeOverride(...readers)` | `MERGE_OVERRIDE` | 合并所有命中；后库的非空值**覆盖**先库 |

```js
const { ChainedReader } = QzdbReader;   // 即 QzdbReader.ChainedReader

const china  = new QzdbReader('qqzeng_ip_std_china.qzdb');
const global = new QzdbReader('qqzeng_ip_ult_china.qzdb');

// 国内优先，未命中回退全球
const chained = ChainedReader.chain(china, global);
const info = chained.find('8.8.8.8');

// 字段级合并（精细库补全基础库缺省字段）
const merged = ChainedReader.chainMerge(china, global);
```

支持的方法：`find` / `findUint` / `findBytes` / `findFields` / `findBatch` / `findBatchFields`；以及聚合元信息 `editions()` / `scopes()` / `dataMonths()` / `readers()`。

---

## 10. 命名注册表 `QzdbRegistry`

用于按名字管理多个 reader（例如在不同模块间共享同一实例）：

```js
const { QzdbRegistry } = QzdbReader;   // 即 QzdbReader.QzdbRegistry

// 实例级
const reg = new QzdbRegistry();
reg.register('china', 'qqzeng_ip_std_china.qzdb');        // 按路径注册（自动 build）
reg.registerBuffer('embedded', bytes);                  // 按内存字节注册
const r = reg.get('china');
reg.unregister('china');                                // 取消并 close 旧实例
reg.clear();                                             // 关闭并清空全部

// 进程全局（静态快捷方式）
QzdbRegistry.registerGlobal('global', 'qqzeng_ip_ult_china.qzdb');
const g = QzdbRegistry.getGlobal('global');
QzdbRegistry.unregisterGlobal('global');
```

`register` / `registerGlobal` 会自动 `close()` 旧实例（同名覆盖时），避免句柄泄漏。

---

## 11. 热更新与生命周期

### 11.1 原子热更新（无需重启进程）

数据库文件更新后，只需调用 `reload` / `reloadBuffer`，**旧数据在整个加载过程中继续提供服务**；只有新快照完整构建成功后才原子切换：

```js
reader.reload('qqzeng_ip_std_china_new.qzdb');     // 重新从文件加载（CRC 始终强制校验）
reader.reloadBuffer(newBytes);                     // 从内存缓冲加载
```

> 注意：`reload` / `reloadBuffer` **始终强制 CRC 校验**（与构造时 `verifyCrc` 选项无关），确保热更新不会加载损坏数据。若新文件损坏，旧快照继续服务，方法会抛出 `QzdbError`。

### 11.2 释放与并发安全

- `reader.close()` 释放内存引用（幂等，可重复调用）；`reader.clear()` 等同释放。已关闭的 reader 再查询会返回 `null`。
- **并发安全**：多个请求 / 协程可同时调用任意查询方法，互不阻塞（只读快照）。`reload` 用原子引用替换，旧快照在 GC 回收前继续服务。

---

## 12. 错误处理

加载 / 解析期错误以 **`QzdbError`（`Error` 子类）** 抛出，携带**字符串错误码**（`e.code`）：

```js
const { QzdbReader, QzdbError } = require('./qzdb');

try {
  const reader = new QzdbReader('qqzeng_ip_std_china.qzdb');
} catch (ex) {
  if (ex instanceof QzdbError) {
    console.log('加载失败 [' + ex.code + ']: ' + ex.message);
    // ex.code ∈ 'BAD_MAGIC' | 'BAD_HEADER' | 'UNSUPPORTED' | 'CORRUPTED'
    //           | 'INVALID_PARAM' | 'NOT_FOUND' | 'OUT_OF_BOUNDS'
  }
}
```

错误码（字符串）：

| 错误码（`e.code`） | 触发场景 |
|--------------------|---------|
| `NOT_FOUND` | 文件不存在或无读取权限 |
| `CORRUPTED` | 分区越界、分组数为 0，或 **CRC32 校验不匹配**（数据损坏 / 被截断） |
| `OUT_OF_BOUNDS` | 数据区被截断、偏移越界 |
| `INVALID_PARAM` | `groupIndex` 超出范围、字段宽度非法等 |
| `BAD_HEADER` | 文件头尺寸异常 |
| `BAD_MAGIC` | 文件头不是 `QZDB` 魔数 |
| `UNSUPPORTED` | 格式版本不受支持 |

> **查询期**：普通「未命中」和「IP 格式非法」通过返回 `null` / `0` / `""` 表达，**不抛异常**（见[第 3 节](#3-快速开始)说明）。只有底层数据异常（如分组越界）才会抛出，批量 / 流式接口会将其封装进 `BatchResult.error` 而不中断整体。

---

## 13. 性能说明

- **不可变快照架构**：查询只读快照指向的不可变状态，多请求零竞争；`reload` 重建全部状态后原子替换，旧快照在 GC 回收前继续服务。
- **per-snapshot 有界 `GeoInfo` 解码缓存**：快照不可变 → 同一 `entryId` 永远解析出同一 `GeoInfo`。以 `(groupIndex, entryId)` 为键（开放寻址哈希），上限受单快照约束、有界；**碰撞只重算、绝不返回错值**。对热点 IP（同段 / 邻近客户端、批量扫段）直接命中，减少重复字段解析与字符串分配。
- **零分配 IP 解析**：IPv4 直接整数运算；IPv6 严格解析（拒绝前导 0 / zone id / 双 `::`）。
- **SENTINEL 高位哨兵位（`0x80000000` / `0x800000`）在解析前剥离**，保证 trie 遍历正确还原叶子行号 / 前缀深度。
- **调用方缓冲查询**：`*_buf` 系列与 `findStr` 全程使用调用方缓冲，`malloc` 次数趋近于 0。

> 实际吞吐随 Node 版本 / CPU / 数据规模 / 查询分布而变；上述设计为量级说明，非 SLA。

---

## 14. 维护与升级

### 14.1 更新数据（最频繁的操作）

不需要重新编译或重启进程：

1. 从官方渠道获取新的 `.qzdb` 文件（注意 `getDataMonth()` / `getBuildTime()` 是否更新）。
2. 调用 `reader.reload(newPath)` 或 `reader.reloadBuffer(newBytes)` 原子热更新。
3. 用 `reader.getDataMonth()` / `reader.getBuildTime()` / `reader.getVersion()` 确认已加载的数据版本。

### 14.2 校验数据完整性

```js
// 重新计算全文件 CRC32 并与 Header 存储值比对（只读操作），返回 boolean
const ok = reader.verifyCrc();
// 或查看文件指纹（8 位小写十六进制）
const hash = reader.getFileHash();
```

### 14.3 升级 SDK

本 SDK 单文件发布，升级即替换 `qzdb.js`。版本遵循 **SemVer**。

---

## 15. 项目结构

`nodejs/` 目录（本 SDK 源码）：

| 文件 | 职责 |
|------|------|
| `qzdb.js` | 核心实现（单文件）：`QzdbReader`（加载 / Trie / 查询 / 热更新 / CRC / 元信息 / CIDR）、`QzdbBuilder`、`QzdbRegistry`、`ChainedReader`、`BatchResult`、`RowIds`、`GeoInfo`、`UsageType` + 兜底、`QzdbError` |
| `test_suite.js` | Tier1 单元测试（无 DB，含解析 / 归一化 / 浮点 / Fail-Closed / 缓存 等）+ Tier2 黄金校验（读取 `tools/golden_vectors.json`，0 失败；向量由被测代码自身生成，只证跨语言一致） |
| `tier2_csv_verify.js` | **独立地面真值校验器**（以源数据 `test_data_202608/<ver>/<scope>/*.csv` 为裁判，`toPipe()` 逐字段比对，覆盖 std/pro/max/ult/asn × china/global 共 10 库；浮点字段按 6 位小数归一，证明对*真值*答得对） |
| `tier3_concurrent.js` | Tier3 并发安全（16 Worker 线程 × 10 万 = 160 万 op，验证无锁快照架构，0 错误） |
| `batch_cli.js` | 命令行批量查询示例 |
| `bench_all.js` | 双栈吞吐基准 |
| `cmp_node_py.js` | 与 Python 实现的交叉比对工具 |

运行测试：

```bash
node test_suite.js              # Tier1 单元测试 + Tier2 黄金校验（需同仓 multi-lang/data 真实库）
node tier2_csv_verify.js        # 独立地面真值校验（需 test_data_202608 源 CSV + qzdb，全 10 库抽样）
node tier2_csv_verify.js full std china   # 单库全量
node tier3_concurrent.js        # 并发安全验证
```

跨语言完整 API 规范见仓库根 [`API_CONTRACT.md`](../../API_CONTRACT.md)（v2.4，单一事实来源）。

---

## License

[MIT](https://opensource.org/licenses/MIT)
