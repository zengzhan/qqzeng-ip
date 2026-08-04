# asn_china ASN 回落 56554 根因分析与修复报告

> 被测文件：`qqzeng_ip_asn_china.qzdb`（单 ASN 组，`ip_row_size=4`，CRC 有效）
> 权威对照（**最真实数据**，202608 发版目录）：`range/qqzeng_ip_asn_china_range.csv`
>   - 123134 行，12 列；**106103 行有真实 ASN**，17031 行 ASN 为空（合法回落 56554）
>   - 其中 IPv4 真实 ASN 行 39158，IPv6 真实 ASN 行 66945
> 数据目录：`/Users/zengxiangzhan/ZengData/qqzeng-data/202608/ip/asn/china`
>
> 说明：`temp_work/qqzeng_ip_asn_china.qzdb` 与本目录 `qzdb.zip` 解压出的 qzdb **sha256 完全相同**
> （`03e25cccb62e010b0ef0919d31acf7ee234ca164c84690abdcd2f1ac79abdd4c`），即同一份真实数据库；
> 此前所有验证本质上已针对真实数据库。以下"真实数据验证"使用本目录的 `range.csv` 作为权威真值。

## 一句话结论

**是解析 Bug，不是数据问题。数据构建流程完全正确，qzdb 数据完好无损。**
真正的根因是 SDK 的 `find_uint` / `lookup_row_id_*` **没有剥离 trie 结果的高位哨兵位 `0x80000000`**，
导致 IP-Row 索引到错误偏移，ASN 大面积解析错误（客户反馈的"解析不对 / 回落 56554"即此）。

第二个 bug（ROW_SCHEMA 字节偏移）是**潜伏性**的：对 asn_china 这一种文件巧合对齐、不会触发，
但对其他布局（含 usage 字段、不同字段顺序或 stride）会崩到 56554。已在全部 8 个 SDK 修正以防后患。

---

## 根因一（真正触发本次 56554）：哨兵位泄漏（Bug2）

QZDB 的 PATRICIA trie 叶子命中时，结果最高位会被置 `SENTINEL = 0x80000000` 作为"命中"标记，
真实 0 基 `row_id` 需要 `& SENTINEL_MASK_31 (0x7FFFFFFF)` 才能拿到。

- 修复前的 `find_uint` 直接拿 `row_id = _trie_walk_v4(ip)` 去 `_resolve_row_id`，**没剥离高位**。
- 实测：`_trie_walk_v4(16844800)` 返回 `0x80000002`，不剥离则拿 `0x80000002` 当 row_id 去读 IP-Row → 偏移错乱 → ASN 全错。

**隔离验证（决定性证据）：**
| 场景 | 真实 ASN 段(39157) | EXACT | COLLAPSE(→56554) | OTHER |
|---|---|---|---|---|
| 修复后（剥离哨兵位） | 39157 | **39157 (100%)** | 0 | 0 |
| 仅把 `find_uint` 哨兵剥离去掉 | 39157 | 812 | 0 | **38345** |

去掉哨兵剥离后 39157 段仅 812 正确、38345 全错 —— 完美复现"解析不对"，**确证哨兵位泄漏是根因**。
（客户现场体现为"回落 56554"：错读偏移命中 `asn_id=0` → 默认 ASN 56554。）

## 根因二（潜伏，已全部修正）：ROW_SCHEMA 字节偏移错误（Bug1）

权威规范（C# 构建器 `QZDBReader.cs` 的 `ParseRowSchema`，Python 修复已对齐）：

```
byte[sp+0]   = fieldCount
byte[sp+1]   = stride (== ip_row_size)
bytes[sp+2..3] = reserved
其后 fieldCount 条 4 字节记录，从 sp+4 起：{ fieldId(1) | width(1) | fieldOffset(1) | flags(1) }
fieldId: 0=geo, 1=asn, 2=usage
```

分发的 7 个 SDK（及 `TestRunner/` 的 C# 副本）用了错误的 "Java-compatible" 布局：
`fieldCount` 在 `sp+5`、`widths` 在 `sp+9+i`。

**对 asn_china 为何没崩：** 该文件 ROW_SCHEMA 字节为 `02 04 00 00 00 02 00 00 01 02 02`
- 旧偏移：`d[sp+5]=2`(fcount)，`widths=d[sp+9],d[sp+10]=[2,2]` → geo=2, asn=2
- 新偏移：`d[sp]=2`(fcount)，`d[sp+1]=4`(stride)，`fid=0→w=2, fid=1→w=2` → geo=2, asn=2

两者数值**完全相同**，所以本文件巧合正确。但这是巧合：一旦字段顺序/宽度/stride 不同（例如含 usage、
或 `ip_row_size=6`、或字段重排），旧偏移就会算出错误宽度 → 解析崩塌。全量扫描 17 个 qzdb 文件，
旧/新偏移对本文件数值均一致，印证此文件不是 Bug1 的触发点。

---

## 验证结果

### A. 真实数据权威验证（202608 发版 `range.csv`，v4+v6 分流）

用**修复后的 Python SDK**，把真实 qzdb 对真实 `range.csv`（最真实数据）做全量比对：

| 维度 | 真实 ASN 行 | 精确匹配 | 命中率 |
|---|---|---|---|
| IPv4 | 39158 | 39158 | **100.0000%** |
| IPv6 | 66945 | 66945 | **100.0000%** |
| **合计** | **106103** | **106103** | **100.0000%** |

- COLLAPSE(→56554) = **0**、OTHER 错位 = **0**、qzdb 返回 None = **0**
- 17031 行源数据本身 ASN 为空 → 合法回落默认 56554（非 bug）

**结论：真实 qzdb 与真实 range.csv 完全一致，数据 100% 完好且版本同步；56554 问题是 SDK 解析 Bug，已修复。**

### B. 隔离测试（决定性证据，针对哨兵位泄漏）

| 场景 | 真实 ASN 段(39157) | EXACT | COLLAPSE(→56554) | OTHER |
|---|---|---|---|---|
| 修复后（剥离哨兵位） | 39157 | **39157 (100%)** | 0 | 0 |
| 仅把 `find_uint` 哨兵剥离去掉 | 39157 | 812 | 0 | **38345** |

去掉哨兵剥离后 39157 段仅 812 正确、38345 全错 → 完美复现"解析不对"，**确证哨兵位泄漏是根因**。

### C. 跨语言与编译校验

| 验证项 | 结果 |
|---|---|
| Python SDK（修复后）真实数据全量 | **106103/106103 精确匹配，0 崩塌** |
| Node.js SDK（修复后）真实数据全量 | **39157/39157（v4）精确匹配，0 崩塌**（第二语言端到端通过）|
| 全部 17 个 qzdb 旧/新偏移对比 | 对 asn_china 类文件数值一致（Bug1 潜伏，未触发）|
| 语法/编译校验 | node `--check`✓ php `-l`✓ c `gcc -fsyntax-only`✓ rust `cargo check`✓ |

### D. 跨语言真实数据回归（真实 qzdb + 真实 range.csv → truth.tsv）

除 Python 外，对 **Node.js** 和 **C** 两个 SDK 跑真实数据全量回归（统一真值 `truth.tsv`，v4/v6 自动路由）：

| SDK | 真实 ASN 段 | 精确匹配 | 命中率 |
|---|---|---|---|
| Node.js（修复后，`find(ip)`） | 106103 | **106103** | **100%** |
| C（修复后，`qzdb_find`） | 106103 | **106103** | **100%** |

- 两者 COLLAPSE(→56554)=0、OTHER=0、None=0，且 `row_geo_width=2 row_asn_width=2`（C 实测）。
- 结论：**Bug2 哨兵修复在 3 个独立运行时（Python/Node.js/C）对真实数据均 100% 通过**。

### E. ROW_SCHEMA 潜伏 bug 回归测试（守卫 Bug1 修复）

新增 `multi-lang/test_row_schema_regression.py`，用真实 SDK + 双公式对比证明 Bug1 修复必要：

- 3 字段布局（geo2+asn2+usage2，stride6）：NEW→(2,2,2) 正确；OLD→None（拒绝，回落错误默认）。**DIVERGE**
- asn_china 原 2 字段布局：NEW→(2,2,0)，OLD→(2,2,0)。**COINCIDE**（解释为何原文件不触发）
- 字段顺序打乱的 2 字段布局：NEW→(2,2,0) 正确；OLD→None。**DIVERGE**
- 真实 SDK 加载补丁过的真实 qzdb（ROW_SCHEMA 顺序打乱）→ `row_geo_width=2 row_asn_width=2` 正确。

**PASS**：修复正确且必要；一旦偏移被回退到 sp+5/sp+9，该测试即失败，形成回归守卫。

## 已修复文件清单（共 9 处）

解析逻辑修正（哨兵剥离 + ROW_SCHEMA 规范布局）：

1. `multi-lang/python/qzdb.py` — 上一轮已修（本次复验通过）
2. `multi-lang/c/qzdb_searcher.c`
3. `multi-lang/go/qzdb/qzdb.go`
4. `multi-lang/java/src/main/java/qzdb/QzdbSearcher.java`
5. `multi-lang/php/QzdbSearcher.php`
6. `multi-lang/nodejs/qzdb.js`
7. `multi-lang/rust/src/lib.rs`
8. `multi-lang/netcore/QzdbSearcher.cs`（分发版，同源损坏，**非**权威 QZDBReader.cs）
9. `TestRunner/QzdbSearcher.cs`（V18 真值校验器的 C# 副本，同源损坏，已修）

> 注：`TestRunner/` 用 `IPDBSearcherV18` 跑真实 CSV 真值校验；其 `QzdbSearcher.cs` 是独立副本，需随 SDK 一起修。
> `go build ./...` 现报 `main redeclared`（main.go 与 batch_main.go 同包重复声明），属**预先存在**的项目结构问题，
> 与本轮 `qzdb.go` 的修改无关，建议另行处理（拆分 package 或移除其一）。

## 测试产物（本轮新增）

- `multi-lang/test_row_schema_regression.py` — **ROW_SCHEMA 回归测试**（Bug1 守卫，见 §E），真实 SDK + 双公式对比，PASS。
- `multi-lang/nodejs/qzdb.js` 已含修复；真实数据回归脚本（一次性）：`/tmp/node_real_regress.cjs`。
- C 真实数据回归 driver（一次性）：`/tmp/c_qzdb_driver.c`（编译 `gcc -O2 -I c c/qzdb_searcher.c /tmp/c_qzdb_driver.c -o drv -lpthread -lm`，结果 106103/106103）。
- 统一真值：`/tmp/real_asn_china/truth.tsv`（由 `range.csv` 导出，`ip \t asn \t family`，106103 行），供各语言回归比对。
- 真实数据解压目录：`/tmp/real_asn_china/{qzdb,range,cidr}`（来自 `202608/ip/asn/china` 三件套 zip）。

## 结论与建议

- **数据侧无需改动**：真实 qzdb（202608 发版）与权威 `range.csv` 真实 ASN 行 **106103/106103 精确匹配、0 崩塌**，
  数据 100% 完好、版本同步，构建流程正确。
- **问题在 SDK 解析实现**：多语言 SDK 的 trie 结果高位哨兵位处理 + ROW_SCHEMA 解析偏移存在缺陷。
- **行动**：将 8 个 SDK（+TestRunner）的修复合入发版；建议对所有语言 SDK 跑 `range.csv`（或 `temp_china_v4.txt`）
  比对回归，并补充一个"非本文件形态"（含 usage 字段 / 不同 stride）的 qzdb 作为 ROW_SCHEMA 偏移回归用例，防止潜伏 bug 复发。
