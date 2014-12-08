--附 MSSQL导入方法:


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