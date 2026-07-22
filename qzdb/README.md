# QZDB: Next-Gen IP Geolocation Engine & Multi-Language SDK

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Cross--Platform-lightgrey.svg)]()
[![Verification](https://img.shields.io/badge/Verification-100%25%20Passed-brightgreen.svg)]()

QZDB (qqzeng IP Database) is a production-grade IP geolocation binary format and search engine. Leveraging a custom **24-bit Trie tree**, dynamic Schema, and zero-allocation memory-mapped (mmap) technology, QZDB delivers microsecond-level query latency on massive IP datasets.

[English](./README.md) | [简体中文](./README_zh.md)

---

## Highlights

* **Cross-Language Verified**: Complete databases are validated via an internal cross-verification pipeline (`cross_verify.py`) — each generated `.qzdb` file is parsed by all 8 SDKs (Python as reference baseline), comparing pipe-delimited output field by field.
* **Thread-Safe Read-Only Mmap**: C, Go, Rust, Java, and C# implementations load all string pools into read-only memory at initialization, ensuring lock-free concurrent queries.
* **Dynamic Schema**: Field structure (continent, country, province, city, district, ISP, coordinates, timezone, etc.) is automatically parsed from database metadata, supporting forward and backward compatibility.

| Language | Query Mode | Performance Characteristics | Recommended Use |
|:---|:---|:---|:---|
| **Rust** | Read-Only Mmap | Zero-allocation, mmap zero-copy | High-concurrency services, embedded, security-sensitive |
| **C / C++** | Read-Only Mmap | Minimal footprint, mmap zero-copy | IoT, gateways, kernel modules, resource-constrained |
| **Go** | Read-Only Mmap | Goroutine-safe, low-latency | Web services, API gateways, microservices |
| **C#** | Eager-load Once | .NET native integration | Enterprise .NET applications |
| **Java** | Eager-load Once | JVM cross-platform | Spring Boot, big data ecosystem |
| **Node.js** | Eager-load Once | Async non-blocking | Full-stack JavaScript |
| **PHP** | Dynamic Parsed | Zero-config drop-in | Web project rapid integration |
| **Python** | Dynamic Parsed | Quick prototyping | Data analysis, scripting, proof-of-concept |

---

## Supported Database Formats

QZDB supports all database variants with the `QZDB` magic header: Standard, Max, Ultimate, ASN editions, etc.

---

## Multi-Language Quick Start

All SDK languages provide a consistent interface design. Singleton pattern is recommended for production use.

### Python
```python
from qzdb import QzdbSearcher

searcher = QzdbSearcher.get_instance("qqzeng_ip_max_china.qzdb")

# Pipe string query
print(searcher.find_str("114.114.114.114"))
# Asia|CN|China|Jiangsu|Nanjing|ChinaNet

# Structured GeoInfo object
loc = searcher.find("114.114.114.114")
if loc:
    print(loc.country, loc.province, loc.city, loc.isp)
```

### Go
```go
import "qzdb_searcher/qzdb"

searcher, err := qzdb.Instance("qqzeng_ip_max_china.qzdb")

// Pipe string
res := searcher.FindStr("114.114.114.114")

// Structured GeoInfo
info := searcher.Find("114.114.114.114")
if info != nil {
    println(info.Get("country"), info.Get("city"))
}
```

### Java
```java
import qzdb.QzdbSearcher;
import qzdb.IpLocation;

QzdbSearcher searcher = QzdbSearcher.getInstance();
searcher.load("qqzeng_ip_max_china.qzdb");

IpLocation loc = searcher.find("114.114.114.114");
if (loc != null) {
    String[] values = loc.getValues();
}
```

### Rust
```rust
use qzdb_searcher::{from_file, QzdbSearcher};

let searcher = from_file("qqzeng_ip_max_china.qzdb");
if let Some(loc) = searcher.find("114.114.114.114") {
    println!("Country: {}, City: {}", loc.get("country"), loc.get("city"));
}
```

### C# (.NET)
```csharp
using Qqzeng;

var searcher = QzdbSearcher.GetInstance("qqzeng_ip_max_china.qzdb");
var loc = searcher.Find("114.114.114.114");
if (loc != null) {
    Console.WriteLine($"Province: {loc.Get("province")}");
}
```

### C / C++
```c
#include "qzdb_searcher.h"

qzdb_searcher_t* searcher = qzdb_instance("qqzeng_ip_max_china.qzdb");
char buf[256];
qzdb_find_str(searcher, "114.114.114.114", buf, sizeof(buf));
printf("Result: %s\n", buf);
```

### Node.js
```javascript
const { QzdbSearcher } = require('./qzdb');

const searcher = QzdbSearcher.getInstance("qqzeng_ip_max_china.qzdb");
const loc = searcher.find("114.114.114.114");
console.log(loc.country, loc.city);
```

### PHP
```php
use Qqzeng\Ip\QzdbSearcher;

$searcher = QzdbSearcher::getInstance("qqzeng_ip_max_china.qzdb");
$loc = $searcher->find("114.114.114.114");
echo $loc['country'] . ' ' . $loc['city'];
```

---

## Algorithm Architecture & Query Complexity

The QZDB engine uses a custom **two-phase Patricia Trie search algorithm**:

1. **Phase 1 (Jump Table Fast Skip)**:
   * **IPv4**: Pre-reads a 16-bit static prefix jump table (2^16 = 65,536 slots). Based on the first two bytes of the IP, it jumps directly to the specific subtree node in O(1), eliminating the first 16 levels of recursive traversal.
   * **IPv6**: Dynamically estimates optimal jump bits `v6_jump_bits` (typically 16~20 bits) based on data volume, achieving similar first-phase dimensionality reduction.

2. **Phase 2 (Trie Node Matching & String Pool Offset Reading)**:
   * In the located subtree, performs longest prefix matching (LPM) by traversing left/right along single-side nodes. All intermediate route pointers and leaf node data are stored contiguously in the file for excellent CPU cache locality.
   * Upon match, the SDK reads the final text from pre-loaded read-only string pools at O(1) using physical offsets, with zero locking overhead.

| Metric | Complexity | Technical Details |
| :--- | :--- | :--- |
| **Search Time** | O(W - K) | Where W is total IP bits (32 for IPv4, 128 for IPv6), K is jump bits (e.g., 16). Average 16 comparisons. |
| **Space** | Minimal | After prefix compression, each Trie node uses only 6~8 bytes; global IP tree storage under 20MB for tens of millions of records. |
| **Memory Overhead** | O(0) | Native compiled languages (Rust/C/Go) use OS mmap for zero-copy addressing, no heap allocation or GC pauses. |

---

## Binary IP Format Comparison

| Format | Time Complexity | Data Size | Core Mechanism | QZDB Optimization |
| :--- | :--- | :--- | :--- | :--- |
| **Generic Nested Tree (.mmdb)** | O(W) + deserialization | Large (metadata KV redundancy) | Classic binary Trie; leaves point to nested Map/List | **QZDB: First-phase skip + zero allocation** |
| **Flat Range Binary (.bin)** | O(log N) | Medium (stores full start/end IP ranges) | Sorted range binary search with prefix index cache | **QZDB: Trie compression + short path search** |
| **Partitioned Vector Index (.xdb)** | O(log N) | Minimal (indexes only core fields) | Vector index table + local B-Tree | **QZDB: Better scalability for global datasets** |
| **Proprietary Prefix Tree (.ipdb)** | O(W) | Small | Prefix node displacement Trie; index/data separated | **QZDB: Multi-language read-only string pools + lock-free design** |

---

## Production Usage Notes

1. **Reuse Searcher as Singleton**: Database loading involves header parsing, CRC verification, and string pool indexing — significant initialization overhead. Initialize once at startup and reuse globally.
2. **Memory**: C, Go, Rust use mmap for shared physical memory across processes. In JVM and managed runtimes, ensure heap limits accommodate database size.
3. **Thread Safety**: All query APIs (`find`, `find_str`) are stateless with read-only core fields, fully supporting multi-threaded high-concurrency lock-free queries.

---

## License

MIT

<!-- commit description sync 1784710613 -->
