-- mysql 导入数据库 

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


