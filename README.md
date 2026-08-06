# qzdb-searcher — 跨平台 IP 地理位置查询 SDK

高性能、跨平台的 IP 地理位置数据库查询引擎，支持 **8 种语言**：C, C#, Go, Java, Node.js, PHP, Python, Rust。

## 项目结构与文档规范

```
├── docs/                       ← 核心权威规范目录
│   ├── QZDB_FORMAT.md          ← QZDB 二进制文件格式规范（底层存储）
│   ├── QZDB_SDK_API.md         ← QZDB 多语言 SDK API 设计规范 v2.4（接口设计基线）
│   └── QZDB_SYNC_GUIDE.md      ← 多语言 SDK 同步与测试构建指南
├── multi-lang/                 ← 8 种语言 SDK 代码库
│   ├── c/                      C SDK (qzdb)
│   ├── csharp/                 C# (.NET) SDK
│   ├── go/                     Go Package SDK
│   ├── java/                   Java SDK (DatabaseReader + GeoInfo)
│   ├── nodejs/                 Node.js SDK
│   ├── php/                    PHP SDK
│   ├── python/                 Python SDK
│   ├── rust/                   Rust Crate SDK
│   └── run_all_tests.sh        一键验证测试脚手架
├── CLAUDE.md                   LLM / 开发者指南
├── README.md                   项目入口说明
└── LICENSE                     MIT 开源协议
```

## 📖 核心设计文档

在开始开发或使用 SDK 前，请参考 `docs/` 目录下的权威文档：

1. **[QZDB 二进制文件格式规范](docs/QZDB_FORMAT.md)**：包含 64 字节 Header 结构、Patricia Trie 存储结构、字符串池编码、CRC32 校验、Group 组扩展逻辑。
2. **[QZDB 多语言 SDK API 设计规范 v2.4](docs/QZDB_SDK_API.md)**：包含 8 种语言统一的 API 签名、`ChainedReader` 多库联合、`openBuffer` 内存加载、`UsageType` 21 场景定义与多语言映射、`BatchResult` 批量处理、`GeoInfo` 25 字段全集 Getter。
3. **[QZDB SDK 同步与构建指南](docs/QZDB_SYNC_GUIDE.md)**：跨仓库文件同步与交叉测试运行指南。

## 前置条件

1. **购买数据库**: 从 [qqzeng.com](https://qqzeng.com) 购买 IP 数据库，获取 `.qzdb` 文件
2. **放置数据**: 将 `.qzdb` 文件放入 `multi-lang/data/` 目录
3. **运行测试**: `cd multi-lang && ./run_all_tests.sh`

## 各语言使用方法

| 语言 | 文件 | 使用方式 |
|------|------|---------|
| Python | `qzdb.py` | 拷贝 `qzdb.py` 到项目，`from qzdb import QzdbReader` |
| Node.js | `qzdb.js` | 拷贝 `qzdb.js`，`const QzdbReader = require('./qzdb')` |
| Go | `qzdb/qzdb.go` | 拷贝 `qzdb/` 目录，`import "your-project/qzdb"` |
| PHP | `QzdbReader.php` | 拷贝文件，`use Qqzeng\Ip\QzdbReader` |
| Rust | `lib.rs` | 拷贝 `src/lib.rs` + `Cargo.toml` 依赖 |
| C | `qzdb_reader.c/.h` | 拷贝两个文件一起编译 |
| Java | `QzdbReader.java` | 拷贝到项目，`import com.qqzeng.ip.QzdbReader` |
| C# | `QzdbReader.cs` | 拷贝到项目，`using Qqzeng` |

详见 [multi-lang/README.md](multi-lang/README.md)

## 许可证

MIT
