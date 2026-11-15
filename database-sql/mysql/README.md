# 🐬 MySQL 数据库部署与建表脚本

本目录提供 **qqzeng** 旗下三大产品（IP 数据库、手机号段数据库、全国行政区划数据库）在 **MySQL 5.7 / 8.0+** 环境下的 DDL 建表脚本与 `LOAD DATA LOCAL INFILE` 极速入库命令。

---

## 📄 脚本文件清单

| 脚本文件 | 适用产品 | 核心特性与支持版本 |
| :--- | :--- | :--- |
| **[`area.sql`](./area.sql)** | **全国行政区划数据库** | 支持 **三级基础版 (`areasbase`)**、**三级旗舰版 (`areas`)**、**四级基础版 (`areastownbase`)**、**四级旗舰版 (`areastown`)**、**五级基础版 (`areasvillage`)**、**三级历史变更版 (`areallyear`)** |
| **[`phone.sql`](./phone.sql)** | **手机号段归属地库** | 覆盖 51.9万+ 号段，8 大核心字段 (`phone` 主键)，支持前缀快速检索 |
| **[`ip_cidr.sql`](./ip_cidr.sql)** | **IP 数据库 (CIDR 网段)** | 支持标准版、专业版、旗舰版、至尊版、ASN 版；网段前缀检索 |
| **[`ip_range.sql`](./ip_range.sql)** | **IP 数据库 (Range 起止)** | 无符号 32 位整型起始/结束 IP，B-Tree `BETWEEN` 检索 |

---

## ⚡ 导入性能与避坑指南

1. **允许本地加载 (`local_infile`)**：
   若报 `Error Code: 1148. The used command is not allowed with this MySQL version`，请在 `my.cnf` 中配置：
   ```ini
   [mysqld]
   local_infile = 1
   [mysql]
   local_infile = 1
   ```
   并在连接命令中加入 `--local-infile=1`。
2. **字符集声明**：
   导入命令中务必声明 `CHARACTER SET utf8mb4`，避免生僻地名、少数民族区划或中文运营商乱码。
