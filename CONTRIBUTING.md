# Contributing to Disc Image Studio

欢迎参与 Disc Image Studio。请先通读本文档；它覆盖问题报告、开发环境搭建、模块边界规则、验证要求与提交流程。

## 报告问题

普通问题请使用 GitHub Issues 提交，并在描述中附上：

- 应用版本与获取方式（源码构建 / MSIX 包），以及 Windows 版本。
- 可稳定复现的步骤、期望行为与实际行为。
- 适用的运行日志或截图。

刻录相关问题请额外附上刻录机型号、固件版本与盘片品牌/型号：可见效果和实测校准与这些硬件强相关，缺少它们的问题通常无法定位。

可被利用的安全问题**不要**在公开 Issue 中披露，请按[安全政策](SECURITY.md)使用 GitHub 私密报告功能提交。

功能建议同样走 Issues：说明要解决的问题、期望行为，以及是否已有可行的绕过方案。

## 开发环境

- Windows 10 2004+ 或 Windows 11。
- .NET 10 SDK；仓库用 `global.json` 锁定 `10.0.x`，安装后 `dotnet` 会自动匹配。
- 测试没有第三方框架依赖，全部是仓库自带的控制台回归程序。

```powershell
git clone <你的 fork 地址>
cd DiscImageStudio
dotnet build DiscImageStudio.slnx --configuration Release
dotnet run --project src/DiscImageStudio.App --configuration Release
```

## 模块边界

改代码前请先读[架构说明](docs/ARCHITECTURE.md)。核心取舍：外壳不依赖任何一种光盘编码，介质专用逻辑留在对应模块。提交评审会重点检查以下规则：

1. 不要把 CD/DVD/未来蓝光的物理调制、纠错、扇区组织或文件系统细节放进 `DiscImageStudio.Core` 或共享层。
2. 模块不得从 GUI 控件读取状态，输入全部来自 `DiscJobRequest`；模块不得直接显示窗口。
3. 底层算法不能安全取消时，`SupportsCancellation` 必须为 `false`。
4. 模块 ID 与命令名全局唯一，目录在启动时拒绝冲突。
5. 新增命令必须注册到 `DiscCommandDescriptor`，并补充目录路由测试和最小输出冒烟测试。

## 编码规范

- 跟随周围代码的命名、结构与注释密度；构建输出保持零警告、零错误。
- 不要为通过构建而删减或降级功能；也不要自行添加向后兼容垫层或重复实现。
- 删除看似有意的功能或代码前，先在 Issue 或 PR 描述中说明目的。
- 不要提交构建产物、二进制资源或任何私钥 / PFX 文件。

## 测试与验证

Pull Request 前必须全部通过：

```powershell
dotnet build DiscImageStudio.slnx --configuration Release
dotnet run --project src/DvdImageSolver --configuration Release -- selftest
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release
```

按改动内容补充验证，并在描述中写明结果：

- 改动图片布局、流式刻录或盘片预设解析时，对照 [VALIDATION.md](docs/VALIDATION.md) 的对应回归项复核结论。
- 改动 GUI 时，用屏幕外渲染截图自检关键页面，必要时更新 `docs/images/`。
- 延迟交织、EFMPlus、ECC 等编码链改动后，`selftest` 覆盖项不变时也请说明变化原因。

## 提交与 Pull Request

1. Fork（或从 `main` 拉出）一个功能分支，一个分支只解决一件事，保持差异可评审。
2. 提交信息标题使用简短描述；正文说明行为变化、验证方式，以及是否会产生大文件。不要滥用 emoji！
3. PR 描述写清动机、改动口径与验证结果；涉及用户可见行为时，同步更新 `CHANGELOG.md` 的 Unreleased 段与相关 `docs/` 文档。
4. 提交后确认 GitHub Actions 的 `build` 工作流为绿色。

大改动（新模块、新 UI 页面、刻录层契约变更）建议先开 Issue 或 Draft PR 讨论方案，再写实现。

## 版本与发布

采用 `main` 加短期功能分支，以及按修复、功能、不兼容变化递增的语义化版本。日常贡献把变更记入 `CHANGELOG.md` 的 `Unreleased`；维护者在同一个发布准备 PR 中更新 `Directory.Build.props` 的版本配置和对应版本记录，合并到 `main` 后由 GitHub Actions 自动生成版本标签与 Release 草稿。普通代码 push 不生成 Release。完整触发条件、预发布后缀、草稿审核与重试规则见[发布指南](docs/RELEASING.md)。

## 许可证

项目采用 [GNU GPL v3.0 or later](LICENSE)（`GPL-3.0-or-later`）。提交贡献即表示你有权提交相关内容，并同意按该许可授权该贡献。引入第三方代码、字体或图片时，请保留原作者与许可证并确认与项目分发方式兼容，补充 [第三方声明](THIRD-PARTY-NOTICES.txt)；第三方授权边界见 [LICENSE-NOTICE.md](LICENSE-NOTICE.md)。
