# qqzeng-ip v6.0 (.db) 多语言解析 SDK

[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Version](https://img.shields.io/badge/Version-6.0.1-orange.svg)]()
[![Format](https://img.shields.io/badge/Format-.db%20(6.0)-brightgreen.svg)]()

**qqzeng-ip v6.0** 是一套专为高并发生产环境设计的工业级 IPv4 归属地解析引擎。底层采用 **65,536 一级前缀索引（16-bit）+ 紧凑 24-bit 二叉检索树（Trie）** 架构，将任意 IPv4 地址查询收敛在最多 16 次常数级内存位跳之内。

本仓库提供 **C、Rust、Go、Java、C# (.NET)、Node.js、PHP、Python** 八大主流语言的零依赖标准实现，统一遵循 **Fail-Closed 安全校验** 与 **无锁并发只读** 准则。

## 核心特性

* **常数级极速检索**：一级索引直接消解前 16 位前缀，剩余 16 位通过预解析的平坦内存数组按位寻址，最高吞吐达 **68.1M QPS**。
* **Fail-Closed 防御性安全**：加载期对文件尺寸、头部元数据、65,536 条一级索引、全部内部节点跳转边界及字符串区 UTF-8 编码进行全量核验；查询期内置 16 跳上限，彻底杜绝越界读取与环路死循环。
* **无锁线程安全**：初始化完成后所有内部索引与字符串表均为只读不可变状态，支持多线程 / 多协程无锁高并发访问。
* **灵活加载与 100% 向下兼容**：完整保留 `getInstance()` / `Instance()` 单例模式，同时全面支持**按指定路径构造多实例**（`from_path`）与**从内存字节流直接加载**（`from_bytes`）。
* **零外部依赖**：纯各语言标准库实现，单文件或单目录即可嵌入任何现有工程。
* **黄金数据集 100% 验证**：通过全球旗舰版 2,112,893 条 CIDR（共 **6,338,679 次首/中/末边界 IP 查询**）全量逐字节对账测试，0 误差。

## 性能基准评测

* **测试环境**：macOS Apple Silicon (ARM64)
* **测试数据集**：`qqzeng-ip-6.0-global.db`（全球旗舰版，2,112,893 条 CIDR 网段）
* **测试方法**：基于相同 LCG 随机序列执行 **3,000,000 次**连续 `uint32` 查询，每次查询强制提取结果字符串并将其长度累加至 `checksum`（彻底排除编译器死代码消除干扰）。

| 语言 | 运行时 / 编译器版本 | 加载及全量校验耗时 | 查询吞吐量 (QPS) | 常驻内存 (RSS) | 并发重放验证 |
| :--- | :--- | ---: | ---: | ---: | :---: |
| **Go** | Go 1.27 | 14.9 ms | **68,100,000** | 60.6 MB | ✅ 8 协程通过 |
| **C#** | .NET 10.0 (`netstandard2.0` 兼容) | 35.6 ms | **60,000,000** | 126.5 MB | ✅ 8 线程通过 |
| **Rust** | Rust 1.98 (`--release`) | 14.2 ms | **54,000,000** | 27.4 MB | ✅ 8 线程通过 |
| **Java** | OpenJDK 21 (JDK 8 字节码兼容) | 58.2 ms | **53,900,000** | 271.3 MB | ✅ 8 线程通过 |
| **C** | Clang 21 (`-O2 -std=c11`) | 9.3 ms | **38,200,000** | 55.2 MB | ✅ 8 线程通过 |
| **Node.js** | Node.js 26.5 (V8) | 31.3 ms | **21,800,000** | 129.5 MB | ✅ 单线程通过 |
| **PHP** | PHP 8.5 (`strict_types=1`) | 243.3 ms | **6,450,000** | 109.1 MB | ✅ 单进程通过 |
| **Python** | CPython 3.14 | 137.9 ms | **2,270,000** | 129.5 MB | ✅ 单进程通过 |

> **注**：加载耗时包含对全部 65,536 条一级索引与数百万节点指针的完整性安全校验；Java 端若在容器环境附加 `-XX:+UseSerialGC` 参数，常驻内存可由 271 MB 降至约 120 MB。

## 快速开始

### 环境要求

| 语言 | 最低兼容版本 | 推荐生产版本 | 构建 / 依赖说明 |
| :--- | :--- | :--- | :--- |
| **C** | C99 / C11 | Clang 17+ / GCC 13+ | 标准 C 库，提供 `Makefile` 与 `CMakeLists.txt` |
| **Rust** | Rust 1.70+ | Rust 1.85+ | 零第三方 crate 依赖 |
| **Go** | Go 1.19+ | Go 1.22+ | Go Modules，仅依赖标准库 |
| **Java** | JDK 8+ | JDK 17 / 21 LTS | 编译目标 `--release 8`，零外部 jar 依赖 |
| **C# (.NET)** | `.NET Standard 2.0` / .NET 6+ | .NET 8 / 10 | 同时支持旧版 .NET Framework 4.6.1+ 与现代 .NET |
| **Node.js** | Node.js 16+ | Node.js 20 / 22+ | 同时支持 CommonJS、`import` (ESM) 与 TypeScript `.d.ts` |
| **PHP** | PHP 7.2+ | PHP 8.2+ | PSR-4 规范，基于 `SplFixedArray` 与紧凑偏移索引 |
| **Python** | Python 3.8+ | Python 3.11+ | 纯标准库 `array('I')` 预解析，无需编译 C 扩展 |

### 多语言调用示例

默认单例会自动按顺序探测当前工作目录、`data/`、`../data/` 等常见路径下的 `qqzeng-ip-6.0-global.db`；生产环境推荐通过路径或字节流显式初始化。

#### 1. C
```c
#include "src/qqzeng_ip_search.h"

// 方式一：显式指定路径初始化（推荐）或 ipdb_init_default() 自动探测
ipdb_search_t *ctx = ipdb_init("/path/to/qqzeng-ip-6.0-global.db");
// 方式二：从内存字节流初始化
// ipdb_search_t *ctx = ipdb_init_bytes(buffer, buffer_len);

const char *loc1 = ipdb_find(ctx, "8.8.8.8");        // 字符串查询
const char *loc2 = ipdb_find_uint(ctx, 0x08080808u); // 32位无符号整数查询（极速路径）

ipdb_free(ctx);
```

#### 2. Rust
```rust
use rust::IpDbSearch;

// 单例模式（自动探测路径）
let db = IpDbSearch::instance();

// 多实例模式：显式指定路径或从 Vec<u8> 字节流加载
let db = IpDbSearch::from_path("/path/to/qqzeng-ip-6.0-global.db")?;
let loc = db.find("8.8.8.8");
let loc_fast = db.find_uint32(0x08080808);
```

#### 3. Go
```go
import "qqzeng-ip-go/ipdb"

// 单例模式
db, err := ipdb.Instance()
// 或指定路径 / 字节流：db, err := ipdb.NewFromPath("/path/to/qqzeng-ip-6.0-global.db")

loc := db.Find("8.8.8.8")
locFast := db.FindUint32(0x08080808) // 亦支持兼容旧版的 db.FindUint(0x0808, 0x0808)
```

#### 4. Java (JDK 8+)
```java
import com.qqzeng.ip.IpDbSearch;

// 单例模式（惰性加载，线程安全）
IpDbSearch db = IpDbSearch.getInstance();
// 多实例模式：new IpDbSearch("/path/to/qqzeng-ip-6.0-global.db") 或 IpDbSearch.fromBytes(bytes)

String loc = db.find("8.8.8.8");
String locFast = db.findUint(0x08080808);
```

#### 5. C# (.NET)
```csharp
using qqzengIp;

// 单例模式
var db = IpDbSearch.Instance;
// 多实例模式：IpDbSearch.FromPath("/path/to/qqzeng-ip-6.0-global.db") 或 IpDbSearch.FromBytes(bytes)

string loc = db.Find("8.8.8.8");
string locFast = db.Find(0x08080808u);
```

#### 6. Node.js (CommonJS / ESM / TypeScript)
```javascript
const IpDbSearch = require('./lib/IpDbSearch'); // ESM: import IpDbSearch from './lib/IpDbSearch.mjs'

// 单例模式
const db = IpDbSearch.getInstance();
// 多实例模式：new IpDbSearch('/path/to/qqzeng-ip-6.0-global.db') 或 IpDbSearch.fromBytes(buffer)

const loc = db.find('8.8.8.8');
const locFast = db.findUint(0x08080808);
```

#### 7. Python
```python
from qqzeng_ip.ipdb import IpDbSearch

# 默认单例或按路径构造独立实例
db = IpDbSearch('/path/to/qqzeng-ip-6.0-global.db')
# 字节流加载：db = IpDbSearch.from_bytes(raw_bytes)

loc = db.find('8.8.8.8')
loc_fast = db.find_uint(0x08080808)
```

#### 8. PHP
```php
use Qqzeng\Ip\IpDbSearch;

// 单例模式：IpDbSearch::getInstance()
// 指定路径或字节流：
$db = IpDbSearch::fromPath('/path/to/qqzeng-ip-6.0-global.db');

$loc = $db->find('8.8.8.8');
$locFast = $db->findUint(0x08080808);
```

## 返回字段格式

所有语言的查询接口在命中时均返回以 `|` 分隔的 11 段地理位置与网络信息字符串；当输入为非法 IP 字符串（如 `"256.1.1.1"`、`"1..2.3"`、`"0008.8.8.8"`）时，所有语言统一返回空字符串 `""`，绝不抛出未捕获异常。

```text
大洲|国家|省份|城市|区县|ISP|行政区码|国家英文名|ISO代码|经度|纬度
```

**返回示例**：
```text
亚洲|中国|广东省|深圳市|南山区|中国电信|440305|China|CN|113.93029|22.53291
```

## 目录结构与文档导航

```text
v6.0-db/
├── c/                  # C11 实现 (Makefile + CMakeLists.txt)
├── rust/               # Safe Rust 实现 (零拷贝字符串切片)
├── go/                 # Go Modules 实现 (package ipdb)
├── netcore/            # C# 实现 (netstandard2.0 + net10.0 双目标)
├── java/               # Java 实现 (JDK 8 兼容字节码, package com.qqzeng.ip)
├── nodejs/             # Node.js 实现 (CommonJS + ESM + TypeScript .d.ts)
├── python/             # Python 实现 (array('I') C 层步长切片预解析)
├── php/                # PHP 实现 (PSR-4, SplFixedArray + 紧凑偏移索引)
├── docs/
│   ├── API.md                  # 8 语言完整 API 参考手册
│   └── INTEGRATION.md          # Spring Boot / Gin / Express / Laravel 等生产集成指南
├── LICENSE             # MIT 开源许可证
└── README.md
```

## 许可证

本项目采用 [MIT License](LICENSE) 开源。
