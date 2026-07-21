# QZDB 二进制格式规范

> **版本**: qzdb (QZDB) · 取代所有旧版格式文档
> **本文件是 QZDB 二进制格式的唯一权威规范，精确对应实际 C# 实现。**
>
> **⚠️ 文档健康度（2026-07 复核）**：字段计数段落自相矛盾，以 C# 源码 `QZDBBuilder.VersionFieldNames` 与 [product-specification.md](./reference/product-specification.md) 为准：
> - **字段计数已统一**：§8.3 与 §12.2 均使用权威值 **std=6 / pro=11 / ult=15 / asn=8 / max=25**（pro 为新增专业版，字段见 product-spec）；以 C# 源码 `QZDBBuilder.VersionFieldNames` 与 [product-specification.md](./reference/product-specification.md) §3 为准。
> - GeoResolver 相关章节若提及 GeoCorrector / GeoHelperNew / GeoAddressParserBest / StringSimilarityHelper，均已被 `src/QQzeng.MergeEngine/GeoMatching/` 完全取代并已删除源码，详见 geo-resolver-comparison.md 的迁移记录。

---

## 目录

- [1. 概述](#1-概述)
- [2. 文件整体布局](#2-文件整体布局)
- [3. Header（192 字节）](#3-header192-字节)
- [4. V4 Jump Table（跳表）](#4-v4-jump-table跳表)
- [5. Trie Nodes](#5-trie-nodes)
- [6. V6 Jump Table](#6-v6-jump-table)
- [7. IPRow Array（★ qzdb 新增）](#7-iprow-array-qzdb-新增)
- [8. GeoEntry Section（★ qzdb 多组架构）](#8-geoentry-section-qzdb-多组架构)
- [9. String Pools](#9-string-pools)
- [10. Metadata](#10-metadata)
- [11. 查询算法](#11-查询算法)
- [12. SDK 实现要点](#12-sdk-实现要点)
- [13. P1 增强：原生标量 / 列投影 / 惰性池 / IPv4 映射 / 增量更新](#13-p1-增强原生标量--列投影--惰性池--ipv4-映射--增量更新)
- [附录 1：格式版本演化](#附录1格式版本演化)
- [附录 2：产品矩阵与字段定义](#附录2产品矩阵与字段定义)

---

## 1. 概述

### 1.1 qzdb 架构

qzdb 在 旧版（PATRICIA Trie）基础上新增 **IPRow 间接层**，实现单文件多版本共存：

```
旧版:  Trie → geo_id → GeoEntry[geo_id] → Pool → string[]
qzdb:  Trie → row_id → IPRow[row_id] → {geo_id, asn_id}
                                       ├─→ GeoEntry_STD[geo_id] → Pool_STD → string[]
                                       ├─→ GeoEntry_ULT[geo_id] → Pool_ULT → string[]
                                       ├─→ GeoEntry_ASN[asn_id] → Pool_ASN → string[]
                                       └─→ GeoEntry_MAX[geo_id] → Pool_MAX → string[]
```

### 1.2 关键设计原则

| 原则 | 说明 |
|------|------|
| 自描述 | Header 包含所有解析所需信息，无外部依赖 |
| 64 字节对齐 | 所有段起始于 64 字节边界（`Align64`），支持 mmap 直接访问 |
| Little-Endian | 所有多字节值为 LE（V6 范围用 BE 的特例已在 qzdb 中移除） |
| CRC32 验证 | 整个文件范围 CRC32（计算时 CRC 自身 4 字节填零） |
| 元数据驱动 | Metadata 区存储字段名列表、版本名等，SDK 优先从此读取 |

### 1.3 字节序

**所有多字节值均为 Little-Endian，除非特别注明。**

---

## 2. 文件整体布局

```
┌──────────────────────────────────────┐
│  Header (192 字节, 固定)             │
├──────────────────────────────────────┤  ← Align64
│  V4 Jump Table (256KB = 65536×4B)    │  无 V4 时跳过（offset=0）
│  V4 Trie Nodes (N4×8B)               │  无 V4 时跳过
├──────────────────────────────────────┤  ← Align64
│  V6 Jump Table (2^J6×4B)            │  J6=12~20, 多为 16；无 V6 时跳过
│  V6 Trie Nodes (N6×8B)              │  无 V6 时跳过
├──────────────────────────────────────┤  ← Align64
│  IPRow Array (RowCount×6B)           │  ★ qzdb 新增间接层
├──────────────────────────────────────┤  ← Align64
│  GeoEntry Section                    │
│  ├─ GroupMetadataTable               │  版本组元信息
│  ├─ GeoEntry_Group[0] (主版本)       │
│  ├─ GeoEntry_Group[1] (可选)         │
│  ├─ GeoEntry_Group[2] (可选)         │
│  └─ GeoEntry_Group[3] (可选)         │
├──────────────────────────────────────┤  ← Align64
│  String Pools (按版本组顺序)          │  N 组 × (每版本 D 个 Pool)
├──────────────────────────────────────┤  ← Align64
│  Metadata (TLV 条目)                 │  版本名/字段列表/描述
└──────────────────────────────────────┘
```

**段顺序恒定**：Header → V4 Jump → V4 Nodes → V6 Jump → V6 Nodes → IPRow → GeoEntry → Pools → Metadata。
可选段（V4/V6）在 Flags 中标记，无数据时对应 offset = 0。

---

## 3. Header（192 字节）

Magic `QZDB`（4 字节 ASCII），固定 192 字节。

| 偏移 | 大小 | 类型 | 字段名 | 说明 |
|------|------|------|--------|------|
| 0 | 4 | ASCII | **Magic** | `QZDB` |
| 4 | 1 | uint8 | **HeaderVersion** | **最新统一写入值固定为 `1`**。统一了所有的老旧格式，去除了冗余的历史兼容分支。 |
| 5 | 1 | uint8 | Reserved | 填 0 |
| 6 | 2 | uint16 LE | **VersionMask** | 文件中包含的版本位掩码 |
| 8 | 2 | uint16 LE | **Flags** | 功能标志位 |
| 10 | 1 | uint8 | **V4JumpBits** | V4 跳表位宽，固定 `16` |
| 11 | 1 | uint8 | **V6JumpBits** | V6 跳表位宽，动态估算选择 `16 ~ 20`（最低 16 位以保证高频 GUA 查询跳过 16 层以上二叉树检索，极速寻址） |
| 12 | 1 | uint8 | **PoolCount** | 主版本组（group 0）的维度数（=字段数） |
| 13 | 1 | uint8 | **PoolIdxSize** | 池索引字节宽度：`2`(≤65535) 或 `3` |
| 14 | 2 | uint16 LE | **GeoCount** | 主版本组 GeoEntry 条数（**兜底字段**：仅当无 GroupMetadataTable 时使用；v4+ 权威计数见 §8.2，为 uint32） |
| 16 | 4 | uint32 LE | CRC32 | 整个文件 CRC32（计算时这 4 字节填 0） |
| 20 | 4 | uint32 LE | **RowCount** | IPRow 总条数（含 #0 空行） |
| 24 | 4 | uint32 LE | V4RecordCount | V4 CIDR 条数（=0 则无 V4） |
| 28 | 4 | uint32 LE | V6RecordCount | V6 CIDR 条数（=0 则无 V6） |
| 32 | 4 | uint32 LE | BuildDate | 编译日期，格式 `yyyyMMdd` |
| 36 | 4 | uint32 LE | HeaderSize | 固定 `192` |
| 40 | 8 | uint64 LE | **OffsetRowSchema** | ROW_SCHEMA 段偏移（v5+，v4 文件中为 0） |
| 48 | 8 | uint64 LE | **OffsetGroupSchema** | GROUP_SCHEMA 段偏移（v5+，v4 文件中为 0） |
| 56 | 8 | bytes | Reserved | 填 0 |
| 64 | 8 | uint64 LE | **OffsetV4Jump** | V4 Jump Table 偏移（0=无V4） |
| 72 | 8 | uint64 LE | **OffsetV4Nodes** | V4 Trie Nodes 偏移（0=无V4） |
| 80 | 8 | uint64 LE | **OffsetV6Jump** | V6 Jump Table 偏移（0=无V6） |
| 88 | 8 | uint64 LE | **OffsetV6Nodes** | V6 Trie Nodes 偏移（0=无V6） |
| 96 | 8 | uint64 LE | **OffsetIPRow** | IPRow Array 偏移（>0） |
| 104 | 8 | uint64 LE | **OffsetGeoEntries** | GeoEntry Section 起始偏移 |
| 112 | 8 | uint64 LE | OffsetColProj | 保留（当前 = 0） |
| 120 | 8 | uint64 LE | OffsetReverseIdx | 保留（当前 = 0） |
| 128 | 8 | uint64 LE | OffsetPoolSummary | 保留（当前 = 0） |
| 136 | 8 | uint64 LE | **OffsetPools** | String Pools 偏移 |
| 144 | 8 | uint64 LE | **OffsetMeta** | Metadata 偏移 |
| 152 | 4 | uint32 LE | **V4NodeCount** | V4 Trie 节点数 |
| 156 | 4 | uint32 LE | **V6NodeCount** | V6 Trie 节点数 |
| 160 | 4 | uint32 LE | **IPRowSize** | IPRow 行字节宽（当前 6） |
| 164 | 4 | uint32 LE | **GeoEntryGroupCount** | GeoEntry 版本组数（1~4） |
| 168 | 24 | uint48×4 | **GeoEntryOffsets[4]** | 每组 GeoEntry 相对 OffsetGeoEntries 的偏移（uint48 LE × 4） |
| 192 | — | — | 结束 | Header 固定 192 字节 |

### 3.1 VersionMask 定义

```
bit0: hasStd  — 文件中包含 std 版本
bit1: hasUlt  — 文件中包含 ult 版本
bit2: hasAsn  — 文件中包含 asn 版本
bit3: hasMax  — 文件中包含 max 版本
bit4~15: reserved
```

### 3.2 Flags 定义

```
bit0: hasV4       — 包含 V4 数据
bit1: hasV6       — 包含 V6 数据
bit2: hasMetadata — 包含 Metadata 段
bit3~15: reserved
```

### 3.3 偏移量规则

- 值为 `0` 表示该段不存在，对应查询应直接返回空结果
- 所有偏移量是**绝对偏移**（从文件开头计算）
- 所有段起始位置必须满足 `Align64`（`(offset & 63) == 0`）
- Header 后的第一个段可能留有填充字节以满足对齐

### 3.4 IPRowSize 说明

当前 Format v5 中 `IPRowSize = 6`（2 × uint24）。
Format v2 中 `IPRowSize = 9`（3 × uint24，含 usage_type_id）。
**SDK 必须读取 `IPRowSize` 字段来确定解析方式，而非硬编码。**

---

## 4. V4 Jump Table（跳表）

### 4.1 结构

固定 `65536 × 4 = 256KB`。

```
JumpV4[65536]: uint32 LE
  [0]     — 高 16 位 = 0x0000 的桶
  [1]     — 高 16 位 = 0x0001 的桶
  ...
  [65535] — 高 16 位 = 0xFFFF 的桶
```

### 4.2 条目语义

由 sentinel bit (`0x80000000`) 区分三种情况：

| 值范围 | 含义 |
|--------|------|
| `0x00000000` | 该桶无数据，查询立即返回 NOT_FOUND |
| `0x00000001 ~ 0x7FFFFFFF` | **内节点索引**：继续在该节点进行 Trie Walk |
| `0x80000001 ~ 0xFFFFFFFF` | **叶子**：低 31 位为 leaf_value（= row_id） |

### 4.3 构建规则

```
for hi16 = 0; hi16 < 65536; hi16++:
    ip = hi16 << 16
    jump[hi16] = TrieWalkFirstBits(ip, 16)
        - 遇到叶子（MSB=1）→ 返回 leaf_value | 0x80000000
        - walk 完 16 步仍在内部节点 → 返回 node_idx
        - 遇到空指针 → 返回 0
```

---

## 5. Trie Nodes

### 5.1 节点结构（8 字节）

```c
struct TrieNode {
    uint32 LE left;   // 左子节点索引（bit31=1 时为叶子值）
    uint32 LE right;  // 右子节点索引（bit31=1 时为叶子值）
};
```

### 5.2 Sentinel 规则

```c
const uint32 SENTINEL = 0x80000000u;

IsLeaf(ptr)         → (ptr & SENTINEL) != 0
GetLeafValue(ptr)   → ptr & 0x7FFFFFFF  // qzdb: 值为 row_id
GetNodeIdx(ptr)     → ptr & 0x7FFFFFFF

内节点: left/right 指向子节点索引（0x00000000~0x7FFFFFFF），0=无数据
叶子:   不独立占数组条目，通过父指针的 MSB=1 内联携带
        qzdb 叶子值语义 = row_id（IPRow 数组索引），不再直接是 geo_id
NOT_FOUND: 0（任何位置读到 0 表示无数据）
```

### 5.3 V4 与 V6 节点格式完全相同

**qzdb 节点格式与 旧版 完全一致**，仅叶子值的语义从 `geo_id` 变为 `row_id`。
Trie Walk 算法一字不改。

### 5.4 DFS 节点编号

构建时采用 DFS（深度优先）重新编号，确保子树节点连续排列，提高缓存命中率。
节点数组 `Nodes[0]` 为根节点。

### 5.5 V4 与 V6 节点计数

- V4 节点数 = `Header.V4NodeCount`
- V6 节点数 = `Header.V6NodeCount`
- `NodesV4` 和 `NodesV6` 各自独立存储在文件中不同偏移处

---

## 6. V6 Jump Table

### 6.1 结构

```
JumpV6[65536]: uint32 LE  // V6JumpBits=16 时的默认大小
大小 = 2^V6JumpBits × 4 字节
```

V6JumpBits 的值在 Header 偏移 11 处（`Header.V6JumpBits`），通常为 `16`。
取值为 `8 ~ 20`，根据 V6 CIDR 的前缀分布自动估算（`EstimateOptimalV6JumpBits`）。

### 6.2 条目语义

与 V4 Jump Table 完全一致。

**跳表深度选择影响**：
- 深度越大（更多跳表条目）→ 查询更快（更多 IP 前缀直接命中叶子）、文件更大（4MB@20bit）
- 深度越小（较少跳表条目）→ 查询更慢（更多 fallback 到 Trie Walk）、文件更小

---

## 7. IPRow Array（★ qzdb 新增）

### 7.1 设计意图

旧版 的 Trie 叶子只存一个 `geo_id`。qzdb 新增 IPRow 间接层，使 Trie 一次构建即可服务多个版本。

### 7.2 行格式

```c
    // Format v5（当前）：6 字节
struct IpRow {
    uint24 LE geo_id;   // 地理维度索引空间（3 字节，最大 1677 万）
    uint24 LE asn_id;   // ASN 维度索引空间（3 字节，最大 1677 万）
};

// Format v2（历史）：9 字节（含 usage_type_id，已移除）
struct IpRowV2 {
    uint24 LE geo_id;
    uint24 LE asn_id;
    uint24 LE usage_type_id;   // 当前恒为 0
};
```

**行号 `0` 为保留空行**（所有字段 = 0），表示"无数据"。

### 7.3 去重

构建时使用 `Dictionary<{geo_id, asn_id}, int>`：相同 ID 组合的 CIDR 共享同一行。
实际行数远小于 CIDR 总数。

### 7.4 查询路径变化

```
旧版:  Trie Walk → geo_id → GeoEntry[geo_id] → poolIdx[] → String[]
qzdb:  Trie Walk → row_id → IPRow[row_id] → {geo_id, asn_id}
                                          ├──→ GeoEntry_STD[geo_id]   → String[]
                                          ├──→ GeoEntry_ULT[geo_id]   → String[]
                                          ├──→ GeoEntry_ASN[asn_id]   → String[]
                                          └──→ GeoEntry_MAX[geo_id]   → String[]
```

**dimensionMask 决定使用哪个 ID**（在 GroupMetadataTable 中声明）：
- bit 0 (0x01)：地理维度 → 使用 `geo_id`
- bit 1 (0x02)：ASN 维度 → 使用 `asn_id`
- bit 2 (0x04) ~ 15：保留未使用

---

## 8. GeoEntry Section（★ qzdb 多组架构）

### 8.1 整体布局

```
OffsetGeoEntries →
┌───────────────────────────────────────┐
│  GroupMetadataTable                   │  ← 固定长度
├───────────────────────────────────────┤  ← Align64
│  GeoEntry_Group[0] 数据               │
│  GeoEntry_Group[1] 数据               │
│  GeoEntry_Group[2] 数据               │
│  GeoEntry_Group[3] 数据               │
└───────────────────────────────────────┘
```

每一组 GeoEntry 的起始位置由 `Header.GeoEntryOffsets[i]` 指定（相对 `OffsetGeoEntries` 的偏移）。

### 8.2 GroupMetadataTable

位于 `OffsetGeoEntries` 处，无对齐填充：

```
byte:     groupCount            ← 版本组数（1~4，与 Header.GeoEntryGroupCount 一致）
For each group:
  byte:     fieldCount          ← 该版本的维度数（std=6, pro=11, ult=15, asn=8, max=25；以 QZDBBuilder.VersionFieldNames 为准）
  uint32 LE: entryCount         ← 该版本组的 GeoEntry 条数（v4+；v2/v3 为 uint16）
  uint16 LE: dimensionMask      ← 该组使用的 ID 维度位掩码
```

**dimensionMask 含义**（Format v5 新增）：

```
bit0 (0x01): 地理维度 — 使用 IPRow.geo_id
bit1 (0x02): ASN 维度 — 使用 IPRow.asn_id
bit2~15:     保留
```

**Group 索引约定**（`GeoEntryOffsets[i]`）：

| i | 典型版本 | dimensionMask |
|---|---------|--------------|
| 0 | std / ult / max | 0x01（地理维度 → geo_id） |
| 1 | 无（或 ult） | — |
| 2 | asn | 0x02（ASN维度 → asn_id） |
| 3 | 无（或 max） | — |

### 8.3 GeoEntry 数据格式

每组的 GeoEntry 紧跟在 GroupMetadataTable 之后（从 `OffsetGeoEntries + GeoEntryOffsets[i]` 起始）。

```
GeoEntry_VER[entryId]:
  dim[0]: 第 0 维度的 Pool 索引（宽度 = PoolIdxSize 字节）
  dim[1]: 第 1 维度的 Pool 索引
  ...
  dim[N-1]: 第 N-1 维度的 Pool 索引（N = fieldCount）
  
每行字节宽 = fieldCount × PoolIdxSize
```

**GeoEntry[0] 始终为保留空条目**——所有 poolIdx = 0（表示空缺/未知）。

**各版本维度数（Pool 顺序 = 维度池顺序，以 `QZDBBuilder.VersionFieldNames` 为唯一真源，与 `reference/product-specification.md` §3 一致）**：

| 版本 | fieldCount | 字段（Pool 顺序） |
|------|-----------|------|
| std | 6 | continent, country_code, country, province, city, isp |
| pro | 11 | continent, country_code, country, province, city, district, geo_id, longitude, latitude, timezone, isp |
| ult | 15 | continent, country_code, country, province, city, district, geo_id, longitude, latitude, timezone, isp, asn, as_name, as_domain, usage_type |
| asn | 8 | continent, country_code, country, isp, asn, as_name, as_domain, usage_type |
| max | 25 | continent, continent_en, country_code, country_alpha3, country, country_en, province, province_en, city, city_en, district, district_en, geo_id, longitude, latitude, timezone, isp, languages, currency_code, phone_prefix, emoji_flag, asn, as_name, as_domain, usage_type |

> 完整字段定义、中文名与逐版本表格以 `reference/product-specification.md` §3 为唯一权威；SDK 按 Metadata 读取维度池，不硬编码 groupIndex→字段顺序映射。
> Pool 顺序以 C# 源码 `QZDBBuilder.VersionFieldNames` 为唯一真源（`usage_type` 替代旧版 `usage_flags` BIGINT 位标记，迷移 079 已执行）。

### 8.4 PoolIdxSize

`Header.PoolIdxSize` 决定池索引占用的字节数：

| PoolIdxSize | 最大索引值 | 选择条件 |
|-------------|-----------|----------|
| 2 | 65535 | 所有池的字符串数均 ≤ 65535 |
| 3 | 16777215 | 存在某池字符串数 > 65535 |

PoolIdxSize 全局统一（所有版本组、所有维度使用相同宽度）。

### 8.5 GROUP_SCHEMA 段（字段布局自描述，★ v5 新增）

位于 `Header.OffsetGroupSchema`，64 字节对齐。提供逐版本组、逐字段的精确布局，使 SDK 无需硬编码即可解析任意 GeoEntry 行：

```
ushort LE:  groupSchemaCount   ← 版本组数（与 GroupMetadataTable.groupCount 一致）
For each group:
  ushort LE:  groupId
  ushort LE:  fieldCount
  uint32 LE:  entryCount
  uint32 LE:  stride            ← 该行字节宽 = Σ(width)
  uint32 LE:  flags             ← 保留（当前 0）
  For each field:
    ushort LE:  fieldId
    byte:       width           ← 该字段槽位字节宽（池索引宽 或 原生值宽）
    byte:       fieldFlags      ★ P1-B 扩展位（见 §8.6）
    uint32 LE:  offset          ← 字段在 GeoEntry 行内的字节偏移
    uint32 LE:  poolSectionId   ← 池段序号（原生字段恒为 0）
```

SDK 应依据 `offset` + `width` 切片每个字段槽位；是否为原生标量由 `fieldFlags.bit0` 决定。

### 8.6 原生类型标量字段（★ HeaderVersion 6，P1-B）

**背景**：`longitude` / `latitude` / `asn` / `zip` 等数值字段在传统实现中以 UTF-8 字符串存入 String Pool，读取时需字符串解析与格式化（如 `%.6f`）。P1-B 引入**原生类型标量**：当事先可判定某字段为数值时，其标量值以定宽二进制**内联**于 GeoEntry 行，完全跳过 String Pool。

**判定规则**（构建期）：字段名匹配已知数值字段集（经纬度 `latitude`/`longitude`/`lat`/`lon`/`lng`、`asn`、`zip`/`zipcode`、`area_code`、`geoname_id` 等）**且**该字段在全部条目中的取值均能被解析为数值。任一取值非数值 → 该字段回退为普通 Pool 字符串字段。

**触发条件**：仅当至少一个版本组含原生字段时，`Header.HeaderVersion` 写为 `6`；否则维持 `5`（旧版 SDK 仍可正常读取 pool 索引部分，向后兼容）。

**字段标志 `fieldFlags`（GROUP_SCHEMA 中，v6 启用）**：

```
bit0 (0x01):  NATIVE    — 该字段为原生标量（值内联于 GeoEntry 行，无 Pool 索引）
bit1-2 (0x06): TYPE     — 原生类型：
              00 (0) = int      → width 取 1~4 字节 LE 无符号整数（按最大值位宽）
              01 (1) = float    → width = 4 (float32) 或 8 (float64)
              10 (2) = varint   → 保留（当前未使用）
bit3~7:       保留
```

**GeoEntry 行内的原生槽位**：

```
字段为 NATIVE 时：
  width 字节按 TYPE 解释：
    int:    ReadUintWidth(data[offset:offset+width], width)      // 1~4 字节 LE
    float:  width==4 → IEEE754 float32 (Int32BitsToSingle)
            width==8 → IEEE754 float64 (Int64BitsToDouble)
字段非 NATIVE 时：
  槽位 = 池索引（width 字节，指向 String Pool）
```

**查询语义**：`ResolveGeo` 对 `fieldFlags.bit0==1` 的字段调用 `DecodeNativeValue` 直接返回数值字符串（int 原样，float 按 6 位小数或更高精度格式化）；非原生字段仍走 Pool 索引。原生字段的 String Pool **不会被写入**（GROUP_SCHEMA 中 `poolSectionId=0`），读取器亦不解析其 Pool。

---

## 9. String Pools

### 9.1 布局

**每个版本组独立拥有一组 String Pool**，按版本组顺序排列。
每组内按维度顺序排列：`Pool[0]`、`Pool[1]`、... `Pool[fieldCount-1]`。

### 9.2 每个 DimensionPool 格式

```
┌───────────────────────────────────────┐
│ uint32 LE  count                      │  该池字符串总数（≥1，索引 0 恒为空串）
│ uint32 LE  offsets[0] (=0)            │  第 0 个字符串偏移（总是 0）
│ uint32 LE  offsets[1]                 │  第 1 个字符串起始偏移
│ ...                                    │
│ uint32 LE  offsets[count]             │  所有字符串数据的总长度
│ UTF-8      strings                    │  连续拼接的字符串数据
└───────────────────────────────────────┘
```

### 9.3 索引规则

- Pool 中索引 0 始终为**空字符串 `""`**（表示"无数据"）
- 第 i 个字符串的字节长度 = `offsets[i+1] - offsets[i]`
- 字符串按 UTF-8 编码，**不含 NUL 终止符**
- `PoolIdxSize` 为 2 时用 `uint16` 索引、为 3 时用 `uint24` 索引

### 9.4 惰性 Pool 加载（★ P1-E）

全量 String Pool 通常在初始化时一次性解析。P1-E 改为**按需惰性加载**：读取器在初始化时仅预计算每层 Pool 的字节偏移表，仅在 `Lookup` 命中具体字段时才解析对应 Pool 段。

```
PrecomputePoolOffsets():   // 初始化时执行一次
  对每组 g、每字段 f：
    _poolOffsets[g][f] = 该 Pool 段在文件中的绝对偏移
    _poolCache[g][f]   = null（首次访问时填充）

GetPool(g, f):             // 首次访问惰性解析
  if _poolCache[g][f] == null:
    _poolCache[g][f] = 解析 DimensionPool(_poolOffsets[g][f])
  return _poolCache[g][f]

GetPoolItem(g, f, poolIdx): // 取具体字符串
  return GetPool(g, f).Strings[poolIdx]
```

**收益**：低频查询或小数据集场景显著减少启动时间与内存占用；原生标量字段（§8.6）不进入 Pool，永不被惰性解析。SDK 可保留此优化，亦可按场景选择一次性全量预加载。

---

## 10. Metadata

### 10.1 格式

当 `Flags bit2` 置位时存在。位于 `Header.OffsetMeta`。

**连续 TLV (Type-Length-Value) 条目，不含 entry count，不含终止标记**。
SDK 应依次解析直到用尽文件长度或读到无效条目。

```
Entry:
  uint8     type      (1~255，类型编码)
  uint8     reserved  (0x00)
  uint16    length    (value 的字节数)
  byte[]    value     (UTF-8 字符串, length 字节)
```

### 10.2 类型定义

| Type | 名称 | 说明 | 示例值 |
|------|------|------|--------|
| 1 | version_list | 包含的版本列表（逗号分隔） | `"max"` 或 `"std,ult,asn,max"` |
| 2 | field_names | **Pipe 分隔的字段名列表** | `"continent|country|province|city|isp"` |
| 3 | description | 版本描述 | `"qqzeng-ip max edition 2026-07"` |
| 4 | primary_version | 主版本名 | `"max"` |

**SDK 必须优先从 Metadata 读取 type=2 的字段名列表**，而非硬编码版本→字段映射。

---

## 11. 查询算法

### 11.1 V4 查询

```
function SearchV4(ip: uint32, jump: uint32[], nodes: uint32[]): uint32
    hi16 = ip >> 16                     // 高 16 位
    ptr = jump[hi16]
    
    if ptr == 0:                return 0      // NOT_FOUND
    if ptr & SENTINEL:          return ptr & 0x7FFFFFFF  // 叶子：row_id
    
    idx = ptr
    suffix = (ip & 0xFFFF) << 16  // 低 16 位左对齐
    
    while true:
        node = nodes[idx*2]        // left
        node2 = nodes[idx*2+1]     // right
        bit = (suffix >> 31) & 1
        ptr = bit == 0 ? node : node2
        
        if ptr == 0:            return 0      // NOT_FOUND
        if ptr & SENTINEL:      return ptr & 0x7FFFFFFF  // 叶子：row_id
        
        idx = ptr
        suffix <<= 1
```

### 11.2 V6 查询

```
function SearchV6(ip: UInt128, jump: uint32[], nodes: uint32[], jumpBits: uint16): uint32
    hiIdx = ip >> (128 - jumpBits)      // 高 jumpBits 位
    ptr = jump[hiIdx]
    
    if ptr == 0:                return 0
    if ptr & SENTINEL:          return ptr & 0x7FFFFFFF
    
    idx = ptr
    depth = jumpBits
    
    while depth < 128:
        node = nodes[idx*2]
        node2 = nodes[idx*2+1]
        bit = (ip >> (127 - depth)) & 1
        ptr = bit == 0 ? node : node2
        
        if ptr == 0:            return 0
        if ptr & SENTINEL:      return ptr & 0x7FFFFFFF
        
        idx = ptr
        depth++
```

### 11.3 IPRow 解析

```
function ReadIpRow(rowId: uint32, data: byte[], ipRowSize: uint32): (geo_id, asn_id)
    if rowId == 0:              return (0, 0)     // 空行
    offset = rowId * ipRowSize
    geo_id = ReadUint24LE(data[offset:offset+3])
    asn_id = ReadUint24LE(data[offset+3:offset+6])
    return (geo_id, asn_id)
```

### 11.4 GeoEntry 解析

```
function ResolveGeo(entryId: uint32, groupIndex: uint8): string[]
    if entryId == 0:            return empty
    
    groupEntryStart = offsetGeoEntries + geoEntryOffsets[groupIndex]
    fieldCount = geoFieldCounts[groupIndex]
    entryOffset = groupEntryStart + entryId × fieldCount × poolIdxSize
    
    for i = 0; i < fieldCount; i++:
        poolIdx = ReadPoolIdx(data[entryOffset:entryOffset+poolIdxSize])
        strings[i] = groupPools[groupIndex][i][poolIdx]
        entryOffset += poolIdxSize
    
    return strings
```

### 11.5 完整查询路径

```
Query(ip_string, groupIndex=0):
  1. 解析 IP 字符串 → UInt128 + IP 版本 family(4/6)
  2. 根据 family 选择 V4/V6 查询路径
  3. Trie Walk → row_id
  4. ReadIpRow(row_id) → {geo_id, asn_id}
  5. 从 GroupMetadataTable 获取 dimensionMask[groupIndex]:
     - mask & 0x01 → GeoEntry ID = geo_id
     - mask & 0x02 → GeoEntry ID = asn_id
     - mask & 0x04 → GeoEntry ID = usage_type_id (当前未使用)
  6. ResolveGeo(entryId, groupIndex) → string[]
   7. 返回 string[]（按该版本的字段顺序）

### 11.6 列投影查询（★ P1-D）

`Lookup` 支持只取部分字段，避免解析全部 Pool：

```
Query(ip_string, groupIndex=0, fieldIndices=null):
  ... 同 11.5 步骤 1~6 得到完整 string[]（记为 full[]）
  if fieldIndices == null 或 为空:
      return full                      // 全字段，按字段顺序
  result = []
  for idx in fieldIndices:
      if idx 越界 (0 <= idx < full.Length):
          result.add(full[idx])        // 保持请求顺序
      else:
          result.add("")               // 越界下标补空串
  return result
```

**语义约束**：`fieldIndices` 仅为读取侧的投影，**不改变存储格式**（§8 GeoEntry 仍按完整 fieldCount 存储）。越界下标返回 `""` 而非抛异常，便于调用方容错。

### 11.7 IPv4 映射的 IPv6 地址（★ P1-F）

`::ffff:0:0/96` 范围内的 IPv6 地址（即 `(ip >> 32) == 0xFFFF`，高 32 位为 `0:0:0:0:0:ffff`，低 32 位承载 IPv4 地址）在语义上等价于对应 IPv4。查询时应自动剥离映射前缀、改走 V4 Trie：

```
IsIpv4MappedIpv6(ip: UInt128): bool
    return (ip >> 32) == 0xFFFF

Search(ip, ...):
  if family == 6 且 IsIpv4MappedIpv6(ip):
      v4addr = (uint)(ip & 0xFFFFFFFF)     // 取低 32 位
      return SearchV4(v4addr, ...)         // 复用 V4 查询路径
  else:
      ... 原 11.1/11.2 路径
```

**注意**：此规则仅适用于「IPv4 映射的 IPv6」一种特例。`::ffff:/96` 之外的其他 IPv6 地址（如原生 IPv6 网段）仍走 V6 Trie。当文件不含 V4 数据（`Flags.bit0==0`）时，映射地址同样返回 NOT_FOUND。
```

### 11.8 查询时间复杂度（★ 重要更正）

QZDB 的查询复杂度**不是**记录数 N 的对数 `O(log N)`，而是**由 IP 地址前缀位宽决定**的 `O(prefix_bits)`：

- **V4**：先经 16 位 V4 Jump Table 一次性定位（1 次查表，见 §4），再在 Trie 中最多走剩余 16 位（§11.1）→ **≤ 32 步**，与 V4 记录总数无关。
- **V6**：先经 `V6JumpBits` 位 V6 Jump Table 定位（1 次查表，默认 16 位，范围 8~20，见 §6），再在 Trie 中走剩余 `128 - V6JumpBits` 位（§11.2）→ **≤ 128 步**，与 V6 记录总数无关。

**结论**：单次查询步数仅取决于地址位宽（32 / 128）和 Jump Table 深度，**与数据规模（记录数 / 节点数）无关**。这是跳表（Jump Table）设计的核心收益——把前缀高位一次性「跳」到正确子树，剩余位宽恒定。

> ⚠️ 旧版格式（见旧版字段设计文档 §4.9 的 Eytzinger 二分搜索，已废弃）才是 `O(log n)`，但那属于**已废弃的旧格式**，不适用于 QZDB。请勿将两者复杂度混淆。

---

## 12. SDK 实现要点

### 12.1 Init 流程

```
1. 读取文件前 192 字节为 Header
2. 验证 Magic = "QZDB"
3. 读取 HeaderVersion；`QZDBReader`**接受 `5` 和 `6`**（`fmtVer != 5 && fmtVer != 6` 抛 `NotSupportedException`）。v5 文件（动态宽度 schema）可被正常解析；不支持 v2~v4 回退。v6 原生标量见 §8.6
4. 读取 Flags → 确定 hasV4/hasV6/hasMetadata
5. 读取 V6JumpBits → 确定 V6 跳表大小
6. 读取偏移量 → 各个段的文件位置
7. 读取 GroupMetadataTable：
   a. 定位到 OffsetGeoEntries
   b. 读取 groupCount + 每组 fieldCount/entryCount/dimensionMask
   c. 读取 GeoEntryOffsets[i]（每组起始偏移）
8. [惰性加载] 读取 String Pools（按版本组 + 维度）
9. [推荐] 从 Metadata 读取 field_names 确定字段映射
```

### 12.2 GeoEntry 版本组索引约定

| groupIndex | 典型版本 | 维度池数（字段数） | 使用 IPRow 中的 ID |
|------------|---------|-------------------|-------------------|
| 0 | std / pro / ult / max | std=6, pro=11, ult=15, max=25 | geo_id（dimensionMask=0x01） |
| 1 | — | — | — |
| 2 | asn | asn=8 | asn_id（dimensionMask=0x02） |
| 3 | — | — | — |

> 各版本完整字段名与维度池顺序以 **§8.3** 与 `reference/product-specification.md` §3 为唯一权威；维度池数：std=6 / pro=11 / ult=15 / asn=8 / max=25。

**SDK 不应硬编码 groupIndex → 版本关系**，应通过 Metadata 读取。

### 12.3 字段名来源优先级

| 优先级 | 来源 | 说明 |
|--------|------|------|
| 1（最高） | Metadata type=2 | `field_names.split('|')` |
| 2 | 硬编码版本→字段映射 | 仅当 Metadata 不存在时回退 |

### 12.4 PoolIdxSize 选择逻辑

构建时遍历所有版本组的所有维度池：
```
poolIdxSize = 2
for each versionGroup:
    for each dimension pool:
        if pool.stringCount > 65535:
            poolIdxSize = 3
```

### 12.5 浮点字段处理

`longitude` / `latitude` 字段在 String Pool 中以字符串存储。
SDK 应通过字段名匹配识别浮点字段，格式化为 6 位小数 (`%.6f`)。

### 12.6 CRC32

- 算法：标准 CRC-32/IEEE 802.3（多项式 `0xEDB88320`）
- 计算范围：**整个文件**
- CRC 自身 4 字节（Header 偏移 16-19）在计算前填 `0x00000000`
- 验证通过不等于数据绝对正确（概率性），但不通过说明文件一定损坏
- **Reader 默认在打开时校验**（`QZDBReader(path, verifyCrc: true)`）；不匹配则抛 `InvalidDataException`
- 诊断/压测可传 `verifyCrc: false` 跳过；**生产路径应保持开启**

### 12.7 零拷贝读取（mmap 优化）

建议使用 mmap 或内存映射文件读取 `.qzdb`：
- Header 段直接取前 192 字节
- Trie Nodes：`ReadOnlySpan<uint>` 零拷贝转换（`MemoryMarshal.Cast`）
- IPRow/GeoEntry：直接切片
- String Pools：惰性加载，按需解析

### 12.8 关键常量

```c
const uint32 SENTINEL = 0x80000000u;
const uint16 HEADER_SIZE = 192;
const uint8  V4_JUMP_BITS = 16;           // V4 跳表固定 16 位
```

---

## 13. P1 增强：原生标量 / 列投影 / 惰性池 / IPv4 映射 / 增量更新

本章汇总 qzdb 格式的 P1 期增强。其中 **§13.1 影响二进制格式**，§13.2~13.5 为读取器 / 构建器 API 行为（不改变磁盘布局，旧文件可直接被新 SDK 读取）。

### 13.1 原生类型标量（磁盘格式扩展）

见 §8.5 / §8.6。仅当至少一个版本组含原生数值字段时 `HeaderVersion=6`，否则 `5`。原生字段值内联于 GeoEntry 行，其 GROUP_SCHEMA `fieldFlags` 标记 `NATIVE`/`TYPE`。**向后兼容**：v5 SDK 忽略 v6 新增语义，仅读取其可解释的 Pool 索引部分（原生字段在 v5 文件中不会出现）。

### 13.2 列投影查询（读取侧）

见 §11.6。`Lookup(ip, groupIndex, int[]? fieldIndices)`：投影仅在读取侧发生，存储格式不变；越界下标返回 `""`。

### 13.3 惰性 Pool 加载（读取侧）

见 §9.4。初始化仅预计算 Pool 偏移表，命中字段时才解析对应 Pool 段；原生字段不入 Pool，永不被解析。

### 13.4 IPv4 映射的 IPv6（查询侧）

见 §11.7。`::ffff:0:0/96` 范围地址自动剥离映射前缀、改走 V4 Trie；其余 IPv6 仍走 V6 Trie。

### 13.5 增量更新 `AppendVersionGroup`（构建侧）

构建器提供**不重建 trie / 不改动 IPRow** 的追加新版本组 API：

```csharp
// 返回新追加组的下标（= 追加前的组数）；失败时抛异常
int QZDBBuilder.AppendVersionGroup(
    string srcPath,                              // 现有 .qzdb 文件
    string newVersion,                           // 新版本组名（不得与已有重名）
    List<(string Cidr, string GeoFields)> newData, // 新组条目；CIDR 必须已存在于既有 trie
    string dstPath                               // 输出路径
);
```

**行为契约**：

1. **4 组上限**：若 `reader.GroupCount >= 4` → 抛 `QZDBValidationException`（硬约束，与 §8 的 1~4 组上限一致）。
2. **版本名冲突**：若 `newVersion` 已存在于源文件 → 抛 `QZDBValidationException`。
3. **GeoEntry 对齐**：将源文件反序列化为可重序列化的 `BuildContext`（1:1 还原既有各段，含原生字段），新组 GeoEntry 按**共享 geoId 空间**对齐——通过 `reader.LookupIds(cidr 的网络地址)` 取得既有 `geo_id`，写入 `GeoEntryList[geo_id]`。CIDR 不在 trie 中时跳过（记日志）。
4. **复用写入路径**：新文件由同一 `WriteQzdbFile` 写出，磁盘格式与一次构建完全一致；`HeaderVersion` 保持源文件值（v5 文件追加后仍为 v5，除非新组自身触发原生标量 → v6）。
5. **不修改磁盘格式 / trie / IPRow / 头结构**，仅在其后追加一组 GeoEntry + 对应 Pool。

**适用场景**：已发布 `std` 文件需追加 `asn` 组，或 `std` 文件需升级至含原生经纬度字段的 `max` 组，而无需重新跑全量 ETL。

---

## 14. 遗留格式（IpDbSearch / v6.0）

> ⚠️ **遗留 / 非 QZDB**：以下描述一个**独立的旧版读取器** `src/QQzeng.MergeEngine/Common/IpDbSearch.cs`（源码注释标记「6.0版本」，读取 `qqzeng-ip-6.0-global.db`）。它**不是** QZDB 格式，本文档 §1~§13 的规范**不适用于**它。此处仅作归档说明，避免与 QZDB 混淆。新代码应统一使用 `QZDBReader` 读取 `.qzdb` 文件。

### 14.1 与 QZDB 的关键差异

| 维度 | IpDbSearch (v6.0 遗留) | QZDB (本文档) |
|------|------------------------|---------------|
| 文件标识 | 无 Magic；首 4 字节为 `nodeCount` (`int32`) | Magic `QZ20` + `HeaderVersion`（§3） |
| Trie 节点 | **6 字节/节点**（`nodeNumber * 6`，3 字节定宽子节点指针） | 8 字节/节点（`uint32` left/right，见 §5） |
| 字符串存储 | **Tab 分隔**（`data.Split('\t')`）的单一扁平数组 `geoispArr` | 多版本组 String Pool（见 §9） |
| 前缀索引 | 首 2 字节前缀 → 3 字节偏移（`ReadPref`，`4 + prefix*3`） | V4/V6 Jump Table（见 §4 / §6） |
| 多版本 | 单版本 | 多版本组（std/ult/asn/max，见 §8） |
| 状态 | 遗留，仅旧数据兼容 | **当前规范格式** |

### 14.2 实现要点（IpDbSearch.cs）

- `startIndex = 0x30004`（= `4 + 65536 * 3`）：跳过文件头（4 字节 `nodeCount`）与 V4 前缀索引区（65536 × 3 字节）。
- `Find(string ip)`：取 IP 高 16 位前缀查 `ReadPref` 得到起始节点，沿 Trie 按位下降（`(suffix >> 15) & 1`），遇 `endMask (0x800000)` 终止，用 `record & ~endMask` 索引 `geoispArr` 返回整行字符串。
- `ReadNode(nodeNumber, bit)`：`offset = startIndex + nodeNumber * 6 + bit * 3`，3 字节定宽子节点指针（与 QZDB 的 8 字节节点不同）。
- 该读取器**未接入**当前 6 层表管线；属于历史兼容路径，不应作为新开发的参考。

---

## 附录1：格式版本演化

### v1 → v2 → v3

| Version | HeaderVer | IPRowSize | dimensionMask | 说明 |
|---------|-----------|-----------|---------------|------|
| v1 | 1 | 9 (3×uint24) | 无 | 初始原型，含 usage_type_id |
| v2 | 2 | 9 (3×uint24) | 无 | 稳定版，含 usage_type_id |
| v3 | 3 | 6 (2×uint24) | 有 | 移除 usage_type_id，新增 dimensionMask |
| v4 | 4 | 6 (2×uint24) | 有 | 每版本组 GeoEntry 条数升级为 uint32（支持 >65535） |
| v5（当前） | **5** | **6 (2×uint24)** | **有** | 新增 ROW_SCHEMA/GROUP_SCHEMA 自描述字段布局 |
| v6（原生标量） | **6** | **6 (2×uint24)** | **有** | 在 v5 基础上：任一版本组含原生数值字段时启用；GROUP_SCHEMA 逐字段 `fieldFlags` 标记 `NATIVE`/`TYPE`，GeoEntry 行内联定宽数值（见 §8.5/§8.6/§13.1） |

> **读取器兼容性更正**：v5 在格式演化中作为独立版本列出。当前 `QZDBReader`（`src/QQzeng.MergeEngine.Core/QZDBReader.cs:127-128`）**同时接受 `HeaderVersion == 5` 和 `== 6`**（`fmtVer != 5 && fmtVer != 6` 才抛 `NotSupportedException`）。v5 文件（含动态宽度 schema）可被正常解析；v6 = v5 + 原生类型标量字段。对外发布的文件由 `QZDBBuilder` 构建为 v6（写入值固定 6），但历史 v5 文件现可读取。文档历史版本曾称「仅 6 兼容」，与当前代码不符，特此更正。

### v3 相对 v2 的变化

1. `IPRowSize` 从 `9` 降为 `6`：移除 `usage_type_id`（该值始终为 0）
2. `HeaderVersion` 从 `2` 升为 `3`
3. GroupMetadataTable 每组新增 `dimensionMask`（uint16 LE）
4. `Reader` 中 `fmtVer >= 3` 时解析 dimensionMask，否则按约定回退

### 旧版 → 旧版 → qzdb 演进

| | 旧版 | 旧版 | qzdb |
|---|-----|-----|-----|
| 查询引擎 | Eytzinger BST + 线性二分 | PATRICIA Trie | PATRICIA Trie + IPRow |
| 跳表 | 16-bit Index Table + 块内二分 | 16-bit Jump Table | V4=16固定, V6=动态 |
| 叶子值 | N/A | geo_id | row_id → IPRow |
| GeoEntry | 1 组 | 1 组 | **多组（每版本独立）** |
| 字段数 | 8 池 ± 2 浮点 | 版本可变 | 版本可变 |
| String Pools | 1 套全局 | 1 套 | **多组（每版本独立）** |

---

## 附录2：产品矩阵与字段定义

### 级别划分

| 中文名 | 英文名 | version(CSV) | 缩写 | 核心字段维度 |
|--------|--------|-------------|------|-------------|
| 标准版 | Standard | std | std | 基础地理定位（大洲/国家/省份/城市/运营商） |
| 旗舰版 | Ultimate | ult | ult | + 区县 + geo_id + 经纬度 + 国家简码 |
| ASN 路由版 | ASN | asn | asn | 自治域号 + 域名 + 应用场景 + 国家 + 运营商 |
| 至尊版 | Max | max | max | 全能 26 字段（含 CIDR）/ 25 维度池（地理+ASN+时区+货币+语言+国旗+电话区号等） |

### 每版本字段集（权威定义见 `reference/product-specification.md` §3）

> ⚠️ 本节历史附录的字段名/成员关系基于旧版源码（含 `country_english` / `timezone_zh` / `currency_name` / `area_code` / `usage_type` / `asn_org` 等已重命名或移除字段）。**当前各版本字段名、字段数与 Pool 顺序以 `reference/product-specification.md` §3 为唯一权威**，构建器与导出端均复用 `QZDBBuilder.VersionFieldNames`。下方仅保留当前 fieldCount 供格式解析参考：

| 版本 | fieldCount（维度池数，不含 CIDR） |
|------|-----------|
| std | 6 |
| pro | 11 |
| ult | 15 |
| asn | 8 |
| max | 25 |

> CSV 列数 = fieldCount + 1（首列 `cidr`）；Range 版前四列为 `start_ip|end_ip|start_ip_num|end_ip_num`。SDK 按 Metadata 读取，不硬编码字段顺序。

---

> **本文是 QZDB 二进制格式的唯一权威规范**。所有跨语言 SDK 实现和开发均以此为准。
> 如有与实际实现冲突的内容，以实际实现为准，但应提交 Issue 更新本文档以消除差异。
