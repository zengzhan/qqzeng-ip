# 🐘 PostgreSQL 数据库部署与建表脚本

本目录提供 **qqzeng** 旗下三大产品（IP 数据库、手机号段数据库、全国行政区划数据库）在 **PostgreSQL 10+** 环境下的 DDL 建表脚本与 `COPY` 极速批量入库命令。

---

## 📄 脚本文件清单

| 脚本文件 | 适用产品 | 核心特性与支持版本 |
| :--- | :--- | :--- |
| **[`area.sql`](./area.sql)** | **全国行政区划数据库** | 支持 **三级基础版 (`areasbase`)**、**三级旗舰版 (`areas`)**、**四级基础版 (`areastownbase`)**、**四级旗舰版 (`areastown`)**、**五级基础版 (`areasvillage`)**、**三级历史变更版 (`areallyear`)**，提供 `WITH RECURSIVE` 树形递归查询 |
| **[`phone.sql`](./phone.sql)** | **手机号段归属地库** | 覆盖 51.9万+ 号段，8 大核心字段 (`phone` 主键)，支持前缀快速检索 |
| **[`ip_cidr.sql`](./ip_cidr.sql)** | **IP 数据库 (CIDR 网段)** | 原生 `CIDR` 类型，配合 GIST `inet_ops` 索引，实现微秒级子网包含（`>>=`）与交集（`&&`）检索 |
| **[`ip_range.sql`](./ip_range.sql)** | **IP 数据库 (Range 起止)** | BIGINT 起止 IP 数值，B-Tree 索引支持 `BETWEEN` 检索 |

---

## ⚡ 导入性能与建议

- 使用 `COPY ... WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')` 单次导入，百万数据仅需数秒。
- 在 `psql` 客户端中可使用 `\copy` 命令直接从客户端本地路径读取文件。
