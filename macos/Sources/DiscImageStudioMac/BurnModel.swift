import Foundation
import Observation
import SwiftUI
import DiscImageStudioKit

/// 刻录工作区的状态：刻录机发现、介质状态、来源校验、drutil 任务与日志。
/// 文件模式直接复用 Kit 的 `BurnService`；流式模式等 `BurnStreamService` 落地后接入。
@MainActor
@Observable
final class BurnModel {
    enum Mode: String, CaseIterable, Identifiable {
        case file
        case stream

        var id: String { rawValue }

        var shortName: String {
            switch self {
            case .file: return L("文件模式")
            case .stream: return L("流式模式")
            }
        }

        var displayName: String {
            switch self {
            case .file: return L("文件模式（drutil）")
            case .stream: return L("流式模式（DiscRecording，实验性）")
            }
        }
    }

    var mode: Mode = .file
    var drives: [OpticalDrive] = []
    var selectedDriveIndex: Int?
    var mediaStatus: MediaStatus?
    var isLoadingDrives = false
    var isLoadingStatus = false
    var driveErrorMessage: String?
    var sourcePath: String = ""
    var isBurning = false
    var progress: Double?
    var stage: String?
    var logLines: [LogLine] = []
    var lastResult: CliResult?
    var burnErrorMessage: String?
    var wasCancelled = false
    private(set) var plan: BurnPlan?

    // MARK: - 流式刻录（从当前工程生成并直接写入）

    var streamProject: ProjectModel?
    var streamDrives: [StreamDrive] = []
    var selectedStreamDriveIndex: Int?
    var isStreamBurning = false
    var streamProgress: Double?
    var streamStage: String?
    var streamLogLines: [LogLine] = []
    var streamResult: StreamBurnResult?
    var streamErrorMessage: String?
    var streamWasCancelled = false
    /// 流式刻录速度倍数；nil 表示交给驱动器自动（通常最高），默认 2×。
    var streamSpeed: Double? = 2
    /// 成功后是否弹出光盘；失败/取消时助手不会弹盘。
    var streamEject = true
    private(set) var streamPlan: StreamBurnPlan?

    @ObservationIgnored private var streamJob: BurnStreamJob?
    @ObservationIgnored private var streamTask: Task<Void, Never>?

    @ObservationIgnored private let service: BurnService
    private var cliInvocation: CliInvocation?
    @ObservationIgnored private var job: CliJob?
    @ObservationIgnored private var eventTask: Task<Void, Never>?

    init(service: BurnService = BurnService()) {
        self.service = service
    }

    // MARK: - 派生状态

    var selectedDrive: OpticalDrive? {
        if let selectedDriveIndex,
           let match = drives.first(where: { $0.index == selectedDriveIndex }) {
            return match
        }
        return drives.first
    }

    var sourceURL: URL? {
        Formatting.expandedURL(for: sourcePath)
    }

    var sourceKind: BurnKind? {
        guard let sourceURL else { return nil }
        return BurnKind.kind(for: sourceURL)
    }

    var sourceExists: Bool {
        guard let sourceURL else { return false }
        return FileManager.default.fileExists(atPath: sourceURL.path)
    }

    /// 流式刻录助手（disc-burn-stream）是否随应用提供。
    /// 完整流式刻录还需要把生成命令（cd-generate --output -）的参数接入管道；
    /// TODO: 在生成任务里暴露 `--output -` 参数后，用 `BurnStreamService.burn(plan:cliArguments:)` 接线。
    var isStreamAvailable: Bool {
        streamService != nil
    }

    /// 流式模式不可用时的具体原因；可用时为 nil。
    var streamUnavailableReason: String? {
        if Self.locateStreamHelper() == nil {
            return L("未找到 disc-burn-stream 助手（应为 Contents/Resources/bin/disc-burn-stream）。请重新构建或安装完整应用。")
        }
        if cliInvocation == nil {
            return L("流式刻录需要可用的 disc-image-studio CLI；请先在设置里指定 CLI。")
        }
        return nil
    }

    /// 由 AppModel 在 CLI 解析完成后注入，供将来的流式刻录使用。
    func updateCLI(_ cli: ResolvedCli?) {
        cliInvocation = cli?.invocation
    }

    private var streamService: BurnStreamService? {
        guard let helperURL = Self.locateStreamHelper(), let cliInvocation else { return nil }
        let service = BurnStreamService(helperURL: helperURL, cliInvocation: cliInvocation)
        return service.isAvailable ? service : nil
    }

    private static func locateStreamHelper() -> URL? {
        var candidates: [URL] = []
        if let auxiliary = Bundle.main.url(forAuxiliaryExecutable: "disc-burn-stream") {
            candidates.append(auxiliary)
        }
        if let resources = Bundle.main.resourceURL {
            candidates.append(resources.appendingPathComponent("bin/disc-burn-stream", isDirectory: false))
        }
        if let executable = Bundle.main.executableURL {
            candidates.append(executable.deletingLastPathComponent().appendingPathComponent("disc-burn-stream"))
        }
        candidates.append(Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/bin/disc-burn-stream"))
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    var isDRUtilAvailable: Bool {
        service.isAvailable
    }

    /// 为空表示可以开始刻录；非空是给用户看的中文原因。
    var blockReason: String? {
        guard mode == .file else {
            if let reason = streamUnavailableReason {
                return reason
            }
            return L("流式刻录已找到助手，但还需要把生成命令（cd-generate --output -）的参数接入管道；当前版本先请使用文件模式。")
        }
        guard isDRUtilAvailable else {
            return L("系统没有提供 /usr/bin/drutil，无法使用文件模式刻录。")
        }
        if isLoadingDrives || isLoadingStatus {
            return L("正在读取刻录机与介质状态…")
        }
        if drives.isEmpty {
            return L("没有找到光盘刻录机。请连接刻录机后点「刷新刻录机」。")
        }
        guard selectedDrive != nil else {
            return L("请选择一个刻录机。")
        }
        guard let status = mediaStatus else {
            if let driveErrorMessage, !driveErrorMessage.isEmpty {
                return driveErrorMessage
            }
            return L("还没有读取到介质状态，请点「刷新介质」。")
        }
        guard status.isPresent else {
            return L("刻录机里没有光盘。请放入空白盘后再试。")
        }
        // drutil 在「有驱动器但没放盘」时仍可能返回 0 且文本非空；没有 Type 字段时按无介质处理（更安全的默认）。
        guard let mediaType = status.mediaType else {
            return L("没有检测到可识别的光盘介质（drutil 没有报告 Type）。请放入空白 CD-R / DVD-R 后刷新。")
        }
        if !status.isWritable {
            return L("当前介质不可写入。")
        }
        guard status.isProbablyBlank else {
            return L("当前盘片可能有内容（检测到已有轨道或会话），不是空白盘，已禁止刻录。")
        }
        guard !sourcePath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return L("请选择要刻录的源文件（.wav / .aif / .iso / .dmg / .cue / .bin / .toc）。")
        }
        guard sourceExists else {
            return L("源文件不存在：%@", String(Formatting.abbreviatedPath(sourcePath)))
        }
        guard let kind = sourceKind else {
            return L("不支持直接刻录该文件类型。可刻录 .wav（音频 CD）或 .iso / .dmg / .cue / .bin / .toc（数据盘）。")
        }
        if kind == .audioCD, !mediaType.uppercased().hasPrefix("CD") {
            return L("音频 CD 需要空白 CD-R；当前介质是 %@。", String(mediaType))
        }
        if status.isBluRay, kind != .dataImage {
            return L("蓝光介质只能刻录数据镜像（.iso）；音频 CD 请使用 CD-R。")
        }
        return nil
    }

    var canBurn: Bool {
        blockReason == nil && !isBurning
    }

    var mediaStatusSummary: String {
        guard let status = mediaStatus else { return L("未知") }
        if !status.isPresent { return L("无介质") }
        var parts: [String] = []
        if let mediaType = status.mediaType { parts.append(mediaType) }
        parts.append(status.isProbablyBlank ? L("空白") : L("非空白"))
        if status.isBluRay { parts.append(L("BD（实验性）")) }
        return parts.isEmpty ? L("未知") : parts.joined(separator: " · ")
    }

    var mediaStatusDescription: String {
        guard let status = mediaStatus else { return L("尚未读取介质状态。") }
        if !status.isPresent { return L("刻录机里没有光盘。") }
        if status.isBluRay {
            return L("蓝光介质：可尝试数据镜像刻录（实验性，未经实机验证）；音频 CD 不受支持，失败请换用第三方软件。")
        }
        if !status.isProbablyBlank { return L("非空白盘：检测到已有轨道或会话，已禁止刻录。") }
        return L("空白 %@，可以刻录。", String(status.mediaType ?? L("光盘")))
    }

    var statusColor: Color {
        guard let status = mediaStatus else { return .orange }
        if !status.isPresent { return .orange }
        if status.isBluRay { return status.isProbablyBlank ? .orange : .red }
        return status.isProbablyBlank ? .green : .red
    }

    var sourceDetail: String? {
        guard sourceURL != nil else { return nil }
        if !sourceExists { return L("源文件不存在") }
        if let kind = sourceKind { return L("将作为%@刻录", String(kind.displayName)) }
        return L("不支持的文件类型")
    }

    // MARK: - 刻录机与介质

    func refreshDrives() async {
        guard !isLoadingDrives else { return }
        isLoadingDrives = true
        driveErrorMessage = nil
        defer { isLoadingDrives = false }

        do {
            let found = try await service.listDrives()
            drives = found
            if let selectedDriveIndex, found.contains(where: { $0.index == selectedDriveIndex }) {
                // keep selection
            } else {
                selectedDriveIndex = found.first?.index
            }
            if found.isEmpty {
                mediaStatus = nil
                driveErrorMessage = L("没有找到光盘刻录机。请连接刻录机后刷新。")
            } else {
                await refreshMediaStatus()
            }
        } catch {
            drives = []
            selectedDriveIndex = nil
            mediaStatus = nil
            driveErrorMessage = error.localizedDescription
        }
    }

    /// 由驱动器下拉框调用。
    func selectDrive(_ index: Int) {
        selectedDriveIndex = index
        Task { await refreshMediaStatus() }
    }

    func refreshMediaStatus() async {
        guard !isLoadingStatus else { return }
        guard let drive = selectedDrive else {
            mediaStatus = nil
            return
        }
        isLoadingStatus = true
        driveErrorMessage = nil
        defer { isLoadingStatus = false }

        do {
            mediaStatus = try await service.mediaStatus(drive: drive)
        } catch {
            mediaStatus = nil
            driveErrorMessage = error.localizedDescription
        }
    }

    // MARK: - 来源

    func setSourcePath(_ path: String) {
        sourcePath = path
    }

    func clearSource() {
        sourcePath = ""
    }

    /// 进入刻录页时用最近一次生成结果预填（如果用户还没选过）。
    func prefillIfNeeded(_ url: URL?) {
        guard sourcePath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, let url else { return }
        sourcePath = url.path
    }

    // MARK: - 流式刻录状态

    /// 进入刻录页时注入当前工程；没有工程时流式模式不可用。
    func updateStreamContext(project: ProjectModel?) {
        streamProject = project
    }

    var streamKind: StreamBurnKind? {
        guard let document = streamProject?.document else { return nil }
        switch document.discFamily {
        case .cd:
            // 音频 CD 才能通过 cd-generate --output - 输出 2352 字节块；
            // 带随盘文件的 CD 数据盘走文件模式。
            return document.outputs.hasBundledFiles ? nil : .cdAudio
        case .bd:
            return .bdData
        case .dvd:
            return .dvdData
        }
    }

    var streamSectors: Int? {
        guard let sectors = streamProject?.document.geometry.sectors, sectors > 0 else { return nil }
        return Int(sectors)
    }

    var streamUnsupportedReason: String? {
        guard streamProject != nil else { return L("请先打开一个工程，流式刻录会直接生成当前工程并写入光盘。") }
        guard let document = streamProject?.document else { return L("请先打开一个工程。") }
        switch document.discFamily {
        case .cd:
            if document.outputs.hasBundledFiles {
                return L("CD 数据盘的流式写入还没有 CLI 输出模式；请使用文件模式生成 ISO 后刻录。")
            }
            return nil
        case .dvd:
            return nil
        case .bd:
            return nil
        }
    }

    var selectedStreamDrive: StreamDrive? {
        if let selectedStreamDriveIndex,
           let match = streamDrives.first(where: { $0.index == selectedStreamDriveIndex }) {
            return match
        }
        return streamDrives.first
    }

    var streamDriveSummary: String {
        guard let drive = selectedStreamDrive else { return L("未检测到可流式写入的驱动器") }
        var parts = [drive.displayName]
        if let mediaType = drive.mediaType { parts.append(mediaType) }
        parts.append(drive.mediaPresent ? (drive.mediaBlank ? L("空白") : L("非空白")) : L("无介质"))
        return parts.joined(separator: " · ")
    }

    var streamBlockReason: String? {
        guard mode == .stream else { return nil }
        if let reason = streamUnavailableReason { return reason }
        if let reason = streamUnsupportedReason { return reason }
        guard let kind = streamKind else { return L("当前工程没有可用的流式刻录类型。") }
        guard let sectors = streamSectors else { return L("工程几何的扇区数无效。") }
        guard !streamDrives.isEmpty else { return L("没有找到支持 DiscRecording 流式写入的驱动器。") }
        guard let drive = selectedStreamDrive else { return L("请选择一个刻录机。") }
        guard drive.isWritable(for: kind) else {
            return L("当前驱动器不能写入 %@。", String(kind.displayName))
        }
        guard drive.mediaPresent else { return L("刻录机里没有光盘，请放入空白盘后刷新。") }
        guard drive.mediaBlank else { return L("当前盘片不是空白盘，已禁止刻录。") }
        if let mediaKind = StreamBurnKind.kind(forMediaType: drive.mediaType), mediaKind != kind {
            return L("盘片类型 %@ 与 %@ 不匹配。", String(drive.mediaType ?? L("未知")), String(kind.displayName))
        }
        if kind == .bdData, drive.mediaType?.uppercased() != "BD-R" {
            return L("BD 流式写入需要空白 BD-R。")
        }
        guard sectors > 0 else { return L("工程扇区数必须大于 0。") }
        return nil
    }

    func refreshStreamDrives() async {
        guard let service = streamService else {
            streamErrorMessage = streamUnavailableReason
            return
        }
        do {
            let found = try await service.listDrives()
            streamDrives = found
            if let selectedStreamDriveIndex,
               found.contains(where: { $0.index == selectedStreamDriveIndex }) {
                // keep
            } else {
                selectedStreamDriveIndex = found.first?.index
            }
            if found.isEmpty {
                streamErrorMessage = L("没有找到光盘刻录机。请连接刻录机后刷新。")
            } else {
                streamErrorMessage = nil
            }
        } catch {
            streamDrives = []
            selectedStreamDriveIndex = nil
            streamErrorMessage = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }

    func selectStreamDrive(_ index: Int) {
        selectedStreamDriveIndex = index
    }

    /// 从当前工程生成并直接写入光盘。生成参数与导出页一致，只是把
    /// `--output` 换成 `-`，由 CLI 通过管道写入 DiscRecording 助手。
    func startStreamingBurn() async {
        guard !isStreamBurning else { return }
        guard streamBlockReason == nil else { return }
        guard let service = streamService,
              let project = streamProject,
              let kind = streamKind,
              let sectors = streamSectors,
              let drive = selectedStreamDrive
        else { return }

        streamErrorMessage = nil
        streamResult = nil
        streamWasCancelled = false
        streamProgress = nil
        streamStage = L("准备中…")
        streamLogLines.removeAll()
        isStreamBurning = true

        do {
            let composite = try await project.prepareExportComposite()
            var arguments = try ExportService.makePlan(
                project: project.document,
                compositeURL: composite
            ).arguments
            let outputOption = project.document.discFamily == .dvd ? "--iso-output" : "--output"
            guard let outputIndex = arguments.firstIndex(of: outputOption), outputIndex + 1 < arguments.count else {
                throw StreamBurnError.invalidPlan(reason: L("生成命令没有 %@ 参数。", String(outputOption)))
            }
            arguments[outputIndex + 1] = "-"
            if kind == .cdAudio, let cueIndex = arguments.firstIndex(of: "--cue"), cueIndex + 1 < arguments.count {
                arguments[cueIndex + 1] = "false"
            }

            let plan = try service.makePlan(
                kind: kind,
                sectors: sectors,
                drive: drive,
                speed: streamSpeed,
                verify: true,
                eject: streamEject,
                test: false
            )
            streamPlan = plan
            let job = service.burn(plan: plan, cliArguments: arguments)
            streamJob = job
            streamTask = Task { @MainActor [weak self] in
                for await event in job.events {
                    guard let self else { return }
                    self.handleStreamEvent(event)
                }
            }
        } catch {
            isStreamBurning = false
            streamStage = nil
            streamErrorMessage = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            appendStreamSystem(L("流式刻录启动失败：%@", String(streamErrorMessage ?? L("未知错误"))))
        }
    }

    func cancelStreamingBurn() {
        guard isStreamBurning, let streamJob else { return }
        streamWasCancelled = true
        streamJob.cancel()
    }

    func clearStreamLog() {
        streamLogLines.removeAll()
    }

    private func handleStreamEvent(_ event: StreamBurnEvent) {
        switch event {
        case .started(let helperCommandLine, let cliCommandLine):
            appendStreamSystem(L("启动生成：%@", String(cliCommandLine)))
            appendStreamSystem(L("启动写入：%@", String(helperCommandLine)))
        case .log(let line):
            let stream: LogLine.Stream
            switch line.source {
            case .helper: stream = .stderr
            case .cli: stream = .stdout
            case .system: stream = .system
            }
            streamLogLines.append(LogLine(stream: stream, text: line.text, timestamp: line.timestamp))
        case .progress(let progress):
            streamProgress = progress.fraction
            switch progress.state {
            case .preparing: streamStage = L("准备刻录…")
            case .writing: streamStage = L("正在生成并写入…")
            case .verifying: streamStage = L("正在校验…")
            case .done: streamStage = L("完成")
            case .failed: streamStage = L("失败")
            }
        case .finished(let result):
            finishStream(result)
        }
    }

    private func finishStream(_ result: StreamBurnResult) {
        isStreamBurning = false
        streamResult = result
        streamJob = nil
        streamTask = nil
        if result.succeeded {
            streamProgress = 1
            streamStage = L("完成")
        }
        let status: String
        if streamWasCancelled {
            status = L("已取消流式刻录")
        } else if result.succeeded {
            status = L("流式刻录完成（用时 %@）", String(Formatting.duration(result.duration)))
        } else {
            status = L("流式刻录失败（助手退出码 %@）", String(result.helperExitCode))
        }
        appendStreamSystem(status)
        Task { await refreshStreamDrives() }
    }

    private func appendStreamSystem(_ text: String) {
        streamLogLines.append(LogLine(stream: .system, text: text))
    }

    // MARK: - 刻录

    func startBurn() {
        guard !isBurning else { return }
        guard blockReason == nil else { return }
        guard let sourceURL, let drive = selectedDrive else { return }

        let burnPlan: BurnPlan
        do {
            burnPlan = try service.makePlan(for: sourceURL, drive: drive, status: mediaStatus)
        } catch {
            burnErrorMessage = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            return
        }

        burnErrorMessage = nil
        lastResult = nil
        wasCancelled = false
        progress = nil
        stage = nil
        logLines.removeAll()
        isBurning = true
        plan = burnPlan

        let job = service.burn(burnPlan)
        self.job = job
        eventTask = Task { @MainActor [weak self] in
            for await event in job.events {
                guard let self else { return }
                self.handle(event)
            }
        }
    }

    func cancel() {
        guard isBurning, let job else { return }
        wasCancelled = true
        job.cancel()
    }

    func clearLog() {
        logLines.removeAll()
    }

    private func handle(_ event: CliEvent) {
        switch event {
        case .started(let commandLine):
            appendSystem(L("启动：%@", String(commandLine)))
        case .line(let line):
            logLines.append(line)
            if let fraction = ProgressParsing.fraction(from: line.text) {
                progress = fraction
            }
            if let parsed = ProgressParsing.stage(from: line.text), !parsed.isEmpty {
                stage = parsed
            } else {
                let lower = line.text.lowercased()
                if lower.contains("writing") {
                    stage = L("正在写入…")
                } else if lower.contains("verifying") {
                    stage = L("正在校验…")
                } else if lower.contains("finishing") {
                    stage = L("正在收尾…")
                }
            }
        case .finished(let result):
            finish(result)
        }
    }

    private func finish(_ result: CliResult) {
        isBurning = false
        lastResult = result
        job = nil
        eventTask = nil
        if result.succeeded {
            progress = 1
        }

        let status: String
        if wasCancelled {
            status = L("已取消刻录（退出码 %@）", String(result.exitCode))
        } else if result.succeeded {
            status = L("刻录完成（用时 %@），光盘已弹出。", String(Formatting.duration(result.duration)))
        } else {
            status = L("刻录失败（退出码 %@），请查看上方日志。", String(result.exitCode))
        }
        appendSystem(status)

        if let plan {
            service.discard(plan)
            self.plan = nil
        }
    }

    private func appendSystem(_ text: String) {
        logLines.append(LogLine(stream: .system, text: text))
    }
}
