-- mysql 导入数据库 

-- 创建表 最新行政区划数据库

-- 字段 区划ID-父ID-全称-全称聚合-简称-简称聚合-级别-区号-邮编-拼音-简拼-首字母-经度-纬度-行政区-功能区

create table `zengip`.`areas` (
    `ID` varchar(45) null,
    `ParentId` varchar(45) null,
    `Name` varchar(45) null,
    `MergerName` varchar(200) null,
    `ShortName` varchar(45) null,
    `MergerShortName` varchar(200) null,
    `LevelType` varchar(45) null,
    `CityCode` varchar(45) null,
    `ZipCode` varchar(45) null,
    `Pinyin` varchar(45) null,
    `Jianpin` varchar(45) null,
    `FirstChar` varchar(45) null,
    `lng` varchar(45) null,
    `Lat` varchar(45) null,
    `Remark1` varchar(45) null,
    `Remark2` varchar(45) null
);




-- 导入数据库
load data local infile 'C:\ProgramData\MySQL\MySQL Server 8.0\Uploads\areas.txt'
into table areas
fields terminated by '\t'  -- '|'
lines terminated by '\n'
(ID,ParentId,Name,MergerName,ShortName,MergerShortName,LevelType,CityCode,ZipCode,Pinyin,Jianpin,FirstChar,lng,Lat,Remark1,Remark2);


-- Error Code: 1148. The used command is not allowed with this MySQL version
-- local-infile = 1 修改后必须重启mysql


-- xls 全选复制到txt
-- 默认 txt为中文编码  导入时 请转为utf-8编码 以免乱码 


