--附 MSSQL导入方法:

--创建 最新IP地址数据库表 

--字段 ip段 数字段 大洲 国家 省份 城市 县区 运营商 区划代码 国家英文名称 国家简码 经度 纬度

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





--导入数据库
BULK INSERT dbo.[ip]
FROM 'G:\IP数据库\ip.txt' 
WITH (
    FIELDTERMINATOR = '|',
    ROWTERMINATOR = '\n'
)


--查询
SELECT  * FROM dbo.ip WHERE @num BETWEEN StartNum AND EndNum
