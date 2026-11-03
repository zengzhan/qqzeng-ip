# QZDB 全渠道包管理器一致性审计报告 (PACKAGE_AUDIT.md)

> **审计基准日**：2026-11-03
> **审计目的**：核对各生态渠道的包名、维护主体、开源许可证及仓库归属，确保开发者无论通过何种包管理器获取到的均是同源、同许可的正版 SDK。

---

## 一、各语言包管理器审计矩阵

| 语言 / 生态 | 包管理器 | 注册包名 (`Package Name`) | 线上最新版 | 许可证 (`License`) | 维护主体 (`Author`) | 官方仓库指向 (`Repository`) | 状态 |
|:---|:---|:---|:---:|:---:|:---|:---|:---:|
| **Node.js** | npm | `@qqzengip/qzdb` | 1.0.8 | **MIT** | qqzeng | `github.com/zengzhan/qqzeng-ip` | ✅ 完全一致 |
| **Python** | PyPI | `qzdb` | 1.0.8 | **MIT** | qqzeng | `github.com/zengzhan/qqzeng-ip` | ✅ 完全一致 |
| **Rust** | crates.io | `qzdb` | 2.0.2 | **MIT** | zengzhan | `github.com/zengzhan/qqzeng-ip` | ✅ 完全一致 |
| **.NET / C#** | NuGet | `QQZeng.Qzdb` | 1.0.10 | **MIT** | QQZeng | `github.com/zengzhan/qqzeng-ip` | ✅ 完全一致 |
| **Go** | Go Module | `github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go` | git-tag | **MIT** | zengzhan | 同源 Git 仓库 | ✅ 完全一致 |
| **PHP** | Packagist | `qqzeng/qzdb` (源内包含 composer.json) | v1.0.0 | **MIT** | qqzeng | `github.com/zengzhan/qqzeng-ip` | ✅ 完全一致 |
| **Java** | Maven | `com.qqzeng:qzdb` (pom.xml 标准) | 1.0.0 | **MIT** | qqzeng | `github.com/zengzhan/qqzeng-ip` | ✅ 完全一致 |
| **C / C++** | 单头文件 | `qzdb_reader.h` / `qzdb_reader.c` | v1.0 | **MIT** | qqzeng | 源码分发 | ✅ 完全一致 |

---

## 二、审计结论与合规备忘

1. **许可证全线统一**：全部包管理器线上发行的 SDK 均显式声明为 **MIT License**，无任何 GPL、Apache 或未声明冲突；
2. **所有权闭环**：所有包的注册维护人与仓库统一归属于 `qqzeng` / `zengzhan`，无第三方冒领或钓鱼包；
3. **版本演进说明**：
   - Rust 包在 2026-10 月因重构为值语义与 4 路组相联缓存升级至 2.x 大版本，其它生态维持在 1.x 稳定分支，语义兼容完好。
