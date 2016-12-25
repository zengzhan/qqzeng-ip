--mysql 导入数据库 

--创建表 最新手机号段归属地数据库

--字段：前三位 号段 省份 城市 运营商  邮编 区号 行政区划编码 类型

CREATE TABLE `phone``.`phone` (
`pref`  VARCHAR(45) NULL,
`phone` VARCHAR(45) NULL,
`province` VARCHAR(45) NULL,
`city` VARCHAR(45) NULL,
`isp` VARCHAR(45) NULL,
`post_code` VARCHAR(45) NULL,
`city_code` VARCHAR(45) NULL,
`area_code` VARCHAR(45) NULL,
`types` VARCHAR(45) NULL
);


--导入数据库
LOAD DATA LOCAL INFILE 'G:\phone.txt'
INTO TABLE phone
FIELDS TERMINATED BY '\t'
LINES TERMINATED BY '\n'
(pref, phone, province,city,isp,post_code,city_code,area_code,types);



--默认 txt为中文编码  导入时 请转为utf-8编码 以免乱码 

--mysql乱码如何解决？


--解决：

--确保两者编码统一 才不会乱码

--（1）先将txt文件转换为UTF-8格式 

--（2）导入命令中加入character set utf8

--如：

…… into table test character set utf8 fields……



