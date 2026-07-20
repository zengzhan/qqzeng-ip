# Task Plan: V18→V20 SDK Migration (All 8 Languages)

## Goal
Update all 8 multi-language IP geolocation SDKs from V18 (QZ18) format to V20 (QZ20) format using PATRICIA Trie + IPRow + multi-GeoEntry groups. Test each SDK against actual V20 QZDB files, cross-validate with CSV source data, then sync clean code to GitHub.

## V20 Format Summary (from QZDBv20Builder.cs + QZDBv20Reader.cs)

**Header (192 bytes):**
| Offset | Size | Field |
|--------|------|-------|
| 0-3 | 4 | Magic "QZ20" |
| 4 | 1 | HeaderVersion (4) |
| 5 | 1 | Reserved |
| 6-7 | 2 | VersionMask (bit0=std, bit1=ult, bit2=asn, bit3=max) |
| 8-9 | 2 | Flags (bit0=hasV4, bit1=hasV6, bit2=hasMetadata) |
| 10 | 1 | V4JumpBits (16) |
| 11 | 1 | V6JumpBits (dynamic) |
| 12 | 1 | PoolCount (primary group field count) |
| 13 | 1 | PoolIdxSize (2 or 3) |
| 14-15 | 2 | GeoCount (ushort, primary group entry count) |
| 16-19 | 4 | CRC32 |
| 20-23 | 4 | RowCount (uint32) |
| 24-27 | 4 | V4RecordCount |
| 28-31 | 4 | V6RecordCount |
| 32-35 | 4 | BuildDate (yyyyMMdd) |
| 36-39 | 4 | HeaderSize (192) |
| 40-63 | 24 | Reserved |
| 64-71 | 8 | OffsetV4Jump |
| 72-79 | 8 | OffsetV4Nodes |
| 80-87 | 8 | OffsetV6Jump |
| 88-95 | 8 | OffsetV6Nodes |
| 96-103 | 8 | OffsetIPRow |
| 104-111 | 8 | OffsetGeoEntries |
| 112-119 | 8 | OffsetColProj (0) |
| 120-127 | 8 | OffsetReverseIdx (0) |
| 128-135 | 8 | OffsetPoolSummary (0) |
| 136-143 | 8 | OffsetPools |
| 144-151 | 8 | OffsetMeta |
| 152-155 | 4 | V4NodeCount |
| 156-159 | 4 | V6NodeCount |
| 160-163 | 4 | IPRowSize (6) |
| 164-167 | 4 | GeoEntryGroupCount |
| 168-191 | 24 | GeoEntryOffsets[4] (uint48 LE × 4, relative to OffsetGeoEntries) |

**Sections (all 64-byte aligned):**
1. V4 Jump Table: 65536 × uint32 LE = 256KB
2. V4 Trie Nodes: V4NodeCount × 8 bytes (each: Left uint32 + Right uint32)
3. V6 Jump Table: 2^V6JumpBits × uint32 LE
4. V6 Trie Nodes: V6NodeCount × 8 bytes
5. IPRow Array: RowCount × 6 bytes (each: geo_id uint24 LE + asn_id uint24 LE)
6. GeoEntry Section: GroupMetadataTable + aligned + GeoEntry data per group
   - GroupMetadata: groupCount(byte) + for each: fieldCount(byte), entryCount(uint32), dimensionMask(ushort)
   - GeoEntry: entryCount × fieldCount × poolIdxSize bytes
7. String Pools: Per-group, per-field. Each pool: count(uint32) + offsets[count+1](uint32) + UTF-8 data
8. Metadata: TLV entries (type=1 version, type=2 field_names, type=3 description, type=4 primary_version)

**Trie Walk V4:**
```
hi16 = ip >> 16
ptr = jump[hi16]
if ptr == 0: return 0
if ptr & 0x80000000: return ptr & 0x7FFFFFFF (leaf = rowId)
idx = ptr, suffix = (ip & 0xFFFF) << 16
loop:
  bit = suffix >> 31
  child = nodes[idx*2 + bit]  // 0=left, 1=right
  if child == 0: return 0
  if child & 0x80000000: return child & 0x7FFFFFFF
  idx = child, suffix <<= 1
```

**Trie Walk V6:**
```
shift = 128 - v6JumpBits
idxJump = (uint)(ip >> shift)
ptr = jump[idxJump]
// same as V4 from here, walking remaining bits (depth starts at v6JumpBits)
```

**Geo Resolution:**
1. rowId from Trie
2. IPRow[rowId] → (geoId, asnId)
3. dimensionMask[groupIndex]: bit0=geo, bit1=asn → select ID
4. GeoEntry[groupIndex][entryId] → poolIdx[fieldCount]
5. For each field: pool[groupIndex][i][poolIdx] → string

## V20 Test Location
- `/Users/zengxiangzhan/ZengData/发行版/2026-07/` contains V20 QZDB files (*_v20.qzdb) and CSV exports
- All 8 versions (std/ult/asn/max) × 2 regions (china/global) = 16 files
- Also has *_range.zip, *_cidr.zip, *_qzdb.zip for cross-validation

## Phases

### Phase 1: Python V20 SDK (Reference Implementation)
- [ ] Write V20 Python SDK at `multi-lang/python/qzdb_v20.py`
- [ ] Support: load V20 file, parse header, trie walk V4+V6, IPRow resolve, multi-GeoEntry resolve, pool loading, metadata reading
- [ ] API: keep same V18 pattern: `QzdbSearcher(db_path)` → `find(ip_str)` → `GeoInfo` / `find_str(ip_str)` → `str`
- [ ] Test against actual V20 QZDB files from 发行版
- [ ] Cross-validate results against CSV

### Phase 2: Parallel Port to All 7 Languages
- [ ] Go V20 SDK: `go/qzdb/qzdb_v20.go`
- [ ] C# V20 SDK: `csharp/QzdbSearcherV20.cs`
- [ ] Node.js V20 SDK: `nodejs/qzdb_v20.js`
- [ ] PHP V20 SDK: `php/QzdbSearcherV20.php`
- [ ] C V20 SDK: `c/qzdb_searcher_v20.c` + `c/qzdb_searcher_v20.h`
- [ ] Java V20 SDK: `java/src/main/java/com/qqzeng/ip/QzdbSearcherV20.java`
- [ ] Rust V20 SDK: `rust/src/lib_v20.rs`

### Phase 3: Testing & Cross-Validation
- [ ] Run each SDK against V20 QZDB files, verify results match
- [ ] Cross-validate CSV source vs QZDB results for all versions
- [ ] Fix any discrepancies found
- [ ] Performance benchmark (basic QPS check)

### Phase 4: Sync to GitHub
- [ ] Copy clean code (no data files) to GitHub directory
- [ ] Update test scripts and run_all_tests.sh
- [ ] Update FORMAT.md with V20 spec
- [ ] Final verification

## Key Decisions
- Keep singleton pattern (V18 pattern): `getInstance()` / `Instance` / `instance()`
- Use memory-mapped files for higher performance where available (Go, Rust, Java NIO)
- For Python/Node.js/PHP, read entire file into memory
- Field names read dynamically from Metadata section (type=2) — no hardcoded field lists needed
- Support all 4 version groups (std/ult/asn/max) with groupIndex parameter
- PoolIdxSize determined from header byte [13]
- CRC32 verification optional but available

## Status
**Currently in: Phase 2 (Parallel Ports)** - Python reference complete and verified. 7 language ports running in parallel background tasks (Go, Rust, Node.js, C#, PHP, Java, C). Preparing cross-validation framework.

## Errors Encountered
- None yet
