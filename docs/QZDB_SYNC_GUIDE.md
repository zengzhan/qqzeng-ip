# QZDB 开发目录 → GitHub 同步指南

## 概述

本指南用于将本地开发目录的 SDK 变更同步到 GitHub 发布仓库。

- **开发目录**：`/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/`
- **GitHub 目录**：`/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/`
- **远程仓库**：`https://github.com/zengzhan/qqzeng-ip.git`

## 核心原则

1. **只同步 SDK 核心文件** — 不发布 benchmark、QA 工具、分析脚本、编译产物
2. **保留 GitHub 自有文件** — GitHub 仓库有独立版本的文件，不要覆盖
3. **每个子目录必须有 README.md** — GitHub 用它们在目录浏览右侧显示文字说明

---

## 目录映射

| 开发目录 | GitHub 目录 | 说明 |
|---|---|---|
| `multi-lang/netcore/` | `qzdb/csharp/` | ⚠️ 目录名不同！dev 叫 netcore GitHub 叫 csharp |
| `multi-lang/c/` | `qzdb/c/` | — |
| `multi-lang/go/` | `qzdb/go/` | — |
| `multi-lang/java/` | `qzdb/java/` | — |
| `multi-lang/nodejs/` | `qzdb/nodejs/` | — |
| `multi-lang/php/` | `qzdb/php/` | — |
| `multi-lang/python/` | `qzdb/python/` | — |
| `multi-lang/rust/` | `qzdb/rust/` | — |
| `multi-lang/run_all_tests.sh` | `qzdb/run_all_tests.sh` | — |

---

## 不同步的文件（⚠️ 重要）

以下文件**存在于开发目录但不应上传到 GitHub**。同步后必须清理。

### 各语言子目录的 README.md ≠ 不要删除！

GitHub 上每个语言子目录有自己的 `README.md`，用于在目录浏览时显示简要说明。
这些 README 是 **GitHub 仓库自有的**，开发目录中不存在它们。

**正确处理方式：**
- ✅ **保留 GitHub 上已有的** 子目录 README.md
- ❌ **不要** 从开发目录覆盖它们（开发目录没有这些文件）
- ❌ **不要** 删除它们（它们是有用的目录说明）

### tools/ 目录

**整个不同步。** 原因：
- 包含内部 QA 工具（cross_verify、golden_vectors 等），客户用不到
- `golden_vectors.json` 有 1.4MB，会白白增加仓库体积

### 其他不应出现的文件

| 类别 | 文件 | 原因 |
|---|---|---|
| 测试用例 | `c/main.c`, `go/main.go` | 非 SDK 核心，仅本地测试用 |
| 基准测试 | `c/bench_qps*`, `go/cmd/`, `nodejs/bench_*.js`, `php/bench_*.php`, `python/bench_*.py`, `rust/bench_*.rs` | 不在 SDK 范围内 |
| 验证脚本 | `python/cross_verify.py`, `python/gen_verify*.py`, `python/verify_*.py` | 内部 QA 用 |
| 编译产物 | `*.o`, `bench_qps`, `qzdb_test`, `main`, `target/`, `bin/`, `obj/`, `__pycache__/` | 构建产生 |
| 锁定文件 | `Cargo.lock` | 不在 SDK 范围内 |
| 其他 | `rust/Cargo.toml.bak` | 备份文件 |

### 不应覆盖的文件（GitHub 自有版本）

以下文件**已经存在于 GitHub 仓库，不要从开发目录覆盖它们**：

| 文件 | 原因 |
|---|---|
| `qzdb/README.md` | GitHub 有独立的内容介绍 |
| `qzdb/README_zh.md` | GitHub 中文版说明 |
| `qzdb/FORMAT.md` | GitHub 有独立的格式文档 |
| `qzdb/.gitignore` | GitHub 有独立的 gitignore |
| 各子目录 `*/README.md` | 用于 GitHub 目录页面右侧的说明文字 |

---

## 同步步骤

### ⚠️ Step 0: 先在 dev 仓库提交改动（这一步最容易被遗漏）

**每次修改 SDK 后，不要直接在 GitHub 仓库改，要先在 dev 仓库提交。**

```bash
# 切换到 dev 仓库
cd "/Users/zengxiangzhan/ZengData/IP数据库/qzdb"

# 按语言独立提交（参考 git log 风格）
git add multi-lang/go/qzdb/qzdb.go
git commit -m "fix(go): 具体改动描述"

git add multi-lang/python/qzdb.py
git commit -m "perf(python): 具体改动描述"

git add multi-lang/rust/src/lib.rs
git commit -m "perf(rust): 具体改动描述"

# 检查 dev 仓库状态干净后再继续
git status --short
```

> **为什么先提交 dev 仓库？** 因为 `cp` 只复制文件，不复制 git 历史。dev 仓库的提交记录用于本地回溯和问题排查。忘记这一步 = 丢失改动历史。

---

### Step 1: 同步 SDK 核心文件（cp 到 GitHub 仓库）

使用精准的 `cp` 命令（不要用 `rsync --delete`，它会删掉 GitHub 独有的文件）：

```bash
# 定义路径
DEV="/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang"
GITHUB="/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/qzdb"

# C（核心 SDK，不含 main.c）
cp "$DEV/c/qzdb_reader.c" "$GITHUB/c/"
cp "$DEV/c/qzdb_reader.h" "$GITHUB/c/"

# Go（只更新 qzdb 包，不覆盖 main.go 和 go.mod）
cp "$DEV/go/qzdb/qzdb.go" "$GITHUB/go/qzdb/"

# Java
cp "$DEV/java/src/main/java/qzdb/QzdbReader.java" "$GITHUB/java/src/main/java/qzdb/"
cp "$DEV/java/src/main/java/qzdb/ErrorCode.java" "$GITHUB/java/src/main/java/qzdb/" 2>/dev/null || true
cp "$DEV/java/src/main/java/qzdb/QzdbException.java" "$GITHUB/java/src/main/java/qzdb/" 2>/dev/null || true
cp "$DEV/java/src/main/java/qzdb/IpLocation.java" "$GITHUB/java/src/main/java/qzdb/" 2>/dev/null || true

# C#（注意目录名：dev=netcore, GitHub=csharp）
cp "$DEV/netcore/QzdbReader.cs" "$GITHUB/csharp/"
cp "$DEV/netcore/qzdb-searcher.csproj" "$GITHUB/csharp/" 2>/dev/null || true

# Node.js
cp "$DEV/nodejs/qzdb.js" "$GITHUB/nodejs/"

# PHP
cp "$DEV/php/QzdbReader.php" "$GITHUB/php/"

# Python（只更新核心 SDK，不含验证脚本）
cp "$DEV/python/qzdb.py" "$GITHUB/python/"

# Rust
cp "$DEV/rust/src/lib.rs" "$GITHUB/rust/src/"

# 测试脚本
cp "$DEV/run_all_tests.sh" "$GITHUB/run_all_tests.sh"
```

### Step 2: 验证

```bash
cd "$GITHUB/.."

# 检查不应该存在的文件已被清理
echo "=== 检查多余文件 ==="
ls qzdb/c/main.c 2>/dev/null && echo "ERROR: main.c 不应存在" || echo "  c/main.c: OK (不存在)"
ls qzdb/go/main.go 2>/dev/null && echo "ERROR: main.go 不应存在" || echo "  go/main.go: OK (不存在)"
ls qzdb/python/cross_verify.py 2>/dev/null && echo "ERROR: cross_verify.py 不应存在" || echo "  python/cross_verify.py: OK"
ls qzdb/tools/ 2>/dev/null && echo "ERROR: tools/ 不应存在" || echo "  tools/: OK"

# 检查必不可少的 README.md 是否完好
echo "=== 检查子目录 README ==="
for d in csharp go java nodejs php python rust; do
  ls qzdb/$d/README.md 2>/dev/null && echo "  $d/README.md: 存在" || echo "  $d/README.md: ❌ 缺失！"
done

# 检查不应覆盖的文件是否完好
echo "=== 检查 GitHub 自有文件 ==="
ls qzdb/README.md 2>/dev/null && echo "  README.md: 存在（不要覆盖）" || echo "  README.md: ❌ 缺失！"
ls qzdb/README_zh.md 2>/dev/null && echo "  README_zh.md: 存在（不要覆盖）" || echo "  README_zh.md: ❌ 缺失！"
ls qzdb/FORMAT.md 2>/dev/null && echo "  FORMAT.md: 存在（不要覆盖❗ 如果被覆盖了马上 git checkout）" || echo "  FORMAT.md: OK（不存在=没同步过来）"

# 检查编译产物
echo "=== 检查编译产物 ==="
ls qzdb/c/bench_qps 2>/dev/null && echo "ERROR: bench_qps 不应存在" || echo "  bench_qps: OK"
ls qzdb/go/main 2>/dev/null && echo "ERROR: go/main 不应存在" || echo "  go/main: OK"
ls qzdb/rust/target/ 2>/dev/null && echo "ERROR: rust/target/ 不应存在" || echo "  rust/target/: OK"
```

### Step 3: 清理编译产物和不需要的文件

```bash
cd "/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip"

# 编译产物
rm -rf qzdb/csharp/bin qzdb/csharp/obj
rm -f qzdb/c/qzdb_reader.o qzdb/c/bench_qps qzdb/c/bench_qps.c
rm -f qzdb/go/main
rm -rf qzdb/rust/target/

# 断掉的符号链接（测试数据库链接在 GitHub 上无效）
rm -f qzdb/c/qqzeng_ip_std_china.qzdb

# Agent 状态文件
rm -rf qzdb/c/.omc
```

### Step 4: 提交 SDK 变更

```bash
cd "/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip"
git add qzdb/
git status
# ⚠️ 确认以下文件不应出现在变更中：
#   - qzdb/README.md       （GitHub 自有，不要覆盖）
#   - qzdb/README_zh.md    （GitHub 自有，不要覆盖）
#   - qzdb/FORMAT.md       （GitHub 自有，不要覆盖）
#   - qzdb/*/README.md     （目录说明文件，不要删除或覆盖）
git commit -m "fix(lang): 具体改动描述"
```

### Step 5: 恢复目录描述（‼️ 关键步骤，容易漏）

SDK 提交后，GitHub 仓库所有目录的右侧描述会被最近的 commit message 覆盖。
需要用隐藏注释触发独立提交来恢复每个目录的描述文案。

⚠️ **不要忘记这一步**，否则 GitHub 页面每个目录右侧会显示"fix: test script..."之类的无关描述。

**操作方式：**

```bash
cd "/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip"

# 逐个目录恢复描述。原理：修改 README.md 末尾的隐藏 HTML 注释 → 提交 → commit message 就是目录描述
# 以下按需执行。如果目录描述已经是正确的，只需要改一下注释版本号触发提交即可。

# 如果只需要更新单个目录（如 csharp）：
sed -i '' 's/<!-- commit: csharp:.*/<!-- commit: csharp: C# .NET 极速解析引擎 (堆分配, 8500 万+ QPS) -->/' qzdb/csharp/README.md
git add qzdb/csharp/README.md
git commit -m "csharp: C# .NET 极速解析引擎 (堆分配, 8500 万+ QPS)"

# 全部目录一次恢复（用脚本）：
for dir in c csharp go java nodejs php python rust; do
  # 确保 README.md 末尾有正确的隐藏注释
  desc=""
  case "$dir" in
    c)      desc="c: C 语言极速解析引擎 (mmap 零拷贝, 2 亿+ QPS)" ;;
    csharp) desc="csharp: C# .NET 极速解析引擎 (堆分配, 8500 万+ QPS)" ;;
    go)     desc="go: Go 语言极速解析引擎 (mmap, 9500 万+ QPS)" ;;
    java)   desc="java: Java 极速解析引擎 (堆分配, 9600 万+ QPS)" ;;
    nodejs) desc="nodejs: Node.js 极速解析引擎 (V8 BigInt, 4700 万+ QPS)" ;;
    php)    desc="php: PHP 极速解析引擎 (动态解析, 400 万+ QPS)" ;;
    python) desc="python: Python 参考实现 (动态解析, 250 万+ QPS)" ;;
    rust)   desc="rust: Rust 极速解析引擎 (mmap 安全, 6900 万+ QPS)" ;;
  esac
  if [ -n "$desc" ] && [ -f "qzdb/$dir/README.md" ]; then
    # 更新隐藏注释（没有则追加）
    if grep -q "<!-- commit:" "qzdb/$dir/README.md"; then
      sed -i '' "s|<!-- commit:.*|<!-- commit: $desc -->|" "qzdb/$dir/README.md"
    else
      echo "" >> "qzdb/$dir/README.md"
      echo "<!-- commit: $desc -->" >> "qzdb/$dir/README.md"
    fi
    git add "qzdb/$dir/README.md"
  fi
done
git commit -m "docs: update per-directory GitHub descriptions"
```

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

### 错误 1：删除了子目录的 README.md

**现象**：`git status` 显示 `D qzdb/csharp/README.md` 等删除记录

**原因**：文档说"各语言子目录的 README.md — 与根目录重复，不要同步"，AI 误解为"这些文件不该存在"

**正解**：这些 README.md 是 **GitHub 仓库自有且必需** 的，用于目录浏览说明。**不要删除它们。** 所谓"不要同步"的意思是"不要从开发目录覆盖它们"，而不是"把它们删掉"。

**补救**：`git checkout HEAD -- qzdb/csharp/README.md`

### 错误 2：上传了 FORMAT.md

**现象**：`git status` 显示 `?? qzdb/FORMAT.md/` 或 `M qzdb/FORMAT.md`

**原因**：`QZDB_SYNC_GUIDE.md` 步骤 3 写着要同步 FORMAT.md，但与"不要同步"列表矛盾

**正解**：**FORMAT.md 不应上传**。GitHub 有自己的版本。

**补救**：`git checkout HEAD -- qzdb/FORMAT.md` 或 `rm qzdb/FORMAT.md && git checkout HEAD -- qzdb/FORMAT.md`

### 错误 3：上传了 README.md

**现象**：`git status` 显示 `M qzdb/README.md`

**原因**：同 FORMAT.md，同步步骤和不要同步列表矛盾

**正解**：**README.md 不应上传**。GitHub 有自己的版本。不要从开发目录覆盖。

**补救**：`git checkout HEAD -- qzdb/README.md`

### 错误 4：使用了 rsync --delete 而非 cp

**现象**：GitHub 独有的文件（子目录 README.md、go.mod 等）被删除

**原因**：`rsync -a --delete` 会让目标目录与源目录完全一致，源目录不存在的文件都会被删

**正解**：使用 `cp` 逐个文件同步。如已使用了 `rsync --delete`，需从 git 历史恢复被误删的文件。

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
