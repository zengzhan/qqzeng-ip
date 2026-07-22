# QZDB 开发目录 → GitHub 同步指南

## 概述

本仓库的 `qzdb/` 目录是从独立开发工作区同步过来的。开发目录包含完整的 SDK 源码、benchmark、测试脚本和分析工具，同步到 GitHub 时只发布**核心 SDK 文件**。

## 目录映射关系

| 开发目录 (`multi-lang/`) | GitHub (`qzdb/`) | 说明 |
|---|---|---|
| `c/` | `c/` | C SDK |
| `netcore/` | `csharp/` | C# SDK（注意目录名不同） |
| `go/` | `go/` | Go SDK |
| `java/` | `java/` | Java SDK |
| `nodejs/` | `nodejs/` | Node.js SDK |
| `php/` | `php/` | PHP SDK |
| `python/` | `python/` | Python SDK |
| `rust/` | `rust/` | Rust SDK |
| `tools/` | `tools/` | 交叉验证工具 |
| `FORMAT.md` | `FORMAT.md` | 二进制格式说明 |
| `README.md` | `README.md` | 项目文档 |
| `run_all_tests.sh` | `run_all_tests.sh` | 一键测试脚本 |

## 不同步的文件

- `data/` — `.qzdb` 数据库文件（单独分发，不包含在 SDK 仓库中）
- `.omc/` — Agent 状态文件（本地开发用）
- `cross_lang_verify.py` — 交叉验证脚本（开发用）
- `accuracy_analysis.py` — 精度分析工具（开发用）
- 各语言的 `bench_*.py`/`bench_*.js`/`bench_*.rs`/`bench_*.c` — 基准测试（不在 SDK 范围内）
- 各语言的 `gen_verify*.py`/`verify*.py` — 验证脚本（开发用）
- Build 产物（`*.o`, `bench_qps`, `qzdb_test`, `main`, `target/`, `bin/`, `obj/` 等）
- `Cargo.lock` — Rust 锁定文件（不在 SDK 范围内）

## 同步命令

```bash
# 变量定义
SOURCE="/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang"
GITHUB="/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/qzdb"

# 1. 处理 netcore/ → csharp/ 重命名
rm -rf "$GITHUB/csharp"
rsync -a --delete "$SOURCE/netcore/" "$GITHUB/csharp/"

# 2. 同步各语言核心 SDK 目录
for lang in c go java nodejs php python rust; do
  rsync -a --delete "$SOURCE/$lang/" "$GITHUB/$lang/"
done

# 3. 同步工具 + 顶层文件
rsync -a --delete "$SOURCE/tools/" "$GITHUB/tools/"
rsync -a "$SOURCE/FORMAT.md" "$GITHUB/FORMAT.md"
rsync -a "$SOURCE/README.md" "$GITHUB/README.md"
rsync -a "$SOURCE/run_all_tests.sh" "$GITHUB/run_all_tests.sh"

# 4. 清理开发目录额外文件（bench/test/分析脚本不发布）
rm -f "$GITHUB/c/batch_query.c" "$GITHUB/c/bench_qps" "$GITHUB/c/bench_qps.c"
rm -rf "$GITHUB/go/cmd/"
rm -f "$GITHUB/nodejs/bench_all.js" "$GITHUB/nodejs/cmp_node_py.js"
rm -f "$GITHUB/php/bench_all.php"
rm -f "$GITHUB/python/bench_qps.py" "$GITHUB/python/cross_verify.py" \
     "$GITHUB/python/gen_verify.py" "$GITHUB/python/verify_*.py"
rm -f "$GITHUB/rust/bench_qps.rs" "$GITHUB/rust/Cargo.toml.bak"
# tools/ 中的 batch/bench 辅助文件
rm -f "$GITHUB/tools/batch_*" "$GITHUB/tools/bench_*" "$GITHUB/tools/edge_test.py" \
     "$GITHUB/tools/xlang_edge_test.py" "$GITHUB/tools/verify_full.py" \
     "$GITHUB/tools/task_plan.md" "$GITHUB/tools/BatchQuery.java"
rm -rf "$GITHUB/tools/batch_csharp/" "$GITHUB/tools/batch_csharp_out/" \
       "$GITHUB/tools/results/" "$GITHUB/tools/src/" "$GITHUB/tools/test_cases/"
```

## 提交策略

每个语言的 SDK 修改作为**独立原子提交**，遵循仓库的 semantic commit 风格：

```
fix(java): extract QzdbException and ErrorCode into separate files
fix(c): safe read helpers with overflow guards and C99 forward declaration
fix(csharp): use Values array instead of null Fields in ToPipe/Get/IsEmpty
fix(go): eliminate BigInteger allocations in IPv6 trie walk + P2 array view
fix(php): fix V6 binary search comparison and pool lookup indexing
fix(python): P2 array view - replace fields dict with values list in GeoInfo
perf(nodejs): replace BigInt loop with Buffer.writeBigUInt64BE
docs(qzdb): sync FORMAT.md and README with latest SDK changes
```

提交顺序：后端语言 → 脚本语言 → 文档。每个提交独立可 revert。

## 推送

```bash
cd "$GITHUB/.."  # 到 qqzeng-ip 仓库根目录
git push origin main
```

## 关键注意事项

1. **`netcore/` → `csharp/`**：开发目录名为 `netcore`，GitHub 上为 `csharp`，同步时必须手动映射
2. **保留 GitHub 特有文件**：`.gitignore`、`README_zh.md` 不在开发目录中，同步后需确保未被删除
3. **只同步核心 SDK**：benchmark、gen_verify、accuracy_analysis 等开发工具不发布
4. **目录描述**：同步后各目录的 GitHub 描述会被最新提交覆盖，需要参考 `GITHUB_FOLDER_DESCRIPTIONS.md` 重新设置
