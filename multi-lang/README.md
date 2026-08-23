# QZDB — 跨语言 IP 地理位置查询 SDK

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Cross--Platform-lightgrey.svg)]()

QZDB (qqzeng IP 数据库) 是一款面向生产环境的 IP 地理位置查询二进制格式与搜索引擎。采用 **Jump Table + Patricia Trie 双阶段检索**与动态 Schema；支持 mmap 的原生 SDK 可进行零拷贝寻址，在海量 IP 数据集上提供**单机微秒级**查询延迟。

---

## 核心能力

* **跨语言验证**：完整数据库经由内部交叉验证流水线（`cross_verify.py`）校验——将每个生成的 `.qzdb` 文件依次交由全部 8 种 SDK 解析（以 Python 为参考基线），逐字段比对竖线分隔输出。该流水线在每次发布前的 CI 中执行；本仓库仅发布 SDK 引擎与测试脚手架，`.qzdb` 数据集单独分发（见下文「数据库文件」）。
* **线程安全与只读数据**：C、Go、Rust、Java 和 C# 的文件路径加载使用只读映射（Rust 通过 `memmap2`）；Python 大文件也使用 mmap。Node.js 读取为全量 `Buffer`，PHP 根据内存限制选择缓冲或带 64 KiB 页缓存的流式模式。所有实现的查询快照均为只读，可并发复用。
* **动态 Schema**：自动从数据库元数据解析字段结构（例如大洲、国家、省份、城市、区县、ISP、经纬度、时区），保证 SDK 具有极强的向前与向后兼容性。

---

## 支持的数据库格式

QZDB 支持 magic 头部为 `QZDB` 的标准版、旗舰版、至尊版、ASN 版等所有数据库。

---

## 多语言快速入门

所有语言 SDK 均提供一致的接口设计。v2.4 起**已彻底移除单例（Singleton）**，核心类统一为 `QzdbReader`（C 语言用 `qzdb_reader_t`）：创建即持有实例，按需复用（跨文件/跨版本请各自持有实例，或用 `QzdbRegistry` 便利层），多实例并发互不干扰。

### Python
```python
from qzdb import QzdbReader

# 加载并查询（无单例；需按路径复用请用 QzdbRegistry）
searcher = QzdbReader("qqzeng_ip_ult_china.qzdb")

# 查询返回 Pipe 字符串
print(searcher.find_str("114.114.114.114"))
# 亚洲|CN|中国|江苏|南京|中国电信

# 查询返回结构化 GeoInfo 对象
loc = searcher.find("114.114.114.114")
if loc:
    print(loc.country, loc.province, loc.city, loc.isp)
```

### Go
```go
import "qzdb_reader/qzdb"

// 创建并持有 QzdbReader 实例（无单例；跨版本/多文件可各持一个）
searcher, err := qzdb.Open("qqzeng_ip_ult_china.qzdb", 0, true)

// 查询 Pipe 字符串
res := searcher.FindStr("114.114.114.114")

// 查询结构化 GeoInfo
info := searcher.Find("114.114.114.114")
if info != nil {
    fmt.Println(info.Get("country"), info.Get("city"))
}
```

### Java
```java
import com.qqzeng.qzdb.QzdbReader;
import com.qqzeng.qzdb.GeoInfo;

// 构建读取器（Builder 模式，支持 groupIndex / verifyCrc）
try (QzdbReader reader = new QzdbReader.Builder(new File("qqzeng_ip_ult_china.qzdb")).build()) {
    // 查询返回 GeoInfo
    GeoInfo loc = reader.find("114.114.114.114").orElse(null);
    if (loc != null) {
        System.out.println(loc.getCountry() + " " + loc.getProvince() + " " + loc.getCity());
    }
    // 查询返回 Pipe 字符串
    System.out.println(reader.findStr("114.114.114.114"));
}
```

### Rust
```rust
use qzdb_reader::{from_file, QzdbReader};

// from_file 返回 Result<QzdbReader, QzdbError>
let searcher = from_file("qqzeng_ip_ult_china.qzdb").expect("open qzdb failed");

if let Some(loc) = searcher.find("114.114.114.114") {
    // 直接字段访问 (O(1))
    println!("Country: {}, City: {}", loc.country(), loc.city());
    // 动态字段访问
    println!("{}", loc.get("isp"));

    // 序列化为 JSON (依赖 serde)
    let json = serde_json::to_string(&loc).unwrap();
    println!("{}", json);
}
```

### C# (.NET)
```csharp
using QQZeng.Qzdb;

using var reader = QzdbReader.Open("qqzeng_ip_ult_china.qzdb");
GeoInfo loc = reader.Find("114.114.114.114");
if (loc != null) {
    Console.WriteLine($"Province: {loc.Get("province")}");
}
```

### C / C++
```c
#include <stdio.h>
#include "qzdb_reader.h"

int main(void) {
    qzdb_reader_t reader;                       /* 栈上持有实例，无单例 */
    if (qzdb_init(&reader, "qqzeng_ip_ult_china.qzdb") != QZDB_OK) {
        fprintf(stderr, "init failed\n");
        return 1;
    }
    char buf[QZDB_VALUE_BUF_SIZE];              /* 值缓冲统一容量，防截断 */
    int rc = qzdb_find_str(&reader, "114.114.114.114", buf, sizeof(buf));
    if (rc == QZDB_OK) {
        printf("Result: %s\n", buf);            /* rc == QZDB_ERR_NOT_FOUND 表示未命中 */
    }
    qzdb_close(&reader);
    return 0;
}
```

### Node.js
```javascript
const QzdbReader = require('./qzdb');

const reader = new QzdbReader.Builder("qqzeng_ip_ult_china.qzdb").build();
const loc = reader.find("114.114.114.114");
console.log(loc.get("country"), loc.get("city"));
```

### PHP
```php
use Qqzeng\Ip\QzdbReader;

$reader = new QzdbReader("qqzeng_ip_ult_china.qzdb");
$loc = $reader->find("114.114.114.114");
echo $loc->get('country') . ' ' . $loc->get('city');
```

---

## 算法架构与查询复杂度 (Algorithm Architecture)

QZDB 引擎核心采用专门定制的 **双阶段 Patricia Trie 树型检索算法**：
1. **第一阶段 (Jump Table 快速跳级)**：
   * **IPv4**：默认预读 `16-bit` 的静态前缀跳转表（2^16 = 65,536 个槽位）。根据 IP 的前两字节，直接 O(1) 跳转定位到 Trie 树的具体子树节点，消除前 16 层的递归遍历。
   * **IPv6**：根据数据量大小动态估算最佳跳转位数 `v6_jump_bits`（通常为 `16~20 bit`），同样实现首阶段的快速降维。
2. **第二阶段 (Trie 节点匹配 & 字符串池偏移读取)**：
   * 在定位到的子树节点中，以最长前缀匹配 (LPM) 算法沿单侧节点向右/向左遍历。所有中间路由指针和叶子节点数据在文件中扁平化连续存放，对 CPU 缓存友好。
   * 查询命中后，SDK 会直接根据其物理偏移量（Offset）在预载入的只读字符串池（String Pool）中以 O(1) 解析最终文本，全程免去临界区上锁（Lock-free）。

| 维度指标 | 复杂度 | 技术细节与优势 |
| :--- | :--- | :--- |
| **检索时间复杂度** | O(W − K) | 其中 W 为 IP 地址总位数（IPv4 为 32 位，IPv6 为 128 位），K 为首阶段跳转位数（如 16 位）。平均只需 16 次比对即可完成检索。 |
| **空间复杂度** | 小 | 经前缀压缩后每个 Trie 节点仅占 6~8 字节，千万级全球 IP 树存储开销低于 20MB。 |
| **内存开销 (Memory)** | 取决于加载后端 / O(1) 单次查询 | C/Go/Rust/Java/C# 文件路径加载使用只读映射；Node.js 与 Rust/各语言内存字节入口保留拷贝语义；PHP 流式路径使用 64 KiB 页缓存。 |

---

## 主流二进制 IP 数据格式对比 (Format Comparison)

下表基于公开资料概述 QZDB 与常见二进制 IP 格式的机制差异；各格式的实际表现以官方基准为准。

| 格式分类 | 检索时间复杂度 | 数据结构体积 | 核心检索树与数据机制 | QZDB 的技术优化点 |
| :--- | :--- | :--- | :--- | :--- |
| **通用嵌套结构树格式（经典二进制 Trie 方案）** | O(W) <br> (需加上反序列化开销) | 较大 <br> (含元数据 Key-Value 冗余) | 经典二进制 Trie；叶子指向嵌套 Map/List 数据区 | **QZDB 首阶段快速跳级 + 零分配**。IPv4 预读 16-bit 跳过前 16 层；叶子基于 Schema 物理偏移，堆内存零分配。 |
| **扁平区间二分格式（`.bin` 类）** | O(log N)（基于多轮二分匹配） | 中等 <br> (需存储完整起止 IP 范围) | 已排序起止范围二分检索；辅以前缀索引缓存 | **QZDB 的 Trie 压缩与短路径检索**。Trie 树结构天生善于压缩重叠段，平均检索路径大幅缩短。 |
| **分区向量索引格式（`.xdb` 类）** | O(log N)（局部向量二分） | 极小 <br> (一般只索引部分核心地理字段) | 向量索引表 + 局部 B-Tree 区间检索 | **QZDB 对全球超大数据集扩展更佳**。采用全局 RowSchema 与双阶段树设计，能自适应承载从小体积到数行大规模全球网段数据的动态扩展。 |
| **专有前缀树格式（`.ipdb` 类）** | O(W) <br> (多次树节点跳转) | 较小 <br> (索引节点与偏移量较为紧凑) | 前缀节点位移 Trie 检索；索引与数据区分离 | **QZDB 的多语种只读字符串池与完全免锁设计**。多维字段在初始化后即建立只读内存视图，多线程并发检索无锁竞争。 |

---

## 生产环境使用注意事项

1. **复用实例而非每次重建**：加载数据库涉及解析头部元数据、CRC 校验、预装载字符串索引池，有一定初始化开销。请在程序启动时创建 `QzdbReader` 实例**一次**并长期持有复用（跨文件/跨版本各自持有一个实例，或用 `QzdbRegistry` 管理）。v2.4 已无单例，所谓「复用」指的是持有同一实例引用，而非依赖全局单例。
2. **内存考虑**：C、Go、Rust、Java、C# 的文件路径加载使用只读映射，可由操作系统共享物理页；Node.js 是全量 `Buffer`，PHP 流式模式只保留页缓存。使用 `from_bytes` / `OpenBuffer` 等内存入口时，应按其 API 的拷贝语义单独规划内存。
3. **线程安全性**：所有查询 API（`find`、`find_str`）皆为无状态设计，且核心字段在初始化后均为只读，完全支持多线程高并发免锁查询。

---

## 授权协议
本开源 SDK 遵循 MIT 开源授权协议。

---

## 核心设计文档

- **[二进制格式规范](../docs/QZDB_FORMAT.md)** - QZDB 底层存储二进制格式规范
- **[SDK API 设计规范](../docs/QZDB_SDK_API.md)** - QZDB 8 种语言 SDK API v2.4 规范
- **[SDK 同步指南](../docs/QZDB_SYNC_GUIDE.md)** - 多语言 SDK 同步与测试指南
