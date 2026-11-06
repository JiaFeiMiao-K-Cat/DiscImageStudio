import Foundation

/// macOS 侧的光盘写入路径：复用系统自带 DiscRecording 工具 `drutil`。
///
/// 与 Windows 的 IMAPI2 流式刻录不同，`drutil` 只接受磁盘上的镜像/目录：
/// - CD-DA：CLI 生成 `.wav`，这里把它放进一个只含该 WAV 的临时目录，用 `burn -audio <dir>` 烧成红皮书音频盘；
/// - 数据盘：CLI 生成 `.iso` / `.dmg` / `.cue`+`.bin` / `.toc`，直接 `burn <image>`；
/// - 蓝光：macOS 的 DiscRecording 不支持 BD 写入，会被明确拒绝（生成 ISO 交给第三方软件）。
public enum BurnKind: String, Sendable, Equatable, CaseIterable {
    case audioCD
    case dataImage

    public var displayName: String {
        switch self {
        case .audioCD: return "音频 CD（CD-DA）"
        case .dataImage: return "数据光盘（镜像）"
        }
    }

    /// 按扩展名推断刻录方式；不支持的扩展名返回 nil。
    public static func kind(for url: URL) -> BurnKind? {
        switch url.pathExtension.lowercased() {
        case "wav", "aif", "aiff": return .audioCD
        case "iso", "dmg", "cue", "bin", "toc": return .dataImage
        default: return nil
        }
    }

    public static var supportedExtensions: [String] {
        ["wav", "aif", "aiff", "iso", "dmg", "cue", "bin", "toc"]
    }

    /// 媒体类型字符串（来自 `drutil status` 的 `Type:`）对应的刻录方式。
    /// 蓝光只按数据镜像处理：`drutil` 的 BD 路径与 DiscRecording 的 BDDAO 同源，
    /// 但 Apple 没有在 `man drutil` 里承诺 BD 写入，属于实验性路径。
    public static func kind(forMediaType mediaType: String?) -> BurnKind? {
        guard let mediaType else { return nil }
        let upper = mediaType.uppercased()
        if upper.hasPrefix("BD") { return .dataImage }
        if upper.hasPrefix("CD") { return .audioCD }
        if upper.hasPrefix("DVD") { return .dataImage }
        return nil
    }
}

public struct OpticalDrive: Sendable, Equatable, Identifiable {
    /// 与 `drutil -drive <n>` 一致，从 1 开始。
    public let index: Int
    public let vendor: String
    public let product: String
    public let revision: String
    public let bus: String
    public let supportLevel: String

    public var id: Int { index }

    public var displayName: String {
        var name = product.isEmpty ? "光盘驱动器" : product
        if !vendor.isEmpty {
            name += "（\(vendor)）"
        }
        if !bus.isEmpty {
            name += " · \(bus)"
        }
        return name
    }
}

public struct MediaStatus: Sendable, Equatable {
    /// `drutil status` 的原始文本，界面直接展示，便于用户在无法自动判断时自行确认。
    public let rawText: String
    /// `drutil status` 的 `Type:` 字段，例如 "CD-R"、"DVD-R"、"BD-R"。
    public let mediaType: String?
    /// `drutil toc` 是否报告了已有轨道/会话。
    public let hasTracks: Bool
    /// `drutil status` 明确报告为空白时 true，明确报告非空白时 false，无法判断时 nil。
    public let blankIndicator: Bool?

    public var isPresent: Bool {
        !rawText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    public var isProbablyBlank: Bool {
        blankIndicator ?? !hasTracks
    }

    public var isBluRay: Bool {
        mediaType?.uppercased().hasPrefix("BD") == true
    }

    public var isWritable: Bool {
        true
    }

    /// 蓝光只能作为数据镜像（实验性）；音频 CD 必须用 CD-R。
    public var supportsDataImage: Bool { true }

    public var supportsAudioCD: Bool { !isBluRay }
}

public enum BurnError: Error, Sendable, LocalizedError, Equatable {
    case unsupportedSource(String)
    case audioStagingFailed(String)
    case noDrive
    case noMedia
    case mediaNotWritable(String)
    case bluRayUnsupported
    case drutilFailed(String)

    public var errorDescription: String? {
        switch self {
        case .unsupportedSource(let path):
            return "不支持直接刻录该文件：\(path)。可刻录 .wav（音频 CD）或 .iso / .dmg / .cue / .bin / .toc（数据盘）。"
        case .audioStagingFailed(let reason):
            return "无法为音频 CD 准备临时目录：\(reason)"
        case .noDrive:
            return "没有找到可用的光盘刻录机。请连接刻录机后刷新。"
        case .noMedia:
            return "刻录机里没有光盘。请放入空白盘后再试。"
        case .mediaNotWritable(let reason):
            return "当前盘片不可写入：\(reason)"
        case .bluRayUnsupported:
            return "文件模式（drutil）不支持蓝光写入；BD 请使用流式模式（MMC），或生成 ISO 后用第三方工具。"
        case .drutilFailed(let reason):
            return "drutil 执行失败：\(reason)"
        }
    }
}

public struct BurnPlan: Sendable, Equatable {
    public let kind: BurnKind
    public let sourceURL: URL
    /// 完整的 `drutil` 参数（不含可执行文件路径）。
    public let commandArguments: [String]
    /// 音频 CD 的临时暂存目录；数据盘为 nil。`BurnService.discard(_:)` 负责清理。
    public let stagingDirectory: URL?

    public var commandLine: String {
        CommandArgumentBuilder.displayCommand(executable: BurnService.drutilURL.path, arguments: commandArguments)
    }
}

/// 发现刻录机、读取介质状态并生成/执行 `drutil` 刻录命令。
public struct BurnService: Sendable {
    public static let drutilURL = URL(fileURLWithPath: "/usr/bin/drutil")

    public let drutilURL: URL
    public let stagingRoot: URL

    public init(drutilURL: URL = BurnService.drutilURL, stagingRoot: URL? = nil) {
        self.drutilURL = drutilURL
        self.stagingRoot = stagingRoot ?? BurnService.defaultStagingRoot
    }

    public static var defaultStagingRoot: URL {
        let cache = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Caches", isDirectory: true)
        return cache
            .appendingPathComponent("DiscImageStudioMac", isDirectory: true)
            .appendingPathComponent("burn", isDirectory: true)
    }

    public var isAvailable: Bool {
        FileManager.default.isExecutableFile(atPath: drutilURL.path)
    }

    // MARK: - 发现与状态

    public func listDrives() async throws -> [OpticalDrive] {
        let output = try await run(arguments: ["list"])
        guard output.result.succeeded else {
            throw BurnError.drutilFailed(errorText(output))
        }
        return BurnService.parseDrives(output.standardOutput)
    }

    public func mediaStatus(drive: OpticalDrive?) async throws -> MediaStatus {
        let driveArguments = drive.map { ["-drive", String($0.index)] } ?? []
        let status = try await run(arguments: driveArguments + ["status"])
        guard status.result.succeeded else {
            throw BurnError.drutilFailed(errorText(status))
        }
        let toc = try? await run(arguments: driveArguments + ["toc"])
        return MediaStatus(
            rawText: status.standardOutput,
            mediaType: BurnService.parseMediaType(status.standardOutput),
            hasTracks: BurnService.parseHasTracks(toc?.standardOutput ?? ""),
            blankIndicator: BurnService.parseBlankIndicator(status.standardOutput)
        )
    }

    /// 校验介质与来源，生成刻录计划。音频 CD 会把 WAV 硬链接（失败时复制）到临时目录。
    public func makePlan(
        for sourceURL: URL,
        drive: OpticalDrive?,
        status: MediaStatus? = nil
    ) throws -> BurnPlan {
        let source = sourceURL.standardizedFileURL
        guard FileManager.default.fileExists(atPath: source.path) else {
            throw BurnError.unsupportedSource(source.path)
        }
        guard let kind = BurnKind.kind(for: source) else {
            throw BurnError.unsupportedSource(source.path)
        }

        if let status {
            guard status.isPresent else { throw BurnError.noMedia }
            if status.isBluRay {
                // drutil has no BD write path; the macOS app streams BD through MMC.
                throw BurnError.bluRayUnsupported
            }
            if !status.isProbablyBlank {
                throw BurnError.mediaNotWritable("检测到已有轨道或会话，疑似非空白盘。请改用空白盘片。")
            }
        }

        var arguments: [String] = []
        if let drive {
            arguments += ["-drive", String(drive.index)]
        }

        switch kind {
        case .dataImage:
            arguments += ["burn", "-eject", source.path]
            return BurnPlan(kind: kind, sourceURL: source, commandArguments: arguments, stagingDirectory: nil)
        case .audioCD:
            let staging = try makeStagingDirectory()
            do {
                let staged = staging.appendingPathComponent(source.lastPathComponent)
                do {
                    try FileManager.default.linkItem(at: source, to: staged)
                } catch {
                    try FileManager.default.copyItem(at: source, to: staged)
                }
            } catch {
                try? FileManager.default.removeItem(at: staging)
                throw BurnError.audioStagingFailed(error.localizedDescription)
            }
            arguments += ["burn", "-eject", "-audio", staging.path]
            return BurnPlan(kind: kind, sourceURL: source, commandArguments: arguments, stagingDirectory: staging)
        }
    }

    /// 启动 `drutil`；事件流与 CLI 任务一致（started → line… → finished）。
    public func burn(_ plan: BurnPlan) -> CliJob {
        let runner = CliRunner(invocation: CliInvocation(
            executableURL: drutilURL,
            workingDirectoryURL: plan.stagingDirectory,
            label: "drutil"
        ))
        return runner.start(arguments: plan.commandArguments)
    }

    /// 删除音频 CD 的临时暂存目录；只清理位于 `stagingRoot` 之下的目录。
    public func discard(_ plan: BurnPlan) {
        guard let staging = plan.stagingDirectory else { return }
        let root = stagingRoot.standardizedFileURL.path
        let target = staging.standardizedFileURL.path
        guard target.hasPrefix(root + "/") else { return }
        try? FileManager.default.removeItem(atPath: target)
    }

    // MARK: - 解析（可单测）

    /// 解析 `drutil list` 的列式输出。使用表头列位置切片；表头缺失时返回空数组。
    public static func parseDrives(_ text: String) -> [OpticalDrive] {
        let lines = text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        guard let headerIndex = lines.firstIndex(where: { $0.contains("Vendor") && $0.contains("Product") }) else {
            return []
        }
        let header = lines[headerIndex]
        guard let vendorStart = header.range(of: "Vendor")?.lowerBound,
              let productStart = header.range(of: "Product")?.lowerBound,
              let revStart = header.range(of: "Rev")?.lowerBound,
              let busStart = header.range(of: "Bus")?.lowerBound,
              let supportStart = header.range(of: "SupportLevel")?.lowerBound
        else { return [] }

        let vendorOffset = header.distance(from: header.startIndex, to: vendorStart)
        let productOffset = header.distance(from: header.startIndex, to: productStart)
        let revOffset = header.distance(from: header.startIndex, to: revStart)
        let busOffset = header.distance(from: header.startIndex, to: busStart)
        let supportOffset = header.distance(from: header.startIndex, to: supportStart)

        var drives: [OpticalDrive] = []
        for line in lines.dropFirst(headerIndex + 1) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty else { continue }
            let characters = Array(line)
            func slice(_ start: Int, _ end: Int?) -> String {
                guard start < characters.count else { return "" }
                let upper = min(end ?? characters.count, characters.count)
                guard start < upper else { return "" }
                return String(characters[start..<upper]).trimmingCharacters(in: .whitespaces)
            }
            let drive = OpticalDrive(
                index: drives.count + 1,
                vendor: slice(vendorOffset, productOffset),
                product: slice(productOffset, revOffset),
                revision: slice(revOffset, busOffset),
                bus: slice(busOffset, supportOffset),
                supportLevel: slice(supportOffset, nil)
            )
            guard !drive.product.isEmpty || !drive.vendor.isEmpty else { continue }
            drives.append(drive)
        }
        return drives
    }

    /// 解析 `drutil status` 的 `Type:` 字段。
    public static func parseMediaType(_ text: String) -> String? {
        for line in text.split(separator: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard trimmed.hasPrefix("Type:") else { continue }
            let value = trimmed.dropFirst("Type:".count).trimmingCharacters(in: .whitespaces)
            guard !value.isEmpty else { continue }
            // "CD-R  Name: /dev/disk4" → "CD-R"
            let token = value.split(whereSeparator: { $0 == " " || $0 == "\t" })
            guard let first = token.first else { continue }
            return String(first)
        }
        return nil
    }

    /// `drutil status` 里的空白指示：存在 "Blank: Yes/No" 时给出明确结论，否则 nil。
    public static func parseBlankIndicator(_ text: String) -> Bool? {
        for line in text.split(separator: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces).lowercased()
            if trimmed.hasPrefix("blank:") {
                let value = trimmed.dropFirst("blank:".count).trimmingCharacters(in: .whitespaces)
                if value.hasPrefix("yes") || value == "true" { return true }
                if value.hasPrefix("no") || value == "false" { return false }
            }
        }
        return nil
    }

    /// `drutil toc` 有轨道/会话输出即认为盘片非空白。
    public static func parseHasTracks(_ tocText: String) -> Bool {
        let trimmed = tocText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return false }
        let lowered = trimmed.lowercased()
        return lowered.contains("track") || lowered.contains("session")
    }

    // MARK: - 进程

    private func run(arguments: [String]) async throws -> CliRunOutput {
        let runner = CliRunner(invocation: CliInvocation(executableURL: drutilURL, label: "drutil"))
        do {
            return try await runner.runAndCollect(arguments: arguments, timeout: .seconds(30))
        } catch {
            throw BurnError.drutilFailed(error.localizedDescription)
        }
    }

    private func makeStagingDirectory() throws -> URL {
        let directory = stagingRoot.appendingPathComponent("burn-\(UUID().uuidString)", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        } catch {
            throw BurnError.audioStagingFailed(error.localizedDescription)
        }
        return directory
    }

    private func errorText(_ output: CliRunOutput) -> String {
        let detail = output.standardError.trimmingCharacters(in: .whitespacesAndNewlines)
        if !detail.isEmpty { return detail }
        let standard = output.standardOutput.trimmingCharacters(in: .whitespacesAndNewlines)
        if !standard.isEmpty { return standard }
        return "退出码 \(output.result.exitCode)"
    }
}
