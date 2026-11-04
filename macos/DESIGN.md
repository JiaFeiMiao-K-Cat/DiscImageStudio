# macOS SwiftUI 界面设计契约

本文件是 `macos/` 下 SwiftUI 客户端与 C# CLI 之间的**唯一接口契约**。实现必须严格遵循本文的类型名、JSON 字段名与公开方法签名；任何偏离都会让另一条并行工作流编译失败。

## 目标

在已有的跨平台 CLI（`src/DiscImageStudio.Cli`，输出 `disc-image-studio`）之上增加一个原生 macOS SwiftUI 图形界面：

- 通过 `catalog --json` 读取 CLI 的命令目录，**动态生成**表单，不在 Swift 侧重复维护参数表。
- 所有任务通过子进程调用 CLI 完成；GUI 绝不直接读写盘片编码逻辑。
- 支持：任务运行/取消、实时日志、进度、结果输出路径、PNG 预览、Finder 定位、设置（CLI 路径、默认输出目录、外观）。
- 不改变 Windows 桌面程序与 CLI 已有的行为；CLI 只新增一个只读 `catalog` 子命令。

## 目录结构

```text
macos/
├── DESIGN.md                     本契约
├── Package.swift                 SwiftPM 清单（只由 Lead 修改）
├── Sources/DiscImageStudioKit/   无 UI 的模型、进程、设置层（可单测）
├── Sources/DiscImageStudioMac/   SwiftUI 应用
├── Tests/DiscImageStudioKitTests/
│   └── Fixtures/catalog.json     CLI `catalog --json` 的真实输出快照（由 CLI 任务产出）
├── Resources/Info.plist.in       打包模板
└── scripts/                      构建、打包、开发脚本
```

`swift-tools-version: 6.0`，Swift 6 语言模式，最低 macOS 14（`@Observable`）。不引入任何第三方依赖。

## 一、CLI 侧：`catalog` 子命令（C# 实现）

`disc-image-studio catalog --json` 把 `DiscModuleCatalog` 序列化为 stdout 的 UTF-8 JSON（无 BOM），末尾换行。JSON 必须**总是输出全部字段**，缺失值为 `null`（不要省略字段，便于快照 diff）。

```json
{
  "schemaVersion": 1,
  "toolVersion": "1.3.6",
  "modules": [
    {
      "id": "cd-da",
      "displayName": "CD",
      "family": "CompactDisc",
      "description": "CD audio tracks or Mode 1 ISO9660/Joliet data images with CLV geometry previews.",
      "supportsCancellation": true,
      "commands": [
        {
          "name": "cd-generate",
          "displayName": "Generate CD audio or data image",
          "description": "Without files, writes WAV/CUE or RAW audio; with --data-dir, writes a Mode 1 ISO with inner/outer-ring files.",
          "capabilities": ["RawTrackGeneration", "DataImageGeneration", "HybridFilesystem"],
          "options": [
            {
              "name": "input",
              "displayName": "Source image",
              "valueType": "InputFile",
              "required": true,
              "defaultValue": null,
              "unit": null,
              "choices": null
            }
          ]
        }
      ]
    }
  ]
}
```

约定：

- `family` 取值：`CompactDisc` | `Dvd` | `BluRay`（与 `OpticalDiscFamily` 枚举名一致）。
- `valueType` 取值：`String` | `InputFile` | `OutputFile` | `InputFolder` | `Integer` | `Decimal` | `Boolean` | `Choice`。
- `capabilities` 是 `DiscModuleCapabilities` 的**标志名数组**，按枚举声明顺序，`None` 时为空数组。
- `toolVersion` 取入口程序集的 `AssemblyInformationalVersion`（不含 `+` 之后的提交哈希），无法取得时输出 `null`。
- `schemaVersion` 目前固定为 `1`；破坏性字段变更才递增。
- `catalog`（不带 `--json`）输出人类可读摘要：模块、命令、参数的缩进列表，供排障使用；退出码 0。
- `catalog --json` 退出码 0；序列化失败退出码 1 并向 stderr 写 `error: ...`。
- `catalog` 不得出现在模块命令目录里（它不是光盘模块命令），`--help` 顶部增加一行说明。

对应的 C# 实现放在 `src/DiscImageStudio.Cli/CatalogJson.cs`，`Program.Main` 在分发到 `ConsoleModuleRunner` 之前处理 `catalog`。允许 `catalog --json` 与 `catalog` 两种形式；其他参数组合报错退出 1。

## 二、Kit 公开 API（Swift）

包 `DiscImageStudioKit` 的全部 API 都是 `public`。命名空间即模块。

### 2.1 模型（`CommandCatalog.swift`）

```swift
public enum OpticalDiscFamily: String, Codable, Sendable, CaseIterable {
    case compactDisc = "CompactDisc"
    case dvd = "Dvd"
    case bluRay = "BluRay"

    public var shortName: String   // "CD" | "DVD" | "BD"
    public var displayName: String // "CD" | "DVD" | "蓝光 BD-R"
}

public enum DiscOptionValueType: String, Codable, Sendable, CaseIterable {
    case string = "String"
    case inputFile = "InputFile"
    case outputFile = "OutputFile"
    case inputFolder = "InputFolder"
    case integer = "Integer"
    case decimal = "Decimal"
    case boolean = "Boolean"
    case choice = "Choice"

    public var isFile: Bool        // InputFile / OutputFile / InputFolder
    public var isNumeric: Bool     // Integer / Decimal
}

public struct DiscOptionDefinition: Codable, Sendable, Equatable, Identifiable {
    public let name: String
    public let displayName: String
    public let valueType: DiscOptionValueType
    public let required: Bool
    public let defaultValue: String?
    public let unit: String?
    public let choices: [String]?
    public var id: String { name }
}

public struct DiscCommandDescriptor: Codable, Sendable, Equatable, Identifiable {
    public let name: String
    public let displayName: String
    public let description: String
    public let capabilities: [String]
    public let options: [DiscOptionDefinition]
    public var id: String { name }
}

public struct DiscModuleDescriptor: Codable, Sendable, Equatable, Identifiable {
    public let id: String
    public let displayName: String
    public let family: OpticalDiscFamily
    public let description: String
    public let supportsCancellation: Bool
    public let commands: [DiscCommandDescriptor]
}

public struct CommandCatalog: Codable, Sendable, Equatable {
    public static let supportedSchemaVersion = 1
    public let schemaVersion: Int
    public let toolVersion: String?
    public let modules: [DiscModuleDescriptor]

    public var entries: [CommandEntry]
    public func entry(named name: String) -> CommandEntry?
}

public struct CommandEntry: Sendable, Equatable, Identifiable {
    public let module: DiscModuleDescriptor
    public let command: DiscCommandDescriptor
    public var id: String { command.name }
    public var family: OpticalDiscFamily { module.family }
}
```

解码要求：`JSONDecoder` 使用默认键策略（JSON 已 camelCase），`valueType`/`family` 严格匹配；未知枚举值解码失败（由调用方转成可读错误，不要静默丢命令）。`entries` 按模块顺序、模块内命令顺序展开。

### 2.2 参数构造与校验（`CommandArguments.swift`）

```swift
public enum CommandArgumentBuilder {
    /// 按 command.options 的顺序，把非空值变成 ["--name", "value", ...]。
    /// - 值先 trim；空串视为未提供。
    /// - Boolean 输出小写 "true"/"false"。
    /// - 未在 options 中出现的键忽略（防御旧表单状态）。
    public static func arguments(for command: DiscCommandDescriptor, values: [String: String]) -> [String]

    /// 人类可读的命令行（仅用于日志/展示，引号规则同 shell）。
    public static func displayCommand(executable: String, arguments: [String]) -> String
}

public enum CommandValidation {
    public struct Issue: Sendable, Equatable {
        public let optionName: String
        public let message: String      // 中文提示
    }

    /// 仅做类型与必填校验：required 为空、Integer/Decimal 解析失败、
    /// Choice 不在 choices 内、Boolean 不是 true/false。
    public static func issues(for command: DiscCommandDescriptor, values: [String: String]) -> [Issue]
}
```

表单初始值规则（UI 与测试共用）：

```swift
public enum DefaultValues {
    /// 每个 option 在表单里的初始字符串值：
    /// defaultValue 非 null 时用它；Boolean 缺省为 "false"；Choice 缺省为 choices.first；
    /// 其余为 ""。
    public static func make(for command: DiscCommandDescriptor) -> [String: String]
}
```

### 2.3 进程执行（`CliRunner.swift`）

```swift
public struct CliInvocation: Sendable, Equatable {
    public let executableURL: URL
    public let prefixArguments: [String]   // 例如 dotnet 回退时的 ["run","--project",…,"-c","Release","--"]
    public let workingDirectoryURL: URL?
    public let label: String               // "内置 CLI" / "PATH" / "dotnet run"
    public init(executableURL: URL, prefixArguments: [String] = [], workingDirectoryURL: URL? = nil, label: String)
    public var debugCommandLine: String
}

public struct CliResult: Sendable, Equatable {
    public let exitCode: Int32
    public let duration: TimeInterval
    public var succeeded: Bool { exitCode == 0 }
}

public struct LogLine: Sendable, Equatable, Identifiable {
    public enum Stream: String, Sendable { case stdout, stderr, system }
    public let id: UUID
    public let stream: Stream
    public let text: String
    public let timestamp: Date
}

public enum CliEvent: Sendable, Equatable {
    case started(commandLine: String)
    case line(LogLine)
    case finished(CliResult)
}

public final class CliJob: Sendable {
    public let events: AsyncStream<CliEvent>
    public func cancel()          // 先 SIGTERM；3 秒后仍未退出则 SIGKILL
    public var isRunning: Bool
}

public struct CliRunner: Sendable {
    public let invocation: CliInvocation
    public init(invocation: CliInvocation)

    public func start(arguments: [String], environment: [String: String]? = nil) -> CliJob

    /// 收集全部输出，用于 `catalog --json` 等短命令；超时（默认 60 秒）抛错。
    public func runAndCollect(arguments: [String], timeout: Duration = .seconds(60)) async throws -> CliRunOutput
}

public struct CliRunOutput: Sendable, Equatable {
    public let result: CliResult
    public let standardOutput: String
    public let standardError: String
}
```

实现约束：

- 用 `Process` + 两个 `Pipe`，逐行推送 stdout/stderr（行按 `\n` 切分，`\r\n` 去掉 `\r`；无换行的尾部残行在进程结束时补发）。
- 事件顺序必须是 `.started` → 若干 `.line` → `.finished`；进程无法启动时抛给调用方（`start` 不抛，`.finished` 里给 `exitCode: -1` 并在 stderr 行说明原因）。
- 取消是幂等的；已被取消的任务再次 `cancel()` 无副作用。取消后仍必须收到 `.finished`。
- 不合并 stderr 到 stdout；两者都保留原始顺序（各自保序即可）。
- 子进程环境默认继承父进程，并强制 `DOTNET_CLI_TELEMETRY_OPTOUT=1`、`DOTNET_NOLOGO=1`、`DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`。
- 工作目录默认用户主目录（`FileManager.default.homeDirectoryForCurrentUser`），不依赖当前进程 cwd。
- 全程不阻塞主线程（`Process` 读取在后台队列），`CliJob.events` 可被任意 Task 消费。

### 2.4 CLI 发现（`CliResolver.swift`）

```swift
public enum CliSource: String, Sendable, Equatable {
    case environment, custom, bundled, path, dotnetProject
}

public struct ResolvedCli: Sendable, Equatable {
    public let invocation: CliInvocation
    public let source: CliSource
    public let detail: String        // 展示用：实际路径或 csproj 路径
}

public enum CliResolverError: Error, Sendable, LocalizedError {
    case notFound(searched: [String])
    case launchFailed(String)
    case catalogFailed(reason: String)
    case unsupportedSchema(found: Int, supported: Int)
    public var errorDescription: String? { get }   // 中文
}

public struct CliResolver: Sendable {
    public init(settings: AppSettings, bundle: Bundle = .main,
                environment: [String: String] = ProcessInfo.processInfo.environment,
                fileManager: FileManager = .default)

    /// 按顺序：环境变量 DIS_IMAGE_STUDIO_CLI → 设置里的自定义路径 → App 内置
    /// Contents/Resources/cli/disc-image-studio → PATH（含 /usr/local/bin、/opt/homebrew/bin、
    /// ~/.local/bin、~/.dotnet/tools）→ 设置里的开发工程路径（dotnet run --project …）。
    /// 只返回"文件存在且可执行"的候选，保持优先级顺序。
    public var candidates: [ResolvedCli]

    public func resolve() -> ResolvedCli?
    public func loadCatalog(from cli: ResolvedCli) async throws -> CommandCatalog
    public func resolveAndLoadCatalog() async throws -> (cli: ResolvedCli, catalog: CommandCatalog)
}
```

- `loadCatalog` 执行 `<cli> catalog --json`，解析 stdout；非 0 退出码或解析失败映射为 `catalogFailed`，`schemaVersion != 1` 映射为 `unsupportedSchema`。
- `dotnetProject` 的识别：`settings.devProjectPath` 可以是仓库根目录、`src/DiscImageStudio.Cli` 目录或 `DiscImageStudio.Cli.csproj` 路径；`dotnet` 从 `~/.dotnet/dotnet` 或 PATH 解析；工作目录设为 csproj 所在目录的上一级（仓库根）。找不到 csproj 就不产生该候选（不要报错）。

### 2.5 设置（`AppSettings.swift`）

存储在 `~/Library/Application Support/DiscImageStudioMac/settings.json`（UTF-8、camelCase、缩进 2、临时文件原子替换）。

```swift
public enum AppearanceMode: String, Codable, Sendable, CaseIterable {
    case system, light, dark
}

public struct AppSettings: Codable, Sendable, Equatable {
    public static let currentSchemaVersion = 1
    public var schemaVersion: Int
    public var customCliPath: String?
    public var devProjectPath: String?
    public var defaultOutputDirectory: String?
    public var appearance: AppearanceMode
    public var lastCommandName: String?
    public var showsAdvancedOptions: Bool

    public init()   // 默认值：schema 1、appearance .system、lastCommandName nil、showsAdvancedOptions true
    public func normalized() -> AppSettings
}

public struct AppSettingsStore: Sendable {
    public init(directory: URL? = nil)   // 默认 Application Support 下的 DiscImageStudioMac
    public var fileURL: URL
    public func load() -> AppSettings    // 文件缺失/损坏/schema 更高 → 默认值，不抛错
    public func save(_ settings: AppSettings) throws
}
```

`normalized()`：trim 所有路径；不存在的自定义路径与默认输出目录置空（`devProjectPath` 只在"目录存在"时保留）；未知 appearance 回落 `.system`；schema 高于当前时按默认值处理。

### 2.6 进度解析（`ProgressParsing.swift`）

```swift
public enum ProgressParsing {
    /// 从 CLI 行里提取进度：匹配 "… 42.3%" / "… 42%"（CLI 的 P1 格式），返回 0…1；否则 nil。
    public static func fraction(from line: String) -> Double?
    /// 匹配 "… progress …" 之外的行是否像"阶段开始"，返回阶段关键词（可选，UI 只做轻量展示）。
    public static func stage(from line: String) -> String?
}
```

## 三、SwiftUI 应用契约（`DiscImageStudioMac`）

> **已更新**：应用形态改为与 Windows 桌面版一致的工程式工作流（主页 / 设计 / 预览 / 导出 / 刻录 / 标定），
> 本节的"命令目录浏览器"契约已废弃。当前界面契约见 [WORKFLOW.md](WORKFLOW.md)；
> 本章保留仅用于说明 CLI `catalog --json` 与通用后台服务（CliRunner/CliResolver/AppSettings/BurnService/BurnStreamService）的契约。


### 3.1 状态模型

```swift
@MainActor @Observable final class AppModel {
    var settings: AppSettings
    var catalog: CommandCatalog?
    var resolvedCli: ResolvedCli?
    var cliErrorMessage: String?
    var isLoadingCatalog: Bool
    var selectedCommandID: String?         // = DiscCommandDescriptor.name（全局唯一）
    var selection: SidebarSelection         // .overview / .command(String) / .settings / .about
    func bootstrap() async                  // 解析 CLI + 加载目录，失败不崩溃
    func refreshCatalog() async
    func select(commandID: String)
    @discardableResult func saveSettings() -> Bool
    func makeRunModel(for entry: CommandEntry) -> CommandRunModel  // 同一命令复用同一实例
}
```

```swift
@MainActor @Observable final class CommandRunModel {
    let module: DiscModuleDescriptor
    let command: DiscCommandDescriptor
    var values: [String: String]           // 初始 DefaultValues.make(for:)
    var lines: [LogLine]
    var progress: Double?                  // 0…1，来自 ProgressParsing
    var isRunning: Bool
    var lastResult: CliResult?
    var lastSummary: String?               // 最后一条 stdout 非空行
    var outputURL: URL?                    // 从 --output / --iso-output 等推导并校验存在
    var issues: [CommandValidation.Issue]
    var launchErrorMessage: String?
    var arguments: [String] { CommandArgumentBuilder.arguments(for: command, values: values) }
    func run(using cli: ResolvedCli)       // isRunning 期间忽略重复调用
    func cancel()
    func clearLog()
    var isPreviewOutput: Bool              // output 形如 .png → 显示图片预览
}
```

要求：

- 所有状态变更在 `@MainActor` 上；后台只读事件流用 `Task { for await … }`，通过 `@MainActor` 更新。
- 取消后 `isRunning == false`，日志追加一条 system 行「已取消」。
- 任务结束时，把最后一条非空 stdout 行记为 `lastSummary`，并从参数值里推导 `outputURL`（`output`、`iso-output`、`track` 之外只取存在的文件）。
- 运行中的命令切换页面不中断任务；切回来仍能看到日志。
- 退出码非 0 时展示 stderr 尾部与「查看完整日志」。

### 3.2 界面结构

- `WindowGroup` 单窗口，`NavigationSplitView`：左侧栏固定 240–280 pt，右侧详情区最小 720 pt。
- 侧栏：
  - 「概览」入口（图标 `square.grid.2x2`）；
  - 「快速生成」分组：CD / DVD / 蓝光三张命令入口（按 family 选 `cd-generate` / `solve` / `bd-generate`）；
  - 「全部命令」按模块分组，显示命令名与图标；
  - 底部 CLI 状态条：已解析时显示来源与版本，未解析时显示「未找到 CLI」并直接打开设置。
- 详情区：
  - `CommandDetailView`：标题行（家族徽标、命令名、说明、能力标签）→ 表单卡 → 运行面板 → 日志卡 → 结果卡（成功时含 PNG 预览）。
  - 「概览」：产品简介、三步上手（选择命令 → 设置参数 → 生成镜像）、当前 CLI 状态与「打开 CLI 目录」。
  - 「设置」：CLI 路径选择（含「使用内置 CLI」「使用源码目录（dotnet run）」「在 PATH 中查找」）、默认输出目录、外观（跟随系统/浅色/深色）、重新加载命令目录、打开设置文件所在目录。设置即时保存（无需重启，外观立即生效）。
  - 「关于」：版本、许可证 GPL-3.0-or-later、上游仓库链接、第三方声明入口（打开仓库内文件）。
- 表单生成（`CommandFormView`）：
  - 分组：`输入与输出`（文件、字符串、Choice）→ `盘片几何`（`r0`/`r1`/`sectors`/`velocity`/`theta0`/`outer`/`total-sectors`/`start-angle`/`preset`/`actual-r0`/`actual-r1`）→ `影调`（`tone-*`、`invert`）→ `高级`（其余可选参数，默认折叠，受 `settings.showsAdvancedOptions` 控制）。
  - 输入文件/输入目录/输出文件：文本框 + 「选择…」按钮（`NSOpenPanel`/`NSSavePanel`），显示文件存在性与大小提示；输出文件默认取设置里的默认输出目录。
  - Boolean：`Toggle`（值 "true"/"false"）；Choice：`Picker`；Integer/Decimal：`TextField` + 等宽字体 + 单位后缀 + 解析错误提示；String：`TextField`。
  - 必填项在标签右侧显示 `必填` 徽标；校验错误就地显示，运行按钮在校验失败时禁用。
  - 全部控件支持键盘 Tab 遍历，`⌘↩` 运行、`⌘.` 取消。
- 日志：等宽字体、自动滚到底部（用户上滚时暂停自动滚动）、按 stream 着色（stdout 主色、stderr 危险色、system 次要色）、「复制」「清空」按钮。
- 结果：退出码徽标、总耗时、输出路径（可点击「在 Finder 中显示」）、PNG 输出时显示图片预览（`NSImage`，等比适配，双击用「预览」打开）。
- 未找到 CLI 时：详情区显示 `OnboardingView`，给出三条获取方式（下载发布包放进 `Contents/Resources/cli/`、设置自定义路径、用源码 `dotnet run`），并提供「选择 CLI 可执行文件…」。
- 所有 UI 文案为简体中文；命令名、参数名、CLI 原始输出保持英文原样。

### 3.3 视觉与交互

- 用系统材质与语义色（`Color(nsColor:)`、`.background(.regularMaterial)`），深色/浅色都自适应；品牌蓝 `#3385FF`（深色强调 `#74C0FF`）作为主按钮与选中态；成功/警告/危险用系统 `green`/`orange`/`red`。
- 圆角 10–12 pt，卡片间距 12/16，详情区最大内容宽度 980 pt。
- 家族徽标：CD 用银色圆环、DVD 用深蓝圆环、BD 用金色圆环的 SF Symbol 组合（`opticaldisc`），不用位图。
- 交互必须给出即时反馈：运行中按钮换成「取消」，进度条用 `ProgressView(value:)`，无进度时用不确定进度。
- 无障碍：主要按钮设置 `accessibilityIdentifier`（`run-command`、`cancel-command`、`choose-cli`、`reveal-output`、`refresh-catalog`），表单控件使用 `displayName` 作为标签。

## 四、构建与验证

```sh
# Kit 单测（无需 .NET）
cd macos && swift test

# 编译应用（未打包，开发用）
cd macos && swift build

# 打包 CLI（需要 .NET 10 SDK）
macos/scripts/build-cli.sh --rid osx-arm64

# 打包 .app（会自动带上 build/cli 里的 CLI）
macos/scripts/build-app.sh

# 端到端：启动打包后的应用
open macos/build/DiscImageStudio.app
```

`catalog.json` 快照：`macos/scripts/build-cli.sh` 成功后应把 `disc-image-studio catalog --json` 的输出写入 `macos/Tests/DiscImageStudioKitTests/Fixtures/catalog.json`（`--update-fixtures` 参数控制），Swift 单测解码该快照以防契约漂移。

## 五、macOS 烧录路径（文件模式 + 流式模式）

macOS 没有 IMAPI2，Windows 的「即时刻录」不能照搬。macOS 侧维护两条路径：

### 5.1 文件模式（默认，稳定）

CLI 先生成镜像/音轨，再交给系统 `drutil` 刻录：

- 音频 CD：CLI 生成 `.wav`，`BurnService` 把它硬链接（失败时复制）进只含该 WAV 的临时目录，执行
  `drutil [-drive N] burn -eject -audio <目录>`（红皮书音频盘）。
- 数据盘：`.iso` / `.dmg` / `.cue` / `.bin` / `.toc` 直接 `drutil [-drive N] burn -eject <文件>`。
- 蓝光：文件模式用 `drutil` 写 BD 数据镜像（实验性），流式用 `disc-burn-stream --kind bd-data`（DiscRecording BDDAO，实验性）；两条路径均未实机验证，音频 CD 不支持蓝光。

`BurnService`（`macos/Sources/DiscImageStudioKit/BurnService.swift`）负责：`drutil list` 解析、`status`+`toc` 介质状态、
刻录计划（`BurnPlan`，含临时目录清理）、以及用 `CliRunner` 启动 `drutil` 并复用同一套事件流。

### 5.2 流式模式（实验性，DiscRecording）

用 DiscRecording 框架把 CLI 生成的字节流直接喂给刻录机，不落完整镜像。
`drutil` 只接受路径，**不能**从 stdin 流式写入；流式必须走 `DRTrack` 的 producer 接口：

```text
CLI（--output -，stdout 输出原始块）
        │  pipe
        ▼
disc-burn-stream（Objective-C + DiscRecording：DRTrack producer + DRBurn）
        │
        ▼
     刻录机
```

- 新目标：`macos/Sources/DiscBurnStream/`（SwiftPM executable target，ObjC 源码，链接 `DiscRecording`）。
- 命令契约：
  - `disc-burn-stream --list-drives` → stdout 输出 JSON 数组：
    `[{"index":1,"vendor":"…","product":"…","firmware":"…","cdWritable":true,"dvdWritable":true,"bdWritable":false,"mediaPresent":true,"mediaBlank":true,"mediaType":"CD-R"}]`
  - `disc-burn-stream --kind cd-audio|dvd-data|bd-data --sectors N [--drive I] [--speed N] [--no-verify] [--no-eject] [--test]`
    从 stdin 读原始块（cd-audio 每块 2352 字节，dvd-data/bd-data 每块 2048 字节；bd-data 使用 BD-R 介质、`kDRBurnStrategyBDDAO` 与 BD 1×=4496 KB/s），写 stderr 进度：
    `state: preparing|writing|verifying|done|failed`、`percent: 42.3`、`error: …`；退出码 0 成功、1 失败、2 参数/设备/介质问题。
  - 介质非空白（`kDRDeviceMediaIsBlankKey` 为 false）或不存在时拒绝并退出 2。
- CLI 侧（`src/DiscImageStudio.Cli`）：
  - `cd-generate --output -`：stdout 只输出 **原始 CD-DA 块**（每扇区 2352 字节、大端 PCM、已延迟交织），不写 WAV 头/CUE；
    所有日志、进度、摘要改走 stderr。GUI 用参数里的 `--sectors` 作为 track 长度。
  - `bd-generate --output -`（可选）：stdout 输出完整 ISO 字节流，日志走 stderr；供第三方 `growisofs` 等工具使用。
- `BurnStreamService`（Kit）负责编排：启动 CLI 子进程，把 stdout 管道接到 helper 的 stdin，转发两边 stderr 日志、
  解析 `percent:` 推进进度；取消时同时终止两个进程。

### 5.3 边界与安全

- 两条路径都只接受空白盘；GUI 在刻录前展示 `drutil status` / helper 报告的介质信息并要求用户确认。
- 流式模式覆盖 CD-DA、DVD 数据（`solve --iso-output -`，无随盘文件）与实验性 BD 数据（`bd-data`：2048 B 块、`kDRBurnStrategyBDDAO`、BD 1×=4496 KB/s）；GUI 直接用当前工程生成并刻录，带随盘文件的 DVD 走文件模式。BD 路径未经实机验证，文档统一标注为实验性。
- 没有刻录机或没有空白盘时，两条路径都必须给出明确提示，而不是静默失败。

## 六、并行边界

| 工作流 | 写入范围 |
| --- | --- |
| CLI catalog（C#） | `src/DiscImageStudio.Cli/`、`tests/test_cli_package.py`、`docs/ARCHITECTURE.md`（仅 CLI 段落）、`macos/Tests/DiscImageStudioKitTests/Fixtures/catalog.json` |
| Kit | `macos/Sources/DiscImageStudioKit/`、`macos/Tests/DiscImageStudioKitTests/`（Fixtures 目录除外） |
| UI | `macos/Sources/DiscImageStudioMac/` |
| 流式刻录 | `macos/Sources/DiscBurnStream/`、`macos/Sources/DiscImageStudioKit/BurnStreamService.swift` |
| 打包/文档 | `macos/scripts/`、`macos/Resources/`、`docs/MACOS_GUI.md`、`.github/workflows/macos-gui.yml`、`.gitignore` |
| Lead | `macos/Package.swift`、`macos/DESIGN.md`、README、CHANGELOG、最终集成 |

任何需要改 `Package.swift` 的变化（新增 target/源文件不受影响；新增依赖、资源声明、平台调整）必须先通知 Lead。
