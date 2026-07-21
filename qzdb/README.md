# QZDB 多语言 IP 地理位置查询 SDK (qzdb-searcher)

> ⚠️ **数据库文件需单独获取**：本目录仅包含 8 种语言的解析引擎源码和调用示例，**不包含** `.qzdb` 数据库文件。
> 请从 [qqzeng.com](https://www.qqzeng.com) 获取 IP 数据库后，将 `.qzdb` 文件放入工作目录使用。

## 简介

高性能、跨平台的 IP 地理位置数据库查询引擎，支持 8 种语言。基于 **QZDB 二进制格式**：192 字节自描述头 + Binary Trie 跳表 + IPRow 间接层 + 多版本组 GeoEntry，支持单文件多版本共存与动态字段 Schema。

## 特性

- **多语言支持**: C, C#, Java, PHP, Go, Python, Node.js, Rust
- **单文件 SDK** — 拷贝即用，无外部依赖
- **QZDB 格式**: 24 位节点 Binary Trie（V4 跳表跳过前 16 层）+ 扁平 Trie 遍历（V6）
- **自描述 Schema**: 字段名、版本组、原生标量布局由文件 Metadata 段动态解析，SDK 前后向兼容
- **地理信息**: 洲、国家、省、市、区、运营商、区划代码、英文名、经纬度等（按版本组动态决定）

## 文件结构

```
├── c/                    # C 语言实现
│   ├── qzdb_searcher.c
│   ├── qzdb_searcher.h
│   └── main.c            # 调用示例
├── csharp/               # C# (.NET)
│   ├── QzdbSearcher.cs
│   ├── Program.cs        # 调用示例
│   └── qzdb-searcher.csproj
├── go/
│   ├── qzdb/
│   │   └── qzdb.go       # Go package
│   ├── go.mod
│   └── main.go           # 调用示例
├── java/
│   └── src/main/java/
│       ├── qzdb/
│       │   ├── QzdbSearcher.java
│       │   └── IpLocation.java
│       └── Main.java     # 调用示例
├── nodejs/               # Node.js
│   ├── qzdb.js
│   ├── test.js           # 调用示例
│   └── package.json
├── php/
│   ├── QzdbSearcher.php
│   └── test.php          # 调用示例
├── python/
│   ├── __init__.py
│   ├── qzdb.py           # 参考实现
│   └── test.py           # 调用示例
├── rust/
│   ├── Cargo.toml
│   └── src/
│       ├── lib.rs
│       └── main.rs       # 调用示例
├── run_all_tests.sh       # 一键测试脚本
├── FORMAT.md              # QZDB 二进制格式说明
└── README.md
```

## 快速开始

### 1. 获取数据库

从 [qqzeng.com](https://www.qqzeng.com) 获取 IP 数据库，将 `.qzdb` 文件放入工作目录。

### 2. 选择语言

### Python

```python
from qzdb import QzdbSearcher
s = QzdbSearcher("qqzeng_ip_std_china.qzdb")
info = s.find("1.2.3.4")
print(info["country"], info["province"])
print(s.find_str("1.2.3.4"))  # "中国|北京|北京|..."
```

### Node.js

```js
const QzdbSearcher = require('./qzdb');
const s = QzdbSearcher.getInstance('qqzeng_ip_std_china.qzdb');
const info = s.find('1.2.3.4');
console.log(info.country, info.province);
console.log(s.findStr('1.2.3.4'));
```

### PHP

```php
use Qqzeng\Ip\QzdbSearcher;
$s = QzdbSearcher::getInstance('qqzeng_ip_std_china.qzdb');
$info = $s->find('1.2.3.4');
echo $info['country'] . ' ' . $info['province'] . "\n";
echo $s->findStr('1.2.3.4') . "\n";
```

### Go

```go
import "qzdb_searcher/qzdb"
s, _ := qzdb.Instance("qqzeng_ip_std_china.qzdb")
info := s.Find("1.2.3.4")
fmt.Println(info.Country, info.Province)
fmt.Println(s.FindStr("1.2.3.4"))
```

### Rust

```rust
use qzdb_searcher::{from_file, QzdbSearcher};
let s = from_file("qqzeng_ip_std_china.qzdb");
if let Some(info) = s.find("1.2.3.4") {
    println!("{} {}", info.get("country"), info.get("province"));
}
println!("{}", s.find_str("1.2.3.4"));
```

### C

```c
qzdb_searcher_t ctx;
qzdb_init(&ctx, "qqzeng_ip_std_china.qzdb");
qzdb_geo_info_t info;
qzdb_find(&ctx, "1.2.3.4", &info);
printf("%s %s\n", info.country, info.province);
char buf[256];
qzdb_find_str(&ctx, "1.2.3.4", buf, sizeof(buf));
printf("%s\n", buf);
qzdb_free(&ctx);
```

### Java

```java
QzdbSearcher s = QzdbSearcher.getInstance();
s.load("qqzeng_ip_std_china.qzdb");
IpLocation info = s.find("1.2.3.4");
if (info != null) {
    String[] values = info.getValues();
}
System.out.println(s.findStr("1.2.3.4"));
```

### C#

```csharp
var s = Qqzeng.QzdbSearcher.GetInstance("qqzeng_ip_std_china.qzdb");
var info = s.Find("1.2.3.4");
Console.WriteLine(info.Get("country") + " " + info.Get("province"));
Console.WriteLine(s.FindStr("1.2.3.4"));
```

## API 参考

### find(ip) / Find(ip)

- **输入**: IPv4 或 IPv6 字符串
- **返回**: 包含字段的对象/结构体，字段名由数据库 Metadata 动态决定（如 `continent`, `country`, `province`, `city`, `district`, `isp`, `longitude`, `latitude` 等）
- 未找到时返回 `null`/`None`/空

### find_str(ip) / FindStr(ip)

- **返回**: 竖线分隔字符串，字段顺序与 `getFieldNames()` 一致
- 未找到时返回空字符串

### find_uint(ip_int) / FindUint(ipInt)

- **输入**: `uint32` IPv4 整数
- **返回**: 同 `find()`

> 所有查询 API 加载后无状态、线程安全，可并发调用（加载/替换数据库时需外部同步）。

## 基准测试

3M 随机 IPv4 + 1M 随机 IPv6 查询，覆盖三种数据库（std_china ~3MB, max_china ~6MB, max_global ~67MB）。
测试环境：**Apple M4 Pro (12 核)**, 单线程, 随机种子 123(V4)/456(V6)。
C/Go/Rust 使用 mmap 内存映射；Java/C# 使用堆分配预加载字节数组。
所有数字为**本轮实测**（2026-06-29），SDK 缺陷修复后重新采集。

| 语言 | 编译器/运行时 | API 类型 |
|------|-------------|---------|
| C | Apple Clang 17, -O3 | `qzdb_find_uint` (V4) / `qzdb_find_v6` (V6) |
| Go | go 1.24, `-gcflags="-B"` | `FindUint` (V4) / `Find` (V6) |
| Java | OpenJDK 21.0.4, -O3 | `findUInt` (V4) / `find` (V6) |
| Rust | rustc 1.87, `--release` (LTO) | `find_uint` (V4) / `find` (V6) |
| C# | .NET 9.0.100, `-c Release` | `FindUInt` (V4) / `Find` (V6) |
| Node.js | Node 25.4.0, V8 | `find_uint` (V4) / `find` (V6) |
| PHP | PHP 8.4, OPcache | `findUInt` (V4) / `find` (V6) |
| Python | CPython 3.13 | `find_uint` (V4) / `find` (V6) |

> 测试代码位于各语言目录下的 `bench_qps` / `bench_qps.rs` / `Main.java` / `Program.cs` 等文件。

### std_china（标准库，中国区，~3MB）

| 语言 | V4 QPS | V6 QPS | 与 C 的比值 |
|------|--------|--------|------------|
| C (mmap) | 206,597,342 | 78,573,112 | 1.00x |
| Go | 95,796,400 | 35,140,327 | 0.46x |
| Java 21 | 96,149,738 | 24,776,163 | 0.47x |
| Rust | 69,414,583 | 49,856,143 | 0.34x |
| C# (.NET) | 85,411,000 | 10,400,000 | 0.41x |
| Node.js 25 | 47,338,709 | 3,831,417 | 0.23x |
| PHP 8 | 4,004,886 | 1,405,906 | 0.019x |
| Python 3 | 2,522,632 | 430,717 | 0.012x |

### max_china（专业库，中国区，25字段，~6MB）

| 语言 | V4 QPS | V6 QPS |
|------|--------|--------|
| C | 137,299,771 | 77,035,668 |
| Java 21 | 59,000,531 | 27,985,596 |
| C# (.NET) | 53,400,000 | 9,850,000 |
| Go | 54,231,487 | 31,708,368 |
| Rust | 19,021,447 | 48,208,551 |
| Node.js | 12,233,708 | 3,236,245 |
| PHP | 2,402,080 | 1,374,058 |
| Python | 1,598,292 | 413,832 |

### max_global（专业库，全球版，25字段，~67MB）

| 语言 | V4 QPS | V6 QPS |
|------|--------|--------|
| C | 19,862,946 | 47,860,630 |
| Java 21 | 7,438,067 | 22,854,379 |
| C# (.NET) | 8,600,000 | 25,800,000 |
| Go | 6,136,692 | 11,545,250 |
| Rust | 2,001,373 | 12,466,360 |
| Node.js | 1,357,025 | 2,444,987 |
| PHP | 434,262 | 1,166,756 |
| Python | 284,510 | 347,535 |

### 关键发现

- **C 全面领先**：V4 最高 2.07 亿 QPS（std_china），mmap + 扁平 Trie 顺序布局 + 手写解析
- **Rust V6 极强**：所有数据库 V6 仅次于 C，std_china 上 4986 万 QPS（接近 C 的 7857 万）
- **C# V4 在 max_global 领先**：827 万 QPS 反超 Java（744 万）和 Go（614 万）
- **Java V4 在小型数据库强劲**：std_china V4 9615 万，仅次 C；max_global 受字符串池影响大
- **Go 整体均衡**：std_china V4 9579 万与 Java 相当，max_global V6 1154 万优于 Node.js
- **Node.js V6 滑落明显**：除 PHP/Python 外最慢，BigInt 运算开销大（383 万 QPS）
- **PHP 和 Python 垫底**：Python V6 仅 43 万 QPS，解释型 + GIL 瓶颈
- **max_global V4 整体大幅下降**：25 字段 × 67MB 数据量，string pool 解析成为瓶颈

## 交叉验证

### 6 数据库 × 8 语言 × 全量随机采样（2026-06-29）

对每个数据库生成 20~68 万随机 IP（seed=42），所有 8 种语言独立查询，Python 为参考基准，逐条对比 pipe 输出。

| 数据库 | V4 查询 | V6 查询 | C | Go | Rust | Node.js | Python | PHP | Java | C# |
|--------|---------|---------|---|---|---|---|---|---|---|---|
| **std_china** | 325,463 | 188,041 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **max_china** | 532,706 | 108,244 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **std_global** | 306,511 | 374,197 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **ult_china** | 209,066 | 37,612 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **ult_global** | 196,019 | 187,025 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **max_global** | 208,445 | 191,118 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **合计** | **1,778,210** | **1,086,237** | | | | | | | | |

**总计 2,864,447 次 IP 查询 × 8 语言 → 零差异 ✅**

### 交叉验证期间修复的 SDK Bug

| Bug | SDK | 根因 | 影响 |
|-----|-----|------|------|
| V6 二分查找短路 | PHP | `$cmp < 0` 应为 `$cmp <= 0` | 约 51% V6 查询返回 null |
| `to_pipe()` 浮点格式化失效 | Python | `GeoInfo._float_indices` 存整数索引但用字符串匹配 | 所有 max 系 DB 经纬度不格式化 |
| `parseFloat('')` 输出 NaN | Node.js | 空值字段被格式化为 `"NaN"` | max DB 3 条 V6 记录差异 |
| IP=0 被提前返回 | C | `if (ip_int == 0) return -1` | IP 0.0.0.0(Cloudflare) 查不到 |

格式说明见 [FORMAT.md](FORMAT.md)

## 测试

一键运行所有语言测试（烟雾测试 + 交叉验证 + 基准测试）：

```bash
./run_all_tests.sh
```

> 测试前需将 `.qzdb` 数据库文件放入工作目录。如果文件缺失，对应语言的测试会因找不到数据库而退出非零（这是预期行为，非 SDK 缺陷）。

输出示例：
```
Testing: Java
V6: ✓ 中国九龙香港 (NTT)
✓ 验证 15/15
V4 verify: 994/994 ✓, V6 verify: 3888/3888 ✓
QPS: 80288862
V6 QPS: 26900281
TEST_PASS
```

## License

MIT
