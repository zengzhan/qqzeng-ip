-- ==============================================================================
-- qqzeng-area 全国行政区划数据库 - MySQL 部署脚本
-- 官方网站: https://www.qqzeng.com
-- 包含版本：三级基础版、三级旗舰版、四级基础版、四级旗舰版、五级基础版、三级历史变更版
-- 字段 100% 对齐官方最新发行包 (CSV / TXT / SQL)
-- ==============================================================================

SET NAMES utf8mb4;

-- ##############################################################################
-- 1. 三级 基础版 (Level 3 Base - 省市区三级 · 4,558 行)
-- 发行文件名参考: 2026年最新行政区划数据库-三级-基础版.csv / qqzeng_area_l3_base.csv
-- ##############################################################################

DROP TABLE IF EXISTS `areasbase`;
CREATE TABLE `areasbase` (
  `admin_code` varchar(20) NOT NULL COMMENT '行政区划代码 (主键)',
  `parent_code` varchar(20) DEFAULT '' COMMENT '父级区划代码 (省级为空)',
  `level_type` varchar(10) DEFAULT '' COMMENT '级别类型 (0:国 1:省 2:市 3:区县)',
  `name` varchar(100) NOT NULL COMMENT '区划名称',
  `short_name` varchar(50) DEFAULT '' COMMENT '简称',
  `province_short_name` varchar(50) DEFAULT '' COMMENT '省份简称',
  `city_short_name` varchar(50) DEFAULT '' COMMENT '城市简称',
  `district_short_name` varchar(50) DEFAULT '' COMMENT '区县简称',
  `area_code` varchar(10) DEFAULT '' COMMENT '电话区号',
  `postal_code` varchar(10) DEFAULT '' COMMENT '邮政编码',
  `remark1` varchar(50) DEFAULT '' COMMENT '备注1 (如: 行政区)',
  `remark2` varchar(50) DEFAULT '' COMMENT '备注2 (如: 经济开发区/高新区)',
  PRIMARY KEY (`admin_code`),
  KEY `idx_parent` (`parent_code`),
  KEY `idx_name` (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='全国三级行政区划基础版';

-- 导入 CSV (推荐，首行表头)
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_area_l3_base.csv'
-- INTO TABLE `areasbase`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n' -- Windows 文件请改为 '\r\n'
-- IGNORE 1 LINES
-- (`admin_code`, `parent_code`, `level_type`, `name`, `short_name`, `province_short_name`, `city_short_name`, `district_short_name`, `area_code`, `postal_code`, `remark1`, `remark2`);

-- 导入 TXT (制表符分隔)
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_area_l3_base.txt'
-- INTO TABLE `areasbase`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY '\t'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES;


-- ##############################################################################
-- 2. 三级 旗舰版 (Level 3 Ultra - 全要素高精度省市区 · 4,558 行)
-- 发行文件名参考: 2026年最新行政区划数据库-三级-旗舰版.csv / qqzeng_area_l3_ultra.csv
-- ##############################################################################

DROP TABLE IF EXISTS `areas`;
CREATE TABLE `areas` (
  `admin_code` varchar(20) NOT NULL COMMENT '行政区划代码 (主键)',
  `parent_code` varchar(20) DEFAULT '' COMMENT '父级区划代码 (省级为空)',
  `level_type` varchar(10) DEFAULT '' COMMENT '级别类型 (0:国 1:省 2:市 3:区县)',
  `name` varchar(100) NOT NULL COMMENT '区划全称',
  `short_name` varchar(50) DEFAULT '' COMMENT '简称',
  `parent_path` varchar(100) DEFAULT '' COMMENT '全层级代码组合路径 (如 110000,110100,110105)',
  `province` varchar(50) DEFAULT '' COMMENT '省份全称',
  `city` varchar(50) DEFAULT '' COMMENT '城市全称',
  `district` varchar(50) DEFAULT '' COMMENT '区县全称',
  `province_short_name` varchar(50) DEFAULT '' COMMENT '省份简称',
  `city_short_name` varchar(50) DEFAULT '' COMMENT '城市简称',
  `district_short_name` varchar(50) DEFAULT '' COMMENT '区县简称',
  `province_pinyin` varchar(50) DEFAULT '' COMMENT '省份拼音',
  `city_pinyin` varchar(50) DEFAULT '' COMMENT '城市拼音',
  `district_pinyin` varchar(50) DEFAULT '' COMMENT '区县拼音',
  `pinyin` varchar(100) DEFAULT '' COMMENT '拼音全拼',
  `jianpin` varchar(50) DEFAULT '' COMMENT '简拼',
  `first_char` varchar(10) DEFAULT '' COMMENT '首字母大写',
  `area_code` varchar(10) DEFAULT '' COMMENT '电话区号',
  `postal_code` varchar(10) DEFAULT '' COMMENT '邮政编码',
  `lng` varchar(30) DEFAULT '' COMMENT '经度坐标',
  `lat` varchar(30) DEFAULT '' COMMENT '纬度坐标',
  `remark1` varchar(50) DEFAULT '' COMMENT '备注1 (如: 行政区)',
  `remark2` varchar(50) DEFAULT '' COMMENT '备注2 (如: 县级市/地级市/新区)',
  PRIMARY KEY (`admin_code`),
  KEY `idx_parent` (`parent_code`),
  KEY `idx_path` (`parent_path`),
  KEY `idx_city` (`city`),
  KEY `idx_pinyin` (`pinyin`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='全国三级行政区划旗舰版';

-- 导入 CSV (推荐)
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_area_l3_ultra.csv'
-- INTO TABLE `areas`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`admin_code`, `parent_code`, `level_type`, `name`, `short_name`, `parent_path`, `province`, `city`, `district`, `province_short_name`, `city_short_name`, `district_short_name`, `province_pinyin`, `city_pinyin`, `district_pinyin`, `pinyin`, `jianpin`, `first_char`, `area_code`, `postal_code`, `lng`, `lat`, `remark1`, `remark2`);


-- ##############################################################################
-- 3. 四级 基础版 (Level 4 Base - 街道乡镇四级 · 42,222 行)
-- 发行文件名参考: 2026年最新行政区划数据库-四级-基础版.csv / qqzeng_area_l4_base.csv
-- ##############################################################################

DROP TABLE IF EXISTS `areastownbase`;
CREATE TABLE `areastownbase` (
  `ID` varchar(20) NOT NULL COMMENT '行政区划代码 (主键)',
  `ParentId` varchar(20) DEFAULT '' COMMENT '父级区划代码 (省级为空)',
  `LevelType` varchar(10) DEFAULT '' COMMENT '行政级别 (1:省 2:市 3:区县 4:街道/乡镇)',
  `Name` varchar(100) NOT NULL COMMENT '区划名称',
  `Province` varchar(50) DEFAULT '' COMMENT '所属省份',
  `City` varchar(50) DEFAULT '' COMMENT '所属城市',
  `District` varchar(50) DEFAULT '' COMMENT '所属区县',
  `Town` varchar(100) DEFAULT '' COMMENT '所属乡镇/街道',
  PRIMARY KEY (`ID`),
  KEY `idx_parent` (`ParentId`),
  KEY `idx_level` (`LevelType`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='全国四级行政区划基础版';

-- 导入 CSV
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_area_l4_base.csv'
-- INTO TABLE `areastownbase`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`ID`, `ParentId`, `LevelType`, `Name`, `Province`, `City`, `District`, `Town`);


-- ##############################################################################
-- 4. 四级 旗舰版 (Level 4 Ultra - 街道乡镇全要素高精度 · 42,222 行)
-- 发行文件名参考: 2026年最新行政区划数据库-四级-旗舰版.csv / qqzeng_area_l4_ultra.csv
-- ##############################################################################

DROP TABLE IF EXISTS `areastown`;
CREATE TABLE `areastown` (
  `ID` varchar(20) NOT NULL COMMENT '行政区划代码 (主键)',
  `ParentId` varchar(20) DEFAULT '' COMMENT '父级区划代码 (省级为空)',
  `LevelType` varchar(10) DEFAULT '' COMMENT '行政级别 (1:省 2:市 3:区县 4:街道/乡镇)',
  `Name` varchar(100) NOT NULL COMMENT '区划名称',
  `ParentPath` varchar(100) DEFAULT '' COMMENT '全层级组合路径 (如 110000,110100,110105,110105001)',
  `Province` varchar(50) DEFAULT '' COMMENT '所属省份',
  `City` varchar(50) DEFAULT '' COMMENT '所属城市',
  `District` varchar(50) DEFAULT '' COMMENT '所属区县',
  `Town` varchar(100) DEFAULT '' COMMENT '所属乡镇/街道',
  `Pinyin` varchar(100) DEFAULT '' COMMENT '拼音全拼',
  `Jianpin` varchar(50) DEFAULT '' COMMENT '简拼',
  `FirstChar` varchar(10) DEFAULT '' COMMENT '首字母',
  `CityCode` varchar(10) DEFAULT '' COMMENT '电话区号',
  `ZipCode` varchar(10) DEFAULT '' COMMENT '邮政编码',
  `Lng` varchar(30) DEFAULT '' COMMENT '经度坐标',
  `Lat` varchar(30) DEFAULT '' COMMENT '纬度坐标',
  PRIMARY KEY (`ID`),
  KEY `idx_parent` (`ParentId`),
  KEY `idx_path` (`ParentPath`),
  KEY `idx_town` (`Town`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='全国四级行政区划旗舰版';

-- 导入 CSV
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_area_l4_ultra.csv'
-- INTO TABLE `areastown`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`ID`, `ParentId`, `LevelType`, `Name`, `ParentPath`, `Province`, `City`, `District`, `Town`, `Pinyin`, `Jianpin`, `FirstChar`, `CityCode`, `ZipCode`, `Lng`, `Lat`);


-- ##############################################################################
-- 5. 五级 基础版 (Level 5 Base - 社区村五级 · 665,552 行)
-- 发行文件名参考: 2024/2026年最新行政区划数据库-五级-基础版.sql / qqzeng_area_l5_base.zip
-- ##############################################################################

DROP TABLE IF EXISTS `areasvillage`;
CREATE TABLE `areasvillage` (
  `ID` varchar(20) NOT NULL COMMENT '12位统计用区划代码 (主键)',
  `ParentId` varchar(20) DEFAULT '' COMMENT '父级区划代码 (9位乡镇代码)',
  `LevelType` varchar(10) DEFAULT '' COMMENT '行政级别 (固定为 5)',
  `Name` varchar(100) NOT NULL COMMENT '社区/村委会名称',
  `Province` varchar(50) DEFAULT '' COMMENT '所属省份',
  `City` varchar(50) DEFAULT '' COMMENT '所属城市',
  `District` varchar(50) DEFAULT '' COMMENT '所属区县',
  `Town` varchar(100) DEFAULT '' COMMENT '所属乡镇/街道',
  `Village` varchar(100) DEFAULT '' COMMENT '所属村委会/社区居委会',
  `ParentPath` varchar(120) DEFAULT '' COMMENT '5级完整组合路径 (如 110000,110100,110105,110105001,110105001001)',
  PRIMARY KEY (`ID`),
  KEY `idx_parent` (`ParentId`),
  KEY `idx_path` (`ParentPath`),
  KEY `idx_village` (`Village`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='全国五级行政区划社区村基础版';

-- 导入 CSV / SQL
-- 注: 五级发布包内通常提供原生 .sql 导入文件（包含已切块的批量 INSERT 语句）。
-- 若自行导出为 CSV，可执行:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_area_l5_base.csv'
-- INTO TABLE `areasvillage`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`ID`, `ParentId`, `LevelType`, `Name`, `Province`, `City`, `District`, `Town`, `Village`, `ParentPath`);


-- ##############################################################################
-- 6. 三级 历史版 (Level 3 History - 1980年至今撤销合并对照 · 8,338 行)
-- 发行文件名参考: 2026年最新行政区划数据库-三级-历史版.csv / qqzeng_area_l3_history.csv
-- ##############################################################################

DROP TABLE IF EXISTS `areallyear`;
CREATE TABLE `areallyear` (
  `admin_code` varchar(20) NOT NULL COMMENT '历史行政区划代码',
  `parent_code` varchar(20) DEFAULT '' COMMENT '历史父级代码',
  `level_type` varchar(10) DEFAULT '' COMMENT '级别类型 (1:省 2:市 3:区县)',
  `name` varchar(100) NOT NULL COMMENT '历史区划名称',
  `province` varchar(50) DEFAULT '' COMMENT '所属省份',
  `city` varchar(50) DEFAULT '' COMMENT '所属城市',
  `district` varchar(50) DEFAULT '' COMMENT '所属区县',
  `remark` text COMMENT '撤销/合并/变更历史沿革说明',
  `new_admin_code` varchar(20) DEFAULT '' COMMENT '映射最新有效行政区划代码',
  `new_address` varchar(200) DEFAULT '' COMMENT '映射当前最新省市区完整地址',
  `new_short` varchar(100) DEFAULT '' COMMENT '映射当前最新省市区简称聚合',
  `year_time` varchar(20) DEFAULT '' COMMENT '变更/撤销年份 (如: 1980, 2023)',
  KEY `idx_admin` (`admin_code`),
  KEY `idx_new_admin` (`new_admin_code`),
  KEY `idx_year` (`year_time`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='全国行政区划历史变更对照版';

-- 导入 CSV
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_area_l3_history.csv'
-- INTO TABLE `areallyear`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`admin_code`, `parent_code`, `level_type`, `name`, `province`, `city`, `district`, `remark`, `new_admin_code`, `new_address`, `new_short`, `year_time`);


-- ##############################################################################
-- 7. 常见问题排查与业务高频查询
-- ##############################################################################

/*
【错误 1148 / 2068 排查指南】
1. Error Code: 1148. The used command is not allowed with this MySQL version
   解决办法：修改 MySQL 配置文件 my.cnf / my.ini：
   [mysqld]
   local_infile = 1
   [mysql]
   local_infile = 1
   重启 MySQL 服务；客户端连接时指定参数：mysql --local-infile=1 -u root -p

2. Error Code: 2068. LOAD DATA LOCAL INFILE file request rejected due to restrictions on access.
   解决办法：把 CSV 放到 MySQL 的 secure_file_priv 目录下，去掉 LOCAL 使用 LOAD DATA INFILE 命令。
*/

-- 业务查询 1: 查询某省（如广东省 440000）下属的所有地级市
-- SELECT admin_code, name, short_name, area_code, postal_code 
-- FROM areas 
-- WHERE parent_code = '440000' AND level_type = '2' 
-- ORDER BY admin_code;

-- 业务查询 2: 查询某市（如深圳市 440300）名下所有区县
-- SELECT admin_code, name, short_name, lng, lat 
-- FROM areas 
-- WHERE parent_code = '440300' AND level_type = '3' 
-- ORDER BY admin_code;

-- 业务查询 3: MySQL 8.0+ 递归查询某个区县的祖先链条
-- WITH RECURSIVE area_ancestors AS (
--   SELECT admin_code, parent_code, name, level_type, CAST(name AS CHAR(200)) AS full_chain
--   FROM areas WHERE admin_code = '440305' -- 深圳市南山区
--   UNION ALL
--   SELECT a.admin_code, a.parent_code, a.name, a.level_type, CONCAT(a.name, ' > ', r.full_chain)
--   FROM areas a
--   INNER JOIN area_ancestors r ON a.admin_code = r.parent_code
-- )
-- SELECT * FROM area_ancestors ORDER BY level_type ASC;
