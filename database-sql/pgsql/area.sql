-- ==============================================================================
-- qqzeng-area 全国行政区划数据库 - PostgreSQL 部署脚本
-- 官方网站: https://www.qqzeng.com
-- 包含版本：三级基础版、三级旗舰版、四级基础版、四级旗舰版、五级基础版、三级历史变更版
-- 字段 100% 对齐官方最新发行包 (CSV / TXT / SQL)
-- ==============================================================================

-- ##############################################################################
-- 1. 三级 基础版 (Level 3 Base - 省市区三级 · 4,558 行)
-- 表名: areasbase
-- ##############################################################################

DROP TABLE IF EXISTS areasbase;
CREATE TABLE areasbase (
    admin_code          VARCHAR(20) PRIMARY KEY,     -- 行政区划代码
    parent_code         VARCHAR(20) DEFAULT '',      -- 父级区划代码 (省级为空)
    level_type          VARCHAR(10) DEFAULT '',      -- 级别类型 (0:国 1:省 2:市 3:区县)
    name                VARCHAR(100) NOT NULL,       -- 区划名称
    short_name          VARCHAR(50) DEFAULT '',      -- 简称
    province_short_name VARCHAR(50) DEFAULT '',      -- 省份简称
    city_short_name     VARCHAR(50) DEFAULT '',      -- 城市简称
    district_short_name VARCHAR(50) DEFAULT '',      -- 区县简称
    area_code           VARCHAR(10) DEFAULT '',      -- 电话区号
    postal_code         VARCHAR(10) DEFAULT '',      -- 邮政编码
    remark1             VARCHAR(50) DEFAULT '',      -- 备注1 (如: 行政区)
    remark2             VARCHAR(50) DEFAULT ''       -- 备注2
);

CREATE INDEX idx_areasbase_parent ON areasbase (parent_code);
CREATE INDEX idx_areasbase_name ON areasbase (name);

-- PostgreSQL 极速导入 CSV
-- COPY areasbase (admin_code, parent_code, level_type, name, short_name, province_short_name, city_short_name, district_short_name, area_code, postal_code, remark1, remark2)
-- FROM '/path/to/qqzeng_area_l3_base.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8');


-- ##############################################################################
-- 2. 三级 旗舰版 (Level 3 Ultra - 全要素高精度省市区 · 4,558 行)
-- 表名: areas
-- ##############################################################################

DROP TABLE IF EXISTS areas;
CREATE TABLE areas (
    admin_code          VARCHAR(20) PRIMARY KEY,     -- 行政区划代码
    parent_code         VARCHAR(20) DEFAULT '',      -- 父级区划代码 (省级为空)
    level_type          VARCHAR(10) DEFAULT '',      -- 级别类型 (0:国 1:省 2:市 3:区县)
    name                VARCHAR(100) NOT NULL,       -- 区划全称
    short_name          VARCHAR(50) DEFAULT '',      -- 简称
    parent_path         VARCHAR(100) DEFAULT '',     -- 全层级代码组合路径 (如 110000,110100,110105)
    province            VARCHAR(50) DEFAULT '',      -- 省份全称
    city                VARCHAR(50) DEFAULT '',      -- 城市全称
    district            VARCHAR(50) DEFAULT '',      -- 区县全称
    province_short_name VARCHAR(50) DEFAULT '',      -- 省份简称
    city_short_name     VARCHAR(50) DEFAULT '',      -- 城市简称
    district_short_name VARCHAR(50) DEFAULT '',      -- 区县简称
    province_pinyin     VARCHAR(50) DEFAULT '',      -- 省份拼音
    city_pinyin         VARCHAR(50) DEFAULT '',      -- 城市拼音
    district_pinyin     VARCHAR(50) DEFAULT '',      -- 区县拼音
    pinyin              VARCHAR(100) DEFAULT '',     -- 拼音全拼
    jianpin             VARCHAR(50) DEFAULT '',      -- 简拼
    first_char          VARCHAR(10) DEFAULT '',      -- 首字母大写
    area_code           VARCHAR(10) DEFAULT '',      -- 电话区号
    postal_code         VARCHAR(10) DEFAULT '',      -- 邮政编码
    lng                 VARCHAR(30) DEFAULT '',      -- 经度坐标 (可存储为 NUMERIC(10,6))
    lat                 VARCHAR(30) DEFAULT '',      -- 纬度坐标 (可存储为 NUMERIC(10,6))
    remark1             VARCHAR(50) DEFAULT '',      -- 备注1 (如: 行政区)
    remark2             VARCHAR(50) DEFAULT ''       -- 备注2 (如: 县级市/地级市/新区)
);

CREATE INDEX idx_areas_parent ON areas (parent_code);
CREATE INDEX idx_areas_path ON areas (parent_path);
CREATE INDEX idx_areas_city ON areas (city);
CREATE INDEX idx_areas_pinyin ON areas (pinyin);

-- PostgreSQL 极速导入 CSV
-- COPY areas (admin_code, parent_code, level_type, name, short_name, parent_path, province, city, district, province_short_name, city_short_name, district_short_name, province_pinyin, city_pinyin, district_pinyin, pinyin, jianpin, first_char, area_code, postal_code, lng, lat, remark1, remark2)
-- FROM '/path/to/qqzeng_area_l3_ultra.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8');


-- ##############################################################################
-- 3. 四级 基础版 (Level 4 Base - 街道乡镇四级 · 42,222 行)
-- 表名: areastownbase
-- ##############################################################################

DROP TABLE IF EXISTS areastownbase;
CREATE TABLE areastownbase (
    "ID"        VARCHAR(20) PRIMARY KEY,     -- 行政区划代码
    "ParentId"  VARCHAR(20) DEFAULT '',      -- 父级区划代码 (省级为空)
    "LevelType" VARCHAR(10) DEFAULT '',      -- 行政级别 (1:省 2:市 3:区县 4:街道/乡镇)
    "Name"      VARCHAR(100) NOT NULL,       -- 区划名称
    "Province"  VARCHAR(50) DEFAULT '',      -- 所属省份
    "City"      VARCHAR(50) DEFAULT '',      -- 所属城市
    "District"  VARCHAR(50) DEFAULT '',      -- 所属区县
    "Town"      VARCHAR(100) DEFAULT ''      -- 所属乡镇/街道
);

CREATE INDEX idx_townbase_parent ON areastownbase ("ParentId");

-- PostgreSQL 导入 CSV
-- COPY areastownbase ("ID", "ParentId", "LevelType", "Name", "Province", "City", "District", "Town")
-- FROM '/path/to/qqzeng_area_l4_base.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8');


-- ##############################################################################
-- 4. 四级 旗舰版 (Level 4 Ultra - 街道乡镇全要素高精度 · 42,222 行)
-- 表名: areastown
-- ##############################################################################

DROP TABLE IF EXISTS areastown;
CREATE TABLE areastown (
    "ID"         VARCHAR(20) PRIMARY KEY,    -- 行政区划代码
    "ParentId"   VARCHAR(20) DEFAULT '',     -- 父级区划代码 (省级为空)
    "LevelType"  VARCHAR(10) DEFAULT '',     -- 行政级别 (1:省 2:市 3:区县 4:街道/乡镇)
    "Name"       VARCHAR(100) NOT NULL,      -- 区划名称
    "ParentPath" VARCHAR(100) DEFAULT '',    -- 全层级组合路径
    "Province"   VARCHAR(50) DEFAULT '',     -- 所属省份
    "City"       VARCHAR(50) DEFAULT '',     -- 所属城市
    "District"   VARCHAR(50) DEFAULT '',     -- 所属区县
    "Town"       VARCHAR(100) DEFAULT '',    -- 所属乡镇/街道
    "Pinyin"     VARCHAR(100) DEFAULT '',    -- 拼音全拼
    "Jianpin"    VARCHAR(50) DEFAULT '',     -- 简拼
    "FirstChar"  VARCHAR(10) DEFAULT '',     -- 首字母
    "CityCode"   VARCHAR(10) DEFAULT '',     -- 电话区号
    "ZipCode"    VARCHAR(10) DEFAULT '',     -- 邮政编码
    "Lng"        VARCHAR(30) DEFAULT '',     -- 经度坐标
    "Lat"        VARCHAR(30) DEFAULT ''      -- 纬度坐标
);

CREATE INDEX idx_town_parent ON areastown ("ParentId");
CREATE INDEX idx_town_path ON areastown ("ParentPath");

-- PostgreSQL 导入 CSV
-- COPY areastown ("ID", "ParentId", "LevelType", "Name", "ParentPath", "Province", "City", "District", "Town", "Pinyin", "Jianpin", "FirstChar", "CityCode", "ZipCode", "Lng", "Lat")
-- FROM '/path/to/qqzeng_area_l4_ultra.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8');


-- ##############################################################################
-- 5. 五级 基础版 (Level 5 Base - 社区村五级 · 665,552 行)
-- 表名: areasvillage
-- ##############################################################################

DROP TABLE IF EXISTS areasvillage;
CREATE TABLE areasvillage (
    "ID"         VARCHAR(20) PRIMARY KEY,    -- 12位统计用区划代码
    "ParentId"   VARCHAR(20) DEFAULT '',     -- 父级区划代码 (9位街道代码)
    "LevelType"  VARCHAR(10) DEFAULT '',     -- 行政级别 (固定为 5)
    "Name"       VARCHAR(100) NOT NULL,      -- 社区/村委会名称
    "Province"   VARCHAR(50) DEFAULT '',     -- 所属省份
    "City"       VARCHAR(50) DEFAULT '',     -- 所属城市
    "District"   VARCHAR(50) DEFAULT '',     -- 所属区县
    "Town"       VARCHAR(100) DEFAULT '',    -- 所属乡镇街道
    "Village"    VARCHAR(100) DEFAULT '',    -- 所属村委会/社区居委会
    "ParentPath" VARCHAR(120) DEFAULT ''     -- 5级完整组合路径
);

CREATE INDEX idx_village_parent ON areasvillage ("ParentId");
CREATE INDEX idx_village_path ON areasvillage ("ParentPath");

-- PostgreSQL 导入 CSV
-- COPY areasvillage ("ID", "ParentId", "LevelType", "Name", "Province", "City", "District", "Town", "Village", "ParentPath")
-- FROM '/path/to/qqzeng_area_l5_base.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8');


-- ##############################################################################
-- 6. 三级 历史版 (Level 3 History - 1980年至今撤销合并对照 · 8,338 行)
-- 表名: areallyear
-- ##############################################################################

DROP TABLE IF EXISTS areallyear;
CREATE TABLE areallyear (
    admin_code     VARCHAR(20) NOT NULL,     -- 历史行政区划代码
    parent_code    VARCHAR(20) DEFAULT '',   -- 历史父级代码
    level_type     VARCHAR(10) DEFAULT '',   -- 级别类型 (1:省 2:市 3:区县)
    name           VARCHAR(100) NOT NULL,    -- 历史区划名称
    province       VARCHAR(50) DEFAULT '',   -- 所属省份
    city           VARCHAR(50) DEFAULT '',   -- 所属城市
    district       VARCHAR(50) DEFAULT '',   -- 所属区县
    remark         TEXT,                     -- 撤销/合并历史沿革说明
    new_admin_code VARCHAR(20) DEFAULT '',   -- 映射最新有效行政区划代码
    new_address    VARCHAR(200) DEFAULT '',  -- 映射当前最新省市区完整地址
    new_short      VARCHAR(100) DEFAULT '',  -- 映射当前最新省市区简称聚合
    year_time      VARCHAR(20) DEFAULT ''    -- 变更年份
);

CREATE INDEX idx_hist_admin ON areallyear (admin_code);
CREATE INDEX idx_hist_new_admin ON areallyear (new_admin_code);

-- PostgreSQL 导入 CSV
-- COPY areallyear (admin_code, parent_code, level_type, name, province, city, district, remark, new_admin_code, new_address, new_short, year_time)
-- FROM '/path/to/qqzeng_area_l3_history.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8');


-- ##############################################################################
-- 7. PostgreSQL 递归查询树示例 (WITH RECURSIVE)
-- ##############################################################################

-- 向上递归：查找指定区县（如 440305 深圳市南山区）的完整省市区链路
-- WITH RECURSIVE area_ancestors AS (
--     SELECT admin_code, parent_code, name, level_type, CAST(name AS VARCHAR(255)) AS full_chain
--     FROM areas WHERE admin_code = '440305'
--     UNION ALL
--     SELECT a.admin_code, a.parent_code, a.name, a.level_type, CAST(a.name || ' > ' || r.full_chain AS VARCHAR(255))
--     FROM areas a
--     INNER JOIN area_ancestors r ON a.admin_code = r.parent_code
-- )
-- SELECT * FROM area_ancestors ORDER BY level_type ASC;
