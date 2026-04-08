--附 MSSQL导入方法:

--创建 最新行政区划数据库  旗舰版

--字段 区划ID-父ID-级别-全称-简称-拼音-简拼-首拼-区号-邮编-经度-纬度-备注-类型-
--    ID路径-省份全称-城市全称-县区全称-省份简称-城市简称-县区简称-省份拼音-城市拼音-县区拼音

SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[areas](
	[ID] [nvarchar](50) NULL,		--区划ID
	[ParentId] [nvarchar](50) NULL,		--父ID
	[LevelType] [nvarchar](50) NULL,	--级别
	[Name] [nvarchar](50) NULL,		--全称
	[ShortName] [nvarchar](50) NULL,	--简称	
	[ParentPath] [nvarchar](50) NULL,	--ID路径（110000,110100,110105）
	[Province] [nvarchar](50) NULL,		--省份全称
	[City] [nvarchar](50) NULL,		--城市全称
	[District] [nvarchar](50) NULL,		--县区全称
	[ProvinceShortName] [nvarchar](50) NULL,--省份简称
	[CityShortName] [nvarchar](50) NULL,	--城市简称
	[DistrictShortName] [nvarchar](50) NULL,--县区简称
	[ProvincePinyin] [nvarchar](50) NULL,	--省份拼音
	[CityPinyin] [nvarchar](50) NULL,	--城市拼音
	[DistrictPinyin] [nvarchar](50) NULL,	--县区拼音
	[Pinyin] [nvarchar](50) NULL,		--拼音
	[Jianpin] [nvarchar](50) NULL,		--简拼
	[FirstChar] [nvarchar](50) NULL,	--首拼
	[CityCode] [nvarchar](50) NULL,		--区号
	[ZipCode] [nvarchar](50) NULL,		--邮编
	[Lng] [nvarchar](50) NULL,		--经度
	[Lat] [nvarchar](50) NULL,		--纬度
	[Remark1] [nvarchar](50) NULL,		--是否行政区
	[Remark2] [nvarchar](50) NULL,		--类型（县级市|地级市|经济开发区|高新区|新区）
) ON [PRIMARY]
GO




--导入数据库   txt 中文编码
BULK INSERT dbo.[areas]
FROM 'G:\db\areas.txt' 
WITH (
    FIELDTERMINATOR = '\t',
    ROWTERMINATOR = '\n'
)


--查询
SELECT  * FROM dbo.areas 


--省市区街道乡镇 四级 旗舰版
CREATE TABLE [dbo].[areasTown](
	[AreaId] [int] IDENTITY(1,1) NOT NULL,
	[ID] [int] NULL,				--行政区划编码
	[ParentId] [int] NULL,			--父子级联
	[LevelType] [int] NULL,			--级别
	[Name] [nvarchar](50) NULL,		--名称
	[ParentPath] [nvarchar](50) NULL,--组合路径
	[Province] [nvarchar](50) NULL,	--省份
	[City] [nvarchar](50) NULL,		--城市
	[District] [nvarchar](50) NULL,	--县区
	[Town] [nvarchar](50) NULL,		--街道/乡镇
	[Pinyin] [nvarchar](200) NULL,	--拼音
	[Jianpin] [nvarchar](50) NULL,	--简拼
	[FirstChar] [nvarchar](50) NULL,--首字母
	[CityCode] [nvarchar](50) NULL,	--区号
	[ZipCode] [nvarchar](50) NULL,	--邮编
	[Lng] [nvarchar](50) NULL,		--经度
	[Lat] [nvarchar](50) NULL,		--纬度
 )
 
 
 

