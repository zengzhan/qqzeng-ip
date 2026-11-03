# QZDB 发行清单与客户端安全更新回滚规范 (RELEASE_CHECKLIST.md)

> **适用场景**：面向企业客户与订阅用户的每月离线包交付（邮件推送 / 专属下载站 / 内部自动化分发）。
> **核心原则**：不信任未经校验的下载包；先验后切；历史双版本就地保留；支持秒级回滚。

---

## 一、版本发行清单规范 (Release Manifest)

每期月度发布物（每月 1 日发行）必须随包附带一份 `release-manifest.json` 与校验清单文本 `SHA256SUMS.txt`。

### 1. 清单 JSON 模板 (`release-manifest.json`)

```json
{
  "edition": "2026-11",
  "release_date": "2026-11-01T00:00:00+08:00",
  "generator_version": "MergeEngine v3.2.0 (Post-order DFS Dedup)",
  "schema_version": "QZDB-v1.0",
  "files": [
    {
      "name": "qqzeng_ip_ult_global.qzdb",
      "edition_code": "ult",
      "region": "global",
      "description": "至尊版全球全要素 IP 库 (25 字段)",
      "size_bytes": 69206016,
      "sha256": "3a5b6c7d8e9f0123456789abcdef0123456789abcdef0123456789abcdef01",
      "total_cidrs": 11920000,
      "ipv4_count": 2360000,
      "ipv6_count": 9560000
    },
    {
      "name": "qqzeng_ip_ult_global.csv",
      "edition_code": "ult",
      "region": "global",
      "description": "至尊版全球全要素 CSV (CIDR 格式)",
      "size_bytes": 485123990,
      "sha256": "4b6c7d8e9f0123456789abcdef0123456789abcdef0123456789abcdef0123"
    }
  ],
  "quality_gate": {
    "ipv4_space_conservation": "4294967296/4294967296 (100.0%)",
    "district_coverage_rate": "42.8%",
    "composite_isp_count": 138520,
    "oracle_probes_tested": 11650000,
    "mismatch_count": 0
  },
  "changelog_summary": [
    "全面同步 2026-11 期 APNIC/RIPE/ARIN 官方最新已委派分配前缀",
    "修正二级市场云巨头资产并购流转网段 1,240 条",
    "补充三大运营商新启用 5G 移动蜂窝基站区段"
  ]
}
```

---

## 二、客户端安全更新与校验规程 (Client Verification)

企业客户或运维人员在接收到新包后，必须遵循以下“四步安全更新法”：

### 步骤 1：完整性校验（防传输损坏与劫持）
在解压或覆盖前，强制对下载文件执行哈希一致性核验：
```bash
# Linux / macOS
sha256sum -c SHA256SUMS.txt
# 或单独校验：
shasum -a 256 qqzeng_ip_ult_global.qzdb
```
若计算所得 Hash 与 Manifest 记录不一致，**严禁加载**，立即联系技术支持重新获取。

### 步骤 2：就地多版本保留 (N-1 回滚备份)
严禁直接覆写生产文件。推荐维护两代物理文件及软链接（或配置指向）：
```bash
/data/qqzeng/
  ├── qqzeng_ip_ult_global_202610.qzdb   # 上期旧版 (N-1)
  ├── qqzeng_ip_ult_global_202611.qzdb   # 当期新版 (N)
  └── current.qzdb -> qqzeng_ip_ult_global_202611.qzdb  # 软链接
```
在不便使用软链接的 Windows / 容器环境中，备份为：
```bash
cp current.qzdb current.qzdb.bak
cp qqzeng_ip_ult_global_202611.qzdb current.qzdb
```

### 步骤 3：预检与平滑重载 (Reload)
1. **SDK 内存映射实例重载**：使用新文件初始化新 Searcher 实例，进行至少 1 条固定探针验证（如 `8.8.8.8` 或 `114.114.114.114`）；
2. **指针/引用原子切换**：业务服务将查询句柄原子切换至新实例，随后释放旧实例；
3. **HTTP 演示服务**：若使用 `netcoreip` 演示站点，直接下发 Admin 重载信号：
   ```bash
   curl -H "X-Access-Token: <ADMIN_TOKEN>" "http://127.0.0.1:8080/api/admin/OptReloadDat?type=ip"
   ```

---

## 三、快速回滚应急方案 (Rollback Procedure)

如若新版上线后发生业务异常（如特殊内部私网判定不符合预期或系统突发告警），执行 30 秒紧急回滚：

1. **软链接回拨**：
   ```bash
   ln -snf /data/qqzeng/qqzeng_ip_ult_global_202610.qzdb /data/qqzeng/current.qzdb
   ```
2. **实例就地回退**：
   ```bash
   cp /data/qqzeng/current.qzdb.bak /data/qqzeng/current.qzdb
   ```
3. **重新加载引擎**：调用 SDK Reload 或重启服务，恢复上一期生产数据。
