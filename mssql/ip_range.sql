-- ==========================================
-- SQL Server (MSSQL) - Range 起止IP数值范围格式 IP 数据库导入
-- 支持版本：标准版(std) / 专业版(pro) / ASN版(asn) / 旗舰版(ult) / 至尊版(max)
-- ==========================================

-- ------------------------------------------
-- 标准版 (Standard Edition) - Range
-- ------------------------------------------
CREATE TABLE [dbo].[qqzeng_ip_range_std] (
  [ip_start] NVARCHAR(45) NOT NULL,
  [ip_end] NVARCHAR(45) NOT NULL,
  [ip_start_num] DECIMAL(38, 0) NOT NULL, -- MSSQL 最大精度 38 位，支持 IPv6 数值
  [ip_end_num] DECIMAL(38, 0) NOT NULL,
  [continent] NVARCHAR(150) NULL,
  [country_code] NVARCHAR(150) NULL,
  [country] NVARCHAR(150) NULL,
  [province] NVARCHAR(150) NULL,
  [city] NVARCHAR(150) NULL,
  [isp] NVARCHAR(150) NULL,
);

CREATE CLUSTERED INDEX [idx_qqzeng_ip_range_std_num] ON [dbo].[qqzeng_ip_range_std]([ip_start_num], [ip_end_num]);

-- 导入 CSV 示例:
-- BULK INSERT [dbo].[qqzeng_ip_range_std]
-- FROM 'C:\path\to\qqzeng_ip_std_global_range.csv'
-- WITH ( FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', CODEPAGE = '65001' );


-- ------------------------------------------
-- 专业版 (Professional Edition) - Range
-- ------------------------------------------
CREATE TABLE [dbo].[qqzeng_ip_range_pro] (
  [ip_start] NVARCHAR(45) NOT NULL,
  [ip_end] NVARCHAR(45) NOT NULL,
  [ip_start_num] DECIMAL(38, 0) NOT NULL, -- MSSQL 最大精度 38 位，支持 IPv6 数值
  [ip_end_num] DECIMAL(38, 0) NOT NULL,
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

CREATE CLUSTERED INDEX [idx_qqzeng_ip_range_pro_num] ON [dbo].[qqzeng_ip_range_pro]([ip_start_num], [ip_end_num]);

-- 导入 CSV 示例:
-- BULK INSERT [dbo].[qqzeng_ip_range_pro]
-- FROM 'C:\path\to\qqzeng_ip_pro_global_range.csv'
-- WITH ( FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', CODEPAGE = '65001' );


-- ------------------------------------------
-- ASN 路由版 (ASN Edition) - Range
-- ------------------------------------------
CREATE TABLE [dbo].[qqzeng_ip_range_asn] (
  [ip_start] NVARCHAR(45) NOT NULL,
  [ip_end] NVARCHAR(45) NOT NULL,
  [ip_start_num] DECIMAL(38, 0) NOT NULL, -- MSSQL 最大精度 38 位，支持 IPv6 数值
  [ip_end_num] DECIMAL(38, 0) NOT NULL,
  [continent] NVARCHAR(150) NULL,
  [country_code] NVARCHAR(150) NULL,
  [country] NVARCHAR(150) NULL,
  [isp] NVARCHAR(150) NULL,
  [asn] NVARCHAR(150) NULL,
  [as_name] NVARCHAR(150) NULL,
  [as_domain] NVARCHAR(150) NULL,
  [usage_flags] NVARCHAR(150) NULL,
);

CREATE CLUSTERED INDEX [idx_qqzeng_ip_range_asn_num] ON [dbo].[qqzeng_ip_range_asn]([ip_start_num], [ip_end_num]);

-- 导入 CSV 示例:
-- BULK INSERT [dbo].[qqzeng_ip_range_asn]
-- FROM 'C:\path\to\qqzeng_ip_asn_global_range.csv'
-- WITH ( FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', CODEPAGE = '65001' );


-- ------------------------------------------
-- 旗舰版 (Ultimate Edition) - Range
-- ------------------------------------------
CREATE TABLE [dbo].[qqzeng_ip_range_ult] (
  [ip_start] NVARCHAR(45) NOT NULL,
  [ip_end] NVARCHAR(45) NOT NULL,
  [ip_start_num] DECIMAL(38, 0) NOT NULL, -- MSSQL 最大精度 38 位，支持 IPv6 数值
  [ip_end_num] DECIMAL(38, 0) NOT NULL,
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
  [usage_flags] NVARCHAR(150) NULL,
);

CREATE CLUSTERED INDEX [idx_qqzeng_ip_range_ult_num] ON [dbo].[qqzeng_ip_range_ult]([ip_start_num], [ip_end_num]);

-- 导入 CSV 示例:
-- BULK INSERT [dbo].[qqzeng_ip_range_ult]
-- FROM 'C:\path\to\qqzeng_ip_ult_global_range.csv'
-- WITH ( FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', CODEPAGE = '65001' );


-- ------------------------------------------
-- 至尊版 (Max Edition) - Range
-- ------------------------------------------
CREATE TABLE [dbo].[qqzeng_ip_range_max] (
  [ip_start] NVARCHAR(45) NOT NULL,
  [ip_end] NVARCHAR(45) NOT NULL,
  [ip_start_num] DECIMAL(38, 0) NOT NULL, -- MSSQL 最大精度 38 位，支持 IPv6 数值
  [ip_end_num] DECIMAL(38, 0) NOT NULL,
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
  [usage_flags] NVARCHAR(150) NULL,
);

CREATE CLUSTERED INDEX [idx_qqzeng_ip_range_max_num] ON [dbo].[qqzeng_ip_range_max]([ip_start_num], [ip_end_num]);

-- 导入 CSV 示例:
-- BULK INSERT [dbo].[qqzeng_ip_range_max]
-- FROM 'C:\path\to\qqzeng_ip_max_global_range.csv'
-- WITH ( FIRSTROW = 2, FIELDTERMINATOR = ',', ROWTERMINATOR = '\n', CODEPAGE = '65001' );

