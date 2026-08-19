# QZDB: Next-Gen IP Geolocation Engine & Multi-Language SDK

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Cross--Platform-lightgrey.svg)]()
[![Verification](https://img.shields.io/badge/Verification-100%25%20Passed-brightgreen.svg)]()

QZDB (qqzeng IP Database) is a production-grade IP geolocation binary format and search engine. Leveraging a custom **Jump Table + Patricia Trie two-phase search algorithm**, dynamic Schema, and zero-allocation memory-mapped (mmap) technology, QZDB delivers microsecond-level query latency on massive IP datasets.

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

All SDK languages provide a consistent interface design. The core class is unified as `QzdbReader` (`qzdb_reader_t` in C): instances hold state upon creation and can be reused as needed (hold separate instances across files/versions or use `QzdbRegistry`), with concurrent multi-instance isolation.

### Python
```python
from qzdb import QzdbReader

searcher = QzdbReader("qqzeng_ip_ult_china.qzdb")

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
import "qzdb/qzdb"

searcher, err := qzdb.Open("qqzeng_ip_ult_china.qzdb", 0, true)

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
import com.qqzeng.qzdb.QzdbReader;
import com.qqzeng.qzdb.GeoInfo;

try (QzdbReader reader = new QzdbReader.Builder(new File("qqzeng_ip_ult_china.qzdb")).build()) {
    GeoInfo loc = reader.find("114.114.114.114").orElse(null);
    if (loc != null) {
        System.out.println(loc.getCountry() + " " + loc.getProvince() + " " + loc.getCity());
    }
    System.out.println(reader.findStr("114.114.114.114"));
}
```

### Rust
```rust
use qzdb::{from_file, QzdbReader};

let searcher = from_file("qqzeng_ip_ult_china.qzdb");
if let Some(loc) = searcher.find("114.114.114.114") {
    println!("Country: {}, City: {}", loc.country(), loc.city());
    println!("{}", loc.get("isp"));
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
#include "qzdb_reader.h"

qzdb_reader_t searcher;
qzdb_init(&searcher, "qqzeng_ip_ult_china.qzdb");
char buf[256];
qzdb_find_str(&searcher, "114.114.114.114", buf, sizeof(buf));
printf("Result: %s\n", buf);
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
| **Memory Overhead** | O(F) mapped space / O(1) per query | Native compiled languages (Rust/C/Go) use OS mmap for zero-copy addressing, no heap allocation or GC pauses after init. |

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

1. **Reuse Reader Instance**: Database loading involves header parsing, CRC verification, and string pool indexing — significant initialization overhead. Initialize once at startup and reuse the `QzdbReader` instance.
2. **Memory**: C, Go, Rust use mmap for shared physical memory across processes. In JVM and managed runtimes, ensure heap limits accommodate database size.
3. **Thread Safety**: All query APIs (`find`, `find_str`) are stateless with read-only core fields, fully supporting multi-threaded high-concurrency lock-free queries.

---

## License

MIT

<!-- commit description sync 1787122549 -->
