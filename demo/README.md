# 演示样本数据

📋 IP 归属地与号段 CSV/TXT 样本数据一览

## qqzeng-ip-ult（ult 完整版 · 25 字段样本）
- `qqzeng-ip-ult.qzdb`：ult 档二进制样本，360 行（220 IPv4 + 140 IPv6）。用各语言 `QzdbReader` 以 **ult** 档加载。
- `qqzeng-ip-ult.csv`：对应 CSV，共 29 列 = `family,start,end,cidr` + 25 个 ult 字段；第 2 行为列名表头，字段值已做 RFC4180 引号转义。
- 调用时注意版本对应：ult 档字段顺序为
  `continent|continent_en|country_code|country_alpha3|country|country_en|province|province_en|city|city_en|district|district_en|geo_id|longitude|latitude|timezone|languages|currency_code|phone_prefix|emoji_flag|isp|asn|as_name|as_domain|usage_type`。**不要用 std/asn/pro/max 的字段数去解析**，否则列会错位。
- **样本文件大小说明（为何 360 行二进制样本约为 4.5MB）**：
  QZDB 引擎采用静态预计算索引架构，二进制文件头部预分配了**固定基础设施开销**，包括：
  1. **前缀跳表索引**：IPv4 16-bit Jump Table（65,536 项固定 256KB）与 IPv6 64K 前缀表（固定 512KB）；
  2. **元数据与结构描述区**：版本号、25 维字段掩码、TLV 元数据与 RowSchema 结构体；
  3. **基础字典常量池**：预构建的全球国家/地区代码、语言代码、时区、货币与 Emoji 国旗常量池（约 3.5MB）。
  在仅包含 360 行抽样数据的 demo 库中，该固定基础设施开销占绝大部分；而在**全量商业库（1,250 万+ 网段）**中，该固定开销被完全分摊，单网段平均存储仅数字节，综合压缩率与检索性能远超传统格式。

<!-- commit: demo: 归属地与号段 CSV/TXT 及 QZDB 演示样本数据 sync=1792769155 -->
