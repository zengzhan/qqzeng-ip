# QZDB 任务型开发者文档大纲与黄金向量规范 (DEVELOPER_GUIDE_OUTLINE.md)

> **目标**：为各语言开发者提供“30秒首次跑通”的极简接入路径，并确立各语言 SDK 输出一致性的对拍基准（黄金向量）。

---

## 一、任务型文档架构大纲 (5 大核心模块)

```
docs/
├── quickstart.md            # 1. 30 秒极速接入指南（全语言最小可运行 Demo）
├── data-schema.md           # 2. 字段字典与数据模型（引用 QUERY_CONTRACT.md）
├── errors.md                # 3. 错误状态码与空值消费准则（空字符串处理）
├── update-and-rollback.md   # 4. 离线更新、安全校验与平滑热重载（引用 RELEASE_CHECKLIST.md）
└── changelog.md             # 5. 月度发版记录与更新摘要
```

### 1. `quickstart.md` (30 秒起步)
- 各语言 1 行包安装命令（`go get`, `npm i`, `pip install`, `cargo add`, `dotnet add`）；
- 5 行开箱即用代码：加载数据文件 → 传入 IP 字符串 → 打印国家、省份、城市、运营商；
- 零外部依赖声明与本地文件加载注意事项。

### 2. `data-schema.md` (数据结构)
- 5 大发行版本字段位置映射表（STD 6 / ASN 8 / PRO 11 / MAX 15 / ULT 25）；
- 字段编码规范（UTF-8 紧凑存储，小端序偏移）。

### 3. `errors.md` (异常处理与空值消费)
- 解析返回结果时的规范消费范式：
  ```go
  // 正确判定方式：检查字符串是否为空
  if result.City != "" {
      // 命中地级市
  }
  ```
- 明确指出返回值绝不会出现 `"unknown"` 或 `"null"`，无需二次去脏。

### 4. `update-and-rollback.md` (运维与集成)
- 守护进程运行期间的多线程只读共享与安全热加载设计模式；
- 配合 Docker / Kubernetes 挂载卷的动态更新实战。

### 5. `changelog.md` (版本演进)
- 遵循 Semantic Versioning 的月度更新摘要。

---

## 二、黄金向量测试集规范 (Golden Vectors)

为确保各语言 SDK（C#、Java、Go、Python、C/C++、Node.js、Rust、PHP）在解析同一份 `.qzdb` 文件时产出 100% 字节级一致的解析结果，构建标准黄金向量集：

### 1. 向量测试点定义 (`golden_vectors.json`)
必须覆盖以下 8 种典型网络场景：

```json
[
  {
    "desc": "国内三大运营商典型宽带 IP (下探至城市与区县)",
    "ip": "114.114.114.114",
    "expected": {
      "country_code": "CN",
      "country": "中国",
      "province": "江苏省",
      "city": "南京市",
      "isp": "中国电信"
    }
  },
  {
    "desc": "公共 DNS / 任播 Anycast 网段",
    "ip": "8.8.8.8",
    "expected": {
      "country_code": "US",
      "country": "美国",
      "isp": "Google",
      "usage_type": "DNS"
    }
  },
  {
    "desc": "IPv6 国内骨干接入网段",
    "ip": "240e:e1:8000::1",
    "expected": {
      "country_code": "CN",
      "country": "中国",
      "province": "广东省",
      "isp": "中国电信"
    }
  },
  {
    "desc": "省级骨干专线 (无法下探地级市，City 必须产出空字符串)",
    "ip": "202.96.0.1",
    "expected": {
      "country_code": "CN",
      "country": "中国",
      "province": "广东省",
      "city": "",
      "district": ""
    }
  },
  {
    "desc": "私有保留地址 (RFC 1918)",
    "ip": "192.168.1.1",
    "expected": {
      "country": "局域网",
      "province": "",
      "city": "",
      "isp": ""
    }
  },
  {
    "desc": "环回地址 (Loopback)",
    "ip": "127.0.0.1",
    "expected": {
      "country": "局域网",
      "province": "",
      "city": "",
      "isp": ""
    }
  },
  {
    "desc": "IPv6 环回地址",
    "ip": "::1",
    "expected": {
      "country": "局域网",
      "province": "",
      "city": "",
      "isp": ""
    }
  }
]
```

### 2. CI 门禁对拍原则
- 每次 SDK 构建发布时，各语言单元测试必须加载 `golden_vectors.json` 逐条对拍；
- 只要任意一语言在任意字段上产生不一致，或者产出了 `"unknown"`/`null`，CI 即刻硬熔断，严禁打包发布。
