# QZDB 多语言 SDK 标准测试与验证规范 (Test & Verification Specification)

本规范为 QZDB IP 数据库所有语言（Java、C#、Go、Rust、Python、Node.js、PHP、C 等）SDK 的**统一测试标准与验证流程**。
所有语言 SDK 的开发、重构与 Code Review 必须严格遵照本规范执行**三层递进验证**，严禁偷懒、严禁仅跑三两个硬编码 IP 敷衍交差。

---

## 一、 测试架构与三层验证体系 (3-Tier Verification Architecture)

```
                            QZDB 多语言 SDK 标准测试体系
  ┌──────────────────────────────────────────────────────────────────────────┐
  │ Tier 1: 纯逻辑与单元边界测试 (Unit & Boundary Defense Test)                │
  │   - 35+ 项边缘用例：非法 IP、损坏二进制、CRC32 fail-closed、无锁原子 reload  │
  ├──────────────────────────────────────────────────────────────────────────┤
  │ Tier 2: 地面真值全量比对 (Ground Truth Cross-Verification)               │
  │   - 10 大商业版本 (std/pro/max/asn/ult 国内+全球) × CSV 全量逐行比对 (0 偏差) │
  ├──────────────────────────────────────────────────────────────────────────┤
  │ Tier 3: 极限高并发与性能压测 (Benchmark & Concurrency Stress Test)       │
  │   - 16 线程 × 10万 QPS 并发无锁零竞争校验                                  │
  │   - 单线程 / 多线程 QPS 及微秒级 Latency 评估 (达到标量基准)               │
  └──────────────────────────────────────────────────────────────────────────┘
```

---

## 二、 Tier 1：纯逻辑与单元边界测试规范 (Unit Tests)

每个语言 SDK 的单元测试套件必须包含以下 **7 大核心测试分类 (30+ 必测断言)**：

### 1. 严格 IP 解析与格式规范测试 (Strict IP Parsing)
- **IPv4 严格性**：校验纯数字、四段式；拒绝前导零（如 `01.1.1.1`）、越界段（`256.1.1.1`）、缺段（`1.1.1`）、超长垃圾串。
- **IPv6 规范性**：校验双冒号压缩（`2001:db8::1`）与 8 组全展开（`2001:0db8:0000:0000:0000:0000:0000:0001`）解出绝对一致；拒绝带 Scope/Zone ID（如 `fe80::1%eth0`）。
- **IPv4-Mapped IPv6 自动降级**：校验 `::ffff:223.5.5.5` 以及十六进制形态 `::ffff:df05:0505` 正确剥离前缀，解出与 `223.5.5.5` **100% 相同**的结果。

### 2. 字段名归一化与 Getter 大小写/下划线不敏感测试 (Normalized Key Lookup)
- 校验 `info.get("country_code")` == `info.get("countryCode")` == `info.get("COUNTRY_CODE")` == `info.get("CountryCode")`。
- 未匹配字段必须安全返回空字符串 `""`（或 `null`/`None`/`Option::None`），严禁抛出 KeyError 或索引越界异常。

### 3. UsageType 21 场景官方映射与未知类型兜底测试 (UsageType Enum/Class)
- 校验官方 21 种场景（如 `CDN`, `Cloud`, `Broadband`, `AICrawler` 等）正确解析 display_zh / display_en / description。
- 传入未来未知场景字符串（如 `FutureUnknownType`）时，必须安全降级，`isKnown()` 为 false，并原样返回原始字符串。

### 4. 恶意输入与伪造/损坏文件安全防御测试 (Fail-Closed Robustness)
- **恶意字符串攻击**：传入超长字符串（10,000+ 字符）、带掩码 CIDR（`1.1.1.1/24`）、空串、纯空格，必须优雅捕获抛出 `ErrorCode.INVALID_IP`，**严禁死循环或内存越界**。
- **损坏二进制文件**：传入伪造 Magic（非 `QZDB`）、截断短文件（<192 字节 Header）、损坏偏移量的非法字节流，构造函数必须**Fail-Closed 拒绝初始化**，抛出 `ErrorCode.BAD_MAGIC` / `BAD_HEADER` / `CORRUPTED`。

### 5. CRC32 全文件流式校验与 Fail-Closed 测试
- 破坏文件第 200 字节后加载，`verifyCrc=true` 必须拒绝加载并抛出校验失败异常。
- 严禁 `crc == 0` 盲目放行。

### 6. 无锁热重载 (Reload) 与原子切换测试
- 运行 `reader.reload(newFile)` 时，校验在热重载过程中后台并发查询**零锁冲突、零脏读、零 NPE**。
- 当 `reload(junkFile)` 失败时，旧快照必须**继续正常服务**（影子替换失败不毁坏现有连接）。

### 7. CIDR 网段反查 API 测试 (`lookupCidr` / `lookupCidrBytes`)
- 测试 `lookupCidr("223.5.5.5")` 准确返回最具体网段 `223.5.0.0/17`。
- 测试 IPv6 `lookupCidr("2001:218::1")` 准确返回 `2001:218::/32`。
- 测试跳表直中叶子节点时自动从 Trie 根部补走还原深度的逻辑。

---

## 三、 Tier 2：地面真值全量逐行比对规范 (Ground Truth Cross-Verification)

此阶段是验证 SDK 解析代码**真实准确性与零幻觉**的最关键关卡！

### 1. 测试数据源标准 (Test Datasets)
测试集必须涵盖 `test_data_202608`（或仓库内最新测试商业库）的 **10 大全规格版本**：
1. `std/china/qqzeng_ip_std_china.qzdb` + 对应 CSV 真值
2. `std/global/qqzeng_ip_std_global.qzdb` + 对应 CSV 真值
3. `pro/china/qqzeng_ip_pro_china.qzdb` + 对应 CSV 真值
4. `pro/global/qqzeng_ip_pro_global.qzdb` + 对应 CSV 真值
5. `ult/china/qqzeng_ip_ult_china.qzdb` + 对应 CSV 真值
6. `ult/global/qqzeng_ip_ult_global.qzdb` + 对应 CSV 真值
7. `asn/china/qqzeng_ip_asn_china.qzdb` + 对应 CSV 真值
8. `asn/global/qqzeng_ip_asn_global.qzdb` + 对应 CSV 真值
9. `max/china/qqzeng_ip_max_china.qzdb` + 对应 CSV 真值
10. `max/global/qqzeng_ip_max_global.qzdb` + 对应 CSV 真值

### 2. 比对采样规则 (Sampling Rules)
- **逐行/抽样比对**：读取真实 CSV 文件的每一行 `start_ip`, `end_ip`（或 CIDR 中的代表 IP）。
- **字段全量比对**：将 SDK `find(ip)` 解析出的 `GeoInfo` 对象的每一个字段（从 6 维到 25 维）与 CSV 行中的期望文本逐字对比。
- **合格标准**：累计校验 **39,000,000+** 节点，**偏差必须为 0（100% 精确对齐）**。

### 3. 规范排除项说明 (Specification Exemptions)
- 依据 `QZDB_SDK_API.md` §9.7 规范，`::ffff:0:0/96` 等 5 条 IPv4-mapped IPv6 网段行在 V6 Trie 中有保留行，但规范强制剥离前缀走 V4 Trie。测试器中对此 5 条特定边界行需做显式排除说明，其余全量行必须 100% 对齐。

---

## 四、 Tier 3：高并发锁安全与性能压测规范 (Benchmark & Concurrency)

### 1. 压测样例生成与公平全覆盖机制 (无硬编码算法)

为了保证跨语言压测的**绝对公平、真实、全面与防 CPU 缓存欺骗**，压测 IP **严禁使用任何固定的三五个 IP 硬编码数组**！压测数据集必须使用以下三种动态生成算法之一：

#### 方案 A：从真实数据库随机全网段平滑散列采样 (全分布覆盖 - 推荐)
- **原理**：在压测初始化阶段，使用随机种子或按特定步长遍历全网 256 个 `/8` A 段，生成包含 **1,000,000 个随机真实 IP 预热池/循环队列**（覆盖真实数据的冷热节点、叶子节点与跳表命中节点）。
- **算法示例**（以伪代码为例）：
  ```python
  # 动态步长取模，覆盖 A/B/C/D 段全网段，防 CPU 分支预测缓存欺骗
  ip_str = f"{(i % 255) + 1}.{((i * 17) % 256)}.{((i * 131) % 256)}.{(i % 254) + 1}"
  ```

#### 方案 B：真实 CSV 地面真值数据全量全覆盖热跑 (Ground Truth Direct Stress)
- **原理**：直接将 Ground Truth CSV 中的前 100 万条 `start_ip` 加载为压测输入数组，模拟真实生产网段访问分布。

#### 方案 C：双栈协议 1:1 均衡压测 (50% IPv4 + 50% IPv6 充分深度压测 - 推荐)
- **比例划分**：**50% 动态 IPv4 + 50% 动态 IPv6**（其中 40% 为纯 IPv6 双冒号压缩/8组全展开多态，10% 为 IPv4-Mapped IPv6）。确保 IPv4 与 IPv6 两种检索树得到 1:1 充分、公正的极限吞吐压测！
- **动态 IPv6 生成算法示例**（伪代码）：
  ```python
  # 动态生成 IPv6 双冒号压缩与 8 组全展开混合流
  g1 = f"{((i * 31) % 0xFFFF):04x}"
  g2 = f"{((i * 17) % 0xFFFF):04x}"
  g3 = f"{((i * 131) % 0xFFFF):04x}"
  # 40% 纯 IPv6 压缩/展开多态 + 10% mapped (::ffff:x.x.x.x)
  if i % 5 == 0:
      v6_str = f"2001:{g1}:{g2}::{g3}" # 双冒号压缩
  elif i % 5 == 1:
      v6_str = f"2001:{g1}:0000:0000:{g2}:0000:0000:{g3}" # 8组全展开
  else:
      v6_str = f"::ffff:{(i%255)+1}.{((i*17)%256)}.{((i*131)%256)}.{(i%254)+1}" # mapped
  ```

---

### 2. 线程/协程并发安全测试 (Concurrency Safety Test)
- **测试配置**：至少 **16 线程/协程 $\times$ 100,000 次** 高频随机并发查询。
- **校验点**：全程无数据竞争（Data Race Free）、无 `NullPointerException` / `Panic` / `Segmentation Fault`，返回数据格式合法。

---

### 3. 性能评估与基准对齐 (Performance Benchmarks)
压测必须使用选定的全量库（如 `max/global/qqzeng_ip_max_global.qzdb`），进行预热后测试：
- **单线程 QPS**：目标达到 150 万 ~ 600 万 QPS（单次查询延迟 < 1 微秒）。
- **多线程扩展性**：4 线程 / 8 线程 / 16 线程阶梯测试，验证 CPU 多核线性扩展能力（16 线程吞吐目标突破 1,000 万 ~ 3,500 万 QPS）。
- **内存/GC 评估**：评估高频查询下的堆内存分配开销，目标趋近零堆分配 (Zero Allocations)。

---

## 五、 语言 SDK 验收交付 CheckList

在为某种语言编写/重构 SDK 提交 Pull Request 时，必须在交付报告中包含以下表格：

```markdown
## SDK 交付验收打钩清单 (QA CheckList)

- [ ] **Tier 1 单元测试**: 30+ 项单元与防爆断言全绿 (ALL PASSED)
- [ ] **Tier 2 Ground Truth 对齐**: 10 大版本 39,000,000+ 节点 CSV 比对 0 偏差 (100%)
- [ ] **Tier 3 高并发校验**: 16 线程 × 10万 QPS 无异常
- [ ] **规范对齐**:
  - [ ] 归一化 Key 匹配 (小写+去下划线)
  - [ ] UsageType 21 场景映射与未知降级
  - [ ] IPv4-Mapped 前缀剥离
  - [ ] CIDR 网段反查 (含跳表补走)
  - [ ] 无锁原子 Reload 架构
  - [ ] Fail-Closed CRC32 与边界防御
```
