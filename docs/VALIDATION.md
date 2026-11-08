# 验证说明

本页列出当前源码的可复现检查。自动化回归验证算法、文件布局和应用工作流；实际刻录效果仍取决于刻录机、固件与盘片。

## 本地验证

图片拖拽导入：`dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release -- --test-image-drop` 覆盖设计页拖入多图、大小写扩展名、混合文件／目录过滤、损坏图片提示、子控件事件路由、仅移动来源与非文件拖动拒绝、拖动中文件消失和任务忙碌保护（含导入按钮禁用），并复核已有图层排序。设置 `DISC_IMAGE_STUDIO_UI_SNAPSHOTS` 可输出 `design-image-drop.png` 离屏截图。手工验收：从资源管理器将单张或多张 PNG／JPEG／BMP／TIFF／GIF 拖到设计页画布或图层区，确认复制光标、图层添加和等比例适配；拖入非图片显示禁止光标；任务执行期间「导入图片」按钮应禁用，拖入同样显示禁止光标。

2026-11-08，使用 .NET SDK 10.0.100 完成拖拽导入验证：Release solution 构建零警告、零错误，`--test-image-drop`、DVD `selftest` 和完整 ArchitectureTests 全部通过。构建／测试日志位于 `tmp/image-drop-*.log`，离屏截图 `artifacts/image-drop/design-image-drop.png` 已检查。拖放事件由 WPF 回归程序构造；Windows x64 自包含测试版通过打包自检及界面启动检查，用户已确认实际拖拽导入测试成功。

2026-11-08 评审补充：悬停只按扩展名给出复制光标，落下时再次校验文件存在，避免在网络路径上每次 `DragOver` 都查询文件系统；任务状态变化会刷新导入命令，运行期间「导入图片」按钮禁用。使用 .NET SDK 10.0.114 重跑 Release 构建、`--test-image-drop` 和完整 ArchitectureTests，均通过。

2026-11-08，#233 卷标长度审查：CD／DVD 的 Joliet 标签超过 16 个字符时在写入前报错，不再由 `Iso9660ImageWriter` 静默截断；蓝光 UDF 标签保持 32 个字符，不受影响。回归验证 16／17 字符边界、PVD 32 字节与 SVD UCS-2 描述符中的实际写入值、空白卷标回退、BD 放行和中英文报错文案。导出页卷标输入限制为 16 个字符并显示提示，DVD 导出页离屏截图 `artifacts/issue233/export-dvd.png` 已核对；Release 构建零警告、零错误，DVD `selftest` 与完整 ArchitectureTests 通过。未执行实盘刻录。

2026-11-07，#184 参数保存流程：CD／DVD／BD 切换预设时同步高级预览的半径和三项轨距系数；工程设置保存预设成功后立即应用并按新 ID 选中，取消后保留该次保存，后续未确认输入不应用。覆盖同名条目、新建工程隔离、命名取消、保存失败、仅改名保留手工实测值和 BD 极性。

标定记录回归覆盖已确认但尚未应用／应用后的拟合保存重开、所选候选与预测恢复、原 Target 冻结、旧记录兼容、取消确认／更改点位后的失效、无效目标和候选校验，以及加载期间切换候选和取消加载不会误恢复确认。原有照片流程测试同步验证新的恢复行为。

Release solution 构建通过（0 警告、0 错误）；`--test-parameter-persistence`、DVD `selftest` 和完整 ArchitectureTests 均通过，完整输出为 `architecture-selftest: all checks passed`，日志位于 `artifacts/parameter-persistence/architecture-tests.log`。测试进程的 `TEMP`／`TMP` 指向仓库 `tmp`，避免沙箱限制默认临时目录的原子重命名。工程设置离屏截图 `artifacts/parameter-persistence/project-settings.png` 已检查。

2026-11-07，#90 周期保护规则更新为 `periodic-9b-16-v1`：每个 2048-byte 扇区只检查偏移 `0, 16, …, 2032`，将 owned 且原始 Q 不是 `A5/9B` 的字节改为 `9B`；完全归属于绘图的纯白扇区计 128 次，已有 `9B` 不计变化。该配方不叠加旧段首 bridge。默认仍关闭，CD／BD 不变；旧 `bridge-9b-v1` sidecar 保持读取兼容，已启用但没有配方字段的旧标靶继续复现 bridge，新建标靶保存周期配方。

本次核对范围：独立逐字节 oracle、偏移 0／16／2032 与非周期位置、全白 128 次／全黑 0 次、已有 `9B` 与其他非 `A5` 值、幂等性、文件／透明／未归属／绘图范围外不改写；分块及标量／SIMD／采样复用／并行确定性；默认关闭输出、精确 Q API 拒绝、ISO／CLI／流式一致性、内外圈文件保护、新旧 sidecar 预览分类、标靶开关与配方冻结及旧记录恢复。

Release solution 构建通过（`--no-restore -m:1 -nr:false -p:UseSharedCompilation=false`，0 警告、0 错误）；`--test-dvd-read-compatibility` 的规则、集成和 UI 回归、DVD 引擎 selftest 以及完整 ArchitectureTests 均通过，完整回归最终输出 `architecture-selftest: all checks passed`。首次默认并行构建退出 1 且未给出诊断，随后单 worker 构建成功；沙箱内专项测试遇到 `File.Move` 的 `UnauthorizedAccessException`，获准在沙箱外重跑后通过，未发现对应代码断言失败。以上检查均未访问光驱。完整回归结束后单独运行新版测速，退出码为 0，结果如下；下方 2026-11-06 的结果和实际队列检查仍仅属于旧 bridge。

新版生成侧测速：Windows X64、.NET 10.0.14、16 个逻辑 CPU，自动并行；完整 DVD-5 几何 2,297,888 sectors、24–58 mm、133.3 nm、起始角 0、旧式二值影调，每次生成前 64 MiB。使用 4096×4096 黑白／彩色透明度图片及原始 2400×2400 标定参考图，恒定轨距与三次系数 `(0.00012, -0.00008, 0.00004)`，分别关闭／开启采样复用，共 12 个场景。**所有场景均开启新保护，表中的开关仅指采样复用。** 每项预热一次后三轮取中位数，计时包含完整生成调用的解码、初始化、首批和尾批工作，不写磁盘、不访问光驱。

| 场景 / Fixture | 轨距 / Pitch | 复用关闭 / Reuse off MiB/s | 复用开启 / Reuse on MiB/s |
| --- | --- | ---: | ---: |
| 标定参考图 / Reference | 恒定 / Constant | 49.49 | 190.36 |
| 标定参考图 / Reference | 三次 / Cubic | 69.26 | 187.06 |
| 黑白图片 / Binary | 恒定 / Constant | 60.07 | 143.27 |
| 黑白图片 / Binary | 三次 / Cubic | 90.83 | 146.72 |
| 彩色透明度 / Colour-alpha | 恒定 / Constant | 45.18 | 45.88 |
| 彩色透明度 / Colour-alpha | 三次 / Cubic | 57.51 | 55.15 |

全部场景的最低中位数为 45.18 MiB/s，默认开启采样复用时为 45.88 MiB/s；所有单次测量中的最低值为 41.68 MiB/s。DVD 24× 需要 33.24 MB/s，即约 31.7001 MiB/s。这些本机场景的生成吞吐达到 24× 需求，但本次没有测量实际缓冲队列、设备写入或实盘读回，不能据此保证任意机器与负载下均无供数中断；旧版队列测试也不能替代新版测试。原始分轮数据在 `artifacts/issue90-periodic-guard/benchmark.log`。

复现新版测速（先完成 Release 构建）：

```powershell
dotnet tests/DiscImageStudio.ArchitectureTests/bin/Release/net10.0-windows/DiscImageStudio.ArchitectureTests.dll --benchmark-dvd-read-compatibility
```

外部硬件证据为 [#90 comment-6693099737](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/issues/90#issuecomment-6693099737)：报告者称原镜像起始即报 medium error，经 [every16 脚本](https://github.com/bczhc/dvd-tools/blob/master/src/bin/dvd-guard-every16.rs) 或另一种段首处理后可全盘读回。该反馈支持此测试环境中的可读性改善，不是本程序新实现的实盘测试，也未证明所有介质、驱动器或盘面光学对比度相同。纹理和对比度可能变化，仍需同图对照。

English: the new `periodic-9b-16-v1` recipe checks fixed offsets 0, 16, …, 2032 and changes only owned bytes whose original scrambled Q is neither `A5` nor `9B`; an entirely owned white sector has 128 changes. It does not add the legacy run-start bridge. Regression covers an independent byte oracle, ownership/range protection, repeatability across chunks and execution modes, output parity, sidecar interpretation and frozen legacy/new calibration recipes. The single-worker Release solution build completed with zero warnings/errors; the focused guard/integration/UI tests, DVD selftest and complete architecture suite passed. An initial parallel build exited without diagnostics, and a sandbox `File.Move` permission failure was resolved by an approved test run outside the sandbox.

The new benchmark ran separately after the complete suite and exited successfully. Its 12 cases all enable the periodic guard; off/on in the table means sample reuse. On X64 .NET 10.0.14 with 16 logical CPUs, each complete call generates the first 64 MiB of full DVD-5 geometry and includes decoding, initialization and the final batch. Fixtures use 4096² binary/colour-alpha images and the original 2400² reference, constant/cubic pitch, one warm-up and median-of-three measurement. The lowest case median was 45.18 MiB/s, or 45.88 MiB/s with default reuse enabled; the lowest individual run was 41.68 MiB/s, against 31.7001 MiB/s for DVD 24×. These local producer fixtures exceed the 24× throughput requirement; no disk output, real queue, recorder or physical readback was measured. The old bridge's queue results below are historical, not evidence for the new recipe. The linked external-script report describes full-disc readback in the reporter's setup, not universal media support or a hardware validation of this implementation. The option remains experimental and off by default; texture and contrast may change, and CD/BD are unchanged.

2026-11-07，#183 提交前复查：修正 BD 缩小可用容量后，预览逐像素重复构造几何的问题，改为在渲染入口缓存有效外半径；新建 CD 固定地址粗标要求完整包含最外侧测量边界及后续浅色留白，旧冻结记录保持原样。新增回归用外圈标记识别 CD／DVD 错用几何尺度导致的裁边，验证 BD 普通图片流式缩短后与完整输出前缀逐字节一致；另检查三次轨距 BD 的外缘像素、两种预览路径一致、常轨距／三次轨距短 ISO 读回，以及 CD 粗标容量的不足、临界值、正常值和旧记录恢复。最终 Release 构建零警告、零错误，容量专项、DVD selftest 和完整 ArchitectureTests 全部通过。日志为 `artifacts/usable-sectors/build-review.log`、`focused-review.log`、`dvd-selftest-review.log` 和 `architecture-review.log`。未执行实盘刻录。

English: the pre-commit review added cached BD preview radii and qualification of new CD coarse targets against their fixed outer boundary. Outer-ring fixtures detect incorrect CD/DVD image scaling; BD stream-prefix, reduced-capacity preview/readback and CD calibration boundary regressions passed. The final Release build, focused capacity checks, DVD selftest and complete architecture suite passed; no physical disc was used.

2026-11-07，可用扇区与几何容量分离（#183）：最终 Release solution 构建通过（`--no-restore -m:1 -nr:false`，0 警告、0 错误），DVD 引擎 selftest、完整 ArchitectureTests 和容量／界面专项回归全部通过。新增检查覆盖内置预设可用数、旧预设缺省预留、显式值不重复扣减、工程／预设／标定记录往返、非对齐的 DVD 几何总数保留，以及 CD／DVD／BD 的输出长度与写入对齐。生成回归验证原始几何不变、画面按可用范围映射、文件系统在最终容量内完整布局、sidecar 恢复；设计图层在可用容量改变后保存／重开保持实际尺寸与合成像素。流式回归对照完整标定输出前缀，确认设备容量缩小时只截尾且不改保存的标定参数。设备替身覆盖 DVD 直接使用 Free、CD 单次预留与严格 DAO lead-out 边界、BD 原有完整布局限制，以及标定读取分别填入总数和可用数。

新建／编辑工程与标定页离屏截图已目视检查，中英文几何总数／可用数可完整显示；本地化回归同时通过。日志和截图位于 `artifacts/usable-sectors/`，完整回归日志为 `architecture-final.log`；测试 `TEMP`／`TMP` 指向该目录的 `temp` 子目录。沙箱内文件原子替换受限，最终完整回归在不受该限制的执行环境复跑通过。未访问刻录设备、未执行实盘刻录；此次调整容量、图像坐标范围与布局，不改 ECC／EFM 等编码算法，DVD selftest 覆盖项不变。

English: the final Release solution build completed with zero warnings/errors; DVD selftest, the complete architecture suite, and focused capacity/UI checks passed. Regression covers explicit usable counts without double deductions, legacy preset defaults, geometry preservation, aligned output, device capacity limits, image scaling, complete file layouts, metadata persistence, and stable saved layers. Truncated calibration streams match the full output prefix. Project/calibration offscreen screenshots were inspected. No optical device or physical burn was used; encoding algorithms and the DVD selftest scope are unchanged.

2026-11-07，DVD／BD 详细诊断补齐：最终 Release solution 构建通过（`--no-restore -m:1 -nr:false`，0 警告、0 错误），DVD 引擎 selftest 和完整 ArchitectureTests 全部通过。新增回归覆盖所有 IMAPI 物理盘片类型、可选字段独立失败、DVD 设备操作顺序／容量／速度／校验失败阻断，以及在事件退订、取消注册注销和源流销毁前保存原始写入异常与快照；用阻塞的取消回调确认失败记录不会被清理延迟。BD 回归覆盖完整介质与轨道上下文、真实 MMC sense 保留、写入／刷新／关闭／释放失败及取消，检查未增加设备查询或改变清理顺序。CD 既有诊断与流式缓冲回归同时通过。未创建刻录设备、未执行实盘刻录；无 GUI 布局或编码链改动，selftest 覆盖项不变。日志位于 `artifacts/burn-diagnostics/`，测试 `TEMP`／`TMP` 使用该目录的 `temp` 子目录。

English: the final Release solution build completed with zero warnings/errors; DVD selftest and the complete architecture suite passed. New checks cover shared physical media identification, DVD operation order and failure snapshots before event/cancellation cleanup, and BD cached media/track context, original MMC sense, finalization failures and cancellation. Existing CD and stream diagnostics also passed. No recorder or physical disc was used; the change adds diagnostics without changing encoded output or media support.

2026-11-07，CD DAO 详细诊断（#182）：Release solution 构建通过（`--no-restore -m:1 -nr:false`，0 警告、0 错误），DVD 引擎 selftest 和完整 ArchitectureTests 全部通过。新增回归覆盖 CD-R/CD-RW 与未细分类型、可选 COM 属性失败、请求／实际速度与容量上下文、原始 `0xC0AA0301` 异常保留、清理前快照、日志回调异常隔离、导入区每两秒事件节流、IMAPI 缓冲字段来源与事件时间，以及流快照不消费数据、Seek／读取区分、队列背压、生成异常和取消。对照 CD 音轨／数据 DAO 及流式进度项目复核：写入格式和容量限制不变、负 LBA 仍不计载荷进度，异常后只读取缓存并保持一次介质释放。未创建刻录设备、未执行实盘刻录，不能据此认定 #182 已修复；高层 IMAPI 不提供的命令级 SCSI sense 明确记为不可用。无 GUI 布局或编码链改动，selftest 覆盖项不变。日志在 `artifacts/cd-burn-diagnostics/`；测试进程的 `TEMP`／`TMP` 指向其 `temp` 子目录，以避开系统临时目录的文件替换权限限制。

English: the Release solution build, DVD selftest and complete architecture suite passed. New CD DAO checks cover media identification, optional telemetry failures, original exception and pre-cleanup context, throttled lead-in snapshots, IMAPI buffer provenance, and non-consuming producer/queue diagnostics. No device or physical burn was used; command-level sense remains explicitly unavailable through the existing RawCD API, and this change does not establish a fix for #182.

2026-11-06 影调预览统一：Release solution 构建为零警告、零错误，完整 ArchitectureTests 和 DVD 引擎 selftest 通过；离屏检查三盘型无文件、内圈文件、外圈文件及 CD 自定义半径宽版面板。日志位于 `artifacts/tone-preview/`，截图位于 `artifacts/tone-preview-ui/screenshots/`。此次仅改变预览呈现，未改动音轨、数据盘或流式刻录的输出字节。

影调预览回归：`TonePreviewTests` 通过真实设计合成与状态刷新链检查 CD/DVD/BD 的 512×512 输出、透明中孔和记录区外遮罩、实际内外圈文件布局、切换文件夹/几何后的刷新、连续更新淘汰旧结果，以及错误/关闭工程时清除旧图；原图与合成图哈希保持不变。`CdPreviewMaskTests` 对比音轨和数据盘的源图投影与成品回读，检查稀疏采样下环内仍为不透明白色，并用 59.5 mm 外半径确认显示适配不改变 57.5 mm 的源图采样比例。`CdBinaryTests` 同时验证灰阶和几何透明度。

English: tone-preview regression covers all three media at 512×512, recording-area alpha masks, exact file/filesystem ranges, automatic updates, stale-result removal and unchanged source/composite hashes. CD tests compare projection with audio/data readback, retain opaque unsampled pixels inside the ring and verify that fitting a 59.5 mm output radius preserves the 57.5 mm source-image mapping. Offscreen narrow and wide tone-panel snapshots are available in `artifacts/tone-preview-ui/screenshots/`.

2026-11-06，CD 数据盘 ADR3 修复：Release solution 构建通过（0 警告、0 错误），完整架构回归及 DVD 引擎 selftest 通过。新增回归用旧实现复现 ADR3 转换失败，修复后通过；包含独立 CRC 多项式除法、9 个计时/CRC 固定向量、预间隙/数据区/lead-out 边界及 BCD 进位与上限、TOC/ADR1/ADR2 保留和 P/R-W 隔离。Raw P-W 与 Cooked R-W 均完成真实离线 IMAPI 全镜像扫描；音轨整张镜像与独立原生 COM 参考逐字节一致，PCM 字节和音轨 ADR3 保持。原有前向流、部分读取、主通道和内外圈 ISO 回读测试通过。此次只改变数据 DAO 的 ADR3 转换，未改动音轨生成/刻录路径或 GUI；DVD selftest 覆盖项不变。未创建刻录设备，未执行实盘读回。修复边界见 [数据轨 ADR3 修复记录](PHYSICAL_MODEL_AUDIT.md#2026-11-06-cd-数据轨-adr3-修复)，日志在 `artifacts/cd-subcode-fix/`。

最后调整部分读取测试，使其也能接受原生模板在该位置安排 ADR2；随后最终 solution 重建仍为零警告、零错误，三个 CD 子码/RAW 镜像测试再次通过。完整回归日志为 `architecture.log`，最终构建及定向复核为 `build-final.log`、`final-cd-check.log`，均在上述日志目录内。

English: the Release solution build completed with zero warnings/errors; the complete architecture suite and DVD selftest passed. The new regression detects the old data ADR3 behavior. Boundary vectors, an independent CRC oracle and complete native IMAPI images cover the repair in both subchannel formats. Audio remains byte-identical to a direct native reference, including its ADR3 metadata and PCM. After making the partial-read expectation tolerate native ADR2 scheduling, the final solution build and all three CD subcode/RAW image test groups passed again. No optical device was accessed.

2026-11-06，CD 后续风险审计（先于上述修复）：当时只新增独立模型/离线 IMAPI 探针及文档，未改生产代码、未操作光驱，未重跑 solution 构建。23 组真实生成器样本的 CIRC 逆变换及 6,003 个抽样校验码字通过；完整 EFM/同步/合并位检查、126 组对齐与同分选择对照、386 个周期样本及 16 个扩长样本未发现持续 DSV 累积。第二实现独立检查两个完整通道输出的 14,636,496T 和 26,656 个合并位决策。实际子码检查另确认 Mode 1 DAO 保留了音轨 ADR3/ISRC，虽然 CRC 和出现频率均通过，数据轨语义不符合标准；后续修复和验证已记在上方。合法 ADR1 参考子码的 8 组 Mode 1 重跑没有改变持续偏置结论。范围、数据和修复建议见 [CD 完整链路审计](PHYSICAL_MODEL_AUDIT.md#2026-11-06-cd-后续完整编码链与数据轨子码)，材料在 `artifacts/issue90-cd-full/`。

2026-11-06，#90 旧 `bridge-9b-v1` 读取兼容性实验（历史结果）：最终 Release solution 构建通过（0 警告、0 错误），完整架构回归、Windows DVD 引擎 selftest 与通用 CLI selftest 全部通过。新增回归对照独立逐字节 oracle，覆盖 57/58 个纹理字节阈值、前后 64-byte 窗口、扇区末端、已有保护字节、幂等性及逐字节归属；核对默认关闭输出、标量/SIMD/采样复用/并行一致性、精确 Q API 拒绝、ISO/CLI/流式字节一致、内外圈文件保护、版本化 sidecar 与读回预览、冻结的标定参数及旧记录默认关闭。四张中英文离屏截图已目视检查，标定窄栏的新开关完整换行。测试不访问光驱；完整读回和光学对比度需实盘验证。日志在 `artifacts/issue90-build.log`、`issue90-architecture.log`、`issue90-dvd-selftest.log`、`issue90-portable-selftest.log`，截图在 `artifacts/issue90-compat-ui/`。

以下旧 `bridge-9b-v1` 生成侧测速使用 Intel Core i5-1340P、Windows x64、.NET 10.0.14 Release，自动并行 16 workers，关闭 tiered compilation 以减少短任务 JIT 波动；每项预热后输出 128 MiB 到 `Stream.Null`，开关交替，三轮取最低值（十进制 MB/s）。完整单层几何 2,297,888 sectors 的中段 65,536 sectors，三次轨距系数 `(0.18, -0.12, 0.06)`。这是生成吞吐，不包含存储或设备写入。

| 场景 | 保护关闭 MB/s | 保护开启 MB/s |
| --- | ---: | ---: |
| 黑白图片 | 294.31 | 232.30 |
| 彩色与透明度图片 | 122.82 | 116.25 |
| 解析标定图案 | 103.02 | 99.54 |
| 按扇区生成环带 | 5162.64 | 1589.95 |

程序按 DVD 1× = 1,385,000 B/s 换算，24× 需要 33.24 MB/s；上述开启保护的最低值为需求的 2.99 倍。限制为 8 workers 的另三轮测试最低为 72.89 MB/s（2.19 倍）。16-worker 开启保护时记录的最大 32 KiB 输出块间隔为 16.78 ms；现有默认队列 64 MiB、预缓冲 32 MiB，在 24× 下预缓冲对应约 1.01 s。这些数据不保证任意 CPU、图片或后台负载下的供数能力。复现程序与原始分轮结果在 `artifacts/issue90-compat-perf/`；实际缓冲管道的节流检查另记如下。

实际流式管道另使用 `GeneratedContentComStream` 默认 64/32 MiB 队列，默认 tiered compilation 和自动并行，对 1025² 彩色透明图片及带文件的混合镜像分别做 24×（33.24 MB/s）和 32×（44.32 MB/s）节流读取。四轮每轮完整生成 131,072 扇区/256 MiB，使用该小容量的完整几何（不是 DVD-5 全盘几何）；未访问光驱。四轮均无生产期间空队列读取、无观察到的供数饥饿、无超过块读取预算的调用；预缓冲 0.425–0.476 s，最大块读取耗时 0.168–1.526 ms，生产期间最小缓冲约 31.5–32 MiB。末尾正常排空单独统计，不当作供数不足。实际消费约 33.23–33.24 / 44.24–44.28 MB/s，生产总时长包含队列背压，不能拿它当纯计算上限。结果在 `artifacts/issue90-compat-perf/pipeline/results.json`，表明这些本机场景可支撑 24×，并通过 32× 供数压力检查；不代表设备支持 32× 或实盘刻录、读回已验证。

English: these historical `bridge-9b-v1` results cover independent boundary/ownership tests and integration for output parity, protected files, sidecar interpretation and frozen calibration settings. On this i5-1340P, the slowest protected producer fixture measured 99.54 MB/s with 16 workers and 72.89 MB/s with 8 workers, versus 33.24 MB/s for DVD 24×. These are warmed Release `Stream.Null` measurements. Four additional 256 MiB tests through the real host queue, consuming at 24× and 32×, showed no active empty-queue reads or read-budget overruns. The queue tests use a complete small-capacity geometry, without an optical device; they do not establish hardware 32× support or physical readback reliability, and they are not measurements of the newer periodic recipe. CD/BD adjustment-bit findings and limitations are documented in [the physical-model audit](PHYSICAL_MODEL_AUDIT.md).

2026-11-06：对照 ECMA-267/359 修正 DVD EFMPlus 状态 3/4 的副 SY6（`0x80440011`），以及 EDC 重复补 32 位零的问题。两项新增回归均先在旧实现复现失败；修正后完整 SYNC 标准向量、实际副 SY6 输出、前缀 DSV、EDC 独立长除法和三种完整数据帧向量通过。Release solution 构建通过（0 警告、0 错误），完整架构回归、Windows DVD 引擎 selftest 和通用 `net10.0` CLI selftest 全部通过。逐表提取、独立 RS/扰码/交织及 9,728 个同步帧解码的审计范围见 [DVD 编码标准复核](DVD_ENGINE.md#编码标准复核2026-11-06)。未执行实盘刻录；#90 的读盘兼容性及 DVD+RW 介质模型尚需单独处理。

正确 EDC 改变了后续调制状态，原 state-control 稀疏交替目标测试在 8 宽候选搜索下得到 987/1936 个错误，未满足既有小于 50% 的质量要求。独立逐位重编码确认评分一致；仅将测试的 BeamWidth 提高为 16 后得到 877/1936，保持原始输入、随机种子、3 轮迭代、重试参数及全部质量/边界断言。生产求解器参数未改动。

2026-11-06：词条回退顺序调整为当前语言 → 英文 → 简体中文 → 原始键，通过 Release solution 构建（0 警告、0 错误）和完整架构回归。验证译文缺失／空白、当前语言优先及格式参数；真实西语标定失败界面保留西语标题，缺译的动态原因在弹窗和底栏回退英文，离屏截图已核对。测试临时补入西语照片及预设词条，确认新增诊断可按语言扩展、重排参数和翻译嵌套说明，测试结束后恢复资源。

2026-11-06：照片矫正及工程、预设校验弹窗的英文补译通过 Release solution 构建（0 警告、0 错误）、完整架构回归和 DVD 引擎 selftest。新增回归触发真实的内外缘不吻合失败，验证直接矫正及恢复已确认照片时的英文弹窗、底栏和误差数值；覆盖英文界面下小数点／小数逗号格式、动态标定版本、参数名后缀、嵌套预设要求、用户文件名与 ID 保留，以及未知外部异常的原文回退。英文失败弹窗离屏截图已核对。

2026-11-05：标定提示英文补译与日志精简通过 Release solution 构建（0 警告、0 错误）、完整架构回归和 DVD 引擎 selftest。新增回归使用实际短边界拟合失败，验证侧栏、底栏和日志的英文原因、点数与覆盖率要求，以及语言切换时保留拟合结果、描点和历史日志。英文失败界面的离屏截图已核对。

2026-11-05：CD DAO 子通道格式选择（优先 Raw P-W、回退 Cooked R-W、拒绝仅 PQ）rebase 到 `507cac7` 后通过 Release solution 构建（0 警告、0 错误）、完整架构回归与 DVD 引擎 selftest。新增检查覆盖格式选择、只读取一次支持列表、诊断日志行、数据盘 DAO 的类型传递，以及原生 IMAPI 内存镜像在两种 2448 字节格式下逐字节相同。对照上方"CD 图片导出、音轨直接刻录""CD Mode 1 文件盘、流式刻录""流式刻录显示"和"CD 数据 DAO"各项复核：生成字节、CIRC 位置、进度与诊断行为不变，只改变告知刻录机的子通道布局。自包含单文件发布按 `packaging/Build-Release.ps1` 的检查项冒烟通过。实盘结果见下文"实盘验证边界"的 2026-11-03 记录（修复版基于 v1.3.6）；rebase 后的构建未另行实盘刻录。未改动界面、编码链或 selftest 覆盖项。

2026-11-05：GUI 内部调用重构通过 Release solution 构建（0 警告、0 错误）、完整架构回归与 DVD 引擎 selftest。新增检查覆盖 CD/DVD/BD 类型化生成入口；DVD 普通、内圈文件及外圈文件 ISO 与 CLI 的载荷、布局和 sidecar 一致性；DVD 投影/回读预览像素一致性、取消和内部调用不写控制台；导出成功、失败、取消、迟到进度及完整异常诊断；标定清理和容量读取失败、预览缓存故障与可选缩略图降级。DVD 投影的离屏 GUI 快照已生成并目视检查。未执行实盘刻录，也未新增性能结论。

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
- CD 数据 DAO：Mode 1 EDC 与全部 P/Q 校验方程、主通道字节序、导入区及预间隙地址、数据轨 Q 标识与 CRC、ADR3 完整替换为 ADR1、全部区域时间/出现频率和 P/R-W 保留、独立原生音轨镜像逐字节不变、前向 Seek 和短读、内外圈 ISO 完整还原；保留铼德预设 359,845 扇区，并使用模拟刻录机验证与音轨共用的容量限制和子通道格式选择（优先 Raw P-W、回退 Cooked R-W、拒绝仅 PQ）。原生 IMAPI 测试只构造内存镜像，不创建刻录设备；两种 2448 字节模板均参与校验。
- BD：17PP/EDC/Reed-Solomon、3T/6T 映射与极性、绘图范围清零、UDF 2.01、大于 4 GiB 文件、sidecar 往返与无效边界拒绝。
- DVD/BD 文件位置：默认内圈兼容、外圈 ISO9660/Joliet 与 UDF 目录独立解析、中文文件内容、完整盘面坐标、不可 Seek 流与文件输出一致、容量及取消保护，以及工程中的位置选项往返。
- 三态模型：工程 schema 2 往返与 schema 1 升级、预设 schema 4 与旧 schema 合并、`ProjectSession` 的 BD apply/capture。
- 刻录：操作互斥、进度、顺序写入、超时、取消和资源清理，均使用模拟接口而非真实刻录机。
- 流式刻录显示：仅设备确认写入量推进进度，CD 音轨／数据、DVD、BD 倍速换算，未知或无效速率、3 秒过期、封盘与结束清理，传输日志节流及迟到事件保护。
- 标定刻录来源：CD/DVD/BD 标签与保存参数一致，BD 位长、极性和绘图范围，跨盘型切换、速度查询，以及模拟刻录请求和确认文案。
- 标定：三次轨距、BD 绘图范围与极性、照片矫正、曲线拟合、会话保存恢复、盘型预设同步与参数精度。
- 预览与窗口：CD/DVD/BD 投影与读回、多显示器和任务栏边界。
- 文件区预览：内外圈设计投影和带 sidecar 的成品读回标蓝、DVD 三次轨距边界、纯绘图及无效布局信息不标蓝、错误目录/容量提示、切换与重置清理。蓝色叠加不改变 ISO 数据。

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

## 刻录失败日志的判读

状态快照仅输出阶段、事件、Action 和 LBA 字段及来源，不在每条日志后重复附加解释。字段含义见下文；校验是否启用、取消结果和完整异常仍会记录。

CD、DVD 与蓝光的日志按实际可获得的状态分开记录，写入完成通知不等于整个刻录任务已经成功；只有写入、必要的收尾及设备释放正常结束后，任务才报告完成。

- DVD 在调用 `Write` 前通过 SDK 定义的 `IBurnVerification` 接口将刻后完整性校验级别设为 `IMAPI_BURN_VERIFICATION_NONE`，并回读确认；无法应用或确认 `NONE` 时在写入前报错。日志明确记录校验已禁用，不将其显示为校验成功。此设置保留必要的写入收尾与封盘，不证明原来的失败就是校验引起或已经修复。
- DVD 仍识别并保留 IMAPI 的原始 `CurrentAction`：`5 / FINALIZATION` 表示收尾，`6 / COMPLETED` 表示写入完成；若收到 `7 / VERIFYING`，仍单独记录实际报告的校验状态，便于检查与禁用设置不一致的状态报告。三者不再共用“正在刷新缓存并关闭刻录会话…”提示。IMAPI 的收尾状态不能进一步证明当时正在执行哪一条底层缓存刷新或关闭命令。
- CD DAO 的 `FINISHING` 单独记录为收尾；`WriteMedia` 正常返回和后续关闭会话分别记录。CD DAO 与当前蓝光顺序写入路径不提供与 DVD 相同的校验事件，未执行完整性校验时明确记录未执行，不生成虚假的校验成功事件。
- 蓝光顺序写入分别记录刷新缓存、关闭轨道、封盘与释放设备。失败后为排空已接收数据执行的缓存刷新属于清理，日志与原始失败操作分别保留；它不能把原本失败的刻录变成成功。
- CD/DVD 的 `StartLba`、`LastWrittenLba` 来自设备进度事件。非写入阶段也保存能读到的最终值；属性无法读取时保留此前有效快照，并注明不可用信息。CD 的负 LBA 属于导入区，不能当成已经写入的图案载荷。
- 蓝光的 `LastAcceptedLba` 来自成功的 MMC `WRITE` 命令，表示设备已经接收，可能仍在缓存中。`LastWrittenLba` 则缓存现有 `READ TRACK INFORMATION` 响应中的最后已记录地址，仅在 `LRA_V=1` 时采纳，并注明来源为 `MMC READ TRACK INFORMATION`；没有观察到有效响应时保留未知，不用接收量或下一可写地址推算。此诊断复用原有响应，不增加设备 I/O，也不在失败后为诊断重新查询设备。两个值独立保留，传给刻录接口的数据量同样不能证明盘面写入完成。
- 失败日志保留操作、最近的原始状态和扇区快照，以及异常类型、HRESULT、内部异常、附加数据和调用堆栈。清理失败作为后续信息保留，不覆盖最初失败。日志中的异常详情保留底层和操作系统原文。

复核时应同时提供从开始刻录到失败的完整日志、应用版本、盘片类型、刻录机型号和实际速度。单独一条阶段提示或“数据传输接近 100%”不能定位故障命令。诊断改动使用模拟事件和异常验证；本次没有通过实盘复现来确认某一光驱的刷新缓存或封盘故障。

## 实盘验证边界

此次文件位置与预览改动没有执行实盘刻录。自动化测试不证明设备写入策略、实际反射亮度或刻录成功率；蓝光功能仍为实验性。

v1.3.1 的 CD 数据盘 DAO 也尚未执行实盘刻录；已经通过完整架构回归、独立单文件 EXE 启动、音轨/CUE、内外圈 ISO 文件内容与成品读回验证。

2026-11-03 记录：HP BD-RE BU20N（HL-DT-ST，固件 0403，USB 连接）配联想 Thinkplus CD-R 80min（ATIP 导入 97:15:17、导出上限 79:59:70，即 359,845 扇区）。v1.3.6 流式音轨刻录（359,840 扇区，4×）在首次写入导入区时失败，返回 `0xC0AA0301`。独立的 SCSI 模拟刻录（Test Write，不烧录数据区）确认：Cooked R-W（数据块类型 2）在 4× 与 16× 下第一个 WRITE 即返回 5/64/00；Raw P-W（数据块类型 3）以 4× 连续接受 3,000 个导入区扇区；SAO 提示表方式以 4× 正常写入。每次模拟后重新装盘，复查均为空白盘。改用 Raw P-W 的修复版随后在同一刻录机上完成了 CD-R 流式实盘刻录，不再返回 `0xC0AA0301`；这是用户提供的实体盘结果。

已经记录的 DVD 三次轨距实拍与参数见 [README 中的前后对比](../README.md#辅助标定展示)。物理模型历史审计见 [PHYSICAL_MODEL_AUDIT.md](PHYSICAL_MODEL_AUDIT.md)，其中结论保留其标明的日期与代码基准，不能代替当前源码验证。

后续实盘记录应包含版本或提交、刻录机和固件、盘片型号、预设参数、速度、生成/刻录入口与原始照片，避免把某一设备的结果泛化到所有盘片。
