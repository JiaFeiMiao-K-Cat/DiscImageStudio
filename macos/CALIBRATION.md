# macOS 标定完整流程契约

本文件定义 macOS GUI 的辅助标定流程与 C# CLI 的接口。设计依据：

- `docs/CALIBRATION.md`（官方「照片矫正 → 曲线标定」流程与判定阈值）
- 上游 issue #41（用户实测：粗标定三次 + 精标定两次的经验）
- 上游 issue #49（两张法：半黑白粗调盘 → 内置靶标精调盘；附录给出粗调拟合公式）

目标是让 macOS GUI 覆盖与 Windows 版相同的完整标定能力，并额外提供 issue #49 描述的「粗调」入口。

## 0. 文件与状态

一次标定 = 工程 `calibration/` 下的一个 `<id>.calibration.json`，它是**唯一事实来源**：目标几何、内嵌照片、轮廓点、描线点与 `RectificationConfirmed` 全部写在里面。没有独立的 GUI 状态文件，也没有记录索引；候选与选中项在打开会话时重新计算，不落盘。

```text
<项目目录>/
├── <project>.discproj                   工程（calibrationSessionPath 指向当前会话）
├── calibration/
│   ├── <id>.calibration.json            标定会话（schema 6，Windows 双向兼容，CLI 读写）
│   ├── target-<id>.png                  内置标靶（2400×2400，按生成几何裁切环带）
│   ├── coarse-<id>.png                  粗调半黑白图案（2400×2400）
│   ├── coarse-<id>-p0.json              粗调 P0 几何记录
│   ├── photo-<id>.png                   缩小后的原照片（≤2000 px，轮廓点坐标基于它）
│   └── rectified-<id>.png               矫正后盘面图（正方形，中心即盘心）
└── <输出文件>
```

- `.discproj` 的 `calibrationSessionPath` 指向当前会话；工程重开时据此恢复（见 §2）。
- `.calibration.json` 必须由 C# 写（schema 校验严格，含内嵌照片与描线点），GUI 不手写；GUI 只通过 `calibration-session-write` / `calibration-session-read` 与它交互。
- 同一 `<id>` 用于会话、靶标、照片、矫正图与粗调文件（`CalibrationWorkspaceLayout` 的命名约定），目录扫描与删除都按 `<id>` 前缀进行。
- 粗调不写会话：粗调图案只有 `coarse-<id>.png` 与 `coarse-<id>-p0.json`；应用后只更新工程几何并清空 `calibrationSessionPath`（见 §2）。

## 1. CLI 命令（全部在 `ConsoleModuleRunner` 之前分发，退出码 0/1/2）

### 1.1 `calibration-target`

生成内置标靶并写出会话骨架。

```sh
disc-image-studio calibration-target \
  --family cd|dvd|bd --r0 24.5 --r1 56.8 --sectors 359849 --density 1200 \
  [--pitch-linear 0 --pitch-quadratic 0 --pitch-cubic 0] \
  [--invert] [--drawing-start 0 --drawing-end 0] \
  --output target.png --session target.calibration.json [--size 2400]   # 当前只接受 2400
```

- `--density`：CD 为线速度 mm/s（1200），DVD 为通道位长 nm（133.3），BD 固定 74.5（传入非 74.5 报错）。
- 图案：内置 `target_slim.png`，按生成外半径缩放、按 `DrawingInner/Outer` 裁切（等价 Windows
  `CalibrationPatternRenderer.RenderEncodingImage`），输出 8 位灰度 PNG。
- 会话：`CalibrationSession(target, [], null, cdInterleave:true, null)`，用
  `CalibrationSessionJson.Save` 原子写；已是合法 schema 6 文件。
- stdout 打印摘要（family、r0/r1、sectors、图案尺寸、会话路径）。

### 1.2 `calibration-photo`

照片缩放 + 双椭圆联合矫正 + 透视重采样（无状态，不写会话）。

```sh
disc-image-studio calibration-photo \
  --photo IMG --disc-size 120|80 \
  [--outer "x,y;x,y;..." --hole "x,y;x,y;..."] \
  --resized-out photo.png --rectified-out rectified.png --json out.json [--size 1200]
```

- 照片缩放到最长边 2000 px（保持纵横比），写 `--resized-out`；**轮廓点坐标基于这张缩小图**。
- `--disc-size 120|80` → 实体外半径 60/40 mm，中心孔半径固定 7.5 mm。
- 提供轮廓时调用 `CalibrationPhotoRectifier.Fit`，成功后把整盘（半径 = 实体外半径）矫正到
  `--size` 正方形 PNG：中心即盘心，坐标 +x 右、+y 下；轮廓点无需顺序。
- `--json` 输出（UTF-8、camelCase）：

```json
{
  "succeeded": true,
  "message": "",
  "photoWidth": 2000, "photoHeight": 1500,
  "rectifiedSize": 1200, "canvasOuterRadiusMm": 60,
  "discSize": 120,
  "outerRmsPixels": 1.7, "holeRmsPixels": 2.3,
  "transformDiscToSource": [9 doubles],
  "resizedPhotoPath": "...", "rectifiedImagePath": "..."
}
```

失败时 `succeeded:false`、`message` 给中文原因，退出码 0（可预期失败），并仍输出 `photoWidth/Height`
与 `resizedPhotoPath`（便于继续调整轮廓）。

### 1.3 `calibration-fit`（精调）

```sh
disc-image-studio calibration-fit \
  --session target.calibration.json --points "x,y;x,y;..." \
  --json fit.json [--radius-range 0.02] [--auto-range true] [--fit-pitch true]
```

- 轻量更新会话：把 `--points` 作为 `Photo.Traces = [ {armIndex:0, points} ]`、`RectificationConfirmed=true`，
  照片/轮廓/盘片规格取会话中已有值；若会话没有 Photo 段则报错退出 1。
- 调用 `CalibrationCrossFitter.Fit(target, traces, options)`；`AutoRange` 默认 true（与 Windows 一致：
  ±0.02 起、边界命中自动扩到 ±0.1/±0.5）。
- 对每个候选用 `CalibrationCrossPrediction.GetOutline(target, candidate.Parameters,
  minObservedR, maxObservedR, rotation, 0, maxPoints:4000)` 生成预测折线（mm）。
- `--json` 输出：

```json
{
  "succeeded": true, "reliable": true, "boundaryLimited": false,
  "warnings": ["..."],
  "bestIndex": 0,
  "candidates": [
    { "innerRadiusMm": 24.146, "outerRadiusMm": 57.886,
      "pitchLinear": 0, "pitchQuadratic": 0, "pitchCubic": 0,
      "rotationRadians": 0.123, "rmsErrorMm": 0.12, "maxErrorMm": 0.31,
      "outlierCount": 0,
      "predicted": [{"x":1.23,"y":4.56}, ...] }
  ]
}
```

- 同时把更新后的会话原子写回 `--session`（含描线点），保证 Windows 可打开。
- 不可应用阈值由 GUI 判定（平均 ≤0.35 mm、最大 ≤0.8 mm），CLI 只给数据。

### 1.4 `calibration-coarse-target`（粗调盘，issue #49）

生成「左黑右白、分界过盘心」的 2400×2400 图案（按生成几何裁切环带，灰度为 0/255），
并输出 P0 记录。

```sh
disc-image-studio calibration-coarse-target \
  --family cd|dvd|bd --r0 --r1 --sectors --density \
  [--drawing-start 0 --drawing-end 0] \
  --output coarse.png --json p0.json [--size 2400]
```

`p0.json`：`{family,r0,r1,sectors,density,pitchLinear,pitchQuadratic,pitchCubic,drawingStart,drawingEnd,outputPath}`。

### 1.5 `calibration-coarse-fit`（粗调反推，issue #49 附录）

输入 P0 与矫正后照片上沿黑白分界描的点（mm，按半径从小到大），拟合 P1 的内外半径与整体旋转。
恒定轨距近似（粗调用途），公式：

```text
pg = π * (Ro² − Ri²) / L            # 生成模型每弧度径向增长
pa = π * (ro² − ri²) / L            # 实际模型
rg(r) = sqrt(Ri² + (r² − ri²) * (Ro² − Ri²) / (ro² − ri²))
Δ(r)  = 2π * [ (rg(r) − Ri)/pg − (r − ri)/pa ]
α(r) + Δ(r) ≈ rotation              # α 为描点相对盘心的连续角度
```

要求点按半径递增、至少 8 点、覆盖生成环带径向宽度的 ≥30%。搜索：先固定 `L`（由 sectors × 密度决定），
在 `r0±1.5 mm`、`r1±1.5 mm` 网格 + 局部细化（可用 Nelder–Mead 或与 Core 相同的稳健代价），
返回最多 5 个候选（按 RMS 排序，含去重），整体旋转作为无关参数单独求解（角度 unwrap）。

```sh
disc-image-studio calibration-coarse-fit \
  --family dvd --r0 --r1 --sectors --density \
  --points "x,y;x,y;..." --json coarse-fit.json
```

```json
{
  "succeeded": true,
  "warnings": [],
  "bestIndex": 0,
  "candidates": [
    { "innerRadiusMm": 24.15, "outerRadiusMm": 57.88, "rotationRadians": 0.1,
      "rmsErrorMm": 0.18, "maxErrorMm": 0.42,
      "predicted": [{"x":1.23,"y":4.56}, ...] }
  ]
}
```

`predicted` 为拟合模型下分界在观察半径区间的折线（mm），供 GUI 叠加核对。方向/绕数无法唯一确定时
必须在 `warnings` 中说明并拒绝给出单一「最佳」结论（`bestIndex:-1`）。

### 1.6 `calibration-session-write`（保存为 Windows 兼容会话）

GUI 完成照片/描线/拟合后，把完整会话写盘：

```sh
disc-image-studio calibration-session-write \
  --session base.calibration.json --photo photo.png --disc-size 120 \
  --outer "..." --hole "..." --points "x,y;..." \
  [--confirmed true|false] --output <id>.calibration.json
```

- 读取 base 会话的 Target，构造 `CalibrationPhotoSession`（照片 base64、轮廓、60/40、
  `RectificationConfirmed` 由 `--confirmed` 决定（默认 true）、单条 `ArmIndex=0` 描线），
  校验后 `CalibrationSessionJson.Save`。
- 照片 base64 使用传入的缩小图 PNG（≤14 MB）。
- `--confirmed false --points ""` 保存「已导照片但还没确认矫正」的中间状态；`--confirmed true --points ""`
  只确认矫正、暂无描线；`--confirmed false` 带非空描线 → 退出 2。
- `--output` 允许与 `--session` 相同：先完整读取 base，再原子覆盖，Target/Observations/FitOptions 不丢。
- GUI 在照片导入、矫正、确认、描线变化后都原地更新同一个 `<id>.calibration.json`。

### 1.7 `calibration-session-read`

```sh
disc-image-studio calibration-session-read --session X.calibration.json --json out.json
```

输出摘要：family、r0/r1/sectors/density/pitch、绘图起止、photo 是否存在、RectificationConfirmed、
描线点数、照片宽高（解码 PNG 头）。

## 2. GUI（SwiftUI `CalibrationView`）

三步向导（保留 Windows 的卡片语言），顶部显示工程盘型与生成几何摘要：

### 步骤 1：标靶

- 两种模式分段控件：**精调靶标**（内置同心圆 + 十字） / **粗调图案**（半黑白，issue #49）。
- 「生成」→ 调 CLI 生成 PNG + 会话（精调）或 P0 JSON（粗调）；显示图案预览与几何摘要。
- 快捷动作：导出到项目 `calibration/` 目录、跳到导出页刻录、跳到刻录页。
- 每次生成都新建文件（带时间戳/短 ID），不覆盖已有会话。

### 步骤 2：照片矫正

- 导入照片；选择 12 cm / 8 cm；显示缩小后的照片（由 CLI 返回）。
- 可在照片上拖动 8 个外缘控制点 + 8 个中心孔控制点（默认按图像范围 0.43/0.125 布置）；
  支持放大/平移、重置轮廓。
- 「计算矫正」→ CLI；成功后显示矫正预览（正方形盘面），「确认矫正」进入下一步；失败显示原因。
- 修改轮廓或盘片规格后，确认状态与后续描线/拟合全部失效（与 Windows 一致）。

### 步骤 3：标定

- 精调：在矫正图上沿一条径向曲线点击打点（从内到外，最少 6 点；DVD 建议 ≥12 点且覆盖 ≥60% 环带），
  支持拖点、Delete 删除、清空；显示点序号与环带参考圆。
- 粗调：沿黑白分界打点（≥8 点，覆盖 ≥30%），支持同样的编辑操作。
- 「反推参数」→ 对应 CLI；显示候选列表：平均偏差、最大偏差、偏离点数、搜索边界提示、warnings。
- 选中候选 → 在矫正图上叠加模型预测折线（青色=描点、橙色=预测；偏离点标红）。
- 可应用条件：精调平均 ≤0.35 mm 且最大 ≤0.8 mm；粗调仅要求 `succeeded` 且 warnings 为空；
  不满足时按钮禁用并说明原因。
- 「应用参数」：
  - 更新工程 `geometry`（CD/DVD/BD 半径；DVD 三次项；扇区保持；BD 极性按会话）；
  - 同步预览页实测参数（actual radii/pitch）为应用值；
  - 精调：写 Windows 兼容 `.calibration.json`（`calibration-session-write`，`--confirmed true`），
    把 `project.calibrationSessionPath` 指向该文件；
  - 粗调：不写会话，只更新几何，并清空 `calibrationSessionPath`（粗调结果不是最终精调会话）；
  - 标记工程为脏并提示「参数已更新」。

### 会话恢复

重开工程时按以下顺序恢复，全部数据来自 `.calibration.json`：

1. 读 `project.calibrationSessionPath`；文件不存在或无法解码时清空该路径，回到步骤 1（不静默套用旧参数）。
2. `CalibrationSessionCatalog.loadSession(at:)` 解码会话：`target.parameters` 恢复盘型、生成几何、密度、DVD 三次项、BD 极性与绘图起止。
3. 若 `photo` 存在：把内嵌照片 Base64 写回 `photo-<id>.png`，恢复 `outerBoundary` / `holeBoundary` / `RectificationConfirmed` / `traces[0].points`。
4. 用恢复的轮廓调用 `calibration-photo` 重建 `rectified-<id>.png`；照片与轮廓都来自会话，不依赖原照片路径。
5. 若会话已确认且有描线，自动重跑 `calibration-fit` 重算候选；候选与选中项不存储，恢复后默认选 `bestIndex`。

### 标定记录

- 页脚列表 = 扫描工程 `calibration/` 下所有 `*.calibration.json`（`CalibrationSessionCatalog.entries()`，按修改时间倒序）；每项显示盘型、r0/r1、密度、是否已确认、描线点数。没有额外索引文件。
- 打开：按上面的会话恢复流程。
- 删除：按 `<id>` 删除会话与同名的 `target-` / `coarse-` / `photo-` / `rectified-` 文件，不触碰其他会话。
- 导出：复制该 `<id>.calibration.json` 到用户选择的位置。
- 粗调不产生会话，因此不出现在记录列表；它只在应用时改一次几何。

## 3. Kit 接口

```swift
// CalibrationService.swift
public struct CalibrationTargetRequest: Sendable, Equatable { family, geometry, density, invert, drawingStart, drawingEnd, outputURL, sessionURL }
public struct CalibrationPhotoResult: Codable, Sendable, Equatable { succeeded, message, photoWidth, photoHeight, rectifiedSize, canvasOuterRadiusMm, discSize, outerRmsPixels, holeRmsPixels, transformDiscToSource, resizedPhotoPath, rectifiedImagePath }
public struct CalibrationCandidate: Codable, Sendable, Equatable, Identifiable { innerRadiusMm, outerRadiusMm, pitchLinear/Quadratic/Cubic, rotationRadians, rmsErrorMm, maxErrorMm, outlierCount, predicted: [CalibrationPoint] }
public struct CalibrationFitResult: Codable, Sendable, Equatable { succeeded, reliable, boundaryLimited, warnings, bestIndex, candidates }
public struct CalibrationCoarseResult: Codable, Sendable, Equatable { succeeded, warnings, bestIndex, candidates }

public struct CalibrationService: Sendable {
    public init(cliInvocation: CliInvocation, workspace: URL)   // workspace = <project dir>/calibration

    public func generateTarget(_ request: CalibrationTargetRequest) async throws -> CalibrationTargetResult
    public func preparePhoto(photoURL: URL, discSize: CalibrationDiscSize, resizedOut: URL) async throws -> CalibrationPhotoResult
    public func rectifyPhoto(photoURL: URL, discSize: CalibrationDiscSize, outer: [CalibrationPoint],
                             hole: [CalibrationPoint], resizedOut: URL,
                             rectifiedOut: URL) async throws -> CalibrationPhotoResult
    public func fit(sessionURL: URL, points: [CalibrationPoint], radiusRange: Double?,
                    autoRange: Bool) async throws -> CalibrationFitResult
    public func generateCoarseTarget(...) async throws -> CalibrationCoarseTargetResult
    public func fitCoarse(...) async throws -> CalibrationCoarseResult
    public func writeSession(baseSessionURL: URL, photoURL: URL, discSize: CalibrationDiscSize,
                             outer: [CalibrationPoint], hole: [CalibrationPoint],
                             points: [CalibrationPoint], outputURL: URL,
                             confirmed: Bool = true) async throws -> CalibrationSessionSummary
    public func readSession(_ sessionURL: URL) async throws -> CalibrationSessionSummary
    public static func workspace(forProjectURL: URL) -> URL     // <project dir>/calibration
    public func ensureWorkspace() throws
}

// CalibrationSession.swift（会话文档 + 目录扫描；替代旧的 GUI 记录索引）
public struct CalibrationSessionDocument: Codable, Sendable, Equatable {
    public struct TargetInfo { id: String?; parameters: Parameters }
    public struct Parameters {
        kind, innerRadiusMm, outerRadiusMm, sectors, linearDensity,
        pitchLinear/Quadratic/Cubic, bluRayInvertMapping?, drawingStartSector?, drawingEndSectorExclusive?
        var family: DiscFamily; var projectGeometry: ProjectGeometry
    }
    public struct Trace { armIndex: Int; points: [CalibrationPoint] }
    public struct PhotoInfo {
        imagePngBase64, outerBoundary, holeBoundary, physicalOuterRadiusMm,
        rectificationConfirmed, traces: [Trace]
    }
    public let target: TargetInfo
    public let photo: PhotoInfo?
}

public struct CalibrationSessionCatalog: Sendable {
    public struct Entry: Sendable, Equatable, Identifiable {
        path, modifiedAt, document, family, geometry, density,
        isConfirmed, tracePointCount, targetID
    }
    public init(workspace: URL, cliInvocation: CliInvocation? = nil)
    public func entries() -> [Entry]                                  // 扫描 *.calibration.json，按修改时间倒序
    public func loadSession(at url: URL) throws -> CalibrationSessionDocument
    public func delete(_ entry: Entry) throws                         // 按 <id> 删除会话与同名文件
    public func export(_ entry: Entry, to destination: URL) throws    // 复制会话
}

public struct CalibrationWorkspaceLayout: Sendable {
    public init(workspace: URL)
    public func sessionURL(id:) / targetImageURL(id:) / photoURL(id:) /
               rectifiedURL(id:) / coarseImageURL(id:) / coarseP0URL(id:)
    public static func id(fromSessionURL: URL) -> String              // "<id>.calibration.json" → "<id>"
}
```

坐标约定：照片轮廓点用缩小照片像素坐标（`CalibrationPhotoResult.photoWidth/Height` 内）；
描线点、预测折线、几何半径都用毫米，+x 右、+y 下，原点盘心。

## 4. 判定与安全

- 精调可应用阈值与 Windows 相同：平均偏差 ≤0.35 mm、最大偏差 ≤0.8 mm；超过则禁用「应用」。
- 粗调只用于「把累计绕圈降到可继续精调」；应用前必须核对叠加图，warnings 非空时禁止应用。
- 应用参数会改变工程几何（因此改变生成结果），必须提示并标记脏；不自动重新生成已有镜像。
- 标定图案与刻录必须在同一生成参数下；应用 P1/P2 后要用新参数重新生成标靶再刻下一张。
- 粗调不写会话：应用后只更新几何并清空 `calibrationSessionPath`，避免工程指向一张不存在的精调会话。
- 不实现自动「密集环相位提取」：粗调仍需人工沿分界描点（与 issue #49 的脚本接口对应，
  但本轮不内置自动环数识别）。文档中明确说明。
