# QZDB SDK 同步流程文档

## 📁 目录结构对比

### 测试目录（工作目录）
**路径**: `/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/`

```
multi-lang/
├── c/              # C语言SDK
├── go/             # Go SDK
├── java/           # Java SDK
├── netcore/        # ⚠️ C# SDK（注意目录名）
├── nodejs/         # Node.js SDK
├── php/            # PHP SDK
├── python/         # Python SDK
├── rust/           # Rust SDK
├── tools/          # 测试工具（完整）
├── data/           # 数据库文件（不上传）
├── docs/           # 📁 内部文档（不上传GitHub）
│   ├── FORMAT.md
│   ├── SDK同步流程.md
│   └── QZDB_SYNC_GUIDE.md
├── README.md
└── run_all_tests.sh
```

### GitHub目录（远程仓库）
**路径**: `/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/qzdb`

```
qzdb/
├── c/              # C语言SDK
├── go/             # Go SDK
├── java/           # Java SDK
├── csharp/         # ✅ C# SDK（目录名不同）
├── nodejs/         # Node.js SDK
├── php/            # PHP SDK
├── python/         # Python SDK
├── rust/           # Rust SDK
├── tools/          # 工具（部分）
├── FORMAT.md
├── README.md       # 英文README
├── README_zh.md    # 中文README
└── run_all_tests.sh
```

---

## ⚠️ 关键差异

### 1. 目录命名差异
| 语言 | 测试目录 | GitHub目录 |
|------|---------|-----------|
| C# | `netcore/` | `csharp/` |

**注意**: GitHub目录使用 `csharp/`，同步时要将 `netcore/` 的文件复制到 `csharp/`

### 2. 文档差异
- GitHub有 `README_zh.md`（中文），测试目录没有
- 各语言子目录：GitHub有 `README.md`，测试目录部分有
- **不要覆盖GitHub的README文档**，只同步SDK代码

### 3. 数据库文件
- GitHub: `python/data/*.qzdb`（包含测试数据库）
- 测试目录: `data/*.qzdb`（购买的数据库）
- **不上传购买的数据库文件**

### 4. tools目录差异
- 测试目录tools更完整（有batch_csharp、test_cases等）
- GitHub目录tools有golden_vectors.json、verify脚本等
- **选择性同步**: 只同步验证工具（cross_verify.py等）

---

## 📋 同步文件清单

### 需要同步的SDK核心文件（8个语言）

#### 1. C语言
```bash
测试目录: c/qzdb_searcher.c
          c/qzdb_searcher.h
GitHub:   c/qzdb_searcher.c  # 覆盖
          c/qzdb_searcher.h  # 覆盖
```

#### 2. Go
```bash
测试目录: go/qzdb/qzdb.go
GitHub:   go/qzdb/qzdb.go  # 覆盖
```

#### 3. Java
```bash
测试目录: java/src/main/java/qzdb/QzdbSearcher.java
GitHub:   java/src/main/java/qzdb/QzdbSearcher.java  # 覆盖
          java/src/main/java/qzdb/ErrorCode.java      # 新增
          java/src/main/java/qzdb/QzdbException.java   # 新增
```

#### 4. C# ⚠️
```bash
测试目录: netcore/QzdbSearcher.cs
GitHub:   csharp/QzdbSearcher.cs  # 注意目录名！
```

#### 5. Node.js
```bash
测试目录: nodejs/qzdb.js
GitHub:   nodejs/qzdb.js  # 覆盖
```

#### 6. PHP
```bash
测试目录: php/QzdbSearcher.php
GitHub:   php/QzdbSearcher.php  # 覆盖
```

#### 7. Python
```bash
测试目录: python/qzdb.py
GitHub:   python/qzdb.py  # 覆盖
```

#### 8. Rust
```bash
测试目录: rust/src/lib.rs
GitHub:   rust/src/lib.rs  # 覆盖
```

### 需要同步的工具文件
```bash
测试目录: tools/cross_verify.py
          tools/verify_golden.py
          tools/gen_golden_vectors.py
          tools/verify_boundary.py
          tools/known_bugs_regression.py
          tools/golden_vectors.json
          tools/golden_boundary.json
GitHub:   tools/  # 覆盖
```

### 不要同步的文件（保留GitHub版本）
- `README.md`（英文）
- `README_zh.md`（中文）
- `FORMAT.md`（格式文档）
- 各语言子目录的 `README.md`
- `.gitignore`
- `data/*.qzdb`（购买的数据库）
- `python/data/*.qzdb`
- `*.pyc`、`__pycache__/`、`target/`、`obj/`、`bin/`（编译产物）

---

## 🔄 执行步骤

### Step 1: 备份当前GitHub目录
```bash
cd /Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/qzdb
cp -r . ../qzdb_backup_$(date +%Y%m%d)
```

### Step 2: 同步SDK核心文件（8个语言）
```bash
# 定义变量
TEST_DIR="/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang"
GITHUB_DIR="/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/qzdb"

# C
cp $TEST_DIR/c/qzdb_searcher.c $GITHUB_DIR/c/
cp $TEST_DIR/c/qzdb_searcher.h $GITHUB_DIR/c/

# Go
cp $TEST_DIR/go/qzdb/qzdb.go $GITHUB_DIR/go/qzdb/

# Java
cp $TEST_DIR/java/src/main/java/qzdb/QzdbSearcher.java $GITHUB_DIR/java/src/main/java/qzdb/
cp $TEST_DIR/java/src/main/java/qzdb/ErrorCode.java $GITHUB_DIR/java/src/main/java/qzdb/ 2>/dev/null || echo "New file"
cp $TEST_DIR/java/src/main/java/qzdb/QzdbException.java $GITHUB_DIR/java/src/main/java/qzdb/ 2>/dev/null || echo "New file"

# C# ⚠️ 注意目录名差异
cp $TEST_DIR/netcore/QzdbSearcher.cs $GITHUB_DIR/csharp/

# Node.js
cp $TEST_DIR/nodejs/qzdb.js $GITHUB_DIR/nodejs/

# PHP
cp $TEST_DIR/php/QzdbSearcher.php $GITHUB_DIR/php/

# Python
cp $TEST_DIR/python/qzdb.py $GITHUB_DIR/python/

# Rust
cp $TEST_DIR/rust/src/lib.rs $GITHUB_DIR/rust/src/
```

### Step 3: 同步工具文件
```bash
cp $TEST_DIR/tools/cross_verify.py $GITHUB_DIR/tools/
cp $TEST_DIR/tools/verify_golden.py $GITHUB_DIR/tools/
cp $TEST_DIR/tools/gen_golden_vectors.py $GITHUB_DIR/tools/
cp $TEST_DIR/tools/verify_boundary.py $GITHUB_DIR/tools/
cp $TEST_DIR/tools/known_bugs_regression.py $GITHUB_DIR/tools/
cp $TEST_DIR/tools/golden_vectors.json $GITHUB_DIR/tools/
cp $TEST_DIR/tools/golden_boundary.json $GITHUB_DIR/tools/
```

### Step 4: 验证同步结果
```bash
cd $GITHUB_DIR
# 检查文件修改时间
ls -lt c/qzdb_searcher.c go/qzdb/qzdb.go rust/src/lib.rs

# 验证C#文件（目录名差异）
ls -lt csharp/QzdbSearcher.cs
```

### Step 5: 提交到GitHub
```bash
cd /Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip
git status
git add qzdb/
git commit -m "fix: 同步SDK修复（Go V6、CRF验证、边界处理等）"
git push origin main
```

---

## ✅ 验证清单

### 同步后检查
- [ ] 各语言SDK文件已更新（对比文件时间戳）
- [ ] C#目录名正确：`csharp/`（不是`netcore/`）
- [ ] Java新增文件：`ErrorCode.java`、`QzdbException.java`
- [ ] 文档未被覆盖（README.md、README_zh.md保持GitHub版本）
- [ ] .gitignore未被覆盖
- [ ] 未提交购买的数据库文件

### 功能验证（可选）
```bash
# 在GitHub目录运行交叉验证
cd /Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip/qzdb
python3 tools/cross_verify.py
```

---

## 📝 注意事项

1. **目录名差异**: C# 在GitHub用 `csharp/`，测试用 `netcore/`
2. **文档保留**: 不要覆盖GitHub的README，只同步SDK代码
3. **数据库不上传**: `.qzdb`文件在`.gitignore`中，不要提交
4. **编译产物**: 不要提交 `target/`、`obj/`、`bin/`、`__pycache__/`
5. **测试目录不同步**: 不要把测试目录的临时文件（`.omc/`、`accuracy_analysis.py`等）上传

---

## 🔗 相关链接

- **GitHub仓库**: https://github.com/qqzeng-ip/qqzeng-ip
- **SDK目录**: https://github.com/qqzeng-ip/qqzeng-ip/tree/main/qzdb
