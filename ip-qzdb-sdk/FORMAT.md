# QZDB 二进制文件格式规范 (Binary Format Specification)

> **版本**: QZDB Format v1 (HeaderVersion = 1)  
> **定位**: 纯离线、高性能 IP 地理位置数据库二进制文件规范  
> **适用**: 8 语言 SDK (C / Go / Java / .NET / Node.js / PHP / Python / Rust) 及官方构建器

---

## 一、 核心架构与设计原则

QZDB 采用 **Multi-ID PATRICIA Trie + IPRow 间接层** 架构，实现单文件多版本共享前缀树的高效存储：

```
IP 输入 ──> Jump Table (O(1) 预检索) ──> Trie Walk (LPM 最长前缀匹配)
                │
                └──> row_id (1-based 行号)
                         │
                         └──> IPRow[row_id] ──> { geo_id, asn_id, usage_id }
                                                  ├─> GeoEntry_STD[geo_id] ──> Pool_STD ──> 字段解包
                                                  ├─> GeoEntry_ULT[geo_id] ──> Pool_ULT ──> 字段解包
                                                  ├─> GeoEntry_ASN[asn_id] ──> Pool_ASN ──> 字段解包
                                                  └─> GeoEntry_MAX[geo_id] ──> Pool_MAX ──> 字段解包
```

### 关键设计特征
1. **完全自描述**：Header 包含所有段的绝对偏移量（`uint64 LE`），无需任何外部配置。
2. **64 字节对齐（Align64）**：所有数据段起始物理偏移满足 `(offset & 63) == 0`，原生支持 OS 级 `mmap` 零拷贝极速寻址。
3. **小端字节序（Little-Endian）**：所有多字节整数统一为 LE 字节序。
4. **全文件 CRC32 完整性防护**：文件尾或加载时基于全文件校验，Fail-Closed 拒绝损坏/截断数据。

---

## 二、 文件整体物理布局

```
┌─────────────────────────────────────────────────────────────┐
│ Header (192 字节, 固定结构)                                 │
├─────────────────────────────────────────────────────────────┤  ← 64字节对齐
│ V4 Jump Table (固定 256KB = 65536 × 4B, 0=无V4)              │
│ V4 Trie Nodes (N4 × 8B 或 6B 压缩节点, 0=无V4)               │
├─────────────────────────────────────────────────────────────┤  ← 64字节对齐
│ V6 Jump Table (2^V6JumpBits × 4B, 通常 16~20bit, 0=无V6)     │
│ V6 Trie Nodes (N6 × 8B 或 6B 压缩节点, 0=无V6)               │
├─────────────────────────────────────────────────────────────┤  ← 64字节对齐
│ IPRow Array (RowCount × Stride, 间接层映射表)               │
├─────────────────────────────────────────────────────────────┤  ← 64字节对齐
│ GeoEntry Section (多版本组 Geo 数据)                         │
│ ├─ GroupMetadataTable (版本组元信息表)                       │
│ └─ GeoEntry_Groups... (各版本组行数据)                       │
├─────────────────────────────────────────────────────────────┤  ← 64字节对齐
│ String Pools (各版本组 × 各字段 字典池数据)                  │
├─────────────────────────────────────────────────────────────┤  ← 64字节对齐
│ Metadata Section (TLV 条目: 版本名/字段列表/构建描述)       │
└─────────────────────────────────────────────────────────────┘
```

---

## 三、 Header 结构（192 字节）

| 偏移 (Offset) | 长度 (Bytes) | 类型 | 字段名 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `0` | 4 | ASCII | **Magic** | 固定为 `QZDB` |
| `4` | 1 | uint8 | **HeaderVersion** | 固定为 `1` |
| `5` | 1 | uint8 | Reserved | 保留，填 0 |
| `6` | 2 | uint16 LE | **VersionMask** | 版本档位 one-hot 掩码 (bit0=std, bit1=asn, bit2=pro, bit3=max, bit4=ult) |
| `8` | 2 | uint16 LE | **Flags** | 功能标志位 (bit0:hasV4, bit1:hasV6, bit2:hasMeta, bit4:v4Node24, bit5:v6Node24) |
| `10` | 1 | uint8 | **V4JumpBits** | IPv4 跳表位宽，固定为 `16` |
| `11` | 1 | uint8 | **V6JumpBits** | IPv6 跳表位宽，动态估算选择 `8 ~ 20`（常见 16） |
| `12` | 1 | uint8 | **PoolCount** | 主版本组（Group 0）字段数 |
| `13` | 1 | uint8 | **PoolIdxSize** | 池索引字节宽度：`2`(≤65535) 或 `3` |
| `14` | 2 | uint16 LE | **GeoCount** | 主版本组 GeoEntry 备用计数 |
| `16` | 4 | uint32 LE | **CRC32** | 全文件 CRC32 校验码（计算时此 4 字节填 0） |
| `20` | 4 | uint32 LE | **RowCount** | IPRow 总行数（含 #0 保留空行） |
| `24` | 4 | uint32 LE | V4RecordCount | IPv4 CIDR 条数 |
| `28` | 4 | uint32 LE | V6RecordCount | IPv6 CIDR 条数 |
| `32` | 4 | uint32 LE | BuildDate | 编译日期，格式 `yyyyMMdd` |
| `36` | 4 | uint32 LE | HeaderSize | 固定为 `192` |
| `40` | 8 | uint64 LE | **OffsetRowSchema** | ROW_SCHEMA 段物理偏移 |
| `48` | 8 | uint64 LE | **OffsetGroupSchema**| GROUP_SCHEMA 段物理偏移 |
| `56` | 8 | bytes | Reserved | 保留，填 0 |
| `64` | 8 | uint64 LE | **OffsetV4Jump** | V4 Jump Table 物理偏移 |
| `72` | 8 | uint64 LE | **OffsetV4Nodes** | V4 Trie Nodes 物理偏移 |
| `80` | 8 | uint64 LE | **OffsetV6Jump** | V6 Jump Table 物理偏移 |
| `88` | 8 | uint64 LE | **OffsetV6Nodes** | V6 Trie Nodes 物理偏移 |
| `96` | 8 | uint64 LE | **OffsetIPRow** | IPRow Array 物理偏移 |
| `104` | 8 | uint64 LE | **OffsetGeoEntries** | GeoEntry Section 起始偏移 |
| `112~135` | 24 | uint64 LE | Reserved | 扩展段保留偏移 |
| `136` | 8 | uint64 LE | **OffsetPools** | String Pools 字典池起始偏移 |
| `144` | 8 | uint64 LE | **OffsetMeta** | Metadata Section 物理偏移 |
| `152` | 4 | uint32 LE | **V4NodeCount** | V4 Trie 节点数 |
| `156` | 4 | uint32 LE | **V6NodeCount** | V6 Trie 节点数 |
| `160` | 4 | uint32 LE | **IPRowSize** | IPRow 单行字节宽（通常 4 或 6） |
| `164` | 4 | uint32 LE | **GeoEntryGroupCount** | GeoEntry 版本组数（1 ~ 4） |
| `168` | 24 | uint48 × 4 | **GeoEntryOffsets** | 每组 GeoEntry 相对 OffsetGeoEntries 的偏移 |

---

## 四、 核心数据段规范

### 1. Jump Table 与 Trie 节点
- **V4 Jump Table**：固定 65,536 个槽位（覆盖 IPv4 前 16 位），单槽 4 字节（uint32 LE）。
  - 最高位 `MSB (0x80000000)` 为 1 表示直接命中**叶子行号**（`val & 0x7FFFFFFF` = `row_id`）；
  - 为 0 且非 0 表示指向 **Trie 内部节点索引**；为 0 表示未命中。
- **Trie 节点结构 (8 字节标准 / 6 字节压缩)**：
  ```c
  struct TrieNode {
      uint32 LE left;   // 0-bit 分支（MSB=1 为叶子 row_id，否则为子节点索引）
      uint32 LE right;  // 1-bit 分支（MSB=1 为叶子 row_id，否则为子节点索引）
  };
  ```

### 2. IPRow 间接层
Trie 遍历得到的 `row_id` 映射到 IPRow：
- `IPRow[row_id]` 存储多维度 ID 组合（如 `geo_id (uint24)` + `asn_id (uint24)`）。
- 行号 `0` 恒为保留空行（返回 NOT_FOUND）。

### 3. GeoEntry 数据与 String Pools
- **GroupMetadataTable** 声明各版本组的字段数、字段偏移、字段宽度（1~3 字节池索引）及原生标量类型。
- **String Pools**：所有字段字符串全局去重存储，通过两级物理偏移 O(1) 解析为 UTF-8 字符串，热路径支持零堆分配解码。

### 4. Metadata TLV 结构
- Type 1：版本与格式标识（如 `"std" / "ult"`）
- Type 2：字段名称列表（逗号分隔或动态数组）
- Type 3：生成器与版权描述文本
- Type 4：版本档次覆盖声明
