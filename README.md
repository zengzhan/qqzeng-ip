# qzdb-searcher — 跨平台 IP 地理位置查询 SDK

高性能、跨平台的 IP 地理位置数据库查询引擎，支持 **8 种语言**：C, C#, Go, Java, Node.js, PHP, Python, Rust。

## 结构

```
├── multi-lang/     ← SDK (8 语言实现)
│   ├── c/              C (qzdb_init/qzdb_find)
│   ├── go/             Go package
│   ├── java/           Java (singleton + load API)
│   ├── netcore/        C# (.NET Core)
│   ├── nodejs/         Node.js (CommonJS)
│   ├── php/            PHP (namespace Qqzeng\Ip)
│   ├── python/         Python (参考实现)
│   ├── rust/           Rust crate with mmap
│   ├── data/           ← 放置购买的 .qzdb 数据库文件
│   ├── FORMAT.md       二进制格式规范
│   └── run_all_tests.sh 一键测试
├── FORMAT.md              V18 二进制格式规
├── LICENSE                 MIT
└── .gitignore
```

## 前置条件

1. **购买数据库**: 从 [qqzeng.com](https://qqzeng.com) 购买 IP 数据库，获取 `.qzdb` 文件
2. **放置数据**: 将 `.qzdb` 文件放入 `multi-lang/data/` 目录
3. **运行测试**: `cd multi-lang && ./run_all_tests.sh`

## 各语言使用方法

| 语言 | 文件 | 使用方式 |
|------|------|---------|
| Python | `qzdb.py` | 拷贝 `qzdb.py` 到项目，`from qzdb import QzdbSearcher` |
| Node.js | `qzdb.js` | 拷贝 `qzdb.js`，`const QzdbSearcher = require('./qzdb')` |
| Go | `qzdb/qzdb.go` | 拷贝 `qzdb/` 目录，`import "your-project/qzdb"` |
| PHP | `QzdbSearcher.php` | 拷贝文件，`use Qqzeng\Ip\QzdbSearcher` |
| Rust | `lib.rs` | 拷贝 `src/lib.rs` + `Cargo.toml` 依赖 |
| C | `qzdb_searcher.c/.h` | 拷贝两个文件一起编译 |
| Java | `QzdbSearcher.java` | 拷贝到项目，`import com.qqzeng.ip.QzdbSearcher` |
| C# | `QzdbSearcher.cs` | 拷贝到项目，`using Qqzeng` |

详见 [multi-lang/README.md](multi-lang/README.md)

## 许可证

MIT
