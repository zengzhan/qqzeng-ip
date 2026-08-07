# QZDB 多语言 SDK 权威 API 契约 (API Contract)

**版本**：v2.4｜**适用范围**：go / rust / python / nodejs / php / c（java / netcore 已实现并作为认证参考）
**性质**：本文档是所有语言 SDK 实现的**唯一事实来源（Single Source of Truth）**。任何语言的最终行为必须与本文档逐字一致；如发现实现与文档冲突，**以本文档为准**修复实现（不要改文档去迁就 bug）。

> 参考实现：Java (`multi-lang/java`) 与 C# (`multi-lang/netcore`) 已通过 Tier1/2/3 全量验证，是 Golden Reference。
> 验证裁判：`multi-lang/tools/golden_vectors.json`（IP → `to_pipe()` 字符串），已由 C# 独立交叉校验 4102/4102 通过。所有语言必须对其 0 偏差。

---

## 1. 类名与命名

| 项 | 名称 |
|----|------|
| 核心读取类 | `QzdbReader`（C 语言为结构体 `qzdb_reader_t`，Go/Rust 包/模块名 `qzdb_reader`） |
| 响应实体 | `GeoInfo`（C 为 `qzdb_geo_info_t`） |
| 错误 | `ErrorCode` 枚举（见 §7） |
| 用法类型 | `UsageType`（21 个预定义 + 未知兜底） |
| 行号三元组 | `RowIds`（`geoId`, `asnId`, `usageId`） |
| 批量结果 | `BatchResult`（`ip`, `geoInfo|null`, `error|null`） |
| 注册表(多库) | `QzdbRegistry` |
| 链式(多库合并) | `ChainedReader` |

---

## 2. 加载 (Builder 模式)

```
Builder(path: string)            # 文件路径
Builder(bytes: byte[])           # 内存字节
Builder(stream: InputStream)     # 输入流（按需支持）
  .groupIndex(int)               # 版本组索引（0=主组；ASN 组通常为 2）
  .verifyCrc(bool=true)          # 默认开启；关闭仅用于受信数据/基准测试
  .build() -> QzdbReader
```

- 加载失败（文件不存在、Magic≠`QZDB`、HeaderVersion≠1、CRC 不匹配、截断）必须 **Fail-Closed 拒绝初始化**，不得部分加载或静默降级。
- 热更新：`reload(path)` / `reloadBuffer(bytes)` —— 构建完整新快照后**原子替换**；新快照强制 CRC 校验，失败时**旧快照继续服务**。
- 资源释放：`close()` / `Drop` / `dispose` / `free`；关闭后查询必须安全失败（不 UAF / 不 double-free）。

---

## 3. 单条查询 API（全部语言必须提供）

| 方法 | 签名(伪代码) | 未命中 | 非法 IP |
|------|-------------|--------|---------|
| `find` | `find(ip: string)` | 语言空值(见 §4) | 见 §4 |
| `findUint` | `findUint(ip: u32)` | 空值 | 空值 |
| `findBytes` | `findBytes(ip: byte[16])` | 空值 | 空值/异常 |
| `findFields` | `findFields(ip: string, fields: string[])` | 空值 | 空值 |
| `findStr` | `findStr(ip: string) -> string` | `""` | `""` |

- `findFields` 为字段投影：只解析 `fields` 指定的字段，减少池读取；`fields=null/空` 等价于 `find`。
- `findStr` 返回 `to_pipe()` 字符串，未命中/非法返回 `""`。

---

## 4. 未命中 / 非法 IP 的语义（跨语言一致性）

**核心约束（全语言一致，硬指标）**：「找到 / 未命中 / 参数错误」三态必须在 **批量/流式路径**（`findBatch` / `findBatchFields` / `findStream` / `findIter`）通过 `BatchResult.Error` 完整保留——非法 IP 在批量路径**不得被归并到未命中**，调用方必须能据此区分。

单条 `find` 的表层表达允许语言惯用法差异：

| 语言 | 未命中返回 | 非法 IP 行为 |
|------|-----------|-------------|
| Java | `Optional.empty()` | **抛 `QzdbException(INVALID_IP)`** |
| C# | `null` | **抛 `QzdbException(INVALID_IP)`** |
| Python | `None` | **抛 `QzdbError(INVALID_PARAM)`** |
| Go | `(nil, nil)` | `(nil, nil)`（与未命中同形；批量路径经 `BatchResult.Error` 区分） |
| Rust | `Option::None` | `Option::None`（与未命中同形；批量路径经 `BatchResult.Error` 区分） |
| Node | `null` | `null`（与未命中同形；批量路径经 `BatchResult.Error` 区分） |
| PHP | `null` | `null`（与未命中同形；批量路径经 `BatchResult.Error` 区分） |
| C | 错误码 `QZDB_ERR_NOT_FOUND` | 错误码 `QZDB_ERR_INVALID_PARAM` |

> **口径修订（v2.4.1，消除与 §7.1 的历史矛盾）**：
> - 旧文档曾写「Python 非法 IP 返回 `None`」，与 Python 实际实现（抛 `QzdbError`）及 §7.1 冲突，此处以**实现为准**更正（契约为 Single Source of Truth，但实现优先于过时文字）。
> - 单条 `find` 对非法 IP：Java/C#/Python **抛异常**（不返回空值）；Go/Rust/Node/PHP 当前返回语言空值（与未命中同形），其 golden 校验包装器统一把「空值 / 异常 / 错误码」映射为 `""`，故非法 IP 返回空值即视为通过。**若业务需要单条 `find` 也严格区分非法 IP，后续版本可让 Go/Rust/Node/PHP 改为抛异常——此为已知分歧，不阻断当前发布（跟踪项见 `QZDB_SDK_API.md` §12.4 / 实施清单）。**

---

## 5. 批量 / 流式 / 低级 / CIDR API（全部语言必须提供）

**批量**（顺序执行，逐条保留三态语义，内部不起线程池）：
- `findBatch(ips: list<string>) -> list<BatchResult>`
- `findBatchFields(ips, fields) -> list<BatchResult>`
- `findStream(ips) -> 流式/迭代器`（内存恒定，不累积）

**低级行号**：
- `lookupRowId(ip: string) -> int`（0=未命中/非法）
- `lookupRowIdUint(u32)` / `lookupRowIdBytes(byte[4|16])`
- `lookupIds(rowId: int) -> RowIds`（越界返回 null）

**CIDR 反查**（数据库本身不存 CIDR，由 Trie 叶子深度重建网络地址）：
- `lookupCidr(ip: string) -> string|null`（如 `1.0.1.0/24`、`2001:218::/32`）
- `lookupCidrUint(u32)` / `lookupCidrBytes(byte[4|16])`
- 未覆盖返回 `null`；非法 IP：Java/C# 抛异常，其余返回 null/空值。

**元信息自省**：
- `getVersion()` / `getDataMonth()` / `getEdition()` / `getScope()`(恒`""`) / `getBuildTime()` / `getDescription()`
- `getFileHash()`(CRC32 十六进制 8 位小写) / `getFieldNames()` / `hasField(name)`
- `verifyCrc() -> bool` / `getGroupCount()` / `getPoolCount()`

---

## 6. GeoInfo 响应实体

**字段访问（大小写/下划线/连字符不敏感）**：
- `get(name: string) -> string`：归一化规则 = **转小写 + 去除 `_` 与 `-`**（`country_code`==`countryCode`==`COUNTRY_CODE`==`Country-Code`）。未匹配返回 `""`，**严禁抛 KeyError / 索引越界 / Panic**。

**序列化**：
- `toPipe()` / `toPipeString()`：字段以 `|` 拼接。**直接拼接已解码的字符串值，禁止任何重新格式化**（见 §8）。
- `toMap()` / `toDict()`：字段名→值（全 string）。
- `toJson()`：手写序列化，保留原始 snake_case 键；`longitude`/`latitude`/`asn`/`geo_id` 输出为 **JSON 数字**（无法解析则 `null`），其余为字符串。
- `toString()` == `toPipe()`。

**语义化 Getter 全集（缺失返回 `""` 或 `null`）**：
`country` `countryEn` `province` `provinceEn` `city` `cityEn` `district`
`getGeoId(): long?` `getLongitude(): double?` `getLatitude(): double?`
`getTimezone()` `getIsp()` `getIspEn()` `getAsn(): long?` `getAsName()` `getAsDomain()`
`getUsageType(): UsageType` `getCountryAlpha2()` `getCountryAlpha3()` `getCurrencyCode()` `getCurrencyName()` `getPhonePrefix()` `getEmojiFlag()` `getLanguages()`
- **`getCidr()` 恒返回 `""`**（CIDR 不是数据库字段；真实网段用 `reader.lookupCidr(ip)`）。

---

## 7. ErrorCode 枚举

`NOT_FOUND` `CORRUPTED` `OUT_OF_BOUNDS` `INVALID_PARAM` `BAD_HEADER` `BAD_MAGIC` `UNSUPPORTED`（Java/C# 另有 `INVALID_IP`）。损坏文件构造时必须抛出 `BAD_MAGIC`/`BAD_HEADER`/`CORRUPTED` 之一。

---

## 8. 正确性强制规则（禁止 0 幻觉的核心，逐条必须实现）

1. **SENTINEL 位剥离**：Trie 返回的 row_id 带有高位哨兵位（`0x80000000` 32 位节点 / `0x800000` 24 位节点）。在调用 `_read_ip_row` / `_resolve_geo` **之前必须剥离**（`row_id & 0x7FFFFFFF` 或 `row_id & 0x7FFFFF`）。**遗漏此步是已知最致命 bug**（曾导致 `find_fields` 全库返回 None）。`find`/`findUint`/`lookupRowId*` 都已剥离；`findFields` 路径同样必须剥离。

2. **原生浮点格式 = 6 位小数**（跨语言唯一正确）：解码 `float32`/`float64` 原生字段时，
   - 整数值（如 `116.0`）→ `"116"`（无小数点）
   - 非整数 → 固定 **6 位小数**（`116.4` → `"116.400000"`）
   - `NaN` / `Inf` → `""`
   - 区域设置使用 `.`（US/Invariant）
   - Java: `DecimalFormat("0.000000")`；C#: `ToString("F6", InvariantCulture`)。**禁止使用最短表示（`%g`/`str(float)`/`%v`）**。

3. **`to_pipe()` 逐字拼接**：`values[i]` 已是格式正确的字符串（原生浮点已在解码时格式化为 6 位小数），`to_pipe` 不得再 `float()` 重新解析，否则会把 `116.400000` 变回 `116.4`。

4. **IPv4-Mapped IPv6 自动降级**：`::ffff:a.b.c.d` 与 `::ffff:hex` 形态必须剥离前缀走 V4 Trie，结果与对应 IPv4 **字段级完全一致**。

5. **Fail-Closed**：非法 Magic / HeaderVersion≠1 / CRC 不匹配（且 `verifyCrc` 开启）/ 截断文件 → 构造即拒绝，绝不部分加载。

6. **CIDR 重建**：最具体网段 = Trie 叶子深度 = 前缀长度 N；网络地址 = IP 高 N 位清零。Jump Table 直接命中叶子时，内部自动从根补走还原深度（不得返回错误网段）。V6 按 RFC 5952 压缩。

7. **缺失数值字段严禁哨兵 0**：解码层对缺失的原生数值字段（`geo_id` / `asn` / `usage_type` 等）必须归为语言空值 / `""`（`null` / `None` / `Option::None` / `""`），**严禁存储或输出哨兵值 `0`**。`0` 是合法业务值，用它表示缺失会让跨语言字段级比对失配。Java/C#/Python/Go/Rust/Node/PHP 已合规；C 须在重构后遵守。

---

## 9. 性能要求

- 采用**不可变快照 + 原子替换**（无锁热更新），查询路径对快照只读。
- 优先 **per-snapshot 有界无锁 GeoInfo 缓存**（以 `row_id`（或 `entry_id`）为键，开放寻址；碰撞只重算、绝不返回错值）。缓存命中应趋近 **零分配**。
- 归一化字段索引在加载期构建一次，查询期仅 O(1) 哈希。
- 查询路径避免每次 `new string[]` / `new GeoInfo` 的不必要分配（缓存命中直接复用）。

---

## 10. 测试交付（Tier 规范摘要，详见 `docs/QZDB_TEST_SPECIFICATION.md`）

- **Tier 1**（≥50 断言，无数据库即可运行）：严格 IPv4/IPv6 解析（前导零/越界/缺段/超长/CIDR 形式/zone-id 全拒绝）；Mapped 降级一致；双栈交叉断言；字段名归一化；`UsageType` 21 场景 + 未知兜底；损坏文件 Fail-Closed；CRC 强制；无锁 Reload 原子性；CIDR 反查；资源释放。
- **Tier 2**（对 `golden_vectors.json` 0 偏差）：加载 `multi-lang/data/qqzeng_ip_std_china.qzdb` 与 `qqzeng_ip_ult_china.qzdb`，对每个 IP 断言 `find(ip).toPipe() == expected`（未命中/非法映射为 `""`）。**必须 0 失败**。
- **Tier 3**（性能，建议）：16 线程 × 10 万双栈混合查询无异常/race-free；单线程 + 多线程 QPS 报告；IPv4/IPv6 分别统计。

---

## 11. README 要求

每种语言目录须有一份专业 README，结构对齐 `multi-lang/netcore/README.md` 与 `multi-lang/java/README.md`：安装/加载/查询 API 全表/GeoInfo 取值/元信息/CIDR 反查/批量流式/错误处理/热更新/性能/维护更新。所有示例必须基于**该语言真实最终 API**（禁止抄其他语言幻影 API）。
