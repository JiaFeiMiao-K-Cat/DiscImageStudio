# 蓝光模式（单层 BD-R）

蓝光实现位于 `src/DiscImageStudio.BluRay`，引用介质无关的 Core 和 `src/DiscImageStudio.Iso`，不依赖 CD/DVD 编码器。依据 ISO/IEC 30190:2021，当前范围为 **120 mm、单层 25 GB BD-R**；不支持双层、BDXL、BD-RE 或视频蓝光制作。

## 在应用中的位置

蓝光不是第六个工作区，而是工程的第三种盘型（CD / DVD / BD）。打开 BD 工程后：

- **设计**：画布与合成图按 BD 生成外半径布局；左栏“随盘文件”可选择文件夹及内圈／外圈位置，与导出页同步；右栏影调模块支持 2 级、sRGB 线性化、亮度与 3T/6T 极性。
- **预览**：设计投影按生成/实测几何、影调与极性渲染 3T/6T 类别图；成品读回直接分类 ISO 的 AA/44 负载字节，不套用极性，显示的是类别图而不是反射亮度。
- **导出**：生成完整 BD ISO（纯绘图或 UDF 2.01 文件）；选择文件夹后可将文件放在内圈或外圈，默认内圈。位置随工程保存，并在同目录的 `<iso>.json` sidecar 中记录。
- **刻录**：把同一 ISO 流直接送入 MMC 顺序写入，不保存完整镜像；任务坞支持进度与取消。
- **标定**：BD 目标覆盖完整数据区，保存扇区、半径、极性、绘图起止与 ISO 时间戳；拟合使用恒定轨距并回到 BD 参数。

BD 预设与 CD/DVD 一起存放在 `disc-presets.json`（schema 4）的 `bluRayPresets` 中。内置“BD-R 单层 25 GB”和“铼德 BD-R 10x”两条；用户预设可在工程设置对话框底部保存。

## 容量与几何

- 单层容量 12,219,392 个 2048-byte 用户扇区，即 25,025,314,816 bytes；32 扇区组成 65,536-byte cluster。
- 工程分区的扇区数必须是 32 的正倍数且不超过单层容量；外半径不超过 60 mm。
- 生成与刻录共用总扇区数；刻录始终声明完整盘容量，文件与元数据之外的未使用扇区为零负载。
- 名义轨距 0.320 µm，通道位长 74.5 nm，起始角 0°。

## 生成与 ISO

`BluRayIsoGenerator` 先规划 UDF 2.01 目录和文件 extent，再分配 32 扇区对齐的绘图范围。内圈模式将文件分区放在前部，外圈模式将文件分区放在盘尾保留区之前；前部识别信息、锚点以及尾部备份信息仍保留在规定位置。输出长度固定为 `TotalSectors × 2048`。

- 未选择文件夹：完整数据区纯绘图，不含文件系统。该 `.iso` 可由本程序回读，但普通文件系统工具不能浏览。
- 选择文件夹：写入 UDF 2.01，支持大于 4 GiB 的单文件；`--file-position inner|outer` 选择文件位置，缺省 `inner`，`outer` 必须配合 `--data-dir`。文件、填充、图案与盘尾元数据在同一前向流中写出。
- 输出路径必须在源文件夹之外，且不能覆盖源图片。取消或失败保留已有 ISO。
- 生成成功后写出 `<iso>.json`（`format: bd-r-image-v1`）：总扇区、内外半径、绘图起止与半径、3T/6T 极性、`ToneProfile`、卷标、UDF 时间戳、源图与生成时间。预览选中该 ISO 时自动恢复这些参数；没有 sidecar 的外部 ISO 仍可按当前工程几何读回。

文件使用普通用户字节，不能进行图案反扰码；图案继续使用原始整盘扇区地址和全盘几何，不将剩余图案区重新拉伸到整盘。UDF 分区只覆盖文件系统所在区域，图案不被标为文件系统的可用空间。实际可绘图区由文件占用与盘尾备份区共同决定。GUI 的设计投影和带 sidecar 的成品读回会把文件区（含文件系统）显示为黄色，并附图例；没有布局信息的外部 ISO 不推测文件范围。

`BluRayIsoGenerator.Prepare` 先扫描文件夹、规划 UDF 2.01 目录和文件 extent。内圈布局将文件末尾向上对齐到 32 扇区，外圈布局将文件分区起点向下对齐到 32 扇区。盘尾保留 288 扇区，用于备份卷描述符及锚点。该文件系统 API 未指定文件夹时仍保留空卷；必须至少剩余一个完整绘图簇，最小测试容量为 640 扇区。

文件/流式共用前向写入路径。规划后源文件或目录内容发生变化时会拒绝继续使用过期布局。文件长度采用 64 位字段，大文件拆成多个不超过 `0x3ffff800` 字节的分配 extent，使用 128 KiB 缓冲顺序读取；单文件大小由盘片剩余容量限制，不再受 4 GiB 限制。DVD 的 ISO9660/Joliet 路径保持不变。

UDF 实现依据 [ECMA-167](https://ecma-international.org/publications-and-standards/standards/ecma-167/) 与 [ECMA TR/112-4（UDF 2.01）](https://ecma-international.org/wp-content/uploads/ECMA_TR-112-4_1st_edition_december_2023.pdf)，写入封闭只读卷。它用于数据盘，不提供视频蓝光制作。

## 影调与极性

BD 的影调在源图分辨率一次性完成：sRGB 线性化 → 亮度偏移 → 字节级抖动 → 128 阈值二值化。生成、刻录和预览共用这张二值栅格，不对 250 亿量级的 payload 字节逐个哈希。

`BluRayRunLengthMapping` 只做最后一步：暗部写偏 3T 数量的 `AA`，亮部写偏 6T 数量的 `44`；`InvertMapping` 交换两端。它是图像映射约定，不承诺纯 3T/6T，也不代表实测光学极性。实际明暗方向需通过标定盘观察；极性字段独立于 `ToneProfile` 保存。

## 3T / 6T 数量映射与扩展

图片链路使用 `BluRayImageLoader` 解码并合成透明白底，再通过 `BluRayImagePayloadMapping` 取样。图片保持宽高比，最长边对应盘片外径，空白区域为白色。取样位置包含每帧 EDC 占位、LDC 交织、BIS 插入、DC 位、同步和 run-in；由 2:3 调制率计算的是名义 byte 中心，不是变长 17PP 码字的精确边界。

内建 `BluRayRunLengthMapping` 先以 128 分界，再返回扰码域 `AA` 或 `44`。选择依据是同样 958,272 channel bits 的完整簇中，相邻 modulation ONE 间隔为 3T / 6T 的次数，mark 与 space 都计入，不是某一种电平的占空比。基准 PSN `0x100000` 的 256 重复字节扫描中，`AA` 有 265,136 个 3T，`44` 有 116,575 个 6T，分别为目标次数最高；四地址复核仍保留强偏向，但不保证每个地址均排名第一。细节见[字节分析](BLURAY_BYTE_DENSITY.md#按-3t--6t-数量接入绘图)。

实现 `IBluRayLuminanceMapping.Map` 可以替换策略，为源图亮度和名义 byte 位置返回所选扰码域 byte。对非图片实验，还可以直接实现 `IBluRayPayloadMapping.FillScrambledPayload`，为扇区的 2048 个用户 byte 位置填入数据，再交给 `BluRayStreamingGenerator`；回调接收名义扇区、PSN 和盘片参数。

`BluRayDiscModule` 通过构造器注入图片解码委托，保持不依赖 WPF；App 的 CLI 使用 `BluRayImageLoader.Load`。直接使用模块 API 生成图片时也应提供该委托。`BluRayConstantByteMapping` 仍只用于显式数字实验。图片与标定都通过 `BluRayIsoGenerator` 生成文件或向刻录器供流。

候选筛选见 [字节与位周期密度分析](BLURAY_BYTE_DENSITY.md)。`tools/BluRayByteProbe` 可重现全部 256 字节和细粒度周期的完整簇统计；周期位串属于其他实验策略，当前数量映射采用重复字节。分析结果仍未指定实盘黑白极性。

## 命令行

```powershell
# 查看单层盘参数，或导出 JSON
dotnet run --project src/DiscImageStudio.App -- bd-info --output bd-parameters.json

# 纯绘图 ISO（1024 扇区仅用于快速功能验证，不是实际整盘配置）
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-test.iso --total-sectors 1024 --invert true

# 含 UDF 内圈文件的完整容量 ISO
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-image.iso --data-dir C:\Files --volume-label BD_IMAGE

# 影调：抖动、亮度、sRGB 线性化
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-tone.iso --tone-dither 0.75 --tone-gamma true --tone-brightness -4

# 原始负载探针与单个 ECC 簇诊断
dotnet run --project src/DiscImageStudio.App -- bd-probe --byte 0xA6 --sectors 32 --output bd-probe.bin
dotnet run --project src/DiscImageStudio.App -- bd-encode --input bd-probe.bin --psn 0x100000 --output bd-ecc.bin
```

`bd-generate` 的 `--preset` 可选 `bd-r-sl-25gb` 或 `bd-r-ritek-10x`；`--total-sectors`、`--r0`、`--r1`、`--start-angle` 可覆盖对应值。`--invert` 接受 `true`/`false`。`bd-generate` 不再接受旧的 `--start-sector` / `--sectors` 切片参数；切片仅保留在 `bd-probe`。

`bd-probe` 的 `--byte` 为 0..255 或 `0x00`..`0xFF`，范围按 32 对齐。`bd-encode` 输入必须恰好 65,536 bytes，输出 76,880 bytes 的 ECC cluster，不含 17PP、DC-control 或文件系统，不能当作可刻录镜像。

## 编码链

| 部分 | ISO/IEC 30190:2021 |
| --- | --- |
| 2048-byte payload + 4-byte EDC | §13.2–13.3 |
| 16-bit LFSR，PSN bits 19..5 派生种子；每个 data frame 复位 | §13.4 |
| LDC RS(248,216)，304 个码字及交织 | §13.5–13.8 |
| 地址、RS(9,5)、BIS RS(62,30) 及复用 | §13.9–13.14 |
| 17PP、DC-control 与同步帧 | §13.15–13.17 |
| Recording unit spacing | §14 |
| 名义轨距 0.320 µm | §15.4.3 |
| 单层 PSN 0x00100000..0x00CA73FF | §17 |
| 单层数据区 381,856 clusters | §19 |

图案测试写入的是**反扰码后的用户 payload**。实际驱动器会计算 EDC，再对完整 2052-byte data frame 扰码；EDC、纠错、地址、同步等数据不能被指定字节探针任意覆盖。

`BluRayRecordingFrameEncoder` 接受调用者明确提供的 28 个 DC 控制位。`BluRayRecordingUnitEncoder` 另外提供包含 run-in、496 帧、run-out 和可选 Guard3 的诊断模拟，每个 modulation bit / NRZI level 用一个 byte 表示。自动策略明确命名为 `BluRayGreedyDcControlEncoder`：使用尚未决定的后续位为 0 的前视贪心选择，后续控制位定案后可能改变之前的局部最佳选择；返回的 `SuboptimalDcControlDecisions` 会报告这种情况。该策略用于实验，不保证标准要求的最终 DC 最优性，也不承诺与刻录机的实际比特序列一致。CLI 的 `bd-encode` 只导出确定的 ECC 阶段，不把这项诊断模拟作为可刻录镜像输出。

`BluRayTrackGeometry` 使用独立恒定轨距螺旋，以手动总扇区对应的总轨长和内外半径求有效轨距；最后扇区到达设置的外半径。从数据面看，固定盘片坐标的取样轨迹顺时针向外（§9.8、§15.3）；屏幕转换只反转一次 Y 轴。每个 recording unit 的跨度是 `2760 + 496 × 1932 + 1104 = 962136` channel bits，位长 74.5 nm。`bd-probe` 的局部范围沿用完整总容量的几何。序列尾部 Guard3 不包含在每个 recording unit 的重复跨度中。

默认 24–58 mm 是名义几何，不是特定盘片的实测参数。内置流式刻录通过保持 BD-R 未格式化、不分配缺陷管理备用区来匹配 `sector → PSN` 模型。实际半径、位置与光学对比度仍需实盘标定；外部软件刻录 ISO 时也应采用无备用区方式。

## 无备用区流式刻录

`IDiscFormat2Data` 对未格式化 BD-R 报告的可用容量可能是默认备用区方案的 11,826,176 扇区，比原始 12,219,392 扇区少 768 MiB。蓝光通道通过 `IDiscRecorder2Ex.SendCommandSendDataToDevice` 从 LBA 0 顺序发送 `WRITE(10)`，每次 32 扇区（64 KiB），单命令超时为 200 秒，给首写初始化留出等待时间。缓冲区遵循适配器的地址对齐和最大传输限制。此通道不调用数据刻录接口的 `Write`、`FORMAT UNIT` 或分配备用区的操作。

公开的 `IWriteEngine2` 接口没有可设置的命令超时属性，因此查询、刷新缓存的超时设置无法延长 `WriteSection` 内部的等待时间。当前显式命令通道会报告失败命令的 LBA、扇区数、实际耗时、HRESULT 和 Sense。超时意味着设备端结果可能不确定，程序不会自动重发该范围。

失败日志还保留最近 8 次成功写入的时序，分别统计源流读取等待、设备命令等待及累计确认的数据量。无有效 Sense 时明确显示“未返回有效的设备 Sense 数据”；全零返回不能解释为盘片没有错误。`0x8007048F` 表示 Windows 报告设备未连接。如果标注 200 秒超时的命令在几毫秒内失败，随后设备重新枚举，需要调查设备连接、固件及 USB 事件，不能只按等待时间不足处理。所有本实例的设备查询和刻录排队执行，避免尚未结束的查询与写盘重叠。

在独占锁定盘片后，程序用 MMC 读取当前 profile、盘片信息、格式容量和轨道信息。必须是空白、未格式化的单层 BD-R SRM，首轨从 0 开始，原始可写容量完整；未知、已使用或已格式化的布局均拒绝，不自动降低总扇区数。界面设置的总扇区数按 2048 字节换算为同一请求长度，UDF 前缀、绘图区和尾元数据全部包含其中；较小的手动容量仍写入相同数量的扇区。

写入完成后刷新缓存、关闭轨道，并用 BD-R 的 finalize 功能 6 封盘。取消在当前设备命令结束后生效；取消或写入失败只尽力刷新缓存和释放设备，不把未完成镜像作为成功产物封盘。各项解锁仅在成功后标记为完成，失败项会再尝试一次，错误也会保留在诊断信息中；已成功的计数配对操作不重复执行。进入封盘后等待命令完成。无备用区模式没有可用于坏块重映射的备用容量；中断后同一盘片通常不能继续写入。

BD 实时速率按刻录机确认接收的写入命令计算，包含设备缓存的影响，不等于激光在盘面上的瞬时速度；最终成功仍以刷新缓存及封盘完成为准。4× 的标称用户数据速率为 18 MB/s（1× = 4.5 MB/s），界面采用十进制 MB/s，不能用请求倍速代替实际吞吐；自动模式仍选择当前盘片报告的最低配置。

写入前立即记录设备返回的设置速度；设置值与实时吞吐分别保留。写入过程中每 10 秒及最后一块后记录一个诊断窗口，区分源流读取用时（含等待和拷贝）与 WRITE 命令用时（含传输及设备等待），并记录确认接收的平均速率。低频日志不增加设备查询、不改写进度或刷新速率的有效期；源流读取用时不能当作生产线程的纯计算耗时，命令用时也不能单独区分 USB 传输与盘面写入。

图片供流采用最多 8 个工作线程、16 个簇（1 MiB）的有界批次，完成计算后按原顺序写出。内建 3T/6T 阈值映射及扰码 XOR 使用整数 SIMD，位置计算保留原精度；不再为每个字节重复检查已经验证的盘片参数。不可变栅格图自动启用并行，内建标定图案显式声明采样线程安全；自定义采样回调默认保持串行。预缓冲只能暂时吸收速度差异，持续刻录仍要求供流速度高于所选设备速度。

协议和工作流测试只使用假设备及 1024 扇区（2 MiB）的 UDF 数据流；完整 12,219,392 扇区仅验证容量与几何元数据，不生成全盘 ISO。物理刻录结果仍需实盘验证。

实现依据：[微软发送数据与命令超时接口](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-idiscrecorder2ex-sendcommandsenddatatodevice)、[libburn MMC 实现](https://dev.lovelyhq.com/libburnia/libburn/src/branch/master/libburn/mmc.c)、[libburn 写入超时](https://dev.lovelyhq.com/libburnia/libburn/src/branch/master/libburn/spc.h)。

## 验证范围

自动检查覆盖 17PP/EDC/Reed-Solomon、完整 RUB 重放、图像映射、并行生成、ISO/UDF、大于 4 GiB 文件、顺序刻录命令、绘图范围、sidecar 与取消。落盘 UDF 测试仅 1024 扇区，单个 ISO 最大 2 MiB；大文件测试先启用并验证稀疏属性，再创建超过 4 GiB 的逻辑源文件（实际分配小于 1 MiB），向校验后丢弃数据的流顺序输出，不写大 ISO。真实盘容量只做数学验证。测试不访问真实刻录设备，也不证明实际盘面的可见亮度或极性。
