# 更新日志 (Changelog)

所有该项目的显著更改都将记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)，
并且本项目遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [6.0.0] - 2026-01-06

### 🚀 新增功能
- **全新架构**: 采用Trie树+位图索引的混合架构，实现O(1)级别的极速查询
- **多语言支持**: 同时发布C, Rust, Go, Java, Node.js, .NET, Python, PHP 8种语言SDK
- **统一接口**: 所有语言版本提供一致的 `find(ip)` 和 `findUint(ip_int)` 接口
- **跨平台**: 完美支持x86_64和ARM64 (Apple Silicon) 架构，自动处理字节序
- **预解析模式**: 启动时一次性加载解析，查询过程无GC压力

### ⚡ 性能提升
- **C语言**: 达到 53M+ QPS (GCC -O3)
- **Rust**: 达到 55M+ QPS (Safe Rust模式)
- **Go**: 达到 43M+ QPS (零内存分配)
- **Node.js**: 达到 41M+ QPS (V8 TurboFan优化)
- **.NET**: 达到 42M+ QPS (TieredPGO优化)

### 🐛 修复
- 修复了Java实现中的路径查找拼写错误 (`getAbsolutePath4`)
- 修复了PHP实现中 `sprintf` 处理无符号整数的类型转换问题
- 统一了所有语言的异常处理逻辑，无效IP返回空字符串而非抛出异常

### 📦 依赖变更
- 移除所有外部运行时依赖，所有SDK均为纯原生实现
- Java版本最低要求升级至 JDK 8，推荐 JDK 21
- PHP版本最低要求升级至 7.4，推荐 8.5
- Node.js版本最低要求 16.x，推荐 25.x

## [5.0.0] - 2023-01-01
- 旧版本归档，采用二分查找算法
- 支持IPv4基础解析

---
[6.0.0]: https://github.com/qqzeng/qqzeng-ip/releases/tag/v6.0.0
