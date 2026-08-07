# QZDB Python SDK 独立审查报告

- 审查对象：`multi-lang/python/qzdb.py`（v2.4 规范实现，约 2029 行）
- 审查角色：独立审查人（非实现 Agent），只读源码/测试，不改源码
- 规范依据：`docs/QZDB_SDK_API.md` v2.4（重点：§4.1/4.2/4.4、§5、§6.1/6.2/6.3/6.4、§7.1、§8.2/8.4、§9、§10、附录 A.5）
- 审查日期：2026-08-07

---

## 1. 验证方式说明（我具体做了什么）

> 说明：仓库**不存在** Python 专属的“实现 Agent 报告”文档（对齐审计表/改动清单/测试报告/遗留问题清单）。
> 最接近“对方声称做过什么”的载体是 `multi-lang/python/README.md`（声明）、`qzdb.py` 内注释、以及测试文件
> `test_tier1.py` / `test_golden.py`。本报告把这些都当作“声称”并逐一独立验证。

1. **完整通读规范** v2.4 的 §4（生命周期/缓冲区所有权/CRC/原子发布）、§5（方法矩阵）、§6（GeoInfo/归一化/空值/UsageType）、§7（未找到 vs 参数错误）、§8（批量/流式三态）、§9（ChainedReader）、§10（命名）、§11（并发）、附录 A.5（Python 完整签名）。
2. **完整通读实际源码** `qzdb.py`（非片段），逐方法核对。
3. **重跑测试（独立）**：
   - `python3 test_tier1.py` → **60/60 断言通过**（独立复现，与 README 声明一致）。
   - `python3 test_golden.py` → **4102/4102 通过**（golden 向量据 README 已由 C# 独立校验；我用 Python 重新跑，0 偏差，作为跨语言独立 Oracle）。
4. **自行构造 Oracle 交叉验证**（不依赖实现 Agent 的用例）：
   - 跨入口一致性：`find` == `find_uint` == `find_bytes(4B)`，对 2000 个真实命中 IP 逐一比对 → **0 不一致**。
   - CIDR 数学独立重算：用 `ipaddress` 按 `ip & netmask` 重建网络地址，与 `lookup_cidr` 输出比对，v4 核验 **3544** 个 → **0 错误**；v6 随机采样命中核验 3 个 → 0 错误。
   - 数值空值兜底：`get_asn()` 在 61952 个缺失场景返回 `None`（类型校验为 `int`），**未出现哨兵值 0**。
   - 边界穷举（独立构造、非 golden）：`0.0.0.0 / 255.255.255.255 / 127.0.0.1 / 10.x / 192.168.x / 224.x / 169.254.x / ::1 / :: / ::ffff:1.2.3.4 / 2001:db8::1 / fe80::1 / ff02::1` → 全部安全返回 `None`，**无崩溃**。
5. **并发压测**：8 线程 × 200 次 `find` + 主线程 20 次 `reload(STD↔ULT)` 交替 → **0 异常、0 撕裂读**（快照 field 数始终一致=25）。
6. **探针验证签名/语义偏差**：针对 `find()`/`find_batch`/`find_str`/`GeoInfo` getter/`lookup_ids`/`open_buffer`/构造函数 bytes 等逐项实测（见第 2 节每条的“复现方式”）。

---

## 2. 发现的问题清单

### 🔴 阻塞性（会导致崩溃 / 违反明确的规范契约，合并前应修复）

**F1 — `GeoInfo.cidr` 缺失且对规范调用方直接抛 `AttributeError`**
- 位置：`qzdb.py` `GeoInfo` 类（仅 `get_cidr()` 方法，行 284；无 `cidr` 属性；`__getattr__` 行 229）。
- 复现：`gi = r.find('223.5.5.5'); gi.cidr` → `AttributeError: cidr`。
- 问题：附录 A.5 明确要求 `@property def cidr(self) -> str`（§6.3 表也列 `cidr()` 兜底 `""`）。实现只提供 `get_cidr()` 方法，缺失属性形式；由于 `cidr` 不是真实字段名，`__getattr__` 直接抛异常。任何“按附录 A.5 写 `gi.cidr`”的调用方会崩溃。
- 建议修复：补 `@property def cidr(self): return ''`（与 `get_cidr()` 行为一致）。

### 🟡 需要关注（偏离规范 / 有风险 / 证据不足）

**F2 — `geo_id` / `asn` / `usage_type` 属性访问返回“原始字符串”，而非附录 A.5 要求的类型化值**
- 位置：`__getattr__`（行 229-233）对真实字段名做字符串回退；`get_geo_id()`（行 303）、`get_asn()`（行 320）、`get_usage_type()`（行 337）才返回 `int|None` / `UsageType`。
- 复现：`gi.geo_id` → `'330100'`（str）；`gi.asn` → `'37963'`（str）；`gi.usage_type` → `'DNS'`（str）。而 `gi.get_geo_id()` → `330100`（int）、`gi.get_usage_type()` → `UsageType('DNS', known=True)`。
- 问题：附录 A.5 把 `geo_id`/`asn` 定义为 `@property -> int|None`，`usage_type` 为 `@property -> UsageType|str`。实现用 `get_*` 方法承载类型化语义，但**属性形式**返回原始字符串。后果：① 按规范用 `gi.geo_id` 的调用方拿到字符串，易产生真值/类型 bug（如 `'0'` 误判为真）；② 无法区分“字段缺失”与“值为 0”。功能上通过 `get_*` 可达正确值，故判 🟡 而非 🔴。
- 建议：要么把 `geo_id`/`asn`/`usage_type`/`cidr` 实现为返回类型化值的 `@property`（与 `longitude`/`latitude` 等现有属性一致），保留 `get_*()` 作为别名。

**F3 — `find()` 对非法 IP 返回 `None`，未按 §7.1 抛 `QzdbError`**
- 位置：`find()`（行 1412-1423）；`_fast_parse_ip` 失败即 `return None`。
- 复现：`r.find('999.1.1.1')` / `r.find('not-an-ip')` / `r.find('1.2.3.4/24')` / `r.find('')` → 全部 `None`，从不抛异常。
- 问题：§7.1 的 Python 行规定“参数/文件错误 → `raise QzdbError(...)`”，即“没查到”与“参数错误”必须区分。实现把两者都吞成 `None`。级联影响：① 违反 §8.2 批量三态（见 F4）；② 违反 §9.1 Fallback 模式“输入格式错误立即终止整条 Chain”——因为底层 `find` 永不抛，Chain 无法感知格式错误。README §3 甚至把“非法 IP 一律返回 None（不抛异常）”当作特性声明，属于**有意的规范偏离**，需与规范 owner 确认是否接受。

**F4 — `find_batch` 永不填充 `error`，批量三态被塌缩**
- 位置：`find_batch`（行 1779-1781）构造 `BatchResult(ip, self.find(ip), None)`；`error` 恒为 `None`。
- 复现：`r.find_batch(['223.5.5.5','not-an-ip','8.8.8.8'])` → 第二条 `geo_info=None, error=None`，与“没查到”无法区分。
- 问题：§8.2 明确定义批量必须保留“找到 / 没找到 / 输入错误”三态，且 §8.2 是 v2.4“恢复此前被删内容”的重点。由于 `find()` 不抛（F3），`error` 永远 `None`，非法输入被错误归类为“没找到”。README §7 也承认此塌缩。

**F5 — `find_stream` 返回 `GeoInfo|None` 且不存在 `find_iter`**
- 位置：`find_stream`（行 1786-1789，yield `self.find(ip)`）；无 `find_iter`。
- 问题：§8.4 / §10.2 规定 Python 流式方法为 `find_iter -> Iterator[BatchResult]`。实现用 `find_stream` 且元素为 `GeoInfo|None`（非 `BatchResult`），同样丢失错误态（见 F4）。README 文档化的是 `find_stream`，属有意命名偏离。

**F6 — `open_buffer` 缺失；构造函数不接受内存 `bytes`，README 示例失效**
- 位置：`__init__`（行 542）→ `load`（行 618）→ `open(db_path,'rb')`；无 `open_buffer` 静态方法（探针确认 `hasattr(QzdbReader,'open_buffer')==False`）。
- 复现：`QzdbReader(f.read())`（`f.read()` 为文件内容 bytes）→ `ValueError: embedded null byte`。
- 问题：§4.1 与附录 A.5 都要求 `open_buffer(buffer, ...)` 静态方法（拷贝语义）。README §2 的“从内存字节加载 `r = QzdbReader(f.read())`”**实际会抛异常**。当前只能用 `QzdbReader(占位路径)` 再 `reload_buffer(bytes)` 绕过（且 `reload_buffer` 的拷贝语义已验证正确，见下）。
- 建议：新增 `@staticmethod open_buffer(buffer, group_index=0, verify_crc=True)`；并修正 README 示例。

**F7 — `lookup_ids` 返回裸元组，而非 `RowIds` 具名结构体**
- 位置：`lookup_ids`（行 1603-1608）返回 `(geo_id, asn_id, usage_type_id)` 元组；`RowIds` 未定义。
- 问题：附录 A.5 明确 `lookup_ids(row_id) -> RowIds | None` 且“禁止裸数组”。实现返回裸 tuple（虽可解包，但违反具名结构体契约）。

**F8 — `BatchResult` 字段为 `ip/geo_info/error`，而非附录 A.5 的 `info/error`**
- 位置：`BatchResult`（行 1878）`__slots__ = ('ip','geo_info','error')`。
- 问题：附录 A.5 定义为 `@dataclass class BatchResult: info; error`。`br.info` 不存在（实际是 `br.geo_info`）。多一个 `ip` 字段、改名 `info`→`geo_info`。跨语言一致性上与其他语言的 `info` 字段名不一致。

**F9 — `QzdbRegistry` / `ChainedReader` 形态偏离规范**
- 位置：`QzdbRegistry`（行 1893-1960）、`ChainedReader`（行 1963-2029）。
- 偏差：
  - 类名 `QzdbRegistry`（附录 A.5 为 `Registry`）。
  - `Registry.register(name, reader)` 接收**已构建的 reader 实例**；附录 A.5 为 `register(name, path, **kwargs)`（由 Registry 负责加载）。且缺 `register_buffer` / `unregister`。
  - `ChainedReader` 用构造器 + `add()`，缺附录 A.5 / §9.5 的 `chain` / `chain_merge` / `chain_merge_override` 静态工厂（§9.5 示例用 `ChainedReader.chainMerge(...)`）。
  - `ChainedReader` 缺 `find_batch`（§9.2 要求与 `QzdbReader` 对等方法矩阵）、缺 §9.3 的 `editions` / `scopes` / `readers` 聚合属性。

**F10 — `UsageType` 不是 `str, Enum`；辅助方法命名与附录 A.5 不符**
- 位置：`UsageType`（行 422-510）。
- 偏差：附录 A.5 写为 `class UsageType(str, Enum)`，辅助为 `display_zh` 属性 / `from_raw`。实现是普通类，命名为 `get_display_zh()` / `get_display_en()` / `get_description()` / `from_string()`。
- **但行为完全符合 §6.3/§6.4**：21 个场景 + 未知值安全降级为 `UsageType`（`from_string` 对未知返回类型化 `UnknownUsageType`，比附录 A.5 的“未知返回裸字符串”更严格、更安全）。属“形态/命名偏离”，非行为偏离，故 🟡。

**F11 — `find_v6_bytes` 未做 IPv4-mapped（`::ffff:`）降级，与 `find`/`find_bytes` 不一致**
- 位置：`find_v6_bytes`（行 1435-1445，无 `::ffff:` 判定）；对比 `find_bytes`（行 1473-1480 有降级）。
- 问题：§5.3 要求“所有入口统一走同一个地址规范化函数”。`find_v6_bytes` 作为低级 v6 入口绕过了规范化。实际影响低（该入口为底层 escape-hatch，且 mapped 地址极少以 v6 形式存储于这些库），但属代码级不一致。本次未在数据中复现到运行时差异（无 mapped 形式的国内命中 IP），故记为代码级偏离而非已复现 bug。

### 🟢 建议（代码质量 / 可维护性，不阻塞合并）

- **S1** `find_str` 返回 `''` 而非 `None`（附录 A.5 为 `str | None`）；属轻微偏离。
- **S2** `get_scope()` 恒返回 `''`（行 1805-1806）。按 §4.5 注释，文件格式未携带 scope 时返回空串是允许的回退，但即便国内库也从未推导 scope；可接受，记录备查。
- **S3** `_norm_key`（行 19-33）同时删除 `_` 与 `-`；§6.1 字面规则只删 `_`，但 §6.1 示例 `Country-Code` 暗示 `-` 也应归一化 → 与示例一致，无害；建议补一行注释说明意图，避免后续被“修正”成只删 `_` 而破坏 `Country-Code` 用例。
- **S4** 大量超出规范矩阵的方法（`find_v6_bytes`/`find_v6_uint`/`lookup_row_id_uint/v6/v6_bytes`/`lookup_cidr*`/`get_field_names`/`get_group_count`/`get_description`/`get_build_time`/`get_data_month`/`get_file_hash`/`get_version`/`verify_crc`）是合理的扩展且未被禁止，但应在文档中明确标注为“扩展”，以免与规范 API 混淆。

---

## 3. 实现 Agent 报告的可信度评估

> 注：仓库内无独立 Python 实现报告文档，以下将 README 声明 / 代码注释 / 测试输出视作“声称”，独立核验。

| 声称 | 我的核验 | 结论 |
|---|---|---|
| `test_tier1.py`：60 断言 0 失败 | 独立重跑 → `TIER1_PASS: 60 assertions, 0 failures` | ✅ 真实可信 |
| golden：Python 与 C# 校验过的 4102 向量 0 偏差 | 独立重跑 `test_golden.py` → `Tier2 golden: total=4102 fail=0` | ✅ 真实可信（作为跨语言独立 Oracle 有效） |
| README §2：“从内存字节加载 `r = QzdbReader(f.read())`” | 实测 → `ValueError: embedded null byte`，构造函数拒绝 bytes | ❌ **声明不成立**（应为 `open_buffer` 或 `reload_buffer`，且 `open_buffer` 尚未实现） |
| README §3：“非法 IP 一律返回 None（不抛异常）” | 实测 7 类非法输入全部 `None` | ✅ 属实，但这是**对 §7.1 的有意偏离**（见 F3） |
| README §7：“未命中 `geo_info=None`，非法输入 `error=None` 且 `geo_info=None`” | 实测 `find_batch(['...','not-an-ip'])` 第二条 `error=None` | ✅ 属实，但坐实了 §8.2 三态塌缩（见 F4） |
| asn 56554 回归修复（哨兵剥离 + row_schema 解析） | golden 含 `ult`（含 asn/usage 字段）4102/4102 通过；跨入口一致性 0 不一致 | ✅ 修复就位且被独立 Oracle 验证（虽无专属 Python 报告，但 golden 已覆盖） |
| “零依赖、无锁热更新、并发安全” | 压测 8 线程+20 reload → 0 异常/0 撕裂读 | ✅ 设计可信（详见第 5 节并发结论） |

整体：测试层声称（tier1/golden）**高度可信**；README 的功能性描述**基本属实但存在一处失效示例（F6）**，且两处“特性”实质是**已被规范明确要求的语义被有意偏离（F3/F4）**——实现 Agent 把它们当作特性宣传，但对照 v2.4 它们是偏离项，应提请规范 owner 裁定是否接受。

---

## 4. 未覆盖到的审查项（诚实声明）

1. **IPv4 全空间（~3900 万节点）精度扫描未独立复现**：Java 侧的 Tier2 全量扫描在 Python 侧没有等价 harness；本次仅用 golden 采样（4102）+ 自建 3544 命中 IP 交叉校验 + 3544 次 CIDR 重算。规范里“IPv4 全空间覆盖 X%”的强声明在 Python 侧**未被本审查独立跑出日志**，只能说“golden 样本 + 抽样均正确”，不能断言全空间零偏差。
2. **仅审查 Python**：其余 7 语言（Go/Rust/Node/PHP/C/Java/C#）未审查。
3. **`reload`/`reload_buffer` 的 CRC 强制路径**仅在初始加载（tier1 CRC 测试）验证；未在“并发 reload + 故意损坏的重载目标”组合下穷举竞态。逻辑简单且 reload 始终强制 CRC（已读码确认），风险低。
4. **`find_v6_bytes` 的 mapped 降级不一致（F11）未能运行时复现**：数据集中无 mapped 形式的国内命中 IP，故仅作为代码级不一致标记，未观测到实际错误结果。
5. **Python 无 `-race` 等价工具**：CPython 在 GIL 下对单个 `__dict__` 替换是原子的，无法用数据竞争检测器形式化证明；并发正确性依赖“构造即正确 + 压测”，非形式化验证。
6. **`to_json` / `to_pipe` 的跨语言 golden 一致性仅通过 golden（`to_pipe`）验证**；`to_json` 的数字/键名规则（§6.2）通过读码确认（`NUMERIC_FIELDS` 与手写拼接），未对 `to_json` 单独跑 golden（golden 向量用的是 `to_pipe`）。

---

## 5. 修复实施记录（2026-08-07，owner 授权"修改/修复/优化/测试"）

> 下列偏离项已按 v2.4 规范修复并回归验证。新增回归测试 `test_review_fixes.py`（57 断言，0 失败）。

| 编号 | 修复内容 | 改动点（`qzdb.py`） | 兼容处理 |
|---|---|---|---|
| **F1** 🔴 | `GeoInfo.cidr` 属性（返回 `''`） | `GeoInfo.cidr` `@property` | `get_cidr()` 保留为别名 |
| **F2** 🟡 | `geo_id`/`asn`/`usage_type` 类型化属性（`int\|None`/`UsageType`） | `GeoInfo` 新增同名 `@property`，覆盖 `__getattr__` 原始字符串回退 | `get_*` 方法保留为别名，行为一致 |
| **F3** 🟡 | `find()` 对非法/空 IP 抛 `QzdbError(INVALID_PARAM)`（§7.1） | `find()` 在 `_fast_parse_ip` 失败时 raise | **语义变更**：原返回 `None`。`find_str` 捕获→`''`；`find_batch`/`find_stream`/`find_iter` 容错；`test_golden.py` 的 `invalid` 分类改为捕获异常视作空结果 |
| **F4** 🟡 | `find_batch`/`find_batch_fields` 三态填充 `error` | 包裹 `find`/`find_fields`，非法输入写 `error` | 命中/未命中逻辑不变 |
| **F5** 🟡 | 新增 `find_iter`（yield `BatchResult`，§8.4）；`find_stream` 容错（非法输入 yield `None`） | `QzdbReader.find_iter` + `find_stream` try/except | `find_stream` 元素类型保持 `GeoInfo\|None` 不破坏旧调用 |
| **F6** 🟡 | 新增 `QzdbReader.open_buffer(buffer, group_index, verify_crc)` 静态工厂（拷贝语义） | 复用 `reload_buffer` 已验证路径 | 修正 README §2 失效示例（原 `QzdbReader(f.read())` 现用 `open_buffer`） |
| **F7** 🟡 | `lookup_ids` 返回 `RowIds` 具名元组 | `RowIds = namedtuple(...)`；`lookup_ids` 包成 `RowIds` | namedtuple 是 tuple 子类，原有解包 `g,a,u = ...` 仍可用 |
| **F8** 🟡 | `BatchResult.info` 属性别名 `geo_info` | `BatchResult.info` `@property` | 保留 `ip`/`geo_info`/`error` 字段 |
| **F9** 🟡 | `Registry = QzdbRegistry` 别名；`register_path`/`register_buffer`/`unregister`/`find_batch`；`ChainedReader` 增加 `find_batch`、`chain`/`chain_merge`/`chain_merge_override` 工厂、`editions`/`scopes`/`readers` | `QzdbRegistry`/`ChainedReader` 扩展 | 既有 `register(name, reader)`/`add`/`find*` 全部保留 |
| **F10** 🟡 | `UsageType` 补 `raw`/`display_zh`/`display_en`/`description` 属性与 `from_raw` 类方法 | `UsageType` 扩展 | `get_*`/`from_string` 保留；**未**转 `str,Enum`（避免破坏 `from_string` 契约与现有测试，行为已合规，仅命名形态偏离） |
| **F11** 🟡 | `find_v6_bytes` 增加 `::ffff:` 降级（与 `find`/`find_bytes` 一致，§5.3） | `find_v6_bytes` 头部判定 | 仅影响底层 16 字节原始入口；`find()` 字符串路径本就经解析器降级，无回归 |

**未改项（owner 决策建议保留）**：
- **F10 的 `str,Enum` 形态**：`UsageType` 当前为普通类，行为完全合规（21 场景 + 未知安全降级），转 `str,Enum` 会改变相等性/序列化语义且无行为收益，故仅补规范命名别名，未重构。
- 次级查询方法（`find_fields`/`lookup_row_id`/`lookup_cidr`）对非法输入仍返回 `None`/`0`（宽松语义，便于组合调用），仅主入口 `find()` 严格区分坏输入/未命中（§7.1）。

**测试结果**：
- `test_tier1.py`：60/60 通过
- `test_golden.py`：4102/4102 通过（已适配 `invalid` 分类的 raise 语义）
- `test_review_fixes.py`：57/57 通过（含 8 线程×300 次 find + 15 次 reload 并发压测 0 异常）
- `python3 -m py_compile`：全部模块编译通过

**风险提示**：F3 是破坏性语义变更（历史上 README 将其宣传为特性）。若下游已有代码依赖"`find()` 对坏输入返回 `None`"，升级需改为 `try/except` 或改用 `find_str()`。已同步更新 README §2/§3/§6/§7/§8/§10。
