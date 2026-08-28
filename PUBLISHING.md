# 发布指南 (Publishing)

本仓库准备了多种可发布的官方包；发布完成后，用户**无需克隆源码**即可集成：

| 语言 | 包坐标 | 注册中心 | 包 ID | 状态 |
|------|--------|----------|-------|------|
| Java | `com.qqzeng:qzdb` 1.0.6 | Maven Central | `com.qqzeng:qzdb` | ✅ 已发布 |
| .NET / C# | `QQZeng.Qzdb` 1.0.7 | NuGet | `QQZeng.Qzdb` | ✅ 已发布 |
| Python | `qzdb` 1.0.5 | PyPI | `qzdb` | ✅ 已发布 |
| Node.js | `@qqzengip/qzdb` 1.0.5 | npm | `@qqzengip/qzdb` | ✅ 已发布 |
| Go | `github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go` | Go Module（无注册中心） | — | ✅ 已可用 |
| **Rust** | **`qzdb` 1.0.5** | **crates.io** | **`qzdb`** | ⏳ 配置就绪，待人工授权发布 |
| **PHP** | **`qqzeng/qzdb` 1.0.5** | **Packagist** | **`qqzeng/qzdb`** | ⏳ 配置就绪，待人工授权发布 |

> 包版本独立于 QZDB 数据格式版本（数据格式见 `API_CONTRACT.md`）。
> 两个待发布包名已于 2026-08-29 在各自注册中心确认**未被占用**。

---

## 0. Git tag 约定（先读这一节）

本仓库是 8 语言 monorepo，各注册中心的发布由 tag 驱动。**tag 前缀决定触发哪个 workflow**：

| tag 形态 | 触发的发布 |
|----------|-----------|
| `v1.0.6` | 全平台（PyPI + Maven Central） |
| `v-python-<ver>` | 仅 PyPI |
| `v-java-<ver>` | 仅 Maven Central |
| `v-rust-<ver>` | 仅 crates.io |
| PHP | **无 CI**：Packagist 通过 GitHub webhook 监听全部 tag 与 push |

> ⚠️ 历史坑：三个发布 workflow 原先都写 `tags: ['v*']`。这在 monorepo 里是错的——
> `v*` 会同时匹配 `v-rust-1.0.5` / `v-java-1.0.5`，导致发 Rust 版时**误触发 PyPI 与
> Maven Central 发布**。现均已收紧为 `v[0-9]*` + 各自前缀。改动 workflow 的 tag 过滤器时
> 务必保持这个约束。

---

## 1. .NET / NuGet 包（`QQZeng.Qzdb`）

- 工程：`multi-lang/netcore/QQZeng.Qzdb.csproj`（**net8.0;net9.0;net10.0;net11.0 多目标**）
- 包元数据：`PackageProjectUrl` / `RepositoryUrl` 指向 `https://github.com/zengzhan/qqzeng-ip`

### 1.1 本地打包

```bash
cd multi-lang/netcore
dotnet pack -c Release -o ./nupkgs
# 产出：nupkgs/QQZeng.Qzdb.1.0.7.nupkg  (+ .snupkg 符号包)
```

### 1.2 发布到 NuGet

```bash
dotnet nuget push ./nupkgs/QQZeng.Qzdb.1.0.7.nupkg \
  --api-key <NUGET_API_KEY> \
  --source https://api.nuget.org/v3/index.json
```

- 需要 NuGet 账号的 **API Key + 2FA**。
- 本仓库 `.gitignore` 已排除 `multi-lang/netcore/nupkgs/`，打包产物不入库。

---

## 2. Java / Maven Central 包（`com.qqzeng:qzdb`）

- 工程：`multi-lang/java/pom.xml`（JDK 21，jar）
- groupId `com.qqzeng` 已通过 **qqzeng.com 域名所有权**验证（Maven Central 强制命名空间校验）。
- 发布插件：`central-publishing-maven-plugin`（OSSRH 已退役，统一走 Central Portal）。

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

```bash
git tag v-java-1.0.6      # 或全平台 tag：v1.0.6
git push origin <tag>
```

CI 自动执行：`setup-java 21` → 导入 GPG 私钥 → `mvn -Ppublish-central deploy`
（含 `-sources.jar` / `-javadoc.jar` + GPG 签名）。

### 2.3 方式 B：本机手动 `mvn deploy`

需本机装有 JDK 21 + Maven + GPG：

```bash
cd multi-lang/java
export CENTRAL_TOKEN=<CENTRAL_TOKEN>
mvn -Ppublish-central -Dgpg.passphrase=<GPG_PASSPHRASE> deploy
```

### 2.4 用户接入（发布后）

```xml
<dependency>
    <groupId>com.qqzeng</groupId>
    <artifactId>qzdb</artifactId>
    <version>1.0.6</version>
</dependency>
```

---

## 3. Rust / crates.io（crate `qzdb`）

- 工程：`multi-lang/rust/Cargo.toml`（crate 名 `qzdb`，edition 2021，MSRV 1.74）
- 自动发布：`.github/workflows/publish-crates.yml`（tag `v-rust-*` 触发）

### 3.1 发布前置条件（一次性）

1. 用 GitHub 账号登录 [crates.io](https://crates.io)。
2. 生成 API token：<https://crates.io/settings/tokens>，作用域选 `publish-update`，
   并限定只作用于 `qzdb` 这一个 crate。
3. 把 token 加到仓库 Secret：`CARGO_REGISTRY_TOKEN`。

> 🔒 **不可逆，务必确认后再发**：crates.io 的 crate 名一经占用即永久归属（无法转让、
> 无法删除），版本号一经发布即永久锁定（只能 yank 后升版本）。`qzdb` 已于 2026-08-29
> 确认未被占用。

### 3.2 发布前本地验证（必做）

```bash
cd multi-lang/rust
cargo package --list --allow-dirty     # 看清楚哪些文件会进包
cargo package --allow-dirty            # 打包 + verify 构建（等价于 CI 的 dry-run）
cargo check --all-targets              # 确保所有 target 编译通过
```

期望清单（10 个文件，约 150 KB）：

```
.cargo_vcs_info.json  Cargo.lock  Cargo.toml  Cargo.toml.orig
LICENSE  README.md  examples/{diag,failclosed,fcprobe}.rs  src/lib.rs
```

> `cargo package` 会打印约 16 行 `warning: ignoring binary/test ... as not included in the
> published package`。这是**预期行为**，不是错误：内部开发工具（`src/bin/*`、`src/main.rs`）
> 与依赖私有 `.qzdb` 数据的集成测试（`tests/*`）已通过 `Cargo.toml` 的 `exclude` 排除在
> 发行包之外。其中 `src/bin/metaprobe.rs` 依赖 dev-only 的 `serde_json`，若随包发布，
> 下游 `cargo install qzdb` 会因缺 dev-dependency 直接编译失败——**不要移除 exclude**。

### 3.3 方式 A：GitHub Actions CI 自动发布（推荐）

```bash
git tag v-rust-1.0.5
git push origin v-rust-1.0.5
```

Workflow 先跑 `cargo package --locked` 做 dry-run，成功后再 `cargo publish --locked`。

### 3.4 方式 B：本机手动发布

```bash
cd multi-lang/rust
cargo login <CRATES_IO_TOKEN>     # 写入 ~/.cargo/credentials.toml
cargo publish --allow-dirty
```

### 3.5 用户接入（发布后）

```bash
cargo add qzdb
```

```rust
use qzdb::QzdbReader;

let reader = QzdbReader::from_file("qqzeng_ip_std_china.qzdb")?;
println!("{}", reader.find_str("114.114.114.114"));
```

---

## 4. PHP / Packagist（包 `qqzeng/qzdb`）

- 包定义：**仓库根** `composer.json`（不是 `multi-lang/php/composer.json`）
- SDK 源码：`multi-lang/php/QzdbReader.php`（单文件，命名空间 `Qqzeng\Ip`）
- dist 裁剪：仓库根 `.gitattributes`

### 4.1 为什么 `composer.json` 在仓库根

Packagist 只在**仓库根**查找 `composer.json`，不支持子目录。若放在 `multi-lang/php/`，
提交时 Packagist 会直接报 "No composer.json found in the root of the repository"。

因此根 `composer.json` 用 `classmap` 指向子目录里的 SDK 文件：

```json
"autoload": { "classmap": ["multi-lang/php/QzdbReader.php"] }
```

配合 `.gitattributes` 的 `export-ignore`，用户下载到的 dist 只有 5 个文件（约 160 KB），
不会被迫拉取另外 7 种语言的源码和 4.3 MB 的 demo 数据库。

### 4.2 发布前置条件（一次性）

1. 用 **GitHub 账号 `zengzhan`** 登录 [packagist.org](https://packagist.org)（OAuth 授权）。
2. 提交包：<https://packagist.org/packages/submit>，填仓库地址
   `https://github.com/zengzhan/qqzeng-ip`。
   - 包名必须与根 `composer.json` 的 `name` 一致：`qqzeng/qzdb`。
3. 提交后按页面提示启用 **Packagist GitHub Hook**（自动在仓库里建好 webhook）。
   若自动配置失败，手动添加：GitHub 仓库 `Settings → Webhooks`，用 Packagist 页面给出的
   payload URL + token。

### 4.3 发布前本地验证（必做）

```bash
# composer.json 格式（严格模式）
composer validate --strict

# dist 裁剪是否符合预期——期望只看到这 5 项：
#   .gitignore  LICENSE  composer.json
#   multi-lang/php/QzdbReader.php  multi-lang/php/README.md
git add -A && TREE=$(git write-tree) && git archive "$TREE" | tar -t | sort
```

> ⚠️ `.gitattributes` 有两条反直觉的语义，改动前必须知道：
> 1. **不支持 `!` 反选**——git 2.40+ 会直接忽略该行并告警
>    `Negative patterns are ignored in git attributes`。覆盖通配忽略必须写 `-export-ignore`
>    （unset），且放在被覆盖规则**之后**。
> 2. **目录**一旦被 `export-ignore`，`git archive` 就不再递归进入它，即使后面用
>    `-export-ignore` 放行目录内文件也无效。所以绝不能写 `/multi-lang/** export-ignore`，
>    必须逐项列出要忽略的目录。

### 4.4 发版

Packagist 从 **git tag** 读取版本号，`composer.json` 里**不要**写 `version` 字段。

```bash
git tag v1.0.5          # 或 v-php-1.0.5
git push origin <tag>
```

推送后 Packagist webhook 会自动抓取新版本，无需 CI。

### 4.5 用户接入（发布后）

```bash
composer require qqzeng/qzdb
```

```php
require_once __DIR__ . '/vendor/autoload.php';
use Qqzeng\Ip\QzdbBuilder;

$reader = QzdbBuilder::path('qqzeng_ip_std_china.qzdb')->build();
echo $reader->findStr('114.114.114.114');
```

---

## 5. 一致性约定

- **版本**：Java `1.0.6`、.NET `1.0.7`、Python / Node.js / Rust / PHP `1.0.5`；升级时分别同步
  `pom.xml`、`csproj`、`pyproject.toml`、`package.json`、`Cargo.toml`，PHP 靠 git tag。
- **命名空间/包标识**：.NET 包 ID / 程序集 / 命名空间均为 `QQZeng.Qzdb`；Java `com.qqzeng.qzdb`；
  Rust crate `qzdb`（lib 根模块同名）；PHP `qqzeng/qzdb` + 命名空间 `Qqzeng\Ip`；
  作者/公司品牌统一为 `QQZeng`。
  > Rust crate 名原为 `qzdb_reader`，2026-08-29 起与 PyPI 的 `qzdb` 对齐改为 `qzdb`
  > （C 语言的 `qzdb_reader_t` / `qzdb_reader.h` 是另一套命名，**不受影响，不要一起改**）。
- **NEVER push 自动执行**：以上发布步骤涉及远端写操作（NuGet push / Maven deploy /
  crates.io publish / 打 tag），均需在确认凭据与仓库存在后由人工触发，CI 仅在你主动打
  tag 时运行。PHP 是例外——Packagist webhook 在你 push tag 后自动抓取，这本身就是发布动作，
  因此 **push tag 前务必确认 `composer.json` 已通过 `composer validate --strict`**。

## 6. 当前状态（2026-08-29）

- **Rust**：`Cargo.toml` 元数据、crate 改名、`LICENSE`、exclude 规则与
  `publish-crates.yml` 均已就绪；`cargo package --allow-dirty` 与 `cargo check --all-targets`
  本机全绿。**待人工**：创建 crates.io token → 写入 Secret `CARGO_REGISTRY_TOKEN` →
  打 `v-rust-1.0.5` tag。
- **PHP**：根 `composer.json`、`composer validate --strict` 通过、`.gitattributes` dist 裁剪
  已实测（160 KB / 5 文件）、Composer 安装 + autoload + 查询冒烟通过（输出与 Python 基准
  逐字节一致）。**待人工**：Packagist 提交授权 + 启用 webhook → 打 `v1.0.5` tag。
- **Java / .NET**：已发布，配置见上文 §1 / §2。
- 本机无 JDK / Maven，Java 包的首次真实 `mvn` 校验留待 CI 执行。
