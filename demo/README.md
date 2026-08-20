# 演示样本数据

📋 IP 归属地与号段 CSV/TXT 样本数据一览

## qqzeng-ip-ult（ult 完整版 · 25 字段样本）
- `qqzeng-ip-ult.qzdb`：ult 档二进制样本，360 行（220 IPv4 + 140 IPv6）。用各语言 `QzdbReader` 以 **ult** 档加载。
- `qqzeng-ip-ult.csv`：对应 CSV，共 29 列 = `family,start,end,cidr` + 25 个 ult 字段；第 2 行为列名表头，字段值已做 RFC4180 引号转义。
- 调用时注意版本对应：ult 档字段顺序为
  `continent|continent_en|country|country_code|country_en|country_alpha3|province|province_en|city|city_en|district|geo_id|longitude|latitude|timezone|languages|currency_code|phone_prefix|emoji_flag|isp|asn|as_name|as_domain|usage_type`。**不要用 std/asn/pro/max 的字段数去解析**，否则列会错位。

<!-- commit description sync 1787122549 -->

<!-- commit: demo: 📋 IP 归属地及手机号段 CSV/TXT 与 QZDB 演示样本数据 sync=1787246204 -->
