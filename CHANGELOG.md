# Changelog

## Unreleased

### 中文

- Windows 应用宿主设置 `CetCompat=false`，作为 #229 所报启动失败的兼容性处理；这会降低该应用进程的硬件影子栈保护，不修改系统级安全设置。受影响原机上的启动结果仍待验证。
- 新增 Windows ARM64 自包含单文件 EXE；Windows x64 与 ARM64 分别在原生 runner 上构建并对最终发布文件执行自检、音轨生成和 GUI 截图检查，Release 复用这些已测试制品。ARM64 使用 Windows 11，CI 仅覆盖软件检查，尚未验证实盘刻录。
- 设计页支持从资源管理器拖入单张或多张图片。
- 设计页占用区引导改用 CD、DVD、BD 的真实布局规划器，并按可用（可写）容量计算，包含文件系统元数据、逐文件扇区取整与对齐；与 #96 的碎片化放置共用与图片无关的元数据预留。外圈放置文件时同时标出内圈元数据，支持多个环形区域。文件夹或盘片参数变化后延迟合并计算，图片编辑不再触发重算。
- 生成 CD／DVD 数据盘 ISO 时校验卷标长度：超过 Joliet 的 16 个字符会在写入前报错，不再静默截断；导出页的卷标输入限制为 16 个字符并显示提示，蓝光 UDF 卷标不受影响。
- 关于页在 GitHub 按钮左侧新增「检查更新」：从上游 GitHub Release 的 `latest.json` 获取正式版信息，按当前处理器架构下载便携 EXE，核对长度与 SHA-256 后替换并重启；替换失败或新版本启动即退出时恢复旧 EXE，应用目录不可写时提示手动下载。应用内更新器不验证 Release 附带的 PGP 分离签名，也不对 `latest.json` 本身进行 PGP 身份验证；SHA-256 只能发现文件与清单不一致，不能防止有权修改 Release 的一方同时替换文件和清单。
- 为 GitHub Actions Release 流程新增可选 PGP 签名：配置 `RELEASE_SIGN_KEY` 后，CI 使用 Sequoia PGP 为 EXE、压缩包、SHA-256 校验文件和更新清单生成独立签名文件，并随 Release 发布公钥；未配置密钥时跳过签名。应用内自动更新暂不验证这些签名。

### English

- Set `CetCompat=false` on the Windows application host as a compatibility workaround for the startup failure reported in #229. This reduces hardware shadow-stack protection for this application process without changing system-wide security settings. Startup on the affected original machine still needs verification.
- Add a self-contained single-file Windows ARM64 EXE. Windows x64 and ARM64 builds run on native runners and check the final executables with self-tests, audio-track generation and GUI snapshots; releases reuse those tested artifacts. ARM64 uses Windows 11. CI covers software checks only; physical disc burning on ARM64 remains unverified.
- Import one or multiple images by dragging files from Explorer onto the design page, using the same layer fitting and failure messages as the import button. Folders and unrelated files are ignored, and imports are disabled while a job is running.
- Any failed preview now clears the previous picture and its export entry; the status line and placeholder keep only the failure reason. Switching the UI language re-words the failure without restoring the old image.
- The design guide now uses the real CD, DVD and BD layout planners with the usable (writable) sector count, including filesystem metadata, per-file sector rounding and alignment. It shares the image-independent metadata reserve with the fragmented placement in #96. Outer-ring files also mark their inner metadata, and multiple annuli are supported. Folder or disc parameter edits are debounced and coalesced; artwork edits no longer recompute the guide.
- The design canvas guide is now theme blue and is drawn above every layer and selection adorner. Its opacity is raised so bright artwork can no longer hide it.
- The tone and preview pages now use the same theme blue for their file-region overlay and legend. The tone panel's legend moved to the design canvas bottom-left corner, so it no longer consumes right-rail height.
- Removed the move-up, move-down, and delete buttons from the design layer card. Reordering stays on drag, and deletion remains available from the row menu and the Del key. The disc-file picker buttons now read "Browse…".
- Removed the "Show data occupancy area" toggle; the blue guide is always visible.
- Validate volume-label length when generating CD/DVD data ISOs: labels longer than Joliet's 16 characters now fail before writing instead of being silently truncated. The export page limits label input to 16 characters with a hint; Blu-ray UDF labels are unaffected.
- Add "Check for updates" to the About page, left of the GitHub button. It reads the stable release manifest (`latest.json`) from upstream GitHub Releases, downloads the portable EXE for the current processor architecture, verifies its length and SHA-256, then replaces the running file and restarts. A failed replacement, or a new build that exits during startup, restores the old EXE; a non-writable application folder prompts a manual download. The updater does not verify the detached PGP signatures attached to the Release or authenticate `latest.json` itself; SHA-256 only detects a mismatch with the manifest and cannot protect against an actor who can replace both the files and the manifest.
- Added optional PGP signing to the GitHub Actions release workflow. When `RELEASE_SIGN_KEY` is configured, CI uses Sequoia PGP to generate detached signature files for EXEs, archives, the SHA-256 checksum file, and the update manifest, and publishes the public key with the release. Signing is skipped when the secret is not configured. The in-app updater does not yet verify these signatures.

## [1.5.0] - 2026-11-07

### 中文

- 工程切换盘片预设时同步高级预览的实测半径和三项轨距系数；在工程设置中保存预设后立即应用并选中新条目，后续取消窗口不撤销该次保存。标定记录可保存已确认的拟合结果，重开后恢复所选候选及设置，同时保留照片对应的原刻录参数。
- 分离盘片几何总扇区数与可用扇区数，补齐内置预设；旧预设缺字段时 CD 预留 2 扇区、DVD 保守预留 608 扇区，显式可用值不重复扣减。设计、预览和导出使用可用区域，流式刻录在布局前按实际可写容量限制输出并保持 DVD 16／BD 32 扇区对齐；标定读盘同时保留几何与可写容量，混合镜像按输出长度安排文件及尾部元数据。移除 CD 隐式允许超出 DAO 边界 3 扇区的容差，写前严格遵守设备报告的 lead-out 上限。新建 CD 粗标会检查最外侧固定测量边界是否完整可写；BD 预览缓存有效外半径，避免缩小容量时逐像素重复解算几何。
- 将详细刻录诊断扩展到 DVD 和 BD：共同记录构建环境、光驱固件、物理盘片类型及空白状态；DVD 记录容量、缓冲保护／封盘设置和阶段耗时，BD 记录 profile、格式化状态、轨道与可写地址及刷新／封盘／释放耗时。失败前缓存源流与设备上下文，保留 BD 底层真实 SCSI sense；DVD 高层接口未提供的 sense 明确记为不可用。
- 补充 CD 音轨／数据盘 DAO 刻录诊断：记录构建版本、系统、光驱固件、实际盘片类型与写前空白状态、子通道及速度设置、容量余量、阶段耗时、生成与主机缓冲状态；导入区事件快照每两秒最多记录一次。失败上下文在清理前保存，不额外查询故障设备；明确标注 IMAPI 未提供失败命令的 SCSI sense。
- 统一 CD、DVD、BD 的影调预览为 512×512：记录区外与中心孔使用透明遮罩，取消固定裁切，并按实际文件布局标黄文件及文件系统区域。CD 显示适配记录外半径，保留生成时的图片采样比例；修改文件夹、卷标和内外圈位置会自动刷新。
- 修复 CD 数据盘 DAO 子码沿用音轨 ADR3/ISRC 包的问题：按扇区位置重建完整 ADR1 时间包并重算 CRC，覆盖预间隙、数据区与导出区；保留原有 TOC、ADR1/ADR2 和 P/R-W 处理。补充边界、两种子通道格式及音轨输出逐字节不变的回归。
- 新增默认关闭的「DVD 读取兼容性（实验性）」选项；根据 #90 的全盘读回反馈，当前配方改为 `periodic-9b-16-v1`：每扇区固定每 16 字节将归属于绘图的非 `A5/9B` 扰码字节改为 `9B`，纯白扇区改变 128 字节，不叠加旧段首规则。文件、透明区域及绘图范围外保持不变；旧 sidecar 继续可读，已有标靶保留原配方，新标靶使用新规则。盘面纹理和对比度可能变化，仍需更多光驱与盘片反馈；CD／BD 不变。
- 按 ECMA-267 修正 DVD EFMPlus 模型在状态 3/4 下的副 SY6 同步码，纠正精确编码及 DSV/边界回放；补充完整同步码标准向量和实际编码回归。
- 修正 DVD 数据帧 EDC 计算中重复补 32 位零的问题，使校验字节及后续 ECC/调制回放符合标准；补充独立多项式长除法和完整数据帧向量检查。

### English

- Synchronize advanced preview radii and cubic pitch coefficients when applying a project preset. Saving a preset in project settings immediately applies and selects it, even if the dialog is later cancelled. Calibration records retain confirmed fitting results and restore the selected candidate and setup while preserving the photographed disc's original generation parameters.
- Separate geometry sector counts from usable capacity and populate built-in presets. Legacy presets without a usable value reserve 2 CD sectors or a conservative 608 DVD sectors; explicit limits are never reduced twice. Artwork, previews and exports follow the usable region. Streaming burns constrain layouts to the current writable capacity, aligned to 16 DVD sectors or 32 BD sectors. Calibration reads retain both counts, and filesystem layouts keep files and trailing metadata within the output length. Remove the implicit 3-sector CD DAO overburn allowance so the final check strictly enforces the reported lead-out limit. Reject new CD coarse targets whose outer fixed measurement edge cannot fit, and cache the usable radius during BD previews to avoid solving geometry for every pixel.
- Extended detailed burn diagnostics to DVD and BD, sharing build/runtime, recorder firmware, physical media type and blank-state reporting. DVD adds capacity, buffer/closure settings and phase timings; BD adds profile, format state, track/writable-address data and flush/close/release timings. Source and device context are cached before cleanup, preserving real BD command-level SCSI sense and explicitly marking sense unavailable through the high-level DVD API.
- Added CD audio/data DAO diagnostics for the build, OS, recorder firmware, reported physical media type and pre-write blank state, subchannel and speed settings, capacity margin, phase timings and producer/host-buffer state. Lead-in event snapshots are throttled to at most one every two seconds. Failure context is retained before cleanup without querying the failing device; unavailable command-level SCSI sense is explicitly identified.
- Unified CD, DVD and BD tone previews at 512×512, with transparent masks outside the recording area and inside the centre hole. Removed the fixed crop and highlighted file and filesystem regions from the actual layout plans. CD previews fit the recording radius while preserving the generator's source-image scale; folder, volume-label and inner/outer placement changes refresh automatically.
- Fixed CD data-disc DAO subcode retaining audio ADR3/ISRC packets. Rebuild complete ADR1 timing packets and CRCs from the sector position in the pregap, program area and lead-out, while retaining existing TOC, ADR1/ADR2 and P/R-W handling. Added boundary, subchannel-format and byte-for-byte audio-output regression coverage.
- Added the opt-in “DVD read compatibility (experimental)” option. Following the full-disc readback feedback in #90, the current `periodic-9b-16-v1` recipe replaces owned scrambled drawing bytes other than `A5/9B` with `9B` at fixed 16-byte intervals: 128 changes per entirely white sector, without the old run-start rule. Files, alpha-excluded bytes and bytes outside the drawing range are preserved. Legacy sidecars remain readable; existing calibration targets retain their original recipe and new targets use the new rule. Texture and contrast may change, and more drive/media feedback is needed. CD/BD are unchanged.
- Corrected the DVD EFMPlus model's secondary SY6 code for states 3/4 to match ECMA-267, fixing exact encoding and DSV/boundary replay. Added standard vectors for all sync codes and an encoded-output regression.
- Fixed an extra 32 zero bits in the DVD data-frame EDC calculation, correcting the checksum and subsequent ECC/modulation replay. Added independent polynomial-division checks and complete data-frame vectors.

## [1.4.2] - 2026-11-06

### 中文

- 界面词条缺译时先回退英文，再回退简体中文；标定、工程和预设弹窗沿用同一规则，可继续按词条补充其他界面语言。
- 补齐照片矫正误差、标定记录版本、工程和预设校验弹窗的英文提示；动态数值与参数名不再妨碍翻译匹配，诊断日志保留原始异常。
- 补齐标定拟合原因与状态提示的英文翻译，修复英文界面的侧栏和底部日志混入中文；精简刻录状态快照、校验和取消日志中的重复说明，保留诊断字段与实际状态。
- 修复 CD 流式刻录（音轨与数据盘 DAO）在部分刻录机上首次写入导入区即失败、Windows 刻录接口返回 `0xC0AA0301` 的问题。刻录机报告支持时改用 Raw P-W 子通道（`IMAPI_FORMAT2_RAW_CD_SUBCODE_IS_RAW`，仍为 2448 字节扇区），否则保留 Cooked R-W。IMAPI 在两种格式下生成的镜像逐字节相同，盘面内容不变，只改变告知刻录机的子通道格式；刻录日志会记录实际采用的格式。该问题在 HP BD-RE BU20N（HL-DT-ST，固件 0403）上发现：v1.3.6 以 4× 刻录时返回 `0xC0AA0301`。独立 SCSI 工具的模拟刻录（Test Write）中，Cooked R-W（数据块类型 2）在 4× 与 16× 下第一个写入命令即被拒绝（5/64/00），Raw P-W（数据块类型 3）以 4× 接受了前 3,000 个导入区扇区，SAO 以 4× 正常写入。这些模拟未经过 IMAPI；修复版随后已在同一刻录机上通过 IMAPI 完成 CD-R 流式实盘刻录（用户实测）。
- CD、DVD、蓝光的界面导出改为调用类型化内部生成接口；DVD 设计投影和读回预览直接调用渲染器，支持取消并保留原始异常。命令行参数、控制台输出和退出码留在 CLI 边界，现有命令保持兼容。
- 导出、标定、设计合成与影调预览日志保留异常堆栈、内部异常及诊断上下文；失败导出不再更新读回输入，标定清理失败不再覆盖原始结果，容量读取失败不再显示为任务成功。
- 设计页图层列表支持拖拽排序（整行快照随指针浮动、相邻行缓动让位、Esc 取消回弹）与右键删除；打开工程时图层图片与缩略图改为后台并行解码，界面不再因逐张解码而冻结；最近工程卡片在等待打开时沿圆角循环显示与保存确认同源的光迹。

### English

- Missing UI translations now fall back to English before Simplified Chinese. Calibration, project, and preset popups use the same rules and support translations in the other UI languages.
- Added English popup messages for photo rectification errors, calibration record versions, and project and preset validation. Dynamic values and parameter names no longer prevent translation lookup; diagnostic logs retain the original exceptions.
- Added English translations for calibration fit reasons and status messages, fixing Chinese text in the English sidebar and bottom log. Shortened repetitive explanations in burn snapshots, verification status, and cancellation logs while retaining diagnostic fields and actual states.
- Fixed CD streaming burns (audio and data-disc DAO) failing with `0xC0AA0301` at the first lead-in write on some recorders. When the recorder reports support, the burner now uses raw P-W subchannel data (`IMAPI_FORMAT2_RAW_CD_SUBCODE_IS_RAW`, still 2448-byte sectors) and otherwise keeps cooked R-W. IMAPI produces byte-identical images in both layouts, so the disc content is unchanged and only the subchannel layout announced to the recorder differs; the burn log records the format in use. Found on an HP BD-RE BU20N (HL-DT-ST, firmware 0403): v1.3.6 failed at 4× with `0xC0AA0301`. In Test Write runs with a standalone SCSI tool, cooked R-W (data block type 2) was rejected at the first write command at 4× and 16× (5/64/00), raw P-W (data block type 3) accepted the first 3,000 lead-in sectors at 4×, and SAO wrote normally at 4×. Those runs did not go through IMAPI; the fixed build has since completed a real CD-R streaming burn through IMAPI on the same recorder (user test).
- GUI CD, DVD, and Blu-ray exports now call typed internal generation APIs. DVD projection and readback previews call renderers directly, support cancellation, and preserve original exceptions. Argument parsing, console output, and exit codes remain at the CLI boundary; existing commands remain compatible.
- Export, calibration, design composition, and tone preview logs retain exception stacks, inner causes, and diagnostic context. Failed exports no longer replace readback inputs; calibration cleanup errors preserve the original outcome, and capacity-read errors no longer complete successfully.
- Design layers can be reordered by dragging (a row snapshot follows the pointer, neighbouring rows ease one slot aside, Escape cancels) and deleted from a single-item right-click menu. Layer image and thumbnail decodes now run off the UI thread before a project is applied, so opening a multi-layer project no longer freezes the window. Recent-project cards show a looping save-trail stroke while their project opens.

## [1.4.1] - 2026-11-05

### 中文

- CD、DVD、蓝光刻录分别记录收尾、写入完成与校验状态，蓝光另区分刷新缓存、关闭轨道和封盘。日志保留最后设备状态、写入扇区来源及完整异常，便于区分写入失败与收尾失败；未执行的校验不会显示为成功。DVD 在写入前明确禁用刻后完整性校验并回读确认，设置失败时不开始写入。本轮诊断改动尚未经过实盘验证，不能据此认定 #90 的读回问题已解决。
- 独立辅助标定与工程页面共用底部任务栏；流式刻录的阶段、写入量、速度、进度和日志统一在底栏查看，并可直接取消。任务运行中返回首页仍显示底栏，刻录完成后不再显示上次导出文件的结果按钮。
- 标定照片在长边 8192 px、总像素 32 × 1024 × 1024 的限制内保留原始尺寸，超限时按比例缩小；矫正图按盘面像素密度自适应，最高 5792 × 5792。记录内嵌 PNG 保留工作分辨率，保存重开不再缩图；高位深照片统一为每通道 8 位 BGRA。高分辨率记录占用更多空间，旧版程序可能无法读取；旧记录丢失的细节需重新导入原照片。

### English

- CD, DVD, and Blu-ray recording now log finalization, write completion, and verification separately; Blu-ray also distinguishes cache flushing, track closing, and disc finalization. Logs retain the last device state, the source of sector progress, and complete exception details to distinguish writing failures from finalization failures. Verification that was not performed is never reported as successful. DVD recording explicitly disables post-burn integrity verification and confirms the setting before writing; a configuration failure prevents writing from starting. These diagnostic changes have not yet been validated with physical burns and do not establish that the readback problem in #90 is resolved.
- Standalone calibration and project pages now share the bottom task dock. Streaming burn stages, written bytes, speed, progress, logs, and cancellation are available there, including while visiting Home during a task. Completed burns no longer show result buttons for an earlier exported file.
- Calibration photos retain their original dimensions within limits of 8192 px on the long edge and 32 × 1024 × 1024 pixels, scaling proportionally when either limit is exceeded. Rectification adapts to the disc's pixel density, up to 5792 × 5792. Embedded PNGs preserve the working resolution across save and reopen; high-bit-depth photos use 8-bit-per-channel BGRA. Larger records require more storage and may not open in older versions; recovering detail lost in existing records requires importing the original photo again.

## [1.4.0] - 2026-11-05

### 中文

- 修复流式刻录速度首次选择后可能无法再次切换的问题；普通刻录和独立标定刻录在重新读取盘片速度或切换界面语言后仍可正常改选。
- 设计合成图、缩略图与预览缓存按应用会话隔离；多个实例共用缓存目录时不再覆盖彼此的图片，清理缓存时跳过其他仍在运行的会话。
- CD、BD 固定参考图标定的导出与直接刻录接入图片采样加速，保持原输出字节、环带裁切和明暗极性；固定 sRGB 转换改为精确查表，减少逐字节重复计算。
- CD 音轨、固定参考标定图案及 Mode 1 文件盘默认分块并行采样和影调计算，使用固定上限的缓冲区，并按原顺序交织、写出和报告进度。
- DVD 指定图片和固定参考图标定在采样结果不变的区域内复用计算，覆盖导出、混合文件盘及直接刻录，保持原插值、透明度、影调和输出字节。
- CD、DVD 和 BD 均可在精细标定中拟合三次轨距，系数随预设、工程、生成与预览完整保存和使用；零系数保留原有几何和输出。重复标定使用该盘生成时的完整参数，结果为可直接使用的绝对系数。
- 辅助标定新增粗标定、边界标定和精细标定三个阶段，支持读取盘片容量、保存独立标定记录及专用刻录流程；可从首页或工程侧栏进入，已有工程时保留分组导航，并可返回原页面。
- 标定流程支持现有界面语言；切换语言后，标定设置、阶段提示与刻录页面立即刷新，保留当前参数和测量进度。缺失或为空的译文回退到简体中文，可通过后续 PR 逐步补充。
- 新增土耳其文、西班牙文和捷克文界面：在「设置 → 通用 → 语言」选择「Türkçe」「Español」或「Čeština」并保存后立即生效；土耳其文、西班牙文、捷克文系统也可跟随系统。译文保留格式参数、数值文化和简体中文回退规则。
- 新增西班牙文、土耳其文、捷克文 README；全部 README 顶部语言切换加入 Türkçe、Español、Čeština。原始文档、署名及许可证保留不变。
- 新增威宝 BD-R (64293) 内置预设，参数与 64099 相同，贡献者 bczhc。
- 新增威宝 DVD+R AZO (43498) 内置预设，贡献者 Disappear9。
- 新增铼德 DVD-R E时代系列内置预设，贡献者 bczhc。
- 新增铼德 BD-R 12x 内置预设：内半径 23.98795 mm、外半径 58.02236 mm，贡献者 DeepAQ。
- 新增铼德 DVD+R E时代系列内置预设。
- 新建工程不再按模板自动填充工程名，输入框保持为空；填写名称后才能创建。
- 侧栏工程头卡片右侧新增软盘保存按钮。
- 缓存位置留空时，缓存改为放在 EXE 旁的 `DiscImageStudio-cache` 文件夹；该处不可写入时（例如安装在 Program Files 下）改用 `%LOCALAPPDATA%\DiscImageStudio\cache`，不再写入系统临时目录。自定义缓存位置照常可用。
- 单文件 EXE 不再在启动时把全部内容解压到 `%TEMP%\.net`，解压量从约 475 MB 降到约 8 MB，只剩 WPF 原生 DLL 需要解压。

### English

- Fixed streaming burn speed selection becoming stuck after the first choice. Both project and standalone calibration recording retain working speed selection after refreshing disc speeds or changing the interface language.
- Isolated design composites, thumbnails, and preview caches by application session so instances sharing a cache folder cannot overwrite each other's images. Cache cleanup skips other running sessions.
- Accelerated sampling for CD and Blu-ray fixed-reference calibration exports and direct recording while preserving output bytes, annulus clipping, and polarity. Cached the exact sRGB transfer curve to avoid repeated per-byte calculations.
- CD audio, fixed-reference calibration artwork, and Mode 1 drawing payloads now sample and apply tone in bounded parallel blocks while preserving interleaving, output order, and progress callbacks.
- DVD image and fixed-reference calibration generation reuse samples within regions proven to retain the same result, across exports, hybrid images, and direct recording, preserving interpolation, alpha, tone, and output bytes.
- Fine calibration now supports cubic track pitch for CD, DVD, and Blu-ray, with coefficients preserved across presets, projects, generation, and preview. Zero coefficients retain the existing geometry and output. Recalibration uses the complete geometry that generated the photographed disc and returns absolute coefficients ready to apply.
- Added coarse, boundary, and fine calibration stages with disc-capacity reading, independent calibration records, and a dedicated recording workflow. Open calibration from Home or the project sidebar, retain grouped navigation while a project is open, and return to the originating page.
- Calibration supports the available interface languages. Language changes immediately refresh calibration settings, stage guidance, and recording pages while preserving parameters and measurement progress. Missing or empty translations fall back to Simplified Chinese and can be added in follow-up PRs.
- Added Turkish, Spanish, and Czech interfaces. Choose Türkçe, Español, or Čeština under Settings → General → Language, and save to apply immediately; Turkish, Spanish, and Czech system locales can also follow the system. Translations preserve format arguments, numeric culture, and the Simplified Chinese fallback.
- Added Spanish, Turkish, and Czech READMEs; every README language switcher now includes Türkçe, Español, and Čeština. Original documents, attribution, and licenses are preserved.
- Added a built-in Verbatim BD-R (64293) preset with the same parameters as 64099, contributed by bczhc.
- Added a built-in Verbatim DVD+R AZO (43498) preset, contributed by Disappear9.
- Added a built-in Ritek DVD-R E-Series preset, contributed by bczhc.
- Added a built-in Ritek BD-R 12x preset with inner radius 23.98795 mm and outer radius 58.02236 mm, contributed by DeepAQ.
- Added a built-in Ritek DVD+R E-Series preset.
- New projects leave the project name empty instead of filling it from the selected template; the project can be created once a name is entered.
- Added a floppy-disk Save button on the right of the sidebar project header.
- When the cache location is left empty, the cache now lives in a `DiscImageStudio-cache` folder next to the EXE. If that folder is not writable (for example under Program Files), `%LOCALAPPDATA%\DiscImageStudio\cache` is used instead; the system temporary folder is no longer used. Custom cache locations work as before.
- The single-file EXE no longer extracts its entire contents to `%TEMP%\.net` at startup. Extraction drops from about 475 MB to about 8 MB; only the WPF native DLLs still need to be extracted.

## [1.3.6] - 2026-11-02

### 中文

- **实验性功能：Linux 和 macOS 离线命令行版。** 可从图片生成 CD 音轨（WAV+CUE 或 RAW）、CD 数据盘、DVD 和蓝光 ISO。两个系统均提供 x64 和 ARM64 自包含下载包。解压后运行 `./disc-image-studio --help` 查看命令。
- Windows 桌面版继续使用 WIC 读图。跨平台 CLI 采用 StbImageSharp 读图和 StbImageWriteSharp 写 PNG，复用相同的盘面编码算法。
- 新增日文界面：在「设置 → 通用 → 语言」可选择「日本語」；系统语言为日文（`ja`、`ja-JP`）时跟随系统也使用日文。所有翻译资源新增 `ja` 文本，`ui-snapshot --language` 支持 `ja`。
- 新增日文 README（`README.ja.md`），所有 README 顶部的语言切换链接加入日本語。

### English

- **Experimental feature: offline CLI for Linux and macOS.** Generate CD audio tracks (WAV+CUE or RAW), CD data images, and DVD or Blu-ray ISOs from an image. Self-contained x64 and ARM64 archives are available for both systems. Extract the archive and run `./disc-image-studio --help` to list commands.
- The Windows desktop app continues to decode images with WIC. The cross-platform CLI uses StbImageSharp to read images and StbImageWriteSharp to write PNGs, with the same disc encoding algorithms.
- Added a Japanese interface. Choose 日本語 under Settings → General → Language; Japanese system locales (`ja`, `ja-JP`) now use Japanese when following the system. Every translation entry gains `ja` text, and `ui-snapshot --language` accepts `ja`.
- Added a Japanese README (`README.ja.md`) and a 日本語 link to the language switcher at the top of every README.

## [1.3.4] - 2026-11-02

### 中文

- 新增简体中文、繁體中文与英文界面，在「设置 → 通用 → 语言」选择跟随系统或指定语言；保存后重启生效，取消不修改偏好。系统语言为台湾、香港、澳门或 `zh-Hant` 时使用繁体，其他中文系统使用简体，其余使用英文。导航、工作区、弹窗和应用状态提示共用嵌入式翻译资源，工程数据与数值格式保持不变；底层模块与操作系统返回的诊断信息保留原文。
- 语言与主题默认跟随系统；缺失或无效的偏好回落到跟随系统，已保存的有效选择继续保留。设置保存在 `%LOCALAPPDATA%\DiscImageStudio\settings.json`，损坏或更高版本的设置文件回落到默认值，不写入 `.discproj`。
- 标题栏右侧新增常驻「齿轮 + 设置」按钮，主页和所有工作区均可直接打开设置，并保留 Ctrl+, 快捷键。长工程名自动省略；「…」菜单提供「关于…」，移除重复的设置菜单项和原侧栏「关于」入口。
- 新增「通用」「存储」两类设置。深色、浅色与跟随系统主题保存后立即应用到所有窗口，侧边栏、分段控件、盘面画布与任务坞同步刷新。
- 新增默认工程位置与缓存位置设置，留空分别使用系统文档目录和临时目录。可查看缓存占用、清空本应用缓存或打开应用数据目录；清空操作仅删除白名单内的缓存图片与 sidecar，不影响工程和导出文件，任务运行期间禁用。
- CD、DVD 与蓝光预览的分辨率上限统一为 8192×8192，覆盖设计投影、成品读回及导出预览图；各盘型的默认预览尺寸保持不变。
- 新增完整英文与繁体中文 README，三份 README 顶部均提供语言切换链接，并补充语言配置与翻译资源维护说明。
- 开发截图命令 `ui-snapshot` 新增 `--language zh-CN|zh-TW|en|system`、`--theme dark|light|system`、`--settings-dialog` 和 `--settings-view general|storage`，可验证不同语言、主题和设置分类的布局。设置窗口截图默认 760×600，使用一次性设置文件，不修改真实配置。

### English

- Added Simplified Chinese, Traditional Chinese, and English interfaces. Choose a language or follow the system under Settings → General → Language; save and restart to apply it, or cancel to leave preferences unchanged. System locales for Taiwan, Hong Kong, Macao, or `zh-Hant` use Traditional Chinese; other Chinese locales use Simplified Chinese, and all others use English. Navigation, workspaces, dialogs, and application status messages share embedded translation resources. Project data and numeric formatting are unchanged; diagnostics returned by lower-level modules and the operating system retain their original language.
- Language and theme now follow the system by default. Missing or invalid preferences fall back to the system, while existing valid saved choices are preserved. Settings are stored in `%LOCALAPPDATA%\DiscImageStudio\settings.json`; corrupt files or files with a newer schema fall back to defaults. Settings are not written to `.discproj` files.
- Added a persistent gear-and-Settings button on the right of the title bar, available on the home page and every workspace, while retaining the Ctrl+, shortcut. Long project names are truncated. The “…” menu provides About; the duplicate Settings menu item and former sidebar About entry were removed.
- Added General and Storage settings categories. Dark, light, and system themes apply immediately to all windows after saving, including the sidebar, segmented controls, disc canvas, and job dock.
- Added default project and cache folder settings, using the system Documents and temporary folders respectively when left empty. View cache usage, clear the app’s cache, or open the application data folder. Cache clearing deletes only allowlisted cached images and sidecars, leaves projects and exported files untouched, and is disabled while a job is running.
- Unified the maximum CD, DVD, and Blu-ray preview resolution at 8192×8192 for design projections, artifact readback, and exported previews. Default preview sizes for each media type remain unchanged.
- Added complete English and Traditional Chinese READMEs, with language links at the top of all three versions, plus documentation for language configuration and translation resource maintenance.
- Extended the developer `ui-snapshot` command with `--language zh-CN|zh-TW|en|system`, `--theme dark|light|system`, `--settings-dialog`, and `--settings-view general|storage` to verify layouts across languages, themes, and settings categories. Settings dialog snapshots default to 760×600 and use a temporary settings file without modifying real preferences.

## [1.3.3] - 2026-10-28

- 新增 Verbatim BD-R (64099) 内置预设，采用用户保存的实测半径、扇区数与映射极性，GUI 和命令行均可使用。
- 修复蓝光标定流式刻录的来源摘要误显示为 CD，并按标定记录显示蓝光位长、3T/6T 极性和绘图范围。
- 流式刻录底部固定显示设备已写入量／总量、实时速率和对应倍速；准备、传输、封盘及诊断信息记录到日志。传输量不再覆盖实际写入进度，封盘或长时间没有新写入数据时清除旧速率。
- BD 标定范围映射接入普通图片生成器的有界并行批处理，并复用四点几何采样；保留原始标定回调、绘图范围、极性和顺序输出。自定义映射仍须明确允许并行。
- CD 标定复用图片生成的四点几何采样，DVD 标定与码字回调复用已有变轨距 SIMD 路径；保留逐点标量三角函数、回调顺序及音轨交织，DVD 恒定轨距仍使用既有标量回退。
- CD 文件模式补齐四点采样，按各字节经过交换与 CIRC 映射后的独立物理位置计算，保留文件区、调色索引、扰码及盘尾边界处理。

## [1.3.2] - 2026-10-27

- 工程设置对话框可直接换用当前盘型的其他预设；切换前先确认覆盖，取消后保留原有参数且不改变选中项。
- 主题蓝统一为深色 `#3385FF`、浅色强调 `#74C0FF`；滑钮选择器的选中项改用主题色填充，高度与输入框对齐，选择框右侧的展开箭头缩为紧凑尺寸。
- 新建工程和工程设置中可右键“自定”预设删除，删除会同步清理最近模板记录。
- 设计页画布缩放栏常驻右下角，新增“−”“+”按钮并把可缩放范围扩到 25%–1200%（此前只能放大）；“适应窗口”改用刷新图标。缩放仍可用 Ctrl/Alt+滚轮，中键拖动平移。
- 主页模板卡片的灰色说明显示盘型（“CD”“DVD”“蓝光”）。
- 修复预设切换确认框误响应普通按键、蓝光切换后另存预设沿用旧极性，以及删除最近预设后盘型与下一选中项不一致的问题。

## [1.3.1] - 2026-10-26

- CD 新增随盘文件模式：未选择文件夹时保留 WAV 音轨，选择文件夹后生成 ISO9660/Joliet Mode 1 数据盘；文件支持内圈或外圈放置，工程保存、ISO 导出与流式刻录共用布局。
- CD 设计投影和带布局信息的成品读回以黄色显示文件区，保留完整盘面坐标；支持中文目录与文件，随盘内容保持原始字节。
- CD 数据盘流式刻录改为与音轨共用 DAO 会话和容量边界处理，修复铼德医疗水蓝盘预设误报“剩余容量不足”的问题；保留 ATIP 标定的 359,845 扇区及既有末端限制。
- 新增 Mode 1 校验码、DAO 数据轨标识、前向流和内外圈 ISO 完整还原回归。CD 数据盘 DAO 已通过软件及原生内存镜像验证，尚未完成实盘刻录验证；即时刻录仍为实验性功能。

## [1.3.0] - 2026-10-26

- 全部应用、光盘模块与工具升级到 .NET 10，CI、EXE 和 MSIX 打包使用 .NET 10 SDK；发布的自包含 EXE 继续无需另装运行时。
- DVD／蓝光设计页新增“随盘文件”区域，可直接选择文件夹和内圈／外圈位置，与导出页共享设置。
- DVD 和蓝光的文件与图案混合模式新增“文件位置：内圈／外圈”，默认内圈；工程保存、ISO 导出与流式刻录使用同一布局。外圈文件模式保留必要的文件系统信息，绘图范围自动避开文件区。
- DVD／蓝光的设计投影和带布局信息的成品读回，以黄色显示文件区（含文件系统），并显示图例；颜色仅用于预览。没有配套布局信息的外部镜像不推测文件占用范围。

## [1.2.0] - 2026-10-26

### 工程与设计

- 重构为设计、预览、导出、刻录和辅助标定五个工作区，CD、DVD、BD 共用当前工程与盘片参数。
- 新增 `.discproj` 工程保存与打开、最近工程缩略图、未保存检查和工程设置；支持将当前盘片参数保存为用户预设。
- 新增多图层设计画布，可导入多张图片、移动、缩放、旋转、调整层次与显隐；导出和刻录统一使用高分辨率合成图。原自动环形复制入口移除，布局由设计画布管理。
- 新增任务坞，集中显示运行日志、任务进度、取消入口及输出位置；导出与刻录页面统一展示当前工程来源。

### 影调、蓝光与标定

- 影调集中到设计页，支持 0–100% 抖动强度、亮度调整、sRGB 线性化及 CD 二阶／四阶灰度。新工程默认 75% 抖动；缺少影调设置的旧工程保留原二值映射。
- 为 CD、DVD 和 BD 生产命令增加可选影调参数；CD/DVD 采用确定性的逻辑字节抖动，BD 在源图分辨率处理，生成、流式刻录与设计投影采用一致设置。
- BD ISO 新增配套 JSON 元数据，记录盘片几何、绘图范围、映射极性、影调与 UDF 时间戳；预览打开 ISO 时可自动恢复对应参数。
- 改进辅助标定拟合：自动扩展搜索范围，蓝光在残差仍较大时继续扩宽；增加最大偏差、异常点标记与误差门槛，避免应用偏差过大的结果。

### 兼容性与迁移

- 工程与用户预设统一支持 CD/DVD/BD；保留旧工程 schema 1、旧预设 schema 2/3 及已有 CD 音轨的读取兼容。生产命令保留已有参数，新增影调参数均为可选。
- 开发截图命令 `ui-snapshot` 的页面编号现为：0 主页、1 设计、2 预览、3 导出、4 刻录、5 标定；关于窗口改用 `--about-dialog`，`--live-processing` 已移除。
- 蓝光与直接刻录仍为实验性功能；硬件实测范围见 `docs/VALIDATION.md`，不将自动化测试等同于新增实盘验证。

## [1.1.0] - 2026-10-25

### 新增

- 单层 25 GB BD-R 实验模式：源图布局、3T/6T 映射与反转、盘片参数和预设、实时预览、ISO 生成及辅助标定，GUI 与命令行均可使用。暂不支持双层盘，实盘明暗仍需验证。
- 蓝光内圈文件支持 UDF 2.01 和大于 4 GiB 的单文件；生成与流式刻录共享完整盘面几何、分段文件布局和有界数据流。
- 全新未格式化 BD-R 的无备用区顺序刻录，保留完整 12,219,392 扇区。开刻前校验容量、空白状态和起始地址；不自动缩容、不在失败后自动重写，增加分块超时和清理诊断。
- CD、DVD、BD 制作与实时预览可开启黑白抖动和独立 sRGB 伽马校正，默认关闭。文件生成、流式刻录及源图预览采用一致的处理选项。
- 辅助标定支持照片透视矫正、曲线描点拟合、参数快照与回填；DVD 使用三次轨距模型，内置已验证的 Verbatim DVD-R AZO (43533) 预设，并附校准前后实拍和记录。
- 新增独立黑白抖动工具、DVD 灰阶实验工具与蓝光字节分析工具，附使用说明和可复现验证步骤。

### 改进与修复

- 实时预览支持 CD/DVD/BD 源图投影和已有产物读回，可指定分辨率，使用适应窗口、1:1 像素、鼠标缩放及拖动；修复预设同步、浏览命令及 DVD 读回采样位置。
- CD/DVD 使用统一的黑白二阶映射；黑白输入保留离散像素，环形复制缩放后重新抖动。旧多阶产物的读回继续识别原有灰阶。
- CD 与 DVD 采样加入 SIMD 与边界回退，蓝光采用有界并行生成及整数 SIMD；回归测试验证标量、并行与流式输出一致性。
- 刻录页显示按设备确认写入数据计算的速率，串行化设备查询与刻录操作；准备及封盘阶段不显示过时速率。实际刻录仍为实验性功能。
- 修复多显示器、不同 DPI 与任务栏位置下的最大化边界，统一参数的小数精度；主页和最近任务支持蓝光，CD WAV 输出可选择是否生成 CUE。
- 标定记录使用 schema 6，不接受旧版图案记录；盘片预设及已有音轨/镜像的兼容范围见对应文档。所有实际几何与亮度结论仍需结合具体设备和介质验证。

### 开源与文档

- 采用 GPL-3.0-or-later，与 CDImage 衍生的 CD 交织实现保持兼容；补齐原作者版权与致谢。
- 保留第三方组件和 HarmonyOS Sans 字体各自的完整许可；“关于”页可离线查看，单文件 EXE 同样包含授权文本。
- README 补充 ECMA-267、ECMA-359、ISO/IEC 30190:2021 等标准及官方链接；整理工具入口、验证文档、缓存忽略规则与个人路径示例。

## [1.0.26] - 2026-09-12

- 首次提供 GitHub Release：Windows x64 单文件 EXE 已内置 .NET，下载后即可运行。
- 包含 CD WAV + CUE / RAW 音轨生成、DVD 绘图 ISO 与混合数据盘，以及 GUI、命令行、实时预览、环形复制和可编辑盘片预设。
- 包含 CD WAV 与流式音轨的字节序修正，保留旧 RAW 兼容；用户实盘反馈确认修复后无重影。
- 包含实验性 CD/DVD 直接刻录。已有部分实盘反馈，不同设备、盘片及各刻录入口的验证覆盖仍有限。
- 发布程序由 GitHub Actions 构建，执行模块回归、DVD 自检、最终单文件 RAW/WAV 生成与 GUI 截图检查；发布说明提供 SHA-256 下载校验值。
- 统一版本来源，修改版本号和对应发布日志后才生成发布包；支持预发布版本及手动重试，保留独立的 MSIX 打包流程。

## 首次 GitHub 发布前的开发记录

以下为累计开发过程记录，验证状态反映各条记录对应的阶段；当前验证范围以 `docs/VALIDATION.md` 为准。

- 新增 GitHub Actions 发布策略：以 `Directory.Build.props` 统一维护版本，仅在合并到 `main` 的变更同时修改版本与发布日志且完整版本变化时，经过 CI 和最终单文件验证后自动生成版本标签、自包含 Windows x64 EXE 与 GitHub Release 草稿；支持 alpha/beta/rc，发布说明包含 SHA-256，维护者检查后公开，已公开制品不覆盖。MSIX 保留手动通道并复用 CI，默认读取同一版本配置。
- CD 文件输出默认改为标准 WAV（44.1 kHz、16 位、双声道 PCM），同时生成 `FILE ... WAVE` CUE：cdrecord 可直接读取 WAV，ImgBurn 打开配套 CUE；旧 `.raw` 仍保持大端字节序。WAV 在延迟交织完成后转为小端，CD 流式刻录显式采用相同的无头小端 PCM，修正与 IMAPI 音频输入约定不一致的问题。音轨预览支持 WAV 的 RIFF 块解析与字节序归一化。新增非对称音频样本和 Windows RAW 镜像字节保留回归。
- DVD 流式刻录新增混合文件夹支持：先计算 ISO9660/Joliet 布局，再通过不可回退的前向流依次写入内圈文件、ECC 对齐填充和外圈图案，无需保存完整临时 ISO。
- 将环形复制的正式生成中间图由 2048×2048 提升至 8192×8192，保存预览使用 4096×4096，实时预览使用 2048×2048；运行日志新增每份副本的实际像素尺寸，避免低分辨率中间图损失细节。
- 新增盘片预设：CD 80/74 分钟、12 cm 单层 DVD、8 cm 单层 Mini DVD；生成页与实时预览页双向同步，手动改值后自动切换为“自定义参数”。
- 新增“铼德医疗水蓝盘（实测）”CD 预设：24.911275–57.931155 mm、359845 扇区。
- 新增“Verbatim CD-R AZO (43438)（实测）”CD 预设：24.837775–58.020875 mm、359848 扇区。
- 新增“Verbatim DVD-R AZO (43533)（实测）”DVD 预设：23.9968875–57.9779875 mm、2,297,888 扇区。
- CD/DVD 盘片预设改为分层 JSON：内置层随应用更新，用户层首次生成自定义 CD/DVD 参数并只保存新增、同 ID 覆盖和禁用项；预设只包含容量与生成几何，不再混入图片画布或实测校准参数。格式预留默认文本语言和名称/说明资源键，后续接入 i18n 无需破坏用户 JSON。界面支持打开和重新加载用户层，无效编辑会显示具体错误并保留上一次有效预设。
- 盘片预设的小数参数改为按实际精度完整显示，不再固定截断为 3 位或 6 位小数。
- 修正 CD 内置预设、GUI 和命令行默认扫描速度：由旧工具沿用的 1100 mm/s 改为 ECMA-130 范围下限 1200 mm/s，并增加标准范围回归检查。
- 新增实验性的 CD 与纯绘图 DVD 流式直接刻录：生成器通过 64 MiB 有界内存缓冲向 Windows IMAPI2 连续供数，不保存完整 ISO/RAW；自动化测试已通过，实体盘验证仍待完成。
- 修复 CD 流式刻录在部分刻录机上因冗余设置 `DoNotFinalizeMedia` 而返回 `0xC0AA0502` 的问题；会话关闭和防缓冲欠载改用 IMAPI2 安全默认值，并确保清理阶段的 `ReleaseMedia` 错误不会覆盖真正的首个写入错误。
- CD 流式刻录由会额外占用 2 扇区并强制拒绝音轨的 Track-at-Once 改为 Raw CD/DAO：通过 Windows RAW 镜像构造器流式加入 lead-in、Cooked R-W 子通道和 lead-out，使实测 359,845 扇区内容刚好落在同一盘片的 DAO 合法边界；另保留最多 3 个 DAO 扇区的受限末端 overburn，超过时仍会在写入前拒绝。设备不报告速度时使用实机确认可接受的约 16× 稳定设置回退。
- 流式刻录界面新增当前盘片支持的写入速度读取与选择；自动模式在刻录前重新读取当次 CD/DVD 介质并选择最低可用速率以降低错误率，手动选择保持不变，刻录时记录设备实际采用速度。
- 根据 DVD 实盘结果反转 dispersion 黑白字节极性：黑色改用扰码域 `0xA5`，白色改用 `0x92`；纯绘图与混合流式刻录同步生效。
- 将流式刻录的有界内存缓冲从 16 MiB 提升到 64 MiB，并将开刻前预填充从 8 MiB 提升到 32 MiB，为 16× DVD 写入提供更充足的生成抖动余量。
- MSIX 打包前现在会清理 RID 构建缓存、检查发布期间的源码变动，并直接验证包内刻录组件已移除旧 `DoNotFinalizeMedia` 设置，防止主程序版本已更新但嵌入旧刻录 DLL。
- 新增独立 `DiscImageStudio.Burning` 抽象层、刻录机枚举、空白盘/容量检查、二次确认、进度和取消处理；原镜像文件工作流保持不变。
- 新增“环形复制（自动份数）”图片处理模式：同一源图自动复制多份、朝光盘外侧环形排列，并支持可调内外安全边界。
- 修正环形复制布局：份数保持自动计算，空间不足时统一等比缩小所有副本，确保不变形、不裁切且彼此不重叠。
- 新增独立 `DiscImageStudio.Imaging` 图片预处理层，CD、DVD 共用，并可供未来蓝光模块复用。
- 独立实时预览页现同时支持 CD 与 DVD；对应参数双向同步，参数变化经 450ms 防抖后自动刷新。
- CD 几何预览改为与 DVD 一致的采样平均灰度模式。
- DVD 界面与模块固定使用快速纹理算法、快速输出和顺时针（CW）螺旋，不再显示算法与方向选择框。
- 新增界面内 PNG 图片加载与展示，并修复日志框高度受全局输入框样式限制的问题。
- 修复 .NET 9 WPF 使用 `StreamSource` 加载 PNG 时错误启用 URI 缓存忽略选项而导致预览加载失败的问题。
- 修复签名 MSIX 中 CD 生成/预览写入运行日志时因 WPF 与不变区域模式冲突而崩溃的问题。
- 新增统一 WPF GUI。
- 合并 CD-DA 与 DVD 工作流，同时保持 DVD 引擎隔离。
- 新增 `DiscImageStudio.Core` 光盘模块抽象层。
- 新增 CD 与 DVD 模块适配器，并为未来蓝光模块预留扩展点。
- 新增 MSIX 打包、商店资源、GitHub CI 与发布文档。
