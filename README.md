# qzdb — IP 地理位置查询 SDK

基于自定义二进制格式（`.qzdb`）的高性能 IP 地理位置查询引擎。单文件或单目录即可集成，8 种语言共享同一 API 设计与同一套验证体系。

| 特性 | 说明 |
|------|------|
| 检索结构 | Jump Table + Patricia Trie 双阶段前缀检索，IPv4 平均约 16 次比对 |
| 访问方式 | mmap 只读映射，零拷贝寻址，无锁并发查询 |
| Schema | 字段集合由数据库元数据驱动，向前向后兼容 |
| 一致性 | 四层验证：冒烟测试 / 跨语言一致性 / 回归基准 / 精度分析 |

## 快速开始

以 Python 为例，其余语言仅构造方式不同：

```python
from qzdb import QzdbReader

reader = QzdbReader("qqzeng_ip_std_china.qzdb")
print(reader.find_str("114.114.114.114"))   # 亚洲|CN|中国|江苏|南京|114DNS
loc = reader.find("114.114.114.114")        # 结构化 GeoInfo 对象
print(loc.country, loc.city)                # 中国 南京
```

## 集成方式

| 语言 | 入口 | 集成 | 语言文档 |
|------|------|------|---------|
| Python | `qzdb.py` | 拷贝单文件，`from qzdb import QzdbReader` | [python](multi-lang/python/README.md) |
| Node.js | `qzdb.js` | 拷贝单文件，`require('./qzdb')` | [nodejs](multi-lang/nodejs/README.md) |
| Go | `qzdb/` 包 | 拷贝 `go/qzdb/` 目录，`import` 该包 | [go](multi-lang/go/README.md) |
| Java | `com.qqzeng.qzdb` 包 | 拷贝 `java/src/main/java/com/qqzeng/qzdb/` 整个包 | [java](multi-lang/java/README.md) |
| C# | `QQZeng.Qzdb` | 拷贝 `netcore/*.cs` 实现文件集，`using QQZeng.Qzdb` | [netcore](multi-lang/netcore/README.md) |
| PHP | `QzdbReader.php` | 拷贝单文件，`use Qqzeng\Ip\QzdbReader` | [php](multi-lang/php/README.md) |
| Rust | `src/lib.rs` | 引入 `rust/` crate（核心为单文件 `lib.rs`） | [rust](multi-lang/rust/README.md) |
| C | `qzdb_reader.c/.h` | 两个文件一起编译 | [c](multi-lang/c/README.md) |

各语言统一的 API 语义（生命周期 / 查询 / GeoInfo / 批量 / 多库联合）见 [API 设计规范](docs/QZDB_SDK_API.md)；语言间差异见 [multi-lang/README.md](multi-lang/README.md)。

## 数据库

从 [qqzeng.com](https://qqzeng.com) 购买数据库取得 `.qzdb` 文件，放置于 `multi-lang/data/` 目录。格式规范见 [QZDB_FORMAT.md](docs/QZDB_FORMAT.md)：192 字节定长 Header、Metadata TLV、Patricia Trie 存储结构、字符串池编码、CRC32 校验。

## 验证

```bash
cd multi-lang && ./run_all.sh        # L1–L4 全量编排（冒烟/交叉一致性/回归基准/精度分析）
cd multi-lang && ./run_all_tests.sh  # L1 冒烟 + 新增门禁
```

## 仓库结构

```
├── docs/                  权威规范：FORMAT / SDK_API / SYNC_GUIDE / TEST_SPECIFICATION
├── multi-lang/            8 语言 SDK 与四层验证脚本（c / netcore / go / java / nodejs / php / python / rust）
│   └── data/              .qzdb 数据库放置处（不入库）
├── sql/                   IPv6 数据融合管线
├── tools/                 元数据探针等辅助工具（6 语言版本）
└── LICENSE                MIT
```

## License

MIT
