# QZDB SDK 同步流程

> ⚠️ **本文件已合并到 `QZDB_SYNC_GUIDE.md`**，以后者为准。
> 本文保留以保持兼容，内容可能落后。

## 快速参考

**同步方式**：用 `cp` 逐个文件同步，**不要用** `rsync --delete`。

### 同步的文件

| 源文件（开发目录） | 目标（GitHub 目录） |
|---|---|
| `c/qzdb_searcher.c` | `c/qzdb_searcher.c` |
| `c/qzdb_searcher.h` | `c/qzdb_searcher.h` |
| `go/qzdb/qzdb.go` | `go/qzdb/qzdb.go` |
| `java/src/.../QzdbSearcher.java` | `java/src/.../QzdbSearcher.java` |
| `java/src/.../ErrorCode.java` | `java/src/.../ErrorCode.java` |
| `java/src/.../QzdbException.java` | `java/src/.../QzdbException.java` |
| `java/src/.../IpLocation.java` | `java/src/.../IpLocation.java` |
| `netcore/QzdbSearcher.cs` | `csharp/QzdbSearcher.cs` ⚠️ 目录名不同 |
| `nodejs/qzdb.js` | `nodejs/qzdb.js` |
| `php/QzdbSearcher.php` | `php/QzdbSearcher.php` |
| `python/qzdb.py` | `python/qzdb.py` |
| `rust/src/lib.rs` | `rust/src/lib.rs` |
| `run_all_tests.sh` | `run_all_tests.sh` |

### 不要碰的文件（GitHub 自有）

| 文件 | 原因 |
|---|---|
| `README.md` | GitHub 有自己的版本 |
| `README_zh.md` | GitHub 中文版，不要覆盖 |
| `FORMAT.md` | GitHub 有自己的版本 |
| 各子目录 `*/README.md` | 用于 GitHub 目录浏览文字说明，**不要删除** |

详见 [QZDB_SYNC_GUIDE.md](./QZDB_SYNC_GUIDE.md)。
