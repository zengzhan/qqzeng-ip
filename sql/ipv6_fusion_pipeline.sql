-- ============================================================
-- IPv6 数据融合流水线 v2.3 (标准地区映射版)
-- 多数据源融合填充 qqzeng_ip_master IPv6 空白
-- Phase 1: 导入辅助表
-- Phase 2: ASN 填充（dbip-asn + geolite2-asn） (使用 MAX(start_ip) 排除 gap 性能黑洞)
-- Phase 3: 地理标准化与填充（dbip-city → 关联 geo_info 统一接管地区信息）
-- Phase 4: ISP 填充（ASN → qqzeng_asn）
-- Phase 5: 云厂商精准打标
-- ============================================================
\timing on

-- PHASE 1: 导入 dbip-asn-ipv6
DROP TABLE IF EXISTS stage_dbip_asn_ipv6;
CREATE UNLOGGED TABLE stage_dbip_asn_ipv6 (
    start_ip inet NOT NULL,
    end_ip   inet NOT NULL,
    asn      integer,
    as_name  text
);
COPY stage_dbip_asn_ipv6(start_ip, end_ip, asn, as_name)
FROM '/Users/zengxiangzhan/ZengData/IP数据库/data/dbip-asn-ipv6.csv'
WITH (FORMAT CSV, DELIMITER ',', QUOTE '"');
CREATE INDEX idx_dbip_asn6 ON stage_dbip_asn_ipv6(start_ip, end_ip);
ANALYZE stage_dbip_asn_ipv6;
SELECT COUNT(*) as dbip_asn_ipv6_rows FROM stage_dbip_asn_ipv6;

-- PHASE 1b: 导入 geolite2-asn-ipv6
DROP TABLE IF EXISTS stage_geolite2_asn_ipv6;
CREATE UNLOGGED TABLE stage_geolite2_asn_ipv6 (
    start_ip inet NOT NULL,
    end_ip   inet NOT NULL,
    asn      integer,
    as_name  text
);
COPY stage_geolite2_asn_ipv6(start_ip, end_ip, asn, as_name)
FROM '/Users/zengxiangzhan/ZengData/IP数据库/data/geolite2-asn-ipv6.csv'
WITH (FORMAT CSV, DELIMITER ',', QUOTE '"');
CREATE INDEX idx_geo2_asn6 ON stage_geolite2_asn_ipv6(start_ip, end_ip);
ANALYZE stage_geolite2_asn_ipv6;
SELECT COUNT(*) as geolite2_asn_ipv6_rows FROM stage_geolite2_asn_ipv6;

-- PHASE 1c: 导入 dbip-city-ipv6（439万行）
DROP TABLE IF EXISTS stage_dbip_city_ipv6;
CREATE UNLOGGED TABLE stage_dbip_city_ipv6 (
    start_ip     inet NOT NULL,
    end_ip       inet NOT NULL,
    country_code text,
    state        text,
    col5         text,
    city         text,
    col7         text,
    lat          double precision,
    lon          double precision,
    col10        text
);
COPY stage_dbip_city_ipv6
FROM PROGRAM 'gunzip -c /Users/zengxiangzhan/ZengData/IP数据库/data/dbip-city-ipv6.csv.gz'
WITH (FORMAT CSV, DELIMITER ',', QUOTE '"');
CREATE INDEX idx_dbip_city6 ON stage_dbip_city_ipv6(start_ip, end_ip);
ANALYZE stage_dbip_city_ipv6;
SELECT COUNT(*) as dbip_city_ipv6_rows FROM stage_dbip_city_ipv6;

-- 创建 functional 索引优化 qqzeng_ip_master lookup 性能
CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_master_net6 ON qqzeng_ip_master (network(cidr)) WHERE ip_version = 6;
ANALYZE qqzeng_ip_master;

-- 基线检查
SELECT 'BASELINE',
  COUNT(*) total_ipv6,
  SUM(CASE WHEN country IS NULL OR country='' THEN 1 ELSE 0 END) no_country,
  SUM(CASE WHEN province IS NULL OR province='' THEN 1 ELSE 0 END) no_province,
  SUM(CASE WHEN city IS NULL OR city='' THEN 1 ELSE 0 END) no_city,
  SUM(CASE WHEN isp IS NULL OR isp='' THEN 1 ELSE 0 END) no_isp,
  SUM(CASE WHEN asn IS NULL OR asn=0 THEN 1 ELSE 0 END) no_asn
FROM qqzeng_ip_master WHERE ip_version=6;

-- PHASE 2a: 用 dbip-asn 填充缺失 ASN (使用 MAX(start_ip) 排除 gap 扫描黑洞)
UPDATE qqzeng_ip_master m
SET asn = sub.asn
FROM (
    SELECT m.cidr, src.asn
    FROM qqzeng_ip_master m
    CROSS JOIN LATERAL (
        SELECT asn FROM stage_dbip_asn_ipv6 src
        WHERE src.start_ip = (
            SELECT MAX(start_ip) FROM stage_dbip_asn_ipv6
            WHERE start_ip <= network(m.cidr)
        )
        AND network(m.cidr) <= src.end_ip
    ) src
    WHERE m.ip_version = 6
      AND (m.asn IS NULL OR m.asn = 0)
) sub
WHERE m.cidr = sub.cidr;
SELECT 'after dbip-asn6:', SUM(CASE WHEN asn IS NULL OR asn=0 THEN 1 ELSE 0 END) still_no_asn FROM qqzeng_ip_master WHERE ip_version=6;

-- PHASE 2b: geolite2-asn 补充 (使用 MAX(start_ip) 排除 gap 扫描黑洞)
UPDATE qqzeng_ip_master m
SET asn = sub.asn
FROM (
    SELECT m.cidr, src.asn
    FROM qqzeng_ip_master m
    CROSS JOIN LATERAL (
        SELECT asn FROM stage_geolite2_asn_ipv6 src
        WHERE src.start_ip = (
            SELECT MAX(start_ip) FROM stage_geolite2_asn_ipv6
            WHERE start_ip <= network(m.cidr)
        )
        AND network(m.cidr) <= src.end_ip
    ) src
    WHERE m.ip_version = 6
      AND (m.asn IS NULL OR m.asn = 0)
) sub
WHERE m.cidr = sub.cidr;
SELECT 'after geolite2-asn6:', SUM(CASE WHEN asn IS NULL OR asn=0 THEN 1 ELSE 0 END) still_no_asn FROM qqzeng_ip_master WHERE ip_version=6;


-- ============================================================
-- PHASE 3: 地理填充与中文化标准化（接管并映射到 geo_info 词典）
-- ============================================================

-- 3a: 生成位置到 geo_info.geoname_id 的映射表 (基于英文拼写 ILIKE 匹配)
CREATE TEMP TABLE tmp_dbip_geo_map AS
WITH city_uniq AS (
    SELECT DISTINCT country_code, state, city 
    FROM stage_dbip_city_ipv6
)
SELECT 
    c.country_code, c.state, c.city,
    COALESCE(
        -- 优先级 1: 匹配 城市 + 省份 + 国家
        (SELECT g.geoname_id FROM geo_info g 
         WHERE g.country_code = c.country_code 
           AND g.province_en ILIKE c.state 
           AND g.city_en ILIKE c.city 
           AND c.city IS NOT NULL AND c.city != ''
         LIMIT 1),
        -- 优先级 2: 匹配 省份 + 国家 (城市为空)
        (SELECT g.geoname_id FROM geo_info g 
         WHERE g.country_code = c.country_code 
           AND g.province_en ILIKE c.state 
           AND (g.city_en = '' OR g.city_en IS NULL)
           AND c.state IS NOT NULL AND c.state != ''
         LIMIT 1),
        -- 优先级 3: 匹配 国家代码 (省市均为空)
        (SELECT g.geoname_id FROM geo_info g 
         WHERE g.country_code = c.country_code 
           AND (g.province_en = '' OR g.province_en IS NULL)
           AND (g.city_en = '' OR g.city_en IS NULL)
         LIMIT 1)
    ) as geoname_id
FROM city_uniq c;
CREATE UNIQUE INDEX idx_tmp_geo_map ON tmp_dbip_geo_map(country_code, state, city);
ANALYZE tmp_dbip_geo_map;

-- 3b: 基于标准化 geoname_id 统一填充 qqzeng_ip_master 关联数据
-- 同步更新 geo_id, country, province, city, district (均来自 geo_info 权威标准化值)
UPDATE qqzeng_ip_master m
SET geo_id = sub.geoname_id,
    country = sub.country,
    province = sub.province,
    city = sub.city,
    district = sub.district
FROM (
    SELECT m.cidr,
           g.geoname_id,
           g.country,
           g.province,
           g.city,
           g.district
    FROM qqzeng_ip_master m
    CROSS JOIN LATERAL (
        SELECT country_code, state, city FROM stage_dbip_city_ipv6 city
        WHERE city.start_ip = (
            SELECT MAX(start_ip) FROM stage_dbip_city_ipv6
            WHERE start_ip <= network(m.cidr)
        )
        AND network(m.cidr) <= city.end_ip
    ) city
    JOIN tmp_dbip_geo_map map 
      ON map.country_code = city.country_code 
     AND map.state = city.state 
     AND map.city = city.city
    JOIN geo_info g ON g.geoname_id = map.geoname_id
    WHERE m.ip_version = 6
      AND (m.country IS NULL OR m.country = '' OR m.province IS NULL OR m.province = '' OR m.city IS NULL OR m.city = '')
) sub
WHERE m.cidr = sub.cidr;

SELECT 'after optimized city fill:',
  SUM(CASE WHEN country IS NULL OR country='' THEN 1 ELSE 0 END) no_country,
  SUM(CASE WHEN province IS NULL OR province='' THEN 1 ELSE 0 END) no_province,
  SUM(CASE WHEN city IS NULL OR city='' THEN 1 ELSE 0 END) no_city
FROM qqzeng_ip_master WHERE ip_version=6;


-- PHASE 4: ISP 填充（ASN → qqzeng_asn.isp）
UPDATE qqzeng_ip_master m
SET isp = a.isp
FROM qqzeng_asn a
WHERE m.ip_version = 6
  AND (m.isp IS NULL OR m.isp = '')
  AND m.asn IS NOT NULL AND m.asn > 0
  AND a.asn_num = m.asn
  AND a.isp IS NOT NULL AND a.isp != '';
SELECT 'after ASN→isp:', SUM(CASE WHEN isp IS NULL OR isp='' THEN 1 ELSE 0 END) no_isp FROM qqzeng_ip_master WHERE ip_version=6;

-- PHASE 5: 云厂商精准 ISP 打标
DROP TABLE IF EXISTS tmp_cloud_isp6;
CREATE TEMP TABLE tmp_cloud_isp6 (cidr inet, isp_name text);

\copy tmp_cloud_isp6(cidr) FROM '/Users/zengxiangzhan/ZengData/IP数据库/data/cloud_isp/aliyun-ipv6.txt'
UPDATE tmp_cloud_isp6 SET isp_name='阿里云' WHERE isp_name IS NULL;
\copy tmp_cloud_isp6(cidr) FROM '/Users/zengxiangzhan/ZengData/IP数据库/data/cloud_isp/tencent-ipv6.txt'
UPDATE tmp_cloud_isp6 SET isp_name='腾讯云' WHERE isp_name IS NULL;
\copy tmp_cloud_isp6(cidr) FROM '/Users/zengxiangzhan/ZengData/IP数据库/data/cloud_isp/huawei-ipv6.txt'
UPDATE tmp_cloud_isp6 SET isp_name='华为云' WHERE isp_name IS NULL;
\copy tmp_cloud_isp6(cidr) FROM '/Users/zengxiangzhan/ZengData/IP数据库/data/cloud_isp/baidu-ipv6.txt'
UPDATE tmp_cloud_isp6 SET isp_name='百度云' WHERE isp_name IS NULL;
\copy tmp_cloud_isp6(cidr) FROM '/Users/zengxiangzhan/ZengData/IP数据库/data/cloud_isp/ksyun-ipv6.txt'
UPDATE tmp_cloud_isp6 SET isp_name='金山云' WHERE isp_name IS NULL;

UPDATE qqzeng_ip_master m
SET isp = c.isp_name
FROM tmp_cloud_isp6 c
WHERE m.ip_version = 6
  AND m.cidr <<= c.cidr
  AND c.isp_name IS NOT NULL;

SELECT '云厂商打标完成', isp, COUNT(*)
FROM qqzeng_ip_master
WHERE ip_version=6 AND isp IN ('阿里云','腾讯云','华为云','百度云','金山云')
GROUP BY isp ORDER BY 3 DESC;

-- 最终完整度报告
SELECT
  '最终统计' label,
  COUNT(*) total_ipv6,
  ROUND(100.0*SUM(CASE WHEN country  !='' AND country  IS NOT NULL THEN 1 ELSE 0 END)/COUNT(*),2) country_pct,
  ROUND(100.0*SUM(CASE WHEN province !='' AND province IS NOT NULL THEN 1 ELSE 0 END)/COUNT(*),2) province_pct,
  ROUND(100.0*SUM(CASE WHEN city     !='' AND city     IS NOT NULL THEN 1 ELSE 0 END)/COUNT(*),2) city_pct,
  ROUND(100.0*SUM(CASE WHEN isp      !='' AND isp      IS NOT NULL THEN 1 ELSE 0 END)/COUNT(*),2) isp_pct,
  ROUND(100.0*SUM(CASE WHEN asn      > 0  AND asn      IS NOT NULL THEN 1 ELSE 0 END)/COUNT(*),2) asn_pct
FROM qqzeng_ip_master WHERE ip_version=6;
