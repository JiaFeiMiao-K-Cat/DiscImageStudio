# 验证说明

本页列出当前源码的可复现检查。自动化回归验证算法、文件布局和应用工作流；实际刻录效果仍取决于刻录机、固件与盘片。

## 本地验证

2026-11-04：CD/BD 三次轨距改动通过 Release solution 构建（0 警告、0 错误）。架构回归从全量入口运行，发现并修正 BD 粗标应用的极性恢复遗漏后，复跑照片标定及后续全部检查通过；新增三次轨距、像素复用、实际 ISO 回读与应用持久化检查均通过。未执行实盘刻录。测试入口直接运行自研 Exe harness，不使用 `dotnet test`。

历史完整架构回归：2026-10-28，覆盖界面修复、标定生成优化与 Verbatim BD-R (64099) 预设（对应提交 `3088802`、`49a0e4d`、`46e2c53`），Windows、.NET SDK 10.0.400（运行时 10.0.11），Release 配置。完整 solution 构建（0 警告、0 错误）与架构回归全部通过。

标定刻录结束后已完成生成优化的构建、完整架构回归和 DVD 引擎自检。Verbatim 预设加入后再次通过构建与完整架构回归，验证 GUI 与 CLI 的半径、扇区数、极性一致，以及预设合并和命令行覆盖。v1.3.3 的版本／更新日志校验与发布工具 45 项检查通过；未新增性能测速或实盘刻录结论。

已通过的新增回归包含：BD 标定并行开关、默认顺序回调、极性及绘图范围、批次顺序和取消；CD 标定四点采样的坐标位一致、交织与字节序，CD 文件模式内外圈的标量／SIMD 字节一致性；DVD 解析采样与码字采样的坐标、索引、逐字节输出及并行尾批一致性。

2026-11-04 三次轨距生成测速：Windows、Intel Core i5-1340P、.NET 10.0.14、x64、16 个逻辑处理器、Vector256 可用，Release；各项先完整预热一次，再取五轮中位数。各测试顺序执行，写入 `Stream.Null`，下表反映采样和数据生成，不包含磁盘写入或刻录时间。

| 图像生成场景 | 恒定轨距 | 三次轨距 |
| --- | ---: | ---: |
| CD，1024×1024 图像，四点 SIMD | 45.75 MiB/s | 25.61 MiB/s |
| BD，4096×4096 图像，默认并行 | 69.32 MiB/s | 114.15 MiB/s |
| BD，8192×8192 图像，默认并行 | 83.01 MiB/s | 106.18 MiB/s |

CD fixture 为 2048 扇区（4,816,896 字节）、24.5–57.5 mm、1200 mm/s、无交织、大端音频，系数 `(0.3, -0.25, 0.12)`；同场景三次轨距标量为 11.87 MiB/s。BD fixture 为完整单层几何的中段 2048 扇区（4 MiB）、24–58 mm、37.25°，系数 `(0.24, -0.18, 0.08)`。短任务受线程池与处理器调度影响，这些数字仅是该 fixture 的测量值。

几何表在生成前构建并复用，热循环不做数值积分。BD 对受支持的轨距形状使用整段插值曲线的导数与舍入误差界判断像素复用；超出误差预算时回退逐点采样。零系数仍走原路径；非零路径在 8K 像素边界、白边重入、极小系数与复用范围边界均逐字节对照标量输出。

构建后可单独复现测速（不要与回归测试并发执行）：

```powershell
dotnet tests/DiscImageStudio.ArchitectureTests/bin/Release/net10.0-windows/DiscImageStudio.ArchitectureTests.dll --benchmark-cd-pitch
dotnet tests/DiscImageStudio.ArchitectureTests/bin/Release/net10.0-windows/DiscImageStudio.ArchitectureTests.dll --benchmark-bd-pitch
```

2026-11-04 进一步检查了全部产品生成入口：

| 入口 | 生成路径与边界 |
| --- | --- |
| CD 图片导出、音轨直接刻录 | 共用 `GenerateToStream`、分块并行及四点 SIMD；交织保持有序 |
| CD Mode 1 文件盘、流式刻录 | 共用数据生成器、分块并行及四点采样，保留 CIRC 物理位置与文件区域 |
| DVD GUI/产品 CLI、普通/混合 ISO、直接刻录 | 统一快速 dispersion；自动分块并行，非零轨距启用四点 SIMD |
| BD GUI/CLI 导出、普通/混合 ISO、直接刻录 | 同一 raster mapping，保留整簇并行和安全像素复用 |
| CD/BD 固定参考 PNG 精细标定：导出及专用刻录 | 已补齐图片快速入口；CD 保留旧记录像素约定，BD 当前记录使用 raster 整簇复用，并保留环带裁切与极性 |
| 宽地址带、角度扫频标定 | 使用按逻辑地址选纹理的专用路径，无需逐点计算盘面几何 |
| 解析图案、自定义采样函数 | 冻结的内置 CD 标定图案分块并行；任意外部回调保留已有顺序/明确允许的并行语义，无法证明同像素时仍逐点调用 |

独立 `DvdImageSolver` 研究 CLI 的 `full` 或显式关闭 fast output 会执行 ECC/EFM/DSV 重放，是不同计算任务。GUI 和产品 `DvdDiscModule` 不会误入该路径；新增测试验证默认产品模块与快速 CLI、直接流式生成的字节一致及快速模式元数据。

CD 分块并行接入前的补测采用同机、同一张 4096×4096 不透明黑白图、各介质标准全盘几何、从盘首生成 64 MiB、各模块当时默认并行/交织、旧式二值影调。先预热一次、再取三轮中位数，从第一次输出写入开始计时，排除图片解码、几何初始化及磁盘写入；三次系数均为 `(0.00012, -0.00008, 0.00004)`：

| 介质 | 恒定轨距 | 三次轨距 |
| --- | ---: | ---: |
| CD 音轨，启用交织 | 46.04 MiB/s | 25.43 MiB/s |
| DVD | 77.77 MiB/s | 118.13 MiB/s |
| BD | 416.19 MiB/s | 503.63 MiB/s |

这些样例并非所有图片的速率保证：BD 支持同像素复用且单位输出字节的轨道长度较短；CD 仍有逐字节处理和顺序交织；DVD 灰度/透明图片还需保留双线性采样。相同图片下各介质的像素坐标约定、编码位置及影调语义也不同。

另以原始 2400×2400 标定参考图、同样的小幅三次系数、盘首 16 MiB 做修正前后对照（一次预热、三轮中位数）：CD 固定 PNG 入口由 14.19 提升至 16.01 MiB/s，BD 由 68.26 提升至 199.94 MiB/s。两者均与原函数采样逐字节一致。CD 覆盖新旧记录、端序、交织及像素边界；BD 覆盖首末扇区、径向边界上下一个浮点间隔、极性及 UDF 内外圈文件区域。sRGB 转换另使用 256 项精确 double 表，避免逐载荷字节计算 `Math.Pow`，所有灰度值与亮度边界均对照原算式验证。

本轮入口修正后，Release solution 构建为 0 警告、0 错误；18 组受影响回归及 DVD 引擎 selftest 全部通过，覆盖 CD 图片/数据盘/模拟刻录、DVD 产品模块/文件/流式一致性、BD 参考图/并行/混合文件区、影调及标定导出/刻录工作流。未执行实盘刻录。

DVD 采样复用接入前的标定补测使用同一张 2400×2400 原始参考图及相同 16 MiB 条件，直接调用产品导出/刻录共用的 `DvdStreamingGenerator.GeneratePattern(target, ...)`：恒定轨距 56.58 MiB/s，三次轨距 79.99 MiB/s（三轮分别为 77.28、79.99、86.77）。当时该路径已有分块并行及三次几何 SIMD，但每点仍执行三角函数、环带裁切和双线性参考图采样，与普通图片的 118.13 MiB/s 测试不是同一场景。

2026-11-04 CD 分块并行补测：沿用上述机器，普通图片和 Mode 1 使用同一张 4096×4096 黑白图，标定使用原始 2400×2400 参考图。均为完整 359847 扇区几何、24–58 mm、1200 mm/s、起始角 0、旧式二值影调；音轨启用交织并输出大端，三次系数仍为 `(0.00012, -0.00008, 0.00004)`。各项顺序执行，一次预热后三轮中位数。串行与并行两边均开启 SIMD，区别仅为分块并行开关。

本组所有场景均生成盘首 64 MiB，计时包住整个生成器调用，包含图片解码、生成器初始化、首批填充及最后一批多计算的部分，不包含磁盘写出或真实刻录。它与前述从首次写入开始计时的样例采用不同口径，尤其不能直接拿前述 16 MiB 标定速率作本组基线。

| CD 路径 | 恒定轨距：串行 → 并行 | 三次轨距：串行 → 并行 |
| --- | ---: | ---: |
| 普通图片音轨 | 43.02 → 148.80 MiB/s | 25.15 → 108.01 MiB/s |
| 固定参考图标定音轨 | 42.66 → 154.36 MiB/s | 23.68 → 114.00 MiB/s |
| Mode 1 文件盘 | 32.09 → 133.66 MiB/s | 20.78 → 103.94 MiB/s |

图片、冻结内置标定目标及 Mode 1 绘图区默认共用有界分块管线；每块 32 扇区，最多 16 个工作线程和两倍线程数的缓冲块，音轨缓冲上限约 2.30 MiB，Mode 1 为 2 MiB。交织状态跨块连续，写出与进度仍按原顺序执行；自定义回调与有状态的逻辑地址采样保持原语义。这些测试值不保证所有图片、轨距参数、影调设置和处理器都能达到 100 MiB/s。

并行改动后 Release solution 构建通过（0 警告、0 错误），新增音轨/Mode 1 并行检查及另外 25 组相关回归全部通过。检查涵盖块/批次边界和尾块、逐字节及进度一致性、交织/端序、影调、内外圈文件区、内置与外部采样、取消、输出异常及有界工作线程生命周期。未执行实盘刻录。

构建后可复现这一组串行/并行对照：

```powershell
dotnet tests/DiscImageStudio.ArchitectureTests/bin/Release/net10.0-windows/DiscImageStudio.ArchitectureTests.dll --benchmark-cd-parallel
```

2026-11-04 DVD 标定与指定图片的采样复用补测：同机、完整 2297888 扇区几何、24–58 mm、133.3 nm、起始角 0、默认并行及旧式二值影调，三次系数 `(0.00012, -0.00008, 0.00004)`。标定直接调用 `DvdStreamingGenerator.GeneratePattern(target, ...)`，图片调用 `Generate(imagePath, ...)`；开关 `UseSampleReuse` 分别选择原路径与新路径。

每项先完整预热一次，再测三轮取中位数，均生成盘首 64 MiB；计时包含整个生成器调用、图片解码、初始化和首尾批次工作，不含磁盘写出或实盘刻录。参考图为固定 2400×2400 原图，指定图片为 4096×4096 黑白图及彩色/半透明渐变图，具体像素公式保存在测速入口中。

| DVD 路径 | 恒定轨距：原路径 → 优化后 | 三次轨距：原路径 → 优化后 |
| --- | ---: | ---: |
| 固定参考图标定 | 60.10 → 215.97 MiB/s | 76.76 → 206.77 MiB/s |
| 指定黑白图片 | 58.26 → 150.74 MiB/s | 94.95 → 160.20 MiB/s |
| 指定彩色/半透明渐变图 | 44.39 → 46.44 MiB/s | 59.57 → 58.39 MiB/s |

标定与黑白图样例超过 100 MiB/s；彩色/半透明渐变样例没有明显收益，仍以原计算为主。不能将这组数据当作所有图片的最低速率。新路径只复用已证明取整结果相同的灰度与透明度，保留原双线性插值、环带裁切和逐字节抖动/扰码；轨迹方向、曲率、数值误差和像素边界共同限定复用距离。连续无效探测后短暂回退原 SIMD，并在后续位置重新尝试，避免单个边缘停用整块加速。

最终 Release solution 构建通过（0 警告、0 错误）；16 组相关回归与 DVD 引擎 selftest 全部通过。新增测试逐字节对照原采样路径，覆盖首/中/末盘面、恒定/三次轨距、透明度与灰度舍入边界、标定环带边界、复用起停、尾批、影调、取消及内外圈混合文件；测试还确认正常几何确实启用复用，而非仅验证回退路径。未执行实盘刻录。

构建后可复现这组对照：

```powershell
dotnet tests/DiscImageStudio.ArchitectureTests/bin/Release/net10.0-windows/DiscImageStudio.ArchitectureTests.dll --benchmark-dvd-sampling
```

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

- CD/DVD/BD 三次轨距：独立数值积分的正反向几何、从恒定与非零生成系数开始的重复拟合、已校正直线保留系数、预设/工程/会话往返，以及粗标定只更新半径而保留精细轨距。对应 [#78](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/issues/78) 和 [#92](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/issues/92) 的软件回归，未替代实盘验证。
- CD/DVD：黑白映射、抖动、sRGB 伽马、文件与流式一致性、SIMD 与标量采样一致性。
- CD 文件模式：ISO9660/Joliet 中文文件独立读取、内外圈位置及工程保存、文件区预览、原始扇区几何、文件与前向流输出一致。
- CD 数据 DAO：Mode 1 EDC 与全部 P/Q 校验方程、主通道字节序、导入区及预间隙地址、数据轨 Q 标识与 CRC、前向 Seek 和短读、内外圈 ISO 完整还原；保留铼德预设 359,845 扇区，并使用模拟刻录机验证与音轨共用的容量限制。原生 IMAPI 测试只构造内存镜像，不创建刻录设备。
- BD：17PP/EDC/Reed-Solomon、3T/6T 映射与极性、绘图范围清零、UDF 2.01、大于 4 GiB 文件、sidecar 往返与无效边界拒绝。
- DVD/BD 文件位置：默认内圈兼容、外圈 ISO9660/Joliet 与 UDF 目录独立解析、中文文件内容、完整盘面坐标、不可 Seek 流与文件输出一致、容量及取消保护，以及工程中的位置选项往返。
- 三态模型：工程 schema 2 往返与 schema 1 升级、预设 schema 4 与旧 schema 合并、`ProjectSession` 的 BD apply/capture。
- 刻录：操作互斥、进度、顺序写入、超时、取消和资源清理，均使用模拟接口而非真实刻录机。
- 流式刻录显示：仅设备确认写入量推进进度，CD 音轨／数据、DVD、BD 倍速换算，未知或无效速率、3 秒过期、封盘与结束清理，传输日志节流及迟到事件保护。
- 标定刻录来源：CD/DVD/BD 标签与保存参数一致，BD 位长、极性和绘图范围，跨盘型切换、速度查询，以及模拟刻录请求和确认文案。
- 标定：三次轨距、BD 绘图范围与极性、照片矫正、曲线拟合、会话保存恢复、盘型预设同步与参数精度。
- 预览与窗口：CD/DVD/BD 投影与读回、多显示器和任务栏边界。
- 文件区预览：内外圈设计投影和带 sidecar 的成品读回标黄、DVD 三次轨距边界、纯绘图及无效布局信息不标黄、错误目录/容量提示、切换与重置清理。黄色叠加不改变 ISO 数据。

GUI 可通过现有命令离屏生成截图：

```powershell
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/home.png --tab 0
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/export-bd.png --tab 3 --project sample.discproj
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/preview-bd.png --tab 2 --project sample.discproj --live-input image.png --live-disc bd --live-mode calibrate
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/burn-bd.png --tab 4 --project sample.discproj --burn-source calibration --burn-progress write
```

`--burn-progress` 接受 `write`、`buffer`、`finalize`，使用模拟进度生成界面，不启动实际刻录。2026-10-28 核对了 BD 标定宽屏与 800×650 窄窗口、准备和封盘状态；底部写入量、速率、倍速与进度条均可见。此次另通过 DVD 引擎自检，没有执行实盘刻录。

`build` 工作流另执行模块目录的 DVD 自检、两扇区 CD 输出检查和主页截图。发布单文件 EXE 的附加检查见[发布指南](RELEASING.md)。

本次另核对了 DVD/BD 各两种文件位置的设计投影、外圈导出页与外圈成品读回，共 8 张离屏截图；DVD/应用自检及发布工具的 45 项回归通过。

设计页文件选择入口另检查了 DVD、BD、CD、未选文件夹、18 个图层、窄窗口及低窗口高度 7 种界面状态；宽屏图层列表内部滚动，文件选择区域保持可达，构建为 0 警告、0 错误。

v1.3.0 另通过 `Build-Release.ps1` 的 .NET 10 自包含单文件验证：隔离目录仅放 EXE，刻录接口构建信息、DVD 自检、两扇区 RAW/WAV/CUE、关闭 CUE 及 WPF 界面渲染均通过。最终内置 .NET / Windows Desktop 10.0.11，并同步保留其完整上游许可声明。

v1.3.2 通过完整架构回归、DVD 引擎及应用模块自检、发布工具 45 项检查。新增回归验证蓝光切换预设后另存与工程确认的极性一致，以及删除最近预设后跨盘型选择与几何同步；画布缩放覆盖 25%–1200% 边界、加减按钮和适应窗口重置。另核对新建 CD、蓝光、DVD 工程设置及 18 图层设计页共 4 张离屏截图。

## 实盘验证边界

此次文件位置与预览改动没有执行实盘刻录。自动化测试不证明设备写入策略、实际反射亮度或刻录成功率；蓝光功能仍为实验性。

v1.3.1 的 CD 数据盘 DAO 也尚未执行实盘刻录；已经通过完整架构回归、独立单文件 EXE 启动、音轨/CUE、内外圈 ISO 文件内容与成品读回验证。

已经记录的 DVD 三次轨距实拍与参数见 [README 中的前后对比](../README.md#辅助标定展示)。物理模型历史审计见 [PHYSICAL_MODEL_AUDIT.md](PHYSICAL_MODEL_AUDIT.md)，其中结论保留其标明的日期与代码基准，不能代替当前源码验证。

后续实盘记录应包含版本或提交、刻录机和固件、盘片型号、预设参数、速度、生成/刻录入口与原始照片，避免把某一设备的结果泛化到所有盘片。
