# 🗄️ 数据库建表与批量入库脚本 (Database SQL Scripts)

本目录提供针对 **IP 数据库** 与 **手机号段归属地数据库** 的主流关系型数据库 DDL 建表脚本与高吞吐批量导入配置。

---

## 📂 数据库分类目录

| 目录 | 数据库类型 | 包含内容 |
| :--- | :--- | :--- |
| **[`mysql/`](./mysql)** | 🐬 **MySQL** | MySQL 5.7/8.0 DDL 建表脚本、高效前缀索引优化、`LOAD DATA` 极速入库脚本 |
| **[`pgsql/`](./pgsql)** | 🐘 **PostgreSQL** | PostgreSQL DDL 建表语句、CIDR 网段索引优化、`\copy` 高性能批量导入 |
| **[`mssql/`](./mssql)** | 🪟 **Microsoft SQL Server** | SQL Server DDL、表结构定义与批量插入脚本 |

---

## 💡 最佳实践与建议

- **索引优化**：脚本针对 IP 起止整型（或 CIDR 前缀）建立了专用索引，在单表千万级数据下依然保证查询毫秒级响应。
- **批量入库**：建议使用数据库原生的批量导入机制（如 MySQL 的 `LOAD DATA LOCAL INFILE` 或 PostgreSQL 的 `COPY`），几秒内即可完成数百万网段数据的初始化。

<!-- commit description sync 1787122549 -->

<!-- commit: database-sql: MySQL / PostgreSQL / SQL Server 建表与入库 DDL sync=1787948345 -->
