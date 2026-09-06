# QZDB 性能数字统一口径(Performance Claims — Single Source of Truth)

> 本文是全部对外性能数字的**唯一事实来源**。任何 README / 文档 / 宣传材料引用性能数字时,
> 必须来自本文的表格,并遵循「数字 + 口径 + 复现方式」三要素格式。禁止出现无口径的裸数字。
>
> 数据基准日:2026-09-05 · 环境:Apple M4 Max(14 核)/ macOS · 数值为单线程 best-of-3。
> 方法学:docs/BENCH_CONTRACT.md v1.0(splitmix64 固定种子 / 四分布 / FNV 指纹对拍 / 并发安全门禁)。

## 一、三种标准口径

| 口径 | 输入 | 数据 | 场景含义 | 数据来源 |
|------|------|------|----------|----------|
| **A. 整型随机(缓存最不利)** | 整型 API(无 IP 字符串解析) | 50 万随机散布 IP,省级大陆库(8.6MB) | 容量规划用的**保守承诺值** | contract `random` 分布;PHP/Node 为对等直测探针(Java 已于 2026-09-06 纳入 contract) |
| **B. 热点缓存(理想上限)** | 整型 API | 同 IP 重复查询(命中解码缓存) | 理想情形展示值,必须挂在 A 之后出现 | contract `hot.mixed` |
| **C. 字符串查询(find_str)** | 字符串 API(含 IP 解析) | demo 样本库(4.5MB),63 个固定 IP | CI 门禁口径,跨语言可比 | `tools/perf_gate.py` |

## 二、汇总表(8 语言 × 3 口径)

**口径 A|整型随机(缓存最不利)**

| 语言 | 吞吐 | 数据来源 |
|------|-----------:|----------|
| Node.js  | 99.4M | 直测探针¹ |
| C        | 96.5M | contract |
| Go       | 74.7M | contract |
| Java     | 48.1M | contract(09-06 纳入) |
| Rust     | 43.3M | contract |
| C#       | 33.1M | contract |
| PHP      | 5.67M | 直测探针¹ |
| Python   | 0.97M | contract |

**口径 B|热点缓存(理想上限)与 口径 C|字符串 find_str**

| 语言 | B 热点 | C find_str |
|------|-----------:|-----------:|
| C        | 99.7M | 10.8M |
| Go       | 75.9M | 9.2M |
| C#       | 84.6M | 11.0M |
| Rust     | 46.1M | 8.4M |
| Python   | 1.08M | 273K |
| Node.js  | 5.04M | 5.5M |
| Java     | 64.3M | 11.9M |
| PHP      | —²    | 429K |

¹ 直测探针 = 与 contract 基准同场景(50 万随机散布、整型输入、省级库)的对等独立测量
  (Java 已于 2026-09-06 补齐契约基准脱离此列;Node/PHP 仍为探针)。
² PHP 的 contract 分布主循环每次查询含 GMP 流推进开销(基准自身开销,非 SDK),
数字低于 SDK 真实整型能力(直测探针 5.67M);B 列留空待 BENCH_CONTRACT v1.1
统一输入形态后回填。

¹ 该语言的 contract 分布主循环输入形态与 A/B 口径定义不一致(PHP 含 GMP 流开销、Node 按字符串),
为避免跨口径混报,此处不填;统一化改造见 BENCH_CONTRACT v1.1 计划。

**库规模效应(必须了解)**:同一语言在 122MB 全球库上,A 口径普遍回落约 3 倍
(C 96.5M→13.7M、Go 74.7M→23.9M、Rust 55.4M→4.4M)——内存型数据库的缓存亲和物理规律,宣传时不得只引用小库数字。

## 二·五、与原版产品及同类格式对比(外部参照)

同机(Apple M4 Max)权威第三方/原版数据,来源:发布仓 `docs/benchmark-comparison.md`
(原版作者 BenchmarkDotNet v0.15 实测,单次查询纳秒级 + 随机亿级批量):

| 产品 / 格式 | 随机亿级吞吐 | 单次查询 |
|-------------|------------:|---------:|
| ip2region xdb(Content 全缓存) | 628 万/s | 45 ns |
| 原版 qqzeng-ip 2.0(.dat) | 950 万/s | 29.9 ns |
| 原版 qqzeng-ip 3.0(.db) | 1555 万/s | 17.5 ns |
| **原版 qqzeng-ip 6.0(.db,经典版最快)** | **3000 万/s** | ≈33 ns |
| **qzdb C#(本 SDK,口径 A)** | **33.1M** | ≈30 ns |
| **qzdb Go / C / Node(口径 A)** | **74.7M / 96.5M / 99.4M** | ≈10-13 ns |

解读要点(引用时须连同比照条件):
- qzdb C# 与原版最快经典版(6.0)同量级;Go/C/Node 为其 2.5~3.3 倍;
- 比照时注意数据内容差异:原版 6.0 基于全量源数据(约 15~20MB 库),qzdb A 口径基于省级
  8.6MB 库,字段集也不同(经典版为 4 字段,qzdb 为 25~29 字段全量解码)——qzdb 在
  **解码更多字段**的前提下达到同量级吞吐;
- 历史宣传「500 万+ QPS 极速内存解析」为原版早期硬件时代的口径,在当前硬件上,
  qzdb 各编译语言 SDK 已超出该阈值 6~20 倍(见第二节 A 列)。

## 三、复现方式

```bash
# 口径 A/B(contract 契约基准,c/go/rust/python,含并发与四分布)
cd multi-lang && BENCH_OPS=2000000 <lang>/bench_contract*    # 各语言入口见其 README

# 口径 C(CI 门禁,8 语言全量,数据无关可跑)
cd multi-lang && python3 tools/perf_gate.py                  # 默认 8 语言

# 基线回归对比(本地细粒度,hot.mixed QPS -10% 或 P99 +20% 即失败)
python3 tools/perf_gate.py --save baseline.json
python3 tools/perf_gate.py --baseline baseline.json --tol 0.25
```

历史与最新基准报告:multi-lang/bench_reports/*.json(随仓库版本化,含 environment 字段)。

## 四、宣传使用规则

1. **三要素**:每个数字必须同时给出「数值 + 口径(A/B/C)+ 复现入口」,缺一不写。
2. **承诺用 A(最不利),展示用 B,跨语言对比只用 C**:热点数字不得单独出现。
3. **库规模标注**:引用 A 口径时注明「省级 8.6MB 库」;全球库(122MB)数字约低 3 倍,需并列或注明。
4. **环境声明**:注明基准硬件(本文为 Apple M4 Max;Intel/AMD x86 通常低 2~4 倍)。
5. **禁止**:跨语言挪用数字(C 的数字不得为 PHP 背书)、无口径裸数字、只有最好情形。
6. **CI 背书**:门禁 8/8 语言全覆盖,任何回退会在 CI 被拦截——文案可放心引用"数字由 CI 门禁守护"。

## 五、口径变更流程

修改本文表格必须同时:① 重跑对应语言 contract 基准并提交 bench_reports JSON;
② CHANGELOG 记录;③ 检索全部 README 中引用旧数字处并同步更新。
