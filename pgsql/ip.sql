--创建表
--字段 cidr 大洲 国家 省份 城市 县区 运营商 区划代码 国家英文名称 国家简码 经度 纬度
CREATE TABLE IF NOT EXISTS public.qqzeng_ip
(
    cidr cidr NOT NULL,
    continent character varying(45),
    country character varying(45),
    province character varying(45),
    city character varying(45),
    district character varying(45),
    isp character varying(45),
    area_code character varying(45),
    country_english character varying(45),
    country_code character varying(45),
    longitude character varying(45),
    latitude character varying(45)
);

--创建索引
CREATE INDEX IF NOT EXISTS idx_qqzeng_ip_cidr
    ON qqzeng_ip USING gist    (cidr inet_ops);


--导出数据库 to txt
COPY qqzeng_ip(cidr, continent, country, province, city, district, isp, area_code, country_english, country_code, longitude, latitude) 
TO 'E:\PostgreSQL\data\ip.txt' WITH (FORMAT text,ENCODING 'UTF8',DELIMITER '|', HEADER true);

--查询ip 
select * from qqzeng_ip where cidr >>= '1.0.0.0'  --单个ip
select * from qqzeng_ip where cidr && '192.167.0.0/16'::cidr; --交集


