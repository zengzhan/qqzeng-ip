# QZDB SDK — 新一代跨语言 IP 地理位置与网络画像解析引擎

[![License](https://img.shields.io/badge/License-MIT-blue.svg)](../LICENSE)
[![Format](https://img.shields.io/badge/Format-QZDB%20v2.4-orange.svg)](./FORMAT.md)
[![Dual-Stack](https://img.shields.io/badge/IPv4%20%26%20IPv6-Dual--Stack-success.svg)](../docs/VERSION_COMPARISON.md)

**QZDB (`ip-qzdb-sdk`)** 是面向高并发生产环境打造的下一代二进制 IP 数据库（`.qzdb`）多语言解析引擎。采用 **16-bit Jump Table + 紧凑 PATRICIA Trie 双阶段检索** 与 **自描述动态 RowSchema**，原生支持操作系统 `mmap` 只读零拷贝映射，在整合全球 **1,250 万+ IPv4/IPv6 双栈网段** 与 **6~25 维结构化字段** 的同时，提供**单核千万级、16 核数亿级 QPS** 的检索吞吐。

> 🔗 **快速导航**：[仓库首页 (`README.md`)](../README.md) · [五大版本与 25 维字段对照 (`VERSION_COMPARISON.md`)](../docs/VERSION_COMPARISON.md) · [多格式性能横评 (`BENCHMARK_COMPARISON.md`)](../docs/BENCHMARK_COMPARISON.md) · [8 语言基准白皮书 (`PERFORMANCE.md`)](../docs/PERFORMANCE.md) · [格式规范 (`FORMAT.md`)](./FORMAT.md) · [API 契约 (`API_CONTRACT.md`)](./API_CONTRACT.md)

## 一、8 大主流语言官方包与性能一览

全部 8 种语言 SDK 均遵循统一的 **API v2.4 契约**（无全局单例锁竞争、支持多实例并存、内置 Fail-Closed 完整性校验），并通过四层黄金向量对账验证：

| 语言与文档 | 官方包坐标与一行安装命令 | 单核整型随机 (口径 A) | 单核热点 / 16核并发 (口径 B) | 字符串直查 `find_str` (口径 C) |
| :--- | :--- | ---: | ---: | ---: |
| 👑 **[C / C++](./c/README.md)** | 单文件源码集成（`qzdb_reader.c` + `.h`） | **96.5 M/s** | 101.8 M/s / **506 M/s** | **12.6 M/s** |
| ⚡ **[Go](./go/README.md)** | `go get github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go@v1.0.6` | **97.3 M/s** | 105.5 M/s / **719 M/s** | **12.1 M/s** |
| 🛡️ **[Rust](./rust/README.md)** | `cargo add qzdb` (crates.io `2.0.0`) | **82.6 M/s** | 88.0 M/s / **233 M/s** | **11.5 M/s** |
| ☕ **[Java](./java/README.md)** | Maven / Gradle: `com.qqzeng:qzdb:1.0.8` | **48.1 M/s** | 105.4 M/s / **136 M/s** | **11.9 M/s** |
| 🚀 **[C# (.NET)](./netcore/README.md)** | `dotnet add package QQZeng.Qzdb` (`1.0.8`) | **46.9 M/s** | 132.1 M/s / **550 M/s** | **20.9 M/s** |
| 🟢 **[Node.js](./nodejs/README.md)** | `npm install @qqzengip/qzdb` (`1.0.6`) | **8.22 M/s** | 7.64 M/s *(裸循环 99.4M)* | **10.3 M/s** |
| 🐘 **[PHP](./php/README.md)** | `composer require qqzeng/qzdb` (`1.2.0`) | **5.67 M/s** | 0.73 M/s *(FPM/Swoole)* | **470 K/s** |
| 🐍 **[Python](./python/README.md)** | `pip install qzdb` (`1.0.6`) | **1.30 M/s** | 1.28 M/s *(多进程扩展)* | **273 K/s** |

> 📌 **测试基准说明**：基于 Apple M4 Max（14 核，macOS）实测。口径定义、全球库规模效应及复现方式详见 **[`docs/PERFORMANCE.md`](../docs/PERFORMANCE.md)**。

## 二、支持的数据库版本与字段规模

同一个 `QzdbReader` 引擎自适应支持 `.qzdb` 全系列版本，字段结构由文件头部元数据动态驱动：

| 版本代号 | 字段数 | 全球双栈版体积 | 国内双栈版体积 | 核心包含字段与适用场景 |
| :--- | :---: | ---: | ---: | :--- |
| **标准版 (`STD`)** | **6 维** | **39.6 MB** (源 728MB) | **2.9 MB** (源 30MB) | 大洲、国家码、国家、省份、城市、运营商 —— 高并发防刷、访问控制、省市分流 |
| **自治域版 (`ASN`)** | **8 维** | **26.6 MB** (源 580MB) | **2.6 MB** (源 39MB) | 大洲、国家码、国家、运营商 + `asn`、`as_name`、`as_domain`、`usage_type` —— BGP 路由与 CDN 调度 |
| **专业版 (`PRO`)** | **11 维** | **49.1 MB** (源 1.47GB) | **5.0 MB** (源 100MB) | 标准版 + 区县 (`district`)、6位行政区划码 (`geo_id`)、经纬度、IANA 时区 —— 本地生活与精准营销 |
| **旗舰版 (`MAX`)** | **15 维** | **62.6 MB** (源 2.09GB) | **5.9 MB** (源 150MB) | 专业版 + ASN 编号、自治域组织、主域名、网络场景 (`usage_type`) —— 业务风控、机房/家宽识别 |
| **至尊版 (`ULT`)** | **25 维** | **69.1 MB** (源 2.94GB) | **6.3 MB** (源 201MB) | 旗舰版 + 全套英文名、ISO 三字码、官方语言、法定货币、国际区号、国旗 Emoji —— 跨国出海与全维画像 |

> 📖 25 维字段完整字典与取值示例请查阅 **[`docs/VERSION_COMPARISON.md`](../docs/VERSION_COMPARISON.md)**。

## 三、多语言快速入门

v2.4 起**已全面移除全局单例（Singleton）**，核心类统一为 `QzdbReader`（C 语言为 `qzdb_reader_t`）。请在应用启动时创建实例并长期持有复用（多线程并发查询安全无锁）；如需同时加载多个库（如国内库 + 全球库），直接创建多个 `QzdbReader` 实例或使用 `QzdbRegistry` 管理即可。

### Python
```python
from qzdb import QzdbReader

with QzdbReader("../demo/qqzeng-ip-ult.qzdb") as reader:
    # 1. 查询返回竖线分隔字符串
    print(reader.find_str("114.114.114.114"))

    # 2. 查询返回结构化 GeoInfo 对象（同时支持 IPv4 与 IPv6）
    loc = reader.find("114.114.114.114")
    if loc is not None:
        print(loc.country, loc.province, loc.city, loc.isp, loc.get("usage_type"))
```

### Go
```go
import "github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"

reader, err := qzdb.Open("../demo/qqzeng-ip-ult.qzdb", 0, true)
if err != nil {
    panic(err)
}
defer reader.Close()

// 查询结构化 GeoInfo
if info := reader.Find("114.114.114.114"); info != nil {
    fmt.Println(info.Get("country"), info.Get("city"), info.Get("isp"))
}
```

### Java
```java
import com.qqzeng.qzdb.GeoInfo;
import com.qqzeng.qzdb.QzdbReader;
import java.io.File;

try (QzdbReader reader = new QzdbReader.Builder(new File("../demo/qqzeng-ip-ult.qzdb")).build()) {
    GeoInfo loc = reader.find("114.114.114.114").orElse(null);
    if (loc != null) {
        System.out.println(loc.getCountry() + " " + loc.getProvince() + " " + loc.getCity());
    }
    System.out.println(reader.findStr("114.114.114.114"));
}
```

### Rust
```rust
use qzdb::from_file;

let reader = from_file("../demo/qqzeng-ip-ult.qzdb").expect("open qzdb failed");
if let Some(loc) = reader.find("114.114.114.114") {
    println!("Country: {}, City: {}, ISP: {}", loc.country(), loc.city(), loc.isp());
}
```

### C# (.NET)
```csharp
using QQZeng.Qzdb;

using var reader = QzdbReader.Open("../demo/qqzeng-ip-ult.qzdb");
GeoInfo? loc = reader.Find("114.114.114.114");
if (loc != null) {
    Console.WriteLine($"{loc.Get("province")} {loc.Get("city")} ({loc.Get("isp")})");
}
```

### C / C++
```c
#include <stdio.h>
#include "qzdb_reader.h"

int main(void) {
    qzdb_reader_t reader;
    if (qzdb_init(&reader, "../demo/qqzeng-ip-ult.qzdb") != QZDB_OK) {
        fprintf(stderr, "init failed\n");
        return 1;
    }
    char buf[QZDB_VALUE_BUF_SIZE];
    if (qzdb_find_str(&reader, "114.114.114.114", buf, sizeof(buf)) == QZDB_OK) {
        printf("Result: %s\n", buf);
    }
    qzdb_close(&reader);
    return 0;
}
```

### Node.js
```javascript
const QzdbReader = require('@qqzengip/qzdb');

const reader = new QzdbReader.Builder('../demo/qqzeng-ip-ult.qzdb').build();
try {
    const loc = reader.find('114.114.114.114');
    if (loc) {
        console.log(loc.get('country'), loc.get('city'), loc.get('isp'));
    }
} finally {
    reader.close();
}
```

### PHP
```php
<?php
use Qqzeng\Ip\QzdbBuilder;

$reader = QzdbBuilder::path('../demo/qqzeng-ip-ult.qzdb')->build();
$loc = $reader->find('114.114.114.114');
if ($loc !== null) {
    echo $loc->get('country') . ' ' . $loc->get('city') . ' ' . $loc->get('isp');
}
$reader->close();
```

## 四、算法架构与主流格式对比

QZDB 引擎采用专门定制的 **双阶段 PATRICIA Trie + RowSchema 偏移直取架构**：
1. **第一阶段（Jump Table 快速跳级）**：
   - **IPv4**：预读 `16-bit` 静态前缀跳转表（$2^{16} = 65,536$ 个槽位），根据 IP 前两字节直接 $O(1)$ 定位子树根节点，消除前 16 层递归遍历。
   - **IPv6**：根据数据规模动态适配最佳跳转位宽 `v6_jump_bits`（通常 `16~20 bit`），首阶段快速降维。
2. **第二阶段（紧凑 PATRICIA Trie & 字符串池 $O(1)$ 切片）**：
   - 沿最长前缀匹配（LPM）路径遍历扁平化连续存放的 Trie 节点，并通过拓扑后序离线去重（Hash-Consing）合并全部相同子树。
   - 命中叶子节点后，直接通过 `IPRow` 定长偏移数组在只读字符串池中以 $O(1)$ 读取对应字段，**全程零反序列化、零堆内存分配、无锁并发**。

| 格式类别 | 代表格式 | 检索复杂度 | 字段结构 | 内存与性能特征 |
| :--- | :--- | :--- | :--- | :--- |
| **双阶段前缀树 + 动态列偏移** | 🚀 **`.qzdb` (QZDB)** | **$O(W - K)$** (跳过前16~20层) | 6~25 维动态 Schema 强类型直取 | `mmap` 零拷贝、零反序列化分配；单核 **46.9M~97.3M QPS**，支持双栈 |
| **前缀表 + 24-bit 二叉树** | ⚡ **`.db` (经典 6.0)** | $O(1)$ 常数位跳 (≤16跳) | 11 维固定竖线字符串 | 单核 **3,000 万 QPS**，纯偏移访问，仅支持 IPv4 |
| **首字节桶 + 区间二分** | 🟢 **`.dat` (经典 2.0)** | $O(\log N)$ 桶内二分 | 11 维固定竖线字符串 | 单核 **950 万 QPS**，受 16MB 字符池上限约束（国内版 6.4MB 稳定适用） |
| **二级向量索引 + 局部二分** | 📦 **`.xdb` (ip2region)** | $O(\log N)$ 局部二分 | 5 维固定竖线字符串 | 全内存 `Content` 模式约 **690 万 QPS**；`File` 模式约 **68 万 QPS** |
| **逐位二叉 Trie + TLV 解码** | 🌐 **`.mmdb` (MaxMind)** | $O(W)$ + 动态反序列化 | 嵌套 Map/List 结构 | 全内存模式约 **138 万 QPS**，`mmap` 模式约 **71 万 QPS**；查询触发对象分配 |

## 五、生产环境最佳实践

1. **长生命周期实例复用**：`QzdbReader` 加载文件时执行头部解析、CRC32 校验与内存映射。请在服务启动时初始化一次并长期持有复用，切勿在每次请求中重复 `Open/Close`。
2. **内存规划**：C、Go、Rust、Java、C# 的文件加载路径默认使用只读 `mmap`（由操作系统内核管理页缓存并跨进程共享物理页）；Node.js 采用全量 `Buffer`，PHP 根据可用内存自动切换全量缓冲或有界多页 LRU 流式模式。
3. **无锁并发安全**：所有查询方法（`find`、`findStr`、`findUint`）均为纯只读无状态操作，天然支持多线程/多协程高并发安全调用。

## 六、核心规范与参考文档

- **[`FORMAT.md`](./FORMAT.md) / [`docs/QZDB_FORMAT.md`](../docs/QZDB_FORMAT.md)**：QZDB 二进制存储格式规范
- **[`API_CONTRACT.md`](./API_CONTRACT.md) / [`docs/QZDB_SDK_API.md`](../docs/QZDB_SDK_API.md)**：8 语言统一 SDK API v2.4 设计规范
- **[`docs/VERSION_COMPARISON.md`](../docs/VERSION_COMPARISON.md)**：五大版本规格、文件大小与 25 维字段全景对照
- **[`docs/BENCHMARK_COMPARISON.md`](../docs/BENCHMARK_COMPARISON.md)**：QZDB vs `.db` / `.dat` / `.xdb` / `.mmdb` 性能评测白皮书
- **[`docs/PERFORMANCE.md`](../docs/PERFORMANCE.md)**：8 语言三口径基准数据与复现指南

<!-- commit: ip-qzdb-sdk: QZDB 多语言 SDK（Rust/C/Go/Java/C#/Node.js/PHP/Python） sync=1793943026 -->
