-- ==========================================
-- MySQL - Range 起止IP数值范围格式 IP 数据库导入与检索脚本
-- 支持版本：标准版(std) / 专业版(pro) / ASN版(asn) / 旗舰版(ult) / 至尊版(max)
-- ==========================================

-- ------------------------------------------
-- 标准版 (Standard Edition) - Range
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS `qqzeng_ip_range_std` (
  `ip_start` VARCHAR(45) NOT NULL,
  `ip_end` VARCHAR(45) NOT NULL,
  `ip_start_num` DECIMAL(39, 0) NOT NULL,
  `ip_end_num` DECIMAL(39, 0) NOT NULL,
  `continent` VARCHAR(150) NULL,
  `country_code` VARCHAR(150) NULL,
  `country` VARCHAR(150) NULL,
  `province` VARCHAR(150) NULL,
  `city` VARCHAR(150) NULL,
  `isp` VARCHAR(150) NULL,
  INDEX `idx_qqzeng_ip_range_std_num` (`ip_start_num`, `ip_end_num`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 导入 CSV 示例:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_ip_std_global_range.csv'
-- INTO TABLE `qqzeng_ip_range_std`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`ip_start`, `ip_end`, `ip_start_num`, `ip_end_num`, `continent`, `country_code`, `country`, `province`, `city`, `isp`);


-- ------------------------------------------
-- 专业版 (Professional Edition) - Range
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS `qqzeng_ip_range_pro` (
  `ip_start` VARCHAR(45) NOT NULL,
  `ip_end` VARCHAR(45) NOT NULL,
  `ip_start_num` DECIMAL(39, 0) NOT NULL,
  `ip_end_num` DECIMAL(39, 0) NOT NULL,
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
  INDEX `idx_qqzeng_ip_range_pro_num` (`ip_start_num`, `ip_end_num`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 导入 CSV 示例:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_ip_pro_global_range.csv'
-- INTO TABLE `qqzeng_ip_range_pro`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`ip_start`, `ip_end`, `ip_start_num`, `ip_end_num`, `continent`, `country_code`, `country`, `province`, `city`, `district`, `geo_id`, `longitude`, `latitude`, `timezone`, `isp`);


-- ------------------------------------------
-- ASN 路由版 (ASN Edition) - Range
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS `qqzeng_ip_range_asn` (
  `ip_start` VARCHAR(45) NOT NULL,
  `ip_end` VARCHAR(45) NOT NULL,
  `ip_start_num` DECIMAL(39, 0) NOT NULL,
  `ip_end_num` DECIMAL(39, 0) NOT NULL,
  `continent` VARCHAR(150) NULL,
  `country_code` VARCHAR(150) NULL,
  `country` VARCHAR(150) NULL,
  `isp` VARCHAR(150) NULL,
  `asn` VARCHAR(150) NULL,
  `as_name` VARCHAR(150) NULL,
  `as_domain` VARCHAR(150) NULL,
  `usage_type` VARCHAR(50) NULL,
  INDEX `idx_qqzeng_ip_range_asn_num` (`ip_start_num`, `ip_end_num`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 导入 CSV 示例:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_ip_asn_global_range.csv'
-- INTO TABLE `qqzeng_ip_range_asn`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`ip_start`, `ip_end`, `ip_start_num`, `ip_end_num`, `continent`, `country_code`, `country`, `isp`, `asn`, `as_name`, `as_domain`, `usage_type`);


-- ------------------------------------------
-- 旗舰版 (Ultimate Edition) - Range
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS `qqzeng_ip_range_ult` (
  `ip_start` VARCHAR(45) NOT NULL,
  `ip_end` VARCHAR(45) NOT NULL,
  `ip_start_num` DECIMAL(39, 0) NOT NULL,
  `ip_end_num` DECIMAL(39, 0) NOT NULL,
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
  INDEX `idx_qqzeng_ip_range_ult_num` (`ip_start_num`, `ip_end_num`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 导入 CSV 示例:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_ip_ult_global_range.csv'
-- INTO TABLE `qqzeng_ip_range_ult`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`ip_start`, `ip_end`, `ip_start_num`, `ip_end_num`, `continent`, `country_code`, `country`, `province`, `city`, `district`, `geo_id`, `longitude`, `latitude`, `timezone`, `isp`, `asn`, `as_name`, `as_domain`, `usage_type`);


-- ------------------------------------------
-- 至尊版 (Max Edition) - Range
-- ------------------------------------------
CREATE TABLE IF NOT EXISTS `qqzeng_ip_range_max` (
  `ip_start` VARCHAR(45) NOT NULL,
  `ip_end` VARCHAR(45) NOT NULL,
  `ip_start_num` DECIMAL(39, 0) NOT NULL,
  `ip_end_num` DECIMAL(39, 0) NOT NULL,
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
  INDEX `idx_qqzeng_ip_range_max_num` (`ip_start_num`, `ip_end_num`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 导入 CSV 示例:
-- LOAD DATA LOCAL INFILE '/path/to/qqzeng_ip_max_global_range.csv'
-- INTO TABLE `qqzeng_ip_range_max`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`ip_start`, `ip_end`, `ip_start_num`, `ip_end_num`, `continent`, `continent_en`, `country_code`, `country_alpha3`, `country`, `country_en`, `province`, `province_en`, `city`, `city_en`, `district`, `district_en`, `geo_id`, `longitude`, `latitude`, `timezone`, `languages`, `currency_code`, `phone_prefix`, `emoji_flag`, `isp`, `asn`, `as_name`, `as_domain`, `usage_type`);


-- ------------------------------------------
-- Range 数值区间高效查询最佳实践 (支持 IPv4 / IPv6)
-- ------------------------------------------
-- 假设要查询 IPv4: '114.114.114.114'，对应数值为 1920119058
-- SELECT * FROM qqzeng_ip_range_max
-- WHERE 1920119058 >= ip_start_num AND 1920119058 <= ip_end_num LIMIT 1;