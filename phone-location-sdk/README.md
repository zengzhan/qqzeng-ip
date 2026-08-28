# 📱 手机号段归属地解析 SDK (Phone Location SDKs)

本目录提供 **50万+ 手机号段（前7位）归属地与运营商数据** 的多版本解析 SDK 及高并发缓存方案。

---

## 📂 版本与方案目录

| 目录 | 说明 | 支持语言 / 组件 |
| :--- | :--- | :--- |
| **[`v6.0/`](./v6.0)** | ⚡ **号段 6.0 旗舰版**（推荐）——高性能二进制 DAT 解析 | C++ · Go · Java · .NET · Node.js · PHP · Python · Rust |
| **[`v5.0/`](./v5.0)** | 📱 **号段 5.0 版**——轻量内存检索 | .NET Core |
| **[`v4.0/`](./v4.0)** | 📱 **号段 4.0 版**——全平台二进制查询 | C · Go · Java · .NET · Node.js · PHP · Python · Rust |
| **[`v3.0/`](./v3.0)** | 📱 **号段 3.0 版**——经典内存版解析 | C++ · Go · Java · .NET · Node.js · PHP · Python · Rust |
| **[`v2.0/`](./v2.0)** | 📱 **号段 2.0 版**——早期紧凑二进制解析 | C++ · Java · .NET · Node.js · PHP |
| **[`redis/`](./redis)** | 🚀 **Redis 高并发缓存方案**——批量导入脚本与查询 API | PHP / Redis |

---

## ⚡ 特性亮点

1. **体积精简**：全国 50 万+ 号段经过压缩仅数兆大小，纯内存操作，单次查询微秒级。
2. **多语言原生支持**：覆盖 C++、Rust、Go、Java、Python、PHP、Node.js、.NET 全语言栈。
3. **高并发缓存支持**：提供 Redis 快速批量导入脚本与热点缓存方案，轻松应对千万级 QPS。

<!-- commit description sync 1787122549 -->

<!-- commit: phone-location-sdk: 手机号段归属地 DAT 解析 SDK（2.0~6.0 全版本多语言） sync=1787945119 -->
