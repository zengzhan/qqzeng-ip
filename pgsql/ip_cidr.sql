-- ==========================================
-- PostgreSQL - CIDR/网络掩码 格式 IP 数据库导入与检索脚本
-- 支持版本：标准版(std) / 专业版(pro) / ASN版(asn) / 旗舰版(ult) / 至尊版(max)
-- ==========================================

-- ------------------------------------------
-- 标准版 (Standard Edition) - CIDR
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS public.qqzeng_ip_std (
  cidr CIDR NOT NULL PRIMARY KEY, -- 使用 PG 原生 CIDR/INET 类型
  continent VARCHAR(150),
  country_code VARCHAR(150),
  country VARCHAR(150),
  province VARCHAR(150),
  city VARCHAR(150),
  isp VARCHAR(150),
);

-- 创建 GiST 索引提供极速掩码扫描
CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_std_cidr ON public.qqzeng_ip_std USING gist (cidr_ops(cidr));

-- 导入 CSV 示例:
-- COPY public.qqzeng_ip_std(cidr, continent, country_code, country, province, city, isp)
-- FROM '/path/to/qqzeng_ip_std_global.csv' WITH (FORMAT csv, HEADER true, DELIMITER ',');


-- ------------------------------------------
-- 专业版 (Professional Edition) - CIDR
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS public.qqzeng_ip_pro (
  cidr CIDR NOT NULL PRIMARY KEY, -- 使用 PG 原生 CIDR/INET 类型
  continent VARCHAR(150),
  country_code VARCHAR(150),
  country VARCHAR(150),
  province VARCHAR(150),
  city VARCHAR(150),
  district VARCHAR(150),
  geo_id VARCHAR(150),
  longitude VARCHAR(150),
  latitude VARCHAR(150),
  timezone VARCHAR(150),
  isp VARCHAR(150),
);

-- 创建 GiST 索引提供极速掩码扫描
CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_pro_cidr ON public.qqzeng_ip_pro USING gist (cidr_ops(cidr));

-- 导入 CSV 示例:
-- COPY public.qqzeng_ip_pro(cidr, continent, country_code, country, province, city, district, geo_id, longitude, latitude, timezone, isp)
-- FROM '/path/to/qqzeng_ip_pro_global.csv' WITH (FORMAT csv, HEADER true, DELIMITER ',');


-- ------------------------------------------
-- ASN 路由版 (ASN Edition) - CIDR
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS public.qqzeng_ip_asn (
  cidr CIDR NOT NULL PRIMARY KEY, -- 使用 PG 原生 CIDR/INET 类型
  continent VARCHAR(150),
  country_code VARCHAR(150),
  country VARCHAR(150),
  isp VARCHAR(150),
  asn VARCHAR(150),
  as_name VARCHAR(150),
  as_domain VARCHAR(150),
  usage_type VARCHAR(150),
);

-- 创建 GiST 索引提供极速掩码扫描
CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_asn_cidr ON public.qqzeng_ip_asn USING gist (cidr_ops(cidr));

-- 导入 CSV 示例:
-- COPY public.qqzeng_ip_asn(cidr, continent, country_code, country, isp, asn, as_name, as_domain, usage_type)
-- FROM '/path/to/qqzeng_ip_asn_global.csv' WITH (FORMAT csv, HEADER true, DELIMITER ',');


-- ------------------------------------------
-- 旗舰版 (Ultimate Edition) - CIDR
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS public.qqzeng_ip_ult (
  cidr CIDR NOT NULL PRIMARY KEY, -- 使用 PG 原生 CIDR/INET 类型
  continent VARCHAR(150),
  country_code VARCHAR(150),
  country VARCHAR(150),
  province VARCHAR(150),
  city VARCHAR(150),
  district VARCHAR(150),
  geo_id VARCHAR(150),
  longitude VARCHAR(150),
  latitude VARCHAR(150),
  timezone VARCHAR(150),
  isp VARCHAR(150),
  asn VARCHAR(150),
  as_name VARCHAR(150),
  as_domain VARCHAR(150),
  usage_type VARCHAR(150),
);

-- 创建 GiST 索引提供极速掩码扫描
CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_ult_cidr ON public.qqzeng_ip_ult USING gist (cidr_ops(cidr));

-- 导入 CSV 示例:
-- COPY public.qqzeng_ip_ult(cidr, continent, country_code, country, province, city, district, geo_id, longitude, latitude, timezone, isp, asn, as_name, as_domain, usage_type)
-- FROM '/path/to/qqzeng_ip_ult_global.csv' WITH (FORMAT csv, HEADER true, DELIMITER ',');


-- ------------------------------------------
-- 至尊版 (Max Edition) - CIDR
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS public.qqzeng_ip_max (
  cidr CIDR NOT NULL PRIMARY KEY, -- 使用 PG 原生 CIDR/INET 类型
  continent VARCHAR(150),
  continent_en VARCHAR(150),
  country_code VARCHAR(150),
  country_alpha3 VARCHAR(150),
  country VARCHAR(150),
  country_en VARCHAR(150),
  province VARCHAR(150),
  province_en VARCHAR(150),
  city VARCHAR(150),
  city_en VARCHAR(150),
  district VARCHAR(150),
  district_en VARCHAR(150),
  geo_id VARCHAR(150),
  longitude VARCHAR(150),
  latitude VARCHAR(150),
  timezone VARCHAR(150),
  languages VARCHAR(150),
  currency_code VARCHAR(150),
  phone_prefix VARCHAR(150),
  emoji_flag VARCHAR(150),
  isp VARCHAR(150),
  asn VARCHAR(150),
  as_name VARCHAR(150),
  as_domain VARCHAR(150),
  usage_type VARCHAR(150),
);

-- 创建 GiST 索引提供极速掩码扫描
CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_max_cidr ON public.qqzeng_ip_max USING gist (cidr_ops(cidr));

-- 导入 CSV 示例:
-- COPY public.qqzeng_ip_max(cidr, continent, continent_en, country_code, country_alpha3, country, country_en, province, province_en, city, city_en, district, district_en, geo_id, longitude, latitude, timezone, languages, currency_code, phone_prefix, emoji_flag, isp, asn, as_name, as_domain, usage_type)
-- FROM '/path/to/qqzeng_ip_max_global.csv' WITH (FORMAT csv, HEADER true, DELIMITER ',');


-- ------------------------------------------
-- PostgreSQL 原生掩码匹配查询最佳实践
-- ------------------------------------------
-- SELECT * FROM public.qqzeng_ip_max WHERE cidr >>= '114.114.114.114'::inet LIMIT 1;