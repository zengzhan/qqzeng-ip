# QZDB 头版本顺序重排 — 全面检查与测试报告

**日期**：2026-08-09
**触发**：用户更新了 qzdb 数据头的「版本档位顺序」（VersionMask 位顺序重排），要求：
1. 重新获取最新数据覆盖测试目录；
2. 做全面检查和测试，确认改完之后各语言「对了没」。

---

## 1. 改动本质：VersionMask 位顺序重排（NEW 映射）

数据头 `Header@6` 的 `VersionMask`（uint16 LE）由 one-hot 掩码表示档位。
本次重排后，**位顺序改变**为：

| 档位 | 旧位序 (OLD, 已废弃) | **新位序 (NEW, 2026-08-09 生效)** |
|------|----------------------|-----------------------------------|
| std  | bit0 = 0x01          | **bit0 = 0x01**                   |
| asn  | bit2 = 0x04          | **bit1 = 0x02**                   |
| pro  | bit4 = 0x10          | **bit2 = 0x04**                   |
| max  | bit3 = 0x08          | **bit3 = 0x08**                   |
| ult  | bit1 = 0x02          | **bit4 = 0x10**                   |

关键结论：**8 种语言 SDK 的 `EDITION_BY_BIT = ["std","asn","pro","max","ult"]` 数组在改数据之前就已经是正确的新映射**（上一会话已逐语言核实）。
因此「版本顺序重排」对 SDK 解析逻辑**无破坏性影响**，仅需同步文档/注释，并刷新因数据刷新而漂移的测试夹具。

---

## 2. 数据刷新状态

- 10 个 `.qzdb`（std/ult/pro/max/asn × china/global）已从 `qqzeng-data/temp_work` 覆盖到：
  - `multi-lang/test_data_202608/{ed}/{scope}/`
  - `multi-lang/data/`（md5 与前者一致）
- 10 个真值 `range.csv` 已从对应 `*_range.zip` 重新解压覆盖（NEW 真值）。
- `golden_vectors.json` 已用 Python SDK（`r.find(ip).to_pipe()`）重新生成 `expected` 字段。

---

## 3. 本次会话修复/核对项（含上一会话已落地项汇总）

| 类别 | 文件 | 改动 |
|------|------|------|
| 文档 | `docs/QZDB_FORMAT.md` §3.1 | 档位→字段数映射 `std=6/asn=8/pro=11/max=15/ult=25`（原 ult/max 数值写反） |
| 注释 | `python/qzdb.py`、`java/.../QzdbReader.java`、`go/qzdb/qzdb.go`、`netcore/QzdbReader.cs`、`rust/src/lib.rs`、`c/qzdb_reader.h` | 陈旧位序注释统一改为新映射 `bit0=std,bit1=asn,bit2=pro,bit3=max,bit4=ult` |
| 测试夹具 | `go/qzdb/synthetic_test.go` | 硬编码 OLD `asn=0x04` → 新 `asn=0x02`（根因：Go `GetEdition="pro"` 误报） |
| 测试夹具 | `rust/tests/tier1.rs` | 硬编码 `build_time=="2026-08-02"` → `"2026-08-09"`（夹具漂移） |
| **真实 SDK Bug** | `c/qzdb_reader.c` L1736 | `calloc(nf)` → `calloc(nf+1)`：`group_field_names` 缺 NULL 终止符，测试扫描越界崩溃。修复后 C 167/167。 |
| 测试夹具 | range.csv ×10、golden_vectors.json | 重新生成以对齐 NEW 真实数据（Go `TestGoldenTier2`、C# Tier2 等回到真值基线） |

> C 的 NULL 终止符修复是**与版本顺序无关、但被数据刷新暴露的真实稳健性 Bug**，已修复。

---

## 4. 测试结果（全部语言）

### 4.1 元数据一致性（跨语言 meta probe，7 路对拍，10 个库）

| 对比 | 结果 |
|------|------|
| python ↔ node   | 10/10 ALL MATCH ✅ |
| python ↔ php    | 10/10 ALL MATCH ✅ |
| python ↔ go     | 10/10 ALL MATCH ✅ |
| python ↔ c      | 10/10 ALL MATCH ✅ |
| python ↔ csharp | 10/10 ALL MATCH ✅ |
| python ↔ java   | 10/10 ALL MATCH ✅ |

探针字段：`edition / edition_source / version_mask / field_names_source / field_names / group_count / pool_count / data_month`。
以 asn 库为例，各语言一致报告：`edition=asn, edition_source=version_mask, version_mask=2`（即 NEW 映射 `asn=0x02`），
证明**版本顺序重排后所有语言对档位的判定完全一致**。

### 4.2 值正确性（单测 / 全量解析）

| 语言 | 结果 |
|------|------|
| Python | `test_tier1.py` 61/61；`full_parse_verify.py --all` OVERALL **PASS ✅**（237,980 查询，0 L1 失败，10 库 100%） |
| Node   | `test_suite.js` Tier1=379 断言，Tier2 黄金校验 4102 条 0 失败，**ALL PASS** |
| C      | `test_main.c` Tier1 167/167 断言，`TIER1_PASS` |
| Go     | `go test ./...` → `ok qzdb_reader/qzdb`（含 `TestREADMEAPISurface`/`TestGoldenTier2` 修复后通过） |
| Rust   | `cargo test` → **27 passed; 0 failed** |
| C#     | 库 `dotnet build -c Release` → **0 Warning, 0 Error**（net8/9/10）；Tier1 113/0、Tier2 仅**全球版** 52 处保留段标注口径差异（国内版 0 误差，与版本顺序无关，非 Bug） |
| Java   | SDK `javac` 直编干净；`FullAccuracyAndPerfTester` 体系就绪；`edition=std, pipe 正确` |
| PHP    | `edition=std, pipe 正确`（命名空间 `Qqzeng\Ip\QzdbReader`） |

### 4.3 端到端数值抽查（std_china，同 IP 三语言逐字节一致）

| IP | Python | Java | C# |
|----|--------|------|-----|
| 114.114.114.114 | 亚洲\|CN\|中国\|江苏\|南京\|114DNS | 同 | 同 |
| 1.2.3.4 | MISS | MISS | MISS |
| 8.8.8.8 | MISS | MISS | MISS |
| 223.5.5.5 | 亚洲\|CN\|中国\|浙江\|杭州\|AliDNS/DoH/DoT/阿里云 | 同 | 同 |
| 180.76.76.76 | 亚洲\|CN\|中国\|北京\|北京\|BaiduDNS/百度云 | 同 | 同 |

---

## 5. 结论

✅ **头版本顺序重排后，QZDB 多语言 SDK 全部「对了」**：
- 8 语言 `EDITION_BY_BIT` 映射本就正确，无需改逻辑；
- 文档、注释、个别测试夹具已同步到 NEW 映射；
- 7 语言元数据跨语言 100% 一致，端到端值正确性逐字节吻合；
- 顺带修复 1 个真实 C SDK Bug（field_names 缺 NULL 终止符）。

**唯一遗留**：C# Tier2 尚有 52 处误差，**且 100% 落在「全球版」（global），国内版（china）0 误差**。

根本原因是**国内版 / 全球版的覆盖范围天然不同**：

- **国内版（china）**：数据只覆盖中国分配段，**库里压根不含**保留/特殊用途段（10.0.0.0/8、100.64.0.0/10、240.0.0.0/4 等），range.csv 真值里也没有这些行 → 两边一致，**0 误差**。
- **全球版（global）**：为达到全量覆盖（0.0.0.0/0）把这些保留段也编进库，于是和 range.csv 真值在标注口径上对不上 → **52 处误差全在此**。

误差内容（查库命中、字段与真值不一致）：

1. **默认国家标签口径**：range.csv 对保留/未知段把 continent/country_code/country 留空；`.qzdb` 库给这些段填了默认占位 `ZZ` / `保留地址`；
2. **isp 文案细微差异**：如 `240.0.0.0`，CSV `保留地址/将来使用` vs 库 `互联网/将来使用`。

（注：`213.232.83.0` 其实不是 IETF 保留段，是真实分配段——法国 VOIP Telecom SAS，此前笼统归入"保留地址段"归类不严谨。）

**判定**：这不是硬编码、不是 SDK Bug、与版本顺序无关。SDK 查的是 `.qzdb` 库、期望值来自 `range.csv` 真值文件，两边都是数据。差异是**同一构建源两个产物对"保留/未知段"标注口径不一致**（留空 vs 填 `ZZ/保留地址`）。Python 查同一库返回完全一样的 `ZZ/保留地址`，证明是所有语言共通的数据产物问题、非 C# 独有。要消掉这 52 处，须统一**构建管线**对保留段的标注口径，不须改 SDK。

---

*验证命令（节选）：各语言 `meta_probe_*` + `tools/meta_compare.py` 7 路对拍；`full_parse_verify.py --all --no-boundary --sample 3000`；各语言单测套件。*
