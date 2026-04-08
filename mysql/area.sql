
-- ==============================================================================
-- 行政区划数据库部署脚本 (MySQL / MSSQL / PostgreSQL)
-- 包含版本：三级基础版、三级旗舰版、三级历史版
-- 开发者提示：请确保 CSV 文件为 UTF-8 编码，并根据实际路径修改 LOAD DATA / BULK INSERT 路径
-- ==============================================================================

/*
*******************************************************************************
1. MySQL 部署脚本
*******************************************************************************
*/

-- 1.1 三级 基础版 (Level 3 Basic)
CREATE TABLE IF NOT EXISTS area_basic (
    admin_code VARCHAR(12) NOT NULL PRIMARY KEY,
    parent_code VARCHAR(12),
    level_type INT,
    name VARCHAR(100),
    short_name VARCHAR(100),
    province_short_name VARCHAR(100),
    city_short_name VARCHAR(100),
    district_short_name VARCHAR(100),
    area_code VARCHAR(20),
    postal_code VARCHAR(10),
    remark1 VARCHAR(255),
    remark2 VARCHAR(255)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- MySQL 导入命令 (请修改路径)
-- LOAD DATA INFILE '/path/to/area_basic.csv' INTO TABLE area_basic FIELDS TERMINATED BY ',' ENCLOSED BY '"' LINES TERMINATED BY '\n' IGNORE 1 LINES;

-- 1.2 三级 旗舰版 (Level 3 Flagship)
CREATE TABLE IF NOT EXISTS area_flagship (
    admin_code VARCHAR(12) NOT NULL PRIMARY KEY,
    parent_code VARCHAR(12),
    level_type INT,
    name VARCHAR(100),
    short_name VARCHAR(100),
    parent_path VARCHAR(255),
    province VARCHAR(100),
    city VARCHAR(100),
    district VARCHAR(100),
    province_short_name VARCHAR(100),
    city_short_name VARCHAR(100),
    district_short_name VARCHAR(100),
    province_pinyin VARCHAR(255),
    city_pinyin VARCHAR(255),
    district_pinyin VARCHAR(255),
    pinyin VARCHAR(255),
    jianpin VARCHAR(100),
    first_char VARCHAR(10),
    area_code VARCHAR(20),
    postal_code VARCHAR(10),
    lng DECIMAL(10, 6),
    lat DECIMAL(10, 6),
    remark1 VARCHAR(255),
    remark2 VARCHAR(255)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 1.3 三级 历史版 (History Version)
CREATE TABLE IF NOT EXISTS area_historical (  
    admin_code VARCHAR(12),
    parent_code VARCHAR(12),
    level_type INT,
    name VARCHAR(100),
    province VARCHAR(100),
    city VARCHAR(100),
    district VARCHAR(100),
    remark TEXT,
    new_admin_code VARCHAR(12),
    new_address VARCHAR(255),
    new_short VARCHAR(255),
    year_time VARCHAR(20)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;


/*
*******************************************************************************
2. SQL Server (MSSQL) 部署脚本
*******************************************************************************
*/

-- 2.1 三级 基础版
CREATE TABLE area_basic (
    admin_code NVARCHAR(12) NOT NULL PRIMARY KEY,
    parent_code NVARCHAR(12),
    level_type INT,
    name NVARCHAR(100),
    short_name NVARCHAR(100),
    province_short_name NVARCHAR(100),
    city_short_name NVARCHAR(100),
    district_short_name NVARCHAR(100),
    area_code NVARCHAR(20),
    postal_code NVARCHAR(10),
    remark1 NVARCHAR(255),
    remark2 NVARCHAR(255)
);

-- MSSQL 导入命令 (请修改路径)
-- BULK INSERT area_basic FROM 'C:\path\to\area_basic.csv' WITH (FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', DATAFILETYPE = 'char', CODEPAGE = '65001');

-- 2.2 三级 旗舰版
CREATE TABLE area_flagship (
    admin_code NVARCHAR(12) NOT NULL PRIMARY KEY,
    parent_code NVARCHAR(12),
    level_type INT,
    name NVARCHAR(100),
    short_name NVARCHAR(100),
    parent_path NVARCHAR(255),
    province NVARCHAR(100),
    city NVARCHAR(100),
    district NVARCHAR(100),
    province_short_name NVARCHAR(100),
    city_short_name NVARCHAR(100),
    district_short_name NVARCHAR(100),
    province_pinyin NVARCHAR(255),
    city_pinyin NVARCHAR(255),
    district_pinyin NVARCHAR(255),
    pinyin NVARCHAR(255),
    jianpin NVARCHAR(100),
    first_char NVARCHAR(10),
    area_code NVARCHAR(20),
    postal_code NVARCHAR(10),
    lng DECIMAL(10, 6),
    lat DECIMAL(10, 6),
    remark1 NVARCHAR(255),
    remark2 NVARCHAR(255)
);

-- 2.3 三级 历史版
CREATE TABLE area_historical (
    admin_code NVARCHAR(12),
    parent_code NVARCHAR(12),
    level_type INT,
    name NVARCHAR(100),
    province NVARCHAR(100),
    city NVARCHAR(100),
    district NVARCHAR(100),
    remark NVARCHAR(MAX),
    new_admin_code NVARCHAR(12),
    new_address NVARCHAR(255),
    new_short NVARCHAR(255),
    year_time NVARCHAR(20)
);


/*
*******************************************************************************
3. PostgreSQL 部署脚本
*******************************************************************************
*/

-- 3.1 三级 基础版
CREATE TABLE area_basic (
    admin_code VARCHAR(12) PRIMARY KEY,
    parent_code VARCHAR(12),
    level_type INT,
    name VARCHAR(100),
    short_name VARCHAR(100),
    province_short_name VARCHAR(100),
    city_short_name VARCHAR(100),
    district_short_name VARCHAR(100),
    area_code VARCHAR(20),
    postal_code VARCHAR(10),
    remark1 VARCHAR(255),
    remark2 VARCHAR(255)
);

-- PostgreSQL 导入命令 (请修改路径)
-- COPY area_basic FROM '/path/to/area_basic.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8');

-- 3.2 三级 旗舰版
CREATE TABLE area_flagship (
    admin_code VARCHAR(12) PRIMARY KEY,
    parent_code VARCHAR(12),
    level_type INT,
    name VARCHAR(100),
    short_name VARCHAR(100),
    parent_path VARCHAR(255),
    province VARCHAR(100),
    city VARCHAR(100),
    district VARCHAR(100),
    province_short_name VARCHAR(100),
    city_short_name VARCHAR(100),
    district_short_name VARCHAR(100),
    province_pinyin VARCHAR(255),
    city_pinyin VARCHAR(255),
    district_pinyin VARCHAR(255),
    pinyin VARCHAR(255),
    jianpin VARCHAR(100),
    first_char VARCHAR(10),
    area_code VARCHAR(20),
    postal_code VARCHAR(10),
    lng NUMERIC(10, 6),
    lat NUMERIC(10, 6),
    remark1 VARCHAR(255),
    remark2 VARCHAR(255)
);

-- 3.3 三级 历史版
CREATE TABLE area_historical (
    admin_code VARCHAR(12),
    parent_code VARCHAR(12),
    level_type INT,
    name VARCHAR(100),
    province VARCHAR(100),
    city VARCHAR(100),
    district VARCHAR(100),
    remark TEXT,
    new_admin_code VARCHAR(12),
    new_address VARCHAR(255),
    new_short VARCHAR(255),
    year_time VARCHAR(20)
);

-- PostgreSQL 历史版导入示例
-- COPY area_historical (admin_code, parent_code, level_type, name, province, city, district, remark, new_admin_code, new_address, new_short, year_time) FROM '/path/to/area_historical.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8');


















-- mysql 导入数据库 旧版

-- 创建表 最新行政区划数据库

-- 字段 
--区划ID
--父ID
--级别
--全称
--简称	
--ID路径（110000,110100,110105）
--省份全称
--城市全称
--县区全称
--省份简称
--城市简称
--县区简称
--省份拼音
--城市拼音
--县区拼音
--拼音
--简拼
--首拼
--区号
--邮编
--经度
--纬度
--是否行政区
--类型（县级市|地级市|经济开发区|高新区|新区）


CREATE TABLE `areas` (
  `ID` varchar(45) DEFAULT NULL,
  `ParentId` varchar(45) DEFAULT NULL,
  `LevelType` varchar(45) DEFAULT NULL,
  `Name` varchar(45) DEFAULT NULL,
  `ShortName` varchar(45) DEFAULT NULL,
  `ParentPath` varchar(45) DEFAULT NULL,
  `Province` varchar(45) DEFAULT NULL,
  `City` varchar(45) DEFAULT NULL,
  `District` varchar(45) DEFAULT NULL,
  `ProvinceShortName` varchar(45) DEFAULT NULL,
  `CityShortName` varchar(45) DEFAULT NULL,
  `DistrictShortName` varchar(45) DEFAULT NULL,
  `ProvincePinyin` varchar(45) DEFAULT NULL,
  `CityPinyin` varchar(45) DEFAULT NULL,
  `DistrictPinyin` varchar(45) DEFAULT NULL,
  `CityCode` varchar(45) DEFAULT NULL,
  `ZipCode` varchar(45) DEFAULT NULL,
  `Pinyin` varchar(45) DEFAULT NULL,
  `Jianpin` varchar(45) DEFAULT NULL,
  `FirstChar` varchar(45) DEFAULT NULL,
  `lng` varchar(45) DEFAULT NULL,
  `Lat` varchar(45) DEFAULT NULL,
  `Remark1` varchar(45) DEFAULT NULL,
  `Remark2` varchar(45) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;




-- 导入数据库
load data local infile 'C:\ProgramData\MySQL\MySQL Server 8.0\Uploads\areas.txt'
into table areas
fields terminated by '\t'  -- '|'
lines terminated by '\n'
(ID,ParentId,LevelType,Name,ShortName,ParentPath,Province,City,District,ProvinceShortName,CityShortName,DistrictShortName,ProvincePinyin,CityPinyin,DistrictPinyin,Pinyin,Jianpin,FirstChar,CityCode,ZipCode, lng,Lat,Remark1,Remark2);


-- Error Code: 1148. The used command is not allowed with this MySQL version
-- local-infile = 1 修改后必须重启mysql


-- xls 全选复制到txt
-- 默认 txt为中文编码  导入时 请转为utf-8编码 以免乱码 


