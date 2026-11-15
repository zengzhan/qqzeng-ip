-- ==============================================================================
-- qqzeng-phone 手机号段归属地数据库 - PostgreSQL 部署脚本
-- 官方网站: https://www.qqzeng.com
-- 覆盖 51.9万+ 号段，包含 8 大核心维度字段
-- ==============================================================================

DROP TABLE IF EXISTS phone_location;
CREATE TABLE phone_location (
    prefix      VARCHAR(10) NOT NULL,   -- 前三位手机号段 (如 188)
    phone       VARCHAR(10) PRIMARY KEY, -- 7位完整手机号段 (如 1886999)
    province    VARCHAR(50),            -- 省份
    city        VARCHAR(50),            -- 城市
    isp         VARCHAR(50),            -- 基础运营商 (中国移动/联通/电信/广电)
    post_code   VARCHAR(10),            -- 邮政编码
    city_code   VARCHAR(10),            -- 电话长途区号
    area_code   VARCHAR(20)             -- 行政区划代码 (6位)
);

CREATE INDEX idx_phone_pref ON phone_location (prefix);
CREATE INDEX idx_phone_city ON phone_location (city);
CREATE INDEX idx_phone_isp ON phone_location (isp);

-- 导入 CSV (推荐，逗号分隔，首行表头)
-- COPY phone_location (prefix, phone, province, city, isp, post_code, city_code, area_code)
-- FROM '/path/to/phone.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8');

-- 导入 TXT (制表符分隔)
-- COPY phone_location (prefix, phone, province, city, isp, post_code, city_code, area_code)
-- FROM '/path/to/phone.txt' WITH (FORMAT text, DELIMITER E'\t', ENCODING 'UTF8');

-- 高频业务查询示例：查询 11 位手机号码归属地 (前7位前缀匹配)
-- SELECT * FROM phone_location WHERE phone = LEFT('18869991234', 7);
