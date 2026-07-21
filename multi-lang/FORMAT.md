# QZDB 二进制格式规范 (QZDB Binary Format Reference)

> **当前格式**：`QZDB`（magic `QZDB`），统一写入 `HeaderVersion = 1`。
> 本文件描述生成器 `QZDBBuilder.cs` 实际输出的磁盘格式，供多语言 SDK 解析参考。
> 权威规范以 `QQzeng.MergeEngine.Core/QZDBBuilder.cs` 源码为准。

---

## 1. 文件整体布局

```
┌──────────────────────────────────────┐
│  Header (192 字节, 固定)             │
├──────────────────────────────────────┤  ← Align64
│  V4 Jump Table (256KB = 65536×4B)    │  无 V4 时跳过（offset=0）
├──────────────────────────────────────┤  ← Align64
│  V4 Trie Nodes (N4 × 6B 或 8B)       │  无 V4 时跳过
├──────────────────────────────────────┤  ← Align64
│  V6 Jump Table (2^J6 × 4B)           │  J6=16~20，多为 16；无 V6 时跳过
├──────────────────────────────────────┤  ← Align64
│  V6 Trie Nodes (N6 × 6B 或 8B)       │  无 V6 时跳过
├──────────────────────────────────────┤  ← Align64
│  IPRow Array (RowCount × IPRowSize)  │  ★ QZDB 新增间接层
├──────────────────────────────────────┤  ← Align64
│  GeoEntry Section                    │
│  ├─ GroupMetadataTable               │  版本组元信息
│  ├─ GeoEntry_Group[0] (主版本)       │
│  ├─ GeoEntry_Group[1..3] (可选)      │
├──────────────────────────────────────┤  ← Align64
│  String Pools (按版本组顺序)          │  N 组 × (每版本 D 个 Pool)
├──────────────────────────────────────┤  ← Align64
│  Metadata (TLV 条目)                 │  版本名/字段列表/描述
└──────────────────────────────────────┘
```

- 所有段起始于 **64 字节边界**（`Align64`），支持 mmap 直接访问。
- 所有多字节值为 **Little-Endian**。
- **CRC32**：整个文件范围 CRC32（IEEE 0xEDB88320 多项式），计算时 CRC 自身 4 字节填零。
- 段顺序恒定：Header → V4 Jump → V4 Nodes → V6 Jump → V6 Nodes → IPRow → GeoEntry → Pools → Metadata。

---

## 2. Header（192 字节）

Magic `QZDB`（4 字节 ASCII），固定 192 字节。

| 偏移 | 大小 | 类型 | 字段名 | 说明 |
|------|------|------|--------|------|
| 0 | 4 | ASCII | **Magic** | `QZDB` |
| 4 | 1 | uint8 | **HeaderVersion** | 统一写入值固定为 `1` |
| 5 | 1 | uint8 | Reserved | 填 0 |
| 6 | 2 | uint16 LE | **VersionMask** | 文件中包含的版本位掩码（bit0=std, bit1=ult, bit2=asn, bit3=max） |
| 8 | 2 | uint16 LE | **Flags** | 功能标志位（见 §2.1） |
| 10 | 1 | uint8 | **V4JumpBits** | V4 跳表位宽，固定 `16` |
| 11 | 1 | uint8 | **V6JumpBits** | V6 跳表位宽，动态 `16 ~ 20`（最低 16） |
| 12 | 1 | uint8 | **PoolCount** | 主版本组（group 0）的维度数（=字段数） |
| 13 | 1 | uint8 | **PoolIdxSize** | 池索引字节宽度：`2`(≤65535) 或 `3`(≤16M) |
| 14 | 2 | uint16 LE | **GeoCount** | 主版本组 GeoEntry 条数（兜底字段；权威计数见 §5 GroupMetadataTable） |
| 16 | 4 | uint32 LE | CRC32 | 整个文件 CRC32（计算时这 4 字节填 0） |
| 20 | 4 | uint32 LE | **RowCount** | IPRow 总条数（含 #0 空行） |
| 24 | 4 | uint32 LE | V4RecordCount | V4 CIDR 条数（=0 则无 V4） |
| 28 | 4 | uint32 LE | V6RecordCount | V6 CIDR 条数（=0 则无 V6） |
| 32 | 4 | uint32 LE | BuildDate | 编译日期，格式 `yyyyMMdd` |
| 36 | 4 | uint32 LE | HeaderSize | 固定 `192` |
| 40 | 8 | uint64 LE | **OffsetRowSchema** | ROW_SCHEMA 段偏移（v5+，旧版为 0） |
| 48 | 8 | uint64 LE | **OffsetGroupSchema** | GROUP_SCHEMA 段偏移（v5+，旧版为 0） |
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
| 160 | 4 | uint32 LE | **IPRowSize** | IPRow 行字节宽（当前 6 = 2×uint24；v2 为 9 含 usage_type） |
| 164 | 4 | uint32 LE | **GeoEntryGroupCount** | GeoEntry 版本组数（1~4） |
| 168 | 24 | uint48×4 | **GeoEntryOffsets[4]** | 每组 GeoEntry 相对 OffsetGeoEntries 的偏移（uint48 LE × 4） |
| 192 | — | — | 结束 | Header 固定 192 字节 |

### 2.1 Flags 定义

```
bit0: hasV4       — 包含 V4 数据
bit1: hasV6       — 包含 V6 数据
bit2: hasMetadata — 包含 Metadata 段（生成器无条件设置）
bit3: reserved
bit4: v4Node24    — V4 节点使用 24 位编码（6 字节/节点）
bit5: v6Node24    — V6 节点使用 24 位编码（6 字节/节点）
bit6~15: reserved
```

### 2.2 节点 24 位编码（节点数 < 8,388,608 且 RowCount < 8,388,608 时启用）

- 每个 Trie 节点占 **6 字节**：left(3B) + right(3B)。
- 高位 sentinel：`0x800000` 表示叶子，低 23 位为 `row_id`（+= 0x80000000 还原为 32 位 sentinel）。
- 未启用时每节点占 **8 字节**：left(4B) + right(4B)，sentinel = `0x80000000`。

---

## 3. V4 Jump Table（跳表）

固定 `65536 × 4 = 256KB`，`uint32 LE`。

条目由 sentinel bit（`0x80000000`）区分三种情况：

| 值范围 | 含义 |
|--------|------|
| `0x00000000` | 该 /16 桶无数据，查询立即返回 NOT_FOUND |
| `0x00000001 ~ 0x7FFFFFFF` | **内节点索引**：继续在该节点进行 Trie Walk |
| `0x80000001 ~ 0xFFFFFFFF` | **叶子**：低 31 位为 `row_id` |

查询时取 IP 高 16 位直接索引，跳过前 16 层二叉树检索（O(1) 跳表）。

---

## 4. Trie Nodes（Binary Trie）

- 顺序扁平布局，节点按数组下标访问，CPU 缓存友好。
- 遍历使用最长前缀匹配（LPM）：从 Jump Table 定位子树根，逐 bit（高位到低位）走向 left/right 子节点，遇 sentinel 即命中 `row_id`。
- V6 同理：取 IP 高 `v6_jump_bits` 位索引 V6 Jump Table，再在子树内逐 bit 遍历剩余位。

---

## 5. IPRow Array（★ QZDB 新增间接层）

`IPRowSize` 字节/行（`RowCount` 行），每行：

| 偏移 | 大小 | 字段 |
|------|------|------|
| 0 | 3 | geo_id (uint24 LE) |
| 3 | 3 | asn_id (uint24 LE) |
| 6 | 3 | usage_type_id (uint24 LE，仅当 IPRowSize ≥ 9) |

Trie 叶子 `row_id` → `IPRow[row_id]` → 得到 `{geo_id, asn_id, usage_type_id}`。

---

## 6. GeoEntry Section（★ 多版本组架构）

### 6.1 GroupMetadataTable（位于 OffsetGeoEntries）

```
byte    groupCount            (uint8)
循环 groupCount 次:
  byte    fieldCount          (uint8)   — 该组字段数
  uint32  entryCount          (uint32 LE) — 该组 GeoEntry 条数
  uint16  dimensionMask       (uint16 LE) — 维度掩码：bit0=geo, bit1=asn, bit2=usage_type
```

其余 `groupCount` 组的 GeoEntry 区起始偏移由 Header 的 `GeoEntryOffsets[4]`（uint48 LE）给出。

### 6.2 GeoEntry 存储

每组 GeoEntry 按 `GROUP_SCHEMA` 动态宽度紧凑排列：

- 每个字段宽度由 `GROUP_SCHEMA` 决定（`poolIdxSize` 或原生类型 4/8 字节）。
- **原生标量字段**（如 `longitude`/`latitude`/`asn`/`zip`）：以内联定宽二进制存储，跳过 String Pool。
- **字符串字段**：存储 String Pool 索引（uint，宽度 = `poolIdxSize`），查询时查 Pool 还原字符串。
- 第 #0 条始终为空条目（全 0）。

### 6.3 GROUP_SCHEMA（位于 OffsetGroupSchema，v5+）

```
uint16  groupCount
循环 groupCount 次:
  uint16  groupId
  uint16  fieldCount
  uint32  entryCount
  uint32  stride
  uint32  flags (保留)
  循环 fieldCount 次:
    uint16  fieldId
    uint8   width
    uint8   fieldFlags   — bit0=NATIVE, bit1-2=TYPE(0=int,1=float,2=varint)
    uint32  offset
    uint32  poolSectionId (原生字段未使用)
```

> SDK 应优先读取 `GROUP_SCHEMA`；若 `OffsetGroupSchema == 0`，回退到 `fieldCount × poolIdxSize` 的均匀宽度布局。

---

## 7. String Pools（位于 OffsetPools）

按版本组顺序，每组包含若干 Pool（每字段一个，原生字段无 Pool）。

每个 Pool 布局：

| 偏移 | 大小 | 字段 |
|------|------|------|
| 0 | 4 | Count (int32 LE) |
| 4 | 4 | DataLength (int32 LE) — **当 ROW_SCHEMA 存在时（v5+）有此 4 字节头** |
| 8 | (Count+1)×4 | Offsets 数组 (int32 LE) |
| ... | variable | UTF-8 字符串数据 |

字符串索引 `i` 为 `data[offsets[i] .. offsets[i+1]-1]`。索引 0 始终为空字符串 `""`。

> **注意**：SDK 解析 Pool 时，若 `OffsetRowSchema > 0`，读取 Count 后需额外跳过 4 字节 DataLength 头，再读 Offsets。

---

## 8. Metadata（位于 OffsetMeta，TLV）

生成器无条件写入（`flags & 4`）。条目格式：

| 偏移 | 大小 | 字段 |
|------|------|------|
| 0 | 1 | type (uint8) |
| 1 | 1 | reserved |
| 2 | 2 | length (uint16 LE) |
| 4 | length | UTF-8 value |

已知 type：

| type | 含义 | 示例 |
|------|------|------|
| 1 | 版本名（逗号分隔的版本列表） | `std,ult,max` |
| 2 | **字段名列表**（竖线 `\|` 分隔，顺序 = 主组字段顺序） | `continent\|country\|province\|city\|isp\|...` |
| 3 | 描述 | `qqzeng-ip ... edition 2026-07` |
| 4 | 主版本号 | `std` |

SDK 优先用 type-2 字段名列表（与 `groupFieldCounts[0]` 数量一致时采用），否则回退为 `field_0, field_1, ...`。

---

## 9. 查询算法复杂度

| 指标 | 复杂度 | 说明 |
|------|--------|------|
| 查找时间 | O(W − K) | W=IP 位宽(32/128)，K=跳表位宽(16)。IPv4 经跳表跳过前 16 层，通常 < 16 次 bit 判断 |
| 空间 | 极小 | 节点 6~8 字节/个；百万级子网全局库索引 < 20MB |
| 堆分配 | O(1) | 查询路径零堆分配：C/Go/Rust 走 mmap 零拷贝直接返回指针；JVM/.NET 类载入后只读视图，查询态无锁 |

---

## 10. CRC32

全文件 CRC32，IEEE 多项式 `0xEDB88320`。各 SDK 实现：

- C / Node / 自实现表：`0xEDB88320`
- Go：`crc32.ChecksumIEEE`
- Python：`zlib.crc32`
- PHP：`hash('crc32b', ...)`
- Java：`java.util.zip.CRC32`

校验时把 Header 字节 [16..20) 的 CRC32 字段清零后再算，与存储值比较。

---

## 11. 字段名命名约定（跨语言一致）

所有 SDK 以 Python 为参考实现，字段名必须完全一致：

| 用途 | 统一字段名 |
|------|-----------|
| 国家英文 | `country_english` |
| 自治域名称 | `asn_org` |
| 自治域域名 | `asn_domain` |
| 自治域编号 | `asn` |
| 经度/纬度 | `longitude` / `latitude`（输出统一 6 位小数） |

> 跨语言交叉验证依赖字段名完全一致，任何同义不同名都会导致管道输出解析失败。
