# 🪟 Microsoft SQL Server 数据库部署与建表脚本

本目录提供 **qqzeng** 旗下三大产品（IP 数据库、手机号段数据库、全国行政区划数据库）在 **Microsoft SQL Server (2012+)** 环境下的 DDL 建表脚本与 `BULK INSERT` 极速批量导入命令。

---

## 📄 脚本文件清单

| 脚本文件 | 适用产品 | 核心特性与支持版本 |
| :--- | :--- | :--- |
| **[`area.sql`](./area.sql)** | **全国行政区划数据库** | 支持 **三级基础版 (`areasbase`)**、**三级旗舰版 (`areas`)**、**四级基础版 (`areastownbase`)**、**四级旗舰版 (`areastown`)**、**五级基础版 (`areasvillage`)**、**三级历史变更版 (`areallyear`)**，提供 T-SQL CTE 递归查询 |
| **[`phone.sql`](./phone.sql)** | **手机号段归属地库** | 覆盖 51.9万+ 号段，8 大核心字段 (`phone` 主键)，支持前缀快速检索 |
| **[`ip_cidr.sql`](./ip_cidr.sql)** | **IP 数据库 (CIDR 网段)** | 规范化 VARCHAR 网段，前缀与覆盖查询 |
| **[`ip_range.sql`](./ip_range.sql)** | **IP 数据库 (Range 起止)** | BIGINT 起止数值，聚集/非聚集索引支持 `BETWEEN` 检索 |

---

## ⚡ 导入性能与建议

- 推荐使用 `BULK INSERT ... WITH (FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '0x0a', CODEPAGE = '65001', TABLOCK)`。
- 若源文件为 Windows CRLF 换行，可指定 `ROWTERMINATOR = '\n'` 或 `ROWTERMINATOR = '0x0d0a'`。
