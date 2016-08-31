--附 MSSQL导入方法:

--创建 最新行政区划数据库

--字段 区划ID-父ID-级别-全称-全称聚合-区号-邮编-简称-简称聚合-拼音-简拼-首字母-经度-纬度

CREATE TABLE [dbo].[areas](
	[ID] [NVARCHAR](50) NULL,
	[ParentId] [NVARCHAR](50) NULL,
	[LevelType] [NVARCHAR](50) NULL,
	[Name] [NVARCHAR](50) NULL,
	[MergerName] [NVARCHAR](200) NULL,
	[CityCode] [NVARCHAR](50) NULL,
	[ZipCode] [NVARCHAR](50) NULL,
	[ShortName] [NVARCHAR](50) NULL,
	[MergerShortName] [NVARCHAR](200) NULL,
	[Pinyin] [NVARCHAR](50) NULL,
	[Jianpin] [NVARCHAR](50) NULL,
	[FirstChar] [NVARCHAR](50) NULL,
	[Lng] [NVARCHAR](50) NULL,
	[Lat] [NVARCHAR](50) NULL,
	[Remark] [NVARCHAR](50) NULL
) 



--导入数据库
BULK INSERT dbo.[areas]
FROM 'G:\IP数据库\areas.txt' 
WITH (
    FIELDTERMINATOR = '\t',
    ROWTERMINATOR = '\n'
)


--查询
SELECT  * FROM dbo.areas 
