-- ==============================================================================
-- qqzeng-phone 手机号段归属地数据库 - Microsoft SQL Server (MSSQL) 部署脚本
-- 官方网站: https://www.qqzeng.com
-- 覆盖 51.9万+ 号段，包含 8 大核心维度字段
-- ==============================================================================

USE [master];
GO

IF OBJECT_ID(N'dbo.phone_location', N'U') IS NOT NULL
    DROP TABLE dbo.phone_location;
GO

CREATE TABLE [dbo].[phone_location] (
    [prefix]    [nvarchar](10) NOT NULL,            -- 前三位手机号段 (如 188)
    [phone]     [nvarchar](10) NOT NULL PRIMARY KEY, -- 7位完整手机号段 (如 1886999)
    [province]  [nvarchar](50) NULL DEFAULT '',     -- 所属省份
    [city]      [nvarchar](50) NULL DEFAULT '',     -- 所属城市
    [isp]       [nvarchar](50) NULL DEFAULT '',     -- 基础运营商 (中国移动/中国联通/中国电信/中国广电)
    [post_code] [nvarchar](10) NULL DEFAULT '',     -- 邮政编码
    [city_code] [nvarchar](10) NULL DEFAULT '',     -- 电话长途区号
    [area_code] [nvarchar](20) NULL DEFAULT ''      -- 行政区划代码 (6位)
);
GO

CREATE NONCLUSTERED INDEX [idx_phone_pref] ON [dbo].[phone_location] ([prefix]);
CREATE NONCLUSTERED INDEX [idx_phone_city] ON [dbo].[phone_location] ([city]);
CREATE NONCLUSTERED INDEX [idx_phone_isp] ON [dbo].[phone_location] ([isp]);
GO

-- 批量导入 CSV 命令 (UTF-8 编码，跳过首行表头)
-- BULK INSERT [dbo].[phone_location]
-- FROM 'C:\data\phone.csv'
-- WITH (
--     FIRSTROW = 2,
--     FIELDTERMINATOR = ',',
--     ROWTERMINATOR = '0x0a', -- 若为 Windows CRLF 换行可设为 '\n' 或 '0x0d0a'
--     CODEPAGE = '65001',
--     TABLOCK
-- );
-- GO

-- 查询示例：11 位手机号码归属地查询 (前7位前缀匹配)
-- SELECT * FROM [dbo].[phone_location] WHERE [phone] = LEFT('18869991234', 7);
