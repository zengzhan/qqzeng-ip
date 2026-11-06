# qqzeng-ip — 高性能 IP 地理位置与网络画像数据库引擎

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Format: QZDB v2.4](https://img.shields.io/badge/Format-.qzdb%20%7C%20.db%20%7C%20.dat-orange.svg)](docs/QZDB_FORMAT.md)
[![Dual-Stack: IPv4 & IPv6](https://img.shields.io/badge/Dual--Stack-IPv4%20%26%20IPv6-success.svg)](docs/VERSION_COMPARISON.md)
[![Maven Central](https://img.shields.io/badge/Maven%20Central-com.qqzeng%3Aqzdb%201.0.8-C71A36.svg)](https://central.sonatype.com/artifact/com.qqzeng/qzdb)
[![crates.io](https://img.shields.io/badge/crates.io-qzdb%202.0.0-DEA584.svg)](https://crates.io/crates/qzdb)
[![NuGet](https://img.shields.io/badge/NuGet-QQZeng.Qzdb%201.0.8-004880.svg)](https://www.nuget.org/packages/QQZeng.Qzdb)
[![PyPI](https://img.shields.io/badge/PyPI-qzdb%201.0.6-3775A9.svg)](https://pypi.org/project/qzdb/)
[![npm](https://img.shields.io/badge/npm-%40qqzengip%2Fqzdb%201.0.6-CB3837.svg)](https://www.npmjs.com/package/@qqzengip/qzdb)
[![Packagist](https://img.shields.io/badge/Packagist-qqzeng%2Fqzdb%201.2.0-F28D1A.svg)](https://packagist.org/packages/qqzeng/qzdb)

> 单文件整合全球 **1,250 万+ IPv4/IPv6 双栈网段** · 亚微秒级 `mmap` 零拷贝检索 · **8 大主流语言**统一 API 与四层黄金向量对账验证。
>
> 🔗 **快捷导航**：[在线查询体验 (25维画像)](https://www.qqzeng.com/ip/) · [版本选型与字段对照](docs/VERSION_COMPARISON.md) · [多格式性能评测](docs/BENCHMARK_COMPARISON.md) · [8语言性能白皮书](docs/PERFORMANCE.md) · [SDK API 规范](docs/QZDB_SDK_API.md) · [商业授权与选购](https://www.qqzeng.com/service/)

## 目录

1. [核心架构亮点](#一核心架构亮点)
2. [一分钟版本选型指南](#二一分钟版本选型指南)
3. [QZDB 版本规格、文件大小与字段区别](#三qzdb-版本规格文件大小与字段区别)
4. [性能测试与多格式横向对比](#四性能测试与多格式横向对比)
5. [30 秒快速安装与集成](#五30-秒快速安装与集成)
6. [多语言代码示例](#六多语言代码示例)
7. [仓库目录结构导航](#七仓库目录结构导航)
8. [验证体系与商业授权](#八验证体系与商业授权)

## 一、核心架构亮点

**qqzeng-ip** 提供以新一代二进制格式 **`.qzdb`** 为核心的 IP 地理位置、运营商、自治域（ASN）与网络应用场景（IDC 机房 / 家宽 / 移动基站 / 云厂商）解析引擎，同时完整维护存量系统广泛使用的经典 **6.0（`.db`）**、**2.0（`.dat`）** 与 **手机号段归属地** SDK。

| 核心特性 | 技术架构与实测指标 | 工程价值 |
| :--- | :--- | :--- |
| **双阶段极速检索** | `16-bit Jump Table` + `PATRICIA Trie` 最长前缀匹配，IPv4 平均 ≤16 次位比对 | 编译型语言单核整型随机达 **46.9M ~ 97.3M QPS**，16 核达 **7.19 亿 QPS** |
| **42:1 极限压缩** | 拓扑后序离线去重（Hash-Consing）+ 字典池化，2.94 GB 双栈 CSV 压至 **69.1 MB** | 国内双栈版仅 **2.6 ~ 6.3 MB**，随容器镜像秒级分发 |
| **零拷贝共享内存** | 原生支持操作系统 `mmap` 只读内存映射，多进程共享同一物理内存页 | 查询全程 **0 堆内存分配、无锁并发**，告别数仓/Redis 3GB~6GB 内存占用 |
| **自描述动态 Schema** | 文件头内嵌字段元数据字典，原生返回强类型 `GeoInfo` 对象与按名取值 | 彻底告别传统竖线字符串手工 `split('|')` 引起的列偏移隐患 |
| **工业级安全门禁** | 加载期执行头部、CRC32、偏移边界与 UTF-8 校验（Fail-Closed） | 截断或损坏文件安全拒绝；8 语言通过 L1~L4 黄金向量逐字节对账 |

## 二、一分钟版本选型指南

根据您的业务场景、网络协议要求与系统架构，按以下对照表直接锁定对应引擎与版本：

| 业务场景与系统形态 | 推荐引擎格式 | 推荐版本规格 | 二进制体积 (全球 / 国内) | 对应 SDK 目录 |
| :--- | :--- | :--- | :--- | :--- |
| **Web 高并发防刷 / 访问控制 / 省市分流** | 新一代 `.qzdb` (双栈) | **标准版 (`STD` · 6维)** | **39.6 MB** / **2.9 MB** | [`ip-qzdb-sdk/`](ip-qzdb-sdk) |
| **BGP 路由调优 / 跨网调度 / CDN 分配** | 新一代 `.qzdb` (双栈) | **自治域版 (`ASN` · 8维)** | **26.6 MB** / **2.6 MB** | [`ip-qzdb-sdk/`](ip-qzdb-sdk) |
| **电商本地化 / 广告精准投放 / 区县下沉** | 新一代 `.qzdb` (双栈) | **专业版 (`PRO` · 11维)** | **49.1 MB** / **5.0 MB** | [`ip-qzdb-sdk/`](ip-qzdb-sdk) |
| **业务风控 / 反欺诈 / 机房与家宽识别** | 新一代 `.qzdb` (双栈) | **旗舰版 (`MAX` · 15维)** | **62.6 MB** / **5.9 MB** | [`ip-qzdb-sdk/`](ip-qzdb-sdk) |
| **跨国出海业务 / 中英双语 / 全维画像** | 新一代 `.qzdb` (双栈) | **至尊版 (`ULT` · 25维)** | **69.1 MB** / **6.3 MB** | [`ip-qzdb-sdk/`](ip-qzdb-sdk) |
| **存量老系统平滑升级（全球纯 IPv4）** | 经典 6.0 `.db` (纯v4) | **全球旗舰版 (11维)** | **33.2 MB** *(解除16MB限制)* | [`ip-classic-sdk/v6.0-db/`](ip-classic-sdk/v6.0-db) |
| **存量老系统长期维护（国内纯 IPv4）** | 经典 2.0 `.dat` (纯v4) | **国内精华版 (11维)** | **6.4 MB** *(字符池1.6MB)* | [`ip-classic-sdk/v2.0-dat/`](ip-classic-sdk/v2.0-dat) |
| **自建关系型数仓 / SQL 离线关联分析** | 明文 `.csv` (CIDR/Range) | **全系列版本可选** | 29.8 MB ~ 2.94 GB | [`database-sql/`](database-sql) |

## 三、QZDB 版本规格、文件大小与字段区别

`.qzdb` 单文件同时涵盖 **IPv4 与 IPv6** 全量网段。五大版本的字段体系遵循严格的**递增包含关系**：

```text
标准版 STD (6维)  ⊂  专业版 PRO (11维)  ⊂  旗舰版 MAX (15维)  ⊂  至尊版 ULT (25维)
└─ 自治域版 ASN (8维)：独立网络专项版 = 基础国家级地理 (4维) + 网络与风控 (4维)
```

### 1. 全球双栈版（IPv4 + IPv6 全量）文件大小对比

| 版本规格 | 字段维度 | 明文 CSV 行数 | 明文 CSV 大小 | **QZDB 二进制大小** | 体积压缩比 |
| :--- | :---: | ---: | ---: | ---: | :---: |
| **标准版 (`STD`)** | 6 维 | 1,183 万行 | 728.2 MB | **39.6 MB** | `18 : 1` |
| **自治域版 (`ASN`)** | 8 维 | 638 万行 | 579.5 MB | **26.6 MB** | `22 : 1` |
| **专业版 (`PRO`)** | 11 维 | 1,217 万行 | 1,469.0 MB | **49.1 MB** | `30 : 1` |
| **旗舰版 (`MAX`)** | 15 维 | 1,250 万行 | 2,091.1 MB | **62.6 MB** | `33 : 1` |
| **至尊版 (`ULT`)** | **25 维** | **1,250 万行** | **2,939.0 MB** | **69.1 MB** | **`42 : 1`** |

### 2. 国内精华双栈版（中国本土业务专用）文件大小对比

| 版本规格 | 字段维度 | 明文 CSV 行数 | 明文 CSV 大小 | **QZDB 二进制大小** | 典型单次延迟 |
| :--- | :---: | ---: | ---: | ---: | ---: |
| **标准版 (`STD`)** | 6 维 | 51.0 万行 | 29.8 MB | **2.9 MB** | `~50 ns` |
| **自治域版 (`ASN`)** | 8 维 | 38.5 万行 | 38.7 MB | **2.6 MB** | `~50 ns` |
| **专业版 (`PRO`)** | 11 维 | 85.3 万行 | 99.7 MB | **5.0 MB** | `~80 ns` |
| **旗舰版 (`MAX`)** | 15 维 | 87.6 万行 | 150.4 MB | **5.9 MB** | `~100 ns` |
| **至尊版 (`ULT`)** | **25 维** | **87.6 万行** | **201.2 MB** | **6.3 MB** | `~120 ns` |

### 3. 五大版本字段区别一览表

| 版本层级 | 字段数 | 包含字段标识 (`Key`) 与中文说明 |
| :--- | :---: | :--- |
| **标准版 (`STD`)** | **6 维** | `continent` (大洲)、`country_code` (国家二字码)、`country` (国家)、`province` (省份)、`city` (城市)、`isp` (运营商) |
| **专业版 (`PRO`)** | **11 维** | 在 **`STD` (6维)** 基础上新增 5 维精细地理：`district` (区县)、`geo_id` (6位行政区划码)、`longitude` (经度)、`latitude` (纬度)、`timezone` (IANA 时区) |
| **旗舰版 (`MAX`)** | **15 维** | 在 **`PRO` (11维)** 基础上新增 4 维网络与风控：`asn` (自治域号)、`as_name` (自治域组织)、`as_domain` (官方主域名)、`usage_type` (应用场景：家宽/移动/机房/云/专线/教育/CDN/爬虫) |
| **至尊版 (`ULT`)** | **25 维** | 在 **`MAX` (15维)** 基础上新增 10 维出海国际化：`continent_en`、`country_alpha3` (三字码)、`country_en`、`province_en`、`city_en`、`district_en`、`languages` (语言码)、`currency_code` (货币码)、`phone_prefix` (电话区号)、`emoji_flag` (国旗 Emoji) |
| **自治域版 (`ASN`)** | **8 维** | 独立网络路由版：`continent`、`country_code`、`country`、`isp` + `asn`、`as_name`、`as_domain`、`usage_type` |

<details>
<summary>📋 <b>点击展开：25 维全要素字段取值示例（深圳南山电信节点）与版本覆盖矩阵</b></summary>

| 序号 | 字段标识 (`Key`) | 中文名称 | 标准取值示例 | `STD` (6) | `PRO` (11) | `MAX` (15) | `ULT` (25) | `ASN` (8) |
| :---: | :--- | :--- | :--- | :---: | :---: | :---: | :---: | :---: |
| 1 | `continent` | 大洲中文 | `亚洲` | ✅ | ✅ | ✅ | ✅ | ✅ |
| 2 | `country_code` | 国家二字码 | `CN` | ✅ | ✅ | ✅ | ✅ | ✅ |
| 3 | `country` | 国家中文名 | `中国` | ✅ | ✅ | ✅ | ✅ | ✅ |
| 4 | `province` | 省份 / 州 | `广东省` | ✅ | ✅ | ✅ | ✅ | — |
| 5 | `city` | 地级市 | `深圳市` | ✅ | ✅ | ✅ | ✅ | — |
| 6 | `isp` | 运营商 | `中国电信` | ✅ | ✅ | ✅ | ✅ | ✅ |
| 7 | `district` | 区县行政区 | `南山区` | — | ✅ | ✅ | ✅ | — |
| 8 | `geo_id` | 行政区划代码 | `440305` | — | ✅ | ✅ | ✅ | — |
| 9 | `longitude` | 经度 (WGS84) | `113.93041` | — | ✅ | ✅ | ✅ | — |
| 10 | `latitude` | 纬度 (WGS84) | `22.53303` | — | ✅ | ✅ | ✅ | — |
| 11 | `timezone` | IANA 时区 | `Asia/Shanghai` | — | ✅ | ✅ | ✅ | — |
| 12 | `asn` | 自治域编号 | `4134` | — | — | ✅ | ✅ | ✅ |
| 13 | `as_name` | 自治域组织 | `CHINANET-BACKBONE` | — | — | ✅ | ✅ | ✅ |
| 14 | `as_domain` | 官方主域名 | `chinatelecom.cn` | — | — | ✅ | ✅ | ✅ |
| 15 | `usage_type` | 应用场景 | `Broadband` | — | — | ✅ | ✅ | ✅ |
| 16 | `continent_en` | 大洲英文 | `Asia` | — | — | — | ✅ | — |
| 17 | `country_alpha3` | 国家三字码 | `CHN` | — | — | — | ✅ | — |
| 18 | `country_en` | 国家英文名 | `China` | — | — | — | ✅ | — |
| 19 | `province_en` | 省份英文 | `Guangdong` | — | — | — | ✅ | — |
| 20 | `city_en` | 城市英文 | `Shenzhen` | — | — | — | ✅ | — |
| 21 | `district_en` | 区县英文 | `Nanshan` | — | — | — | ✅ | — |
| 22 | `languages` | 官方语言码 | `zh-CN` | — | — | — | ✅ | — |
| 23 | `currency_code` | 货币代码 | `CNY` | — | — | — | ✅ | — |
| 24 | `phone_prefix` | 国际电话区号 | `86` | — | — | — | ✅ | — |
| 25 | `emoji_flag` | 国旗 Emoji | `🇨🇳` | — | — | — | ✅ | — |

- **`usage_type` 8 大风控场景枚举**：`Broadband` (家庭宽带)、`Mobile` (移动基站)、`DataCenter` (IDC 机房)、`Cloud` (公有云主机)、`Enterprise` (政企专线)、`Education` (教育科研网)、`Anycast` (骨干 CDN/DNS)、`Crawler` (商业搜索引擎爬虫)。
- **经典版 `.dat` 16MB 容量边界提示**：经典 2.0 格式（`.dat`）采用 24-bit（16MB）字符池寻址。国内精华版字符池仅 1.6MB（总文件 6.4MB），可永久安全使用 `.dat`；全球版唯一字符池已突破 16.9MB，纯 IPv4 全球版存量用户请使用 6.0 格式（`.db`，33.2MB）或升级至新一代 `.qzdb`。详见 [docs/VERSION_COMPARISON.md](docs/VERSION_COMPARISON.md)。

</details>

## 四、性能测试与多格式横向对比

> **基准环境与口径声明**：实测硬件为 **Apple M4 Max（14 核，macOS）**，遵循 [docs/PERFORMANCE.md](docs/PERFORMANCE.md) 与 [docs/BENCH_CONTRACT.md](docs/BENCH_CONTRACT.md) 统一契约（`splitmix64` 固定种子、FNV-1a 指纹对账）。
> - **口径 A（整型随机·保守承诺值）**：50 万随机散布 IP 整型查询（省级 8.6MB 库；在 122MB 全球库上受 CPU L3 缓存物理规律影响，吞吐约为小库的 1/3）。
> - **口径 B（热点 / 16 核并发）**：重复查询命中缓存或 16 线程无锁并发吞吐。
> - **口径 C（字符串直查 `find_str`）**：含 IP 字符串解析 + 走树 + UTF-8 结果拼装全链路（CI 门禁口径）。

### 1. 同源数据下六大二进制格式横向评测

将同源 IP 数据集分别编译为 `.qzdb`、经典 `.db` (6.0 / 3.0)、经典 `.dat` (2.0)、`ip2region (.xdb)` 与 `MaxMind (.mmdb)` 同机对比：

| 格式与引擎 | 最快单次查询 | 单核随机吞吐 (QPS) | 协议与字段能力 | 核心检索机制与瓶颈分析 |
| :--- | ---: | ---: | :--- | :--- |
| 🚀 **[QZDB (`.qzdb`)](ip-qzdb-sdk)** | **~10 – 21 ns** | **46.9M ~ 97.3 M/s** | IPv4 + IPv6 双栈 (6~25维对象) | `16-bit Jump Table` + 扁平 `PATRICIA Trie` + `RowSchema` 偏移直取，零反序列化 |
| ⚡ **[qqzeng 6.0 (`.db`)](ip-classic-sdk/v6.0-db)** | **10.6 ns** | **30.0 M/s** (3,000万/s) | 纯 IPv4 (11维竖线字符串) | 65,536 前缀表 + 24-bit 二叉树（≤16 跳），纯偏移寻址，经典版最快 |
| 🟢 **[qqzeng 3.0 (`.db`)](ip-classic-sdk)** | **17.5 ns** | **15.5 M/s** (1,555万/s) | 纯 IPv4 (11维竖线字符串) | 前缀索引 + 紧凑区间检索 |
| 🟢 **[qqzeng 2.0 (`.dat`)](ip-classic-sdk/v2.0-dat)** | **29.9 ns** | **9.5 M/s** (950万/s) | 纯 IPv4 (11维竖线字符串) | 256 首字节桶 + 有序区间二分查找（国内版 6.4MB 稳定首选） |
| 📦 **ip2region (`.xdb` Content)** | **45.4 ~ 70.2 ns** | **6.28 ~ 6.99 M/s** (690万/s) | 纯 IPv4/独立v6 (5维字符串) | 二级向量索引 + 局部二分；`VectorIndex` 模式为 91万/s，`File` 模式为 68万/s |
| 🌐 **MaxMind (`.mmdb` Memory)** | **668.1 ns** | **1.38 M/s** (138万/s) | IPv4 + IPv6 (嵌套 Map) | 逐位二叉 Trie 遍历（无首阶段大表跳级）+ 叶子 TLV 动态反序列化；`mmap` 模式为 71万/s |

```text
单核随机查询吞吐对比 (Apple M4 Max，单位：万次/秒)：
QZDB (.qzdb, Go/C)        ████████████████████████████████████████████████ 9,730 万/s (~10 ns)
QZDB (.qzdb, Java/C#)     ████████████████████████ 4,810 万/s (~21 ns)
qqzeng-ip 6.0 (.db)       ███████████████ 3,000 万/s (10.6 ns)
qqzeng-ip 3.0 (.db)       ████████ 1,555 万/s (17.5 ns)
qqzeng-ip 2.0 (.dat)      █████ 950 万/s (29.9 ns)
ip2region (.xdb Content)  ███▌ 690 万/s (45~70 ns)
MaxMind (.mmdb Memory)    ▊ 138 万/s (668 ns)
MaxMind (.mmdb mmap)      ▍ 71 万/s (1,410 ns)
```

> 📖 完整 `BenchmarkDotNet` 多轮实测报表（单次纳秒、百万次毫秒、亿级吞吐）见 **[docs/BENCHMARK_COMPARISON.md](docs/BENCHMARK_COMPARISON.md)**。

### 2. QZDB 8 大主流语言 SDK 性能榜单

| 语言 SDK | 口径 A：单核整型随机 | 口径 B：单核热点 / 16核并发 | 口径 C：字符串 `find_str` | 架构特性说明 |
| :--- | ---: | ---: | ---: | :--- |
| 👑 **[C / C++](ip-qzdb-sdk/c/README.md)** | **96.5 M/s** | 101.8 M/s / **506 M/s** | **12.6 M/s** | 纯 C99 零拷贝 `mmap`，栈上实例化，单文件 `.c/.h` 直接内嵌 |
| ⚡ **[Go](ip-qzdb-sdk/go/README.md)** | **97.3 M/s** | 105.5 M/s / **719 M/s** | **12.1 M/s** | 跨平台 `mmap` 只读视图，原生 Goroutine 无锁高并发 |
| 🛡️ **[Rust](ip-qzdb-sdk/rust/README.md)** | **82.6 M/s** | 88.0 M/s / **233 M/s** | **11.5 M/s** | 值语义去共享化 + 4 路组相联缓存 + `Arc<str>` 零分配池化 |
| ☕ **[Java](ip-qzdb-sdk/java/README.md)** | **48.1 M/s** | 105.4 M/s / **136 M/s** | **11.9 M/s** | `MappedByteBuffer` 堆外内存视图 + 零分配 IPv6 解析，0 GC 停顿 |
| 🚀 **[C# (.NET)](ip-qzdb-sdk/netcore/README.md)** | **46.9 M/s** | 132.1 M/s / **550 M/s** | **20.9 M/s** | 原生指针内存映射，字符串全链路解析吞吐居首 |
| 🟢 **[Node.js](ip-qzdb-sdk/nodejs/README.md)** | **8.22 M/s** | 7.64 M/s *(裸循环 99.4M)* | **10.3 M/s** | V8 `BigInt` 极速偏移计算，纯 JS 零 C++ 扩展依赖 |
| 🐘 **[PHP](ip-qzdb-sdk/php/README.md)** | **5.67 M/s** | 0.73 M/s *(FPM/Swoole)* | **470 K/s** | 纯 PHP 零扩展，支持全内存 Buffer 与有界多页 LRU 流式双模式 |
| 🐍 **[Python](ip-qzdb-sdk/python/README.md)** | **1.30 M/s** | 1.28 M/s *(多进程扩展)* | **273 K/s** | 纯标准库 `mmap` 零三方依赖；另有 PyO3 扩展 `qqzeng-ip-rust` 可选 |

<details>
<summary>🔬 <b>点击展开：本机一键复现基准命令</b></summary>

```bash
# 口径 C：8 语言全量 CI 门禁基准（含 FNV-1a 指纹对账）
cd ip-qzdb-sdk && python3 tools/perf_gate.py

# 口径 A / B：各语言统一契约基准（支持 random / hot / seq 分布与多线程并发）
cd ip-qzdb-sdk && BENCH_OPS=2000000 ./c/bench_contract
```

</details>

## 五、30 秒快速安装与集成

全部主流语言均已发布官方稳定包，支持**包管理器一行命令引入**或**单文件/单目录零依赖源码拷贝**：

| 语言 | 官方包坐标 (最新版本) | 包管理器安装命令 | 源码直接拷贝入口 |
| :--- | :--- | :--- | :--- |
| ☕ **[Java](ip-qzdb-sdk/java/README.md)** | `com.qqzeng:qzdb:1.0.8` | Gradle: `implementation 'com.qqzeng:qzdb:1.0.8'` | 拷贝 [`com/qqzeng/qzdb/`](ip-qzdb-sdk/java/src/main/java/com/qqzeng/qzdb) 包 |
| 🚀 **[C# (.NET)](ip-qzdb-sdk/netcore/README.md)** | `QQZeng.Qzdb` (`1.0.8`) | `dotnet add package QQZeng.Qzdb` | 拷贝 [`ip-qzdb-sdk/netcore/*.cs`](ip-qzdb-sdk/netcore) |
| ⚡ **[Go](ip-qzdb-sdk/go/README.md)** | `v1.0.6` (Go Module) | `go get github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go@v1.0.6` | 拷贝 [`ip-qzdb-sdk/go/qzdb/`](ip-qzdb-sdk/go/qzdb) 目录 |
| 🛡️ **[Rust](ip-qzdb-sdk/rust/README.md)** | `qzdb` (`2.0.0`) | `cargo add qzdb` | 引入 [`ip-qzdb-sdk/rust/`](ip-qzdb-sdk/rust) crate |
| 🐍 **[Python](ip-qzdb-sdk/python/README.md)** | `qzdb` (`1.0.6`) | `pip install qzdb` | 拷贝单文件 [`qzdb.py`](ip-qzdb-sdk/python/qzdb.py) |
| 🟢 **[Node.js](ip-qzdb-sdk/nodejs/README.md)** | `@qqzengip/qzdb` (`1.0.6`) | `npm install @qqzengip/qzdb` | 拷贝单文件 [`qzdb.js`](ip-qzdb-sdk/nodejs/qzdb.js) |
| 🐘 **[PHP](ip-qzdb-sdk/php/README.md)** | `qqzeng/qzdb` (`1.2.0`) | `composer require qqzeng/qzdb` | 拷贝单文件 [`QzdbReader.php`](ip-qzdb-sdk/php/QzdbReader.php) |
| 👑 **[C / C++](ip-qzdb-sdk/c/README.md)** | 原生 C99 库 | 随宿主项目直接编译 | 拷贝 [`qzdb_reader.c`](ip-qzdb-sdk/c/qzdb_reader.c) + [`qzdb_reader.h`](ip-qzdb-sdk/c/qzdb_reader.h) |

## 六、多语言代码示例

> 💡 **生产环境提示**：请在服务启动时创建一次 `QzdbReader` 实例并全局长期复用（内部全只读，天然支持多线程无锁并发查询）。仓库 [`demo/qqzeng-ip-ult.qzdb`](demo/README.md) 提供了 25 维双栈演示样本库，可直接运行以下示例。

### Python
```python
from qzdb import QzdbReader

with QzdbReader("demo/qqzeng-ip-ult.qzdb") as reader:
    # 1. 查询返回竖线分隔字符串
    print(reader.find_str("114.114.114.114"))

    # 2. 查询返回强类型 GeoInfo 对象（同时支持 IPv4 与 IPv6）
    loc = reader.find("2400:3200::1")
    if loc is not None:
        print(loc.country_code, loc.province, loc.city, loc.isp, loc.get("usage_type"))
```

### Go
```go
package main

import (
    "fmt"
    "log"
    "github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"
)

func main() {
    reader, err := qzdb.Open("demo/qqzeng-ip-ult.qzdb", 0, true)
    if err != nil {
        log.Fatal(err)
    }
    defer reader.Close()

    if info := reader.Find("114.114.114.114"); info != nil {
        fmt.Println(info.Get("country"), info.Get("province"), info.Get("city"), info.Get("isp"))
    }
}
```

### Java
```java
import com.qqzeng.qzdb.GeoInfo;
import com.qqzeng.qzdb.QzdbReader;
import java.io.File;

try (QzdbReader reader = new QzdbReader.Builder(new File("demo/qqzeng-ip-ult.qzdb")).build()) {
    reader.find("114.114.114.114").ifPresent(loc -> {
        System.out.printf("%s %s %s (%s)%n",
            loc.getCountry(), loc.getProvince(), loc.getCity(), loc.get("usage_type"));
    });
}
```

### Rust
```rust
use qzdb::from_file;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let reader = from_file("demo/qqzeng-ip-ult.qzdb")?;
    if let Some(loc) = reader.find("114.114.114.114") {
        println!("{} {} {} [ASN: {}]", loc.country(), loc.city(), loc.isp(), loc.get("asn"));
    }
    Ok(())
}
```

<details>
<summary>📌 <b>点击展开：C# (.NET) / C / Node.js / PHP 完整调用示例</b></summary>

#### C# (.NET)
```csharp
using QQZeng.Qzdb;

using var reader = QzdbReader.Open("demo/qqzeng-ip-ult.qzdb");
GeoInfo? loc = reader.Find("114.114.114.114");
if (loc != null)
{
    Console.WriteLine($"{loc.Get("country")} {loc.Get("province")} {loc.Get("city")} - {loc.Get("isp")}");
}
```

#### C / C++
```c
#include <stdio.h>
#include "qzdb_reader.h"

int main(void) {
    qzdb_reader_t reader;
    if (qzdb_init(&reader, "demo/qqzeng-ip-ult.qzdb") != QZDB_OK) {
        return 1;
    }
    char buf[QZDB_VALUE_BUF_SIZE];
    if (qzdb_find_str(&reader, "114.114.114.114", buf, sizeof(buf)) == QZDB_OK) {
        printf("%s\n", buf);
    }
    qzdb_close(&reader);
    return 0;
}
```

#### Node.js
```javascript
const QzdbReader = require('@qqzengip/qzdb');

const reader = new QzdbReader.Builder('demo/qqzeng-ip-ult.qzdb').build();
try {
    const loc = reader.find('114.114.114.114');
    if (loc) {
        console.log(loc.get('country'), loc.get('city'), loc.get('isp'));
    }
} finally {
    reader.close();
}
```

#### PHP
```php
<?php
require_once __DIR__ . '/vendor/autoload.php';
use Qqzeng\Ip\QzdbBuilder;

$reader = QzdbBuilder::path('demo/qqzeng-ip-ult.qzdb')->build();
$loc = $reader->find('114.114.114.114');
if ($loc !== null) {
    echo $loc->get('country') . ' ' . $loc->get('city') . ' ' . $loc->get('isp') . PHP_EOL;
}
$reader->close();
```

</details>

## 七、仓库目录结构导航

| 目录路径 | 模块定位与包含内容 | 核心文档 |
| :--- | :--- | :--- |
| 👑 **[`ip-qzdb-sdk/`](ip-qzdb-sdk)** | **新一代 QZDB 极速双栈解析引擎 SDK**（C / C# / Go / Java / Node.js / PHP / Python / Rust 8 语言源码） | [SDK 总览](ip-qzdb-sdk/README.md) · [API 契约](ip-qzdb-sdk/API_CONTRACT.md) |
| 📦 **[`ip-classic-sdk/`](ip-classic-sdk)** | **经典版纯 IPv4 解析引擎 SDK**（含 `v6.0-db` 16 跳二叉树版与 `v2.0-dat` 经典二分版，共 17 套实现） | [经典版总览](ip-classic-sdk/README.md) |
| 📱 **[`phone-location-sdk/`](phone-location-sdk)** | **50 万+ 中国手机号段归属地解析 SDK**（涵盖 2.0 ~ 6.0 各版本多语言源码及 Redis 方案） | [号段库文档](phone-location-sdk/README.md) |
| 🗄️ **[`database-sql/`](database-sql)** | **关系型数据库建表与秒级批量入库脚本**（MySQL `LOAD DATA`、PostgreSQL `COPY`、SQL Server `BULK`） | [SQL 导入指南](database-sql/README.md) |
| 📋 **[`demo/`](demo)** | **官方演示样本数据**（含 `qqzeng-ip-ult.qzdb` 25 维双栈二进制样本与对应 CSV 对照表） | [样本说明](demo/README.md) |
| 📚 **[`docs/`](docs)** | **架构规范与性能评测白皮书**（格式规范、API 规范、版本选型指南、多格式 Benchmark 横评） | [版本对比](docs/VERSION_COMPARISON.md) · [性能横评](docs/BENCHMARK_COMPARISON.md) |

## 八、验证体系与商业授权

- **四层自动化质量门禁**：每次发版均通过 **L1 冒烟与损坏文件 Fail-Closed 测试**（`run_all_tests.sh`）、**L2 跨 8 语言逐字节一致性对账**（`cross_lang_verify.py`）、**L3 千万级明文黄金向量回归**（`run_batch_test_suite.py`）与 **L4 性能门禁**（`perf_gate.py`）。
- **开源与商业授权**：本仓库所有 SDK 源码遵循 **[MIT License](LICENSE)** 免费开源；商业生产环境数据库文件（`.qzdb` / `.db` / `.dat` / `.csv`）由 **[qqzeng.com](https://www.qqzeng.com)** 按月固定全量发版更新，按独立法人实体授权，**不限服务器数量、不限部署节点数、不限调用频次**。详见 [产品分层与商业授权说明](docs/PRODUCT_TIERS.md) 或访问 [官方服务中心](https://www.qqzeng.com/service/)。
