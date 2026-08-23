# QZDB 开发目录 → GitHub 仓库规范同步全流程指南 (2026 最新版)

> **核心原则**：
> 1. 开发修改在本地开发仓库 `qzdb` 完成全量测试与本地提交；
> 2. 同步到 GitHub 发布仓库 `qqzeng-ip` 时，只同步源码与使用文档，绝对杜绝内部测试文件与商业 `*.qzdb` 数据库；
> 3. **GitHub 目录描述展示机制**：必须按**“先子语言目录、再顶级产品目录”**的严格拓扑顺序独立 Commit，确保 GitHub 网页文件列表中每个目录右侧均显示最新、专业的描述文案。

---

## 一、目录映射规范

| 环境 | 本地绝对路径 | 说明 |
|:---|:---|:---|
| **本地开发仓库 (Dev)** | `/Users/zengxiangzhan/ZengData/IP数据库/qzdb` | 包含完整测试用例、10个真实数据库、跨语言比对工具 |
| **GitHub 发布仓库 (Local)** | `/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip` | 面向公开开源的代码仓库 |
| **SDK 子目录映射** | `qzdb/multi-lang/` ➔ `qqzeng-ip/ip-qzdb-sdk/` | QZDB 多语言 SDK 目标目录 |
| **远程 GitHub 地址** | `https://github.com/zengzhan/qqzeng-ip.git` | `origin/main` |

---

## 二、一键自动化同步（推荐首选）

开发仓库已内置官方全自动同步工具：[`tools/sync_to_github.py`](file:///Users/zengxiangzhan/ZengData/IP数据库/qzdb/tools/sync_to_github.py)。

### 1. 同步并自动推送到远程 GitHub
```bash
cd "/Users/zengxiangzhan/ZengData/IP数据库/qzdb"
python3 tools/sync_to_github.py --push
```

### 2. 该脚本自动完成的核心动作：
1. **源码同步**：精准拷贝 8 语言源码、`pom.xml`、`QQZeng.Qzdb.csproj`、`Cargo.toml`、`package.json`、`pyproject.toml` 等；
2. **规范文档**：同步 `README.md`、`FORMAT.md`、`API_CONTRACT.md`；
3. **安全过滤**：自动排除 `*_test.go`、`Main.java`、测试/基准脚本、临时编译目录 (`bin/`, `obj/`, `target/`)、商业 `*.qzdb` 数据库文件及 Agent 配置；
4. **描述刷新**：严格按**“8个子语言 ➔ ip-qzdb-sdk ➔ 其他顶级目录”**的顺序为每个目录追加带有时间戳的注释并独立 Commit；
5. **远程推送**：安全推送到 GitHub `origin/main`。

---

## 三、手动标准化同步流程（若不使用脚本）

### Step 0: 先在本地开发仓库提交所有代码
```bash
cd "/Users/zengxiangzhan/ZengData/IP数据库/qzdb"
git add multi-lang/
git commit -m "feat(sdk): multi-language optimizations and .NET SDK v1.0.5 release"
```

### Step 1: 精准拷贝 SDK 源码及 README 文档
```bash
DEV="/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang"
GITHUB="/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/ip-qzdb-sdk"

# 1. C
cp "$DEV/c/qzdb_reader.c" "$DEV/c/qzdb_reader.h" "$DEV/c/README.md" "$GITHUB/c/"

# 2. Go (排除测试用例)
cp "$DEV/go/go.mod" "$DEV/go/README.md" "$GITHUB/go/"
[ -f "$DEV/go/go.sum" ] && cp "$DEV/go/go.sum" "$GITHUB/go/"
mkdir -p "$GITHUB/go/qzdb"
rsync -av --exclude='*_test.go' "$DEV/go/qzdb/" "$GITHUB/go/qzdb/"

# 3. Java (排除 Main.java)
cp "$DEV/java/pom.xml" "$DEV/java/README.md" "$GITHUB/java/"
mkdir -p "$GITHUB/java/src/main/java/com/qqzeng/qzdb"
cp "$DEV/java/src/main/java/com/qqzeng/qzdb/"*.java "$GITHUB/java/src/main/java/com/qqzeng/qzdb/"
rm -f "$GITHUB/java/src/main/java/com/qqzeng/qzdb/Main.java"

# 4. .NET / C#
cp "$DEV/netcore/QQZeng.Qzdb.csproj" "$DEV/netcore/README.md" "$GITHUB/netcore/"
cp "$DEV/netcore/"*.cs "$GITHUB/netcore/"

# 5. Node.js
cp "$DEV/nodejs/package.json" "$DEV/nodejs/qzdb.js" "$DEV/nodejs/README.md" "$GITHUB/nodejs/"

# 6. PHP
cp "$DEV/php/QzdbReader.php" "$DEV/php/README.md" "$GITHUB/php/"

# 7. Python
cp "$DEV/python/qzdb.py" "$DEV/python/pyproject.toml" "$DEV/python/README.md" "$GITHUB/python/"

# 8. Rust
cp "$DEV/rust/Cargo.toml" "$DEV/rust/README.md" "$GITHUB/rust/"
mkdir -p "$GITHUB/rust/src"
cp "$DEV/rust/src/lib.rs" "$GITHUB/rust/src/"

# 9. 跑测脚本与协议规范
cp "$DEV/run_all_tests.sh" "$GITHUB/run_all_tests.sh"
cp "$DEV/FORMAT.md" "$GITHUB/FORMAT.md"
cp "$DEV/API_CONTRACT.md" "$GITHUB/API_CONTRACT.md"
```

### Step 2: 验证与清理（安全红线）
```bash
cd "/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/ip-qzdb-sdk"
rm -rf netcore/bin netcore/obj netcore/nupkg rust/target
find . -name "*.qzdb*" -delete
```

### Step 3: 按严格拓扑层级提交目录描述（防描述变旧的关键！）
> ⚠️ **注意**：GitHub 文件浏览器显示的是修改该目录内任意文件的**最后一次 Commit Message**。
> 因此必须**先修改子目录提交，再修改顶级目录提交**！

```bash
cd "/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip"

# 1. 先提交基础变更
git add .
git commit -m "feat(sdk): update multi-language source code"

# 2. 依次单独提交各目录描述
python3 - << 'EOF'
import os, time, subprocess
repo_dir = "."
folders = [
    ("ip-qzdb-sdk/rust", "rust: ⚡ Rust 极速解析引擎 (内存安全 mmap 零拷贝, 微秒级响应)"),
    ("ip-qzdb-sdk/c", "c: ⚡ C/C++ 语言极速解析引擎 (mmap 零拷贝, 微秒级响应, 零堆内存分配)"),
    ("ip-qzdb-sdk/go", "go: ⚡ Go 语言极速解析引擎 (跨平台 mmap 零拷贝, 无锁并发, 极致低延迟)"),
    ("ip-qzdb-sdk/netcore", "netcore: ⚡ C# .NET 极速解析引擎 (内存映射优化, 高并发 760 万+ QPS 零分配)"),
    ("ip-qzdb-sdk/java", "java: ⚡ Java 极速解析引擎 (堆外内存优化, 极致并发性能)"),
    ("ip-qzdb-sdk/nodejs", "nodejs: ⚡ Node.js 极速解析引擎 (V8 原生 BigInt 优化, 异步高效检索)"),
    ("ip-qzdb-sdk/php", "php: ⚡ PHP 极速解析引擎 (高性能内存解析, 开箱即用)"),
    ("ip-qzdb-sdk/python", "python: ⚡ Python 极速解析引擎 (二进制轻量解析, 极简集成)"),
    ("ip-qzdb-sdk", "ip-qzdb-sdk: 👑 下一代 QZDB 极速 IP 解析引擎多语言 SDK (支持 Rust/C/Go/Java/C#/Node/PHP/Python)"),
    ("ip-classic-sdk", "ip-classic-sdk: 📦 IP 数据库经典版 SDK (经典 6.0 .db 与 2.0 .dat 多语言源码)"),
    ("ip-history-sdk", "ip-history-sdk: 🗂️ IP 数据库历史版本与工具 (3.0~5.0 历史演进与桌面查询工具)"),
    ("phone-location-sdk", "phone-location-sdk: 📱 50万+ 手机号段归属地 2.0~6.0 全版本多语言 DAT 解析 SDK 与 Redis 方案"),
    ("database-sql", "database-sql: 🗄️ MySQL / PostgreSQL / SQL Server IP 与号段数据库建表与批量入库 DDL"),
    ("demo", "demo: 📋 IP 归属地及手机号段 CSV/TXT 与 QZDB 演示样本数据"),
    ("docs", "docs: 📚 项目核心设计文档、多格式性能基准对比报告与维护指南"),
]
ts = int(time.time())
for path, msg in folders:
    p = os.path.join(repo_dir, path, "README.md")
    if os.path.exists(p):
        with open(p, "r", encoding="utf-8") as f:
            lines = [l for l in f.read().splitlines() if not l.startswith("<!-- commit:")]
        with open(p, "w", encoding="utf-8") as f:
            f.write("\n".join(lines).strip() + f"\n\n<!-- commit: {msg} sync={ts} -->\n")
        subprocess.run(["git", "add", p], check=True)
        subprocess.run(["git", "commit", "-m", msg], check=True)
EOF
```

### Step 4: 推送到远程 GitHub
```bash
git push origin main
```

---

## 四、安全与合规避坑红线

1. ❌ **严禁上传商业数据库**：任何 `*.qzdb`, `*.qzdb.*` 严禁出现在 GitHub 仓库中；
2. ❌ **严禁上传内部开发/审计文档**：`CODE_AUDIT_REPORT.md`, `RELEASE_READINESS_REPORT.md` 等内部文件仅保留在本地 dev 仓库；
3. ❌ **严禁使用 `rsync --delete` 直接覆盖 GitHub 根目录**：会导致 GitHub 根目录专属的 `README.md`, `database-sql/`, `phone-location-sdk/` 等独立仓库文件被误删；
4. ⚠️ **必须使用拓扑顺序 Commit**：如果修改了 `ip-qzdb-sdk` 顶级目录的内容，必须重新执行 Step 3 刷一遍子目录和顶级目录的 Commit，否则 GitHub 网页右侧会回退显示为旧的通用 Commit Message。

---

## 五、Trie 游走终止保护审计（跨语言统一）

> **背景**：Trie 游走若遇敌对 / 自引用节点（指针回指形成环），可能陷入死循环导致 DoS。
> 各语言需保证**任何文件下游走必然终止**。终止机制分三类：
> - **(a) 派生步数上限**：步数上限由 IP 位宽派生 `cap = max(IPBits + 8, 40)`，即 IPv4 ≤ 40、IPv6 ≤ 136；超过即视为敌对文件，拒绝（返回 miss）是正确 fail-closed 行为。正确 Patricia 树深度不可能超过位宽 + root 余量，故该上限对良构文件零影响。
> - **(b) 魔法常量上限**：硬编码经验值（如 `1000`），功能等价但不可解释、易误改。
> - **(c) 构造性有界（bounded-by-construction）**：循环变量严格按位宽递增（`for depth < 128` / `for step < 16`），深度天然封顶，无需步数上限。
> - **(d) 逐步结构边界校验**：每步校验 `idx < nodeCount` 等，防止越界读取（属内存安全，不单独防环）。
>
> **统一结论**：原 Node.js 等 5 语言使用的魔法常量 `1000` 已统一替换为按位宽派生的命名常量；Rust / Java / C# 本就构造性有界，无需改动，仅在此登记机制。

| 语言 | 终止机制 | 上限来源 | 理由 |
|:---|:---|:---|:---|
| **C** | 派生步数上限（V4）+ 构造性有界（V6） | V4 = `max(32+8,40)` = 40；V6 = `max(128+8,40)` = 136（按位宽派生） | V4 游走 `while(1)` 仅靠步数上限兜底；V6 `while(depth<128)` 构造性有界，步数上限为冗余防御。原魔法常量 `1000` 已替换为按位宽派生常量 `QZDB_MAX_TRIE_WALK_STEPS_V4/V6`。 |
| **C# (.NET)** | 构造性有界 + 逐步结构校验 | 无魔法常量；V4 `for step < 16`，V6 `for depth < 128` | unsafe 指针游走，每步校验 `idx >= nodeCount` 与 `node >= nodesEnd`；循环深度由 IP 位宽天然封顶，无需步数上限。 |
| **Go** | 派生步数上限（V4）+ 构造性有界（V6） | V4 = `max(32+8,40)` = 40（按位宽派生）；V6 无步数上限 | V4 `for steps < maxTrieWalkSteps` 兜底；V6 `for depth < 128` 构造性有界。原魔法常量 `1000` 已替换为按位宽派生常量 `maxTrieWalkSteps`。 |
| **Java** | 构造性有界 | 无魔法常量；V4 `for step < 16`，V6 `for depth < 128` | V4 行号游走 `for step < 16`、V4/V6 深度游走 `for depth < maxDepth`（maxDepth = 32 / 128）；循环深度由 IP 位宽天然封顶。 |
| **Node.js** | 派生步数上限（V4）+ 构造性有界（V6） | V4 = `max(32+8,40)` = 40（按位宽派生）；V6 无步数上限 | V4 `while(true)` 仅靠步数上限兜底；V6 `while(depth < 128)` 构造性有界。原魔法常量 `1000` 已替换为按位宽派生常量 `MAX_TRIE_WALK_STEPS`。 |
| **PHP** | 派生步数上限（V4）+ 构造性有界（V6） | V4 = `max(32+8,40)` = 40；V6 = `max(128+8,40)` = 136（按位宽派生） | V4 `while(true)` 仅靠步数上限兜底；V6 `while(depth < 128)` 构造性有界，步数上限为冗余防御。原魔法常量 `1000` 已替换为按位宽派生常量 `MAX_TRIE_WALK_STEPS_V4/V6`。 |
| **Python** | 派生步数上限（V4）+ 构造性有界（V6） | V4 = `max(32+8,40)` = 40；V6 = `max(128+8,40)` = 136（按位宽派生） | V4 `while True` 仅靠步数上限兜底（每步 `idx >= nodeCount` 仅防越界、不防环）；V6 `while depth < 128` 构造性有界，步数上限为冗余防御。原魔法常量 `1000` 已替换为按位宽派生常量 `MAX_TRIE_WALK_STEPS_V4/V6`。 |
| **Rust** | 构造性有界 | 无魔法常量；V4 `for _ in 0..16` / `while depth < max_depth`，V6 `while depth < 128` | 深度游走 `while depth < max_depth`（max_depth = 32 / 128）、行号游走 `for _ in 0..16`；循环深度由 IP 位宽天然封顶。 |

> **变更清单（仅 5 个文件，其余 3 个语言仅登记）**：`multi-lang/nodejs/qzdb.js`、`multi-lang/go/qzdb/qzdb.go`、`multi-lang/c/qzdb_reader.h` + `multi-lang/c/qzdb_reader.c`、`multi-lang/python/qzdb.py`、`multi-lang/php/QzdbReader.php`。所有改动仅替换终止上限的推导方式，未触碰解析语义、公开 API、缓存或文件格式假设；良构文件行为完全不变（V4 实际 ≤ 16 步、V6 实际 ≤ 128 步，均远低于派生上限）。

## 六、文档同步规范

代码同步之外，以下文档面变更必须随同一提交落地，防止文档与实现漂移：

| 触发变更 | 必须同步的位置 |
|:---|:---|
| 公开 API 签名 / 语义变化 | `docs/QZDB_SDK_API.md`（先改）→ `docs/QZDB_SYNC_GUIDE.md` → 各语言 README 的 API 章节 |
| 二进制格式变化 | `docs/QZDB_FORMAT.md`（唯一权威来源，先改）→ 全部 8 语言实现 |
| README 示例代码 | 两份总 README（根 + `multi-lang/`）中的示例片段必须来自可运行代码并实测输出；示例须体现错误处理路径（fail-closed），不得省略返回值检查与资源释放 |
| 集成方式描述 | 根 README「集成方式」表：语言入口文件数与实际目录一致（Java/C# 为多文件包，Python/Node/PHP/C 为单/双文件） |

**一致性检查项**（每次发布前人工过一遍）：8 语言 README 章节骨架同构；事实性数字（Header 大小、复杂度、缓冲容量）全仓只允许一处权威来源、他处引用；无 emoji 与营销化措辞（技术陈述以数据结构与实测指标为准）。
