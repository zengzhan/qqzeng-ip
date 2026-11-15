-- ==============================================================================
-- qqzeng-area 全国行政区划数据库 - Microsoft SQL Server (MSSQL) 部署脚本
-- 官方网站: https://www.qqzeng.com
-- 包含版本：三级基础版、三级旗舰版、四级基础版、四级旗舰版、五级基础版、三级历史变更版
-- 字段 100% 对齐官方最新发行包 (CSV / TXT / SQL)
-- ==============================================================================

USE [master];
GO

-- ##############################################################################
-- 1. 三级 基础版 (Level 3 Base - 省市区三级 · 4,558 行)
-- 表名: areasbase
-- ##############################################################################

IF OBJECT_ID(N'dbo.areasbase', N'U') IS NOT NULL
    DROP TABLE dbo.areasbase;
GO

CREATE TABLE [dbo].[areasbase] (
    [admin_code]          [nvarchar](20) NOT NULL PRIMARY KEY, -- 行政区划代码
    [parent_code]         [nvarchar](20) NULL DEFAULT '',      -- 父级区划代码 (省级为空)
    [level_type]          [nvarchar](10) NULL DEFAULT '',      -- 级别类型 (0:国 1:省 2:市 3:区县)
    [name]                [nvarchar](100) NOT NULL,             -- 区划名称
    [short_name]          [nvarchar](50) NULL DEFAULT '',      -- 简称
    [province_short_name] [nvarchar](50) NULL DEFAULT '',      -- 省份简称
    [city_short_name]     [nvarchar](50) NULL DEFAULT '',      -- 城市简称
    [district_short_name] [nvarchar](50) NULL DEFAULT '',      -- 区县简称
    [area_code]           [nvarchar](10) NULL DEFAULT '',      -- 电话区号
    [postal_code]         [nvarchar](10) NULL DEFAULT '',      -- 邮政编码
    [remark1]             [nvarchar](50) NULL DEFAULT '',      -- 备注1 (如: 行政区)
    [remark2]             [nvarchar](50) NULL DEFAULT ''       -- 备注2
);
GO

CREATE NONCLUSTERED INDEX [idx_areasbase_parent] ON [dbo].[areasbase] ([parent_code]);
GO

-- 批量导入 CSV 命令 (UTF-8 编码，跳过首行表头)
-- BULK INSERT [dbo].[areasbase]
-- FROM 'C:\data\qqzeng_area_l3_base.csv'
-- WITH (
--     FIRSTROW = 2,
--     FIELDTERMINATOR = ',',
--     ROWTERMINATOR = '0x0a', -- 若为 Windows CRLF 换行可设为 '\n' 或 '0x0d0a'
--     CODEPAGE = '65001',
--     TABLOCK
-- );
-- GO


-- ##############################################################################
-- 2. 三级 旗舰版 (Level 3 Ultra - 全要素高精度省市区 · 4,558 行)
-- 表名: areas
-- ##############################################################################

IF OBJECT_ID(N'dbo.areas', N'U') IS NOT NULL
    DROP TABLE dbo.areas;
GO

CREATE TABLE [dbo].[areas] (
    [admin_code]          [nvarchar](20) NOT NULL PRIMARY KEY, -- 行政区划代码
    [parent_code]         [nvarchar](20) NULL DEFAULT '',      -- 父级区划代码 (省级为空)
    [level_type]          [nvarchar](10) NULL DEFAULT '',      -- 级别类型 (0:国 1:省 2:市 3:区县)
    [name]                [nvarchar](100) NOT NULL,             -- 区划全称
    [short_name]          [nvarchar](50) NULL DEFAULT '',      -- 简称
    [parent_path]         [nvarchar](100) NULL DEFAULT '',     -- 全层级组合路径 (如 110000,110100,110105)
    [province]            [nvarchar](50) NULL DEFAULT '',      -- 省份全称
    [city]                [nvarchar](50) NULL DEFAULT '',      -- 城市全称
    [district]            [nvarchar](50) NULL DEFAULT '',      -- 区县全称
    [province_short_name] [nvarchar](50) NULL DEFAULT '',      -- 省份简称
    [city_short_name]     [nvarchar](50) NULL DEFAULT '',      -- 城市简称
    [district_short_name] [nvarchar](50) NULL DEFAULT '',      -- 区县简称
    [province_pinyin]     [nvarchar](50) NULL DEFAULT '',      -- 省份拼音
    [city_pinyin]         [nvarchar](50) NULL DEFAULT '',      -- 城市拼音
    [district_pinyin]     [nvarchar](50) NULL DEFAULT '',      -- 区县拼音
    [pinyin]              [nvarchar](100) NULL DEFAULT '',     -- 拼音全拼
    [jianpin]             [nvarchar](50) NULL DEFAULT '',      -- 简拼
    [first_char]          [nvarchar](10) NULL DEFAULT '',      -- 首字母
    [area_code]           [nvarchar](10) NULL DEFAULT '',      -- 电话区号
    [postal_code]         [nvarchar](10) NULL DEFAULT '',      -- 邮政编码
    [lng]                 [nvarchar](30) NULL DEFAULT '',      -- 经度坐标 (可存储为 decimal(10,6))
    [lat]                 [nvarchar](30) NULL DEFAULT '',      -- 纬度坐标 (可存储为 decimal(10,6))
    [remark1]             [nvarchar](50) NULL DEFAULT '',      -- 备注1 (如: 行政区)
    [remark2]             [nvarchar](50) NULL DEFAULT ''       -- 备注2 (如: 县级市/地级市/新区)
);
GO

CREATE NONCLUSTERED INDEX [idx_areas_parent] ON [dbo].[areas] ([parent_code]);
CREATE NONCLUSTERED INDEX [idx_areas_path] ON [dbo].[areas] ([parent_path]);
CREATE NONCLUSTERED INDEX [idx_areas_city] ON [dbo].[areas] ([city]);
GO

-- 批量导入 CSV 命令
-- BULK INSERT [dbo].[areas]
-- FROM 'C:\data\qqzeng_area_l3_ultra.csv'
-- WITH (
--     FIRSTROW = 2,
--     FIELDTERMINATOR = ',',
--     ROWTERMINATOR = '0x0a',
--     CODEPAGE = '65001',
--     TABLOCK
-- );
-- GO


-- ##############################################################################
-- 3. 四级 基础版 (Level 4 Base - 街道乡镇四级 · 42,222 行)
-- 表名: areastownbase
-- ##############################################################################

IF OBJECT_ID(N'dbo.areastownbase', N'U') IS NOT NULL
    DROP TABLE dbo.areastownbase;
GO

CREATE TABLE [dbo].[areastownbase] (
    [ID]        [nvarchar](20) NOT NULL PRIMARY KEY, -- 行政区划代码
    [ParentId]  [nvarchar](20) NULL DEFAULT '',      -- 父级区划代码 (省级为空)
    [LevelType] [nvarchar](10) NULL DEFAULT '',      -- 行政级别 (1:省 2:市 3:区县 4:街道乡镇)
    [Name]      [nvarchar](100) NOT NULL,             -- 区划名称
    [Province]  [nvarchar](50) NULL DEFAULT '',      -- 所属省份
    [City]      [nvarchar](50) NULL DEFAULT '',      -- 所属城市
    [District]  [nvarchar](50) NULL DEFAULT '',      -- 所属区县
    [Town]      [nvarchar](100) NULL DEFAULT ''      -- 所属街道/乡镇
);
GO

CREATE NONCLUSTERED INDEX [idx_townbase_parent] ON [dbo].[areastownbase] ([ParentId]);
GO

-- 批量导入 CSV
-- BULK INSERT [dbo].[areastownbase]
-- FROM 'C:\data\qqzeng_area_l4_base.csv'
-- WITH (
--     FIRSTROW = 2,
--     FIELDTERMINATOR = ',',
--     ROWTERMINATOR = '0x0a',
--     CODEPAGE = '65001',
--     TABLOCK
-- );
-- GO


-- ##############################################################################
-- 4. 四级 旗舰版 (Level 4 Ultra - 街道乡镇全要素高精度 · 42,222 行)
-- 表名: areastown
-- ##############################################################################

IF OBJECT_ID(N'dbo.areastown', N'U') IS NOT NULL
    DROP TABLE dbo.areastown;
GO

CREATE TABLE [dbo].[areastown] (
    [ID]         [nvarchar](20) NOT NULL PRIMARY KEY, -- 行政区划代码
    [ParentId]   [nvarchar](20) NULL DEFAULT '',      -- 父级区划代码
    [LevelType]  [nvarchar](10) NULL DEFAULT '',      -- 行政级别
    [Name]       [nvarchar](100) NOT NULL,             -- 区划名称
    [ParentPath] [nvarchar](100) NULL DEFAULT '',     -- 全层级组合路径
    [Province]   [nvarchar](50) NULL DEFAULT '',      -- 所属省份
    [City]       [nvarchar](50) NULL DEFAULT '',      -- 所属城市
    [District]   [nvarchar](50) NULL DEFAULT '',      -- 所属区县
    [Town]       [nvarchar](100) NULL DEFAULT '',     -- 所属街道/乡镇
    [Pinyin]     [nvarchar](100) NULL DEFAULT '',     -- 拼音全拼
    [Jianpin]    [nvarchar](50) NULL DEFAULT '',      -- 简拼
    [FirstChar]  [nvarchar](10) NULL DEFAULT '',      -- 首字母
    [CityCode]   [nvarchar](10) NULL DEFAULT '',      -- 电话区号
    [ZipCode]    [nvarchar](10) NULL DEFAULT '',      -- 邮政编码
    [Lng]        [nvarchar](30) NULL DEFAULT '',      -- 经度
    [Lat]        [nvarchar](30) NULL DEFAULT ''       -- 纬度
);
GO

CREATE NONCLUSTERED INDEX [idx_town_parent] ON [dbo].[areastown] ([ParentId]);
CREATE NONCLUSTERED INDEX [idx_town_path] ON [dbo].[areastown] ([ParentPath]);
GO

-- 批量导入 CSV
-- BULK INSERT [dbo].[areastown]
-- FROM 'C:\data\qqzeng_area_l4_ultra.csv'
-- WITH (
--     FIRSTROW = 2,
--     FIELDTERMINATOR = ',',
--     ROWTERMINATOR = '0x0a',
--     CODEPAGE = '65001',
--     TABLOCK
-- );
-- GO


-- ##############################################################################
-- 5. 五级 基础版 (Level 5 Base - 社区村五级 · 665,552 行)
-- 表名: areasvillage
-- ##############################################################################

IF OBJECT_ID(N'dbo.areasvillage', N'U') IS NOT NULL
    DROP TABLE dbo.areasvillage;
GO

CREATE TABLE [dbo].[areasvillage] (
    [ID]         [nvarchar](20) NOT NULL PRIMARY KEY, -- 12位统计用区划代码
    [ParentId]   [nvarchar](20) NULL DEFAULT '',      -- 父级区划代码 (9位街道代码)
    [LevelType]  [nvarchar](10) NULL DEFAULT '',      -- 级别类型 (固定为 5)
    [Name]       [nvarchar](100) NOT NULL,             -- 社区/村委会名称
    [Province]   [nvarchar](50) NULL DEFAULT '',      -- 所属省份
    [City]       [nvarchar](50) NULL DEFAULT '',      -- 所属城市
    [District]   [nvarchar](50) NULL DEFAULT '',      -- 所属区县
    [Town]       [nvarchar](100) NULL DEFAULT '',     -- 所属乡镇街道
    [Village]    [nvarchar](100) NULL DEFAULT '',     -- 所属村委会/社区居委会
    [ParentPath] [nvarchar](120) NULL DEFAULT ''      -- 5级完整组合路径
);
GO

CREATE NONCLUSTERED INDEX [idx_village_parent] ON [dbo].[areasvillage] ([ParentId]);
CREATE NONCLUSTERED INDEX [idx_village_path] ON [dbo].[areasvillage] ([ParentPath]);
GO

-- 批量导入 CSV
-- BULK INSERT [dbo].[areasvillage]
-- FROM 'C:\data\qqzeng_area_l5_base.csv'
-- WITH (
--     FIRSTROW = 2,
--     FIELDTERMINATOR = ',',
--     ROWTERMINATOR = '0x0a',
--     CODEPAGE = '65001',
--     TABLOCK
-- );
-- GO


-- ##############################################################################
-- 6. 三级 历史版 (Level 3 History - 1980年至今撤销合并对照 · 8,338 行)
-- 表名: areallyear
-- ##############################################################################

IF OBJECT_ID(N'dbo.areallyear', N'U') IS NOT NULL
    DROP TABLE dbo.areallyear;
GO

CREATE TABLE [dbo].[areallyear] (
    [admin_code]     [nvarchar](20) NOT NULL,            -- 历史行政区划代码
    [parent_code]    [nvarchar](20) NULL DEFAULT '',     -- 历史父级代码
    [level_type]     [nvarchar](10) NULL DEFAULT '',     -- 级别类型 (1:省 2:市 3:区县)
    [name]           [nvarchar](100) NOT NULL,            -- 历史区划名称
    [province]       [nvarchar](50) NULL DEFAULT '',     -- 所属省份
    [city]           [nvarchar](50) NULL DEFAULT '',     -- 所属城市
    [district]       [nvarchar](50) NULL DEFAULT '',     -- 所属区县
    [remark]         [nvarchar](max) NULL,                -- 变更说明
    [new_admin_code] [nvarchar](20) NULL DEFAULT '',     -- 对应最新区划代码
    [new_address]    [nvarchar](200) NULL DEFAULT '',    -- 对应最新地址聚合
    [new_short]      [nvarchar](100) NULL DEFAULT '',    -- 对应最新简称聚合
    [year_time]      [nvarchar](20) NULL DEFAULT ''      -- 变更年份
);
GO

CREATE NONCLUSTERED INDEX [idx_hist_admin] ON [dbo].[areallyear] ([admin_code]);
CREATE NONCLUSTERED INDEX [idx_hist_new_admin] ON [dbo].[areallyear] ([new_admin_code]);
GO

-- 批量导入 CSV
-- BULK INSERT [dbo].[areallyear]
-- FROM 'C:\data\qqzeng_area_l3_history.csv'
-- WITH (
--     FIRSTROW = 2,
--     FIELDTERMINATOR = ',',
--     ROWTERMINATOR = '0x0a',
--     CODEPAGE = '65001',
--     TABLOCK
-- );
-- GO


-- ##############################################################################
-- 7. SQL Server 高频业务查询实战 (CTE 递归树)
-- ##############################################################################

-- 向上递归：查找指定区县（如 440305 深圳市南山区）的完整上级链条
-- WITH AreaChain AS (
--     SELECT admin_code, parent_code, name, level_type, CAST(name AS NVARCHAR(500)) AS FullChain
--     FROM dbo.areas WHERE admin_code = '440305'
--     UNION ALL
--     SELECT a.admin_code, a.parent_code, a.name, a.level_type, CAST(a.name + ' > ' + c.FullChain AS NVARCHAR(500))
--     FROM dbo.areas a
--     INNER JOIN AreaChain c ON a.admin_code = c.parent_code
-- )
-- SELECT * FROM AreaChain ORDER BY level_type ASC;
