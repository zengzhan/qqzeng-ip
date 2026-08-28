# qzdb

> 纯离线、零外部依赖、高性能的 **QZDB IP 地理定位数据库**官方 Rust SDK（支持 IPv4 / IPv6 双栈）。

- **官方坐标**：crates.io 的 `qzdb`（`cargo add qzdb`，crate 名与库根模块名一致）
- **定位**：离线解析 `.qzdb` 二进制数据库文件，不依赖任何外部网络请求
- **架构**：无锁快照（lock-free snapshot）——并发查询互不阻塞，`reload` 原子切换（`ArcSwap`）
- **运行要求**：Rust 1.74+（运行时依赖仅 `arc-swap` 与 `memmap2`）
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
| Rust 工具链 | `cargo` / `rustc` 1.74 或更高 |
| 操作系统 | Windows / Linux / macOS 均可 |
| 数据库文件 | `.qzdb` 格式（由官方数据构建工具生成，含所需分组的二进制数据） |
| 运行时依赖 | 仅 `arc-swap` 与 `memmap2`（无其它第三方依赖） |

---

## 2. 安装

在 `Cargo.toml` 中添加：

```toml
[dependencies]
qzdb = "1.0.5"
```

或执行：

```bash
cargo add qzdb
```

Rust 代码统一从 crate 根引入：

```rust
use qzdb::QzdbReader;
```

---

## 3. 快速开始

```rust
use qzdb::QzdbReader;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    // 通过文件路径加载（默认：校验 CRC、加载第 0 个分组）
    let reader = QzdbReader::from_file("ip_china.qzdb")?;

    // 单次查询：未命中或 IP 非法时返回 None（不会抛异常）
    if let Some(info) = reader.find("114.114.114.114") {
        println!("{}", info.to_pipe());                 // 管道符分隔：国家|省份|城市|ISP|...
        println!("{}", info.to_json());                 // 紧凑 JSON 字符串
        println!("{} / {} / {}", info.country(), info.province(), info.city());
        println!("ISP={}, ASN={:?}", info.isp(), info.asn());
    }

    // 管道符格式（未命中返回空字符串，适合直接落库 / 日志）
    let pipe: String = reader.find_str("240e:390:1:1::1");
    println!("{}", pipe);

    // 仅取内部行号（最轻量，不涉及字段解析）
    let row_id: u32 = reader.lookup_row_id("8.8.8.8");
    println!("row_id={}", row_id);

    Ok(())
}
```

> **查询语义约定**：`find` / `find_uint` / `find_v6` / `find_bytes` 等**在 IP 未命中或格式非法时返回 `None`，不抛异常**。只有数据库文件损坏、格式不支持、CRC 校验失败等**加载期错误**才会以 `Err(QzdbError)` 返回（见[第 12 节](#12-错误处理)）。

---

## 4. 加载数据库

所有加载入口都通过 **`QzdbReader::builder`** 或 `Builder` 直接构造。

### 4.1 从文件路径加载

```rust
use qzdb::{QzdbReader, Builder};

// 最简：路径 + 默认第 0 分组 + 校验 CRC
let reader = Builder::new("ip_china.qzdb").build()?;

// 等价简写
let reader = QzdbReader::from_file("ip_china.qzdb")?;

// 完整选项
let reader = Builder::new("ip_all.qzdb")
    .group_index(1)      // 选择数据库中的第 N 个分组（多分组文件时用于选不同数据维度）
    .verify_crc(false)   // 关闭 CRC32 校验（仅在你已离线校验过、追求极限加载速度时关闭）
    .build()?;
```

### 4.2 从内存缓冲区加载（Serverless / 内嵌资源）

适用于把 `.qzdb` 作为嵌入式资源、或运行时从对象存储 / 网络下载到内存后直接解析，避免落盘：

```rust
let bytes: Vec<u8> = std::fs::read("ip_china.qzdb")?;
let reader = Builder::from_bytes(&bytes).build()?;
// 同样支持 .group_index(...) / .verify_crc(...)
```

### 4.3 关于分组（GroupIndex）

一个 `.qzdb` 文件可内嵌多个数据分组（如 `std` / `ult` 等不同维度）。`group_index` 指定加载哪一个；不传时默认 `0`。加载后可通过 `reader.get_edition()` / `reader.get_field_names()` 确认实际加载的维度与字段。

### 4.4 加载异常处理

文件路径加载（`build()` / `from_file` / `reload`）使用只读 mmap；内存字节入口（`from_bytes` / `reload_bytes`）保持拷贝语义。上述 API 可能在以下情况返回 `Err(QzdbError)`（务必在启动期 `?` / `match`，避免程序崩溃）：

| ErrorCode | 触发场景 |
|-----------|---------|
| `BadMagic` | 文件头不是 `QZDB` 魔数 |
| `BadHeader` / `Unsupported` | 文件头尺寸异常或格式版本不受支持 |
| `Corrupted` | 分区越界、分组数为 0，或 **CRC32 校验不匹配**（数据损坏 / 被截断） |
| `InvalidParam` | `group_index` 超出范围、字段宽度非法等 |
| `OutOfBounds` | 数据区被截断、偏移越界 |

---

## 5. 查询 API

`QzdbReader` 提供多种输入形态的查询。下表列出**全部公开方法**：

| 方法 | 签名 | 返回 | 说明 |
|------|------|------|------|
| 字符串查询 | `find(&str) -> Option<GeoInfo>` | `Option<GeoInfo>` | 按字符串查（IPv4 / IPv6 / IPv4 映射地址均可） |
| 字节查询 | `find_bytes(&[u8]) -> Option<GeoInfo>` | `Option<GeoInfo>` | 按 4 字节（IPv4）或 16 字节（IPv6）原始字节查 |
| 整数查询 | `find_uint(u32) -> Option<GeoInfo>` | `Option<GeoInfo>` | 按 IPv4 的 `u32` 整型查（主机序） |
| IPv6 整数 | `find_v6(u128) -> Option<GeoInfo>` | `Option<GeoInfo>` | 按 IPv6 的 `u128` 整型查（主机序） |
| 字段子集 | `find_fields(&str, &[&str]) -> Option<GeoInfo>` | `Option<GeoInfo>` | 只解析指定字段，减少不必要的字符串分配 |
| 管道字符串 | `find_str(&str) -> String` | `String` | 直接返回 `to_pipe()` 结果；未命中返回 `""` |
| 行号查询 | `lookup_row_id(&str) -> u32` | `u32` | 仅返回内部行号（不含字段，最轻；未命中返回 `0`） |
| 行号（整数） | `lookup_row_id_uint(u32) -> u32` | `u32` | `find_uint` 的轻量版，只返回行号 |
| 行号（IPv6） | `lookup_row_id_v6(u128) -> u32` | `u32` | `find_v6` 的轻量版，只返回行号 |
| 行号（字节） | `lookup_row_id_bytes(&[u8]) -> u32` | `u32` | `find_bytes` 的轻量版，只返回行号 |
| 反查 ID | `lookup_ids(u32) -> Option<RowIds>` | `Option<RowIds>` | 由行号反查 Geo / ASN / Usage 三类索引 ID |
| CIDR 反查 | `lookup_cidr(&str) -> Option<String>` | `Option<String>` | 反查 IP 所属 CIDR 网段（如 `1.2.3.0/24`） |
| 批量查询 | `find_batch(&[&str]) -> Vec<BatchResult>` | `Vec<BatchResult>` | 批量字符串查询，逐条容错 |
| 批量字段 | `find_batch_fields(&[&str], &[&str]) -> Vec<BatchResult>` | `Vec<BatchResult>` | 批量 + 字段子集 |
| 流式查询 | `find_stream(&[&str]) -> impl Iterator<Item = BatchResult>` | 迭代器 | 惰性流式逐条产出 |

### 5.1 IP 输入约定

- **`find(&str)`**：接受点分十进制（`1.2.3.4`）与完整 / 压缩 IPv6（`2001:db8::1`）、IPv4 映射地址（`::ffff:1.2.3.4`）；非法格式直接返回 `None`。解析严格拒绝前导零、段数错误、CIDR 形式、zone-id、空白等。
- **`find_uint(u32)`**：`ip` 应为 IPv4 地址的 **主机序 `u32`**（即 `(a<<24)|(b<<16)|(c<<8)|d`）。若你手上是网络序字节，请先转换或改用 `find_bytes`。
- **`find_v6(u128)`**：`ip` 为 IPv6 地址的**主机序 `u128`**；纯 V6 Trie 遍历，**不会**自动降级到 V4 Trie。若你持有 IPv4-mapped 地址的 `u128`，请改用 `find_bytes` 或 `find(&str)`，它们会在解析阶段自动降级。
- **`find_bytes(&[u8])`**：4 字节按网络序（高位在前）解析为 IPv4；16 字节解析为 IPv6。

### 5.2 行号与 `RowIds`

`lookup_ids(u32)` 返回 `RowIds` 结构，包含：

```rust
pub struct RowIds {
    pub geo_id: u32,
    pub asn_id: u32,
    pub usage_id: u32,
}
```

常用于：你想自己持有"行号"做缓存 / 批处理，再按需用 `find*` 取完整字段，或构造跨维度关联。

---

## 6. 结果对象 `GeoInfo`

`find*` 系列返回 `Option<GeoInfo>`。它是**不可变**的结果对象，提供三种读取形态：

### 6.1 通用取字段 `get(name)`

```rust
let country = info.get("country");   // 大小写、下划线不敏感：country / Country / country_code 等价
let isp     = info.get("isp");
```

字段名匹配会**忽略大小写和下划线**（例如 `country_code`、`CountryCode`、`countrycode` 等同）。未命中返回 `""`（永不返回 `None`，不会 panic）。

### 6.2 强类型便捷方法

| 方法 | 返回类型 | 含义 |
|------|---------|------|
| `country()` / `country_en()` / `country_alpha2()` / `country_alpha3()` | `&str` | 国家 |
| `province()` / `province_en()` | `&str` | 省份 |
| `city()` / `city_en()` | `&str` | 城市 / 区县 |
| `isp()` / `isp_en()` | `&str` | 运营商 |
| `asn() -> Option<u64>` | `Option<u64>` | ASN（自治域号） |
| `geo_id() -> Option<u64>` | `Option<u64>` | 地理 ID |
| `longitude() -> Option<f64>` | `Option<f64>` | 经度 |
| `latitude() -> Option<f64>` | `Option<f64>` | 纬度 |
| `usage_type() -> UsageType` | `UsageType` | 用途分类（见下） |
| `get_cidr() -> &str` | `&str` | 始终返回 `""`（CIDR 请使用 `lookup_cidr`） |

### 6.3 序列化输出

| 方法 | 说明 |
|------|------|
| `to_pipe() -> String` | 用 `|` 连接所有字段（直接拼接，不二次解析；数值字段为 6 位小数定点格式，如 `116.400000`） |
| `to_json() -> String` | 紧凑 JSON；数值字段（asn / 经纬度 / geo_id）自动输出为数字或 `null` |
| `to_map() -> HashMap<String, String>` | 字段名 → 值映射 |
| `to_string_pipe() -> String` | 与 `to_pipe` 等价的管道字符串 |

> **数值格式约定**：经纬度 / 数值字段在解码阶段即格式化为 **6 位定点小数**（`{:.6}`），`to_pipe` 直接拼接该字符串；`NaN` / `Inf` 输出为 `""`。这与契约 `§8` 及黄金用例一致。

### 6.4 用途分类 `UsageType`

`usage_type()` 返回 `UsageType`：

```rust
let u = info.usage_type();
let known = u.is_known();        // 是否为预定义分类
let raw = u.raw_value();         // 原始字符串（未知时保留原值，已知时为规范名如 "Cloud"）
```

预定义分类（21 种）：`AICrawler`、`Backbone`、`Broadband`、`Business`、`CDN`、`Cloud`、`DNS`、`DataCenter`、`Education`、`Finance`、`Government`、`Isp`、`Iot`、`Mobile`、`Reserved`、`Satellite`、`Spider`、`Streaming`、`Unknown`、`Vpn` 等。未命中预定义值时 `is_known() == false`，`raw_value()` 保留原始字符串。

---

## 7. CIDR 网段反查

由 IP 反查其所属 CIDR 网段（网络地址 / 前缀长度）。通过 Trie 叶子深度还原前缀长度，网络地址由 IP 高位 N bit 清零得到；IPv6 结果采用 RFC 5952 压缩格式。

```rust
// IPv4
if let Some(cidr) = reader.lookup_cidr("1.2.3.4") {
    println!("{}", cidr);   // 例如 "1.2.3.0/24"
}
// IPv6
if let Some(cidr) = reader.lookup_cidr("2001:db8::1") {
    println!("{}", cidr);   // 例如 "2001:db8::/32"
}
// 也支持整数 / 字节输入
let c1 = reader.lookup_cidr_uint(0x01020304);     // IPv4 u32
let c2 = reader.lookup_cidr_bytes(&[1,2,3,4]);    // IPv4 4 字节
```

> IPv4 映射地址（如 `::ffff:1.2.3.4`）会自动降级到 V4 Trie 进行反查。

---

## 8. 批量与流式查询

```rust
// 批量：一次性返回数组，单条异常不影响其它条目
let results = reader.find_batch(&["1.1.1.1", "8.8.8.8", "bad-ip"]);
for r in &results {
    if let Some(info) = &r.geo_info {
        println!("{} -> {}", r.ip, info.to_pipe());
    } else if r.error.is_some() {
        println!("{} -> 错误: {}", r.ip, r.error.as_ref().unwrap());
    } else {
        println!("{} -> 未命中", r.ip);
    }
}

// 流式：惰性产出，适合超大数据集 / 管道消费
for r in reader.find_stream(&huge_ip_list) {
    if let Some(info) = &r.geo_info {
        process(info);
    }
}
```

`BatchResult` 结构：

```rust
pub struct BatchResult {
    pub ip: String,
    pub geo_info: Option<GeoInfo>,
    pub error: Option<String>,
}
```

> **性能提示**：`find_fields(ip, fields)` 与 `find_batch_fields` 配合，可只对需要的字段做解析，减少大批量查询下的字符串分配压力。

---

## 9. 链式多库查询 `ChainedReader`

当你有多个 `.qzdb`（例如"国内库 + 全球库"、"基础库 + 精细库"），可用 `ChainedReader` 把多个 `QzdbReader` 组合成一个逻辑查询器，按优先级串联、返回首个命中：

```rust
use qzdb::{QzdbReader, ChainedReader};

let china = QzdbReader::from_file("ip_china.qzdb")?;
let global = QzdbReader::from_file("ip_global.qzdb")?;

// 国内优先，未命中回退全球
let mut chained = ChainedReader::new();
chained.push(china);
chained.push(global);

if let Some(info) = chained.find("8.8.8.8") {
    println!("{}", info.to_pipe());
}
```

支持的方法：`find` / `find_str`（返回首个命中；全未命中返回 `None` / `""`）。

> **资源说明**：`ChainedReader` 持有底层 `QzdbReader` 的所有权；`ChainedReader` 被丢弃时会一并释放这些 reader。

---

## 10. 命名注册表 `QzdbRegistry`

用于按名字管理多个 reader（例如在不同模块间共享同一实例）：

```rust
use qzdb::{QzdbReader, QzdbRegistry};

let mut reg = QzdbRegistry::new();
reg.register("china", QzdbReader::from_file("ip_china.qzdb")?);
reg.register("global", QzdbReader::from_file("ip_global.qzdb")?);

if let Some(r) = reg.get("china") {
    println!("{}", r.find_str("114.114.114.114"));
}
// 按注册顺序返回首个命中
let pipe = reg.find_str("8.8.8.8");
```

> **无进程级单例**：v2.4 起 Rust crate 不再提供全局 `QzdbReader` 单例。`QzdbRegistry` 只是用户持有的 `Map<name, QzdbReader>`，可独立实例化、可多实例并存；`OnceLock` 仅用于内部 CRC 表与空快照缓存，与读取器单例无关。跨文件/跨版本请各自持有 `QzdbReader` 实例。

---

## 11. 热更新与生命周期

### 11.1 原子热更新（无需重启进程）

数据库文件更新后，只需调用 `reload` / `reload_bytes`，**旧数据在整个加载过程中继续提供服务**；只有新快照完整构建成功后才原子切换：

```rust
// 重新从文件加载（CRC 始终强制校验）
reader.reload("ip_china_new.qzdb")?;

// 从内存缓冲加载
let new_bytes: Vec<u8> = std::fs::read("ip_china_new.qzdb")?;
reader.reload_bytes(&new_bytes)?;
```

> 注意：`reload` / `reload_bytes` **始终强制 CRC 校验**（与构造时 `verify_crc` 选项无关），确保热更新不会加载损坏数据。若新文件损坏，旧快照继续服务，方法返回 `Err(QzdbError)`。

### 11.2 释放与并发安全

- `QzdbReader` 内部为 `ArcSwap` 快照，**不需要手动 `Drop`**：离开作用域即自动释放（无文件句柄需关闭，因为数据是内存中的 `Arc<Vec<u8>>`）。
- 调用 `close()` 会原子切换到一个最小占位快照（无数据），之后查询一律返回 `None` / `""`，用于显式"软卸载"而不销毁对象。
- **并发安全**：多个线程可同时调用任意查询方法，互不阻塞（无锁读取快照）；`reload` 线程安全。

```rust
let reader = QzdbReader::from_file("ip_china.qzdb")?;
// 多线程共享：用 Arc 包裹（QzdbReader 内部仅持有 ArcSwap，体积小、切换开销低）
let shared = std::sync::Arc::new(reader);
let r2 = shared.clone(); // 仅复制 Arc 指针，不复制数据
reader.close();
```

> 提示：`QzdbReader` 内部仅持有 `ArcSwap<SnapshotInner>`，体积很小。若需在多线程间共享，推荐包进 `Arc<QzdbReader>` 后 `clone()`；`reload` / 查询均线程安全。

---

## 12. 错误处理

所有加载 / 解析期错误以 `QzdbError` 返回，携带 `ErrorCode` 枚举：

```rust
use qzdb::{QzdbReader, ErrorCode};

match QzdbReader::from_file("ip_china.qzdb") {
    Ok(reader) => { /* 使用 */ }
    Err(e) => {
        println!("加载失败 [{:?}]: {}", e.code(), e);
        match e.code() {
            ErrorCode::BadMagic => { /* 魔数错误 */ }
            ErrorCode::Corrupted => { /* CRC 不匹配 / 越界 */ }
            _ => {}
        }
    }
}
```

`ErrorCode` 取值：`BadMagic`、`BadHeader`、`Unsupported`、`Corrupted`、`InvalidParam`、`OutOfBounds`、`NotFound`、`InvalidIp`。

> **查询期**：普通"未命中"和"IP 格式非法"通过返回 `None` / `""` / `0` 表达，**不抛异常**（见[第 3 节](#3-快速开始)说明）。只有底层数据异常（如分组越界）才会返回错误，批量接口会将其封装进 `BatchResult.error` 而不中断整体。

---

## 13. 性能说明

本 SDK 在查询热路径上做了深度优化：

- **无锁快照架构**：查询只读 `ArcSwap` 快照引用，多线程零竞争；`reload` 用原子替换切换。
- **只读 mmap 加载 + 懒解析**：文件路径加载（`from_file`/`reload`/`Builder::build(path)`）通过 `memmap2` 只读内存映射，可在多进程间共享物理页；unsafe 仅隔离在 `map_file()` 一处（配 SAFETY 注释说明不变式），crate 其余部分仍是安全代码。内存字节入口（`from_bytes`/`reload_bytes`）保持拷贝语义（`Vec<u8>`）。字段解析按需切片，不做整库反序列化。
- **per-snapshot 有界无锁缓存**：GeoInfo 解码缓存为固定 **16384 槽位**（`1 << 14`）、只填不淘汰的开放寻址表。快照不可变 → 同一 `entry_id` 永远解析出同一 `GeoInfo`。对热点 IP（同段 / 邻近客户端、批量扫段）直接命中缓存，**命中路径零分配**。超出容量后新查询到的条目走非缓存路径解码，不影响正确性但会降低该条目的吞吐；如数据库 distinct 地理条目数明显超过该值，命中率会相应下降。
- **SENTINEL 位即时剥离**：Trie 返回的 row_id / index 在遍历时即剥离哨兵位（`& 0x7FFFFFFF`），无需调用方处理。
- **IPv4 映射地址自动降级**：`::ffff:a.b.c.d` 在解析阶段即降级到 V4 Trie，双栈查询统一路径。

> 实际吞吐随 CPU、数据规模、查询分布而变；上述设计用于说明量级，非 SLA。

---

## 14. 维护与升级

### 14.1 更新数据（最频繁的操作）

不需要重新编译或重启进程：

1. 从官方渠道获取新的 `.qzdb` 文件（注意 `get_data_month` / `get_build_time` 是否更新）。
2. 调用 `reader.reload(new_path)` 或 `reader.reload_bytes(new_bytes)` 原子热更新。
3. 用 `reader.get_version()` / `reader.get_data_month()` / `reader.get_build_time()` 确认已加载的数据版本。

### 14.2 元信息访问

```rust
println!("version   = {}", reader.get_version());      // 如 "std" / "ult"
println!("data_month= {}", reader.get_data_month());   // 如 "2026-08"
println!("edition   = {}", reader.get_edition());      // 推断维度
println!("scope     = {}", reader.get_scope());        // 恒为 ""（保留字段）
println!("build_time= {}", reader.get_build_time());   // 如 "2026-08-02"
println!("hash      = {}", reader.get_file_hash());    // CRC32 十六进制（8 字符）
println!("fields    = {:?}", reader.get_field_names());// 实际字段名
println!("groups    = {}", reader.get_group_count());
println!("pools     = {}", reader.get_pool_count());
println!("crc_ok    = {}", reader.verify_crc());
```

### 14.3 升级 crate

```bash
cargo update -p qzdb
```

版本遵循 **SemVer**：破坏性 API 变更升主版本；向后兼容功能新增升次版本；Bug 修复 / 性能优化升补丁版本。

---

## 15. 项目结构

`rust/` 目录（本库源码）：

| 文件 | 职责 |
|------|------|
| `src/lib.rs` | 核心读取器：加载、Trie 遍历、查询、热更新、CRC、生命周期、`GeoInfo`、`Builder`、`Registry`、`ChainedReader`、`UsageType` |
| `src/main.rs` | 命令行单 IP 查询示例（`cargo run -- <db_path> <ip>`） |
| `src/bin/demo.rs` | 综合用法示例 |
| `src/bin/batch_rust.rs` | 标准输入批量查询示例 |
| `src/bin/dump_rust.rs` | 元信息 / 结构导出示例 |
| `src/bin/regress_rust.rs` | 回归测试 / 比对工具 |
| `bench_qps.rs` | QPS 基准测试 |
| `tests/golden.rs` | Tier2 黄金校验（强制 0 失败，读取 `tools/golden_vectors.json`） |
| `tests/tier1.rs` | Tier1 单元测试（覆盖契约 §10 九大类） |
| `tests/csv_oracle.rs` | **独立 CSV 地面真值校验**：以源数据 `test_data_202608/{std,ult}/china/*_range.csv` 为裁判，对 std/ult_china 各约 6000 区间内 + 5000 全局随机样本比对 `country/province/city/isp`，强制 0 失配 |
| `tests/cidr_oracle.rs` | CIDR 反查独立 Oracle（不依赖内部 Trie，交叉验证网络地址/前缀长度） |
| `tests/concurrency.rs` / `tests/edge_cases.rs` / `tests/ipv4_scan.rs` / `tests/ipv6_boundary.rs` | 并发安全 / 边界 / 扫描 测试 |
| `Cargo.toml` | 包定义与依赖（`arc-swap`, `memmap2`, `serde_json`） |

> **运行测试**：`cargo test`（覆盖 Tier1 + golden + 独立 CSV 真值 + CIDR Oracle + 并发/边界）。其中 `csv_oracle` 需仓库根 `test_data_202608/` 源 CSV 与 `../data/` 真实库，强制 0 失配。

跨语言完整 API 规范见仓库根：`multi-lang/API_CONTRACT.md`。

---

## License

[MIT](https://opensource.org/licenses/MIT)

<!-- commit: rust: Rust 极速解析引擎 (mmap + 最小 unsafe surface, 6900 万+ QPS) -->
