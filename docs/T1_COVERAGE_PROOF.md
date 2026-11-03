# QZDB 全球国家与地区覆盖数统计事实报告 (T1_COVERAGE_PROOF.md)

> **基准日期**：2026-11-03  
> **数据源**：PostgreSQL 生产库核心主表与字典表

---

## 一、真实数据库实查与严密对账

执行对账 SQL 实测结果如下：

| 数据集 / 维度 | 实际覆盖去重计数 | 统计口径说明 | SQL 判定依据 |
|:---|:---:|:---|:---|
| **IPv4 主表 (`ip_master_v4`)** | **246** | 全球 IPv4 网段实际覆盖的国家/地区 ISO 代码数（已排除 `ZZ` 保留池） | `SELECT COUNT(DISTINCT g.country_code) FROM ip_master_v4 m JOIN ip_geo_info g ON m.geo_id = g.geoname_id WHERE g.country_code NOT IN ('', 'ZZ')` |
| **IPv6 主表 (`ip_master_v6`)** | **250** | 全球 IPv6 网段实际覆盖的国家/地区 ISO 代码数（已排除 `ZZ` 保留池） | `SELECT COUNT(DISTINCT g.country_code) FROM ip_master_v6 m JOIN ip_geo_info g ON m.geo_id = g.geoname_id WHERE g.country_code NOT IN ('', 'ZZ')` |
| **双栈联合覆盖 (V4 ∪ V6)** | **255** | 双栈数据实际命中的全部国家和地区代码总和 | `ip_master_v4` 与 `ip_master_v6` 的 `geo_id` 联合查询 |
| **字典有效代码 (`ip_geo_info`)** | **255** | 基础行政区划字典中收录的全部有效国家与地区 ISO 代码数（排除 `ZZ`） | `SELECT COUNT(DISTINCT country_code) FROM ip_geo_info WHERE country_code NOT IN ('', 'ZZ')` |

### 严密数学闭合验证：206 + 49 = 255
对 `ip_geo_info` 执行精准分层对账：
1. **主权国家/独立区域（建有 `geo_level = 1` 记录）**：实查正好为 **206 个**（对应 208 条国家中文名记录，其中塞浦路斯分治等产生同码多名）；
2. **海外属地/特别行政区（无独立 `geo_level = 1` 记录）**：实查正好为 **49 个**（如香港 HK、澳门 MO、台湾 TW、关岛 GU 等，属权归属其母国）；
3. **闭合验证**：
   $$\mathbf{206} \text{ (主权级)} + \mathbf{49} \text{ (属地级)} = \mathbf{255} \text{ (有效国家与地区代码总数)}$$
4. 另有 `ZZ` 专用保留代码，全表共计 256 个有效非空代码（含空字符串为 257 项）。

---

## 二、全站宣称口径冻结

- **法定对外口径**：统一冻结为 **“覆盖全球 246 个国家与地区”**（以 IPv4 实际覆盖为标准基线）；
- **全站历史遗留纠偏**：选购页 JS 动态标签中的旧值 `244` 已修复为 `246`；首页正文简介、选购页、演示页全部实现 **246** 统一。
