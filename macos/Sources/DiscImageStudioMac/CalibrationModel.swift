import AppKit
import Foundation
import Observation
import DiscImageStudioKit

/// 完整标定向导状态：标靶 → 照片矫正 → 描点/反推 → 应用 → 记录。
@MainActor
@Observable
final class CalibrationModel {
    enum Step: Int, CaseIterable {
        case target = 1
        case photo = 2
        case fit = 3

        var title: String {
            switch self {
            case .target: return "生成标靶"
            case .photo: return "照片矫正"
            case .fit: return "描点与反推"
            }
        }

        var subtitle: String {
            switch self {
            case .target: return "生成用于刻录的标定盘"
            case .photo: return "拖拽盘缘与中心孔轮廓"
            case .fit: return "沿曲线打点并反推参数"
            }
        }
    }

    enum BoundaryKind {
        case outer
        case hole
    }

    unowned let project: ProjectModel
    let workspace: URL
    let recordStore: CalibrationRecordStore

    var step: Step = .target
    var mode: CalibrationMode = .fine
    var records: [CalibrationRecord] = []

    var targetImageURL: URL?
    var targetResult: CalibrationTargetResult?
    var coarseTargetResult: CalibrationCoarseTargetResult?
    var baseSessionURL: URL?
    var canonicalSessionURL: URL?

    var photoOriginalURL: URL?
    var photoURL: URL?
    var photoWidth: Int = 0
    var photoHeight: Int = 0
    var discSize: CalibrationDiscSize = .standard
    var outerBoundary: [CalibrationPoint] = []
    var holeBoundary: [CalibrationPoint] = []

    var rectifiedURL: URL?
    var rectifiedSize: Int = 1200
    var canvasOuterRadiusMm: Double = 60
    var rectificationConfirmed = false
    var showRectifiedPreview = true

    var tracePoints: [CalibrationPoint] = []
    var selectedPointIndex: Int?
    var fitResult: CalibrationFitResult?
    var coarseFitResult: CalibrationCoarseResult?
    var selectedCandidateIndex = 0
    var showPrediction = true

    var isBusy = false
    var busyText = ""
    var errorMessage: String?
    var statusMessage: String?
    var appliedMessage: String?
    var restoredSessionNote: String?

    @ObservationIgnored private var activeTask: Task<Void, Never>?
    @ObservationIgnored private var didRestore = false
    @ObservationIgnored private var currentRecordID: String?
    @ObservationIgnored private var currentRecordCreatedAt: String?
    @ObservationIgnored private var currentRecordAppliedAt: String?

    init(project: ProjectModel) {
        self.project = project
        self.workspace = CalibrationService.workspace(forProjectURL: project.fileURL)
        self.recordStore = CalibrationRecordStore(workspace: workspace)
        self.records = recordStore.load()
        self.discSize = .standard
        self.canvasOuterRadiusMm = CalibrationDiscSize.standard.physicalOuterRadiusMm
    }

    // MARK: - 派生

    var family: DiscFamily { project.family }
    var density: Double { CalibrationDensity.default(for: project.family) }
    var service: CalibrationService? {
        guard let invocation = project.cliInvocation else { return nil }
        return CalibrationService(cliInvocation: invocation, workspace: workspace)
    }

    var currentRecord: CalibrationRecord? {
        guard let currentRecordID else { return nil }
        return records.first { $0.id == currentRecordID }
    }

    var candidates: [CalibrationCandidate] {
        switch mode {
        case .fine: return fitResult?.candidates ?? []
        case .coarse: return coarseFitResult?.candidates ?? []
        }
    }

    var selectedCandidate: CalibrationCandidate? {
        let list = candidates
        guard selectedCandidateIndex >= 0, selectedCandidateIndex < list.count else { return nil }
        return list[selectedCandidateIndex]
    }

    var fitSucceeded: Bool {
        switch mode {
        case .fine: return fitResult?.succeeded == true
        case .coarse: return coarseFitResult?.succeeded == true
        }
    }

    var fitWarnings: [String] {
        switch mode {
        case .fine: return fitResult?.warnings ?? []
        case .coarse: return coarseFitResult?.warnings ?? []
        }
    }

    var boundaryLimited: Bool {
        fitResult?.boundaryLimited == true
    }

    var minimumPointCount: Int {
        mode == .fine ? 6 : 8
    }

    var recommendedPointCount: Int {
        mode == .fine ? (family == .dvd ? 12 : 20) : 8
    }

    var traceCoveragePercent: Double {
        let radii = tracePoints.map { hypot($0.x, $0.y) }
        guard let minR = radii.min(), let maxR = radii.max() else { return 0 }
        let ring = max(project.document.geometry.outerRadiusMm - project.document.geometry.innerRadiusMm, 0.001)
        return min(max((maxR - minR) / ring, 0), 1) * 100
    }

    var recommendedCoveragePercent: Double {
        mode == .fine ? (family == .dvd ? 60 : 50) : 30
    }

    var traceSummary: String {
        let coverage = String(format: "%.0f", traceCoveragePercent)
        let min = minimumPointCount
        return "\(tracePoints.count) 点 · 覆盖 \(coverage)% · 至少 \(min) 点（建议 \(recommendedPointCount) 点 / ≥\(Int(recommendedCoveragePercent))%）"
    }

    var canApply: Bool {
        applyDisabledReason == nil
    }

    var applyDisabledReason: String? {
        guard let candidate = selectedCandidate else {
            return fitSucceeded ? "请选择一个候选。" : "请先反推参数。"
        }
        switch mode {
        case .fine:
            guard let fitResult, fitResult.succeeded else { return "精调拟合失败，请检查描点。" }
            if candidate.rmsErrorMm > CalibrationFitResult.applicableRmsMm {
                return String(format: "平均偏差 %.3f mm 超过 0.35 mm。", candidate.rmsErrorMm)
            }
            if candidate.maxErrorMm > CalibrationFitResult.applicableMaxMm {
                return String(format: "最大偏差 %.3f mm 超过 0.8 mm。", candidate.maxErrorMm)
            }
            if baseSessionURL == nil {
                return "缺少标靶会话，无法写出 Windows 兼容会话。"
            }
            return nil
        case .coarse:
            guard let coarseFitResult else { return "请先反推参数。" }
            guard coarseFitResult.succeeded else { return "粗调拟合失败。" }
            if !coarseFitResult.warnings.isEmpty {
                return "粗调存在歧义警告，必须消除后才能应用。"
            }
            if coarseFitResult.bestIndex < 0 {
                return "粗调无法确定唯一结论（bestIndex = -1）。"
            }
            return nil
        }
    }

    var predictedPoints: [CalibrationPoint] {
        guard showPrediction, let candidate = selectedCandidate else { return [] }
        return candidate.predicted
    }

    var outlierIndices: Set<Int> {
        guard let candidate = selectedCandidate,
              candidate.predicted.count >= 2,
              !tracePoints.isEmpty else { return [] }
        let threshold = max(0.8, candidate.maxErrorMm * 1.2)
        var result: Set<Int> = []
        for (index, point) in tracePoints.enumerated() {
            if distanceToPolyline(point, candidate.predicted) > threshold {
                result.insert(index)
            }
        }
        return result
    }

    var monitorImageURL: URL? {
        switch step {
        case .target:
            return targetImageURL
        case .photo:
            if showRectifiedPreview, let rectifiedURL { return rectifiedURL }
            return photoURL
        case .fit:
            return rectifiedURL
        }
    }

    var monitorImageSize: CGSize {
        switch step {
        case .target:
            return CGSize(width: 2400, height: 2400)
        case .photo:
            if showRectifiedPreview, rectifiedURL != nil {
                return CGSize(width: rectifiedSize, height: rectifiedSize)
            }
            return CGSize(width: max(photoWidth, 1), height: max(photoHeight, 1))
        case .fit:
            return CGSize(width: rectifiedSize, height: rectifiedSize)
        }
    }

    var geometrySummary: String {
        var parts = [
            "r0 \(format(project.document.geometry.innerRadiusMm)) mm",
            "r1 \(format(project.document.geometry.outerRadiusMm)) mm",
            "\(project.document.geometry.sectors) 扇区",
            "盘型 \(family.familyLabel)",
        ]
        if family == .dvd {
            parts.append("pitch \(format(project.document.geometry.pitchLinear))/\(format(project.document.geometry.pitchQuadratic))/\(format(project.document.geometry.pitchCubic))")
        }
        return parts.joined(separator: " · ")
    }

    // MARK: - 恢复

    func restoreIfNeeded() async {
        guard !didRestore else { return }
        didRestore = true
        records = recordStore.load()

        if let sessionPath = project.document.calibrationSessionPath,
           let record = records.first(where: { $0.canonicalSessionPath == sessionPath }) {
            restore(record)
            statusMessage = "已从工程会话恢复标定记录。"
            return
        }

        if let sessionPath = project.document.calibrationSessionPath {
            if let service {
                do {
                    let summary = try await service.readSession(URL(fileURLWithPath: sessionPath))
                    restoredSessionNote = "找到标定会话（\(summary.family ?? "未知")，r0 \(format(summary.innerRadiusMm ?? 0)) / r1 \(format(summary.outerRadiusMm ?? 0))），但缺少 GUI 记录（.mac.json），无法恢复照片与描点。请重新导入照片并完成步骤 2–3。"
                } catch {
                    restoredSessionNote = "工程引用的标定会话无法读取：\(error.localizedDescription)"
                }
            } else {
                restoredSessionNote = "工程引用了标定会话，但未找到 CLI，无法读取会话摘要。"
            }
        } else if let latest = records.first {
            restore(latest)
            statusMessage = "已恢复最近一条标定记录。"
        }
    }

    func restore(_ record: CalibrationRecord) {
        mode = record.mode
        step = record.tracePoints.isEmpty
            ? (record.photoPath == nil ? .target : .photo)
            : .fit
        targetImageURL = URL(fileURLWithPath: record.targetImagePath)
        baseSessionURL = record.baseSessionPath.map { URL(fileURLWithPath: $0) }
        canonicalSessionURL = record.canonicalSessionPath.map { URL(fileURLWithPath: $0) }
        photoURL = record.photoPath.map { URL(fileURLWithPath: $0) }
        photoOriginalURL = record.photoPath.map { URL(fileURLWithPath: $0) }
        photoWidth = record.photoWidth
        photoHeight = record.photoHeight
        discSize = CalibrationDiscSize(rawValue: record.discSize) ?? .standard
        outerBoundary = record.outerBoundary
        holeBoundary = record.holeBoundary
        rectifiedURL = record.rectifiedPath.map { URL(fileURLWithPath: $0) }
        rectifiedSize = record.rectifiedSize
        canvasOuterRadiusMm = record.canvasOuterRadiusMm
        rectificationConfirmed = record.rectifiedPath != nil
        tracePoints = record.tracePoints
        fitResult = record.fit
        coarseFitResult = record.coarseFit
        selectedCandidateIndex = record.selectedCandidateIndex
        currentRecordID = record.id
        currentRecordCreatedAt = record.createdAt
        currentRecordAppliedAt = record.appliedAt
        appliedMessage = record.isApplied
            ? "此记录已应用：r0 \(format(record.appliedInnerRadiusMm ?? 0)) / r1 \(format(record.appliedOuterRadiusMm ?? 0)) mm"
            : nil
        selectedPointIndex = nil
        showRectifiedPreview = record.rectifiedPath != nil
        errorMessage = nil
        targetResult = nil
        coarseTargetResult = nil
    }

    func openRecord(_ record: CalibrationRecord) {
        restore(record)
        statusMessage = "已打开标定记录（\(record.createdAt)）。"
    }

    func deleteRecord(_ record: CalibrationRecord) {
        do {
            records = try recordStore.delete(id: record.id)
            if currentRecordID == record.id {
                currentRecordID = nil
                currentRecordCreatedAt = nil
                currentRecordAppliedAt = nil
            }
            statusMessage = "已删除记录。"
        } catch {
            errorMessage = "删除记录失败：\(error.localizedDescription)"
        }
    }

    func exportRecord(_ record: CalibrationRecord) {
        if let canonical = record.canonicalSessionPath,
           FileManager.default.fileExists(atPath: canonical) {
            let source = URL(fileURLWithPath: canonical)
            guard let destination = FilePanels.chooseOutputFile(
                title: "导出标定会话",
                currentPath: source.lastPathComponent,
                defaultDirectory: workspace.path,
                allowedExtensions: ["json"]
            ) else { return }
            do {
                if FileManager.default.fileExists(atPath: destination.path) {
                    try FileManager.default.removeItem(at: destination)
                }
                try FileManager.default.copyItem(at: source, to: destination)
                statusMessage = "会话已导出：\(destination.lastPathComponent)"
            } catch {
                errorMessage = "导出会话失败：\(error.localizedDescription)"
            }
            return
        }
        guard let destination = FilePanels.chooseOutputFile(
            title: "导出标定记录",
            currentPath: "calibration-\(record.id).json",
            defaultDirectory: workspace.path,
            allowedExtensions: ["json"]
        ) else { return }
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(record).write(to: destination, options: .atomic)
            statusMessage = "记录已导出：\(destination.lastPathComponent)"
        } catch {
            errorMessage = "导出记录失败：\(error.localizedDescription)"
        }
    }

    // MARK: - 模式与步骤

    func setMode(_ newMode: CalibrationMode) {
        guard newMode != mode else { return }
        mode = newMode
        resetAll()
        statusMessage = "已切换到\(newMode.displayName)，请重新生成标靶。"
    }

    func selectStep(_ newStep: Step) {
        switch newStep {
        case .target:
            step = .target
        case .photo:
            guard targetImageURL != nil else {
                errorMessage = "请先生成标靶。"
                return
            }
            step = .photo
        case .fit:
            guard rectificationConfirmed else {
                errorMessage = "请先完成并确认照片矫正。"
                return
            }
            step = .fit
        }
    }

    // MARK: - 步骤 1：标靶

    func generateTarget() {
        guard let service else {
            errorMessage = "未找到 CLI，无法生成标靶。请在设置里配置 CLI。"
            return
        }
        let geometry = project.document.geometry
        let family = project.family
        let density = density
        let invert = project.document.bluRaySettings.invertMapping
        let mode = mode
        let id = Self.shortID()
        let targetURL = workspace.appendingPathComponent("target-\(id).png")
        let sessionURL = workspace.appendingPathComponent("target-\(id).calibration.json")
        let coarseURL = workspace.appendingPathComponent("coarse-\(id).png")
        let p0URL = workspace.appendingPathComponent("coarse-\(id)-p0.json")

        run("正在生成标靶…") {
            try FileManager.default.createDirectory(at: self.workspace, withIntermediateDirectories: true)
            switch mode {
            case .fine:
                let request = CalibrationTargetRequest(
                    family: family,
                    geometry: geometry,
                    density: density,
                    invertMapping: invert,
                    drawingStart: 0,
                    drawingEnd: 0,
                    size: 2400,
                    outputURL: targetURL,
                    sessionURL: sessionURL
                )
                let result = try await service.generateTarget(request)
                try Task.checkCancellation()
                self.targetImageURL = result.imageURL
                self.targetResult = result
                self.coarseTargetResult = nil
                self.baseSessionURL = result.sessionURL
                self.canonicalSessionURL = nil
            case .coarse:
                let result = try await service.generateCoarseTarget(
                    family: family,
                    geometry: geometry,
                    density: density,
                    size: 2400,
                    outputURL: coarseURL,
                    p0URL: p0URL
                )
                try Task.checkCancellation()
                self.targetImageURL = result.imageURL
                self.coarseTargetResult = result
                self.targetResult = nil
                self.baseSessionURL = nil
                self.canonicalSessionURL = nil
            }
            self.resetPhotoAndFit()
            self.step = .target
            self.beginNewRecord()
            self.statusMessage = "标靶已生成：\(self.targetImageURL?.lastPathComponent ?? "")"
            self.persistRecord()
        }
    }

    func revealTargetInFinder() {
        guard let targetImageURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([targetImageURL])
    }

    func openCalibrationDirectory() {
        try? FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        NSWorkspace.shared.open(workspace)
    }

    // MARK: - 步骤 2：照片矫正

    func importPhoto() {
        guard targetImageURL != nil else {
            errorMessage = "请先生成标靶，再导入对应的盘面照片。"
            return
        }
        guard let source = FilePanels.chooseInputFile(
            title: "导入标定盘照片",
            currentPath: photoOriginalURL?.path
        ) else { return }
        loadPhoto(from: source)
    }

    /// 供向导与自动测试使用：直接导入指定照片。
    func loadPhoto(from source: URL) {
        guard targetImageURL != nil else {
            errorMessage = "请先生成标靶，再导入对应的盘面照片。"
            return
        }
        guard let service else {
            errorMessage = "未找到 CLI，无法处理照片。"
            return
        }
        let id = Self.shortID()
        let resized = workspace.appendingPathComponent("photo-\(id).png")
        let discSize = discSize
        run("正在准备照片…") {
            let result = try await service.preparePhoto(
                photoURL: source,
                discSize: discSize,
                resizedOut: resized
            )
            try Task.checkCancellation()
            self.photoOriginalURL = source
            let resizedURL = result.resizedPhotoPath.map { URL(fileURLWithPath: $0) } ?? resized
            self.photoURL = resizedURL
            var width = result.photoWidth
            var height = result.photoHeight
            if width <= 0 || height <= 0,
               let pixelSize = DesignComposer.imagePixelSize(resizedURL.path) {
                width = pixelSize.width
                height = pixelSize.height
            }
            self.photoWidth = max(width, 1)
            self.photoHeight = max(height, 1)
            self.defaultBoundaries()
            self.rectifiedURL = nil
            self.rectificationConfirmed = false
            self.showRectifiedPreview = true
            self.tracePoints = []
            self.fitResult = nil
            self.coarseFitResult = nil
            self.appliedMessage = nil
            self.step = .photo
            if !result.succeeded, !result.message.isEmpty {
                self.errorMessage = result.message
            } else {
                self.statusMessage = "照片已准备：\(self.photoWidth)×\(self.photoHeight) px，拖动轮廓点对齐盘缘与中心孔。"
            }
            self.persistRecord()
        }
    }

    func setDiscSize(_ size: CalibrationDiscSize) {
        guard size != discSize else { return }
        discSize = size
        invalidateAfterBoundaryChange()
        statusMessage = "盘片规格改为 \(size.displayName)，请重新计算矫正。"
        persistRecord()
    }

    func moveBoundary(kind: BoundaryKind, index: Int, to point: CalibrationPoint) {
        let clamped = CalibrationPoint(
            x: min(max(point.x, 0), Double(photoWidth)),
            y: min(max(point.y, 0), Double(photoHeight))
        )
        switch kind {
        case .outer:
            guard outerBoundary.indices.contains(index) else { return }
            guard outerBoundary[index] != clamped else { return }
            outerBoundary[index] = clamped
        case .hole:
            guard holeBoundary.indices.contains(index) else { return }
            guard holeBoundary[index] != clamped else { return }
            holeBoundary[index] = clamped
        }
        invalidateAfterBoundaryChange()
    }

    func resetBoundaries() {
        defaultBoundaries()
        invalidateAfterBoundaryChange()
        persistRecord()
    }

    func rectifyPhoto() {
        guard let service else {
            errorMessage = "未找到 CLI，无法计算矫正。"
            return
        }
        guard let photoOriginalURL, let photoURL else {
            errorMessage = "请先导入照片。"
            return
        }
        guard outerBoundary.count >= 5, holeBoundary.count >= 5 else {
            errorMessage = "外缘与中心孔各需要至少 5 个控制点。"
            return
        }
        let discSize = discSize
        let outer = outerBoundary
        let hole = holeBoundary
        let rectified = workspace.appendingPathComponent("rectified-\(Self.shortID()).png")
        run("正在计算照片矫正…") {
            let result = try await service.rectifyPhoto(
                photoURL: photoOriginalURL,
                discSize: discSize,
                outer: outer,
                hole: hole,
                resizedOut: photoURL,
                rectifiedOut: rectified,
                size: 1200
            )
            try Task.checkCancellation()
            self.photoWidth = max(result.photoWidth, self.photoWidth)
            self.photoHeight = max(result.photoHeight, self.photoHeight)
            let rectifiedPath = result.rectifiedImagePath ?? rectified.path
            if result.succeeded,
               FileManager.default.fileExists(atPath: rectifiedPath) {
                self.rectifiedURL = URL(fileURLWithPath: rectifiedPath)
                self.rectifiedSize = result.rectifiedSize ?? 1200
                self.canvasOuterRadiusMm = result.canvasOuterRadiusMm
                    ?? discSize.physicalOuterRadiusMm
                self.rectificationConfirmed = false
                self.showRectifiedPreview = true
                self.tracePoints = []
                self.fitResult = nil
                self.coarseFitResult = nil
                let outerRms = result.outerRmsPixels.map { String(format: "%.2f", $0) } ?? "—"
                let holeRms = result.holeRmsPixels.map { String(format: "%.2f", $0) } ?? "—"
                self.statusMessage = "矫正完成（外缘 RMS \(outerRms) px，中心孔 RMS \(holeRms) px）。请检查预览后确认。"
                self.errorMessage = nil
            } else {
                self.errorMessage = result.message.isEmpty ? "照片矫正失败，请调整轮廓点。" : result.message
            }
            self.persistRecord()
        }
    }

    func confirmRectification() {
        guard rectifiedURL != nil else {
            errorMessage = "请先计算矫正。"
            return
        }
        rectificationConfirmed = true
        step = .fit
        statusMessage = "矫正已确认，请在矫正图上沿一条清楚曲线打点。"
        persistRecord()
    }

    func backToPhoto() {
        step = .photo
        showRectifiedPreview = true
    }

    private func defaultBoundaries() {
        let width = Double(max(photoWidth, 1))
        let height = Double(max(photoHeight, 1))
        let center = CalibrationPoint(x: width / 2, y: height / 2)
        let extent = min(width, height)
        let outerRadius = extent * 0.43
        let holeRadius = extent * 0.125
        outerBoundary = (0..<8).map { index in
            let angle = Double(index) * 2 * .pi / 8
            return CalibrationPoint(
                x: center.x + outerRadius * cos(angle),
                y: center.y + outerRadius * sin(angle)
            )
        }
        holeBoundary = (0..<8).map { index in
            let angle = Double(index) * 2 * .pi / 8
            return CalibrationPoint(
                x: center.x + holeRadius * cos(angle),
                y: center.y + holeRadius * sin(angle)
            )
        }
    }

    private func invalidateAfterBoundaryChange() {
        rectifiedURL = nil
        rectificationConfirmed = false
        showRectifiedPreview = true
        tracePoints = []
        selectedPointIndex = nil
        fitResult = nil
        coarseFitResult = nil
        selectedCandidateIndex = 0
    }

    // MARK: - 步骤 3：描点与拟合

    func addTracePoint(_ point: CalibrationPoint) {
        guard rectificationConfirmed else { return }
        let clamped = clampToDisc(point)
        tracePoints.append(clamped)
        selectedPointIndex = tracePoints.count - 1
        clearFitResults()
    }

    func moveTracePoint(index: Int, to point: CalibrationPoint) {
        guard tracePoints.indices.contains(index) else { return }
        tracePoints[index] = clampToDisc(point)
        selectedPointIndex = index
        clearFitResults()
    }

    func selectTracePoint(_ index: Int?) {
        selectedPointIndex = index
    }

    func deleteSelectedPoint() {
        guard let index = selectedPointIndex, tracePoints.indices.contains(index) else { return }
        tracePoints.remove(at: index)
        selectedPointIndex = nil
        clearFitResults()
        persistRecord()
    }

    func clearTracePoints() {
        tracePoints.removeAll()
        selectedPointIndex = nil
        clearFitResults()
        persistRecord()
    }

    func persistAfterInteraction() {
        persistRecord()
    }

    private func clearFitResults() {
        fitResult = nil
        coarseFitResult = nil
        selectedCandidateIndex = 0
        appliedMessage = nil
    }

    func fit() {
        guard let service else {
            errorMessage = "未找到 CLI，无法反推参数。"
            return
        }
        guard rectificationConfirmed else {
            errorMessage = "请先确认照片矫正。"
            return
        }
        guard tracePoints.count >= minimumPointCount else {
            errorMessage = "至少需要 \(minimumPointCount) 个点才能反推参数。"
            return
        }
        let mode = mode
        let points = tracePoints
        let family = family
        let geometry = project.document.geometry
        let density = density
        let baseSession = baseSessionURL

        run("正在反推参数…") {
            switch mode {
            case .fine:
                guard let baseSession else {
                    self.errorMessage = "缺少标靶会话，无法反推。请重新生成精调标靶。"
                    return
                }
                let result = try await service.fit(
                    sessionURL: baseSession,
                    points: points,
                    radiusRange: nil,
                    autoRange: true,
                    fitPitch: family == .dvd
                )
                try Task.checkCancellation()
                self.fitResult = result
                self.coarseFitResult = nil
                self.selectedCandidateIndex = Self.validSelection(result.bestIndex, count: result.candidates.count)
                self.statusMessage = result.succeeded
                    ? "已得到 \(result.candidates.count) 个候选。"
                    : "拟合失败：\(result.warnings.joined(separator: "；"))"
                self.errorMessage = result.succeeded ? nil : (result.warnings.joined(separator: "\n").isEmpty ? "拟合失败。" : nil)
            case .coarse:
                let result = try await service.fitCoarse(
                    family: family,
                    geometry: geometry,
                    density: density,
                    points: points
                )
                try Task.checkCancellation()
                self.coarseFitResult = result
                self.fitResult = nil
                self.selectedCandidateIndex = Self.validSelection(result.bestIndex, count: result.candidates.count)
                self.statusMessage = result.succeeded
                    ? "已得到 \(result.candidates.count) 个粗调候选。"
                    : "粗调失败：\(result.warnings.joined(separator: "；"))"
                self.errorMessage = result.succeeded ? nil : (result.warnings.joined(separator: "\n").isEmpty ? "粗调失败。" : nil)
            }
            self.persistRecord()
        }
    }

    func selectCandidate(_ index: Int) {
        guard candidates.indices.contains(index) else { return }
        selectedCandidateIndex = index
        persistRecord()
    }

    func applyParameters() {
        guard let candidate = selectedCandidate, canApply else {
            errorMessage = applyDisabledReason ?? "当前候选不可应用。"
            return
        }
        let mode = mode
        let baseSession = baseSessionURL
        let photoURL = photoURL
        let outer = outerBoundary
        let hole = holeBoundary
        let points = tracePoints
        let discSize = discSize
        let canonical = workspace.appendingPathComponent("session-\(currentRecordID ?? Self.shortID()).calibration.json")
        let service = self.service

        run("正在应用标定参数…") {
            var canonicalURL: URL?
            if mode == .fine, let service {
                guard let baseSession, let photoURL else {
                    self.errorMessage = "缺少标靶会话或照片，无法写出会话。"
                    return
                }
                _ = try await service.writeSession(
                    baseSessionURL: baseSession,
                    photoURL: photoURL,
                    discSize: discSize,
                    outer: outer,
                    hole: hole,
                    points: points,
                    outputURL: canonical
                )
                try Task.checkCancellation()
                canonicalURL = canonical
            }
            self.project.applyCalibration(candidate, canonicalSessionURL: canonicalURL)
            self.canonicalSessionURL = canonicalURL
            self.currentRecordAppliedAt = ProjectDocument.timestamp()
            self.appliedMessage = String(
                format: "参数已更新：r0 %.3f mm / r1 %.3f mm",
                candidate.innerRadiusMm,
                candidate.outerRadiusMm
            )
            self.statusMessage = self.appliedMessage
            self.persistRecord()
        }
    }

    // MARK: - 记录

    private func beginNewRecord() {
        currentRecordID = Self.shortID()
        currentRecordCreatedAt = ProjectDocument.timestamp()
        currentRecordAppliedAt = nil
        appliedMessage = nil
    }

    private func persistRecord() {
        guard targetImageURL != nil else { return }
        if currentRecordID == nil {
            beginNewRecord()
        }
        let record = CalibrationRecord(
            id: currentRecordID ?? Self.shortID(),
            createdAt: currentRecordCreatedAt ?? ProjectDocument.timestamp(),
            mode: mode,
            family: family,
            generatedGeometry: project.document.geometry,
            density: density,
            invertMapping: project.document.bluRaySettings.invertMapping,
            targetImagePath: targetImageURL?.path ?? "",
            baseSessionPath: baseSessionURL?.path,
            canonicalSessionPath: canonicalSessionURL?.path,
            photoPath: photoURL?.path,
            photoWidth: photoWidth,
            photoHeight: photoHeight,
            discSize: discSize.rawValue,
            outerBoundary: outerBoundary,
            holeBoundary: holeBoundary,
            rectifiedPath: rectifiedURL?.path,
            rectifiedSize: rectifiedSize,
            canvasOuterRadiusMm: canvasOuterRadiusMm,
            tracePoints: tracePoints,
            fit: fitResult,
            coarseFit: coarseFitResult,
            selectedCandidateIndex: selectedCandidateIndex,
            appliedAt: currentRecordAppliedAt,
            appliedInnerRadiusMm: currentRecordAppliedAt == nil ? nil : selectedCandidate?.innerRadiusMm,
            appliedOuterRadiusMm: currentRecordAppliedAt == nil ? nil : selectedCandidate?.outerRadiusMm
        )
        do {
            records = try recordStore.save(record)
        } catch {
            errorMessage = "保存标定记录失败：\(error.localizedDescription)"
        }
    }

    // MARK: - 任务

    private func run(_ label: String, _ operation: @escaping @MainActor () async throws -> Void) {
        guard !isBusy else { return }
        isBusy = true
        busyText = label
        errorMessage = nil
        activeTask = Task { @MainActor [weak self] in
            guard let self else { return }
            do {
                try await operation()
                if Task.isCancelled {
                    self.statusMessage = "已取消等待；CLI 进程可能仍在后台完成写入。"
                }
            } catch is CancellationError {
                self.statusMessage = "已取消等待；CLI 进程可能仍在后台完成写入。"
            } catch {
                self.errorMessage = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            }
            self.isBusy = false
            self.activeTask = nil
        }
    }

    func cancelCurrentOperation() {
        guard isBusy else { return }
        statusMessage = "正在取消等待…"
        activeTask?.cancel()
    }

    // MARK: - 工具

    private func clampToDisc(_ point: CalibrationPoint) -> CalibrationPoint {
        let maxRadius = max(canvasOuterRadiusMm, 1)
        let radius = hypot(point.x, point.y)
        guard radius > maxRadius else { return point }
        let scale = maxRadius / radius
        return CalibrationPoint(x: point.x * scale, y: point.y * scale)
    }

    private func distanceToPolyline(_ point: CalibrationPoint, _ polyline: [CalibrationPoint]) -> Double {
        guard polyline.count >= 2 else { return .infinity }
        var best = Double.infinity
        for index in 0..<(polyline.count - 1) {
            best = min(best, Self.segmentDistance(point, polyline[index], polyline[index + 1]))
        }
        return best
    }

    private static func segmentDistance(_ p: CalibrationPoint, _ a: CalibrationPoint, _ b: CalibrationPoint) -> Double {
        let dx = b.x - a.x
        let dy = b.y - a.y
        let lengthSquared = dx * dx + dy * dy
        guard lengthSquared > 0 else { return hypot(p.x - a.x, p.y - a.y) }
        let t = min(max(((p.x - a.x) * dx + (p.y - a.y) * dy) / lengthSquared, 0), 1)
        return hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy))
    }

    private static func validSelection(_ index: Int, count: Int) -> Int {
        guard index >= 0, index < count else { return 0 }
        return index
    }

    private static func shortID() -> String {
        String(UUID().uuidString.prefix(8)).lowercased()
    }

    private func format(_ value: Double) -> String {
        if value == value.rounded() { return String(Int(value)) }
        return String(format: "%.3f", value)
    }

    private func resetAll() {
        targetImageURL = nil
        targetResult = nil
        coarseTargetResult = nil
        baseSessionURL = nil
        canonicalSessionURL = nil
        photoOriginalURL = nil
        photoURL = nil
        photoWidth = 0
        photoHeight = 0
        outerBoundary = []
        holeBoundary = []
        rectifiedURL = nil
        rectificationConfirmed = false
        tracePoints = []
        selectedPointIndex = nil
        fitResult = nil
        coarseFitResult = nil
        selectedCandidateIndex = 0
        appliedMessage = nil
        errorMessage = nil
        statusMessage = nil
        currentRecordID = nil
        currentRecordCreatedAt = nil
        currentRecordAppliedAt = nil
        step = .target
    }

    private func resetPhotoAndFit() {
        photoOriginalURL = nil
        photoURL = nil
        photoWidth = 0
        photoHeight = 0
        outerBoundary = []
        holeBoundary = []
        rectifiedURL = nil
        rectificationConfirmed = false
        showRectifiedPreview = true
        tracePoints = []
        selectedPointIndex = nil
        fitResult = nil
        coarseFitResult = nil
        selectedCandidateIndex = 0
        appliedMessage = nil
    }
}
