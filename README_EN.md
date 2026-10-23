# 👑 qqzeng-ip / QZDB: Ultra-Fast IP Geolocation Binary Engine

<div align="center">

**English** | [简体中文](./README.md)

</div>

<div align="center">

[![Verification](https://img.shields.io/badge/Verification-100%25%20Passed-brightgreen.svg)](https://github.com/zengzhan/qqzeng-ip)
[![Latency](https://img.shields.io/badge/Latency-%3C0.08%C2%B5s-purple.svg)](https://github.com/zengzhan/qqzeng-ip)
[![Languages](https://img.shields.io/badge/Languages-Rust%20%7C%20C%20%7C%20Go%20%7C%20Java%20%7C%20C%23%20%7C%20Node%20%7C%20PHP%20%7C%20Python-blue.svg)](https://github.com/zengzhan/qqzeng-ip)
[![License](https://img.shields.io/badge/License-MIT-orange.svg)](./LICENSE)

**Official Packages Available on All Major Registries 👇**

[![Maven Central](https://img.shields.io/maven-central/v/com.qqzeng/qzdb?logo=apache-maven&label=Maven%20Central&color=C71A36)](https://central.sonatype.com/artifact/com.qqzeng/qzdb)
[![NuGet](https://img.shields.io/nuget/v/QQZeng.Qzdb?logo=nuget&label=NuGet&color=004880)](https://www.nuget.org/packages/QQZeng.Qzdb)
[![PyPI](https://img.shields.io/pypi/v/qzdb?logo=pypi&label=PyPI&color=3776AB)](https://pypi.org/project/qzdb/)
[![npm](https://img.shields.io/npm/v/@qqzengip/qzdb?logo=npm&label=npm&color=CB3837)](https://www.npmjs.com/package/@qqzengip/qzdb)
[![crates.io](https://img.shields.io/crates/v/qzdb?logo=rust&label=crates.io&color=DEA584)](https://crates.io/crates/qzdb)
[![Packagist](https://img.shields.io/packagist/v/qqzeng/qzdb?logo=composer&label=Packagist&color=F28D1A)](https://packagist.org/packages/qqzeng/qzdb)
[![Go Module](https://img.shields.io/badge/Go%20Module-v1.0.6-00ADD8?logo=go)](https://pkg.go.dev/github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go)

</div>

> **QZDB (qqzeng IP Database)** is a next-generation IP geolocation and mobile phone prefix binary search engine engineered for enterprise-grade high concurrency and cloud-native architectures. Powered by a **two-phase Patricia Trie lookup algorithm**, **`mmap` zero-copy**, and **lock-free concurrent design**, a single machine achieves 5M+ QPS (native C/Rust reaches 10M+ QPS with sub-80ns latency, scaling to tens or hundreds of millions QPS under multi-threaded caching; see docs/PERFORMANCE.md for methodology and reproduction).

---

> [!IMPORTANT]
> **🚀 Why Choose the QZDB Engine?**
> - ⚡ **Extreme Performance**: Rust, C, and Go leverage read-only memory mapping (`mmap`) for zero heap allocations during query. Single lookup latency drops to **< 0.08 µs (80 nanoseconds)**.
> - 🛡️ **100% Boundary Verification**: Evaluated across all `959,162` CIDR intervals at boundary and center IPs with **`2,877,486` exhaustive non-sampled checks**, achieving a **100.00% pass rate**.
> - 📦 **Ultra-Dense Storage**: Prefix compression algorithms reduce tens of millions of global IP/CIDR segments by **95%+** (compacted to just a few megabytes).
> - 🌐 **Native SDKs in 8 Languages**: Official first-party support for Rust, C/C++, Go, Java, C#, Node.js, PHP, and Python.

---

## 📑 Table of Contents

| Section | What You Will Find |
| :--- | :--- |
| [⚡ 30-Second Quick Start](#-30-second-quick-start) | 8-language one-line install commands + Hello World examples |
| [🧭 Product Navigation](#-product-navigation) | IP / Phone / Database SQL / Historical archive entry points |
| [📊 SDK Benchmark Ranking](#-sdk-benchmark-ranking) | Multi-language throughput and latency benchmarks |
| [📐 Algorithm & Architecture](#-algorithm--architecture) | Two-phase Patricia Trie + Jump Table mechanics |
| [⚖️ Binary Format Comparison](#️-binary-format-comparison) | Objective comparison with `.mmdb`, `.xdb`, and `.ipdb` |
| [📂 Project Structure & Specifications](#-project-structure--specifications) | Repository structure, file size, and 5 product editions |
| [🗺️ Schema & Data Dimensions](#️-schema--data-dimensions) | Continent, country, province, city, coordinates, ISP, timezone |
| [🎯 Application Scenarios](#-application-scenarios) | Smart DNS, CDN traffic scheduling, risk control |
| [📱 Phone Location Database](#-phone-location-database) | 500K+ mobile prefix `.dat` format & Redis solution |

---

## 🧭 Product Navigation

| Product Line | Highlights & Capabilities | Supported Languages / Formats | Quick Link |
| :--- | :--- | :--- | :---: |
| 🚀 **IP Flagship QZDB SDK** | **Next-Gen Flagship**: Two-phase Trie + `mmap` zero-allocation, nanosecond retrieval, full IPv4/IPv6 fields & risk profiling | 🦀 Rust · 🐹 Go · ☕ Java · ⚡ C/C++ <br> 🔷 C# · 🟩 Node · 🐍 Python · 🐘 PHP | 👉 [**`ip-qzdb-sdk/`**](./ip-qzdb-sdk) |
| 📦 **IP Classic SDK** | **Legacy Formats**: v6.0 (.db) and v2.0 (.dat) multi-language parsers | v6.0 (.db) SDK · v2.0 (.dat) SDK | 👉 [**`ip-classic-sdk/`**](./ip-classic-sdk) |
| 📱 **Phone Location SDK** | **500K+ Prefixes**: Binary DAT parsing (95.7%+ compression) & Redis caching | v2.0 ~ v6.0 DAT SDK · Redis Import | 👉 [**`phone-location-sdk/`**](./phone-location-sdk) |
| 🗄️ **Relational Database DDL** | **Database DDL**: Optimized table schemas, prefix indexing, and bulk import scripts | 🐬 MySQL · 🐘 PostgreSQL · 🪟 SQL Server | 👉 [**`database-sql/`**](./database-sql) |
| 🗂️ **IP Historical Versions & Tools** | **Historical Archive**: v3.0~v5.0 parsers, Big DAT, and Windows GUI query tool | v3.0~v5.0 Source · Desktop Tool | 👉 [**`ip-history-sdk/`**](./ip-history-sdk) |
| 📋 **Sanitized Demo Datasets** | **Sample Datasets**: IP geolocation and mobile prefix CSV / TXT / QZDB samples | China & Global Samples | 👉 [**`demo/`**](./demo) |

> 💰 **Get Full Datasets**: This repository includes sanitized evaluation samples. Five commercial editions (Standard from ¥299 / Pro ¥499 / ASN ¥399 / Max ¥699 / Ultimate ¥999) available in China or Global coverage. Perpetual buyout license; annual subscription updates auto-delivered on the 1st of each month (save 40%+ vs single updates).
> - 🛒 Products & Pricing: https://www.qqzeng.com/service/
> - 🔍 Online Live Data Verification: https://www.qqzeng.com/ip/

---

## 📊 SDK Benchmark Ranking

Dual metrics evaluated under random integer lookups (worst-case cache penalty) and string parsing (`find_str`). Figures guarded continuously by CI gates across 8 languages. Full definitions and reproduction steps documented in [docs/PERFORMANCE.md](./docs/PERFORMANCE.md).

| Language (Click for source) | Integer Lookup¹<br>**Metric A · Worst-Case Cache** | String Lookup<br>**Metric C · With IP Parse** | Features |
| :--- | ---: | ---: | :--- |
| 🔥 [Node.js](./ip-qzdb-sdk/nodejs) | **99.4 M/s** | 5.5 M/s | Fastest tier in JS ecosystem |
| 👑 [C / C++](./ip-qzdb-sdk/c) | **96.5 M/s** | 10.8 M/s | Ultra-lightweight · Zero dependencies |
| ⚡ [Go](./ip-qzdb-sdk/go) | **74.7 M/s** | 9.2 M/s | High concurrency · Single binary deploy |
| ☕ [Java](./ip-qzdb-sdk/java) | **74.9 M/s** | 11.9 M/s | Fastest string query throughput |
| 🛡️ [Rust](./ip-qzdb-sdk/rust) | **43.3 M/s** | 8.4 M/s | Memory safe · Zero overhead |
| 🚀 [C#](./ip-qzdb-sdk/netcore) | **33.1 M/s** | 11.0 M/s | Native SIMD / Float zero parsing |
| 🐘 [PHP](./ip-qzdb-sdk/php) | **5.67 M/s** | 429 K/s | Zero extensions · Pure PHP |
| 🐍 [Python](./ip-qzdb-sdk/python) | **0.97 M/s** | 273 K/s | Consistency baseline · Zero dependencies |

<details>
<summary>📐 Metric Definitions, Test Environment, and Reproduction</summary>

- **Metric A | Random Integer (Worst-case cache)**: Integer input, 500,000 scattered random IPs, 8.6MB provincial database, single-threaded — conservative baseline for capacity planning.
- **Metric C | String `find_str`**: String IP input (including IP parsing overhead), demo database — identical to CI test suite for cross-language comparison.
- **Environment**: Apple M4 Max (14 cores), single-thread best-of-3; x86 environments typically measure 2~4x lower; 122MB global database Metric A is approximately 1/3 of provincial database.
- **Single Lookup Latency (Metric A average)**: Compiled languages 10~30 ns, PHP ≈176 ns, Python ≈1.0 µs.
- ¹ Node / Java / PHP Metric A figures are measured with equivalent direct probes matching the contract scenario.
- Hot-cache repeated lookups reach **46M ~ 100M QPS** (Metric B); 16-thread lock-free scaling scales up to **730M QPS**.
- Full definitions and run commands in [docs/PERFORMANCE.md](./docs/PERFORMANCE.md).

</details>

---

## ⚡ 30-Second Quick Start

**No need to clone the repo** — QZDB SDKs are published to official package registries across all major programming languages.

### 📦 One-Line Package Installation

| Language | Registry | Command |
| :--- | :--- | :--- |
| 🐍 **Python** | PyPI | `pip install qzdb` |
| 🦀 **Rust** | crates.io | `cargo add qzdb` |
| 🐹 **Go** | Go Module | `go get github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go` |
| 🔷 **C#** | NuGet | `dotnet add package QQZeng.Qzdb` |
| 🟢 **Node.js** | npm | `npm i @qqzengip/qzdb` |
| 🐘 **PHP** | Packagist | `composer require qqzeng/qzdb` |
| ☕ **Java** | Maven Central | See dependency snippet below |
| ⚡ **C / C++** | Source Integration | Download [`qzdb_reader.c`](./ip-qzdb-sdk/c/qzdb_reader.c) + [`qzdb_reader.h`](./ip-qzdb-sdk/c/qzdb_reader.h) and compile |

<details>
<summary>☕ Java Maven / Gradle Dependency Snippet</summary>

Maven:
```xml
<dependency>
    <groupId>com.qqzeng</groupId>
    <artifactId>qzdb</artifactId>
    <version>1.0.7</version>
</dependency>
```

Gradle:
```groovy
implementation 'com.qqzeng:qzdb:1.0.7'
```

</details>

### 🚀 Hello World Examples (8 Languages)

All 8 languages share a unified design pattern: **Load once, keep instance active, query concurrently without locks**.

<details open>
<summary>🐍 Python</summary>

```python
from qzdb import QzdbReader

searcher = QzdbReader("qqzeng_ip_ult_china.qzdb")

# Return pipe-delimited string
print(searcher.find_str("114.114.114.114"))
# Asia|CN|China|Jiangsu|Nanjing|ChinaNet

# Return structured GeoInfo
loc = searcher.find("114.114.114.114")
if loc:
    print(loc.country, loc.province, loc.city, loc.isp)
```

</details>

<details>
<summary>🦀 Rust</summary>

```rust
use qzdb::QzdbReader;

// from_file leverages read-only mmap, returning a Result
let searcher = QzdbReader::from_file("qqzeng_ip_ult_china.qzdb")?;

if let Some(loc) = searcher.find("114.114.114.114") {
    // Typed field access (O(1), zero allocation)
    println!("Country: {}, City: {}", loc.country(), loc.city());
    // Dynamic field access
    println!("ISP: {}", loc.get("isp"));
}
```

</details>

<details>
<summary>🐹 Go</summary>

```go
import (
    "fmt"
    "log"

    "github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"
)

// Initialize and retain QzdbReader instance (mmap zero-copy, reuse globally)
searcher, err := qzdb.Open("qqzeng_ip_ult_china.qzdb", 0, true)
if err != nil {
    log.Fatal(err)
}
defer searcher.Close()

// Return pipe-delimited string
fmt.Println(searcher.FindStr("114.114.114.114"))

// Return structured GeoInfo
info, err := searcher.Find("114.114.114.114")
if err == nil && info != nil {
    fmt.Println(info.GetCountry(), info.GetProvince(), info.GetCity())
}
```

</details>

<details>
<summary>☕ Java</summary>

```java
import com.qqzeng.qzdb.QzdbReader;
import com.qqzeng.qzdb.GeoInfo;

// Builder pattern supporting groupIndex / verifyCrc
try (QzdbReader reader = new QzdbReader.Builder(new File("qqzeng_ip_ult_china.qzdb")).build()) {
    GeoInfo loc = reader.find("114.114.114.114").orElse(null);
    if (loc != null) {
        System.out.println(loc.getCountry() + " " + loc.getProvince() + " " + loc.getCity());
    }
    System.out.println(reader.findStr("114.114.114.114"));
}
```

</details>

<details>
<summary>🔷 C# (.NET)</summary>

```csharp
using QQZeng.Qzdb;

using var reader = QzdbReader.Open("qqzeng_ip_ult_china.qzdb");
GeoInfo loc = reader.Find("114.114.114.114");
if (loc != null) {
    // Typed property access
    Console.WriteLine("Province: " + loc.GetProvince());
    // Dynamic field access
    Console.WriteLine("ISP: " + loc.Get("isp"));
}
```

</details>

<details>
<summary>🟢 Node.js</summary>

```javascript
const QzdbReader = require('@qqzengip/qzdb');

const reader = new QzdbReader.Builder("qqzeng_ip_ult_china.qzdb").build();
const loc = reader.find("114.114.114.114");
console.log(loc.get("country"), loc.get("city"));
```

</details>

<details>
<summary>🐘 PHP</summary>

```php
require_once __DIR__ . '/vendor/autoload.php';
use Qqzeng\Ip\QzdbReader;

$reader = new QzdbReader("qqzeng_ip_ult_china.qzdb");
$loc = $reader->find("114.114.114.114");
echo $loc->get('country') . ' ' . $loc->get('city');
```

</details>

<details>
<summary>⚡ C / C++</summary>

```c
#include "qzdb_reader.h"

qzdb_reader_t searcher;
qzdb_init(&searcher, "qqzeng_ip_ult_china.qzdb");   // Retain instance on stack
char buf[256];
qzdb_find_str(&searcher, "114.114.114.114", buf, sizeof(buf));
printf("Result: %s\n", buf);
```

</details>

> For in-depth API specifications, error handling, and multi-language guides, refer to **[QZDB Multi-Language SDK Guide](./ip-qzdb-sdk/README.md)**.

---

## 📐 Algorithm & Architecture

The QZDB engine utilizes a customized **Two-Phase Patricia Trie Search Algorithm**:

```mermaid
flowchart LR
    A["Target IP (e.g., 114.114.114.114)"] --> B["Phase 1: Jump Table (16-bit static prefix lookup)"]
    B --> C["Phase 2: Patricia Trie LPM (Read-only bit traversal)"]
    C --> D["Phase 3: String Pool Offset (O(1) lock-free read)"]
    D --> E["Output (China|Jiangsu|Nanjing|ChinaNet)"]
```

1. **Phase 1 (Jump Table Quick Level Skip)**:
   - **IPv4**: Prefetches a `16-bit` static prefix jump table ($2^{16} = 65,536$ slots). By examining the first two bytes of the IP, it jumps directly in $\mathcal{O}(1)$ to the target subtree node, eliminating recursive traversal across the first 16 tree levels.
   - **IPv6**: Dynamically calculates the optimal jump width `v6_jump_bits` (typically `16~20 bits`) based on database volume to achieve fast dimension reduction.
2. **Phase 2 (Trie Node Matching & String Pool Offset Reading)**:
   - Traverses right/left along the nodes using Longest Prefix Match (LPM). All routing pointers and leaf nodes are flattened and stored contiguously in the binary file for maximum CPU cache locality.
   - Upon a match, the SDK reads the final text from the preloaded read-only string pool in $\mathcal{O}(1)$ via physical byte offset, eliminating critical section locks completely.

| Dimension / Metric | Complexity | Technical Details & Advantages |
| :--- | :--- | :--- |
| **Lookup Time Complexity** | $\mathcal{O}(W - K)$ | Where $W$ is total IP bit width (32 for IPv4, 128 for IPv6), $K$ is the jump table width (e.g. 16 bits). On average, only ~16 comparisons complete the lookup. |
| **Space Complexity** | Ultra-compact | Prefix compaction keeps each Trie node at only 6~8 bytes; tens of millions of global IP prefixes occupy < 20MB. |
| **Memory Overhead** | $\mathcal{O}(0)$ | Compiled languages (Rust/C/Go) leverage OS `mmap` for zero-copy addressing with zero heap allocation and no GC pause. |

---

## ⚖️ Binary Format Comparison

Objective comparison between QZDB and standard industry binary IP formats (detailed benchmark report in [`docs/benchmark-comparison.md`](./docs/benchmark-comparison.md)):

| Format | Lookup Complexity | File Size | QZDB Advantage |
| :--- | :--- | :--- | :--- |
| Nested Tree `.mmdb` | O(W) + deserialization | Large | 16-bit Jump Table + Schema leaf physical offset, zero heap allocation |
| Flat Range Binary Search `.bin` | O(log N) multi-round | Medium | Trie naturally compacts overlapping ranges, significantly shorter lookup path |
| Partition Vector Index `.xdb` | O(W) / O(log N) | Very Small | Global RowSchema two-phase tree provides superior full-field extensibility |
| Proprietary Prefix Tree `.ipdb` | O(W) multiple hops | Small | Multi-language read-only string pool + completely lock-free concurrency |

---

## 📂 Project Structure & Specifications

| Directory / File | Description |
| :--- | :--- |
| **[`ip-qzdb-sdk/`](./ip-qzdb-sdk)** | ⚡ **QZDB Flagship Engine** — Rust / C / Go / Java / C# / Node.js / PHP / Python 8-language SDKs |
| **[`ip-classic-sdk/`](./ip-classic-sdk)** | 📦 **Classic IP SDK** — v6.0 (.db) and v2.0 (.dat) parsers |
| **[`phone-location-sdk/`](./phone-location-sdk)** | 📱 **Phone Location SDK** — v2.0~v6.0 DAT SDKs & Redis solutions |
| **[`database-sql/`](./database-sql)** | 🗄️ **Database SQL DDL** — MySQL / PostgreSQL / SQL Server schemas and bulk loaders |
| **[`ip-history-sdk/`](./ip-history-sdk)** | 🗂️ **Historical Archive & Tools** — v3.0~v5.0 archives, Big DAT, and Windows offline desktop tool |
| **[`demo/`](./demo)** | 📋 **Evaluation Samples** — IP and mobile prefix CSV / TXT / QZDB sample files |
| **[`docs/`](./docs)** | 📄 **Architecture & Guides** — Performance benchmarks, specifications, and style guides |

### IP Database Product Editions & Field Specifications

Following the official standard (5 Editions × 2 Regions × 3 Protocols):

| Edition | Field Count | Core Focus | Included Fields (in standard physical order) |
| :--- | :---: | :--- | :--- |
| **`std` Standard** | **6** | Basic Geolocation + ISP | `continent`, `country_code`, `country`, `province`, `city`, `isp` |
| **`pro` Professional** | **11** | High-Precision District | `std` fields + `district`, `geo_id`, `longitude`, `latitude`, `timezone` |
| **`asn` ASN Routing** | **8** | Network Routing (no district) | `continent`, `country_code`, `country`, `isp`, `asn`, `as_name`, `as_domain`, `usage_type` |
| **`max` Flagship** | **15** | Geolocation + ASN + Usage Risk | `pro` fields + `asn`, `as_name`, `as_domain`, `usage_type` |
| **`ult` Ultimate** | **25** | All-Dimension Comprehensive | `max` fields + 10 English extensions (`continent_en`, `country_alpha3`, `country_en`, `province_en`, `city_en`, `district_en`, `languages`, `currency_code`, `phone_prefix`, `emoji_flag`) |

---

## 🗺️ Schema & Data Dimensions

| Layer Category | Standard Name | Example | Standard / Reference |
| :--- | :--- | :--- | :--- |
| **Spatial / Geopolitical** | Continent / Country / Province / City / District | `Asia` / `China` / `Guangdong` / `Shenzhen` / `Nanshan` | GB/T 2260 administrative divisions; international state/county/city level |
| **English / International** | English Country / Alpha-2 Code | `China` / `CN` | ISO 3166-1 Alpha-2 standard |
| **Network & Carrier** | ISP / ASN | `China Telecom` / `Aliyun` / `AWS` / `AS4134` | Global major ISPs and cloud IDC prefix labeling |
| **Coordinate Projection** | Latitude / Longitude | `113.930478,22.53332` | WGS-84 coordinate system |
| **Time & Timezone** | Timezone | `Asia/Shanghai` | IANA Time Zone Database (TZDB) |
| **Administrative Code** | Regional Code | `440305` | 6-digit official administrative division code |

---

## 🎯 Application Scenarios

### 1. Smart DNS & Traffic Routing
- **ISP-Specific Line Optimization**: Route traffic dynamically by carrier (China Telecom, China Unicom, China Mobile, CERNET, etc.) segmented by province.
- **Geographical Line Routing**: Precise continent- and country-level steering.
- **Cloud Infrastructure Tagging**: Identify prefixes from AWS, GCP, Azure, Cloudflare, Alibaba Cloud, Tencent Cloud, and Huawei Cloud.

### 2. Enterprise Core Workloads
- **Content Delivery (CDN) Optimization**: Route end-users to the nearest edge point of presence (PoP).
- **Targeted Regional Delivery & Localization**: Localized content, currency, and language adaptation.
- **Risk Control & Security Defenses**: Identify datacenter IP traffic, crawler bots, VPN/proxy proxies, and unauthorized overseas access.

---

# 📱 Phone Location Database

> **500,000+ Mobile Phone Prefixes (First 7 digits)**: Covers province, city, district, postal code, area code, administrative code, and carrier with ultra-compact `.dat` binary format and Redis cache support. See **[`phone-location-sdk/`](./phone-location-sdk)**.

```text
Fields: Guangdong|Shenzhen|518000|0755|440300|China Mobile
Encoding: UTF-8    Byte Order: Little-Endian
```

### Storage & Compression Efficiency

| Version | File Format | File Size | Compression Ratio | Lookup Complexity | Core Technical Feature |
| :--- | :---: | :---: | :---: | :---: | :--- |
| **Raw Plaintext** | `.txt` / `.csv` | ~30 MB | — | — | Plain text |
| **v6.0 (Latest)** | `.dat` / `.db` | **1.28 MB** | **▸ 95.7%** | **$\mathcal{O}(1)$** | **High concurrency, microsecond response** |
| **v5.0** | `.dat` | 1.60 MB | ▸ 94.6% | $\mathcal{O}(1)$ | Flat data blocks, lock-free concurrency |
| **v4.0** | `.dat` | 1.80 MB | ▸ 94.0% | $\mathcal{O}(\log N)$ | Range binary search index |
| **v3.0** | `.dat` | 1.95 MB | ▸ 93.5% | $\mathcal{O}(\log N)$ | Basic binary search, low memory |
| **v2.0** | `.dat` | 2.40 MB | ▸ 92.0% | $\mathcal{O}(\log N)$ | Early binary format |

---

* **Live Online Demo**: https://www.qqzeng.com/ip
* **Product Purchase**: https://www.qqzeng.com/service/
* **Traffic Statistics & Analytics**: https://www.qqzeng.com/tongji.html
* **Official Website**: https://www.qqzeng.com
