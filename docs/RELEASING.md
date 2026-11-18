# 发布指南

## 分支与版本政策

日常开发使用 `main` 加短期功能分支，通过 Pull Request 合并。修复版本也从 `main` 发布；暂不维护长期发布分支。

版本统一在仓库根目录的 `Directory.Build.props` 中维护。维护者按改动选择下一个版本：

| 改动 | 版本变化 | 示例 |
| --- | --- | --- |
| 向后兼容的问题修复 | patch | `1.0.26` → `1.0.27` |
| 向后兼容的新功能 | minor | `1.0.26` → `1.1.0` |
| 不兼容的接口、命令或文件格式变化 | major | `1.0.26` → `2.0.0` |

正式版在 `VersionPrefix` 中填写 `A.B.C`，将 `VersionSuffix` 留空；预发布在 `VersionSuffix` 中填写 `alpha.N`、`beta.N` 或 `rc.N`，其中 `N` 是不带前导零的正整数。例如 `VersionPrefix=1.0.27`、`VersionSuffix=rc.1` 对应 `1.0.27-rc.1`，不要把后缀写入 `VersionPrefix`。候选版转正式版时清空 `VersionSuffix`。

GitHub Actions 从配置读取完整版本，自动创建对应的 `vA.B.C` 或 `vA.B.C-rc.N` 等标签。发布 EXE 的信息版本保留预发布后缀；文件版本与程序集版本使用四段数字 `A.B.C.0`。仓库数字段限制为 `0..65534`，以兼容 .NET 程序集版本；提交 Microsoft Store 时第一段还必须大于 `0`，MSIX 版本为 `A.B.C.0`。

## GitHub Actions 发布 Windows 应用和跨平台 CLI

### 准备发布

1. 确认 GPL-3.0-or-later 许可证、隐私政策与支持联系方式已满足本次分发要求。发布时保留指向构建提交的标签，并在同一 Release 提供该版本完整对应源代码及构建说明，确保源代码可与 EXE/MSIX 一同获取；核对 GitHub 自动生成的源码归档包含构建所需文件。升级依赖或自包含 .NET 运行时时同步更新 [第三方声明](../THIRD-PARTY-NOTICES.txt)。项目许可证、第三方声明和字体协议均嵌入应用，可在“关于”页离线查看；授权边界见 [LICENSE-NOTICE.md](../LICENSE-NOTICE.md)。
2. 在功能分支修改 `Directory.Build.props` 的 `VersionPrefix`（预发布同时设置 `VersionSuffix`），并把本次内容从 `CHANGELOG.md` 的 `Unreleased` 整理到对应版本段。保留 `## Unreleased`，用于后续开发。
3. 版本段标题必须使用 `## [1.0.27] - YYYY-MM-DD` 或 `## [1.0.27-rc.1] - YYYY-MM-DD` 的形式，将日期占位符替换为实际发布日期，且段落内容不能为空。预发布和正式版各自需要与标签完全对应的版本段；从本次发布起，新增版本段按下文约定同时提供中文和英文日志。
4. 将版本配置和发布日志放入同一个发布准备 PR，确认 `build` 工作流通过后合并到 `main`。该次 push 的变更范围必须同时包含这两个文件，且配置中的完整版本必须实际变化，才会启动自动发布。
5. GitHub Actions 完成检查、自动创建版本标签并生成 Release 草稿；维护者检查后手动公开。
6. 公开后，用上一个正式版的便携 EXE 在关于页执行一次「检查更新」，确认发现新版本、下载校验、替换重启和版本号更新。`releases/latest` 不包含草稿或预发布，因此该验证只能在公开后完成。

例如未来修复版在 `Directory.Build.props` 中设置：

```xml
<VersionPrefix>1.0.27</VersionPrefix>
<VersionSuffix></VersionSuffix>
```

同时在 `CHANGELOG.md` 中新增非空的 `## [1.0.27] - YYYY-MM-DD` 版本段并填写实际日期，合并到 `main` 后自动生成 `v1.0.27` 标签。候选版则将 `VersionSuffix` 设为 `rc.1`，日志版本写为 `1.0.27-rc.1`。

### 双语发布日志

后续发布在同一个带日期的 `## [版本] - 日期` 段内依次使用 `### 中文` 和 `### English`。两组条目描述相同的改动事实、限制及验证情况；英文应完整翻译对应中文内容，不省略警告，也不增加未经验证的能力声明。整理发布内容后保留顶部的 `## Unreleased`，供下一轮开发使用。

例如，发布准备中的结构如下；版本、日期和条目都应替换为本次发布的实际内容：

```markdown
## Unreleased

## [1.3.4] - YYYY-MM-DD

### 中文

- 在设置中增加界面语言选择。

### English

- Add interface language selection to Settings.
```

`Get-ReleaseMetadata.ps1` 提取整个版本段，仅在下一条 `## ` 标题或文件末尾停止，因此 `### 中文` 和 `### English` 都会完整进入 Release 说明。不要将语言标题写成 `## 中文` 或 `## English`，否则会提前结束版本段。当前元数据检查验证版本段唯一、日期有效且内容非空；两种语言是否齐全、条目是否一致仍需发布评审确认。

工作流在双语日志之后附加中英文下载与运行说明、SHA-256 核对命令，以及对应源代码和构建说明。下载说明须用两种语言明确标注 Linux 和 macOS CLI 为实验性功能，并分别说明 Windows EXE 的签名状态及即时刻录的实验性限制。

### 自动检查与草稿

`.github/workflows/release.yml` 关注 `main` 上对 `Directory.Build.props` 或 `CHANGELOG.md` 的变更，但只有该次 push **同时修改版本配置与发布日志，且完整版本变化**才进入发布流程。普通代码 push 只运行常规构建检查；仅补充 `Unreleased` 或未改版本的配置调整不会生成 Release。

工作流从配置读取完整版本，校验格式及对应的非空、带日期 Changelog 段，确认发布提交属于 `origin/main`，并复用 `build.yml` 的构建与回归检查。全部通过后才创建对应版本标签与草稿。

Windows x64 与 ARM64 均提供自包含的单文件 EXE。`build.yml` 的 Windows 矩阵分别使用 `windows-latest`（`win-x64`）和 `windows-11-arm`（`win-arm64`）原生 runner，运行软件回归后，每次构建都调用 `Build-Release.ps1` 生成并检查最终 EXE。脚本核对 runner 的系统及进程架构、EXE 的 PE 架构和 CET 兼容标记，然后在隔离目录执行自检、两扇区 RAW/WAV 生成检查及 GUI 截图检查。ARM64 要求 Windows 11；这些 CI 检查不连接刻录设备，尚不能证明 ARM64 上的实盘刻录兼容性。

Windows 应用宿主设置 `CetCompat=false`，用于绕过 #229 所报的受影响系统启动问题；这会降低该应用进程的硬件影子栈保护，不修改 Windows 的全局安全设置。原报告机器上的修复效果仍待验证，发布说明不得将软件自检等同于原机复现验证。

Linux 与 macOS 的实验性离线 CLI 在各自的 x64、Arm64 runner 上构建，发布包自带 .NET 运行时。每个 runner 解开刚生成的 tar.gz，在仓库目录外运行 `--help`、DVD 自检以及 CD 音轨、DVD 镜像和蓝光镜像生成测试。发布流程收集通过这些测试的四个原始压缩包，以及 Windows 矩阵已检查的两个原始 EXE；不会另行重建 EXE。每份制品均核对其相邻的 SHA-256 sidecar 后再汇集到同一个发布目录。附件命名如下（`A.B.C` 替换为实际版本，预发布还包括后缀）：

```text
DiscImageStudio-vA.B.C-win-x64.exe
DiscImageStudio-vA.B.C-win-arm64.exe
DiscImageStudio-CLI-vA.B.C-linux-x64.tar.gz
DiscImageStudio-CLI-vA.B.C-linux-arm64.tar.gz
DiscImageStudio-CLI-vA.B.C-osx-x64.tar.gz
DiscImageStudio-CLI-vA.B.C-osx-arm64.tar.gz
SHA256SUMS.txt
latest.json
```

工作流合并并验证各平台的 SHA-256 校验值，写入 Release 说明和附件 `SHA256SUMS.txt`，并根据已测试的两个 Windows EXE 生成应用内更新清单 `latest.json`。工作流使用内置 `GITHUB_TOKEN` 创建 **GitHub Release 草稿**，无需配置个人访问令牌（PAT）。预发布标签自动设置 prerelease 标志，正式标签创建正式版草稿。

维护者在 GitHub 的 [Releases 页面](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/releases) 检查草稿中的版本、发布说明、各平台附件和校验值，并下载验证启动后，再手动公开。可用下列命令计算 Windows 下载文件的 SHA-256，与 Release 说明中的值比较：

```powershell
Get-FileHash .\DiscImageStudio-vA.B.C-win-x64.exe -Algorithm SHA256
```

## Release 附件的可选 PGP 签名

Release 工作流支持可选的 OpenPGP 分离签名。只有在仓库的 GitHub Actions Secrets 中配置了 `RELEASE_SIGN_KEY`（值为用于签名的 PGP 私钥）时，签名步骤才会执行；未配置时，工作流会跳过 Sequoia SQ 的构建、签名和验签步骤，不影响原有发布流程。

启用签名后，工作流使用 Sequoia PGP 为以下附件生成分离签名，签名文件以原文件名加 `.sig` 命名，并在上传草稿前使用导出的公钥执行验签：

- Windows x64 与 ARM64 EXE；
- Linux 与 macOS CLI 的 tar.gz 压缩包；
- `SHA256SUMS.txt`；
- 应用内更新清单 `latest.json`。

公钥以 `public_key.asc` 随 Release 发布。工作流通过 `sq key delete --cert-file private_key.pgp --output release/public_key.asc` 从私钥文件导出公钥证书；该命令会从输出证书中移除私钥材料，并不意味着删除或吊销原始密钥。需要同时保留原始密钥文件时，应先备份。具体语义见 [Sequoia PGP 文档](https://book.sequoia-pgp.org/revocation.html#delete-a-key)。

### 配置与构建

在仓库 Settings → Secrets and variables → Actions 中创建名为 `RELEASE_SIGN_KEY` 的 Repository Secret，并将 PGP 私钥内容作为 Secret 值。不要把私钥提交到仓库、写入普通工作流变量，或上传为 Actions Artifact。

签名 job 使用 Ubuntu 26.04 提供的 OpenSSL 3.5.x，并通过 Cargo 从源码构建 Sequoia SQ，显式启用 `crypto-openssl` 特性。工作流将 `./sequoia/bin` 缓存为 Actions Cache；当前缓存键固定，因此更新 Sequoia SQ 构建时，需要删除对应缓存，才能强制重新构建。未配置 `RELEASE_SIGN_KEY` 时不会安装或构建 Sequoia SQ。

### 验证签名

下载对应附件、其 `.sig` 文件及 `public_key.asc` 后，可使用 Sequoia SQ 验证分离签名。例如：

```bash
sq verify \
  --signer-file public_key.asc \
  --signature-file "DiscImageStudio-vA.B.C-win-x64.exe.sig" \
  "DiscImageStudio-vA.B.C-win-x64.exe"
```

将示例文件名替换为实际 Release 附件名称。验证签名时还必须确认公钥属于项目官方发布者。不能仅因为公钥与签名文件来自同一个 Release，就认为公钥可信；发布者应通过独立可信渠道公布公钥指纹，用户应通过该渠道核对指纹。

OpenPGP 分离签名与 Windows Authenticode 代码签名是两种不同机制。提供 PGP 签名不会使 Windows EXE 成为 Authenticode 已签名文件。

**应用内更新目前不执行上述 PGP 验签。** 更新器仍只根据 `latest.json` 中的文件长度和 SHA-256 检查下载的 EXE；它既不验证 EXE 的 `.sig`，也不验证 `latest.json` 的 PGP 签名。因此，这些校验不能抵御有权同时修改 Release 附件和清单的攻击者。

## 应用内更新

Windows 便携 EXE 的关于页提供「检查更新」，请求固定地址：

```text
https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/releases/latest/download/latest.json
```

发布流程从已测试的 win-x64 与 win-arm64 EXE 生成该清单，记录版本、标签、更新说明链接，以及每个架构的文件 URL、SHA-256 和字节数。`releases/latest` 只指向已公开的正式版；草稿和 prerelease 不会提供给稳定版客户端。更新源一次只保留一个正式版本，不提供预发布频道或增量更新。

应用内更新只支持官方仓库 `JiaFeiMiao-K-Cat/DiscImageStudio` 的正式 Release。清单地址固定在代码中，下载 URL 必须属于同一仓库的 `releases/download/` 路径，且不能带查询串、片段、用户信息或非默认端口。fork、私有镜像和 GitHub Enterprise 构建不会读取各自的 Release，也没有改写更新源的设置；这些构建请自行分发，并由用户手动替换 EXE。

客户端先校验清单结构、下载 URL、字节数、SHA-256、EXE 产品版本和 PE 架构，再把文件存入 `%LOCALAPPDATA%\DiscImageStudio\updates\<版本>`。用户确认安装后，设置窗口保存自己的改动并关闭，主窗口再对未保存的工程执行与关闭窗口相同的保存/放弃/取消流程；取消保存会放弃安装并删除已下载的文件。安装开始时，应用把 EXE 复制到同卷的 `<EXE>.update`，由临时 PowerShell 辅助进程等待主程序退出后原子替换原文件。新版本启动并写入健康标记后更新才算完成；启动失败时辅助进程回滚旧 EXE 并重新启动它。辅助进程错误写入 `update-error.txt`，下次启动会在主窗口提示上次更新未完成及原因。

更新期间使用命名信号量阻止另一个实例同时下载或安装，同一路径的其他实例会被要求先关闭。检查更新可随时取消；关闭设置窗口会中止下载、释放确认对话框并删除未完成的临时文件。应用目录不可写、磁盘空间不足或文件与清单不符时，界面显示可读错误并建议手动下载，不会静默退出或留下交换用临时文件。

更新包和清单未做代码签名，SHA-256 只能发现传输损坏或与清单不一致的文件；能够改写 Release 的维护者仍可发布任意二进制。不要把更新源改为未经核对的第三方镜像。

### 失败重试与更正

可以重跑失败的 Actions 运行，也可以在 GitHub Actions 从 `main` 手动运行 `release` 工作流。手动触发没有标签输入，读取 `main` 当前配置和发布日志并执行全部发布检查，无需再次修改版本文件来重试。

同一版本重试时，已有标签必须指向本次构建的同一提交，且已有 Release 必须仍为草稿。已经公开的 Release、标签与制品不得覆盖或移动；发现问题时，从 `main` 修复并发布新版本，预发布迭代则增加序号，例如 `rc.1` → `rc.2`。如果 `main` 已前进且同版本标签指向旧提交，应重跑原先失败的运行，或为新提交准备新版本。仅修改发布说明也应保留事实与更正记录，不得替换已公开的二进制文件。

建议仓库管理员在 GitHub 配置 `main` 分支保护，要求 Pull Request 和 `build` 必需检查；为 `v*` 标签设置规则集，允许发布工作流创建标签并禁止更新、删除。工作流文件不等同于远端保护设置，这些保护需要管理员另行配置。

## 本地 EXE 演练

在对应架构的 Windows 机器上，使用 PowerShell 7.4 或更新版本，从仓库根目录运行与 GitHub Actions 相同的打包脚本。默认 `RuntimeIdentifier` 为 `win-x64`：

```powershell
pwsh -File packaging/Build-Release.ps1
```

在 Windows 11 ARM64 上，使用 ARM64 PowerShell/.NET 进程运行：

```powershell
pwsh -File packaging/Build-Release.ps1 -RuntimeIdentifier win-arm64
```

脚本要求系统架构、进程架构与所选 RID 一致，不通过跨架构模拟完成发布验证，也不提供跳过最终 EXE 检查的开关。需要交叉编译时可单独使用 `dotnet publish`，但交叉编译结果不算通过本流程的原生发布验证。

脚本自动使用 `Directory.Build.props` 中的版本，输出到 `artifacts/release/<tag>/<rid>/`。例如版本 `1.0.26` 的 x64 文件位于 `artifacts/release/v1.0.26/win-x64/`，文件名为 `DiscImageStudio-v1.0.26-win-x64.exe`，相邻校验文件为 `DiscImageStudio-v1.0.26-win-x64.exe.sha256`；ARM64 使用对应的 `win-arm64` 目录和文件名。正式发布时，工作流验证两份 Windows EXE 与四份 CLI 压缩包的 sidecar，再为全部六份制品生成统一的 `SHA256SUMS.txt`。

## Microsoft Store MSIX

MSIX 是独立的手动分发通道。先在 Partner Center 保留产品名，并从 Product identity 页面复制：

- Package/Identity/Name
- Package/Identity/Publisher
- Package/Properties/PublisherDisplayName

本地执行：

```powershell
powershell -ExecutionPolicy Bypass -File packaging/Build-MSIX.ps1 `
  -IdentityName "Partner Center Identity Name" `
  -Publisher "CN=Partner Center Publisher" `
  -PublisherDisplayName "Publisher display name" `
  -DisplayName "Reserved product name"
```

未填写 `-Version` 时，从 `Directory.Build.props` 读取正式版本并转换为 `A.B.C.0`。需要明确指定商店版本时，使用 `-Version "1.0.26.0"`；此覆盖值也会同步到包内 EXE 的版本，避免清单与程序版本不一致。Microsoft Store 通道不接受 `-rc.1` 等预发布后缀；准备 Store 包时应使用正式版本配置。

也可以在 GitHub Actions 手动运行 `package-msix` 工作流并填写相同值，`version` 留空时同样读取仓库配置。该工作流先复用 `build.yml` 的 CI，再生成 MSIX 作为 Actions 附件；它不会自动提交商店或附加到 GitHub Release。生成的 Store MSIX 可以保持未签名；若要旁加载测试，需使用 Publisher 匹配的证书签名。

## 本机签名测试包

为本机测试创建主题与测试 Publisher 完全一致的代码签名证书，把公钥证书放到：

```text
artifacts/certificates/DiscImageStudio-Test.cer
```

使用证书指纹运行 `Build-MSIX.ps1 -CertificateThumbprint <thumbprint>`（同时填写上文的身份参数）生成签名包。然后双击 `packaging/Install-TestPackage.cmd`，或运行：

```powershell
powershell -ExecutionPolicy Bypass -File packaging/Install-TestPackage.ps1
```

安装脚本会请求一次管理员权限，将测试证书公钥导入“本地计算机/受信任人”，并安装签名 MSIX。不要把私钥或 PFX 文件提交到 GitHub，也不要把本机自签名测试证书作为正式 Release 附件。

## Windows 11 未签名快速测试（可选）

未签名测试包必须使用独立的 OID 发布者命名空间，不能与商店正式身份混用。生成本机测试包：

```powershell
powershell -ExecutionPolicy Bypass -File packaging/Build-MSIX.ps1 `
  -IdentityName "12345.DiscImageStudioArchitectureTest" `
  -Publisher "CN=Disc Image Studio Test" `
  -PublisherDisplayName "Disc Image Studio Test" `
  -DisplayName "Disc Image Studio Test" `
  -Version "1.0.26.0" `
  -UnsignedDevelopmentPackage
```

然后用管理员 PowerShell 安装：

```powershell
Add-AppxPackage -Path ".\artifacts\msix\DiscImageStudio_1.0.26.0_x64_unsigned-dev.msix" -AllowUnsigned
```

`-UnsignedDevelopmentPackage` 只用于支持该功能的 Windows 11 系统快速测试；部分 Windows 版本即使提供 `-AllowUnsigned` 仍可能拒绝可执行包，此时使用上面的签名测试方案。准备分发或提交 Microsoft Store 时不要使用该选项，正式包仍需使用 Partner Center 的真实身份。

正式提交前：

- 用真实身份重新打包，不能提交测试 Identity 包。
- 在干净的 Windows 设备上测试安装、卸载和离线运行。
- 对最终包运行 Windows App Certification Kit。
- 提供至少一张 1366×768 或更大的真实桌面截图。
- 将 `PRIVACY.md` 发布到公开 HTTPS 页面并填写真实支持联系方式。
- 在认证备注中说明应用生成大文件，除用户主动检查更新外不联网（检查更新只访问 GitHub Releases），并包含使用 Windows IMAPI2 的实验性 CD/DVD 直接刻录功能；如实列出适用的设备、空白盘片要求与当前验证范围。
