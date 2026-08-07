# QZDB Python SDK

官方 QZDB（离线 IP 地理定位数据库）Python SDK。纯离线、零依赖、零分配查询路径，支持 IPv4/IPv6 单条/批量/流式查询、CIDR 反查、多库链式合并与无锁热更新。

> 规范版本 **v2.4**。所有语言行为以 [`../API_CONTRACT.md`](./API_CONTRACT.md) 为唯一事实来源；跨语言正确性由 `../tools/golden_vectors.json` 裁判（已用 C# 独立校验 4102/4102 通过）。

---

## 目录

1. [安装](#1-安装)
2. [加载（Builder / 内存 / 热更新）](#2-加载)
3. [单条查询 API](#3-单条查询-api)
4. [字段投影 `find_fields`](#4-字段投影-find_fields)
5. [低级行号 / CIDR 反查](#5-低级行号--cidr-反查)
6. [`GeoInfo` 响应实体](#6-geoinfo-响应实体)
7. [批量 / 流式](#7-批量--流式)
8. [多库合并：`QzdbRegistry` / `ChainedReader`](#8-多库合并qzdbregistry--chainedreader)
9. [元信息自省](#9-元信息自省)
10. [错误处理](#10-错误处理)
11. [性能说明](#11-性能说明)
12. [测试](#12-测试)
13. [数据更新与维护](#13-数据更新与维护)

---

## 1. 安装

```bash
pip install qzdb            # 发布后；或直接从源码使用：
# 将 qzdb.py 放到你的项目里即可，无第三方依赖
```

```python
from qzdb import QzdbReader
```

---

## 2. 加载

```python
from qzdb import QzdbReader

# 从文件加载（group_index=0 主组；ASN 组通常为 2）
r = QzdbReader('qqzeng_ip_ult_china.qzdb', group_index=0, verify_crc=True)

# 从内存字节加载
with open('qqzeng_ip_ult_china.qzdb', 'rb') as f:
    r = QzdbReader(f.read())

# 热更新：构建完整新快照后原子替换；新快照强制 CRC，失败则旧快照继续服务
r.reload('qqzeng_ip_ult_china_new.qzdb')
r.reload_buffer(byte_data)

# 资源释放（幂等、可多次调用）
r.close()
```

- 加载失败（文件不存在、Magic≠`QZDB`、HeaderVersion≠1、CRC 不匹配、截断）**Fail-Closed 拒绝初始化**，不会部分加载。
- 默认 `verify_crc=True`；仅对受信数据/基准测试可设 `verify_crc=False`。

---

## 3. 单条查询 API

| 方法 | 说明 | 未命中 / 非法 IP |
|------|------|----------------|
| `find(ip: str) -> GeoInfo \| None` | 按 IP 字符串查询（自动识别 v4/v6，含 `::ffff:` 降级） | `None` |
| `find_uint(ip: int) -> GeoInfo \| None` | 按 IPv4 整数查询 | `None` |
| `find_v6_uint(ip: int) -> GeoInfo \| None` | 按 IPv6 整数查询 | `None` |
| `find_v6_bytes(ip: bytes) -> GeoInfo \| None` | 按 16 字节 IPv6 查询 | `None` |
| `find_bytes(ip: bytes) -> GeoInfo \| None` | 4 字节→v4，16 字节→v6 | `None` |
| `find_str(ip: str) -> str` | 返回 `to_pipe()` 字符串 | `""` |

```python
gi = r.find('223.5.5.5')
if gi is None:
    print('not found')
else:
    print(gi.to_pipe())

# IPv6
gi = r.find('2408:4004:10:1::1')
v6 = r.find_bytes(bytes.fromhex('24084004100000010000000000000001'))
```

> 非法 IP 一律返回 `None`（不抛异常）。未命中同样返回 `None`，调用方以 `None` 区分"未命中/坏输入"，与契约一致。

---

## 4. 字段投影 `find_fields`

只解析指定字段，减少池读取开销：

```python
gi = r.find_fields('223.5.5.5', ['country', 'province', 'city', 'isp'])
print(gi.get('country'), gi.get('city'))
```

`fields=None` 等价于 `find`。

---

## 5. 低级行号 / CIDR 反查

```python
# 低级行号
row_id = r.lookup_row_id('223.5.5.5')        # 0 = 未命中/非法
row_id = r.lookup_row_id_uint(0xDF050505)
row_id = r.lookup_row_id_v6_bytes(v6_bytes)
geo_id, asn_id, usage_id = r.lookup_ids(row_id)   # 越界返回 None

# CIDR 反查（数据库本身不存 CIDR，由 Trie 叶子深度重建）
cidr = r.lookup_cidr('223.5.5.5')          # 例: "223.5.5.0/24"
cidr = r.lookup_cidr_uint(0xDF050505)      # IPv4 整数
cidr = r.lookup_cidr_bytes(v6_bytes)       # 16 字节 / 4 字节
```

- 网络地址 = IP 高 N 位清零，前缀长度 N = Trie 叶子深度；Jump Table 直接命中叶子时自动还原深度，不返回错误网段。
- IPv6 按 RFC 5952 压缩输出（如 `2001:218::/32`）。
- 未覆盖返回 `None`。

---

## 6. `GeoInfo` 响应实体

```python
gi = r.find('223.5.5.5')

# 字段访问：大小写/下划线/连字符不敏感，缺失返回 ""（绝不抛 KeyError）
gi.get('country') == gi.get('COUNTRY') == gi.get('country-code')   # True

# 语义化 getter
gi.country          # "中国"
gi.province         # "广东"
gi.city             # "深圳"
gi.isp              # "电信"
gi.longitude        # 113.95 (float) / None
gi.latitude         # 22.55 (float) / None
gi.get_geo_id()     # int / None
gi.get_asn()        # int / None
gi.get_usage_type() # UsageType 实例
gi.get_cidr()       # 恒返回 ""（CIDR 不是字段，用 reader.lookup_cidr）

# 序列化
gi.to_pipe()        # "中国|广东|深圳|...|113.95|22.55"  字段以 | 拼接
gi.to_pipe_string() # 别名
gi.to_dict()        # {field: value} 全字符串
gi.to_map()         # 别名
gi.to_json()        # 手写 JSON：longitude/latitude/asn/geo_id 为数字，其余字符串

print(gi)           # 等价于 to_pipe()
```

原生浮点已在解码时格式化为 **6 位小数**（如 `116.400000`，整数值 `116.0`→`116`），`to_pipe()` 直接拼接，不做二次格式化（保证跨语言 golden 一致）。

### `UsageType`

```python
ut = gi.get_usage_type()
ut.raw_value()        # "CDN"
ut.get_display_zh()   # "CDN"
ut.get_display_en()   # "CDN"
ut.is_known()         # True
```

21 个预定义场景（AICrawler / Backbone / Broadband / Business / CDN / Cloud / DNS / DataCenter / Education / Finance / Government / ISP / IXP / IoT / Mobile / Reserved / Satellite / Spider / Streaming / Unknown / VPN）+ 未知兜底（不抛异常）。

---

## 7. 批量 / 流式

```python
ips = ['223.5.5.5', '8.8.8.8', '2408:4004:10:1::1']

# 批量：逐条保留三态，内部不起线程池
results = r.find_batch(ips)
for b in results:
    # b.ip, b.geo_info (GeoInfo|None), b.error (QzdbError|None)
    if b.geo_info:
        print(b.ip, b.geo_info.to_pipe())

results = r.find_batch_fields(ips, ['country', 'isp'])

# 流式：内存恒定，不累积
for gi in r.find_stream(ips):
    if gi:
        print(gi.to_pipe())
```

每个 `BatchResult` 含 `ip` / `geo_info` / `error` 三态；未命中 `geo_info=None`，非法输入 `error` 为 `None` 且 `geo_info=None`。

---

## 8. 多库合并：`QzdbRegistry` / `ChainedReader`

```python
from qzdb import QzdbRegistry, ChainedReader

# 注册表：按名称持有多个 reader，查询返回首个命中
reg = QzdbRegistry()
reg.register('std', QzdbReader('qqzeng_ip_std_china.qzdb'))
reg.register('ult', QzdbReader('qqzeng_ip_ult_china.qzdb'))
gi = reg.find('223.5.5.5')            # 依次尝试，首个命中
row = reg.lookup_row_id('223.5.5.5')

# 链式：有序 reader 列表，返回首个命中
chained = ChainedReader([reg.get('std'), reg.get('ult')])
gi = chained.find('223.5.5.5')
```

两者都提供 `find` / `find_uint` / `find_bytes` / `find_fields` / `lookup_row_id` / `lookup_cidr`，以及 `close()`。

---

## 9. 元信息自省

```python
r.version            # 版本名（如 "ult"）
r.get_edition()      # 版本层级：std/asn/pro/max/ult
r.get_field_names()  # 字段名列表
r.has_field('country_code')   # True（归一化匹配）
r.get_group_count()  # group 数量
r.pool_count         # 池数量
r.get_description()  # 数据库描述
r.get_build_time()   # 构建时间（内部整数）
r.get_file_hash()    # CRC32 十六进制 8 位小写（如 "63dec0ca"）
r.verify_crc()       # CRC 校验是否通过 (bool)
r.get_scope()        # 恒返回 ""（保留字段）
```

---

## 10. 错误处理

所有加载/校验失败抛出 `QzdbError`（含 `code` 属性）：

```python
from qzdb import QzdbError

try:
    QzdbReader('missing.qzdb')
except QzdbError as e:
    print(e.code)   # NOT_FOUND / CORRUPTED / BAD_MAGIC / BAD_HEADER / UNSUPPORTED / INVALID_PARAM / OUT_OF_BOUNDS
```

| code | 触发场景 |
|------|---------|
| `NOT_FOUND` | 文件不存在 |
| `BAD_MAGIC` | 文件头 Magic ≠ `QZDB` |
| `UNSUPPORTED` | HeaderVersion ≠ 1 |
| `CORRUPTED` | CRC 不匹配 / 截断 / 越界段 |
| `BAD_HEADER` | 头部尺寸异常 |
| `INVALID_PARAM` | 参数非法（如 reload buffer 为空） |

> 查询期非法 IP 不抛异常、返回 `None`；只有加载/校验阶段 Fail-Closed 抛 `QzdbError`（与契约 §4 一致：Python 在未命中与坏输入上均返回空值）。

---

## 11. 性能说明

- **不可变快照 + 原子替换**：`reload`/`reload_buffer` 先构建完整新快照再一次性交换，查询期对快照只读，无锁热更新。
- **per-snapshot 有界无锁 GeoInfo 缓存**：以 `(group_index, entry_id)` 为键，开放寻址；缓存命中趋近零分配。容量满后仅跳过缓存、重算，**碰撞绝不返回错值**。
- **零拷贝解析**：大文件走 `mmap` 懒加载；字段名归一化索引在加载期构建一次，查询期 O(1)。
- 查询路径避免每请求新建字符串数组 / `GeoInfo` 的不必要分配（缓存命中直接复用）。

---

## 12. 测试

```bash
python3 test_tier1.py     # Tier1 单元测试（无数据库即可运行，≥50 断言）
python3 test_golden.py    # Tier2 黄金校验（对 golden_vectors.json 0 偏差）
```

- **Tier1**：严格 IP 解析、Mapped 降级、字段归一化、UsageType 21+未知兜底、损坏文件 Fail-Closed、CRC 强制、无锁 Reload、CIDR 反查、资源释放、批量/流式/注册表。
- **Tier2**：加载 `data/qqzeng_ip_std_china.qzdb` 与 `qqzeng_ip_ult_china.qzdb`，对每个 IP 断言 `find(ip).to_pipe() == expected`，**必须 0 失败**。
- **Tier3（性能，建议）**：16 线程 × 10 万双栈混合查询无异常；单/多线程 QPS 报告。

---

## 13. 数据更新与维护

1. 从官方渠道获取新版 `.qzdb`（`.qzdb` 为付费数据，不入库）。
2. 放入 `../data/` 或指定路径，`QzdbReader(path)` 重新加载即可；线上热更新用 `reader.reload(path)`（原子替换，不影响在途查询）。
3. 字段新增/布局变化由文件头 `GROUP_SCHEMA` / `ROW_SCHEMA` 自描述，SDK 自动适配，无需改代码。
4. 多语言行为以 `../API_CONTRACT.md` 为准；新增 IP 样本后重建 `tools/golden_vectors.json` 并跑全语言 Tier2 校验。
