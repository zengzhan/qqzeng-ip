-- ==========================================
-- MySQL - QZDB CIDR 格式 IP 数据库导入与检索脚本
-- 支持版本：标准版(std) / 专业版(pro) / ASN版(asn) / 旗舰版(ult) / 至尊版(max)
-- ==========================================

-- ------------------------------------------
-- 标准版 (Standard Edition) - 6 维度
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS `qqzeng_ip_std` (
  `cidr` VARCHAR(50) NOT NULL,
  `continent` VARCHAR(150) NULL,
  `country_code` VARCHAR(150) NULL,
  `country` VARCHAR(150) NULL,
  `province` VARCHAR(150) NULL,
  `city` VARCHAR(150) NULL,
  `isp` VARCHAR(150) NULL,
  PRIMARY KEY (`cidr`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 导入 CSV 示例:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_ip_std_global.csv'
-- INTO TABLE `qqzeng_ip_std`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`cidr`, `continent`, `country_code`, `country`, `province`, `city`, `isp`);


-- ------------------------------------------
-- 专业版 (Professional Edition) - 11 维度
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS `qqzeng_ip_pro` (
  `cidr` VARCHAR(50) NOT NULL,
  `continent` VARCHAR(150) NULL,
  `country_code` VARCHAR(150) NULL,
  `country` VARCHAR(150) NULL,
  `province` VARCHAR(150) NULL,
  `city` VARCHAR(150) NULL,
  `district` VARCHAR(150) NULL,
  `geo_id` VARCHAR(150) NULL,
  `longitude` VARCHAR(150) NULL,
  `latitude` VARCHAR(150) NULL,
  `timezone` VARCHAR(150) NULL,
  `isp` VARCHAR(150) NULL,
  PRIMARY KEY (`cidr`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 导入 CSV 示例:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_ip_pro_global.csv'
-- INTO TABLE `qqzeng_ip_pro`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`cidr`, `continent`, `country_code`, `country`, `province`, `city`, `district`, `geo_id`, `longitude`, `latitude`, `timezone`, `isp`);


-- ------------------------------------------
-- ASN 路由版 (ASN Edition) - 8 维度
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS `qqzeng_ip_asn` (
  `cidr` VARCHAR(50) NOT NULL,
  `continent` VARCHAR(150) NULL,
  `country_code` VARCHAR(150) NULL,
  `country` VARCHAR(150) NULL,
  `isp` VARCHAR(150) NULL,
  `asn` VARCHAR(150) NULL,
  `as_name` VARCHAR(150) NULL,
  `as_domain` VARCHAR(150) NULL,
  `usage_type` VARCHAR(50) NULL,
  PRIMARY KEY (`cidr`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 导入 CSV 示例:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_ip_asn_global.csv'
-- INTO TABLE `qqzeng_ip_asn`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`cidr`, `continent`, `country_code`, `country`, `isp`, `asn`, `as_name`, `as_domain`, `usage_type`);


-- ------------------------------------------
-- 旗舰版 (Flagship Edition) - 15 维度
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS `qqzeng_ip_max` (
  `cidr` VARCHAR(50) NOT NULL,
  `continent` VARCHAR(150) NULL,
  `country_code` VARCHAR(150) NULL,
  `country` VARCHAR(150) NULL,
  `province` VARCHAR(150) NULL,
  `city` VARCHAR(150) NULL,
  `district` VARCHAR(150) NULL,
  `geo_id` VARCHAR(150) NULL,
  `longitude` VARCHAR(150) NULL,
  `latitude` VARCHAR(150) NULL,
  `timezone` VARCHAR(150) NULL,
  `isp` VARCHAR(150) NULL,
  `asn` VARCHAR(150) NULL,
  `as_name` VARCHAR(150) NULL,
  `as_domain` VARCHAR(150) NULL,
  `usage_type` VARCHAR(50) NULL,
  PRIMARY KEY (`cidr`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 导入 CSV 示例:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_ip_max_global.csv'
-- INTO TABLE `qqzeng_ip_max`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`cidr`, `continent`, `country_code`, `country`, `province`, `city`, `district`, `geo_id`, `longitude`, `latitude`, `timezone`, `isp`, `asn`, `as_name`, `as_domain`, `usage_type`);


-- ------------------------------------------
-- 至尊版 (Ultimate Edition) - 25 维度
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS `qqzeng_ip_ult` (
  `cidr` VARCHAR(50) NOT NULL,
  `continent` VARCHAR(150) NULL,
  `continent_en` VARCHAR(150) NULL,
  `country_code` VARCHAR(150) NULL,
  `country_alpha3` VARCHAR(150) NULL,
  `country` VARCHAR(150) NULL,
  `country_en` VARCHAR(150) NULL,
  `province` VARCHAR(150) NULL,
  `province_en` VARCHAR(150) NULL,
  `city` VARCHAR(150) NULL,
  `city_en` VARCHAR(150) NULL,
  `district` VARCHAR(150) NULL,
  `district_en` VARCHAR(150) NULL,
  `geo_id` VARCHAR(150) NULL,
  `longitude` VARCHAR(150) NULL,
  `latitude` VARCHAR(150) NULL,
  `timezone` VARCHAR(150) NULL,
  `languages` VARCHAR(150) NULL,
  `currency_code` VARCHAR(150) NULL,
  `phone_prefix` VARCHAR(150) NULL,
  `emoji_flag` VARCHAR(150) NULL,
  `isp` VARCHAR(150) NULL,
  `asn` VARCHAR(150) NULL,
  `as_name` VARCHAR(150) NULL,
  `as_domain` VARCHAR(150) NULL,
  `usage_type` VARCHAR(50) NULL,
  PRIMARY KEY (`cidr`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 导入 CSV 示例:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_ip_ult_global.csv'
-- INTO TABLE `qqzeng_ip_ult`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`cidr`, `continent`, `continent_en`, `country_code`, `country_alpha3`, `country`, `country_en`, `province`, `province_en`, `city`, `city_en`, `district`, `district_en`, `geo_id`, `longitude`, `latitude`, `timezone`, `languages`, `currency_code`, `phone_prefix`, `emoji_flag`, `isp`, `asn`, `as_name`, `as_domain`, `usage_type`);


-- ------------------------------------------
-- MySQL 8.0+ CIDR 匹配查询最佳实践
-- ------------------------------------------
-- 方案 A: 使用 LIKE 通配 (适用极简前缀匹配)
-- SELECT * FROM qqzeng_ip_ult WHERE '114.114.114.114' LIKE CONCAT(SUBSTRING_INDEX(cidr, '/', 1), '%') LIMIT 1;

-- 方案 B: 高并发与超大数据集场景，强烈推荐直接使用 QZDB 极速二进制解析 SDK (纯内存检索无上锁)。