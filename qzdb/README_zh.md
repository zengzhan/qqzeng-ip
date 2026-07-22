# QZDB: 极速 IP 解析引擎与多语言 SDK

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Cross--Platform-lightgrey.svg)]()
[![Verification](https://img.shields.io/badge/Verification-100%25%20Passed-brightgreen.svg)]()

QZDB (qqzeng IP 数据库) 是一款面向生产环境的 IP 地理位置查询二进制格式与搜索引擎。利用定制的 **24位 Trie 树**、动态 Schema 以及零分配内存映射（mmap）技术，QZDB 在海量 IP 数据集上提供单机微秒级的查询延迟。

[简体中文](./README_zh.md) | [English](./README.md)

---

## 💎 核心能力

* **🔬 跨语言验证**：完整数据库经由内部交叉验证流水线（`cross_verify.py`）校验——将每个生成的 `.qzdb` 文件依次交由全部 8 种 SDK 解析（以 Python 为参考基线），逐字段比对竖线分隔输出。
* **⚙️ 线程安全与只读 Mmap**：C、Go、Rust、Java 和 C# 实现均在加载时将所有字符串池装载进只读内存，确保多线程并发查询无锁竞争。
* **🌐 动态 Schema**：自动从数据库元数据解析字段结构（例如大洲、国家、省份、城市、区县、ISP、经纬度、时区），保证 SDK 具有极强的向前与向后兼容性。

| 排名 | 语言 | 查询模式 | 单线程吞吐量 (Ops/sec) | 平均查询延迟 | 性能评价 | 状态 | 适用场景 |
| :---: | :--- | :--- | :--- | :--- | :--- | :---: | :--- |
| **1** | **Rust** | Read-Only Mmap | **10.0 M+ ~ 18.0 M+** | **< 0.08 µs** | 🛡️ 极速安全 | ✅ 生产推荐 | 高并发服务、嵌入式、安全敏感场景 |
| **2** | **C / C++** | Read-Only Mmap | **10.0 M+ ~ 18.0 M+** | **< 0.08 µs** | 👑 极致轻量 | ✅ 生产推荐 | IoT、网关、内核模块、资源受限环境 |
| **3** | **Go** | Read-Only Mmap | **8.0 M+ ~ 12.0 M+** | **< 0.10 µs** | ⚡ 高并发 | ✅ 生产推荐 | Web 服务、API 网关、微服务 |
| **4** | **C#** | Eager-load Once | **6.0 M+ ~ 10.5 M+** | **< 0.15 µs** | 🚀 优秀 | ✅ 生产推荐 | .NET 企业应用 |
| **5** | **Java** | Eager-load Once | **5.0 M+ ~ 8.0 M+** | **< 0.20 µs** | ☕ 稳健 | ✅ 生产推荐 | Spring Boot / 大数据生态 |
| **6** | **Node.js** | Eager-load Once | **3.0 M+ ~ 5.0 M+** | **< 0.33 µs** | 🔥 优异 | ✅ 生产推荐 | 全栈 JavaScript 应用 |
| **7** | **PHP** | Dynamic Parsed | **100 K+ ~ 2.0 M+** | **< 0.90 µs** | 🐘 实用 | ✅ 生产推荐 | Web 项目快速集成 |
| **8** | **Python** | Dynamic Parsed | **100 K+ ~ 2.2 M+** | **< 0.90 µs** | 🐍 标准 | ✅ 生产推荐 | 数据分析、脚本、快速原型 |

*(注：基准测试基于普通 x86_64 / ARM64 处理器单线程单核内存检索测试，不同 CPU 频率及物理内存带宽下测试数值可能有所浮动，仅供技术选型参考)*

---

## 📦 支持的数据库格式

QZDB 支持 magic 头部为 `QZDB` 的标准版、旗舰版、至尊版、ASN 版等所有数据库。

---

## 🛠️ 多语言快速入门

所有语言 SDK 均提供一致的接口设计，生产环境推荐使用单例（Singleton）模式。

### 🐍 Python
```python
from qzdb import QzdbSearcher

# 加载并查询 (推荐单例)
searcher = QzdbSearcher.get_instance("qqzeng_ip_max_china.qzdb")

# 查询返回 Pipe 字符串
print(searcher.find_str("114.114.114.114"))
# 亚洲|CN|中国|江苏|南京|中国电信

# 查询返回结构化 GeoInfo 对象
loc = searcher.find("114.114.114.114")
if loc:
    print(loc.country, loc.province, loc.city, loc.isp)
```

### 🐹 Go
```go
import "qzdb_searcher/qzdb"

// 初始化单例
searcher, err := qzdb.Instance("qqzeng_ip_max_china.qzdb")

// 查询 Pipe 字符串
res := searcher.FindStr("114.114.114.114")

// 查询结构化 GeoInfo
info := searcher.Find("114.114.114.114")
if info != nil {
    println(info.Get("country"), info.Get("city"))
}
```

### ☕ Java
```java
import qzdb.QzdbSearcher;
import qzdb.IpLocation;

// 初始化单例
QzdbSearcher searcher = QzdbSearcher.getInstance();
searcher.load("qqzeng_ip_max_china.qzdb");

// 查询
IpLocation loc = searcher.find("114.114.114.114");
if (loc != null) {
    String[] values = loc.getValues();
    // 对应 searcher.getFieldNames() 的索引获取数据
}
```

### 🦀 Rust
```rust
use qzdb_searcher::{from_file, QzdbSearcher};

let searcher = from_file("qqzeng_ip_max_china.qzdb");
if let Some(loc) = searcher.find("114.114.114.114") {
    println!("Country: {}, City: {}", loc.get("country"), loc.get("city"));
}
```

### ⚡ C# (.NET)
```csharp
using Qqzeng;

var searcher = QzdbSearcher.GetInstance("qqzeng_ip_max_china.qzdb");
var loc = searcher.Find("114.114.114.114");
if (loc != null) {
    Console.WriteLine($"Province: {loc.Get("province")}");
}
```

### 🔌 C / C++
```c
#include "qzdb_searcher.h"

qzdb_searcher_t* searcher = qzdb_instance("qqzeng_ip_max_china.qzdb");
char buf[256];
qzdb_find_str(searcher, "114.114.114.114", buf, sizeof(buf));
printf("Result: %s\n", buf);
```

### 🟢 Node.js
```javascript
const { QzdbSearcher } = require('./qzdb');

const searcher = QzdbSearcher.getInstance("qqzeng_ip_max_china.qzdb");
const loc = searcher.find("114.114.114.114");
console.log(loc.country, loc.city);
```

### 🐘 PHP
```php
use Qqzeng\Ip\QzdbSearcher;

$searcher = QzdbSearcher::getInstance("qqzeng_ip_max_china.qzdb");
$loc = $searcher->find("114.114.114.114");
echo $loc['country'] . ' ' . $loc['city'];
```

---

## 📐 算法架构与查询复杂度 (Algorithm Architecture)

QZDB 引擎核心采用专门定制的 **双阶段 Patricia Trie 树型检索算法**：
1. **第一阶段 (Jump Table 快速跳级)**：
   * **IPv4**：默认预读 `16-bit` 的静态前缀跳转表（$2^{16} = 65,536$ 个槽位）。根据 IP 的前两字节，直接 $\mathcal{O}(1)$ 跳转定位到 Trie 树的具体子树节点，消除前 16 层的递归遍历。
   * **IPv6**：根据数据量大小动态估算最佳跳转位数 `v6_jump_bits`（通常为 `16~20 bit`），同样实现首阶段的快速降维。
2. **第二阶段 (Trie 节点匹配 & 字符串池偏移读取)**：
   * 在定位到的子树节点中，以最长前缀匹配 (LPM) 算法沿单侧节点向右/向左遍历。所有中间路由指针和叶子节点数据在文件中扁平化连续存放，极具 CPU 缓存友好性。
   * 查询命中后，SDK 会直接根据其物理偏移量（Offset）在预载入的只读字符串池（String Pool）中以 $\mathcal{O}(1)$ 解析最终文本，全程免去临界区上锁（Lock-free）。

| 维度指标 | 复杂度 | 技术细节与优势 |
| :--- | :--- | :--- |
| **检索时间复杂度** | $\mathcal{O}(W - K)$ | 其中 $W$ 为 IP 地址总位数（IPv4 为 32 位，IPv6 为 128 位），$K$ 为首阶段跳转位数（如 16 位）。平均只需 16 次比对即可完成检索。 |
| **空间复杂度** | 极小量级 | 经过前缀压路机压缩，每个 Trie 节点仅占用 6~8 字节，千万级全球 IP 树存储开销低于 20MB。 |
| **内存开销 (Memory)** | $\mathcal{O}(0)$ | 原生编译型语言（Rust/C/Go）直接借助操作系统 `mmap` 进行零拷贝（Zero-copy）寻址，无堆分配与 GC 停顿。 |

---

## ⚖️ 主流二进制 IP 数据格式对比 (Format Comparison)

为了帮助架构师进行技术选型，以下列出了 QZDB 与业界主流二进制 IP 格式设计的客观对比：

| 格式分类 | 检索时间复杂度 | 数据结构体积 | 核心检索树与数据机制 | QZDB 的技术优化点 |
| :--- | :--- | :--- | :--- | :--- |
| **通用嵌套结构树格式 (`.mmdb`)** | $\mathcal{O}(W)$ <br> (需加上反序列化开销) | 较大 <br> (含元数据 Key-Value 冗余) | 经典二进制 Trie；叶子指向嵌套 Map/List 数据区 | **QZDB 首阶段快速跳级 + 零分配**。IPv4 预读 16-bit 跳过前 16 层；叶子基于 Schema 物理偏移，堆内存零分配。 |
| **扁平区间二分格式 (`.bin`)** | $\mathcal{O}(\log N)$ <br> (基于多轮二分匹配) | 中等 <br> (需存储完整起止 IP 范围) | 已排序起止范围二分检索；辅以前缀索引缓存 | **QZDB 的 Trie 压缩与短路径检索**。Trie 树结构天生善于压缩重叠段，平均检索路径大幅缩短。 |
| **分区向量索引格式 (`.xdb`)** | $\mathcal{O}(\log N)$ <br> (局部向量二分) | 极小 <br> (一般只索引部分核心地理字段) | 向量索引表 + 局部 B-Tree 区间检索 | **QZDB 对全球超大数据集扩展更佳**。采用全局 RowSchema 与双阶段树设计，能自适应承载从小体积到数行大规模全球网段数据的动态扩展。 |
| **专有前缀树格式 (`.ipdb`)** | $\mathcal{O}(W)$ <br> (多次树节点跳转) | 较小 <br> (索引节点与偏移量较为紧凑) | 前缀节点位移 Trie 检索；索引与数据区分离 | **QZDB 的多语种只读字符串池与完全免锁设计**。多维字段在初始化后即建立只读内存视图，多线程并发检索无锁竞争。 |

---

## ⚠️ 生产环境使用注意事项

1. **务必以单例模式复用 Searcher**：加载数据库涉及解析头部元数据、CRC 校验、预装载字符串索引池，有一定初始化开销。请务必在程序启动时初始化**一次**并全局复用。
2. **内存考虑**：在 C、Go、Rust 中数据库通过内存映射（`mmap`）加载，可在多进程间共享物理内存。在 JVM 等托管运行环境中，请确保堆内存上限（Heap limits）能够容纳数据库大小。
3. **线程安全性**：所有查询 API（`find`、`find_str`）皆为无状态设计，且核心字段在初始化后均为只读，完全支持多线程高并发免锁查询。

---

## 📄 授权协议
本开源 SDK 遵循 MIT 开源授权协议。
