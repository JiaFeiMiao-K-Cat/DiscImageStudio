import AppKit
import Foundation
import Observation
import DiscImageStudioKit

/// 底部任务坞的数据源：一次 CLI 预览/导出任务的状态与日志。
@MainActor
@Observable
final class WorkspaceTask {
    enum Kind: Equatable {
        case preview
        case export
    }

    var kind: Kind?
    var title: String = "就绪"
    var isRunning: Bool = false
    var progress: Double?
    var stage: String?
    var lines: [LogLine] = []
    var lastResult: CliResult?
    var wasCancelled = false

    @ObservationIgnored private var job: CliJob?
    @ObservationIgnored private var consumer: Task<Void, Never>?
    @ObservationIgnored private var completion: ((CliResult) -> Void)?

    var statusText: String {
        if isRunning {
            return title
        }
        if let lastResult {
            if wasCancelled { return "已取消" }
            return lastResult.succeeded ? "\(title)完成" : "\(title)失败（退出码 \(lastResult.exitCode)）"
        }
        return "就绪 · 尚未运行任务"
    }

    var stderrLines: [String] {
        lines.compactMap { line in
            guard line.stream == .stderr else { return nil }
            let text = line.text.trimmingCharacters(in: .whitespacesAndNewlines)
            return text.isEmpty ? nil : line.text
        }
    }

    var stderrTailText: String {
        Array(stderrLines.suffix(12)).joined(separator: "\n")
    }

    func start(
        kind: Kind,
        title: String,
        job: CliJob,
        onFinished: @escaping (CliResult) -> Void
    ) {
        guard !isRunning else { return }
        self.kind = kind
        self.title = title
        self.isRunning = true
        self.progress = nil
        self.stage = nil
        self.lines.removeAll()
        self.lastResult = nil
        self.wasCancelled = false
        self.completion = onFinished
        self.job = job

        let job = job
        consumer = Task { @MainActor [weak self] in
            for await event in job.events {
                guard let self else { return }
                self.handle(event)
            }
        }
    }

    func cancel() {
        guard isRunning, let job else { return }
        wasCancelled = true
        job.cancel()
    }

    func clearLog() {
        lines.removeAll()
    }

    func resetIfIdle() {
        guard !isRunning else { return }
        kind = nil
        title = "就绪"
        lastResult = nil
        progress = nil
        stage = nil
    }

    private func handle(_ event: CliEvent) {
        switch event {
        case .started(let commandLine):
            lines.append(LogLine(stream: .system, text: "启动：\(commandLine)"))
        case .line(let line):
            lines.append(line)
            if let fraction = ProgressParsing.fraction(from: line.text) {
                progress = fraction
            }
            if let parsed = ProgressParsing.stage(from: line.text), !parsed.isEmpty {
                stage = parsed
            }
        case .finished(let result):
            isRunning = false
            lastResult = result
            progress = result.succeeded ? 1 : progress
            job = nil
            consumer = nil
            lines.append(LogLine(
                stream: .system,
                text: wasCancelled
                    ? "已取消（退出码 \(result.exitCode)）"
                    : "进程结束（退出码 \(result.exitCode)，用时 \(Formatting.duration(result.duration))）"
            ))
            let completion = self.completion
            self.completion = nil
            completion?(result)
        }
    }
}

/// 当前打开的工程：文档、图层编辑、450ms 防抖合成、自动保存、预览/导出任务。
@MainActor
@Observable
final class ProjectModel {
    enum SaveState: Equatable {
        case saved
        case unsaved
        case saving
        case failed(String)
    }

    var document: ProjectDocument
    let fileURL: URL
    let task = WorkspaceTask()

    var selectedLayerID: String?
    var isDirty = false
    var saveState: SaveState = .saved
    var saveError: String?

    var designCompositeURL: URL?
    var designCompositeRevision = 0
    var isComposing = false
    var composeError: String?

    var previewMode: PreviewMode = .designProjection
    var previewSize: Int
    var samplesPerSector: Int = 16
    var actualInnerRadiusMm: Double
    var actualOuterRadiusMm: Double
    var actualPitchLinear: Double
    var actualPitchQuadratic: Double
    var actualPitchCubic: Double
    var invertMapping: Bool
    var deinterleave: Bool = true
    var byteStep: Int = 48
    var previewURL: URL?
    var previewRevision = 0
    var previewError: String?

    var artifactURL: URL?
    var exportError: String?
    var exportOutputURL: URL?
    var burnPrefillURL: URL?

    var cliInvocation: CliInvocation?
    var defaultOutputDirectory: String?

    @ObservationIgnored private let store: ProjectStore
    @ObservationIgnored private var composeTask: Task<Void, Never>?
    @ObservationIgnored private var saveTask: Task<Void, Never>?
    @ObservationIgnored private var previewTask: Task<Void, Never>?
    @ObservationIgnored private var exportTask: Task<Void, Never>?
    @ObservationIgnored private var editRevision = 0
    @ObservationIgnored private var composedRevision = -1
    @ObservationIgnored private var exportCompositeURL: URL?
    @ObservationIgnored private var onPersisted: ((ProjectDocument, URL) -> Void)?

    /// 清理上次会话遗留的导出合成图和预览 PNG（设计合成图固定名称，会被覆盖）。
    private static func removeStaleCacheArtifacts() {
        let fileManager = FileManager.default
        guard let files = try? fileManager.contentsOfDirectory(
            at: cacheDirectory,
            includingPropertiesForKeys: nil
        ) else { return }
        for url in files {
            let name = url.lastPathComponent
            if name.hasPrefix("export-composite-") || name.hasPrefix("preview-") {
                try? fileManager.removeItem(at: url)
            }
        }
    }

    static var cacheDirectory: URL {
        let cache = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Library/Caches", isDirectory: true)
        return cache.appendingPathComponent("DiscImageStudioMac", isDirectory: true)
    }

    init(
        document: ProjectDocument,
        fileURL: URL,
        cliInvocation: CliInvocation?,
        defaultOutputDirectory: String? = nil,
        onPersisted: ((ProjectDocument, URL) -> Void)? = nil
    ) {
        self.document = document
        self.fileURL = fileURL
        self.cliInvocation = cliInvocation
        self.defaultOutputDirectory = defaultOutputDirectory
        self.onPersisted = onPersisted
        self.store = ProjectStore()
        self.previewSize = PreviewRequest.defaultPreviewSize(for: document.discFamily)
        self.actualInnerRadiusMm = document.geometry.innerRadiusMm
        self.actualOuterRadiusMm = document.geometry.outerRadiusMm
        self.actualPitchLinear = document.geometry.pitchLinear
        self.actualPitchQuadratic = document.geometry.pitchQuadratic
        self.actualPitchCubic = document.geometry.pitchCubic
        self.invertMapping = document.bluRaySettings.invertMapping
        try? FileManager.default.createDirectory(
            at: ProjectModel.cacheDirectory,
            withIntermediateDirectories: true
        )
        ProjectModel.removeStaleCacheArtifacts()
        self.exportOutputURL = ExportServiceComp.fallbackURL(document: document)
    }

    func updateCLI(_ invocation: CliInvocation?) {
        cliInvocation = invocation
    }

    func handleCacheCleared() {
        designCompositeURL = nil
        composedRevision = -1
        previewURL = nil
        previewRevision += 1
        scheduleCompose()
    }

    // MARK: - 派生

    var family: DiscFamily { document.discFamily }
    var canvasOuterRadiusMm: Double { document.canvasOuterRadiusMm }
    var layers: [DesignLayer] { document.layers }
    var selectedLayer: DesignLayer? {
        guard let selectedLayerID else { return nil }
        return document.layers.first { $0.id == selectedLayerID }
    }
    var tone: ProjectTone { document.toneOrLegacy }
    var isPreviewing: Bool { task.isRunning && task.kind == .preview }
    var isExporting: Bool { task.isRunning && task.kind == .export }
    var canExport: Bool { !layers.isEmpty && !isExporting && !isComposing && cliInvocation != nil }
    var canPreview: Bool { cliInvocation != nil && !isPreviewing }

    var geometrySummary: String {
        var parts = [
            "r0 \(format(document.geometry.innerRadiusMm)) mm",
            "r1 \(format(document.geometry.outerRadiusMm)) mm",
            "\(document.geometry.sectors) 扇区",
        ]
        if family == .dvd {
            parts.append("pitch \(format(document.geometry.pitchLinear))/\(format(document.geometry.pitchQuadratic))/\(format(document.geometry.pitchCubic))")
        }
        return parts.joined(separator: " · ")
    }

    var sourceSummary: String {
        if layers.isEmpty { return "尚无图层，请先在设计中导入图片。" }
        return "设计页合成源图（\(DesignComposer.previewSize) px 预览 / \(DesignComposer.exportSize) px 导出）"
    }

    var deviationSummary: String? {
        let deltaInner = actualInnerRadiusMm - document.geometry.innerRadiusMm
        let deltaOuter = actualOuterRadiusMm - document.geometry.outerRadiusMm
        guard abs(deltaInner) > 0.001 || abs(deltaOuter) > 0.001 else { return nil }
        return "实测与生成几何差：内 \(signed(deltaInner)) mm，外 \(signed(deltaOuter)) mm"
    }

    var artifactDisplayURL: URL? {
        if let artifactURL, FileManager.default.fileExists(atPath: artifactURL.path) { return artifactURL }
        return nil
    }

    var latestArtifactURL: URL? {
        if let artifactDisplayURL { return artifactDisplayURL }
        let planned = (document.outputs.outputPath as NSString).expandingTildeInPath
        guard !planned.isEmpty else { return nil }
        let url = URL(fileURLWithPath: planned)
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }

    // MARK: - 图层

    func addLayerImages(_ urls: [URL]) {
        guard !urls.isEmpty else { return }
        var firstID: String?
        for url in urls {
            let name = url.deletingPathExtension().lastPathComponent
            let layer = DesignLayer(name: name.isEmpty ? "图层" : name, imagePath: url.path)
            document.layers.insert(layer, at: 0)
            if firstID == nil { firstID = layer.id }
        }
        selectedLayerID = firstID
        markDirty()
    }

    func selectLayer(_ id: String?) {
        selectedLayerID = id
    }

    func toggleVisibility(id: String) {
        mutateLayer(id: id) { $0.visible.toggle() }
    }

    func removeLayer(id: String) {
        document.layers.removeAll { $0.id == id }
        if selectedLayerID == id { selectedLayerID = nil }
        markDirty()
    }

    func removeSelectedLayer() {
        guard let selectedLayerID else { return }
        removeLayer(id: selectedLayerID)
    }

    func moveLayer(id: String, offset: Int) {
        guard let index = document.layers.firstIndex(where: { $0.id == id }) else { return }
        let target = index + offset
        guard document.layers.indices.contains(target) else { return }
        document.layers.swapAt(index, target)
        markDirty()
    }

    func updateSelectedLayer(_ mutate: (inout DesignLayer) -> Void) {
        guard let selectedLayerID else { return }
        mutateLayer(id: selectedLayerID, mutate)
    }

    func mutateLayer(id: String, _ mutate: (inout DesignLayer) -> Void) {
        guard let index = document.layers.firstIndex(where: { $0.id == id }) else { return }
        mutate(&document.layers[index])
        markDirty()
    }

    // MARK: - 影调

    func setDitherPercent(_ value: Double) {
        var tone = document.toneOrLegacy
        tone.ditherPercent = value
        document.tone = tone.normalized(for: family)
        markDirty()
    }

    func setBrightness(_ value: Double) {
        var tone = document.toneOrLegacy
        tone.brightness = min(max(value, -64), 64)
        document.tone = tone.normalized(for: family)
        markDirty()
    }

    func setLevels(_ value: Int) {
        guard family == .cd else { return }
        var tone = document.toneOrLegacy
        tone.levels = value == 4 ? 4 : 2
        document.tone = tone.normalized(for: family)
        markDirty()
    }

    func setSRGBLinearization(_ value: Bool) {
        var tone = document.toneOrLegacy
        tone.srgbLinearization = value
        document.tone = tone.normalized(for: family)
        markDirty()
    }

    // MARK: - 随盘文件与输出

    func setDataDirectory(_ path: String) {
        let wasDataMode = document.outputs.hasBundledFiles
        document.outputs.dataDirectory = path
        if family == .cd, wasDataMode != document.outputs.hasBundledFiles {
            adjustCdOutputExtension(toDataMode: document.outputs.hasBundledFiles)
        }
        markDirty()
    }

    /// CD 在音频/数据模式间切换时自动把输出扩展名改成 wav/iso。
    private func adjustCdOutputExtension(toDataMode: Bool) {
        let raw = document.outputs.outputPath.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !raw.isEmpty else { return }
        let url = URL(fileURLWithPath: (raw as NSString).expandingTildeInPath)
        let ext = url.pathExtension.lowercased()
        let target = toDataMode ? "iso" : "wav"
        if toDataMode, ext == "wav" || ext == "raw" {
            document.outputs.outputPath = url.deletingPathExtension().appendingPathExtension(target).path
            exportOutputURL = URL(fileURLWithPath: document.outputs.outputPath)
        } else if !toDataMode, ext == "iso" {
            document.outputs.outputPath = url.deletingPathExtension().appendingPathExtension(target).path
            exportOutputURL = URL(fileURLWithPath: document.outputs.outputPath)
        }
    }

    func setFilesAtOuterRing(_ outer: Bool) {
        document.outputs.filesAtOuterRing = outer
        markDirty()
    }

    func setOutputPath(_ path: String) {
        document.outputs.outputPath = path
        exportOutputURL = ExportServiceComp.fallbackURL(document: document)
        markDirty()
    }

    func setVolumeLabel(_ value: String) {
        document.outputs.volumeLabel = value
        markDirty()
    }

    func setWriteCue(_ value: Bool) {
        document.outputs.writeCue = value
        markDirty()
    }

    func setInterleave(_ value: Bool) {
        document.outputs.interleave = value
        markDirty()
    }

    func setGeometry(_ geometry: ProjectGeometry) {
        document.geometry = geometry
        markDirty()
    }

    func setBluRayInvert(_ value: Bool) {
        document.bluRay = ProjectBluRaySettings(invertMapping: value)
        markDirty()
    }

    // MARK: - 预览参数

    func setPreviewMode(_ mode: PreviewMode) {
        previewMode = mode
        previewError = nil
    }

    func setPreviewSize(_ size: Int) {
        previewSize = PreviewService.clampPreviewSize(size, for: family)
    }

    func setMeasuredInner(_ value: Double) {
        actualInnerRadiusMm = value
    }

    func setMeasuredOuter(_ value: Double) {
        actualOuterRadiusMm = value
    }

    func setMeasuredPitch(linear: Double? = nil, quadratic: Double? = nil, cubic: Double? = nil) {
        if let linear { actualPitchLinear = linear }
        if let quadratic { actualPitchQuadratic = quadratic }
        if let cubic { actualPitchCubic = cubic }
    }

    func setInvertMapping(_ value: Bool) {
        invertMapping = value
        setBluRayInvert(value)
    }

    func setArtifactURL(_ url: URL?) {
        artifactURL = url
        guard url != nil else { return }
        try? FileManager.default.createDirectory(
            at: ProjectModel.cacheDirectory,
            withIntermediateDirectories: true
        )
    }

    func previewRequest() -> PreviewRequest {
        PreviewRequest(
            mode: previewMode,
            previewSize: previewSize,
            samplesPerSector: samplesPerSector,
            actualInnerRadiusMm: actualInnerRadiusMm,
            actualOuterRadiusMm: actualOuterRadiusMm,
            actualPitchLinear: actualPitchLinear,
            actualPitchQuadratic: actualPitchQuadratic,
            actualPitchCubic: actualPitchCubic,
            invertMapping: invertMapping,
            artifactURL: artifactDisplayURL,
            deinterleave: deinterleave,
            byteStep: byteStep
        )
    }

    func applyCalibrationToPreview() {
        // 标定页写回预览实测参数（不改变生成几何）。
        previewError = nil
    }

    // MARK: - 合成

    func scheduleCompose() {
        composeTask?.cancel()
        composeTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(450))
            guard !Task.isCancelled, let self else { return }
            await self.composePreviewNow()
        }
    }

    func composePreviewNow() async {
        guard !isComposing else { return }
        guard composedRevision != editRevision || designCompositeURL == nil else { return }
        isComposing = true
        composeError = nil
        let revision = editRevision
        let layers = document.layers
        let radius = canvasOuterRadiusMm
        let destination = ProjectModel.cacheDirectory.appendingPathComponent("design-composite.png")
        do {
            try await ProjectModel.render(
                layers: layers,
                canvasOuterRadiusMm: radius,
                outputSize: DesignComposer.previewSize,
                to: destination
            )
            designCompositeURL = destination
            designCompositeRevision += 1
            composedRevision = revision
            if revision != editRevision {
                // 合成期间又有编辑：再排队一次。
                scheduleCompose()
            }
        } catch {
            composeError = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
        isComposing = false
    }

    private func ensurePreviewComposite() async throws -> URL {
        if let designCompositeURL, composedRevision == editRevision {
            return designCompositeURL
        }
        let layers = document.layers
        let radius = canvasOuterRadiusMm
        let destination = ProjectModel.cacheDirectory.appendingPathComponent("design-composite.png")
        try await ProjectModel.render(
            layers: layers,
            canvasOuterRadiusMm: radius,
            outputSize: DesignComposer.previewSize,
            to: destination
        )
        designCompositeURL = destination
        designCompositeRevision += 1
        composedRevision = editRevision
        return destination
    }

    private func ensureExportComposite() async throws -> URL {
        let destination = ProjectModel.cacheDirectory
            .appendingPathComponent("export-composite-\(UUID().uuidString).png")
        try await ProjectModel.render(
            layers: document.layers,
            canvasOuterRadiusMm: canvasOuterRadiusMm,
            outputSize: DesignComposer.exportSize,
            to: destination
        )
        if let old = exportCompositeURL { try? FileManager.default.removeItem(at: old) }
        exportCompositeURL = destination
        return destination
    }

    nonisolated private static func render(
        layers: [DesignLayer],
        canvasOuterRadiusMm: Double,
        outputSize: Int,
        to destination: URL
    ) async throws {
        try await Task.detached(priority: .userInitiated) {
            try DesignComposer().render(
                layers: layers,
                canvasOuterRadiusMm: canvasOuterRadiusMm,
                outputSize: outputSize,
                to: destination
            )
        }.value
    }

    // MARK: - 保存

    func markDirty() {
        editRevision += 1
        isDirty = true
        saveState = .unsaved
        scheduleCompose()
        scheduleSave()
    }

    private func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(900))
            guard !Task.isCancelled, let self else { return }
            self.saveNow()
        }
    }

    func saveNow() {
        guard isDirty else { return }
        saveState = .saving
        var doc = document
        doc.modifiedUtc = ProjectDocument.timestamp()
        if let designCompositeURL,
           let thumbnail = store.thumbnailBase64(fromComposite: designCompositeURL) {
            doc.thumbnailPngBase64 = thumbnail
        }
        do {
            try store.save(doc, to: fileURL)
            document = doc
            isDirty = false
            saveState = .saved
            saveError = nil
            onPersisted?(doc, fileURL)
        } catch {
            saveState = .failed((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)
            saveError = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }

    // MARK: - 预览

    func refreshPreview() {
        guard let cliInvocation else {
            previewError = "未找到 CLI，无法生成预览。请在设置里配置 CLI。"
            return
        }
        guard !task.isRunning else { return }
        previewError = nil
        let revision = editRevision
        previewTask?.cancel()
        previewTask = Task { @MainActor [weak self] in
            guard let self else { return }
            do {
                let composite = try await self.ensurePreviewComposite()
                let output = ProjectModel.cacheDirectory
                    .appendingPathComponent("preview-\(self.previewMode.rawValue)-\(UUID().uuidString).png")
                let plan = try PreviewService.makePlan(
                    project: self.document,
                    compositeURL: composite,
                    request: self.previewRequest(),
                    outputURL: output
                )
                let oldPreview = self.previewURL
                let job = PreviewService(cliInvocation: cliInvocation).run(plan)
                self.task.start(kind: .preview, title: "刷新预览", job: job) { [weak self] result in
                    guard let self else { return }
                    if result.succeeded {
                        self.previewURL = output
                        self.previewRevision += 1
                        self.previewError = nil
                        if let oldPreview, oldPreview != output {
                            try? FileManager.default.removeItem(at: oldPreview)
                        }
                    } else {
                        self.previewError = self.task.stderrTailText.isEmpty
                            ? "预览失败（退出码 \(result.exitCode)）"
                            : self.task.stderrTailText
                        try? FileManager.default.removeItem(at: output)
                    }
                }
                if revision != self.editRevision {
                    self.previewError = "工程在预览期间有新的修改，请再次刷新。"
                }
            } catch {
                self.previewError = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            }
        }
    }

    // MARK: - 导出

    func generate() {
        guard let cliInvocation else {
            exportError = "未找到 CLI，无法生成。请在设置里配置 CLI。"
            return
        }
        guard !layers.isEmpty else {
            exportError = "请先在设计中导入图片。"
            return
        }
        guard !task.isRunning else { return }
        exportError = nil
        exportTask?.cancel()
        exportTask = Task { @MainActor [weak self] in
            guard let self else { return }
            do {
                let outputURL = try ExportService.resolveOutput(
                    project: self.document,
                    outputURL: self.exportOutputURL
                )
                let composite = try await self.ensureExportComposite()
                let plan = try ExportService.makePlan(
                    project: self.document,
                    compositeURL: composite,
                    outputURL: outputURL
                )
                let job = ExportService(cliInvocation: cliInvocation).run(plan)
                self.task.start(kind: .export, title: "生成镜像", job: job) { [weak self] result in
                    guard let self else { return }
                    if result.succeeded {
                        self.artifactURL = plan.outputURL
                        self.exportOutputURL = plan.outputURL
                        self.document.outputs.outputPath = plan.outputURL.path
                        self.exportError = nil
                        self.markDirty()
                        self.saveNow()
                    } else {
                        self.exportError = self.task.stderrTailText.isEmpty
                            ? "生成失败（退出码 \(result.exitCode)）"
                            : self.task.stderrTailText
                    }
                }
            } catch {
                self.exportError = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            }
        }
    }

    func cancelTask() {
        task.cancel()
    }

    func revealOutput() {
        guard let url = artifactDisplayURL ?? exportOutputURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }

    func revealPreview() {
        guard let previewURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([previewURL])
    }

    // MARK: - 格式化

    private func format(_ value: Double) -> String {
        if value == value.rounded() { return String(Int(value)) }
        return String(format: "%.3f", value)
    }

    private func signed(_ value: Double) -> String {
        String(format: "%+.3f", value)
    }
}

/// 导出输出路径的 UI 默认值（Kit 的 resolveOutput 只负责校验）。
enum ExportServiceComp {
    static func fallbackURL(document: ProjectDocument) -> URL? {
        let raw = (document.outputs.outputPath as NSString).expandingTildeInPath
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !raw.isEmpty else { return nil }
        return URL(fileURLWithPath: raw)
    }
}
