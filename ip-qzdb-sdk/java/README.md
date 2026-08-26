# QQZeng QZDB — Java SDK

> 纯离线、零依赖、高性能的 **QZDB IP 地理定位数据库**官方 Java SDK（支持 IPv4 / IPv6 双栈）。

- **官方坐标**：`com.qqzeng:qzdb`（已发布至 Maven Central）；Java 包名即 `com.qqzeng.qzdb`
- **定位**：离线解析 `.qzdb` 二进制数据库文件，不依赖任何外部网络请求
- **架构**：无锁快照（lock-free snapshot）——并发查询互不阻塞，`reload` 原子切换（原子引用替换）
- **运行要求**：JDK 21+（编译目标 `maven.compiler.release=21`）
- **许可**：MIT

---

## 目录

1. [环境要求](#1-环境要求)
2. [安装](#2-安装)
3. [快速开始](#3-快速开始)
4. [加载数据库](#4-加载数据库)
5. [查询 API](#5-查询-api)
6. [结果对象 `GeoInfo`](#6-结果对象-geoinfo)
7. [CIDR 网段反查（Java 特有）](#7-cidr-网段反查java-特有)
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
| JDK | **21 或更高**（SDK 使用了 `record`、`sealed interface`、`switch` 表达式、`Stream` 等特性） |
| 构建工具 | Maven 3.6+（也可直接用 `javac` 编译 `src/main/java`） |
| 操作系统 | Windows / Linux / macOS 均可 |
| 数据库文件 | `.qzdb` 格式（由官方数据构建工具生成，含所需分组的二进制数据） |
| 依赖 | **无第三方运行时依赖**（零依赖） |

---

## 2. 安装

### 2.1 Maven / Gradle 坐标

已发布至 **Maven Central**，构建工具会自动拉取 `qzdb-1.0.0.jar` 及其 `-sources.jar` / `-javadoc.jar`，**无需克隆本仓库源码**。

```xml
<!-- Maven -->
<dependency>
    <groupId>com.qqzeng</groupId>
    <artifactId>qzdb</artifactId>
    <version>1.0.0</version>
</dependency>
```

```groovy
// Gradle (Groovy DSL)
implementation 'com.qqzeng:qzdb:1.0.0'
```

```kotlin
// Gradle (Kotlin DSL)
implementation("com.qqzeng:qzdb:1.0.0")
```

> 若需本地构建：在 `multi-lang/java/` 执行 `mvn install` 安装到本地仓库；或直接把 `src/main/java/com/qqzeng/qzdb/` 目录加入你的源码树编译。

### 2.2 源码编译

```bash
cd multi-lang/java
mvn -q compile          # 产出 target/classes
# 或打成 jar
mvn -q package -DskipTests
```

Java 代码统一包名：

```java
import com.qqzeng.qzdb.QzdbReader;
import com.qqzeng.qzdb.GeoInfo;
```

---

## 3. 快速开始

```java
import com.qqzeng.qzdb.QzdbReader;
import com.qqzeng.qzdb.GeoInfo;

import java.io.File;
import java.util.Optional;

public class Demo {
    public static void main(String[] args) {
        // 通过文件路径加载（默认：校验 CRC、加载第 0 个分组）
        // QzdbReader 实现 AutoCloseable，优先用 try-with-resources 自动释放
        try (QzdbReader reader = new QzdbReader.Builder(new File("ip_china.qzdb")).build()) {

            // 单次查询：命中返回 Optional<GeoInfo>，未命中返回 Optional.empty()
            Optional<GeoInfo> info = reader.find("114.114.114.114");
            info.ifPresent(g -> {
                System.out.println(g.toPipeString());                    // 管道符分隔：国家|省份|城市|ISP|...
                System.out.println(g.toJson());                          // 紧凑 JSON 字符串
                System.out.println(g.getCountry() + " / " + g.getProvince() + " / " + g.getCity());
                System.out.println("ISP=" + g.getIsp() + ", ASN=" + g.getAsn());
            });

            // 管道符格式（未命中返回空字符串 ""，适合直接落库 / 日志）
            String pipe = reader.findStr("240e:390:1:1::1");

            // 仅取内部行号（最轻量，不涉及字段解析）
            int rowId = reader.lookupRowId("8.8.8.8");
        }
    }
}
```

> **注意：查询语义约定（与部分语言 SDK 不同，务必留意）**
>
> - `find*` 系列方法在 **IP 未命中** 时返回 `Optional.empty()`，**不抛异常**。
> - 但 **IP 格式非法**（如 `"abc.def"`、`"256.1.1.1"`、带端口 `"1.1.1.1:80"`）时，会抛出 **`QzdbException`（错误码 `INVALID_IP`）**，而不是返回 empty。
> - 只有在**数据库文件损坏、格式不支持、CRC 校验失败等加载期错误**时，`build()` 才会抛出 `QzdbException`（见[第 12 节](#12-错误处理)）。
>
> 因此：对来自外部、可能非法的 IP 文本，建议包一层 `try/catch (QzdbException)`；对已知合法的 IP（如来自 `int`/`byte[]` 或已校验过的输入）可直接用 `findUint`/`findBytes`。

---

## 4. 加载数据库

所有加载入口都通过 **`QzdbReader.Builder`** 构造。Builder 不可变链式调用，`build()` 返回 `QzdbReader`（实现 `AutoCloseable`）。

### 4.1 从文件路径加载

```java
// 最简：路径 + 默认第 0 分组 + 校验 CRC
try (QzdbReader reader = new QzdbReader.Builder(new File("ip_china.qzdb")).build()) {
    // ...
}

// 完整选项
QzdbReader reader = new QzdbReader.Builder(new File("ip_all.qzdb"))
    .groupIndex(1)        // 选择数据库中的第 N 个分组（多分组文件时用于选不同数据维度）
    .verifyCrc(false)     // 关闭 CRC32 校验（仅在你已离线校验过、追求极限加载速度时关闭）
    .build();
```

### 4.2 从内存缓冲区 / 输入流加载（Serverless / 内嵌资源）

适用于把 `.qzdb` 作为嵌入式资源、或运行时从对象存储 / 网络下载到内存后直接解析，避免落盘：

```java
// 字节数组（内部拷贝传入数组，调用方可自由修改 / 释放原数组）
byte[] bytes = Files.readAllBytes(Paths.get("ip_china.qzdb"));
try (QzdbReader reader = new QzdbReader.Builder(bytes).build()) {
    // ...
}

// 输入流（内部读取全部字节）
try (InputStream in = getClass().getResourceAsStream("/ip_china.qzdb")) {
    QzdbReader reader = new QzdbReader.Builder(in).build();
    // ...
}
```

### 4.3 关于分组（GroupIndex）

一个 `.qzdb` 文件可内嵌多个数据分组（如 `std` / `asn` / `max` 等不同维度）。`groupIndex` 指定加载哪一个；不传时默认 `0`。加载后可通过 `reader.getEdition()` / `reader.getFieldNames()` 确认实际加载的维度与字段。

### 4.4 加载异常处理

`build()` 可能在以下情况抛出 `QzdbException`（务必在启动期 `try/catch`，避免程序崩溃）：

| ErrorCode | 触发场景 |
|-----------|---------|
| `FILE_NOT_FOUND` | 文件不存在或无读取权限 |
| `BAD_MAGIC` | 文件头不是 `QZDB` 魔数 |
| `BAD_HEADER` / `UNSUPPORTED` | 文件头尺寸异常或格式版本不受支持 |
| `CORRUPTED` | 分区越界、分组数为 0，或 **CRC32 校验不匹配**（数据损坏 / 被截断） |
| `INVALID_PARAM` | `groupIndex` 超出范围、字段宽度非法等 |

---

## 5. 查询 API

`QzdbReader` 提供多种输入形态的查询。下表列出**全部公开方法**：

| 方法 | 签名 | 返回 | 说明 |
|------|------|------|------|
| 字符串查询 | `Optional<GeoInfo> find(String ipStr)` | `Optional<GeoInfo>` | 按字符串查（IPv4 / IPv6 / IPv4 映射地址均可）；IP 非法抛 `QzdbException` |
| InetAddress 查询 | `Optional<GeoInfo> find(InetAddress addr)` | `Optional<GeoInfo>` | 按 `InetAddress` 查；`addr` 为 null 抛异常 |
| 字节查询 | `Optional<GeoInfo> findBytes(byte[] ip16)` | `Optional<GeoInfo>` | 按 4 字节（IPv4）或 16 字节（IPv6）原始字节查 |
| 整数查询 | `Optional<GeoInfo> findUint(int ipInt)` | `Optional<GeoInfo>` | 按 IPv4 的 `int` 整型查（见下方说明） |
| 字段子集 | `Optional<GeoInfo> findFields(String ipStr, String[] fields)` | `Optional<GeoInfo>` | 只解析指定字段，减少不必要的字符串分配 |
| 管道字符串 | `String findStr(String ipStr)` | `String` | 直接返回 `toPipeString()` 结果；未命中返回 `""`；IP 非法抛异常 |
| 行号查询 | `int lookupRowId(String ipStr)` | `int` | 仅返回内部行号（不含字段，最轻）；非法 / 未命中返回 `0` |
| 行号（整数） | `int lookupRowIdUint(int ipInt)` | `int` | `findUint` 的轻量版，只返回行号 |
| 行号（字节） | `int lookupRowIdBytes(byte[] ipBytes)` | `int` | `findBytes` 的轻量版，只返回行号；非 4/16 字节返回 `0` |
| 反查 ID | `RowIds lookupIds(int rowId)` | `RowIds` | 由行号反查 Geo / ASN / Usage 三类索引 ID；越界返回 `null` |
| 批量查询 | `List<BatchResult> findBatch(List<String> ips)` | `List<BatchResult>` | 批量字符串查询，逐条容错 |
| 批量字段 | `List<BatchResult> findBatchFields(List<String> ips, String[] fields)` | `List<BatchResult>` | 批量 + 字段子集 |
| 流式查询 | `Stream<BatchResult> findStream(Stream<String> ips)` | `Stream<BatchResult>` | 惰性 `Stream` 流式逐条产出，内存恒定 |

### 5.1 IP 输入约定

- **`find(String)`**：接受点分十进制（`1.2.3.4`）与完整 / 压缩 IPv6（`2001:db8::1`）、IPv4 映射地址（`::ffff:1.2.3.4`、`::ffff:102:304`）；非法格式**抛 `QzdbException`**。
- **`findUint(int ipInt)`**：`ipInt` 应为 IPv4 地址的 **主机序 32 位无符号值按位存进 `int`**（即 `（a<<24）|（b<<16）|（c<<8）| d`，注意 Java `int` 有符号，超过 `0x7FFFFFFF` 会显示为负数，属正常）。若你手上是网络序字节，请先转换或改用 `findBytes`。
- **`findBytes(byte[])`**：4 字节按网络序（高位在前）解析为 IPv4；16 字节解析为 IPv6。

### 5.2 行号与 `RowIds`

`lookupIds(int rowId)` 返回 `RowIds` 只读记录，包含：

```java
public record RowIds(int geoId, int asnId, int usageId) {}
```

常用于：你想自己持有"行号"做缓存 / 批处理，再按需用 `lookupIds` 反查各维度 ID，或构造跨维度关联。

---

## 6. 结果对象 `GeoInfo`

`find*` 系列返回 `Optional<GeoInfo>`。它是**不可变**的结果对象，提供三种读取形态：

### 6.1 通用取字段 `get(name)`

```java
String country = info.get("country");        // 大小写、下划线不敏感：country / Country / country_code 等价
String isp     = info.get("isp");
```

字段名匹配会**忽略大小写和下划线 / 连字符**（例如 `country_code`、`CountryCode`、`country-code`、`COUNTRY_CODE` 等同）。未命中返回 `""`，**不会抛异常**。

### 6.2 强类型便捷方法

| 方法 | 返回类型 | 含义 |
|------|---------|------|
| `getCountry()` / `getCountryEn()` / `getCountryAlpha2()` / `getCountryAlpha3()` | `String` | 国家 |
| `getProvince()` / `getProvinceEn()` | `String` | 省份 |
| `getCity()` / `getCityEn()` / `getDistrict()` | `String` | 城市 / 区县 |
| `getIsp()` / `getIspEn()` | `String` | 运营商 |
| `getAsn()` | `Long` | ASN（自治域号），缺省返回 `null` |
| `getAsName()` / `getAsDomain()` | `String` | ASN 名称 / 域名 |
| `getGeoId()` | `Long` | 地理 ID |
| `getLongitude()` / `getLatitude()` | `Double` | 经纬度，缺省返回 `null` |
| `getTimezone()` | `String` | 时区 |
| `getUsageType()` | `UsageType` | 用途分类（见下） |
| `getCurrencyCode()` / `getCurrencyName()` / `getPhonePrefix()` / `getEmojiFlag()` / `getLanguages()` | `String` | 货币 / 名称 / 电话区号 / 国旗 / 语言 |

> **注意 `getCidr()`**：`GeoInfo` 的字段来自数据库，**不含 `cidr` 字段**，因此 `getCidr()` 恒返回空字符串 `""`。如需网段信息，请使用 `reader.lookupCidr(ip)`（见[第 7 节](#7-cidr-网段反查java-特有)）。

### 6.3 序列化输出

| 方法 | 说明 |
|------|------|
| `toPipeString()` | 用 `|` 连接所有字段 |
| `toJson()` | 紧凑 JSON；数值字段（asn / 经纬度 / geo_id 等）自动输出为数字或 `null`，键名保持原始 `snake_case` |
| `toMap()` | `Map<String,String>`，便于泛型消费 |
| `fieldNames()` / `values()` | 字段名数组 / 值数组（均做了克隆，安全外传） |

### 6.4 用途分类 `UsageType`

`getUsageType()` 返回 `UsageType`（一个 `sealed interface`）：

```java
UsageType u = info.getUsageType();
boolean known = u.isKnown();            // 是否为预定义分类
String zh = u.getDisplayZh();           // 中文名：如 "云服务"
String en = u.getDisplayEn();           // 英文名：如 "Cloud"
String desc = u.getDescription();       // 详细描述
String raw = u.rawValue();              // 原始编码字符串（未知场景时保留原始值）
```

预定义分类（共 21 个）：`AICrawler`(AI 爬虫)、`Backbone`(骨干网)、`Broadband`(宽带)、`Business`(企业)、`CDN`、`Cloud`(云服务)、`DNS`、`DataCenter`(数据中心)、`Education`(教育网)、`Finance`(金融)、`Government`(政府)、`ISP`、`IXP`(交换中心)、`IoT`(物联网)、`Mobile`(移动网络)、`Reserved`(保留地址)、`Satellite`(卫星互联网)、`Spider`(爬虫)、`Streaming`(流媒体)、`Unknown`(未知)、`VPN`(VPN/代理)。

未命中预定义值时 `isKnown() == false`，返回一个 `UnknownUsageType` 安全兜底（**不崩溃**），`rawValue()` 保留原始字符串。

---

## 7. CIDR 网段反查（Java 特有）

QZDB 数据库本身**不存储 CIDR**，但 Trie 每个叶子对应构建时的一条 CIDR 记录，叶子深度 = 前缀长度 N。本 SDK 提供从 IP **反查其所属最具体网段**的能力（由 Trie 匹配深度重建，Jump Table 命中叶子时内部自动从根补走），IP 未覆盖时返回 `null`：

| 方法 | 签名 | 返回 | 说明 |
|------|------|------|------|
| 字符串反查 | `String lookupCidr(String ipStr)` | `String` | 如 `"1.0.1.0/24"`、`"2001:218::/32"`；未覆盖返回 `null`；IP 非法抛 `INVALID_IP` |
| 整数反查 | `String lookupCidrUint(int ipInt)` | `String` | IPv4 `int` 入口；未覆盖返回 `null` |
| 字节反查 | `String lookupCidrBytes(byte[] ipBytes)` | `String` | 4 字节（V4）/ 16 字节（V6、含 mapped）；长度非 4/16 抛 `INVALID_IP` |

```java
try (QzdbReader reader = new QzdbReader.Builder(new File("ip_china.qzdb")).build()) {
    String cidr4 = reader.lookupCidr("223.5.5.5");     // 例如 "223.5.5.0/24"
    String cidr6 = reader.lookupCidr("2001:218::1");   // 例如 "2001:218::/32"，无 V6 数据时可能为 null
    if (cidr4 != null) System.out.println(cidr4);
}
```

> IPv4-mapped IPv6（如 `::ffff:223.5.5.5`）按规范剥离后走 V4 Trie，返回 V4 CIDR。

---

## 8. 批量与流式查询

```java
// 批量：一次性返回列表，单条异常不影响其它条目
List<BatchResult> results = reader.findBatch(List.of("1.1.1.1", "8.8.8.8", "bad-ip"));
for (BatchResult r : results) {
    if (r.isSuccess()) System.out.println(r.result().get().toPipeString());
    else if (r.isNotFound()) System.out.println("未命中");
    else System.out.println("错误: " + r.error().getMessage());
}

// 流式：惰性产出，适合超大数据集 / 管道消费（内存恒定）
reader.findStream(java.util.stream.Stream.of("1.1.1.1", "8.8.8.8"))
      .filter(BatchResult::isSuccess)
      .forEach(r -> process(r.result().get()));
```

`BatchResult` 是只读 `record`：

```java
public record BatchResult(String input, Optional<GeoInfo> result, QzdbException error) {
    public boolean isSuccess();   // error == null 且 命中
    public boolean isNotFound();  // error == null 且 未命中
    public boolean hasError();    // 输入格式错误或底层故障
}
```

> **性能提示**：`findFields(ip, fields)` 与 `findBatch` 配合，可只对需要的字段做解析，减少大批量查询下的字符串分配压力。

---

## 9. 链式多库查询 `ChainedReader`

当你有多个 `.qzdb`（例如"国内库 + 全球库"、"基础库 + 精细库"），可用 `ChainedReader` 把多个 `QzdbReader` 组合成一个逻辑查询器，支持三种合并模式：

| 工厂方法 | 模式 | 行为 |
|----------|------|------|
| `ChainedReader.chain(...)` | `FALLBACK` | 依次查询，返回**第一个命中**的结果 |
| `ChainedReader.chainMerge(...)` | `MERGE` | 合并所有命中；字段空缺才由后库补充（先库优先） |
| `ChainedReader.chainMergeOverride(...)` | `MERGE_OVERRIDE` | 合并所有命中；后库的非空值**覆盖**先库 |

```java
try (QzdbReader china = new QzdbReader.Builder(new File("ip_china.qzdb")).build();
     QzdbReader global = new QzdbReader.Builder(new File("ip_global.qzdb")).build()) {

    // 国内优先，未命中回退全球
    ChainedReader chained = ChainedReader.chain(china, global);
    Optional<GeoInfo> info = chained.find("8.8.8.8");

    // 字段级合并（精细库补全基础库缺省字段）
    ChainedReader merged = ChainedReader.chainMerge(china, global);
}
```

支持的方法：`find` / `findUint` / `findBytes` / `findFields` / `findBatch` / `findBatchFields` / `findStream`；以及聚合元信息 `editions()` / `scopes()` / `dataMonths()` / `readers()`。

> **资源说明**：`ChainedReader` **不会关闭**其下的底层 `QzdbReader`，下层各 reader 需自行 `close()`（建议各自用 try-with-resources）。

---

## 10. 命名注册表 `QzdbRegistry`

用于按名字管理多个 reader（例如在不同模块间共享同一实例）。提供**实例级**与**进程全局级**两套 API：

```java
// 实例级
QzdbRegistry reg = new QzdbRegistry();
reg.register("china", "ip_china.qzdb");                 // 按路径注册（自动 build）
reg.registerBuffer("embedded", bytes);                  // 按内存字节注册
QzdbReader r = reg.get("china");
reg.unregister("china");                                // 取消并 close 旧实例
reg.clear();                                            // 关闭并清空全部

// 进程全局（静态快捷方式）
QzdbRegistry.registerGlobal("global", "ip_global.qzdb");
QzdbReader g = QzdbRegistry.getGlobal("global");
QzdbRegistry.unregisterGlobal("global");
```

`register` 会自动 `close()` 旧实例（同名覆盖时），避免句柄泄漏。

---

## 11. 热更新与生命周期

### 11.1 原子热更新（无需重启进程）

数据库文件更新后，只需调用 `reload` / `reloadBuffer`，**旧数据在整个加载过程中继续提供服务**；只有新快照完整构建成功后才原子切换：

```java
// 重新从文件加载（CRC 始终强制校验）
reader.reload("ip_china_new.qzdb");

// 从内存缓冲加载（内部拷贝传入数组）
reader.reloadBuffer(newBytes);
```

> 注意：`reload` / `reloadBuffer` **始终强制 CRC 校验**（与构造时 `verifyCrc` 选项无关），确保热更新不会加载损坏数据。若新文件损坏，旧快照继续服务，方法会抛出 `QzdbException`。

### 11.2 释放与并发安全

- `QzdbReader` 实现 `AutoCloseable`：用 **try-with-resources** 或显式 `close()` 释放（mmap / 文件句柄 / 内存引用）。
- **`close()` 幂等**：可重复调用，多次 `close()` 安全。
- **并发安全**：多个线程可同时调用任意查询方法，互不阻塞（无锁读取快照）。已 `close()` 的 reader 再查询会抛出 `IllegalStateException`。

---

## 12. 错误处理

所有加载 / 解析期错误以 **`QzdbException`（非受检异常，`RuntimeException` 子类）** 抛出，携带 `ErrorCode` 枚举：

```java
try {
    QzdbReader reader = new QzdbReader.Builder(new File("ip_china.qzdb")).build();
} catch (QzdbException ex) {
    System.out.println("加载失败 [" + ex.getErrorCode() + "]: " + ex.getMessage());
}
```

`ErrorCode` 取值：`FILE_NOT_FOUND`、`BAD_MAGIC`、`BAD_HEADER`、`UNSUPPORTED`、`CORRUPTED`、`INVALID_PARAM`、`NOT_FOUND`、`INVALID_IP`。

> **查询期**：普通"未命中"通过返回 `Optional.empty()` 表达；"IP 格式非法"通过抛出 `QzdbException(INVALID_IP)` 表达（与加载期异常共用同一类型，用 `getErrorCode()` 区分）。批量 / 流式接口会把单条错误封装进 `BatchResult.error()`，不中断整体。

---

## 13. 性能说明

本 SDK 在查询热路径上采用与 .NET 及其他语言实现同架构的热路径优化：

- **无锁快照架构**：查询只读原子引用指向的不可变快照，多线程零竞争；`reload` 用原子引用替换，旧快照在 GC 回收前继续服务。
- **mmap 内存映射**：文件通过 `FileChannel.map(READ_ONLY)` 直接映射，避免整文件堆内拷贝；内存模式则用 `ByteBuffer.wrap` + 拷贝语义。
- **零分配 trie 遍历**：基于 `ByteBuffer` 绝对定位读取（无 `position` 副作用、线程安全），单次查询不分配托管对象。
- **不可变快照缓存**：快照不可变 → 同一 `entryId` 永远解析出同一 `GeoInfo`。对热点 IP（同段 / 邻近客户端、批量扫段）直接命中，减少重复字段解析分配。
- **零拷贝 IP 解析**：IPv4 整数解析、IPv6 严格解析（拒绝前导 0 / zone id / 双 `::`），无 DNS、无外部依赖。

### 参考性能（同架构理论量级，实际随 JVM / CPU / 数据规模 / 查询分布而变）

| 场景 | 量级 | 说明 |
|------|------|------|
| 单线程 IPv4 / IPv6 查询 | 数百万 QPS | 50 万随机散布 IP（缓存最不利情形），与 .NET 实现同量级（实测约 7.8M IPv4 / 8.5M IPv6 QPS） |
| 多线程并发 | 安全无锁 | 16 线程并发查询 / 热重载零异常、无竞争退化 |
| 热点 IP 重复查询 | 吞吐进一步放大、分配趋零 | 同 IP 重复查询，GC 压力显著下降 |

> 上述数字用于说明量级，非 SLA；以你自身环境的基准测试为准。

---

## 14. 维护与升级

### 14.1 更新数据（最频繁的操作）

不需要重新编译或重启进程：

1. 从官方渠道获取新的 `.qzdb` 文件（注意 `getDataMonth()` / `getBuildTime()` 是否更新）。
2. 调用 `reader.reload(newPath)` 或 `reader.reloadBuffer(newBytes)` 原子热更新。
3. 用 `reader.getDataMonth()` / `reader.getBuildTime()` / `reader.getVersion()` 确认已加载的数据版本。

### 14.2 升级 SDK

```bash
mvn dependency:resolve   # 重新拉取最新版（修改 pom.xml 中的 <version>）
```

版本遵循 **SemVer**：

| 变更类型 | 版本位 | 影响 |
|----------|--------|------|
| 破坏性 API 变更 | 主版本 `x` | 需改调用代码 |
| 向后兼容的功能新增 | 次版本 `y` | 直接升级 |
| Bug 修复 / 性能优化 | 补丁 `z` | 直接升级（建议始终跟进） |

### 14.3 校验数据完整性

```java
// 重新计算全文件 CRC32 并与 Header 存储值比对（只读操作）
boolean ok = reader.verifyCrc();
// 或查看文件指纹（8 位小写十六进制）
String hash = reader.getFileHash();
```

### 14.4 兼容性注意

- Java 包名与 Maven `artifactId` 一致（`com.qqzeng.qzdb` / `qzdb`），升级不会造成包名漂移。
- 编译目标 JDK 21：引用方 JDK 至少需 21。

---

## 15. 项目结构

`java/` 目录（本库源码）：

| 文件 | 职责 |
|------|------|
| `src/main/java/com/qqzeng/qzdb/QzdbReader.java` | 核心读取器：加载、trie 遍历、查询、热更新、CRC、生命周期 |
| `src/main/java/com/qqzeng/qzdb/GeoInfo.java` | 查询结果对象：字段解析、序列化（`toPipeString`/`toJson`/`toMap`）、强类型取值 |
| `src/main/java/com/qqzeng/qzdb/QzdbRegistry.java` | 命名 reader 注册表（实例级 + 全局级） |
| `src/main/java/com/qqzeng/qzdb/ChainedReader.java` | 多库链式组合（Fallback / Merge / MergeOverride） |
| `src/main/java/com/qqzeng/qzdb/BatchResult.java` | 批量查询的三态结果 `record`（`input` / `result` / `error`） |
| `src/main/java/com/qqzeng/qzdb/RowIds.java` | 行号反查 `record`（`geoId` / `asnId` / `usageId`） |
| `src/main/java/com/qqzeng/qzdb/UsageType.java` + `KnownUsageType.java` + `UnknownUsageType.java` | 用途分类密封接口与中英映射、未知兜底 |
| `src/main/java/com/qqzeng/qzdb/QzdbException.java` | 异常类型与 `ErrorCode` 枚举 |
| `pom.xml` | Maven 项目文件（JDK 21 编译目标 + 元数据） |

测试 / 基准（同 `src/test/java`，依赖外部 `test_data_202608/` 数据，已跳过 surefire 自动执行）：

- `QzdbReaderTest.java` —— 全功能单元测试 + 2026-08 修复回归套件（运行：`java -cp target/classes com.qqzeng.qzdb.QzdbReaderTest`）
- `FullAccuracyAndPerfTester.java` —— 全量精度与性能基准
- `DualStackBenchmark.java` —— 双栈吞吐基准

跨语言完整 API 规范见仓库根：`docs/QZDB_SDK_API.md`。

---

## License

[MIT](https://opensource.org/licenses/MIT)

<!-- commit: java: Java SDK（堆外内存与 Builder API） sync=1787727713 -->
