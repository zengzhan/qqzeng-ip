# QZDB Go SDK（qqzeng-ip）

> 纯离线、零第三方依赖的 **QZDB IP 地理定位数据库**官方 Go 语言 SDK（支持 IPv4 / IPv6 双栈）。

- **模块名**：`github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go`；包名 `qzdb`
- **定位**：离线解析 `.qzdb` 二进制数据库文件，不依赖任何外部网络请求
- **特性**：无锁快照（`atomic.Pointer[Snapshot]`）+ per-snapshot 有界 `GeoInfo` 解码缓存，并发查询互不阻塞
- **依赖**：Go 标准库（`go >= 1.21`）+ `golang.org/x/sys`（跨平台只读 `mmap`：Unix 使用 `x/sys/unix`，Windows 使用 `x/sys/windows` 的 `CreateFileMapping`）
- **许可**：MIT
- **跨语言规范**：以仓库根 [`API_CONTRACT.md`](../../API_CONTRACT.md) 为唯一事实来源（SSOT）

---

## 目录

1. [环境要求](#1-环境要求)
2. [安装与集成](#2-安装与集成)
3. [快速开始](#3-快速开始)
4. [加载数据库](#4-加载数据库)
5. [查询 API](#5-查询-api)
6. [结果实体 `GeoInfo`](#6-结果实体-geoinfo)
7. [字段投影 `FindFields`](#7-字段投影-findfields)
8. [行号 / ID 反查与 CIDR](#8-行号--id-反查与-cidr)
9. [批量与流式查询](#9-批量与流式查询)
10. [多库联合查询 `ChainedReader`](#10-多库联合查询-chainedreader)
11. [命名注册表 `QzdbRegistry`](#11-命名注册表-qzdbregistry)
12. [热更新与生命周期](#12-热更新与生命周期)
13. [错误处理](#13-错误处理)
14. [性能说明](#14-性能说明)
15. [维护与升级](#15-维护与升级)

---

## 1. 环境要求

| 项 | 要求 |
|----|------|
| Go | 1.21 及以上 |
| 操作系统 | Windows / Linux / macOS 均可 |
| 数据库文件 | `.qzdb` 格式（由官方数据构建工具生成，含所需分组的二进制数据） |

---

## 2. 安装

`go.mod` 中引用（模块名统一为 `qzdb_reader`）：

```go
require qzdb_reader v0.0.0
```

若使用本地路径或 `replace`：

```go
require qzdb_reader v0.0.0

replace qzdb_reader => ../path/to/multi-lang/go
```

代码中导入：

```go
import "github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"
```

---

## 3. 快速开始

```go
package main

import (
	"fmt"

	"github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"
)

func main() {
	// 通过文件路径加载（默认：校验 CRC、加载第 0 个分组）
	reader, err := qzdb.NewBuilder("qqzeng_ip_std_china.qzdb").Build()
	if err != nil {
		panic(err) // Fail-Closed：文件不存在 / Magic 错 / Header 版本错 / CRC 不匹配 / 截断 均在此拒绝
	}
	defer reader.Close()

	// 单次查询：未命中或 IP 非法时返回 (nil, nil)（不抛异常）
	info, _ := reader.Find("114.114.114.114")
	if info != nil {
		fmt.Println(info.ToPipe())                                  // 管道符分隔：亚洲|CN|中国|江苏|南京|中国电信
		fmt.Println(info.ToJson())                                  // 紧凑 JSON 字符串
		fmt.Printf("%s / %s / %s\n", info.GetCountry(), info.GetProvince(), info.GetCity())
		fmt.Printf("ISP=%s, ASN=%v\n", info.GetIsp(), info.GetAsn())
	}

	// 直接取管道字符串（未命中/非法返回 ""）
	fmt.Println(reader.FindStr("223.5.5.5")) // 亚洲|CN|中国|浙江|杭州|阿里云
}
```

---

## 4. 加载数据库

使用 `Builder` 链式构造：

```go
// 文件加载（推荐）
reader, err := qzdb.NewBuilder("qqzeng_ip_std_china.qzdb").
	GroupIndex(0).   // 版本组索引：0=主组；ASN 组通常为 2
	VerifyCRC(true). // 默认开启；关闭仅用于受信数据 / 基准测试
	Build()

// 内存字节加载（内部拷贝，调用方可自由释放原数组）
data, _ := os.ReadFile("qqzeng_ip_std_china.qzdb")
reader, err := qzdb.NewBuilderBytes(data).Build()

// 输入流加载（读取全部字节）
f, _ := os.Open("qqzeng_ip_std_china.qzdb")
reader, err := qzdb.NewBuilderReader(f).Build()
```

构造期 **Fail-Closed**：Magic≠`QZDB`、HeaderVersion≠1、CRC 不匹配（且 `VerifyCRC(true)`）、文件截断，都会直接返回错误，绝不部分加载或静默降级。

> **无单例设计**：v2.4 起 Go SDK 不再提供 `NewSearcher` 或 `qzdb.Instance(path)` 进程级单例，统一入口为 `qzdb.Open(path, groupIndex, verifyCrc)`（返回 `*QzdbReader`）。跨文件/跨版本复用请持有各自的 `*QzdbReader` 实例（或用 `QzdbRegistry` 便利层），多实例并发互不干扰。

---

## 5. 查询 API 全表

| 方法 | 签名 | 未命中 | 非法 IP |
|------|------|--------|---------|
| `Find` | `Find(ip string) (*GeoInfo, error)` | `(nil, nil)` | `(nil, nil)` |
| `FindUint` | `FindUint(ip uint32) (*GeoInfo, error)` | `(nil, nil)` | — |
| `FindV6Uint` | `FindV6Uint(ip [16]byte) (*GeoInfo, error)` | `(nil, nil)` | — |
| `FindBytes` | `FindBytes(ip [16]byte) (*GeoInfo, error)` | `(nil, nil)` | — |
| `FindFields` | `FindFields(ip string, fields []string) (*GeoInfo, error)` | `(nil, nil)` | `(nil, nil)` |
| `FindStr` | `FindStr(ip string) string` | `""` | `""` |
| `LookupRowId` | `LookupRowId(ip string) uint32` | `0` | `0` |
| `LookupRowIdUint` | `LookupRowIdUint(ip uint32) uint32` | `0` | — |
| `LookupRowIdV6` | `LookupRowIdV6(ip [16]byte) uint32` | `0` | — |
| `LookupRowIdBytes` | `LookupRowIdBytes(ip []byte) uint32` | `0` | `0` |
| `LookupIds` | `LookupIds(rowID uint32) *RowIds` | `nil` | — |
| `LookupCidr` | `LookupCidr(ip string) string` | `""` | `""` |
| `LookupCidrUint` | `LookupCidrUint(ip uint32) string` | `""` | — |
| `LookupCidrBytes` | `LookupCidrBytes(ip []byte) string` | `""` | `""` |

> **契约约定（§4）**：Go 的 `Find` 等单条查询对「未命中」与「非法 IP」统一返回 `(nil, nil)`；golden 校验将该空值统一映射为 `""`，因此返回空值即通过。

**IPv4-Mapped IPv6 自动降级**：`::ffff:114.114.114.114`（及 `::ffff:7272:7272` 十六进制形态）会自动剥离前缀走 V4 Trie，结果与 `114.114.114.114` **字段级完全一致**。

```go
a, _ := reader.Find("114.114.114.114")
b, _ := reader.Find("::ffff:114.114.114.114")
// a.ToPipe() == b.ToPipe()
```

**字段投影**（`FindFields` 只解析指定字段，减少池读取）：

```go
g, _ := reader.FindFields("114.114.114.114", []string{"country", "isp"})
fmt.Println(g.Get("isp")) // 中国电信
```

---

## 6. 结果对象 `GeoInfo`

`GeoInfo` 字段只读、可安全跨 goroutine 共享。

### 字段访问（大小写 / 下划线 / 连字符不敏感）

```go
info.Get("country_code")      // "CN"
info.Get("countryCode")       // "CN"（等价）
info.Get("COUNTRY_CODE")       // "CN"（等价）
info.Get("Country-Code")      // "CN"（等价）
info.Get("missing")           // ""（绝不 panic / 越界）
```

### 序列化

```go
info.ToPipe()   // 字段以 | 拼接；直接拼接已解码字符串，禁止重新格式化
info.String()   // 等价于 ToPipe()
info.ToMap()    // map[string]string（全 string）
info.ToJson()   // 手写 JSON：longitude/latitude/asn/geo_id 输出为数字（无法解析为 null），其余为字符串
```

### 原生浮点格式（§8.2）

`longitude`/`latitude` 等原生浮点字段严格按 **6 位小数** 格式化：

```go
// 116.0     -> "116"      （整数值：无小数点）
// 116.4     -> "116.400000"
// NaN / Inf -> ""
```

> `ToPipe()` 直接拼接已解码字符串，不会对 `116.400000` 重新解析回 `116.4`。

### 语义化 Getter 全集

| Getter | 返回 | 缺失 |
|--------|------|------|
| `GetCountry()` / `GetCountryEn()` | `string` | `""` |
| `GetProvince()` / `GetProvinceEn()` | `string` | `""` |
| `GetCity()` / `GetCityEn()` | `string` | `""` |
| `GetDistrict()` | `string` | `""` |
| `GetGeoId() *int64` | `int64` | `nil` |
| `GetLongitude() / GetLatitude() *float64` | `float64` | `nil` |
| `GetTimezone()` / `GetIsp()` / `GetIspEn()` | `string` | `""` |
| `GetAsn() *int64` | `int64` | `nil` |
| `GetAsName()` / `GetAsDomain()` | `string` | `""` |
| `GetUsageType() UsageType` | 21 语义 + 未知兜底 | `Unknown` |
| `GetCountryAlpha2()` / `GetCountryAlpha3()` | `string` | `""` |
| `GetCurrencyCode()` / `GetCurrencyName()` | `string` | `""` |
| `GetPhonePrefix()` / `GetEmojiFlag()` / `GetLanguages()` | `string` | `""` |
| `GetCidr()` | `string` | `""`（恒返回 `""`；真实网段请用 `reader.LookupCidr(ip)`） |

`UsageType` 提供 21 个官方场景（`AICrawler`/`Backbone`/`Broadband`/`Business`/`CDN`/`Cloud`/`DNS`/`DataCenter`/`Education`/`Finance`/`Government`/`ISP`/`IXP`/`IoT`/`Mobile`/`Reserved`/`Satellite`/`Spider`/`Streaming`/`Unknown`/`VPN`），未知场景安全兜底（不崩溃）：

```go
ut := info.GetUsageType()
fmt.Println(ut.RawValue(), ut.DisplayZh(), ut.IsKnown()) // "DNS" "DNS" true
u := qzdb.ParseUsageType("MyCustom")                     // 未知 -> RawValue="MyCustom", IsKnown()=false
```

---

## 7. CIDR 反查

数据库本身不存 CIDR，由 Trie 叶子深度重建网络地址（叶子深度 = 前缀长度 N；网络地址 = IP 高 N 位清零；V6 按 RFC 5952 压缩）。

```go
fmt.Println(reader.LookupCidr("114.114.114.114")) // 形如 114.114.0.0/16
fmt.Println(reader.LookupCidr("2408:8000:9000::1")) // 形如 2408:8000::/32
reader.LookupCidrUint(0x01020304)                  // IPv4 uint32 入口
reader.LookupCidrBytes(ip16[:])                    // 4/16 字节入口（IPv4-mapped 自动降级）
// 未覆盖 / 非法 IP 返回 ""
```

---

## 8. 批量与流式查询

顺序执行、逐条保留三态语义，内部不起线程池：

```go
ips := []string{"114.114.114.114", "223.5.5.5", "8.8.8.8"}

// 批量
results := reader.FindBatch(ips)
for _, r := range results {
	if r.GeoInfo != nil {
		fmt.Printf("%s => %s\n", r.IP, r.GeoInfo.ToPipe())
	}
}

// 字段投影批量
proj := reader.FindBatchFields(ips, []string{"country", "isp"})

// 流式（内存恒定，不累积结果）
stream := reader.FindStream(ips)
for {
	res, ok := stream.Next()
	if !ok {
		break
	}
	if res.GeoInfo != nil {
		fmt.Println(res.IP, res.GeoInfo.ToPipe())
	}
}
```

`BatchResult` 结构：`IP string`、`GeoInfo *GeoInfo`、`Error error`。

---

## 9. 链式多库查询 `ChainedReader`

按添加顺序合并多个 reader，返回首个命中：

```go
chain := qzdb.NewChainedReader(readerStd, readerUlt)
chain.Add(readerAsn)
info, _ := chain.Find("114.114.114.114") // 第一个非空的 GeoInfo
fmt.Println(chain.FindStr("1.2.3.4"))
```

---

## 10. 命名注册表 `QzdbRegistry`

管理多个命名 reader，查询时按注册顺序返回首个命中：

```go
reg := qzdb.NewQzdbRegistry()
reg.Register("std", readerStd)
reg.Register("ult", readerUlt)
info, _ := reg.Find("2408:8000:9000::1")
fmt.Println(reg.FindStr("114.114.114.114"))
fmt.Println(reg.Names()) // ["std", "ult"]
```

---

## 11. 元信息自省

| 方法 | 返回 |
|------|------|
| `GetVersion()` / `Version()` | Metadata 版本（无则 `""`） |
| `GetDataMonth()` | 数据期号 `"yyyy-MM"`（由 Header BuildDate 推算） |
| `GetEdition()` | 版本档次 `std`/`pro`/`asn`/`max`/`ult` |
| `GetScope()` | `""`（当前格式 Header 尚无 scope 字段） |
| `GetBuildTime()` | 构建日期 `"yyyy-MM-dd"` |
| `GetDescription()` | Metadata 描述（无则 `""`） |
| `GetFileHash()` | 文件 CRC32 十六进制字符串（**8 位小写**） |
| `GetFieldNames()` / `FieldNames()` | 当前版本组字段名 |
| `HasField(name)` | 是否含该字段（大小写/下划线不敏感） |
| `VerifyCRC()` | 重新计算全文件 CRC32 并与存储值比对 |
| `GetGroupCount()` | 版本组数量（1~4） |
| `GetPoolCount()` / `PoolCount()` | Header poolCount |

```go
fmt.Println(reader.GetVersion())    // "std" / "ult"
fmt.Println(reader.GetFileHash())   // 例如 "1a2b3c4d"
fmt.Println(reader.HasField("province")) // true（字段因版本档而异；std 含 continent/country_code/country/province/city/isp）
fmt.Println(reader.GetFieldNames()) // [continent country_code country province city isp]
```

---

## 12. 热更新与生命周期

```go
reader, _ := qzdb.NewBuilder("a.qzdb").Build()

// 原子热替换（强制 CRC；失败保留旧快照，旧数据继续服务）
if err := reader.Reload("b.qzdb"); err != nil {
	log.Printf("reload 失败，沿用旧数据: %v", err)
}
reader.ReloadBuffer(newBytes) // 从内存字节热替换

// 资源释放（幂等；关闭后查询安全失败，不 UAF / 不 double-free）
reader.Close()
```

`Close()` 之后调用 `Find` 等方法会返回 `qzdb.ErrClosed`，不会崩溃。

---

## 13. 错误处理

- **构造期 Fail-Closed**：文件不存在、Magic≠`QZDB`、HeaderVersion≠1、CRC 不匹配、截断 —— 全部拒绝初始化并返回 `*QzdbError`（含 `ErrorCode`：`BAD_MAGIC`/`BAD_HEADER`/`CORRUPTED`/`UNSUPPORTED`/`INVALID_PARAM` 等）。
- **查询期**：Go 的 `Find` 等单条查询对「未命中」与「非法 IP」返回 `(nil, nil)`（契约 §4）。Trie 损坏等真正异常返回 `error`。
- **`GeoInfo.Get` / `GetXxx`**：任一缺失均返回 `""` 或 `nil`，绝不 panic / 越界。

```go
import "github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"

if errors.Is(err, qzdb.ErrClosed) { /* 已关闭 */ }
if qe, ok := err.(*qzdb.QzdbError); ok {
	fmt.Println(qe.Code(), qe.Error()) // 例如 CORRUPTED
}
```

---

## 14. 性能说明

- **无锁快照 + 原子替换**：查询路径通过 `atomic.Pointer[Snapshot]` 读取当前快照，对快照只读，多 goroutine 并发查询互不阻塞；`Reload` 构建完整新快照后原子替换，期间旧快照继续服务。
- **per-snapshot 有界无锁缓存**：以 `row_id` 为键、开放寻址的 `GeoInfo` 缓存（默认 2^18 槽，有界内存）。缓存命中趋近 **零分配**；碰撞只重算、**绝不返回错值**。
- **归一化索引加载期构建一次**：字段名 → 索引的归一化映射在加载期完成，查询期仅 O(1) 哈希。
- **零拷贝解析**：优先 `mmap` 只读映射；原生浮点字段在解码时即格式化为 6 位小数字符串，`ToPipe()` 直接拼接，避免查询期重复解析与分配。

> 基准示例见 `cmd/bench`；典型单线程查询可达百万级 QPS，16 线程并发无锁线性扩展、race-free。

---

## 15. 维护与升级

- **升级数据库**：调用 `Reload` / `ReloadBuffer` 热替换即可，无需重启进程；新快照强制 CRC 校验。
- **多版本组**：通过 `Builder.GroupIndex(n)` 选择不同版本组（0=主组，2=ASN 组等）；字段名标签与维度掩码按组独立解析。
- **数据来源**：`.qzdb` 文件由官方构建工具生成；本 SDK 不负责数据采集。
- **兼容性**：API 严格对齐 `API_CONTRACT.md` v2.4，与 Java / C# 参考实现逐字一致（含 golden 校验 0 偏差）。

---

## 测试与验证

```bash
cd go
go test ./...                 # 全量：Tier1 + Tier2(golden) + Tier3(并发/性能) + chain_merge + README API
go test -run TestCSVOracle ./qzdb/...   # Tier0 独立真值校验（对源 CSV）
go vet ./...                 # 静态检查
```

- **Tier0（CSV 真值）**：`csv_oracle_test.go` 以 `.qzdb` 的源数据 `test_data_202608/{std,ult}/china/*_range.csv`（带 `start_ip_num/end_ip_num` + 地理字段）为独立裁判，全局随机 + 区间内随机共约 18000 样本比对 `country/province/city/isp`，**0 失配**。注意：`TestGoldenTier2` 的向量由被测代码自身生成，只证确定性 / 跨语言一致；本测试证明"返回正确答案"。源 CSV 缺失时优雅跳过。
- **Tier1**：严格 IP 解析（含 SSRF 防护）、Mapped 降级、字段归一化、UsageType 21 + 未知兜底、损坏文件 Fail-Closed、CRC 强制、无锁 Reload、CIDR 反查、资源释放、Find 语义、浮点 6 位格式、字段投影。
- **Tier2**：加载 `qqzeng_ip_std_china.qzdb` 与 `qqzeng_ip_ult_china.qzdb`，对 `golden_vectors.json` 断言 `Find(ip).ToPipe() == expected`，**必须 0 失败**。
- **Tier3**：`TestTier3ConcurrentSafety`（多 goroutine 查询 + 热更新无撕裂读）、`TestTier3DualStackPerformance`（双栈吞吐基准）。

---

## 项目结构

```
go/
├── go.mod            # module qzdb_reader
├── qzdb/
│   ├── qzdb.go        # Snapshot / QzdbReader / Trie / 解析 / 加载 / 热更新 / 元信息
│   ├── geoinfo.go     # GeoInfo / 归一化 / 无锁缓存 / 字段投影
│   ├── geo_getters.go # 语义化 Getter / ToJson
│   ├── usagetype.go   # UsageType（21 场景 + 未知兜底）
│   ├── cidr.go        # CIDR 反查（前缀长度重建 + RFC 5952）
│   ├── batch.go       # 批量 / 流式
│   ├── registry.go    # QzdbRegistry / ChainedReader
│   ├── builder.go     # Builder 构造器
│   ├── ip.go          # 严格 IP 解析 / IPv4-mapped 降级
│   ├── errors.go      # ErrorCode / QzdbError
│   ├── *_test.go      # Tier1 单测 + Tier2 黄金校验 + Tier0 CSV 真值 + Tier3 并发/性能
└── cmd/               # demo / batch / bench / dump / regress 等示例
```

<!-- commit: go: ⚡ Go 语言极速解析引擎 (跨平台 mmap 零拷贝, 无锁并发, 极致低延迟) sync=1787391447 -->
