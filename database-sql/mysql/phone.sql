-- ==============================================================================
-- qqzeng-phone 手机号段归属地数据库 - MySQL 部署脚本
-- 官方网站: https://www.qqzeng.com
-- 覆盖 51.9万+ 号段，包含 8 大核心维度字段
-- ==============================================================================

SET NAMES utf8mb4;

DROP TABLE IF EXISTS `phone_location`;
CREATE TABLE `phone_location` (
  `prefix`    varchar(10) NOT NULL COMMENT '前三位手机号段 (如 188)',
  `phone`     varchar(10) NOT NULL COMMENT '7位完整手机号段 (如 1886999)',
  `province`  varchar(50) DEFAULT '' COMMENT '所属省份',
  `city`      varchar(50) DEFAULT '' COMMENT '所属城市',
  `isp`       varchar(50) DEFAULT '' COMMENT '基础运营商 (中国移动/中国联通/中国电信/中国广电)',
  `post_code` varchar(10) DEFAULT '' COMMENT '邮政编码',
  `city_code` varchar(10) DEFAULT '' COMMENT '电话长途区号',
  `area_code` varchar(20) DEFAULT '' COMMENT '行政区划代码 (6位)',
  PRIMARY KEY (`phone`),
  KEY `idx_prefix` (`prefix`),
  KEY `idx_city` (`city`),
  KEY `idx_isp` (`isp`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='全国手机号段归属地数据库';

-- 导入 CSV (推荐，逗号分隔，跳过首行表头)
-- LOAD DATA LOCAL INFILE '/path/to/phone.csv'
-- INTO TABLE `phone_location`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '"'
-- LINES TERMINATED BY '\n' -- Windows 文件请改为 '\r\n'
-- IGNORE 1 LINES
-- (`prefix`, `phone`, `province`, `city`, `isp`, `post_code`, `city_code`, `area_code`);

-- 导入 TXT (制表符分隔)
-- LOAD DATA LOCAL INFILE '/path/to/phone.txt'
-- INTO TABLE `phone_location`
-- CHARACTER SET utf8mb4
-- FIELDS TERMINATED BY '\t'
-- LINES TERMINATED BY '\n'
-- IGNORE 1 LINES
-- (`prefix`, `phone`, `province`, `city`, `isp`, `post_code`, `city_code`, `area_code`);

-- 查询示例：11 位手机号码归属地查询 (前7位前缀匹配)
-- SELECT * FROM `phone_location` WHERE `phone` = LEFT('18869991234', 7);
