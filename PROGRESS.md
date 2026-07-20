# 项目进度

## 完成

### 编译器修复
- **ReadRecords gParts[6] skip bug**: `CodeIdx = p7.GetOrAdd(gParts[8])` → `p7.GetOrAdd(gParts[6])`，修复了区划代码/ASN号丢失问题
- **VersionExporter asn 缺少 continent/country**: 修复了 asn 版本的 GetGeoInfoLine 缺少洲/国家信息

### 8语言 SDK 统一 version 参数
所有 SDK（Python、Node.js、PHP、Go、Rust、C、Java、C#）均已添加 `version` 参数：
- 支持 `"std"`、`"ult"`、`"asn"`、`"max"` 四种版本
- C#/Java: 移除单例 return 限制，支持重载
- Node.js/PHP: 添加 `load()` 方法
- Rust: 添加 `from_file()` 非静态构造
- PHP: 设置 `memory_limit = 256M` 处理 max_global.qzdb (45MB)
- Python: 添加 GeoInfo 版本别名（`area_code`/`country_english`/`asn_num`/`asn_org`）

### 测试
- 所有语言测试通过（Python、CSV Verify、Node.js、PHP、Go、Rust、C、Java、C#）
- 新增 max_china/max_global 验证：161/161 + 230/230 + 184/184 + 9028/9028 全部通过
- 所有 verify_*_max_*.txt 文件生成完毕

### 项目文档
- 创建 `LICENSE` (MIT)
- 创建 `.gitignore`（排除 `.csv`/`.qzdb`/`.db`/`bin`/`obj`/`.agent`/`node_modules`/`vendor`/`target`/`build`）
- 创建根目录 `README.md`
- 更新 `multi-lang/README.md`（数据需购买警告、字段文档引用）
- 复制 `ip_database_fields_design.md` 到 `qzdb/Docs/`

### 交付
- 9 语言 SDK 源码已复制到 `qzdb/` 目标目录
- `run_all_tests.sh` 统一测试脚本
- `copy_to_github.sh` 部署脚本

## 待办
1. 重建所有 `.qzdb` 文件（修复 compiler bug 后）
2. 初始化 git，提交（仅 SDK + 文档），推送到 GitHub
3. 用户购买 qqzeng IP 数据库，放置 `.qzdb` 到 `data/`，运行 `./run_all_tests.sh`

## 关键决策
- **V18 保留 8 个字符串池**（24 bytes/条），扩展字段（时区、货币、电话、emoji 旗帜、使用类型、ASN域名/组织等）有意省略
- **V18 不含 country_alpha3** 
- **GeoInfo 保持统一字段名**，通过 version 别名支持版本特定名称
