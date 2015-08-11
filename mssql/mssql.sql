--附 MSSQL导入方法:

--创建 新版表 增加 大洲 国家英文名称 国家简码 经度 纬度
CREATE TABLE [dbo].[ip](
 [ip_start] [varchar](50) NULL,
 [ip_end] [varchar](50) NULL,
 [ip_start_num] [bigint] NULL,
 [ip_end_num] [bigint] NULL,
 [continent] [varchar](50) NULL,
 [country] [varchar](50) NULL,
 [province] [varchar](50) NULL,
 [city] [varchar](50) NULL,
 [district] [varchar](50) NULL,
 [isp] [varchar](50) NULL,
 [area_code] [varchar](50) NULL,
 [country_english] [varchar](50) NULL,
 [country_code] [varchar](50) NULL,
 [longitude] [varchar](50) NULL,
 [latitude] [varchar](50) NULL
)



--创建表
CREATE TABLE [dbo].[ip](
 [Start] [varchar](50) NULL,
 [End] [varchar](50) NULL,
 [StartNum] [bigint] NULL,
 [EndNum] [bigint] NULL,
 [Country] [varchar](50) NULL,
 [Province] [varchar](50) NULL,
 [City] [varchar](50) NULL,
 [District] [varchar](50) NULL,
 [Isp] [varchar](50) NULL,
 [Code] [int] NULL 
)


--导入数据库
BULK INSERT dbo.[ip]
FROM 'G:\IP数据库\ip.txt' 
WITH (
    FIELDTERMINATOR = '|',
    ROWTERMINATOR = '\n'
)


--查询
SELECT  * FROM dbo.ip WHERE @num BETWEEN StartNum AND EndNum