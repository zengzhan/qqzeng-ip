-- ==========================================
-- PostgreSQL - Range 起止IP数值范围格式 IP 数据库导入与检索脚本
-- 支持版本：标准版(std) / 专业版(pro) / ASN版(asn) / 旗舰版(ult) / 至尊版(max)
-- ==========================================

-- ------------------------------------------
-- 标准版 (Standard Edition) - Range
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS public.qqzeng_ip_range_std (
  ip_start VARCHAR(45) NOT NULL,
  ip_end VARCHAR(45) NOT NULL,
  ip_start_num NUMERIC(39, 0) NOT NULL,
  ip_end_num NUMERIC(39, 0) NOT NULL,
  continent VARCHAR(150),
  country_code VARCHAR(150),
  country VARCHAR(150),
  province VARCHAR(150),
  city VARCHAR(150),
  isp VARCHAR(150),
);

CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_range_std_num ON public.qqzeng_ip_range_std (ip_start_num, ip_end_num);

-- 导入 CSV 示例:
-- COPY public.qqzeng_ip_range_std(ip_start, ip_end, ip_start_num, ip_end_num, continent, country_code, country, province, city, isp)
-- FROM '/path/to/qqzeng_ip_std_global_range.csv' WITH (FORMAT csv, HEADER true, DELIMITER ',');


-- ------------------------------------------
-- 专业版 (Professional Edition) - Range
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS public.qqzeng_ip_range_pro (
  ip_start VARCHAR(45) NOT NULL,
  ip_end VARCHAR(45) NOT NULL,
  ip_start_num NUMERIC(39, 0) NOT NULL,
  ip_end_num NUMERIC(39, 0) NOT NULL,
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

CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_range_pro_num ON public.qqzeng_ip_range_pro (ip_start_num, ip_end_num);

-- 导入 CSV 示例:
-- COPY public.qqzeng_ip_range_pro(ip_start, ip_end, ip_start_num, ip_end_num, continent, country_code, country, province, city, district, geo_id, longitude, latitude, timezone, isp)
-- FROM '/path/to/qqzeng_ip_pro_global_range.csv' WITH (FORMAT csv, HEADER true, DELIMITER ',');


-- ------------------------------------------
-- ASN 路由版 (ASN Edition) - Range
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS public.qqzeng_ip_range_asn (
  ip_start VARCHAR(45) NOT NULL,
  ip_end VARCHAR(45) NOT NULL,
  ip_start_num NUMERIC(39, 0) NOT NULL,
  ip_end_num NUMERIC(39, 0) NOT NULL,
  continent VARCHAR(150),
  country_code VARCHAR(150),
  country VARCHAR(150),
  isp VARCHAR(150),
  asn VARCHAR(150),
  as_name VARCHAR(150),
  as_domain VARCHAR(150),
  usage_flags VARCHAR(150),
);

CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_range_asn_num ON public.qqzeng_ip_range_asn (ip_start_num, ip_end_num);

-- 导入 CSV 示例:
-- COPY public.qqzeng_ip_range_asn(ip_start, ip_end, ip_start_num, ip_end_num, continent, country_code, country, isp, asn, as_name, as_domain, usage_flags)
-- FROM '/path/to/qqzeng_ip_asn_global_range.csv' WITH (FORMAT csv, HEADER true, DELIMITER ',');


-- ------------------------------------------
-- 旗舰版 (Ultimate Edition) - Range
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS public.qqzeng_ip_range_ult (
  ip_start VARCHAR(45) NOT NULL,
  ip_end VARCHAR(45) NOT NULL,
  ip_start_num NUMERIC(39, 0) NOT NULL,
  ip_end_num NUMERIC(39, 0) NOT NULL,
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
  usage_flags VARCHAR(150),
);

CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_range_ult_num ON public.qqzeng_ip_range_ult (ip_start_num, ip_end_num);

-- 导入 CSV 示例:
-- COPY public.qqzeng_ip_range_ult(ip_start, ip_end, ip_start_num, ip_end_num, continent, country_code, country, province, city, district, geo_id, longitude, latitude, timezone, isp, asn, as_name, as_domain, usage_flags)
-- FROM '/path/to/qqzeng_ip_ult_global_range.csv' WITH (FORMAT csv, HEADER true, DELIMITER ',');


-- ------------------------------------------
-- 至尊版 (Max Edition) - Range
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS public.qqzeng_ip_range_max (
  ip_start VARCHAR(45) NOT NULL,
  ip_end VARCHAR(45) NOT NULL,
  ip_start_num NUMERIC(39, 0) NOT NULL,
  ip_end_num NUMERIC(39, 0) NOT NULL,
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
  usage_flags VARCHAR(150),
);

CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_range_max_num ON public.qqzeng_ip_range_max (ip_start_num, ip_end_num);

-- 导入 CSV 示例:
-- COPY public.qqzeng_ip_range_max(ip_start, ip_end, ip_start_num, ip_end_num, continent, continent_en, country_code, country_alpha3, country, country_en, province, province_en, city, city_en, district, district_en, geo_id, longitude, latitude, timezone, languages, currency_code, phone_prefix, emoji_flag, isp, asn, as_name, as_domain, usage_flags)
-- FROM '/path/to/qqzeng_ip_max_global_range.csv' WITH (FORMAT csv, HEADER true, DELIMITER ',');


-- ------------------------------------------
-- Range 数值区间匹配查询最佳实践
-- ------------------------------------------
-- SELECT * FROM public.qqzeng_ip_range_max WHERE 1920119058 >= ip_start_num AND 1920119058 <= ip_end_num LIMIT 1;