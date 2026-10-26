# 验证说明

本页列出当前源码的可复现检查。自动化回归验证算法、文件布局和应用工作流；实际刻录效果仍取决于刻录机、固件与盘片。

## 本地验证

最近一次完整回归：2026-10-26，Windows、.NET SDK 10.0.400（运行时 10.0.11），Release 配置。完整 solution 构建（0 警告）与架构回归全部通过；测试入口直接运行自研 Exe harness，不使用 `dotnet test`。

在仓库根目录执行：

```powershell
dotnet restore DiscImageStudio.slnx
dotnet build DiscImageStudio.slnx --configuration Release --no-restore
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release --no-build
dotnet run --project src/DvdImageSolver --configuration Release --no-build -- selftest
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- selftest
./tests/Test-ReleaseTooling.ps1
```

架构回归覆盖：

- CD/DVD：黑白映射、抖动、sRGB 伽马、文件与流式一致性、SIMD 与标量采样一致性。
- BD：17PP/EDC/Reed-Solomon、3T/6T 映射与极性、绘图范围清零、UDF 2.01、大于 4 GiB 文件、sidecar 往返与无效边界拒绝。
- DVD/BD 文件位置：默认内圈兼容、外圈 ISO9660/Joliet 与 UDF 目录独立解析、中文文件内容、完整盘面坐标、不可 Seek 流与文件输出一致、容量及取消保护，以及工程中的位置选项往返。
- 三态模型：工程 schema 2 往返与 schema 1 升级、预设 schema 4 与旧 schema 合并、`ProjectSession` 的 BD apply/capture。
- 刻录：操作互斥、进度、顺序写入、超时、取消和资源清理，均使用模拟接口而非真实刻录机。
- 标定：三次轨距、BD 绘图范围与极性、照片矫正、曲线拟合、会话保存恢复、盘型预设同步与参数精度。
- 预览与窗口：CD/DVD/BD 投影与读回、多显示器和任务栏边界。
- 文件区预览：内外圈设计投影和带 sidecar 的成品读回标黄、DVD 三次轨距边界、纯绘图及无效布局信息不标黄、错误目录/容量提示、切换与重置清理。黄色叠加不改变 ISO 数据。

GUI 可通过现有命令离屏生成截图：

```powershell
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/home.png --tab 0
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/export-bd.png --tab 3 --project sample.discproj
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/preview-bd.png --tab 2 --project sample.discproj --live-input image.png --live-disc bd --live-mode calibrate
```

`build` 工作流另执行模块目录的 DVD 自检、两扇区 CD 输出检查和主页截图。发布单文件 EXE 的附加检查见[发布指南](RELEASING.md)。

本次另核对了 DVD/BD 各两种文件位置的设计投影、外圈导出页与外圈成品读回，共 8 张离屏截图；DVD/应用自检及发布工具的 45 项回归通过。

设计页文件选择入口另检查了 DVD、BD、CD、未选文件夹、18 个图层、窄窗口及低窗口高度 7 种界面状态；宽屏图层列表内部滚动，文件选择区域保持可达，构建为 0 警告、0 错误。

v1.3.0 另通过 `Build-Release.ps1` 的 .NET 10 自包含单文件验证：隔离目录仅放 EXE，刻录接口构建信息、DVD 自检、两扇区 RAW/WAV/CUE、关闭 CUE 及 WPF 界面渲染均通过。最终内置 .NET / Windows Desktop 10.0.11，并同步保留其完整上游许可声明。

## 实盘验证边界

此次文件位置与预览改动没有执行实盘刻录。自动化测试不证明设备写入策略、实际反射亮度或刻录成功率；蓝光功能仍为实验性。

已经记录的 DVD 三次轨距实拍与参数见 [README 中的前后对比](../README.md#辅助标定展示)。物理模型历史审计见 [PHYSICAL_MODEL_AUDIT.md](PHYSICAL_MODEL_AUDIT.md)，其中结论保留其标明的日期与代码基准，不能代替当前源码验证。

后续实盘记录应包含版本或提交、刻录机和固件、盘片型号、预设参数、速度、生成/刻录入口与原始照片，避免把某一设备的结果泛化到所有盘片。
