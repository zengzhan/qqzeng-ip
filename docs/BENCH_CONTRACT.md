# QZDB Runtime Benchmark Contract

**版本**：v1.0
**性质**：`docs/QZDB_TEST_SPECIFICATION.md` §四（Tier 3 性能压测）的**强制性补充条款**，非替代。
**适用范围**：C / C# / Go / Java / Node.js / PHP / Python / Rust 八语言 SDK 的**全部性能基准**。
**依赖基线**：`multi-lang/API_CONTRACT.md`（行为契约）、`multi-lang/tools/golden_vectors.json`（正确性裁判）。

---

## 0. 为什么需要这份契约（背景）

`QZDB_TEST_SPECIFICATION.md` §四已经规定了 Tier 3 压测的骨架（固定种子、双栈 50/40/10、16 线程 × 10 万并发、P50/P99、环境声明、回归阈值 QPS↓>10% / P99↑>20% 即失败）。Java 的 `DualStackBenchmark.java` 是该规范的参考实现，且**已落地**。

但截至本契约发布时，其余 6 种语言的 bench（`bench_qps.c` / `cmd/bench` / `bench_qps.py` / `bench_qps.rs` / `bench_all.js` / `bench_all.php`）**均不符合 §四**，且全部缺少一项对 QZDB 至关重要的维度：

> **`entryId` 解码缓存的命中率对 IP 分布极度敏感。**
> 现有 6 个 bench 用 `rand()` 生成**均匀、互不相关**的 IP —— 这系统性低估了缓存收益（最坏情况分布）；
> Java 用有限池重复采样 —— 偏向热点（最好情况分布）。
> 没有统一的分布定义，任何"优化后 X%"的结论都**不可证伪**，跨语言数字也无法比较。

本契约做三件事：
1. 规定一个**语言无关、逐字节可复现**的参考 RNG，使 8 语言生成**完全相同**的查询数组；
2. 把压测工作负载从"单一混合分布"升级为 **四种分布**（random / hot / sequential / real_world），以包围缓存的真实行为；
3. 规定一个**规范化 JSON 输出 schema** 与 **baseline 回归门禁**，让性能像正确性一样可被 CI 冻结。

**本契约是其余一切 Runtime 优化（见 `RUNTIME_PROPOSAL_ASSESSMENT_20260810.md` 第 3 节 4 条真缺口）的决策门。未用本契约口径测得瓶颈前，任何"性能优化"不得合入。**

---

## 1. 强制合规范围

以下条目为**强制**，任何语言的 bench 不满足即视为 Tier 3 不通过：

1. 使用 §3 参考 RNG 生成全部查询数组（**禁止** `rand()` / `Math.random()` / `random.Random` 无种子，或硬编码 IP 数组）。
2. 覆盖 §4 四种分布，每种分布报告 §6 全部指标。
3. 输出 §7 规范化 JSON 到 `multi-lang/bench_reports/<lang>_<edition>.json`。
4. 在 JSON 中完整填写 §8 环境声明。
5. 双栈：每种分布分别报告 v4-only / v6-only / mixed(50/40/10) 三模式（见 §5）。
6. 接入 §9 baseline 回归门禁（CI 中，非 `|| true`）。

---

## 2. 规范版本与数据基

| 项 | 取值 | 说明 |
|----|------|------|
| `bench_contract` 版本字段 | `"QZDB_BENCH_CONTRACT v1.0"` | 写入 JSON 顶层 |
| 主测库（必测） | `max/global/qqzeng_ip_max_global.qzdb`（117 MB） | 大库，缓存压力与 trie 深度最坏 |
| 小库（必测） | `std/china/qqzeng_ip_std_china.qzdb`（8.6 MB） | 快路径、低基数，作为下限对照 |
| 可选库 | `ult/global/qqzeng_ip_ult_global.qzdb`（122 MB） | 内存峰值压力测试 |
| 数据根目录 | `multi-lang/test_data_202608/` | 路径解析见下 |

**路径解析**（每种语言须按序尝试，命中即停，与 Java 当前策略一致）：
```
<lang>/../test_data_202608/<edition>/<region>/<file>
multi-lang/test_data_202608/<edition>/<region>/<file>
../test_data_202608/<edition>/<region>/<file>
test_data_202608/<edition>/<region>/<file>
```
> 现状不一致问题：Rust bench 指向 `std/china`、Go/Python 指向 `../data/`、Java 指向 `max/global`。本契约统一为"小库 + 大库双测"，消除口径漂移。

---

## 3. 参考 RNG（splitmix64，逐字节可复现）

所有查询数组必须由以下算法生成。**八语言必须产出逐字节相同的 u64 序列**（已验证：该算法在 C/Go/Rust/Java/Python/Node/PHP/C# 下用 64 位无符号整数实现结果一致；右移为**逻辑右移**，乘法/加法按 `2^64` 取模）。

```
SEED = 0x0134F107   // = 20260807，与 Java DualStackBenchmark 保持连续

state : u64 = SEED

function next_u64() -> u64:
    state = (state + 0x9E3779B97F4A7C15) mod 2^64
    z = state
    z = ((z XOR (z >>> 30)) * 0xBF58476D1CE4E5B9) mod 2^64
    z = ((z XOR (z >>> 27)) * 0x94D049BB133111EB) mod 2^64
    return z XOR (z >>> 31)

// 辅助
u32()  = next_u64() & 0xFFFFFFFF
u128() = (next_u64() << 64) | next_u64()         // 大端语义：high 在前
```

**确定性约定**：每种分布/模式独立"消费" RNG 流，顺序严格按 §4 伪代码。各语言须**按相同顺序调用 `next_u64`**，不得为"好看"而重排，否则跨语言数组失配。

> 参考实现建议：新增 `multi-lang/tools/bench_gen.py`，按本契约生成并落盘 `bench_vectors.json`（含四种分布 × 双栈的 IP 数组），作为八语言 bench 的**公共输入**，从根上消除"各语言各自生成、互不可比"。（该工具属于实现步骤，不在本契约强制之内，但强烈建议。）

---

## 4. 四种分布（强制）

`OPS = 2_000_000`（单线程热跑每分布每模式）；`WARMUP_OPS = 1_000_000`；`COLD_OPS = 200_000`。
预热池（每个 bench 进程生成**一次**，全语言相同）：

```
POOL_HOT_V4 = 4096 个 distinct u32   // 由连续 4096 次 u32() 填充
POOL_HOT_V6 = 1024 个 distinct u128   // 由连续 1024 次 u128() 填充
```

对每个查询索引 `i ∈ [0, OPS)`：

### 4.1 random —— 全空间均匀（最冷）
- v4：`ip = u32()`
- v6：`ip = u128()`
- 语义：无局部性、缓存命中率最低，逼近 trie walk 与 decode 的"裸成本"。

### 4.2 hot —— 小工作集重复（最热）
- v4：`ip = POOL_HOT_V4[ u32() % 4096 ]`
- v6：`ip = POOL_HOT_V6[ u32() % 1024 ]`
- 语义：高重复率 → 高缓存命中、稳态。这是 `entryId` 缓存收益的上界，也是 Java 现有有限池思路的强化版（Java 池 500k 偏"温"，本契约 4096 偏"热"，二者共同覆盖谱）。

### 4.3 sequential —— 单调游走
- v4：`base4 = u32()`（每进程固定）；`ip = (base4 + i) mod 2^32`
- v6：`base6 = u128()`（每进程固定）；`ip = (base6 + i) mod 2^128`
- 语义：无缓存局部性但对 trie 遍历顺序友好，可暴露 prefetch / 分支预测相关差异。

### 4.4 real_world —— 偏斜真实流量**代理**（proxy）
对每 `i`：
```
r = u32() % 10
if r < 6:   ip = hot 池采样          // 60% 热点
elif r < 9: ip = random 采样         // 30% 随机
else:       ip = sequential 采样     // 10% 单调
```
- 语义：**明确标注为 proxy**，非真实流量。其唯一用途是提供一个"有偏斜"的工作负载，介于 hot 与 random 之间。
- 若未来获得**脱敏的真实查询日志**，以其替代本代理，并在 JSON 中记录 `provenance: "real_anonymized_log"`。

> 任一分布的 v6 `ip` 在写入字节序列时一律采用**大端 16 字节**（high 在前），与现有 `find_v6` / `find_bytes` API 约定一致。

---

## 5. 双栈三元模式

每种分布须报告三种模式，输入序列由同一 RNG 流按固定交织生成，确保跨语言一致：

| 模式 | 占比 | 生成规则（每查询 `i`） |
|------|------|------------------------|
| `v4` | 100% | 仅 v4 查询（按 §4 该分布规则） |
| `v6` | 100% | 仅 v6 查询（pure v6 80% + mapped 20%，mapped 由 `::ffff:` 前缀从对应 v4 值构造） |
| `mixed` | 50/40/10 | `i%10<5` → v4；`<9` → v6-pure；else → v6-mapped |

`mapped` 构造：取该分布的 v4 值 `a.b.c.d`，输出字节序列 `0000:0000:0000:0000:0000:FFFF:aabb:ccdd`（标准 IPv4-Mapped，SDK 内部自动剥离前缀走 v4 trie）。

---

## 6. 指标（每 分布 × 模式 × 线程配置）

每次测量须记录：

| 字段 | 类型 | 说明 |
|------|------|------|
| `ops` | int | 本次测量操作数 |
| `qps` | float | ops / 秒 |
| `avg_ns` | float | 平均单次延迟（纳秒） |
| `p50_ns` | int | 50 分位 |
| `p95_ns` | int | 95 分位 |
| `p99_ns` | int | 99 分位（§四强制） |
| `p999_ns` | int | 99.9 分位（可选） |
| `alloc_bytes_per_op` | float | 每次操作分配字节（best-effort；C/Rust/Go 零分配记 0；托管语言用 GC 分配计数器） |
| `cache_hit_rate` | float | entryId 缓存命中率（best-effort；语言若埋点则报告，否则省略该键） |
| `errors` | int | **必须为 0**，否则本次测量作废 |
| `warm` | `"cold"`\|`"hot"` | cold = open 后首 `COLD_OPS` 空缓存；hot = `WARMUP_OPS` 预热后 |

**延迟采样**：每 `LAT_SAMPLE_EVERY = 20` 次采样一次单次 `find` 耗时（环内仅计时、不格式化），采样数组排序后取分位。禁止在测量环内做日志/字符串格式化。

---

## 7. 规范化 JSON 输出 schema

每种语言输出到 `multi-lang/bench_reports/<lang>_<edition>.json`，顶层结构：

```json
{
  "contract": "QZDB_BENCH_CONTRACT v1.0",
  "language": "java",
  "sdk_version": "1.0.4",
  "timestamp": "2026-08-10T15:43:27+08:00",
  "seed": 20260807,
  "db": {
    "path": "multi-lang/test_data_202608/max/global/qqzeng_ip_max_global.qzdb",
    "edition": "max_global",
    "bytes": 117127664,
    "hash": "crc32:xxxxxxxx"
  },
  "environment": {
    "cpu": "Apple M4 Pro",
    "cores": 14,
    "ram_gb": 24,
    "os": "macOS 15.5 arm64",
    "runtime": "OpenJDK 21.0.4",
    "compiler": "javac 21 (no 3rd-party deps, -encoding UTF-8)",
    "bench_contract": "v1.0"
  },
  "distributions": {
    "random":    { "v4": {...§6}, "v6": {...§6}, "mixed": {...§6, threads:{...}} },
    "hot":       { "v4": {...§6}, "v6": {...§6}, "mixed": {...§6, threads:{...}} },
    "sequential":{ "v4": {...§6}, "v6": {...§6}, "mixed": {...§6, threads:{...}} },
    "real_world":{ "v4": {...§6}, "v6": {...§6}, "mixed": {...§6, threads:{...}} }
  },
  "string_roundtrip": {
    "hot": { "mixed": {...§6, "api": "string"} }
  },
  "concurrency_safe": true
}
```

`threads` 对象示例：`{ "1": {...§6}, "8": {...§6}, "16": {...§6} }`（详见 §8 线程扩展）。

---

## 8. 预热、线程扩展与并发安全

- **预热**：open 后先跑 `WARMUP_OPS` 混合查询（不计时），再测 `hot` 阶段。
- **线程扩展**（每分布每模式报告）：`1 / 2 / 4 / 8 / 16` 线程；`32` 线程可选（硬件允许时）。多线程总 ops = `MULTI_OPS = 2_000_000` 按线程均分。共享同一 `QzdbReader` 实例，验证无锁快照并发安全。
- **并发安全门**（§四强制）：`16 线程 × 100_000` 双栈混合查询，全程 `errors == 0` 且 `done == 1_600_000`；任一语言须以原生 race detector（Go `-race` / Rust TSan / Java Concurrent）佐证或显式声明未启用。
- **string_roundtrip**：仅对 `hot.mixed` 额外跑一次 `find(string)`（字符串解析路径），与 `find_uint` 对照，分离"解析开销"与"解码缓存开销"。

---

## 9. Baseline 与回归门禁（CI）

1. 每种语言在 `multi-lang/bench_reports/baseline/<lang>_<edition>.json` 提交**基线**（首次由该语言合规 bench 产出）。
2. CI（`verify.yml`，**禁止使用 `|| true`**）在 Tier 3 阶段运行合规 bench，将 `hot` 分布（缓存敏感、信息量最大）的 QPS 与 P99 对比基线：
   - `hot.mixed` QPS 下降 **> 10%** → 失败
   - `hot.mixed` P99 上升 **> 20%** → 失败
   （阈值沿用 `QZDB_TEST_SPECIFICATION.md` §六.4。）
3. 回归仅以 `hot` 分布为硬门禁；`random` / `sequential` / `real_world` 仅记录、不参与失败判定（避免噪声误杀）。
4. 性能门禁与现有 L1–L4 正确性门禁**并列**，不得互相 `|| true` 吞错。

---

## 10. 当前合规矩阵（发布本契约时的实况）

| 语言 | 参考 RNG | 四分布 | P50/P99 | 线程扩展 | JSON | 环境声明 | 结论 |
|------|:---:|:---:|:---:|:---:|:---:|:---:|------|
| **Java** `DualStackBenchmark` | ✅(有限池) | ⚠️仅混合 | ✅ | ✅4/8/16 | ✅ | ✅ | 最接近，缺四分布与 real_world |
| C `bench_qps.c` | ❌rand | ❌ | ❌ | ❌ | ❌ | ❌ | 不符合 §四 |
| Go `cmd/bench` | ❌rand | ❌ | ❌ | ❌ | ❌ | ❌ | 不符合 §四 |
| Python `bench_qps.py` | ❌Random | ❌ | ❌ | ❌ | ❌ | ❌ | 不符合 §四 |
| Rust `bench_qps.rs` | ❌LCG | ❌ | ❌ | ❌ | ❌ | ❌ | 不符合 §四 |
| Node `bench_all.js` | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | 不符合 §四 |
| PHP `bench_all.php` | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | 不符合 §四 |
| C# | —（无独立 bench） | ❌ | ❌ | ❌ | ❌ | ❌ | 缺 bench |

> 注：Java 的"有限池"等价于本契约 `hot` 的弱版；其余 6 语言需按本契约从零补齐。这是把"唯一 P0"落到实处的清单。

---

## 11. 与 4 条真缺口的关系（决策门语义）

`RUNTIME_PROPOSAL_ASSESSMENT_20260810.md` 第 3 节判定：

- **P0 统一基准口径** → 即本契约。无本契约，下面三条无从证伪。
- **P1 Rust 去 Mutex** → 用本契约 `hot` 分布在 8/16 线程下观察同槽争用；若 QPS 不随线程线性扩展且 `p99` 显著高于 C/Go，则进入优化。
- **P1 C# mmap / Rust 双拷贝** → 用 `ult_global`（122 MB）测加载峰值与首查延迟；C# 缺 mmap、Rust `from_file` 2× 拷贝由此暴露。
- **P2 原生数值旁路** → 用 `string_roundtrip.hot` 测 `GeoInfo.GetXxx()` 数值往返占比；占比 > 15% 才值得付出缓存内存翻倍的代价。

**结论**：本契约冻结的是"性能"这一目前唯一未被冻结的维度。格式冻结靠自律，行为冻结靠 4102 条 golden 向量，性能冻结靠本契约 + baseline。

---

## 12. 实施顺序（建议，非本契约强制）

1. 落地 `multi-lang/tools/bench_gen.py`（§3 参考实现），产 `bench_vectors.json`，人工核对八语言数组逐字节一致。
2. 各语言 bench 改为消费公共向量（或按 §3/§4 自实现 RNG，二者等价），补齐四分布 + 双栈三元 + P50/P99 + 线程扩展 + JSON + 环境声明。
3. 提交 `baseline/`，把回归门禁挂进 `verify.yml`（不用 `|| true`）。
4. 拿到数据后，按第 11 节门槛决定 P1/P2 是否进入代码优化。
