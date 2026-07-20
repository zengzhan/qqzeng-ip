# QZ18 Binary Format Specification v2

## 1. Overview

QZ18 是 qqzeng-ip 数据库的版本 18 二进制格式。
所有多字节值均为 **Little-Endian**，除非特别注明。
头长度固定 **128 字节**。

### Design Principles

- **Self-describing**: 头包含解析所需全部信息，SDK 无需外部参数
- **Extensible**: 版本号 + 保留空间支持向后兼容演进
- **Aligned**: 所有字段按自然边界对齐，支持直接内存访问
- **Verified**: CRC32 覆盖整个文件（含头，CRC 自身填零后计算）
- **Metadata**: 可选元数据区存放字段名、版本名等，实现完全自描述

---

## 2. Header (128 bytes)

```
偏移    大小    类型        说明
─────   ────   ──────      ─────────────────────────────────────────────
  0      4     char[4]     Magic: "QZ18"

  4      1     uint8       Header Version: 0x01
  5      1     uint8       Database Version Code (0x00-0x03 built-in, 0xFF custom)
  6      1     uint8       Pool Count (= 字段数量 = 字符串池数量)
  7      1     uint8       Geo ID Size (2 或 3 字节)

  8      2     uint16      Flags:
                              bit 0: has IPv4
                              bit 1: has IPv6
                              bit 2: has Metadata section
                              bits 3-15: reserved
 10      2     uint16      Reserved (填 0x0000)

 12      4     uint32      Header Size (固定 128，允许未来扩展)
 16      4     uint32      Geo Count (地理条目总数)
 20      4     uint32      V4 Record Count (IPv4 段数)
 24      4     uint32      V6 Record Count (IPv6 段数)
 28      4     uint32      Build Date (yyyyMMdd 格式 LE)
 32      4     uint32      CRC32 (整个文件，CRC 自身 4 字节填零后计算)

 36-63  28     byte[28]    Reserved (填零)

段偏移 (相对于文件开头, uint64 LE):
 64      8     uint64      Offset Geo Data
 72      8     uint64      Offset String Pools
 80      8     uint64      Offset V4 Index
 88      8     uint64      Offset V4 Block Data
 96      8     uint64      Offset V6 Data
104      8     uint64      Offset Metadata (0 表示不存在)

112-127 16     byte[16]    Reserved (填零)
```

### 2.1 各字段说明

| 字段 | 说明 |
|------|------|
| Header Version | 标识头结构版本。阅读器检查此字段以确定如何解析头 |
| Database Version | `0=std` `1=ult` `2=asn` `3=max` `0xFF=custom`。SDK 应优先从 Metadata 取字段名；版本号仅用于无 Metadata 文件的回退 |
| Pool Count | 等于字段数（=字符串池数）。与 Metadata field_names 中的字段数应一致 |
| Geo ID Size | 2 或 3。可根据 GeoCount 推算: >65535 用 3 |
| Flags bit 0 | hasV4=1 时 OffsetV4Index 和 OffsetV4Data 有效 |
| Flags bit 1 | hasV6=1 时 OffsetV6Data 有效 |
| Flags bit 2 | hasMetadata=1 时 OffsetMetadata 指向元数据区 |
| Header Size | 固定 128，升级时可增大 |
| CRC32 | 整个文件(CRC32 位置填 4 字节 0 后计算)，含头和数据 |

---

## 3. Data Sections

所有段起始于 `Align64` 边界（向上取整到 64 的倍数）。

### 3.1 Geo Data (`OffsetGeo`)

每条地理记录 = 连续排列的 PoolCount 个 uint16 池索引。

```
字节范围                         说明
─────────────────────────────────────────────────
[0..PoolCount×2 - 1]       geoId=0 空记录 (全 0)
[PoolCount×2 .. ×2-1]      geoId=1 记录
...
总大小 = GeoCount × PoolCount × 2 字节
```

geoId=0 永远是空记录，所有池索引为 0（对应空字符串）。

### 3.2 String Pools (`OffsetPools`)

按 0..PoolCount-1 顺序排列的字符串池。每个池格式相同：

```
+──────────┬──────────────────────────────────────+
│ uint32   │ count (字符串数量, ≥1, 第0个为空串)         │
│ uint32[] │ offset[count+1] (相对数据区起点的偏移数组)     │
│ byte[]   │ UTF-8 字符串数据拼接                       │
+──────────┴──────────────────────────────────────+
```

池中第 0 个字符串始终是空字符串 `""`。
`offset[i+1] - offset[i]` 给出第 i 个字符串的字节长度。

### 3.3 V4 Index Table (`OffsetV4Index`)

65537 个 uint32 LE 的数组。第 i 个条目是第 i 个 /16 块在 V4BlockData 区内的**相对偏移**。
第 65536 个条目是 V4BlockData 总大小。

```
index[i] = block i 相对 OffsetV4Data 的偏移 (i = 0..65535)
index[65536] = V4BlockData 总大小
```

若 `index[i] == index[i+1]`，则该 /16 块无数据。

查找 IPv4: `blockOffset = OffsetV4Data + index[ip >> 16]`。

### 3.4 V4 Block Data (`OffsetV4Data`)

每个有数据的 `/16` 块按 **Eytzinger (BST)** 布局存储，便于二分查找。

```
+──────────┬──────────────────────────────────────+
│ uint16   │ count (本块内 IP 段数量)                    │
│ Node[1]  │ Eytzinger 树根节点                      │
│ Node[2]  │                                        │
│ ...      │ (1-indexed Eytzinger 布局)               │
│ Node[count]                                      │
+──────────┴──────────────────────────────────────+

Node 结构:
  uint16        key      (IP 低 16 位 = startIP & 0xFFFF)
  uint8[GeoIdSize] geoId (LE)
```

**搜索算法**:
```
k = 1
while k ≤ count:
    node = block[k]
    if key < node.key:
        k = 2*k          // 左子树
    else:
        bestGeo = node.geoId
        k = 2*k + 1      // 右子树
```

### 3.5 V6 Data (`OffsetV6Data`)

按 startIP 升序排列的定长记录数组，二分查找。

```
+──────────┬──────────────────────────────────────+
│ uint32   │ count (段数)                            │
│ Record[] │ 定长记录数组                              │
+──────────┴──────────────────────────────────────+

Record 结构 (32 + GeoIdSize 字节):
  uint64 BE   startIP 高 64 位
  uint64 BE   startIP 低 64 位
  uint64 BE   endIP 高 64 位
  uint64 BE   endIP 低 64 位
  uint8[GeoIdSize]  geoId (LE)
```

V6 IP 片段用 Big-Endian 存储（与网络字节序一致）。

### 3.6 Metadata (`OffsetMetadata`, 可选)

当 Flags bit 2 置位时存在，为连续 TLV (Type-Length-Value) 条目。

⚠️ 注意：Builder 写出的 metadata 区**不含 entry count**，条目连续排列直到文件末尾。
    SDK 读取时依次解析直到遇到 type=0 的终止条目或用尽文件长度。

```
Entry:
  uint8     type      (1=version_name, 2=field_names, 3=description)
  uint8     reserved  (0x00)
  uint16    length    (value 字节数)
  byte[]    value     (UTF-8 字符串)
```

| Type | Name | 用途 | 示例 |
|------|------|------|------|
| 1 | version_name | 版本标识字符串 | `max` |
| 2 | field_names | **pipe 分隔的字段名列表，SDK 应以此为准** | `continent|country|province|city|isp` |
| 3 | description | 版本描述 | `qqzeng-ip max edition 2026-06` |

> **关键设计决策**：所有 SDK 在 Init 时必须优先从 Metadata 区读取 `field_names` (type=2)，
> `split('|')` 后得到字段名数组，字段索引与字符串池索引一一对应。
> 仅当 Metadata 区不存在时才回退到按版本号硬编码的字段映射。

---

## 4. Field Mappings（仅供参考）

> ⚠️ **本节的字段映射表仅作文档参考。SDK 必须优先从 Metadata 区读取 `field_names` (type=2)。
> 版本号→字段映射的硬编码已废弃，仅作为无 Metadata 旧文件的回退。**

### 4.1 内置版本（硬编码回退映射）

| # | 字段 | std (5) | ult (11) | asn (7) | max (25) |
|---|------|:-------:|:--------:|:-------:|:--------:|
| 0 | `continent` | ● | ● | ● | ● |
| 1 | `country` | ● | ● | ● | ● |
| 2 | `province` | ● | ● | — | ● |
| 3 | `city` | ● | ● | — | ● |
| 4 | `isp` | ● | ● | ● | ● |
| 5 | `district` | — | ● | — | ● |
| 6 | `area_code` | — | ● | — | ● |
| 7 | `country_english` | — | ● | — | ● |
| 8 | `country_code` | — | ● | — | ● |
| 9 | `longitude` | — | ● | — | ● |
| 10 | `latitude` | — | ● | — | ● |
| 11 | `country_alpha3` | — | — | — | ● |
| 12 | `province_en` | — | — | — | ● |
| 13 | `city_en` | — | — | — | ● |
| 14 | `timezone_en` | — | — | — | ● |
| 15 | `timezone_zh` | — | — | — | ● |
| 16 | `languages` | — | — | — | ● |
| 17 | `currency_code` | — | — | — | ● |
| 18 | `currency_name` | — | — | — | ● |
| 19 | `phone_prefix` | — | — | — | ● |
| 20 | `emoji_flag` | — | — | — | ● |
| 21 | `usage_type` | — | — | ● | ● |
| 22 | `asn` | — | — | ● | ● |
| 23 | `asn_org` | — | — | ● | ● |
| 24 | `asn_domain` | — | — | ● | ● |

### 4.2 Custom Version (0xFF) / 未来版本

版本号 0xFF 或 Metadata 区存在的文件：SDK 必须通过 `field_names.split('|')` 获取字段名列表，
数量应与 PoolCount 一致。这是**唯一正确的做法**，不依赖任何硬编码。

### 4.3 浮点字段识别

`longitude` / `latitude` 字段的值格式化为 6 位小数 (`%.6f`)。
SDK 应通过字段名称匹配（而非数字索引）来识别浮点字段，
以确保支持未来新增的浮点类型字段。

---

## 5. SDK Behavior

### 5.1 核心原则：Metadata-Driven

> 所有 SDK **必须**优先从文件 Metadata 区读取字段信息，硬编码的版本→字段映射
> 仅作为无 Metadata 旧文件的回退。

### 5.2 Init 流程

```
1. 读取文件前 128 字节作为头
2. 校验 Magic ("QZ18")
3. 读取 Header Version，确定头解析方式
4. 读取 Pool Count (= 字段数 = 字符串池数)
5. 读取 Flags，检查 bit 2 (hasMetadata)
6. 读取段偏移 (Geo, Pools, V4Idx, V4Data, V6Data, Metadata)
7. 读取字符串池 _pools[0..PoolCount-1]
8. **读取字段名（关键步骤）**：
   a. 如果 Flags bit 2 = 1 且 OffsetMetadata > 0：
      - 解析 TLV 条目
      - 找到 type=2 (field_names)，split('|') → 字段名数组
      - 找到 type=1 (version_name) → 版本字符串
   b. 如果 Metadata 不存在或 field_names 缺失/不匹配：
      - 回退到按 Database Version Code 硬编码的字段映射
9. **预计算浮点字段标记**：遍历字段名，longitude/latitude 标记为 float
```

### 5.3 findStr() Output

按 Metadata 或回退映射中字段名的**索引顺序**输出，管道符 `|` 分隔。
`longitude` / `latitude` 格式化为 6 位小数 (`%.6f`)。
不存在的字段输出空字符串。

字段名和格式规则总结：
- `Values[i]` = `_pools[i][geo.poolIdx[i]]`（所有字段统一走字符串池索引）
- `longitude` / `latitude` → 池中取字符串 → `float.Parse` → `%.6f`
- 其余字段 → 池中取字符串原样输出

### 5.4 搜索流程

**IPv4**: `findUint(ipInt)`:
```
high = ipInt >> 16
blockOffset = V4Index[high]  (rel)
if next index same: return null
block = V4BlockData[blockOffset]
search block by Eytzinger BST using ipInt & 0xFFFF
```

**IPv6**: `findV6(high, low)`:
```
二分查找 V6Data 数组，比较 startIP
```

### 5.5 字段访问 API（各语言参考）

```python
# Python 参考实现
class QzdbSearcher:
    def load(self, path):
        data = read_file(path)
        meta = data[104:112]  # OffsetMetadata
        if flags & 4 and meta > 0:
            for entry in parse_tlv(data, meta):
                if entry.type == 2:     # field_names
                    self.field_names = entry.value.split('|')
                elif entry.type == 1:   # version_name
                    self.version = entry.value

    def find_str(self, ip):
        values = self.find(ip)  # string[_poolCount]
        parts = []
        for i, name in enumerate(self.field_names):
            val = values[i] or ''
            if name in ('longitude', 'latitude') and val:
                val = f'{float(val):.6f}'
            parts.append(val)
        return '|'.join(parts)
```

### 5.6 关键兼容性保证

| 场景 | 字段名来源 | 说明 |
|------|-----------|------|
| 新 QZDB 文件（有 Metadata） | Metadata type=2 | **推荐路径**，SDK 零硬编码 |
| 旧 QZDB 文件（无 Metadata） | 版本码→硬编码映射 | 回退路径，向后兼容 |
| 版本号 0xFF (custom) | **必须**有 Metadata | 硬编码映射无对应版本 |
| 新增字段 | Metadata 自动适配 | SDK 无需更新 |

---

## 6. Constants & Limits

| Item | Value |
|------|-------|
| Magic | `QZ18` |
| Header Size | 128 bytes |
| Section Alignment | 64 bytes |
| Max Geo Entries | `2^24 - 1` (16,777,215) with 3-byte IDs |
| Geo ID Size | 2 bytes (≤65535 entries) or 3 bytes |
| Max Pools | 255 (uint8) |
| V4 Index Entries | 65537 × uint32 |
| V6 Record Size | 32 + GeoIdSize bytes |
| String Encoding | UTF-8 |
| CRC-32 Poly | 0xEDB88320 (standard) |

---

## 7. File Layout Example

```
┌─────────────────────────────────────┐
│ Header (128 bytes)                  │
├─────────────────────────────────────┤
│ Geo Data (variable)                 │  ← Align64
│   [geoId=0] [geoId=1] ...          │
├─────────────────────────────────────┤
│ String Pools (variable)             │  ← Align64
│   [pool0] [pool1] ... [poolN-1]    │
├─────────────────────────────────────┤
│ V4 Index Table (65537 × 4 bytes)   │  ← Align64
├─────────────────────────────────────┤
│ V4 Block Data (variable)            │  ← Align64
│   [/16 block keys+geoIds]          │
├─────────────────────────────────────┤
│ V6 Data (variable)                  │  ← Align64
│   [count + records]                 │
├─────────────────────────────────────┤
│ Metadata (optional, variable)       │  ← Align64
│   [TLV entries]                     │
└─────────────────────────────────────┘
```

---

## 8. Version History

| Header Version | Date | Changes |
|---------------|------|---------|
| 0x01 | 2026-06 | Initial V18 specification with variable pool count, version code, metadata |
