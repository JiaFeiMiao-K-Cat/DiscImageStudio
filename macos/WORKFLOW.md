# macOS 工作流界面规范（与 Windows 桌面版一致）

本文件是 macOS SwiftUI 应用的目标形态：**不是命令目录浏览器**，而是与 Windows 桌面版同一套工程式工作流。

Windows 版的可视化基准（截图与 XAML）：

- 主页：`docs/images/ui-home.png`、`src/DiscImageStudio.App/Views/HomeView.xaml`
- 设计页：`docs/images/ui-design.png`、`src/DiscImageStudio.App/Views/DesignView.xaml`
- 导出页：`docs/images/ui-export.png`、`src/DiscImageStudio.App/Views/ExportView.xaml`
- 预览 / 刻录 / 标定 / 设置：`src/DiscImageStudio.App/Views/{LivePreviewView,BurnView,CalibrationView,SettingsWindow}.xaml`
- 视觉令牌：`src/DiscImageStudio.App/Themes/Tokens.xaml`（深色 #121212、表面 #18181B/#26262B、品牌蓝 #3385FF）
- 参数与命令构造的权威实现：
  - 导出：`src/DiscImageStudio.App/ViewModels/ExportViewModel.cs` 的 `GenerateCdAsync` / `GenerateDvdAsync` / `GenerateBluRayAsync`
  - 预览：`src/DiscImageStudio.App/ViewModels/LivePreviewViewModel.cs` 的 DVD `calibrate` / `simulate` 构造、CD `cd-preview-warp` / `cd-preview-track` 参数（来自 CLI 命令目录）、蓝光投影/读回
  - 工程读写：`src/DiscImageStudio.Core/ProjectJsonStore.cs`
  - 预设：`src/DiscImageStudio.Core/DiscPresetJsonStore.cs` 与 `built-in-disc-presets.json`
  - 设计合成：`src/DiscImageStudio.App/Services/DesignComposer.cs`、`ViewModels/DesignLayerViewModel.cs`

## 1. 屏幕结构

无工程时：

```text
┌ 侧边栏（可收起）┐ ┌──────────── 主页 ────────────┐
│ 主页            │ │ Disc Image Studio 标题/副标题  │
│                 │ │ [打开工程] [新建工程]          │
│                 │ │ 从模板开始：空白 / 各预设卡片   │
│                 │ │ 最近工程（缩略图 + 名称 + 盘型）│
│ 底部：设置/关于  │ └──────────────────────────────┘
└─────────────────┘
```

打开工程后（侧边栏变为工作区导航，顺序即工作流）：

```text
设计 → 预览 → 导出 → 刻录 │ 标定
```

- 顶部页眉：工程名 + 盘型徽标、阶段条（设计/预览/导出/刻录/标定，可点击直达）、右侧「下一步」。
- 底部任务坞：当前任务状态（进行中/进度/成功/失败），运行中显示取消。
- 设置/关于常驻侧边栏底部（⌘, 打开设置）。

## 2. 各页要求

### 主页

- 标题区 + 「打开工程…」「新建工程」（⌘N）。
- 「从模板开始」：第一张「空白工程」+ 每类盘型的预设卡片（CD/DVD/BD，来自 `presets --json`）；点击即按该预设新建工程。
- 「最近工程」：缩略图（工程内 `thumbnailPngBase64` 或默认盘面）、名称、盘型标签、路径；点击打开；「清空」。
- 新建工程流程（对话框或 sheet）：盘型（CD/DVD/BD）+ 预设 + 工程名 + 保存位置（默认 `~/Documents/DiscImageStudio Projects`）+ 输出位置。确认后写 `.discproj` 并进入设计页。

### 设计页

三栏（窄窗口折行）：左「图层」、中「画布」、右「属性 + 影调」。

- 图层：导入图片（PNG/JPEG）、列表（缩略图、名称、坐标摘要、可见性眼睛）、上移/下移/删除；选中行与画布双向同步。
- 画布：以盘心为原点、毫米坐标；显示盘体轮廓与中心孔；支持拖动画布平移、角点缩放、层拖动/旋转/缩放（选框与控制点）；缩放百分比 + 适应/重置。
- 属性：图层名、X/Y(mm)、旋转(°)、缩放(%)、图片路径；变更即更新画布。
- 影调：抖动强度 0–100%（默认 75）、灰阶档位（CD 2/4，DVD/BD 固定 2）、亮度 −64…64、sRGB 线性化开关；实时盘面缩略预览。
- 随盘文件：文件夹选择（空则整盘绘图）+ 内圈/外圈。
- 页眉「下一步」→ 预览页；编辑 450ms 防抖后重新合成。

### 预览页

- 模式分段：「设计投影」/「成品读回」。
- 盘型固定跟随工程；参数区显示只读生成几何摘要 +「编辑…」。
- 实测参数（实测内/外半径；DVD 三次项；BD 极性）与预览尺寸（CD 1600、DVD 1200、BD 2048，上限 8192）。
- 「刷新预览」按钮 + 返回的 PNG 展示（等比适配、棋盘格透明背景、双击开预览）。
- 底部：生成几何 vs 实测几何偏差提示；「导出预览 PNG」。
- 失败时显示 CLI stderr 摘要与重试。

### 导出页

- 输出路径（NSOpenPanel/SavePanel，默认取工程 outputs.outputPath）。
- CD：文件模式（音轨 WAV+CUE / RAW）与数据盘（ISO）由是否选择随盘文件决定；交织、写 CUE 开关。
- DVD/BD：ISO 输出、随盘文件、内/外圈、卷标。
- 「生成」按钮 → 底部任务坞显示进度/日志；成功后给出输出路径与「在 Finder 中显示」。
- 生成成功即把产物路径回填到预览的成品读回输入。

### 刻录页

沿用现有文件模式（`BurnService` + `drutil`）与流式模式（`BurnStreamService` + `disc-burn-stream`，实验性）；界面风格与 Windows 刻录页一致：写入内容来源、刻录机、速度、介质状态、二次确认。

### 标定页（本迭代简化版）

- 说明标定流程（拍摄要点、框选实拍盘片外缘与中心孔）。
- 允许手工填写实测内/外半径（DVD 另含三次项）并写回工程；从预览页跳转。
- 完整的照片框选/曲线拟合在后续迭代提供（Windows 的 `CalibrationPhotoCanvas` 依赖 WPF 渲染，不在本轮范围）。

### 设置 / 关于

- 设置：外观（跟随系统/浅色/深色）、默认工程位置、缓存位置（查看占用/清空）、CLI 路径（自动/自定义/源码 dotnet run）、语言（暂仅简体中文）。
- 关于：版本、GPL-3.0-or-later、上游仓库、第三方声明。

## 3. Swift 侧 Kit（工作流层，`DiscImageStudioKit`）

新增文件（Lead 负责，UI 只调用）：

```swift
// ProjectModel.swift
public enum DiscFamily: String, Codable, Sendable, CaseIterable { case cd, dvd, bd }
public struct ProjectGeometry: Codable, Sendable, Equatable { innerRadiusMm, outerRadiusMm, sectors, pitchLinear, pitchQuadratic, pitchCubic }
public struct DesignLayer: Codable, Sendable, Equatable, Identifiable { id, name, imagePath, visible, translateXMm, translateYMm, rotationDeg, scale }
public struct ProjectOutputs: Codable, Sendable, Equatable { outputPath, writeCue, dataDirectory, volumeLabel, interleave, filesAtOuterRing }
public struct ProjectTone: Codable, Sendable, Equatable { ditherStrength, levels, brightness, srgbLinearization }
public struct ProjectDocument: Codable, Sendable, Equatable {
    schemaVersion, name, discFamily, geometry, layers, outputs,
    calibrationSessionPath: String?, thumbnailPngBase64: String?,
    createdUtc: String, modifiedUtc: String, tone: ProjectTone?, bluRay: ProjectBluRaySettings?
    public func validate() throws
    public func canvasOuterRadiusMm() -> Double

    // 新建
    public static func make(name: String, family: DiscFamily, preset: DiscPreset,
                            outputPath: String) -> ProjectDocument
}

// ProjectStore.swift
public struct ProjectStore: Sendable {
    public static var defaultProjectsDirectory: URL   // ~/Documents/DiscImageStudio Projects
    public init()
    public func load(from url: URL) throws -> ProjectDocument
    public func save(_ document: ProjectDocument, to url: URL) throws
    public func thumbnailBase64(compositeURL: URL) -> String?   // 256px PNG
}
public struct RecentProject: Codable, Sendable, Equatable, Identifiable { path, name, family, modifiedUtc, thumbnailPngBase64 }
public struct RecentProjectsStore: Sendable { load(), record(project:url:thumbnail:), removeAll(), list }

// PresetCatalog.swift
public struct DiscPreset: Sendable, Equatable, Identifiable {
    // 统一三类的只读投影：id/displayName/description/family/geometry(pitch 默认 0)/invertMapping?
}
public struct PresetCatalog: Sendable { public func presets(for family: DiscFamily) -> [DiscPreset] }
public struct PresetLoader: Sendable {
    public init(cliInvocation: CliInvocation)
    public func load() async throws -> PresetCatalog   // `disc-image-studio presets --json`
}

// DesignComposer.swift
public struct DesignComposer: Sendable {
    public init()
    public func render(layers: [DesignLayer], canvasOuterRadiusMm: Double, outputSize: Int, to destination: URL) throws
    public func renderPNGData(layers: [DesignLayer], canvasOuterRadiusMm: Double, outputSize: Int) throws -> Data
    public static func fittedSizeMm(pixelWidth: Int, pixelHeight: Int, canvasOuterRadiusMm: Double) -> (widthMm: Double, heightMm: Double)
}

// ExportService.swift
public struct ExportPlan: Sendable, Equatable { family, arguments, outputURL, expectedFamily }
public struct ExportService: Sendable {
    public init(cliInvocation: CliInvocation)
    public static func makePlan(project: ProjectDocument, compositeURL: URL, outputURL: URL) throws -> ExportPlan
    public func run(_ plan: ExportPlan) -> CliJob
}

// PreviewService.swift
public enum PreviewMode: String, Sendable { case designProjection, artifactReadback }
public struct PreviewRequest: Sendable, Equatable { ... 实测半径/轨距、尺寸、每扇区采样、来源产物路径 }
public struct PreviewPlan: Sendable, Equatable { mode, arguments, outputURL, isBluRay }
public struct PreviewService: Sendable {
    public init(cliInvocation: CliInvocation)
    public static func makePlan(project: ProjectDocument, compositeURL: URL, request: PreviewRequest, outputURL: URL) throws -> PreviewPlan
    public func run(_ plan: PreviewPlan) -> CliJob
}
```

行为约束：

- 工程 JSON 与 Windows 完全兼容（camelCase、schemaVersion 2；读 schema 1 视为 CD/DVD；保存写 2；未知字段忽略；原子写）。
- 设计合成：图层数组索引 0 为最上层；绘制时自底向上；每层初始尺寸 = 较大边跨 `2 × canvasOuterRadiusMm`；单位 mm；输出正方形 PNG，中心即盘心；导出 8192 px，预览 2048 px。
- 所有生成/预览/刻录仍由 CLI 子进程完成，参数构造逐字对齐上面列出的 Windows ViewModel。

## 4. 本轮并行边界

| 工作流 | 写入范围 |
| --- | --- |
| Lead | `macos/Sources/DiscImageStudioKit/`（工作流层全部新文件）、`macos/WORKFLOW.md`、`macos/Package.swift`、README/CHANGELOG、最终集成 |
| SwiftUI | `macos/Sources/DiscImageStudioMac/` |
| CLI | `src/DiscImageStudio.Cli/`、`src/DiscImageStudio.BluRay/`（仅新增预览命令相关文件）、`tests/test_cli_package.py`、`docs/ARCHITECTURE.md` |
| 测试 | `macos/Tests/DiscImageStudioKitTests/` |
| 打包/文档 | `macos/scripts/`、`macos/Resources/`、`docs/MACOS_GUI.md`、`.github/workflows/macos-gui.yml` |

## 5. 命令映射（权威）

| 动作 | CLI |
| --- | --- |
| 预设目录 | `presets --json` |
| CD 导出（音轨） | `cd-generate --input composite --output out.wav --r0 --r1 --sectors --tone-* [--interleave --cue]` |
| CD 导出（数据盘） | `cd-generate ... --data-dir DIR --volume-label LABEL --file-position inner|outer` |
| DVD 导出 | `solve --image composite --iso-output out.iso --total-sectors --inner-radius-mm --outer-radius-mm --pitch-linear/-quadratic/-cubic --spiral-direction cw --image-threshold 128 --alpha-threshold 1 --tone-* --algorithm dispersion --iterations-per-block 1 --fast-output true --volume-label ... [--fill-sectors N | --data-dir DIR --file-position ...]` |
| BD 导出 | `bd-generate --input composite --output out.iso --total-sectors --r0 --r1 --invert --volume-label ... --tone-* [--data-dir --file-position]` |
| CD 设计投影 | `cd-preview-warp --input composite --output png --r0 --r1 --sectors --velocity 1200 --theta0 0 --outer 57.5 --actual-r0 --actual-r1 --tone-* --size N [--data-dir --volume-label --file-position]` |
| CD 成品读回 | `cd-preview-track --track artifact --output png --r0 --r1 --sectors --velocity --theta0 --outer --size N --byte-step 48 --deinterleave bool` |
| DVD 设计投影 | `calibrate --image composite --output png --total-sectors N --lba START --fill-sectors DRAW --generated-inner-radius-mm --generated-outer-radius-mm --actual-inner-radius-mm --actual-outer-radius-mm --pitch-* --actual-pitch-* --spiral-direction cw --image-threshold 128 --alpha-threshold 1 --tone-* --preview-size N --samples-per-sector N` |
| DVD 成品读回 | `simulate --iso artifact --output png --total-sectors N --inner-radius-mm --outer-radius-mm --pitch-* --spiral-direction cw --preview-size N --samples-per-sector N` |
| BD 设计投影 | `bd-preview --input composite --output png --total-sectors --r0 --r1 --actual-r0 --actual-r1 --invert --tone-* --size N`（本轮新增） |
| BD 成品读回 | `bd-preview-readback --iso artifact --output png --size N`（本轮新增，读取同名 sidecar） |
