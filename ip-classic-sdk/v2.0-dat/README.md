# qqzeng-ip v2.0 (.dat) 多语言解析 SDK

[![License](https://img.shields.io/badge/License-MIT-blue.svg)](../v6.0-db/LICENSE)
[![Format](https://img.shields.io/badge/Format-.dat%20(2.0)-orange.svg)]()
[![Languages](https://img.shields.io/badge/Languages-C%20%7C%20C%2B%2B%20%7C%20Go%20%7C%20Java%20%7C%20C%23%20%7C%20Node.js%20%7C%20PHP%20%7C%20Python%20%7C%20Ruby-brightgreen.svg)]()

**qqzeng-ip v2.0** 是针对 `.dat` 高压缩紧凑二进制格式打造的跨平台多语言解析引擎，全面覆盖 **C、C++、Go、Java、C# (.NET)、Node.js、PHP、Python、Ruby** 九大语言及 Redis 导入方案。

本目录代码已完成现代化重构，在 **100% 兼容历史类名与调用方式** 的基础上，彻底修复了早期版本在大容量全球库下的静态数组溢出、有符号整数比较翻转、`0.0.0.0/8` 前缀误判等历史隐患，并引入了**字符串池去重**、**平坦数组预解析**与**加载期 Fail-Closed 结构校验**。

## 二进制文件结构规范（2.0 格式）

`.dat` 文件采用小端序（Little-Endian）与 UTF-8 编码，由以下四个紧凑区域组成：

1. **文件头（16 字节，4 × `uint32`）**：依次存储 `[索引区首条偏移]`、`[索引区末条偏移]`、`[前缀区首条偏移]`、`[前缀区末条偏移]`。
2. **内容区（变长）**：存储唯一不重复的地理与运营商字符串，由索引区通过 `(offset, length)` 引用。
3. **索引区（12 字节/条）**：按起始 IP 严格升序排列，每条包含 `[起始 IP (4B)]` + `[结束 IP (4B)]` + `[内容偏移 (3B, 24-bit)]` + `[内容长度 (1B, 8-bit)]`。
4. **前缀区（9 字节/条）**：按 IPv4 首字节（`0~255`）划分桶区间，每条包含 `[首字节前缀 (1B)]` + `[起始索引号 (4B)]` + `[结束索引号 (4B)]`。查询时先通过前缀表 $O(1)$ 锁定桶区间，再在桶内执行无符号二分逼近查找。

## 黄金数据对账与性能基准评测

* **测试环境**：macOS Apple Silicon (ARM64)
* **黄金基准对账**：对每个 CIDR 网段的**起始 IP、中点 IP、结束 IP** 逐一查询，并与明文 CSV 的 11 字段拼接串进行逐字节比对；同时验证 15 类畸形输入（如 `"1..2.3"`、`"0008.8.8.8"`）与国内版未覆盖间隙网段（如 `"128.0.115.255"`、`"8.8.8.8"`）严格返回空串 `""`。
  * **国内精华版（`qqzeng-ip-china-utf8.dat`，550,671 条 CIDR）**：编译型语言全量对账 **1,652,013 次查询 0 误差**；解释型语言均匀抽样 **127,080 次查询 0 误差**。
  * **全球旗舰版（`qqzeng-ip-global-utf8.dat`，2,112,893 条 CIDR）**：编译型语言全量对账 **6,338,679 次查询 0 误差**；解释型语言均匀抽样 **301,842 次查询 0 误差**。
* **压测方法**：3,000,000 次 LCG 随机 `uint32` 查询，强制累加返回字符串长度至 `checksum`（与 v6.0 `.db` 同源数据的 `checksum` 逐位一致）。

| 语言 | 国内精华版 QPS | 国内版加载耗时 | 全球旗舰版 QPS | 全球版加载耗时 | 多线程并发验证 |
| :--- | ---: | ---: | ---: | ---: | :---: |
| **C++** (C++11 `-O2`) | **38,500,000** | 2.8 ms | **18,500,000** | 15.5 ms | ✅ 8 线程通过 |
| **Java** (OpenJDK 21 / JDK 8 兼容) | **35,500,000** | 19.5 ms | **16,800,000** | 70.1 ms | ✅ 8 线程通过 |
| **C** (C11 `-O2`) | **35,400,000** | 24.4 ms | **15,300,000** | 102.1 ms | ✅ 8 线程通过 |
| **Go** (Go 1.27) | **34,900,000** | 3.7 ms | **16,900,000** | 19.7 ms | ✅ 8 协程通过 |
| **Node.js** (Node 26.5) | **19,000,000** | 14.9 ms | **11,900,000** | 68.4 ms | ✅ 单线程通过 |
| **C#** (.NET 10 / `netstandard2.0`) | **18,900,000** | 19.5 ms | **12,000,000** | 37.8 ms | ✅ 8 线程通过 |
| **PHP** (PHP 8.5) | **6,500,000** | 35.1 ms | **3,400,000** | 111.8 ms | ✅ 单进程通过 |
| **Python** (CPython 3.14) | **3,400,000** | 48.9 ms | **2,300,000** | 203.8 ms | ✅ 单进程通过 |
| **Ruby** (Ruby 3.x) | **2,300,000** | 135.9 ms | **1,300,000** | 521.1 ms | ✅ 单进程通过 |

## 快速开始（多语言调用示例）

所有语言均支持 **默认路径单例**、**指定路径多实例（可同时加载国内版与全球版）** 以及 **内存字节流加载**，并提供字符串查询与 32 位无符号整数查询两套入口。

### 1. C (`qqzeng-ip-c`)
```c
#include "IpSearch2Fast.h"

// 方式一：默认路径单例
IpSearch2Fast* finder = getInstance();

// 方式二：指定路径多实例（支持国内库与全球库同时在线）
IpSearch2Fast* china  = geoip_init("/path/to/qqzeng-ip-china-utf8.dat");
IpSearch2Fast* global = geoip_init("/path/to/qqzeng-ip-global-utf8.dat");

const char* loc1 = geoip_query(china, "58.213.198.68");
const char* loc2 = geoip_query_uint(global, 0x08080808u); // 整数极速查询

geoip_free(china);
geoip_free(global);
```

### 2. C++ (`qqzeng-ip-c++`)
```cpp
#include "IPLocator.h"

// 方式一：线程安全单例（std::call_once 保护）
IPSearch* finder = IPSearch::instance();

// 方式二：多实例构造（文件损坏或不存在时抛出 std::runtime_error）
IPSearch china("/path/to/qqzeng-ip-china-utf8.dat");
std::string loc = china.Query("58.213.198.68");
const std::string& locFast = china.findUint(0x3AD5C644u); // 零拷贝引用返回
```

### 3. Go (`qqzeng-ip-go`)
```go
import "ipsearch"

// 方式一：默认单例（sync.Mutex 并发安全保护）
s, err := ipsearch.New()

// 方式二：指定路径或字节流加载多实例
china, err := ipsearch.NewIpSearch("/path/to/qqzeng-ip-china-utf8.dat")
loc := china.Get("58.213.198.68")         // 亦支持 china.Find(ip) / china.Query(ip)
locFast := china.GetUint(0x3AD5C644)      // 亦支持 china.FindUint(val) / china.QueryUint(val)
```

### 4. Java (`qqzeng-ip-java`, JDK 8+)
```java
import qqzeng.ip.IpSearch;

// 方式一：静态内部类线程安全单例
IpSearch finder = IpSearch.getInstance();

// 方式二：指定路径或 byte[] 构造多实例
IpSearch china  = new IpSearch("/path/to/qqzeng-ip-china-utf8.dat");
IpSearch global = new IpSearch("/path/to/qqzeng-ip-global-utf8.dat");

String loc = china.Get("58.213.198.68");  // 或 china.find("58.213.198.68")
String locFast = global.findUint(0x08080808);
```

### 5. C# (`qqzeng-ip-c#`, .NET Standard 2.0 / .NET 6+)
```csharp
using qqzeng_ip_dat;

// 方式一：带文件热更新监听的单例模式
string loc1 = IPSearch2Fast.Instance.Query("58.213.198.68");

// 方式二：标准多实例模式
var china  = new IPSearch("/path/to/qqzeng-ip-china-utf8.dat");
var global = new IPSearch("/path/to/qqzeng-ip-global-utf8.dat");
string loc2 = china.Query("58.213.198.68");
string locFast = global.QueryUint(0x08080808u);
```

### 6. Node.js (`qqzeng-ip-nodejs`, CommonJS + `.d.ts`)
```javascript
// 方式一：兼容旧版模块级 API
const ipsearch = require('./qqzeng-ip-nodejs/IpSearch');
ipsearch.load('/path/to/qqzeng-ip-china-utf8.dat');
const loc1 = ipsearch.query('58.213.198.68');

// 方式二：多实例类 API
const { IpSearch } = require('./qqzeng-ip-nodejs/IpSearch');
const china  = new IpSearch('/path/to/qqzeng-ip-china-utf8.dat');
const global = new IpSearch('/path/to/qqzeng-ip-global-utf8.dat');
const loc2 = china.find('58.213.198.68');
const locFast = global.findUint(0x08080808);
```

### 7. PHP (`qqzeng-ip-php`, PHP 7.2+ ~ 8.5+)
```php
require_once __DIR__ . '/qqzeng-ip-php/IpSearch.php';

$china  = new IpSearch('/path/to/qqzeng-ip-china-utf8.dat');
$global = new IpSearch('/path/to/qqzeng-ip-global-utf8.dat');

$loc = $china->get('58.213.198.68');      // 或 $china->find('58.213.198.68')
$locFast = $global->findUint(0x08080808);
```

### 8. Python (`qqzeng-ip-python`, Python 3.8+)
```python
from IpSearch import IpSearch

# 支持上下文管理器与多实例
with IpSearch('/path/to/qqzeng-ip-china-utf8.dat') as china:
    loc = china.Find('58.213.198.68')     # 或 china.find('58.213.198.68')
    loc_fast = china.find_uint(0x3AD5C644)

# 如需结构化对象返回，可使用兼容层 IpSearch23：
# from IpSearch23 import IpSearch as IpSearchObj
# info = IpSearchObj('/path/to/qqzeng-ip.dat').lookup('58.213.198.68')
```

### 9. Ruby (`qqzeng-ip-ruby`, Ruby 2.4+)
```ruby
require_relative 'qqzeng-ip-ruby/zeng_ip'

china = ZengIP.new('/path/to/qqzeng-ip-china-utf8.dat')
loc = china.find('58.213.198.68')         # 返回 '|' 分隔字符串，未命中返回 ''
loc_fast = china.find_uint(0x3AD5C644)
hash_info = china.info('58.213.198.68')   # 兼容旧版：返回结构化 Hash，未命中返回 nil
```

## 核心工程重构说明（相对早期历史版本）

1. **消除容量上限与内存暴涨**：移除 C 版硬编码 `ipMap[1000000][2]` 静态数组限制；全语言引入 `(offset, length)` 字符串池去重（全球版 137 万条索引记录仅对应约 16 万条唯一地域字符串），消除了数百万次重复字符串分配。
2. **修复 `0.0.0.0/8` 桶与未命中误判**：将缺失前缀统一标记为 `-1`（或 `None`/`nil`），修复了早期版本使用 `if (high == 0)` 判定前缀缺失而导致第 0 号记录（`low = 0, high = 0`）漏查或国内版未覆盖前缀误查第 0 条记录的缺陷。
3. **修复无符号整数与右边界比较**：Java 版采用 `Integer.compareUnsigned` 消除 `>= 128.0.0.0` 网段在有符号 `int` 下的负数翻转；Ruby 版修复了二分查找 `end_ip_num > k` 漏写 `=` 导致网段末尾 IP 查错的 Bug。
4. **线程安全与热加载原子化**：C++ 移除 `static` 局部变量并改用 `std::call_once` 单例；Go `New()` 增加互斥锁保护；C# `FileSystemWatcher` 热重载改为构造不可变 `DatEngine` 快照后执行 `volatile` 原子引用替换，确保线上高并发查询永不读到半初始化状态。

## 目录结构

```text
v2.0-dat/
├── qqzeng-ip-c/        # C11 实现 (IpSearch2Fast.h/.c + Test.c + Makefile)
├── qqzeng-ip-c++/      # C++11 实现 (IPLocator.h/.cpp + main.cpp + Makefile)
├── qqzeng-ip-go/       # Go Modules 实现 (ipsearch.go + ipsearch_test.go)
├── qqzeng-ip-java/     # Java 8+ 实现 (IpSearch.java)
├── qqzeng-ip-c#/       # C# 实现 (IPSearch.cs + IPSearch2Fast.cs + .csproj)
├── qqzeng-ip-nodejs/   # Node.js 实现 (IpSearch.js + IpSearch.d.ts + test.js)
├── qqzeng-ip-php/      # PHP 7.2+ 实现 (IpSearch.php + IpSearchOptimized.php + test.php)
├── qqzeng-ip-python/   # Python 3.8+ 实现 (IpSearch.py + IpSearch23.py + test.py)
├── qqzeng-ip-ruby/     # Ruby 实现 (zeng_ip.rb)
├── qqzeng-ip-redis/    # Redis ZSET 批量导入脚本 (ip-import.php + IpSearch.php)
└── README.md
```

## 许可证

本项目采用 [MIT License](../v6.0-db/LICENSE) 开源。

<!-- commit: v2.0-dat: 经典 2.0 (.dat) 多语言解析 SDK（C/C++/Go/Java/C#/Node.js/PHP/Python/Ruby） sync=1792914953 -->
