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
            case .file: return "文件模式"
            case .stream: return "流式模式"
            }
        }

        var displayName: String {
            switch self {
            case .file: return "文件模式（drutil）"
            case .stream: return "流式模式（DiscRecording，实验性）"
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
            return "未找到 disc-burn-stream 助手（应为 Contents/Resources/bin/disc-burn-stream）。请重新构建或安装完整应用。"
        }
        if cliInvocation == nil {
            return "流式刻录需要可用的 disc-image-studio CLI；请先在设置里指定 CLI。"
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
            return "流式刻录已找到助手，但还需要把生成命令（cd-generate --output -）的参数接入管道；当前版本先请使用文件模式。"
        }
        guard isDRUtilAvailable else {
            return "系统没有提供 /usr/bin/drutil，无法使用文件模式刻录。"
        }
        if isLoadingDrives || isLoadingStatus {
            return "正在读取刻录机与介质状态…"
        }
        if drives.isEmpty {
            return "没有找到光盘刻录机。请连接刻录机后点「刷新刻录机」。"
        }
        guard selectedDrive != nil else {
            return "请选择一个刻录机。"
        }
        guard let status = mediaStatus else {
            if let driveErrorMessage, !driveErrorMessage.isEmpty {
                return driveErrorMessage
            }
            return "还没有读取到介质状态，请点「刷新介质」。"
        }
        guard status.isPresent else {
            return "刻录机里没有光盘。请放入空白盘后再试。"
        }
        // drutil 在「有驱动器但没放盘」时仍可能返回 0 且文本非空；没有 Type 字段时按无介质处理（更安全的默认）。
        guard let mediaType = status.mediaType else {
            return "没有检测到可识别的光盘介质（drutil 没有报告 Type）。请放入空白 CD-R / DVD-R 后刷新。"
        }
        if !status.isWritable {
            return "当前介质不可写入。"
        }
        guard status.isProbablyBlank else {
            return "当前盘片可能有内容（检测到已有轨道或会话），不是空白盘，已禁止刻录。"
        }
        guard !sourcePath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return "请选择要刻录的源文件（.wav / .aif / .iso / .dmg / .cue / .bin / .toc）。"
        }
        guard sourceExists else {
            return "源文件不存在：\(Formatting.abbreviatedPath(sourcePath))"
        }
        guard let kind = sourceKind else {
            return "不支持直接刻录该文件类型。可刻录 .wav（音频 CD）或 .iso / .dmg / .cue / .bin / .toc（数据盘）。"
        }
        if kind == .audioCD, !mediaType.uppercased().hasPrefix("CD") {
            return "音频 CD 需要空白 CD-R；当前介质是 \(mediaType)。"
        }
        if status.isBluRay, kind != .dataImage {
            return "蓝光介质只能刻录数据镜像（.iso）；音频 CD 请使用 CD-R。"
        }
        return nil
    }

    var canBurn: Bool {
        blockReason == nil && !isBurning
    }

    var mediaStatusSummary: String {
        guard let status = mediaStatus else { return "未知" }
        if !status.isPresent { return "无介质" }
        var parts: [String] = []
        if let mediaType = status.mediaType { parts.append(mediaType) }
        parts.append(status.isProbablyBlank ? "空白" : "非空白")
        if status.isBluRay { parts.append("BD（实验性）") }
        return parts.isEmpty ? "未知" : parts.joined(separator: " · ")
    }

    var mediaStatusDescription: String {
        guard let status = mediaStatus else { return "尚未读取介质状态。" }
        if !status.isPresent { return "刻录机里没有光盘。" }
        if status.isBluRay {
            return "蓝光介质：可尝试数据镜像刻录（实验性，未经实机验证）；音频 CD 不受支持，失败请换用第三方软件。"
        }
        if !status.isProbablyBlank { return "非空白盘：检测到已有轨道或会话，已禁止刻录。" }
        return "空白 \(status.mediaType ?? "光盘")，可以刻录。"
    }

    var statusColor: Color {
        guard let status = mediaStatus else { return .orange }
        if !status.isPresent { return .orange }
        if status.isBluRay { return status.isProbablyBlank ? .orange : .red }
        return status.isProbablyBlank ? .green : .red
    }

    var sourceDetail: String? {
        guard sourceURL != nil else { return nil }
        if !sourceExists { return "源文件不存在" }
        if let kind = sourceKind { return "将作为\(kind.displayName)刻录" }
        return "不支持的文件类型"
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
                driveErrorMessage = "没有找到光盘刻录机。请连接刻录机后刷新。"
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
            appendSystem("启动：\(commandLine)")
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
                    stage = "正在写入…"
                } else if lower.contains("verifying") {
                    stage = "正在校验…"
                } else if lower.contains("finishing") {
                    stage = "正在收尾…"
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
            status = "已取消刻录（退出码 \(result.exitCode)）"
        } else if result.succeeded {
            status = "刻录完成（用时 \(Formatting.duration(result.duration))），光盘已弹出。"
        } else {
            status = "刻录失败（退出码 \(result.exitCode)），请查看上方日志。"
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
