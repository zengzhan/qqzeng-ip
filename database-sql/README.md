# 🗄️ 数据库建表与批量入库脚本 (Database SQL Scripts)

本目录提供针对 **IP 数据库 (IPv4/IPv6)**、**手机号段归属地数据库 (Phone)** 以及 **全国行政区划数据库 (Area)** 的主流关系型数据库 DDL 建表脚本、字段映射、索引优化与高吞吐批量导入配置。

---

## 📂 数据库分类目录

| 目录 | 数据库类型 | 包含脚本 | 核心功能与亮点 |
| :--- | :--- | :--- | :--- |
| **[`mysql/`](./mysql)** | 🐬 **MySQL 5.7 / 8.0+** | `ip_cidr.sql`<br>`ip_range.sql`<br>`phone.sql`<br>`area.sql` | `LOAD DATA LOCAL INFILE` 极速入库、InnoDB UTF8MB4 字符集、覆盖行政区划三/四/五级/历史版 |
| **[`pgsql/`](./pgsql)** | 🐘 **PostgreSQL 10+** | `ip_cidr.sql`<br>`ip_range.sql`<br>`phone.sql`<br>`area.sql` | 原生 `CIDR` 类型与 GIST 几何/网段索引、`COPY ... WITH (FORMAT csv)` 高吞吐、`WITH RECURSIVE` 树形递归 |
| **[`mssql/`](./mssql)** | 🪟 **Microsoft SQL Server** | `ip_cidr.sql`<br>`ip_range.sql`<br>`phone.sql`<br>`area.sql` | `BULK INSERT` (CODEPAGE 65001)、T-SQL 原生 CTE 递归树、NVARCHAR 全要素 Unicode 支持 |

---

## 📦 三大产品线与支持版本清单

### 1. IP 地址数据库 (IP Geolocation)
- **支持版本**：标准版 (STD · 6字段)、专业版 (PRO · 11字段)、旗舰版 (MAX · 15字段)、至尊版 (ULT · 25字段)、全球 ASN 版 (4字段)
- **双模部署**：
  - **CIDR 模式** (`ip_cidr.sql`)：PostgreSQL 原生 `inet_ops` GIST 索引；MySQL/MSSQL 字符串前缀与范围检索。
  - **Range 模式** (`ip_range.sql`)：无符号 32 位整型起始/结束 IP，B-Tree `BETWEEN` 毫秒级极速定位。

### 2. 手机号段归属地数据库 (Phone Location)
- **覆盖规模**：51.9 万+ 全量中国移动/联通/电信/广电号段 (`phone.sql`)
- **标准 8 维度字段**：`prefix` (前3位), `phone` (前7位号段主键), `province` (省份), `city` (城市), `isp` (运营商), `post_code` (邮编), `city_code` (长途区号), `area_code` (6位行政区划代码)

### 3. 全国行政区划数据库 (National Administrative Divisions)
严格对齐官方最新发行包结构 (`area.sql`)：
- **三级基础版 (`areasbase`)**：省/市/区县 3 级 · 4,558 行 · 12 核心字段
- **三级旗舰版 (`areas`)**：省/市/区县 3 级 · 4,558 行 · 24 全要素字段 (包含全拼/简拼/首字母/经纬度/全路径/双备注)
- **四级基础版 (`areastownbase`)**：街道乡镇 4 级 · 42,222 行 · 8 核心字段
- **四级旗舰版 (`areastown`)**：街道乡镇 4 级 · 42,222 行 · 16 全要素字段 (拼音/区号/邮编/经纬度/全层级路径)
- **五级基础版 (`areasvillage`)**：社区村委会 5 级 · 665,552 行 · 10 核心字段 (12位统计用区划代码)
- **三级历史版 (`areallyear`)**：1980年至今历年撤销合并对照 · 8,338 行 · 12 变更映射字段

---

## 💡 最佳实践与建议

- **编码统一**：所有 CSV/TXT 源文件均为 `UTF-8 无 BOM` 编码，导入时务必显式声明字符集（如 MySQL 的 `CHARACTER SET utf8mb4`、MSSQL 的 `CODEPAGE = '65001'`、PostgreSQL 的 `ENCODING 'UTF8'`）。
- **批量入库**：建议使用数据库原生的批量导入机制（如 MySQL 的 `LOAD DATA LOCAL INFILE` 或 PostgreSQL 的 `COPY`），几秒内即可完成数百万记录的初始化。
- **树形检索**：针对行政区划的父子级联，已建 `idx_parent` 索引；结合 `WITH RECURSIVE` 或 CTE 语法可单次查询获取完整的“省 > 市 > 区 > 街道”祖先链条。
