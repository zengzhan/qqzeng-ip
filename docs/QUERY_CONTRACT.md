# QZDB 查询契约与字段规范 (QUERY_CONTRACT.md)

> **核心原则**：
> 1. **数据字段纯净**：数据字段（大洲、国家、省市区县、运营商、ASN 等）无值或无法下探时，**一律输出空字符串 `""`**，**严禁返回 `"unknown"`、`"null"` 或 `"None"`**；
> 2. **元数据状态语义解耦**：细分判别语义（如仅到省、发生冲突、数据集过期等）通过独立的顶层元数据字段 `status` 表达，绝不污染业务数据字段本身；
> 3. **最长前缀匹配**：所有 IPv4 / IPv6 检索遵循无符号 128-bit PATRICIA Trie 最长前缀匹配。

---

## 一、查询状态枚举 (Query Status)

SDK 统一返回对象或 API 响应体中的 `status` 状态枚举定义如下：

| 状态码 (`status`) | 状态语义说明 | 对应数据字段表现 | 适用业务场景 |
|:---|:---|:---|:---|
| `ok` | 正常命中精细数据 | 各字段返回真实值（无值字段为 `""`） | 成功解析出城市/运营商等 |
| `region_only` | 仅下探至大洲/国家/省份 | `country`/`province` 有值，`city`/`district` 为 `""` | 国家级骨干网、省级专线等不可下探网段 |
| `not_found` | 未命中任何记录 | 全部数据字段均为 `""` | 彻底未分配或哨兵网段 |
| `conflict` | 多源数据在同网段存在争议 | 依维度层表既定优先级裁决，争议维度输出胜出值 | 多源交叉回填争议网段 |
| `special_purpose` | 命中 IANA 保留/私网/环回地址 | `country` 标“局域网”或“保留地址”，其余置 `""` | `10.0.0.0/8`, `127.0.0.1`, `::1` 等 |
| `stale_dataset` | 数据集版本陈旧 | 返回数据正常，附带更新建议提示 | 运行环境载入的库版本较旧时的状态预警 |
| `parse_error` | 输入 IP 文本格式非法 | 全部数据字段均为 `""` | 无法通过 IPv4/IPv6 正则 |

> [!NOTE]
> 数据字段永远不会出现 `"unknown"`。业务层判断是否存在地级市，只需直接执行 `if (res.city != "")`。

---

## 二、五大发行版本统一字段定义

各版本按固定字段顺序解包，所有字段类型均为 UTF-8 字符串（`string`）。

### 1. 标准版 (STD · 6 维)
```
1. continent     (大洲中文)
2. country_code  (ISO 3166-1 alpha-2 代码，如 CN, US)
3. country       (国家中文，如 中国, 美国)
4. province      (省份/州中文，如 广东, 加利福尼亚州)
5. city          (地级市中文，如 深圳；无法下探时为空字符串 "")
6. isp           (网络运营商，如 中国电信, 阿里云)
```

### 2. ASN 路由版 (ASN · 8 维)
```
1. continent     (大洲中文)
2. country_code  (ISO 3166-1 alpha-2 代码)
3. country       (国家中文)
4. isp           (运营商名称)
5. asn           (自治域 ASN 纯数字字符串，如 "4134"；无 ASN 时为 "")
6. as_name       (AS 注册英文全称)
7. as_domain     (AS 所属机构主域名)
8. usage_type    (业务场景类型，如 Broadband, Backbone, Cloud, DataCenter)
```

### 3. 专业版 (PRO · 11 维)
```
1. continent     (大洲中文)
2. country_code  (ISO 3166-1 alpha-2 代码)
3. country       (国家中文)
4. province      (省份/州中文)
5. city          (地级市中文)
6. district      (区县中文，如 南山区；无区县时为 "")
7. geo_id        (行政区划代码，如 "440305")
8. longitude     (经度字符串，如 "113.93041")
9. latitude      (纬度字符串，如 "22.53303")
10. timezone     (IANA 标准时区，如 "Asia/Shanghai")
11. isp          (运营商名称)
```

### 4. 旗舰版 (MAX · 15 维)
```
1. continent     (大洲中文)
2. country_code  (ISO 3166-1 alpha-2 代码)
3. country       (国家中文)
4. province      (省份/州中文)
5. city          (地级市中文)
6. district      (区县中文)
7. geo_id        (行政区划代码)
8. longitude     (经度字符串)
9. latitude      (纬度字符串)
10. timezone     (时区字符串)
11. isp          (运营商名称)
12. asn          (ASN 编号字符串)
13. as_name      (AS 英文名称)
14. as_domain    (AS 域名)
15. usage_type   (业务场景)
```

### 5. 至尊版 (ULT · 25 维全要素)
```
1. continent        (大洲中文，如 亚洲)
2. continent_en     (大洲英文，如 Asia)
3. country_code     (ISO 3166-1 alpha-2，如 CN)
4. country_alpha3   (ISO 3166-1 alpha-3，如 CHN)
5. country          (国家中文，如 中国)
6. country_en       (国家英文，如 China)
7. province         (省份中文，如 浙江省)
8. province_en      (省份英文，如 Zhejiang)
9. city             (城市中文，如 杭州市)
10. city_en         (城市英文，如 Hangzhou)
11. district        (区县中文，如 西湖区)
12. district_en     (区县英文，如 Xihu District)
13. geo_id          (行政区划代码，如 330106)
14. longitude       (经度，如 120.13020)
15. latitude        (纬度，如 30.25949)
16. timezone        (时区，如 Asia/Shanghai)
17. languages       (官方/通用语言，如 zh-CN)
18. currency_code   (法定货币代号，如 CNY)
19. phone_prefix    (国际电话区号，如 86)
20. emoji_flag      (国旗 Emoji，如 🇨🇳)
21. isp             (运营商/服务商，如 阿里云)
22. asn             (自治域编号，如 37963)
23. as_name         (AS 英文名，如 ALIBABA-CN-NET)
24. as_domain       (AS 机构域名，如 alibaba.com)
25. usage_type      (网络使用场景，如 Cloud)
```

---

## 三、实盘网络应用场景分类 (usage_type)

与核心生产库（`ip_master_v4` 与 `ip_master_v6`）完全对齐的 18 类场景枚举（空值统一为 `""`）：

- `Broadband`（固定民用宽带 / 家庭网络接入）
- `Business`（企业商用专线 / 商业机构接入）
- `Backbone`（国家级/运营商骨干网核心承载链路）
- `Cloud`（主流公有云主机，如 阿里云、腾讯云、AWS、华为云等）
- `DataCenter`（通用 IDC 数据中心 / 托管机房）
- `Education`（高校及科研教育网 CERNET / 实验室）
- `VPN`（企业或商业网络代理 / 隧道出口）
- `Mobile`（蜂窝移动网络 4G/5G/IoT 接入）
- `CDN`（内容分发网络边缘加速节点）
- `Finance`（银行及金融机构专网专线）
- `Government`（政府部门 / 关键公共机构）
- `Satellite`（卫星通信 / 星链天基网络）
- `DNS`（权威及公共递归 DNS 节点，如 114.114.114.114 / 8.8.8.8）
- `Spider`（搜索引擎合规爬虫抓取节点）
- `Streaming`（流媒体服务专用加速带宽）
- `IoT`（窄带物联网 / 智能设备互联段）
- `AICrawler`（AI 训练与大模型检索爬虫）
- `Reserved`（特定实验与协议保留网络）

---

## 四、输入解析与边界规范

1. **IPv4 格式**：标准点分十进制（如 `1.1.1.1`）；
2. **IPv6 格式**：RFC 5952 规范化 IPv6 文本（如 `240e::1`）；
3. **空值与干净消费**：字段无确切数据时一律为 `""`，免除上层二次清洗。
