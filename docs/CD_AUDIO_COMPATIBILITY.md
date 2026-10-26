# CD 音轨格式、字节序与重影排查

## 随盘文件与数据盘

CD 工程在设计页或导出页选择随盘文件夹后，自动切换为 ISO9660/Joliet 数据盘，输出后缀切换为 `.iso`；清空文件夹路径后恢复 WAV 音轨。文件位置可选内圈或外圈，默认内圈，设置随工程保存。文件系统元数据仍位于盘首，外圈模式把文件内容放在盘尾，绘图保持原有的整盘坐标，不挤压到剩余区域。

```powershell
DiscImageStudio.exe cd-generate --input picture.png --output cd-image.iso `
  --data-dir "D:\随盘文件" --volume-label CD_IMAGE --file-position outer
```

ISO 长度为 `扇区数 × 2048`，按 CD-ROM Mode 1 数据盘刻录。程序内即时刻录使用相同的 ISO 数据流；外部刻录软件应选择刻录镜像功能。音频交织开关和 CUE 只用于音轨模式。数据盘的绘图区单独补偿 CD-ROM 扰码及位置映射，随盘文件保留原始内容。

CD 数据盘即时刻录与音轨共用 DAO 会话和 lead-out 边界检查，保留 ATIP 标定的扇区数及完整文件布局。程序将 ISO 负载编码为 Mode 1 原始扇区，并设置数据轨标识；使用铼德医疗水蓝盘预设时仍写出 359,845 个数据扇区，不按 TAO 接口较小的可用容量缩短图案或文件。DAO 沿用音轨模式最多 3 扇区的受限 overburn 检查，超限会在写入前报错。

数据轨使用 IMAPI 小端主通道约定：在标准 Mode 1 扇区上补齐 EDC、P/Q 纠错码并扰码，保持同步头 `00 FF…FF 00` 的字节顺序。CD-ROM 物理编码的相邻字节交换与转为小端的交换相抵消；这一点也与 cdrdao 的 [RAW 编码和设备字节序转换](https://github.com/cdrdao/cdrdao/blob/master/dao/dao.cc) 相符。文件内外圈的 ISO 布局和绘图物理坐标映射不随该传输约定改变。

设计投影、影调小预览和带有效 `.iso.json` 布局信息的成品读回以黄色标出文件区（含文件系统）。同名元数据记录几何、影调和文件位置；移动 ISO 时建议同时移动它。预览显示绘图负载的映射，实盘效果仍需刻录验证。

## 推荐用法

新生成任务默认输出 `cd-track.wav`，同时生成 `cd-track.cue`。WAV 为无压缩 PCM：44.1 kHz、16 位、双声道，数据区为小端。将这两个文件放在同一目录，移动或改名时也要同步更新 CUE 中的文件名。CUE 是可选项：导出工作区取消勾选“同时导出 CUE 文件”，或命令行传 `--cue false`，则只写出 WAV。

| 刻录入口 | 使用方式 |
| --- | --- |
| cdrecord | `cdrecord dev=设备 speed=速度 -audio "cd-track.wav"`，不要额外添加 `-swab` |
| ImgBurn | 在“Write image file to disc”中打开 `cd-track.cue` |
| 程序内流式刻录 | 自动生成同样的小端 PCM 数据，不包含 WAV 文件头，不生成完整临时音轨 |
| 旧 RAW 工作流 | 输出仍可选择 `.raw`；`cdrecord -audio "cd-track.raw"` 不加 `-swab` |

输出后缀决定真实格式，只接受 `.wav` 和 `.raw`。不能通过重命名把旧 RAW 变成 WAV；必须重新生成。WAV 的配套 CUE 使用 `FILE "cd-track.wav" WAVE`，不使用 `BINARY`。Unicode 文件名使用带 BOM 的 UTF-8 CUE，支持空格和中文。ImgBurn 作者曾验证这种 CUE 的 Unicode 文件名支持，并指出早期 2.5.0.0 的相关问题在 2.5.1.0 修复。[ImgBurn 作者说明](https://forum.imgburn.com/topic/13526-unicode-filename-problem-in-cue-file/)

命令行生成示例（几何参数应使用自己的实测设置）：

```powershell
DiscImageStudio.exe cd-generate --input picture.png --output cd-track.wav --interleave true
```

`--cue`（缺省 `true`）控制 WAV 输出是否同时写出同名 CUE；`.raw` 输出始终不写 CUE。

WAV 的文件长度为 `扇区数 × 2352 + 44`；音频长度、生成进度和流式刻录容量仍为 `扇区数 × 2352`。文件头不占盘片音轨扇区，也不改变图案几何。

## 影调（抖动、sRGB 线性化、灰阶与亮度）

CD 的每个音频字节对应约 6.8 µm 的盘面长度；正常观看距离下，眼睛会把成百上千个字节积分成一个色调。因此“每个字节只有黑白两种图案”不等于“画面只有黑白两色”，中间调靠相邻字节的黑白比例还原。

| 参数 | 含义 |
| --- | --- |
| 抖动强度 0–100% | 0% 按 128 的硬阈值输出（旧行为）；100% 逐字节在相邻档位间按比例选择，中间调由黑白字节的空间比例形成 |
| 灰阶档位（仅 CD） | 2 级用 `0x0D` / `0xAA`；4 级用 `0x0D` / `0x10` / `0x63` / `0xAA`（`0x0D` 为黑、`0xAA` 为白） |
| sRGB 线性化（伽马） | 先按 sRGB 传递函数解码再抖动；改变黑白点密度，不改变输出电平 |
| 亮度 | 整体平移灰阶，范围 −64…+64（0..255 单位） |

命令行参数（`cd-generate` 与 `cd-preview-warp` 都接受）：

```powershell
DiscImageStudio.exe cd-generate --input picture.png --output cd-track.wav `
  --tone-dither 1 --tone-levels 2 --tone-brightness 0 --tone-gamma false
```

缺省值是 `--tone-dither 0 --tone-levels 2 --tone-brightness 0 --tone-gamma false`，与旧版逐字节一致。新建工程在设计页右栏的「影调」模块里默认 75% / 2 级；没有 `tone` 节的旧工程按 0% 载入，保存后写入 `tone` 节。导出页只保留输出选项，并在输出卡内提示影调归设计页。

抖动相位由**逻辑字节索引**决定（`globalByte`），而不是屏幕坐标。延迟交织把字节在文件中挪位后，读回预览仍按同一逻辑索引还原，因此生成、标定投影和读回看到的是同一套图案。标定图案（`GeneratePattern`）固定使用硬阈值，不参与抖动。

音轨旁的同名 `.json` 记录 `toneDitherStrength`、`toneLevels`、`toneBrightness`、`toneSrgbLinearization`；预览页载入外部音轨时会连同几何、交织一起同步这些值。

读回预览默认 1600 px、每扇区 16 个采样，并把落在同一像素的采样**累加平均**。旧版固定 900 px、每扇区 2 个采样且后写覆盖，稀疏采样会在中间调留下白点，看起来像报纸网点；提高采样密度后同一盘面才呈现连续影调。预览页的“设计投影”默认把实测几何设为与工程几何一致，打开工程即得到中性投影；填入实测半径后才会显示两套几何的偏差。

## 已确认的问题

2026-09-12 的用户实盘对照：同一份既有 RAW，用 cdrecord 的 `-audio` 和 `speed` 正常；添加 `-swab` 即可复现重影。这确认了既有 RAW 应按 cdrecord 默认的大端音频输入解释。

此前文件导出和流式刻录共用完全相同的 `GenerateToStream` 输出，流式路径没有执行字节序转换。但不同输入入口的约定不同：

| 输入 | 音频数据的要求 |
| --- | --- |
| cdrecord 无头 `-audio` | 默认大端 |
| cdrecord 无头 `-audio -swab` | 小端；该选项声明需要交换的输入顺序 |
| cdrecord WAV | 解析文件头，自动识别音频字节序 |
| CUE 的 `FILE ... BINARY` | 小端；不能把既有大端 RAW 直接套入 BINARY CUE |
| IMAPI `AddTrack(IMAPI_CD_SECTOR_AUDIO, stream)` | 与 16 位双声道 44.1 kHz WAV 数据区相同的样本布局，即无头小端 PCM |

上述 cdrecord 行为及 BINARY 定义来自 [cdrecord 官方手册](https://cdrtools.sourceforge.net/private/man/cdrecord/cdrecord.1.html)；IMAPI 的音频样本布局来自 [Microsoft 的 AUDIO 扇区定义](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/ne-imapi2-imapi_cd_sector_type)。ImgBurn 作者也提供了成功刻录 `FILE ... WAVE` CUE 的例子。[ImgBurn WAVE CUE 实例](https://forum.imgburn.com/topic/25173-extract-only-session-two-from-audio-cd/page/2/)

这里的 `AudioSectorType = 0` 是 IMAPI 枚举的 AUDIO 值，不是把音轨写成物理 Mode 0。`IRawCDImageCreator::AddTrack` 当前只支持 AUDIO 类型。[Microsoft AddTrack 文档](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-irawcdimagecreator-addtrack)

## 为什么会形成两组轮廓

现有 `CddaInterleaver` 为目标字节执行延迟补偿。交换最终输出每个 16 位样本的两个字节，会把原本属于不同延迟位置的字节交叉送入编码链。因此不能把变化理解为普通音频数值变化，也不能在图像采样之前交换一次来抵消。

针对现有延迟表进行整数标签排列实验，排除头尾填充后，额外的样本字节交换会让逆映射后的偶数位置偏移 `+73` 个目标字节，奇数位置偏移 `-73` 个目标字节。这解释了双轮廓机制；它是程序延迟排列模型的推导，并非完整光驱 CIRC 模型或实体盘测量。

修正保留采样和延迟表，在补偿完成后的 2352 字节扇区输出边界选择字节序：旧 RAW 大端；WAV 和 IMAPI 小端。WAV 文件头解决外部工具对格式和字节序的识别，流式接口仍按其约定只接收数据区。

## 读回必须抵消延迟交织

延迟补偿会把逻辑字节 i 推到文件索引 `i + offset(i % 24)`：交织表按 28×D（D=4）帧的延迟线存储，每帧 24 字节，所以偏移在 72 与 2592 之间（对应帧延迟与帧内列旋转之和）。直接按文件顺序把字节铺到螺旋线上，等于把图案整体旋转并剪切——同一张图在 60000 扇区、900 px 的对照下，约 17.5% 的绘制像素与源图不符，看起来就是"轻微扭曲"。

因此 `cd-preview-track` 默认执行逆映射（`--deinterleave`，缺省 `true`）：逻辑字节 i 取文件索引 `i + CddaInterleaveTable.FileOffsetFor(i % 24)`，需要 2592 字节的前视窗口。该表的正向延迟与 `CddaInterleaver` 共用 `CddaInterleaveTable`，两侧不可能各自漂移。

尾部的代价：延迟线在文件结束时仍持有最后若干帧，这些逻辑字节没有写出（1318–1332 字节，不到一个扇区），读回时只能留空。它位于音轨最外圈，占整条音轨的比例随扇区数下降。

`cd-generate` 现在会在音轨旁写出 `<track>.json`（`format: cd-da-track-v1`，含 r0/r1/扇区数/线速度/起始角/图片外半径/是否交织）。预览工作区在切换音轨时读取它并载入标定几何与交织开关，因此导入其他机器生成的音轨也能按正确模型读回。

## 标定投影：源图片与两套几何

`cd-preview-warp` 是读回路径的快速对照，也是预览工作区“标定预览”模式调用的命令：输入是源图片而不是音轨，参数是两套几何——生成几何决定第 i 个字节从图片的哪个位置取样，实测几何决定这个字节画在盘面的哪个位置（`--actual-r0`、`--actual-r1`，缺省 24.3 / 56.6 mm）。两套几何相同时投影即原图；不同时，图案按实测盘面偏移，这正是半径标定要看的现象。扇区数、线速度、起始角与图片外半径由两套几何共用，后三者固定为 1200 mm/s、0°、57.5 mm。

因为它只读源图片、不扫描整条音轨，改半径或几何后的刷新成本是图片的尺寸而不是音轨的尺寸（80 分钟盘约 845 MB），适合在标定预览里试参数、再用读回模拟确认最终盘面。

## 验证与边界

- RAW 与 WAV：对非对称图案，交织开启与关闭两种情况下，WAV 的 data 区均严格等于旧 RAW 逐 16 位交换后的字节序列；长度保持不变。
- 流式输出：在禁止 Seek/Position 的前向流中生成小端音频，结果逐字节等于 WAV 的 data 区，且不包含文件头。
- WAV 预览：解析 RIFF 的 `fmt ` / `data`，跳过未知块及奇数长度填充，限制读取范围，归一化字节序；拒绝非 PCM、非 44.1 kHz、非 16 位或非双声道数据。不同采样步长下与对应 RAW 预览一致。预览仍是文件字节映射，未模拟完整物理 CIRC/EFM，不能替代实盘判定。
- 读回一致性：同一源图分别生成交织与不交织的音轨，`--deinterleave true` 读回交织音轨与按文件顺序读回不交织音轨的结果，除延迟线尾部一个扇区内的像素外逐像素相同（900 px、非对称测试图下差异仅落在尾部投影内，变异测试把帧延迟改动 24 字节即报 245 处不一致）。
- 标定投影一致性：同一源图先按生成几何 `(24.5, 56.8)` 生成音轨，再把 `cd-preview-warp` 的投影（实测=生成、实测=`24.3/56.6`）与 `cd-preview-track` 用对应几何读回的结果逐像素比较，阈值化后完全一致（900 px）；实测半径缩小时投影的重心半径随之减小，因此两套几何不会串位。
- Windows 内存实验：向现有 `MsftRawCDImageCreator` 输入 300 个非对称音频扇区，扫描完整的 2448 字节 RAW 结果扇区，全部音频字节原样保留。生成器不会替调用者交换字节。测试不创建刻录设备、不写入光盘。
- 2026-09-12 用户在本次修复后完成实盘试验，反馈已无重影。这是用户提供的实体盘结果；流式、cdrecord、ImgBurn 三个入口的逐项实测细节未单独记录。

微软 `WriteMedia` 文档没有说明它内部的字节交换实现；上述结论没有把内存实验等同于观察到设备传输时的交换。[Microsoft WriteMedia 文档](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-idiscformat2rawcd-writemedia)
