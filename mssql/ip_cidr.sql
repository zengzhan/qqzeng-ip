-- ==========================================
-- SQL Server (MSSQL) - CIDR 格式 IP 数据库导入与检索
-- 支持版本：标准版(std) / 专业版(pro) / ASN版(asn) / 旗舰版(ult) / 至尊版(max)
-- ==========================================

-- ------------------------------------------
-- 标准版 (Standard Edition) - CIDR
-- ------------------------------------------
CREATE TABLE [dbo].[qqzeng_ip_std] (
  [cidr] NVARCHAR(50) NOT NULL PRIMARY KEY,
  [continent] NVARCHAR(150) NULL,
  [country_code] NVARCHAR(150) NULL,
  [country] NVARCHAR(150) NULL,
  [province] NVARCHAR(150) NULL,
  [city] NVARCHAR(150) NULL,
  [isp] NVARCHAR(150) NULL,
);

-- 导入 CSV 示例:
-- BULK INSERT [dbo].[qqzeng_ip_std]
-- FROM 'C:\path\to\qqzeng_ip_std_global.csv'
-- WITH ( FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', CODEPAGE = '65001' );


-- ------------------------------------------
-- 专业版 (Professional Edition) - CIDR
-- ------------------------------------------
CREATE TABLE [dbo].[qqzeng_ip_pro] (
  [cidr] NVARCHAR(50) NOT NULL PRIMARY KEY,
  [continent] NVARCHAR(150) NULL,
  [country_code] NVARCHAR(150) NULL,
  [country] NVARCHAR(150) NULL,
  [province] NVARCHAR(150) NULL,
  [city] NVARCHAR(150) NULL,
  [district] NVARCHAR(150) NULL,
  [geo_id] NVARCHAR(150) NULL,
  [longitude] NVARCHAR(150) NULL,
  [latitude] NVARCHAR(150) NULL,
  [timezone] NVARCHAR(150) NULL,
  [isp] NVARCHAR(150) NULL,
);

-- 导入 CSV 示例:
-- BULK INSERT [dbo].[qqzeng_ip_pro]
-- FROM 'C:\path\to\qqzeng_ip_pro_global.csv'
-- WITH ( FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', CODEPAGE = '65001' );


-- ------------------------------------------
-- ASN 路由版 (ASN Edition) - CIDR
-- ------------------------------------------
CREATE TABLE [dbo].[qqzeng_ip_asn] (
  [cidr] NVARCHAR(50) NOT NULL PRIMARY KEY,
  [continent] NVARCHAR(150) NULL,
  [country_code] NVARCHAR(150) NULL,
  [country] NVARCHAR(150) NULL,
  [isp] NVARCHAR(150) NULL,
  [asn] NVARCHAR(150) NULL,
  [as_name] NVARCHAR(150) NULL,
  [as_domain] NVARCHAR(150) NULL,
  [usage_type] NVARCHAR(150) NULL,
);

-- 导入 CSV 示例:
-- BULK INSERT [dbo].[qqzeng_ip_asn]
-- FROM 'C:\path\to\qqzeng_ip_asn_global.csv'
-- WITH ( FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', CODEPAGE = '65001' );


-- ------------------------------------------
-- 旗舰版 (Ultimate Edition) - CIDR
-- ------------------------------------------
CREATE TABLE [dbo].[qqzeng_ip_ult] (
  [cidr] NVARCHAR(50) NOT NULL PRIMARY KEY,
  [continent] NVARCHAR(150) NULL,
  [country_code] NVARCHAR(150) NULL,
  [country] NVARCHAR(150) NULL,
  [province] NVARCHAR(150) NULL,
  [city] NVARCHAR(150) NULL,
  [district] NVARCHAR(150) NULL,
  [geo_id] NVARCHAR(150) NULL,
  [longitude] NVARCHAR(150) NULL,
  [latitude] NVARCHAR(150) NULL,
  [timezone] NVARCHAR(150) NULL,
  [isp] NVARCHAR(150) NULL,
  [asn] NVARCHAR(150) NULL,
  [as_name] NVARCHAR(150) NULL,
  [as_domain] NVARCHAR(150) NULL,
  [usage_type] NVARCHAR(150) NULL,
);

-- 导入 CSV 示例:
-- BULK INSERT [dbo].[qqzeng_ip_ult]
-- FROM 'C:\path\to\qqzeng_ip_ult_global.csv'
-- WITH ( FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', CODEPAGE = '65001' );


-- ------------------------------------------
-- 至尊版 (Max Edition) - CIDR
-- ------------------------------------------
CREATE TABLE [dbo].[qqzeng_ip_max] (
  [cidr] NVARCHAR(50) NOT NULL PRIMARY KEY,
  [continent] NVARCHAR(150) NULL,
  [continent_en] NVARCHAR(150) NULL,
  [country_code] NVARCHAR(150) NULL,
  [country_alpha3] NVARCHAR(150) NULL,
  [country] NVARCHAR(150) NULL,
  [country_en] NVARCHAR(150) NULL,
  [province] NVARCHAR(150) NULL,
  [province_en] NVARCHAR(150) NULL,
  [city] NVARCHAR(150) NULL,
  [city_en] NVARCHAR(150) NULL,
  [district] NVARCHAR(150) NULL,
  [district_en] NVARCHAR(150) NULL,
  [geo_id] NVARCHAR(150) NULL,
  [longitude] NVARCHAR(150) NULL,
  [latitude] NVARCHAR(150) NULL,
  [timezone] NVARCHAR(150) NULL,
  [languages] NVARCHAR(150) NULL,
  [currency_code] NVARCHAR(150) NULL,
  [phone_prefix] NVARCHAR(150) NULL,
  [emoji_flag] NVARCHAR(150) NULL,
  [isp] NVARCHAR(150) NULL,
  [asn] NVARCHAR(150) NULL,
  [as_name] NVARCHAR(150) NULL,
  [as_domain] NVARCHAR(150) NULL,
  [usage_type] NVARCHAR(150) NULL,
);

-- 导入 CSV 示例:
-- BULK INSERT [dbo].[qqzeng_ip_max]
-- FROM 'C:\path\to\qqzeng_ip_max_global.csv'
-- WITH ( FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', CODEPAGE = '65001' );

