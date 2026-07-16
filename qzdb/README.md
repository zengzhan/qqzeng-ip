# QZDB: High-Performance IP Lookup Engine & Multi-Language SDKs

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Cross--Platform-lightgrey.svg)]()
[![Verification](https://img.shields.io/badge/Verification-100%25%20Passed-brightgreen.svg)]()

QZDB (qqzeng IP Database) is a next-generation binary format and search engine designed for ultra-high performance, thread-safe IP geo-location queries. Leveraging a customized **24-bit Trie tree**, dynamic schemas, and zero-allocation memory mapped (mmap) technology, QZDB provides microsecond-level query latencies across massive IP database distributions.

[简体中文](./README_zh.md) | [English](./README.md)

---

## 💎 Key Highlights & Real Performance Benchmarks

* **🚀 Astronomical QPS (Queries Per Second)**: Single-threaded, in-memory lookup benchmarks run under high-end hardware (e.g. Apple Silicon / Intel Xeon), excluding network and disk I/O:
  * **Rust / C / C++**: `10.0M+` ~ `18.0M+` QPS (Zero heap allocation, pure pointer arithmetic; small database reaches `18.5M+` QPS)
  * **Go**: `8.0M+` ~ `12.0M+` QPS
  * **C# (.NET)**: `6.0M+` ~ `10.5M+` QPS
  * **Java**: `5.0M+` ~ `8.0M+` QPS
  * **Node.js**: `3.0M+` ~ `5.0M+` QPS
  * **Python / PHP**: `100K+` ~ `2.0M+` QPS (depending on database size and lookup depth)
* **🔬 Cross-Language Verification**: The full database is validated by an internal cross-verification pipeline (`cross_verify.py`) that runs every generated `.qzdb` against all 8 SDKs (Python as the reference baseline), comparing pipe-delimited output field-by-field. This pipeline is exercised in CI before each release; the published repository ships the SDK engines and test harnesses, while the `.qzdb` datasets are distributed separately (see *Database Files* below).
* **⚙️ Thread-Safe Read-Only Mmap**: The C, Go, Rust, Java, and C# implementations load all string pools eagerly into immutable read-only memory, guaranteeing absolute thread-safety and zero query-time locking overhead.
* **🌐 Dynamic Fields Schema**: Supports dynamic fields (e.g. continent, country, province, city, district, ISP, longitude, latitude, timezone) determined at runtime by the database metadata.

---

## 📦 Database File Formats Supported

QZDB supports standard, ultimate, max, and ASN versions of the database with magic header `QZDB`.
Format properties are parsed dynamically from the database headers, making the SDKs forward and backward compatible.

---

## 🛠️ Multi-Language Quick Start

All SDKs expose a clean, consistent interface and support the Singleton pattern for production use.

### 🐍 Python
```python
from qzdb import QzdbSearcher

# Load and query (Singleton pattern recommended)
searcher = QzdbSearcher.get_instance("qqzeng_ip_max_china.qzdb")

# Query to string pipe format
print(searcher.find_str("114.114.114.114"))
# 亚洲|CN|中国|江苏|南京|中国电信

# Query to structured GeoInfo object
loc = searcher.find("114.114.114.114")
if loc:
    print(loc.country, loc.province, loc.city, loc.isp)
```

### 🐹 Go
```go
import "qzdb_searcher/qzdb"

// Initialize singleton
searcher, err := qzdb.Instance("qqzeng_ip_max_china.qzdb")

// Lookup string
res := searcher.FindStr("114.114.114.114")

// Lookup structured GeoInfo
info := searcher.Find("114.114.114.114")
if info != nil {
    println(info.Get("country"), info.Get("city"))
}
```

### ☕ Java
```java
import qzdb.QzdbSearcher;
import qzdb.IpLocation;

// Load singleton
QzdbSearcher searcher = QzdbSearcher.getInstance();
searcher.load("qqzeng_ip_max_china.qzdb");

// Query
IpLocation loc = searcher.find("114.114.114.114");
if (loc != null) {
    String[] values = loc.getValues();
    // Retrieve by index mapped to searcher.getFieldNames()
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

## 📐 Algorithm Architecture & Query Complexity

The QZDB engine relies on a custom **two-phase Patricia Trie lookup algorithm**:
1. **Phase 1 (Jump Table Shortcut)**:
   * **IPv4**: Prefetches a static `16-bit` prefix jump table ($2^{16} = 65,536$ slots). Based on the first two octets of the IP, it jumps directly to the matching subtree node in $\mathcal{O}(1)$ time, eliminating the first 16 steps of tree traversal.
   * **IPv6**: Dynamically calculates the optimal jump bits `v6_jump_bits` (usually `16~20 bit`) based on database volume, achieving immediate index shortcut.
2. **Phase 2 (Trie Traversal & String Pool Retrieval)**:
   * Traverses the subtree using Longest Prefix Matching (LPM). Trie nodes and pointers are laid out sequentially in flat binary, providing extreme CPU cache-friendliness.
   * On match, the SDK reads the string field data using relative physical offsets in the pre-loaded string pools in $\mathcal{O}(1)$ time without any query-time locking (Lock-free).

| Metric | Complexity | Technical Details & Advantages |
| :--- | :--- | :--- |
| **Lookup Time Complexity** | $\mathcal{O}(W - K)$ | Where $W$ is IP address width (32 for IPv4, 128 for IPv6), and $K$ is jump table bits (e.g. 16). Typically resolved in under 16 bitwise checks. |
| **Space Complexity** | Minimal Footprint | Nodes are heavily compressed, taking only 6~8 bytes per Trie node. Global datasets with millions of subnets require less than 20MB of index space. |
| **Heap Memory Allocation** | $\mathcal{O}(0)$ | Compiled environments (Rust/C/Go) leverage OS-level `mmap` for zero-copy addressing, avoiding heap allocations and GC overhead. |

---

## ⚖️ Mainstream Binary Format Comparison

To assist architects in technical selection, the table below provides an objective architectural comparison between QZDB and other mainstream binary IP database formats:

| Format Classification | Time Complexity | Structure Size | Core Retrieval & Data Mechanism | QZDB Optimization Points |
| :--- | :--- | :--- | :--- | :--- |
| **Common Nested Tree Format (`.mmdb`)** | $\mathcal{O}(W)$ <br> (plus deserialization overhead) | Larger <br> (metadata Key-Value redundancy) | Classic binary Trie; leaves point to nested Map/List structures | **QZDB Prefix Jump Table + Zero Allocation**. IPv4 skips the first 16 layers using a 16-bit prefix index; leaf nodes match via Schema offsets. |
| **Flat Range Binary Search (`.bin`)** | $\mathcal{O}(\log N)$ <br> (binary search range matching) | Medium <br> (requires storing full start/end ranges) | Sorted ranges binary search; cached via prefix index | **QZDB Trie Compression & Short Retrieval Paths**. Tries naturally compress overlapping ranges, significantly shortening the search route. |
| **Partitioned Vector Index (`.xdb`)** | $\mathcal{O}(\log N)$ <br> (localized vector search) | Tiny <br> (only indexes core geo fields) | Vector index table + localized B-Tree ranges | **QZDB Global Scalability**. Utilizes a global RowSchema and two-phase tree design to scale seamlessly from MBs to hundreds of MBs of data. |
| **Proprietary Prefix Tree (`.ipdb`)** | $\mathcal{O}(W)$ <br> (multiple node pointer hops) | Small <br> (compact index nodes & offsets) | Prefix shifting Trie traversal; index separated from data | **QZDB Multi-Language Read-Only String Pool & Lock-Free Design**. Multi-dimensional fields establish read-only memory views after initialization, allowing thread-safe concurrent searches. |

---

## ⚠️ Important Production Guidelines

1. **Keep Searcher as a Singleton**: Loading the database parses headers, checks CRC, and prepares string index pools. This has an initialization overhead. Always instantiate the searcher **once** at startup and reuse it across the application life cycle.
2. **Memory Considerations**: In C, Go, and Rust, databases are loaded via memory-mapping (`mmap`), which shares physical memory across processes. In JVM/JVM-like environments, ensure heap limits accommodate the size of the database.
3. **Thread Safety**: All query APIs (`find`, `find_str`) are completely thread-safe and stateless. You can query concurrently from hundreds of threads without mutex locks.

---

## 📁 Database Files

The `.qzdb` datasets are **not** bundled in this repository — they are distributed separately. To run the test harnesses (`run_all_tests.sh` and each language's `test.*`), place a dataset in the expected location:

```
qzdb/
├── data/
│   └── qqzeng_ip_std_china.qzdb   # required by run_all_tests.sh / test harnesses
└── ... (other language SDK dirs)
```

The SDKs resolve the database path in this order: current directory → `data/` → `../data/`. Any edition (`std` / `ult` / `max` / `asn` × `china` / `global`) is accepted; the schema (field names, group count, native scalar layout) is read dynamically from the file header and Metadata section, so the SDKs are forward and backward compatible across builds.

> **Note**: `run_all_tests.sh` reports `TEST_PASS` only when a dataset is present. Without `data/qqzeng_ip_std_china.qzdb` the suite exits non-zero — this is expected, not a SDK defect.

---

## 📄 License
This project is licensed under the MIT License.

<!-- commit description update -->
