import Foundation

public enum CalibrationMode: String, Codable, Sendable, CaseIterable {
    case fine
    case coarse

    public var displayName: String {
        switch self {
        case .fine: return "精调（内置靶标）"
        case .coarse: return "粗调（半黑白盘）"
        }
    }
}

/// GUI 侧标定记录：保存照片、轮廓、描线、候选与应用状态。
/// 真正的 Windows 会话仍是 `.calibration.json`，由 CLI 写入。
public struct CalibrationRecord: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var createdAt: String
    public var mode: CalibrationMode
    public var family: DiscFamily
    public var generatedGeometry: ProjectGeometry
    public var density: Double
    public var invertMapping: Bool
    public var targetImagePath: String
    /// 精调靶标生成的 schema 6 会话骨架（未含照片）。
    public var baseSessionPath: String?
    /// 写出的 Windows 兼容完整会话（project.calibrationSessionPath 指向它）。
    public var canonicalSessionPath: String?
    public var photoPath: String?
    public var photoWidth: Int
    public var photoHeight: Int
    public var discSize: Int
    public var outerBoundary: [CalibrationPoint]
    public var holeBoundary: [CalibrationPoint]
    public var rectifiedPath: String?
    public var rectifiedSize: Int
    public var canvasOuterRadiusMm: Double
    public var tracePoints: [CalibrationPoint]
    public var fit: CalibrationFitResult?
    public var coarseFit: CalibrationCoarseResult?
    public var selectedCandidateIndex: Int
    public var appliedAt: String?
    public var appliedInnerRadiusMm: Double?
    public var appliedOuterRadiusMm: Double?

    public init(
        id: String = UUID().uuidString.lowercased(),
        createdAt: String = ProjectDocument.timestamp(),
        mode: CalibrationMode,
        family: DiscFamily,
        generatedGeometry: ProjectGeometry,
        density: Double,
        invertMapping: Bool = false,
        targetImagePath: String,
        baseSessionPath: String? = nil,
        canonicalSessionPath: String? = nil,
        photoPath: String? = nil,
        photoWidth: Int = 0,
        photoHeight: Int = 0,
        discSize: Int = 120,
        outerBoundary: [CalibrationPoint] = [],
        holeBoundary: [CalibrationPoint] = [],
        rectifiedPath: String? = nil,
        rectifiedSize: Int = 1200,
        canvasOuterRadiusMm: Double = 60,
        tracePoints: [CalibrationPoint] = [],
        fit: CalibrationFitResult? = nil,
        coarseFit: CalibrationCoarseResult? = nil,
        selectedCandidateIndex: Int = 0,
        appliedAt: String? = nil,
        appliedInnerRadiusMm: Double? = nil,
        appliedOuterRadiusMm: Double? = nil
    ) {
        self.id = id
        self.createdAt = createdAt
        self.mode = mode
        self.family = family
        self.generatedGeometry = generatedGeometry
        self.density = density
        self.invertMapping = invertMapping
        self.targetImagePath = targetImagePath
        self.baseSessionPath = baseSessionPath
        self.canonicalSessionPath = canonicalSessionPath
        self.photoPath = photoPath
        self.photoWidth = photoWidth
        self.photoHeight = photoHeight
        self.discSize = discSize
        self.outerBoundary = outerBoundary
        self.holeBoundary = holeBoundary
        self.rectifiedPath = rectifiedPath
        self.rectifiedSize = rectifiedSize
        self.canvasOuterRadiusMm = canvasOuterRadiusMm
        self.tracePoints = tracePoints
        self.fit = fit
        self.coarseFit = coarseFit
        self.selectedCandidateIndex = selectedCandidateIndex
        self.appliedAt = appliedAt
        self.appliedInnerRadiusMm = appliedInnerRadiusMm
        self.appliedOuterRadiusMm = appliedOuterRadiusMm
    }

    public var candidates: [CalibrationCandidate] {
        switch mode {
        case .fine: return fit?.candidates ?? []
        case .coarse: return coarseFit?.candidates ?? []
        }
    }

    public var selectedCandidate: CalibrationCandidate? {
        let list = candidates
        guard selectedCandidateIndex >= 0, selectedCandidateIndex < list.count else { return nil }
        return list[selectedCandidateIndex]
    }

    public var isApplied: Bool {
        appliedAt != nil
    }

    public var isRectificationConfirmed: Bool {
        rectifiedPath != nil
    }
}

public struct CalibrationRecordStore: Sendable {
    public let workspace: URL
    public let fileURL: URL

    public init(workspace: URL) {
        self.workspace = workspace
        self.fileURL = workspace.appendingPathComponent("calibration-records.json", isDirectory: false)
    }

    public func load() -> [CalibrationRecord] {
        guard let data = try? Data(contentsOf: fileURL),
              let records = try? JSONDecoder().decode([CalibrationRecord].self, from: data)
        else { return [] }
        return records.sorted { $0.createdAt > $1.createdAt }
    }

    @discardableResult
    public func save(_ record: CalibrationRecord) throws -> [CalibrationRecord] {
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        var records = load().filter { $0.id != record.id }
        records.append(record)
        records.sort { $0.createdAt > $1.createdAt }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(records).write(to: fileURL, options: .atomic)
        return records
    }

    @discardableResult
    public func delete(id: String) throws -> [CalibrationRecord] {
        let records = load().filter { $0.id != id }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        try encoder.encode(records).write(to: fileURL, options: .atomic)
        return records
    }

    public func record(id: String) -> CalibrationRecord? {
        load().first { $0.id == id }
    }
}

public extension ProjectDocument {
    /// 应用标定候选：更新生成几何（半径；DVD 三次项；BD 扇区/极性），保留其他字段。
    /// 与 Windows `CalibrationViewModel.ApplyParameters` 的字段范围一致。
    func applyingCalibration(_ candidate: CalibrationCandidate) -> ProjectDocument {
        var updated = self
        updated.geometry.innerRadiusMm = candidate.innerRadiusMm
        updated.geometry.outerRadiusMm = candidate.outerRadiusMm
        switch discFamily {
        case .dvd:
            updated.geometry.pitchLinear = candidate.pitchLinear
            updated.geometry.pitchQuadratic = candidate.pitchQuadratic
            updated.geometry.pitchCubic = candidate.pitchCubic
        case .bd:
            if let sectors = candidate.sectors, sectors > 0 {
                updated.geometry.sectors = sectors
            }
            if let invert = candidate.invertMapping {
                updated.bluRay = ProjectBluRaySettings(invertMapping: invert)
            }
        case .cd:
            break
        }
        return updated
    }
}
