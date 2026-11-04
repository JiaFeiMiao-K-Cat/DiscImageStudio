import AppKit
import Foundation
import ImageIO
import Observation
import DiscImageStudioKit

/// 完整标定向导状态：标靶 → 照片矫正 → 描点/反推 → 应用。
/// 持久化以工程 `calibration/<id>.calibration.json` 为唯一事实来源；
/// 记录列表直接扫描该目录，不再维护 `.mac.json` 或 records 索引。
@MainActor
@Observable
final class CalibrationModel {
    enum Step: Int, CaseIterable {
        case target = 1
        case photo = 2
        case fit = 3

        var title: String {
            switch self {
            case .target: return L("生成标靶")
            case .photo: return L("照片矫正")
            case .fit: return L("描点与反推")
            }
        }

        var subtitle: String {
            switch self {
            case .target: return L("生成用于刻录的标定盘")
            case .photo: return L("拖拽盘缘与中心孔轮廓")
            case .fit: return L("沿曲线打点并反推参数")
            }
        }
    }

    enum BoundaryKind {
        case outer
        case hole
    }

    unowned let project: ProjectModel
    let workspace: URL

    var step: Step = .target
    var mode: CalibrationMode = .fine
    var sessionEntries: [CalibrationSessionCatalog.Entry] = []

    var sessionURL: URL?
    var sessionID: String?
    var targetImageURL: URL?
    var baseSessionURL: URL? { sessionURL }
    var targetResult: CalibrationTargetResult?
    var coarseTargetResult: CalibrationCoarseTargetResult?

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
    @ObservationIgnored private var sessionWriteTask: Task<Void, Never>?
    @ObservationIgnored private var didRestore = false

    init(project: ProjectModel) {
        self.project = project
        self.workspace = CalibrationService.workspace(forProjectURL: project.fileURL)
        self.sessionEntries = CalibrationSessionCatalog(workspace: workspace).entries()
    }

    // MARK: - 基础派生

    var family: DiscFamily { project.family }
    var density: Double { CalibrationDensity.default(for: project.family) }
    var layout: CalibrationWorkspaceLayout { CalibrationWorkspaceLayout(workspace: workspace) }
    var catalog: CalibrationSessionCatalog { CalibrationSessionCatalog(workspace: workspace) }
    var service: CalibrationService? {
        guard let invocation = project.cliInvocation else { return nil }
        return CalibrationService(cliInvocation: invocation, workspace: workspace)
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

    var boundaryLimited: Bool { fitResult?.boundaryLimited == true }
    var minimumPointCount: Int { mode == .fine ? 6 : 8 }
    var recommendedPointCount: Int { mode == .fine ? (family == .dvd ? 12 : 20) : 8 }

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
        return L("%@ 点 · 覆盖 %@%% · 至少 %@ 点（建议 %@ 点 / ≥%@%%）", String(tracePoints.count), String(coverage), String(minimumPointCount), String(recommendedPointCount), String(Int(recommendedCoveragePercent)))
    }

    var canApply: Bool { applyDisabledReason == nil }

    var applyDisabledReason: String? {
        guard let candidate = selectedCandidate else {
            return fitSucceeded ? L("请选择一个候选。") : L("请先反推参数。")
        }
        switch mode {
        case .fine:
            guard let fitResult, fitResult.succeeded else { return L("精调拟合失败，请检查描点。") }
            if candidate.rmsErrorMm > CalibrationFitResult.applicableRmsMm {
                return String(format: L("平均偏差 %.3f mm 超过 0.35 mm。"), candidate.rmsErrorMm)
            }
            if candidate.maxErrorMm > CalibrationFitResult.applicableMaxMm {
                return String(format: L("最大偏差 %.3f mm 超过 0.8 mm。"), candidate.maxErrorMm)
            }
            if sessionURL == nil {
                return L("缺少标定会话，无法写回参数。")
            }
            return nil
        case .coarse:
            guard let coarseFitResult else { return L("请先反推参数。") }
            guard coarseFitResult.succeeded else { return L("粗调拟合失败。") }
            if !coarseFitResult.warnings.isEmpty {
                return L("粗调存在歧义警告，必须消除后才能应用。")
            }
            if coarseFitResult.bestIndex < 0 {
                return L("粗调无法确定唯一结论（bestIndex = -1）。")
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
        case .target: return targetImageURL
        case .photo:
            if showRectifiedPreview, let rectifiedURL { return rectifiedURL }
            return photoURL
        case .fit: return rectifiedURL
        }
    }

    var monitorImageSize: CGSize {
        switch step {
        case .target: return CGSize(width: 2400, height: 2400)
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
            L("%@ 扇区", String(project.document.geometry.sectors)),
            L("盘型 %@", String(family.familyLabel)),
        ]
        if family == .dvd {
            parts.append("pitch \(format(project.document.geometry.pitchLinear))/\(format(project.document.geometry.pitchQuadratic))/\(format(project.document.geometry.pitchCubic))")
        }
        return parts.joined(separator: " · ")
    }

    // MARK: - 会话记录（扫描目录）

    func refreshSessionEntries() {
        sessionEntries = catalog.entries()
    }

    func restoreIfNeeded() async {
        guard !didRestore else { return }
        didRestore = true
        refreshSessionEntries()
        guard let path = project.document.calibrationSessionPath,
              FileManager.default.fileExists(atPath: path) else {
            if let path = project.document.calibrationSessionPath {
                restoredSessionNote = L("工程引用的标定会话不存在：%@", String(path))
            }
            return
        }
        run(L("正在恢复标定会话…")) {
            try await self.restoreSession(at: URL(fileURLWithPath: path))
        }
    }

    func openEntry(_ entry: CalibrationSessionCatalog.Entry) {
        run(L("正在打开标定会话…")) {
            try await self.restoreSession(at: entry.path)
        }
    }

    private func restoreSession(at url: URL) async throws {
        guard let service else {
            throw CalibrationError.missingInput(L("未找到 CLI，无法恢复标定会话。"))
        }
        let session = try catalog.loadSession(at: url)
        guard session.target.parameters.family == project.family else {
            throw CalibrationError.invalidGeometry(
                L("会话盘型 %@ 与工程 %@ 不一致。", String(session.target.parameters.family.familyLabel), String(family.familyLabel))
            )
        }

        let id = CalibrationWorkspaceLayout.id(fromSessionURL: url)
        sessionURL = url
        sessionID = id
        mode = .fine
        targetResult = nil
        coarseTargetResult = nil
        fitResult = nil
        coarseFitResult = nil
        selectedCandidateIndex = 0
        selectedPointIndex = nil
        errorMessage = nil
        appliedMessage = nil

        let parameters = session.target.parameters
        let targetURL = layout.targetImageURL(id: id)
        if !FileManager.default.fileExists(atPath: targetURL.path) {
            let tempSession = workspace.appendingPathComponent(".restore-\(UUID().uuidString.lowercased()).calibration.json")
            let request = CalibrationTargetRequest(
                family: parameters.family,
                geometry: parameters.projectGeometry,
                density: parameters.density,
                invertMapping: parameters.invertMapping,
                drawingStart: parameters.drawingStart,
                drawingEnd: parameters.drawingEnd,
                size: 2400,
                outputURL: targetURL,
                sessionURL: tempSession
            )
            _ = try await service.generateTarget(request)
            try? FileManager.default.removeItem(at: tempSession)
        }
        try Task.checkCancellation()
        targetImageURL = FileManager.default.fileExists(atPath: targetURL.path) ? targetURL : nil

        if let photo = session.photo {
            let embeddedPhoto = layout.photoURL(id: id)
            try photo.writePhoto(to: embeddedPhoto)
            photoURL = embeddedPhoto
            photoOriginalURL = embeddedPhoto
            photoWidth = max(photo.organicPhotoWidth, 1)
            photoHeight = max(photo.organicPhotoHeight, 1)
            outerBoundary = photo.outerBoundary
            holeBoundary = photo.holeBoundary
            discSize = photo.discSize
            tracePoints = photo.polygonPoints
            rectificationConfirmed = photo.rectificationConfirmed
            showRectifiedPreview = true

            let rectified = layout.rectifiedURL(id: id)
            do {
                let result = try await service.rectifyPhoto(
                    photoURL: embeddedPhoto,
                    discSize: discSize,
                    outer: outerBoundary,
                    hole: holeBoundary,
                    resizedOut: embeddedPhoto,
                    rectifiedOut: rectified,
                    size: 1200
                )
                try Task.checkCancellation()
                if result.succeeded, FileManager.default.fileExists(atPath: rectified.path) {
                    rectifiedURL = rectified
                    rectifiedSize = result.rectifiedSize ?? 1200
                    canvasOuterRadiusMm = result.canvasOuterRadiusMm ?? discSize.physicalOuterRadiusMm
                } else {
                    restoredSessionNote = result.message.isEmpty
                        ? L("会话已恢复，但矫正图重建失败。")
                        : L("会话已恢复，但矫正图重建失败：%@", String(result.message))
                }
            } catch {
                restoredSessionNote = L("会话已恢复，但矫正图重建失败：%@", String(error.localizedDescription))
            }

            if photo.rectificationConfirmed, tracePoints.count >= 6 {
                step = .fit
                do {
                    let fit = try await service.fit(
                        sessionURL: url,
                        points: tracePoints,
                        radiusRange: nil,
                        autoRange: true,
                        fitPitch: project.family == .dvd
                    )
                    try Task.checkCancellation()
                    fitResult = fit
                    selectedCandidateIndex = Self.validSelection(fit.bestIndex, count: fit.candidates.count)
                } catch {
                    self.errorMessage = L("会话已恢复，但候选重算失败：%@", String(error.localizedDescription))
                }
            } else {
                step = .photo
            }
        } else {
            photoURL = nil
            photoOriginalURL = nil
            outerBoundary = []
            holeBoundary = []
            tracePoints = []
            rectificationConfirmed = false
            rectifiedURL = nil
            step = .target
        }

        project.setCalibrationSessionPath(url.path)
        refreshSessionEntries()
        statusMessage = L("已从 %@ 恢复标定会话。", String(url.lastPathComponent))
    }

    // MARK: - 应用状态

    func setMode(_ newMode: CalibrationMode) {
        guard newMode != mode else { return }
        sessionWriteTask?.cancel()
        mode = newMode
        resetAll()
        statusMessage = L("已切换到%@，请重新生成标靶。", String(newMode.displayName))
    }

    func selectStep(_ newStep: Step) {
        switch newStep {
        case .target:
            step = .target
        case .photo:
            guard targetImageURL != nil else {
                errorMessage = L("请先生成标靶。")
                return
            }
            step = .photo
        case .fit:
            guard rectificationConfirmed else {
                errorMessage = L("请先完成并确认照片矫正。")
                return
            }
            step = .fit
        }
    }

    // MARK: - 步骤 1

    func generateTarget() {
        guard let service else {
            errorMessage = L("未找到 CLI，无法生成标靶。请在设置里配置 CLI。")
            return
        }
        let geometry = project.document.geometry
        let family = project.family
        let density = density
        let invert = project.document.bluRaySettings.invertMapping
        let mode = mode
        let id = Self.shortID()
        let targetURL = layout.targetImageURL(id: id)
        let session = layout.sessionURL(id: id)
        let coarseURL = layout.coarseImageURL(id: id)
        let p0URL = layout.coarseP0URL(id: id)

        run(L("正在生成标靶…")) {
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
                    sessionURL: session
                )
                let result = try await service.generateTarget(request)
                try Task.checkCancellation()
                self.sessionID = id
                self.sessionURL = result.sessionURL
                self.targetImageURL = result.imageURL
                self.targetResult = result
                self.coarseTargetResult = nil
                self.project.setCalibrationSessionPath(result.sessionURL.path)
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
                self.sessionID = id
                self.sessionURL = nil
                self.targetImageURL = result.imageURL
                self.coarseTargetResult = result
                self.targetResult = nil
            }
            self.resetPhotoAndFit()
            self.step = .target
            self.refreshSessionEntries()
            self.statusMessage = L("标靶已生成：%@", String(self.targetImageURL?.lastPathComponent ?? ""))
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

    // MARK: - 步骤 2

    func importPhoto() {
        guard targetImageURL != nil else {
            errorMessage = L("请先生成标靶，再导入对应的盘面照片。")
            return
        }
        guard let source = FilePanels.chooseInputFile(
            title: L("导入标定盘照片"),
            currentPath: photoOriginalURL?.path
        ) else { return }
        loadPhoto(from: source)
    }

    func loadPhoto(from source: URL) {
        guard targetImageURL != nil else {
            errorMessage = L("请先生成标靶，再导入对应的盘面照片。")
            return
        }
        guard let service else {
            errorMessage = L("未找到 CLI，无法处理照片。")
            return
        }
        let id = sessionID ?? Self.shortID()
        sessionID = id
        let resized = layout.photoURL(id: id)
        let discSize = discSize
        run(L("正在准备照片…")) {
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
            }
            if self.mode == .fine {
                do {
                    try await self.writeSessionNow(confirmed: false, points: [])
                } catch {
                    self.errorMessage = L("照片已导入，但会话保存失败：%@", String(error.localizedDescription))
                }
            }
            self.statusMessage = L("照片已准备：%@×%@ px，拖动轮廓点对齐盘缘与中心孔。", String(self.photoWidth), String(self.photoHeight))
        }
    }

    func setDiscSize(_ size: CalibrationDiscSize) {
        guard size != discSize else { return }
        discSize = size
        invalidateAfterBoundaryChange()
        statusMessage = L("盘片规格改为 %@，请重新计算矫正。", String(size.displayName))
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
    }

    func rectifyPhoto() {
        guard let service else {
            errorMessage = L("未找到 CLI，无法计算矫正。")
            return
        }
        guard let photoOriginalURL, let photoURL else {
            errorMessage = L("请先导入照片。")
            return
        }
        guard outerBoundary.count >= 5, holeBoundary.count >= 5 else {
            errorMessage = L("外缘与中心孔各需要至少 5 个控制点。")
            return
        }
        let discSize = discSize
        let outer = outerBoundary
        let hole = holeBoundary
        let rectified = layout.rectifiedURL(id: sessionID ?? Self.shortID())
        run(L("正在计算照片矫正…")) {
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
            if result.succeeded, FileManager.default.fileExists(atPath: rectifiedPath) {
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
                self.statusMessage = L("矫正完成（外缘 RMS %@ px，中心孔 RMS %@ px）。请检查预览后确认。", String(outerRms), String(holeRms))
                self.errorMessage = nil
                if self.mode == .fine {
                    do {
                        try await self.writeSessionNow(confirmed: false, points: [])
                    } catch {
                        self.errorMessage = L("矫正已计算，但会话保存失败：%@", String(error.localizedDescription))
                    }
                }
            } else {
                self.errorMessage = result.message.isEmpty ? L("照片矫正失败，请调整轮廓点。") : result.message
            }
        }
    }

    func confirmRectification() {
        guard rectifiedURL != nil else {
            errorMessage = L("请先计算矫正。")
            return
        }
        rectificationConfirmed = true
        step = .fit
        statusMessage = L("矫正已确认，请在矫正图上沿一条清楚曲线打点。")
        guard mode == .fine else { return }
        run(L("正在保存矫正状态…")) {
            do {
                try await self.writeSessionNow(confirmed: true, points: self.tracePoints)
            } catch {
                self.errorMessage = L("矫正已确认，但会话保存失败：%@", String(error.localizedDescription))
            }
        }
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
        appliedMessage = nil
    }

    // MARK: - 步骤 3

    func addTracePoint(_ point: CalibrationPoint) {
        guard rectificationConfirmed else { return }
        tracePoints.append(clampToDisc(point))
        selectedPointIndex = tracePoints.count - 1
        clearFitResults()
        scheduleSessionWrite()
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
        scheduleSessionWrite()
    }

    func clearTracePoints() {
        tracePoints.removeAll()
        selectedPointIndex = nil
        clearFitResults()
        scheduleSessionWrite()
    }

    func persistAfterInteraction() {
        scheduleSessionWrite()
    }

    private func clearFitResults() {
        fitResult = nil
        coarseFitResult = nil
        selectedCandidateIndex = 0
        appliedMessage = nil
    }

    func fit() {
        guard let service else {
            errorMessage = L("未找到 CLI，无法反推参数。")
            return
        }
        guard rectificationConfirmed else {
            errorMessage = L("请先确认照片矫正。")
            return
        }
        guard tracePoints.count >= minimumPointCount else {
            errorMessage = L("至少需要 %@ 个点才能反推参数。", String(minimumPointCount))
            return
        }
        sessionWriteTask?.cancel()
        let mode = mode
        let points = tracePoints
        let family = family
        let geometry = project.document.geometry
        let density = density
        let sessionURL = sessionURL

        run(L("正在反推参数…")) {
            switch mode {
            case .fine:
                guard let sessionURL else {
                    self.errorMessage = L("缺少标定会话，无法反推。请重新生成精调标靶。")
                    return
                }
                let result = try await service.fit(
                    sessionURL: sessionURL,
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
                    ? L("已得到 %@ 个候选。", String(result.candidates.count))
                    : L("拟合失败：%@", String(result.warnings.joined(separator: "；")))
                self.errorMessage = result.succeeded ? nil : (result.warnings.isEmpty ? L("拟合失败。") : nil)
                self.refreshSessionEntries()
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
                    ? L("已得到 %@ 个粗调候选。", String(result.candidates.count))
                    : L("粗调失败：%@", String(result.warnings.joined(separator: "；")))
                self.errorMessage = result.succeeded ? nil : (result.warnings.isEmpty ? L("粗调失败。") : nil)
            }
        }
    }

    func selectCandidate(_ index: Int) {
        guard candidates.indices.contains(index) else { return }
        selectedCandidateIndex = index
    }

    func applyParameters() {
        guard let candidate = selectedCandidate, canApply else {
            errorMessage = applyDisabledReason ?? L("当前候选不可应用。")
            return
        }
        let mode = mode
        let sessionURL = sessionURL
        let service = self.service
        let photoURL = photoURL
        let outer = outerBoundary
        let hole = holeBoundary
        let points = tracePoints
        let discSize = discSize

        run(L("正在应用标定参数…")) {
            switch mode {
            case .fine:
                guard let sessionURL, let service, let photoURL else {
                    self.errorMessage = L("缺少标定会话或照片，无法写回会话。")
                    return
                }
                _ = try await service.writeSession(
                    baseSessionURL: sessionURL,
                    photoURL: photoURL,
                    discSize: discSize,
                    outer: outer,
                    hole: hole,
                    points: points,
                    outputURL: sessionURL,
                    confirmed: true
                )
                try Task.checkCancellation()
                self.project.applyCalibration(candidate, canonicalSessionURL: sessionURL)
                self.appliedMessage = String(
                    format: L("参数已更新：r0 %.3f mm / r1 %.3f mm"),
                    candidate.innerRadiusMm,
                    candidate.outerRadiusMm
                )
            case .coarse:
                self.project.applyCalibration(candidate, canonicalSessionURL: nil)
                self.sessionURL = nil
                self.appliedMessage = L("参数已更新；粗调不单独保存会话。")
            }
            self.statusMessage = self.appliedMessage
            self.refreshSessionEntries()
        }
    }

    // MARK: - 会话写入

    private func writeSessionNow(confirmed: Bool, points: [CalibrationPoint]) async throws {
        guard mode == .fine, let service, let sessionURL, let photoURL else { return }
        _ = try await service.writeSession(
            baseSessionURL: sessionURL,
            photoURL: photoURL,
            discSize: discSize,
            outer: outerBoundary,
            hole: holeBoundary,
            points: points,
            outputURL: sessionURL,
            confirmed: confirmed
        )
        refreshSessionEntries()
    }

    private func scheduleSessionWrite() {
        guard mode == .fine, sessionURL != nil, photoURL != nil else { return }
        sessionWriteTask?.cancel()
        sessionWriteTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(450))
            guard !Task.isCancelled, let self else { return }
            guard let service = self.service,
                  let sessionURL = self.sessionURL,
                  let photoURL = self.photoURL else { return }
            do {
                _ = try await service.writeSession(
                    baseSessionURL: sessionURL,
                    photoURL: photoURL,
                    discSize: self.discSize,
                    outer: self.outerBoundary,
                    hole: self.holeBoundary,
                    points: self.tracePoints,
                    outputURL: sessionURL,
                    confirmed: self.rectificationConfirmed
                )
                self.refreshSessionEntries()
                self.statusMessage = L("标定会话已更新。")
            } catch {
                self.errorMessage = L("保存标定会话失败：%@", String(error.localizedDescription))
            }
        }
    }

    // MARK: - 记录操作

    func deleteEntry(_ entry: CalibrationSessionCatalog.Entry) {
        do {
            let id = CalibrationWorkspaceLayout.id(fromSessionURL: entry.path)
            try catalog.delete(entry)
            // 会话命名约定的配套文件也一并清理（Kit 的 delete 只处理记录文件时的兜底）。
            for url in [
                layout.targetImageURL(id: id),
                layout.photoURL(id: id),
                layout.rectifiedURL(id: id),
                layout.coarseImageURL(id: id),
                layout.coarseP0URL(id: id),
            ] {
                try? FileManager.default.removeItem(at: url)
            }
            if sessionURL?.path == entry.path.path {
                project.setCalibrationSessionPath(nil)
                sessionURL = nil
                sessionID = nil
                targetImageURL = nil
                resetPhotoAndFit()
                step = .target
            }
            refreshSessionEntries()
            statusMessage = L("已删除标定会话 %@。", String(entry.path.lastPathComponent))
        } catch {
            errorMessage = L("删除标定会话失败：%@", String(error.localizedDescription))
        }
    }

    func exportEntry(_ entry: CalibrationSessionCatalog.Entry) {
        guard let destination = FilePanels.chooseOutputFile(
            title: L("导出标定会话"),
            currentPath: entry.path.lastPathComponent,
            defaultDirectory: workspace.path,
            allowedExtensions: ["json"]
        ) else { return }
        do {
            try catalog.export(entry, to: destination)
            statusMessage = L("会话已导出：%@", String(destination.lastPathComponent))
        } catch {
            errorMessage = L("导出标定会话失败：%@", String(error.localizedDescription))
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
                    self.statusMessage = L("已取消等待；CLI 进程可能仍在后台完成写入。")
                }
            } catch is CancellationError {
                self.statusMessage = L("已取消等待；CLI 进程可能仍在后台完成写入。")
            } catch {
                self.errorMessage = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            }
            self.isBusy = false
            self.activeTask = nil
        }
    }

    func cancelCurrentOperation() {
        guard isBusy else { return }
        statusMessage = L("正在取消等待…")
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
        sessionURL = nil
        sessionID = nil
        targetImageURL = nil
        targetResult = nil
        coarseTargetResult = nil
        resetPhotoAndFit()
        errorMessage = nil
        statusMessage = nil
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

private extension CalibrationSessionDocument.PhotoInfo {
    /// 会话只保存缩小照片的 PNG 数据；恢复时从 PNG 头读尺寸。
    var organicPhotoWidth: Int {
        guard let data = pngData, let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int
        else { return 0 }
        return width
    }

    var organicPhotoHeight: Int {
        guard let data = pngData, let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let height = properties[kCGImagePropertyPixelHeight] as? Int
        else { return 0 }
        return height
    }
}
