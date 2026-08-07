# 发布指南 (Publishing)

本仓库提供两种「开箱即用」的官方包，用户**无需克隆源码**即可集成：

| 语言 | 包坐标 | 注册中心 | 包 ID |
|------|--------|----------|-------|
| .NET / C# | `QQZeng.Qzdb` 1.0.1 | NuGet (`nuget.org`) | `QQZeng.Qzdb` |
| Java | `com.qqzeng:qzdb` 1.0.0 | Maven Central (`repo1.maven.org`) | `com.qqzeng:qzdb` |

> 包版本独立于 QZDB 数据格式版本（数据格式见 `API_CONTRACT.md`）。
> 版本差异说明：.NET 现为 **1.0.1**（net10.0 单目标）。因 NuGet `1.0.0` 不可变，且初版上传仅含 net8.0，已升版重发（见 §1.4）；Java 仍为未发布的 **1.0.0**。

---

## 1. .NET / NuGet 包（`QQZeng.Qzdb`）

- 工程：`multi-lang/netcore/QQZeng.Qzdb.csproj`（**net10.0 单目标**，LTS）
- 包元数据：`PackageProjectUrl` / `RepositoryUrl` 指向 `https://github.com/zengzhan/qqzeng-ip`

### 1.1 本地打包

```bash
cd multi-lang/netcore
dotnet pack -c Release -o ./nupkgs
# 产出：nupkgs/QQZeng.Qzdb.1.0.1.nupkg  (+ QQZeng.Qzdb.1.0.1.snupkg 符号包)
```

### 1.2 发布到 NuGet

```bash
dotnet nuget push ./nupkgs/QQZeng.Qzdb.1.0.1.nupkg \
  --api-key <NUGET_API_KEY> \
  --source https://api.nuget.org/v3/index.json
```

- 需要 NuGet 账号的 **API Key + 2FA**。
- NuGet 版本**不可变**：`1.0.0`（net8-only 初版）已无法覆盖，当前应发布 **1.0.1**。
- 建议申请 `QQZeng.` **前缀保护**，避免包名被占用。
- 本仓库 `.gitignore` 已排除 `multi-lang/netcore/nupkgs/`，打包产物不入库。

---

## 2. Java / Maven Central 包（`com.qqzeng:qzdb`）

- 工程：`multi-lang/java/pom.xml`（JDK 21，jar）
- groupId `com.qqzeng` 已通过 **qqzeng.com 域名所有权**验证（Maven Central 强制命名空间校验）。
- 发布插件：`central-publishing-maven-plugin`（OSSRH 已于 2026 年退役，统一走 Central Portal）。

### 2.1 发布前置条件（一次性）

1. 在 [central.sonatype.com](https://central.sonatype.com) 注册，验证命名空间 `com.qqzeng`
   （用 qqzeng.com 域名所有权证明：DNS TXT 记录或上传验证文件）。
2. 生成 Central **发布 token**（Settings → Publish Token）。
3. 生成 **GPG 密钥对**，并将**公钥**上传到公钥服务器：
   ```bash
   gpg --gen-key                       # 用与开发者信息一致的邮箱
   gpg --keyserver keyserver.ubuntu.com --send-keys <KEY_ID>
   ```
4. 在 GitHub 仓库 `Settings → Secrets and variables → Actions` 添加 3 个 Secret：
   - `CENTRAL_TOKEN`：Central 发布 token
   - `GPG_PRIVATE_KEY`：`gpg --armor --export-secret-keys <KEY_ID>` 的完整输出
   - `GPG_PASSPHRASE`：GPG 私钥口令

### 2.2 方式 A：GitHub Actions CI 自动发布（推荐）

打版本 tag 即触发 `.github/workflows/publish-maven-central.yml`：

```bash
git tag v1.0.0
git push origin v1.0.0
```

CI 自动执行：`setup-java 21` → 导入 GPG 私钥 → `mvn -Ppublish-central deploy`
（含 `-sources.jar` / `-javadoc.jar` + GPG 签名），构件发布到 Maven Central。

### 2.3 方式 B：本机手动 `mvn deploy`

需本机装有 JDK 21 + Maven + GPG：

```bash
cd multi-lang/java
export CENTRAL_TOKEN=<CENTRAL_TOKEN>
mvn -Ppublish-central -Dgpg.passphrase=<GPG_PASSPHRASE> deploy
```

### 2.4 用户接入（发布后）

```xml
<!-- Maven pom.xml -->
<dependency>
    <groupId>com.qqzeng</groupId>
    <artifactId>qzdb</artifactId>
    <version>1.0.0</version>
</dependency>
```
```groovy
// Gradle
implementation 'com.qqzeng:qzdb:1.0.0'
```

---

## 3. 一致性约定

- **版本**：.NET = `1.0.1`（net10.0 单目标），Java = `1.0.0`；升级时分别同步 `csproj` 的 `<Version>` 与 `pom.xml` 的 `<version>`，并以新版本号重新发布（NuGet 版本不可变）。
- **命名空间品牌**：`.NET = QQZeng.*`、`Java = com.qqzeng.*`，二者同源 `qqzeng`。
- **NEVER push 自动执行**：以上发布步骤涉及远端写操作（NuGet push / Maven Central deploy / 打 tag），
  均需在确认凭据与仓库存在后由人工触发，CI 仅在你主动打 tag 时运行。

## 4. 当前状态（2026-08-07）

- .NET：初版 `1.0.0`（仅 net8.0）已上传；因改用 net10.0 单目标且 NuGet `1.0.0` 不可变，需改传 **1.0.1**（`nupkgs/QQZeng.Qzdb.1.0.1.nupkg`，仅 net10.0）。
- Java：发布配置已就绪（`pom.xml` + workflow 已提交），待用户提供 Central token + GPG 私钥并打 `v1.0.0` tag 触发首次发布。
- 本机无 JDK / Maven，Java 包的首次真实 `mvn` 校验留待 CI 执行。
