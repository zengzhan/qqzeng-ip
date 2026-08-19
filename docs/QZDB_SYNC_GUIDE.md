# QZDB 开发目录 → GitHub 同步指南

## 概述

本指南用于将本地开发目录的 SDK 变更同步到 GitHub 发布仓库。

- **开发目录**：`/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/`
- **GitHub 目录**：`/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/`
- **远程仓库**：`https://github.com/zengzhan/qqzeng-ip.git`

## 核心原则

1. **同步完整 SDK 源码与文档** — 各语言子目录同步完整的 SDK 代码库（包含多源码文件/架构）以及对应的完整 `README.md` 使用文档
2. **只过滤非 SDK 文件** — 不发布 benchmark 脚本、QA 工具、跨语言验证脚本、临时编译产物
3. **保留 GitHub 独立文件** — GitHub 根目录独有文件（如 `qzdb/README.md`, `qzdb/README_zh.md`, `qzdb/FORMAT.md`, `qzdb/.gitignore`）不要误覆盖

---

## 目录映射与文件结构

| 开发目录 | GitHub 目录 | 核心 SDK 文件 / 结构说明 |
|---|---|---|
| `multi-lang/c/` | `qzdb/c/` | `qzdb_reader.c`, `qzdb_reader.h`, `README.md` |
| `multi-lang/go/` | `qzdb/go/` | `go.mod`, `qzdb/*.go` (全套 Go 源码), `README.md` |
| `multi-lang/java/` | `qzdb/java/` | `pom.xml`, `src/main/java/.../*.java` (包含 `QzdbReader.java`, `ChainedReader.java`, `GeoInfo.java` 等全套 Class), `README.md` |
| `multi-lang/netcore/` | `qzdb/netcore/` | `*.cs` (包含 `QzdbReader.cs`, `ChainedReader.cs`, `GeoInfo.cs` 等全套源码), `QQZeng.Qzdb.csproj`, `README.md` |
| `multi-lang/nodejs/` | `qzdb/nodejs/` | `package.json`, `qzdb.js`, `README.md` |
| `multi-lang/php/` | `qzdb/php/` | `QzdbReader.php`, `README.md` |
| `multi-lang/python/` | `qzdb/python/` | `qzdb.py`, `pyproject.toml`, `README.md` |
| `multi-lang/rust/` | `qzdb/rust/` | `Cargo.toml`, `src/lib.rs` (包含全套 Rust 模块与实现), `README.md` |
| `multi-lang/run_all_tests.sh` | `qzdb/run_all_tests.sh` | 跑测脚本 |

---

## 各语言文档同步规范（README.md）

在过去，GitHub 上的 README.md 只是简短的几行字。**现在每个语言目录下都有详细且完整的 README.md 规则与使用文档。**

1. **直接同步 dev 仓库的 README.md**：
   - 每次从 `multi-lang/<lang>/README.md` 同步复制到 `qzdb/<lang>/README.md`
2. **保留/追加隐藏 commit 描述注释**：
   - 为确保 GitHub 目录列表中右侧能够正确显示描述，子目录 `README.md` 文件末尾需要包含对应语言的隐藏注释：`<!-- commit: <lang>: ... -->`
   - 同步 README.md 后，请检查并保留该隐藏注释。

---

## 不同步的文件（⚠️ 重要）

以下文件**存在于开发目录但不应上传到 GitHub**。同步后必须清理或避免复制。

### docs/ 目录及内部开发/报告文档

**整个不同步。** 原因：
- `docs/` 包含内部开发规范、Prompt 模板、测试规范与格式大纲 (`QZDB_SDK_API.md`, `QZDB_TEST_SPECIFICATION.md`, `QZDB_FORMAT.md`, `QZDB_SYNC_GUIDE.md` 等)，属于内部开发文档
- 根目录下的审计/部署报告与配置：`CODE_AUDIT_REPORT.md`, `RELEASE_READINESS_REPORT.md`, `VERSION_ORDER_CHECK_REPORT.md`, `PUBLISHING.md`, `CLAUDE.md` 等均不上传
- Agent 代理配置文件与缓存：`.claude/`, `.omc/`, `.omo/`, `.omx/`, `.workbuddy/` 属于本地开发环境配置，严禁上传

### tools/ 目录及内部脚本

- `tools/` 目录（内部 QA 工具、`golden_vectors.json` 等 1.4MB 数据文件）
- 跨语言/精度验证脚本：`cross_lang_verify*.py`, `accuracy_analysis.py`, `run_batch_test_suite.py`
- 各语言测试/基准程序：
  - C: `main.c`, `batch_cli.c`, `batch_query.c`, `bench_qps.c`, `csv_oracle.c`, `failclosed.c`, `golden_check.c`, `test_main.c`
  - Go: `cmd/` (测试入口/命令行), `qzdb/*_test.go` (测试用例)
  - Java: `src/test/`, `build/`, `test_reports/`, `Main.java`
  - .NET: `netcore.Tests/`, `netcore.samples/`, `bin/`, `obj/`
  - Node.js: `test*.js`, `bench_*.js`, `cmp_node_py.js`, `batch_cli.js`
  - PHP: `test*.php`, `bench_*.php`, `batch_cli.php`, `csv_oracle_test.php`
  - Python: `test*.py`, `bench_*.py`, `batch_cli.py`, `cross_verify.py`
  - Rust: `tests/`, `examples/`, `bench_qps.rs`, `src/bin/`

### 二进制数据库文件 (*.qzdb)

**绝对不能上传。** 原因：
- 数据库文件（如 `*.qzdb`, `*.qzdb.*`）属于商业/测试数据资产，体积大且涉及版权数据
- GitHub 仓库只发布开源 SDK 核心代码，不包含任何真实或测试数据库文件

### 编译与临时产物

- 数据库文件：`*.qzdb`, `*.qzdb.*`
- 编译中间件：`*.o`, `*.so`, `*.a`, `*.dll`, `*.exe`, `bench_qps`, `qzdb_test`, `test_main`, `main`
- 缓存与临时目录：`target/`, `bin/`, `obj/`, `__pycache__/`, `.pytest_cache/`, `.omc/`
- 临时文件：`Cargo.lock`（如不需要强锁定库版本）, `*.bak`, `.DS_Store`

### 不应覆盖的文件（GitHub 根目录自有版本）

以下文件**存在于 GitHub 仓库根目录/qzdb 根目录，不要从 dev 其它无关地方乱覆盖**：

| 文件 | 原因 |
|---|---|
| `qzdb/README.md` | GitHub 仓库总介绍 |
| `qzdb/README_zh.md` | GitHub 中文总介绍 |
| `qzdb/FORMAT.md` | GitHub 数据格式说明 |
| `qzdb/.gitignore` | GitHub Git 忽略配置 |

---

## 同步步骤

### ⚠️ Step 0: 先在 dev 仓库提交改动

**每次修改 SDK 后，不要直接在 GitHub 仓库改，要先在 dev 仓库提交。**

```bash
# 切换到 dev 仓库
cd "/Users/zengxiangzhan/ZengData/IP数据库/qzdb"

# 按语言独立提交
git add multi-lang/go/qzdb/
git commit -m "fix(go): 具体改动描述"

git status --short
```

---

### Step 1: 同步 SDK 源码及 README 文档（cp 到 GitHub 仓库）

注意：各语言现包含多个源码文件及完整的 `README.md`，使用以下精准的同步命令：

```bash
# 定义路径
DEV="/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang"
GITHUB="/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/qzdb"

# 1. C 语言 (核心 SDK 文件及 README)
cp "$DEV/c/qzdb_reader.c" "$DEV/c/qzdb_reader.h" "$DEV/c/README.md" "$GITHUB/c/"

# 2. Go (只同步 pkg 内源码及 mod / README，排除 *_test.go 文件)
cp "$DEV/go/go.mod" "$DEV/go/README.md" "$GITHUB/go/"
mkdir -p "$GITHUB/go/qzdb"
rsync -av --exclude='*_test.go' "$DEV/go/qzdb/" "$GITHUB/go/qzdb/"

# 3. Java (同步 pom.xml, README 及 src/main/java 下所有 SDK 类文件)
cp "$DEV/java/pom.xml" "$DEV/java/README.md" "$GITHUB/java/"
mkdir -p "$GITHUB/java/src/main/java/com/qqzeng/qzdb"
# 拷贝所有 Java 核心类（如 Main.java 为测试类可从 GitHub 排除）
cp "$DEV/java/src/main/java/com/qqzeng/qzdb/"*.java "$GITHUB/java/src/main/java/com/qqzeng/qzdb/"
rm -f "$GITHUB/java/src/main/java/com/qqzeng/qzdb/Main.java"

# 4. .NET / C# (同步 csproj, README 及所有 .cs SDK 源码)
cp "$DEV/netcore/QQZeng.Qzdb.csproj" "$DEV/netcore/README.md" "$GITHUB/netcore/"
cp "$DEV/netcore/"*.cs "$GITHUB/netcore/"

# 5. Node.js (package.json, qzdb.js, README)
cp "$DEV/nodejs/package.json" "$DEV/nodejs/qzdb.js" "$DEV/nodejs/README.md" "$GITHUB/nodejs/"

# 6. PHP (QzdbReader.php, README)
cp "$DEV/php/QzdbReader.php" "$DEV/php/README.md" "$GITHUB/php/"

# 7. Python (qzdb.py, pyproject.toml, README)
cp "$DEV/python/qzdb.py" "$DEV/python/pyproject.toml" "$DEV/python/README.md" "$GITHUB/python/"

# 8. Rust (Cargo.toml, README, src/lib.rs)
cp "$DEV/rust/Cargo.toml" "$DEV/rust/README.md" "$GITHUB/rust/"
mkdir -p "$GITHUB/rust/src"
cp "$DEV/rust/src/lib.rs" "$GITHUB/rust/src/"

# 9. 运行全局测试脚本
cp "$DEV/run_all_tests.sh" "$GITHUB/run_all_tests.sh"
```

---

### Step 2: 验证与清理

```bash
cd "$GITHUB"

# 1. 检查测试/基准/编译产物、*.qzdb 数据文件及内部 docs/配置 是否被遗漏带入
echo "=== 检查多余/不应出现的文件 ==="
rm -rf netcore/bin netcore/obj rust/target
rm -f c/main.c c/*.o c/bench_qps c/qzdb_test c/test_main
rm -f go/main go/qzdb/*_test.go
rm -f java/src/main/java/com/qqzeng/qzdb/Main.java
rm -f python/cross_verify.py python/test*.py
rm -f php/test*.php php/bench*.php
rm -f nodejs/test*.js nodejs/bench*.js
rm -rf docs/ .claude/ .omc/ .omo/ .omx/ .workbuddy/
find . -name "*.qzdb*" -delete

# 2. 检查各语言子目录 README.md 是否完好
echo "=== 检查子目录 README ==="
for d in c netcore go java nodejs php python rust; do
  ls $d/README.md 2>/dev/null && echo "  $d/README.md: ✅ 存在" || echo "  $d/README.md: ❌ 缺失！"
done

# 3. 检查 GitHub 根目录自有文件是否完好
echo "=== 检查 GitHub 根目录文件与风险文件 ==="
ls README.md 2>/dev/null && echo "  README.md: ✅ 存在" || echo "  README.md: ❌ 缺失！"
ls README_zh.md 2>/dev/null && echo "  README_zh.md: ✅ 存在" || echo "  README_zh.md: ❌ 缺失！"
ls docs 2>/dev/null && echo "ERROR: docs/ 目录不应存在！" || echo "  docs/: ✅ 不存在 (内部文档未带入)"
find . -name "*.qzdb*" | grep . && echo "ERROR: 发现残留的 .qzdb 数据文件！" || echo "  *.qzdb: ✅ 不存在 (数据库文件未带入)"
```

---

### Step 3: 提交 SDK 变更

```bash
cd "/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip"
git add qzdb/
git status
git commit -m "fix(sdk): update multi-language qzdb SDKs and docs"
```

---

### Step 4: 检查/更新 GitHub 目录描述文案（隐藏注释）

因为各语言子目录的 `README.md` 现在由 dev 目录同步过去，请确保每个 `README.md` 的底部含有如下隐藏注释。如果没有，可以通过以下脚本添加/修正：

```bash
cd "/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip"

for dir in c netcore go java nodejs php python rust; do
  desc=""
  case "$dir" in
    c)       desc="c: C 语言极速解析引擎 (mmap 零拷贝, 2 亿+ QPS)" ;;
    netcore) desc="netcore: .NET 极速解析引擎 (堆分配, 8500 万+ QPS)" ;;
    go)     desc="go: Go 语言极速解析引擎 (mmap, 9500 万+ QPS)" ;;
    java)   desc="java: Java 极速解析引擎 (堆分配, 9600 万+ QPS)" ;;
    nodejs) desc="nodejs: Node.js 极速解析引擎 (V8 BigInt, 4700 万+ QPS)" ;;
    php)    desc="php: PHP 极速解析引擎 (动态解析, 400 万+ QPS)" ;;
    python) desc="python: Python 参考实现 (动态解析, 250 万+ QPS)" ;;
    rust)   desc="rust: Rust 极速解析引擎 (只读 mmap, 6900 万+ QPS)" ;;
  esac
  if [ -n "$desc" ] && [ -f "qzdb/$dir/README.md" ]; then
    if grep -q "<!-- commit:" "qzdb/$dir/README.md"; then
      sed -i '' "s|<!-- commit:.*|<!-- commit: $desc -->|" "qzdb/$dir/README.md"
    else
      echo "" >> "qzdb/$dir/README.md"
      echo "<!-- commit: $desc -->" >> "qzdb/$dir/README.md"
    fi
    git add "qzdb/$dir/README.md"
  fi
done
git commit -m "docs: update per-directory GitHub descriptions" 2>/dev/null || true
```

---

### Step 5: 推送

```bash
git push origin main
```

---

## ⚠️ 常见操作防错清单

1. **子目录 README.md **：现已统一从开发目录同步最新详细版 README.md，但必须确保文件末尾带有 `<!-- commit: <desc> -->` 注释。
2. **多源码文件放置**：
   - Java/C# 支持多文件架构，同步时需要包含所有功能 `.java` / `.cs` 文件（非单独一两个文件）。
   - Go 语言只同步 `qzdb/*.go` 源码，排除以 `_test.go` 结尾的测试代码。
3. **避免上传测试类**：例如 `Main.java`、`main.c`、`test*.py` 等内部测试文件不要拷贝到 GitHub 仓库。

> **为什么这样做**：GitHub 文件浏览器的每个目录右侧显示的是"最近一次修改该目录内任意文件的 commit message"。通过独立提交只改 README.md 的隐藏注释，commit message 就会成为目录描述。参见仓库根目录 `docs/GITHUB_FOLDER_DESCRIPTIONS.md`。

### Step 6: 推送

```bash
git push origin main
```

**推送后的验证：**
1. 打开 https://github.com/zengzhan/qqzeng-ip/tree/main/qzdb
2. 检查每个子目录右侧的描述是否正确
3. 如果描述还是旧的 commit message，说明 Step 5 漏了

## ⚠️ 常见 AI 操作错误（记忆辅助）

AI 代理在操作时经常犯以下错误，请仔细检查：

### 错误 1：误删或乱改了子目录 README.md 的隐藏 commit 注释

**现象**：`git status` 显示子目录 README.md 的隐藏注释丢失，导致 GitHub 目录浏览右侧无法正确显示功能描述。

**原因**：从 dev 同步子目录 README.md 时覆盖了尾部的 `<!-- commit: <desc> -->` 隐藏 HTML 注释。

**正解**：每次同步 README.md 后，需执行 Step 4 重新确认/追加隐藏注释并提交。

### 错误 2：上传了 FORMAT.md 或根目录 README.md

**现象**：`git status` 显示 `M qzdb/FORMAT.md` 或 `M qzdb/README.md`

**原因**：错把 dev 根目录相关文档覆盖到了 GitHub 仓库根目录。

**正解**：**`qzdb/README.md` 与 `qzdb/FORMAT.md` 为 GitHub 仓库专属总文档，不应被 dev 目录文件覆盖**。

**补救**：`git checkout HEAD -- qzdb/README.md qzdb/FORMAT.md`

### 错误 3：使用了 rsync --delete 删除了 GitHub 独有配置

**现象**：GitHub 独有的配置文件（如 go.mod, package.json 或目录结构）被整目录擦除

**原因**：`rsync -a --delete` 会让目标目录与源目录完全一致，源目录不存在的文件都会被删

**正解**：按 Step 1 命令进行精准 `cp` 或带 `--exclude` 的 `rsync`。如已被误删，需通过 `git checkout` 恢复。

### 错误 5：上传了工具/测试文件

**现象**：`git status` 出现 `python/cross_verify.py`、`c/bench_qps`、`go/main.go` 等

**原因**：`rsync --delete` 或粗放的 cp 把整个目录复制过来了

**正解**：只同步核心 SDK 文件。同步后用验证脚本检查。

### 错误 6：忘记恢复目录描述（最常见）

**现象**：推送到 GitHub 后，https://github.com/zengzhan/qqzeng-ip/tree/main/qzdb 每个子目录右侧显示"fix: test script..."之类的 SDK 提交信息，而不是专业的目录描述。

**原因**：GitHub 目录右侧描述取自"最近一次修改该目录的 commit message"。SDK 提交后描述被覆盖，但没有做 Step 5 来恢复。

**正解**：每次 SDK 同步后必须执行 Step 5（恢复目录描述）。用隐藏注释触发独立提交。

---

## 提交策略

每个语言的修改建议独立提交，遵循 semantic commit 风格：

```
fix(c): safe read helpers with overflow guards
fix(go): eliminate BigInteger allocations in IPv6 trie walk
perf(nodejs): replace BigInt loop with Buffer approach
docs(qzdb): sync README with latest changes
```

提交顺序：后端语言 → 脚本语言 → 文档/清理。
