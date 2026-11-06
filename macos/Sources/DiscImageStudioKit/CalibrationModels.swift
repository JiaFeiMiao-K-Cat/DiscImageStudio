import CoreGraphics
import Foundation

/// 标定流程使用的毫米坐标点（+x 右、+y 下，原点为盘心）。
public struct CalibrationPoint: Codable, Sendable, Equatable {
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }

    public init(_ point: CGPoint) {
        self.init(x: Double(point.x), y: Double(point.y))
    }

    public var cgPoint: CGPoint {
        CGPoint(x: x, y: y)
    }
}

public enum CalibrationDiscSize: Int, Codable, Sendable, CaseIterable {
    case standard = 120
    case mini = 80

    public var physicalOuterRadiusMm: Double {
        self == .standard ? 60 : 40
    }

    public var displayName: String {
        self == .standard ? "标准 12 cm 光盘" : "8 cm 小盘"
    }
}

public enum CalibrationDensity {
    /// CD 为线速度 mm/s；DVD 为通道位长 nm；BD 固定 74.5 nm。
    public static func `default`(for family: DiscFamily) -> Double {
        switch family {
        case .cd: return 1200
        case .dvd: return 133.3
        case .bd: return 74.5
        }
    }
}

public struct CalibrationTargetRequest: Sendable, Equatable {
    public var family: DiscFamily
    public var geometry: ProjectGeometry
    public var density: Double
    public var invertMapping: Bool
    public var drawingStart: Int64
    public var drawingEnd: Int64
    public var size: Int
    public var outputURL: URL
    public var sessionURL: URL

    public init(
        family: DiscFamily,
        geometry: ProjectGeometry,
        density: Double,
        invertMapping: Bool = false,
        drawingStart: Int64 = 0,
        drawingEnd: Int64 = 0,
        size: Int = 2400,
        outputURL: URL,
        sessionURL: URL
    ) {
        self.family = family
        self.geometry = geometry
        self.density = density
        self.invertMapping = invertMapping
        self.drawingStart = drawingStart
        self.drawingEnd = drawingEnd
        self.size = size
        self.outputURL = outputURL
        self.sessionURL = sessionURL
    }
}

public struct CalibrationTargetResult: Sendable, Equatable {
    public let imageURL: URL
    public let sessionURL: URL
    public let size: Int
    public let family: DiscFamily
}

public struct CalibrationPhotoResult: Codable, Sendable, Equatable {
    public let succeeded: Bool
    public let message: String
    public let photoWidth: Int
    public let photoHeight: Int
    public let rectifiedSize: Int?
    public let canvasOuterRadiusMm: Double?
    public let discSize: Int?
    public let outerRmsPixels: Double?
    public let holeRmsPixels: Double?
    public let transformDiscToSource: [Double]?
    public let resizedPhotoPath: String?
    public let rectifiedImagePath: String?

    private enum CodingKeys: String, CodingKey {
        case succeeded, message, photoWidth, photoHeight, rectifiedSize, canvasOuterRadiusMm
        case discSize, outerRmsPixels, holeRmsPixels, transformDiscToSource
        case resizedPhotoPath, rectifiedImagePath
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        succeeded = try container.decodeIfPresent(Bool.self, forKey: .succeeded) ?? false
        message = try container.decodeIfPresent(String.self, forKey: .message) ?? ""
        photoWidth = try container.decodeIfPresent(Int.self, forKey: .photoWidth) ?? 0
        photoHeight = try container.decodeIfPresent(Int.self, forKey: .photoHeight) ?? 0
        rectifiedSize = try container.decodeIfPresent(Int.self, forKey: .rectifiedSize)
        canvasOuterRadiusMm = try container.decodeIfPresent(Double.self, forKey: .canvasOuterRadiusMm)
        discSize = try container.decodeIfPresent(Int.self, forKey: .discSize)
        outerRmsPixels = try container.decodeIfPresent(Double.self, forKey: .outerRmsPixels)
        holeRmsPixels = try container.decodeIfPresent(Double.self, forKey: .holeRmsPixels)
        transformDiscToSource = try container.decodeIfPresent([Double].self, forKey: .transformDiscToSource)
        resizedPhotoPath = try container.decodeIfPresent(String.self, forKey: .resizedPhotoPath)
        rectifiedImagePath = try container.decodeIfPresent(String.self, forKey: .rectifiedImagePath)
    }
}

public struct CalibrationCandidate: Codable, Sendable, Equatable, Identifiable {
    public let innerRadiusMm: Double
    public let outerRadiusMm: Double
    public let pitchLinear: Double
    public let pitchQuadratic: Double
    public let pitchCubic: Double
    public let rotationRadians: Double
    public let rmsErrorMm: Double
    public let maxErrorMm: Double
    public let outlierCount: Int
    public let predicted: [CalibrationPoint]
    public let sectors: Int64?
    public let invertMapping: Bool?
    public var id: String { "\(innerRadiusMm)-\(outerRadiusMm)-\(rmsErrorMm)" }

    private enum CodingKeys: String, CodingKey {
        case innerRadiusMm, outerRadiusMm, pitchLinear, pitchQuadratic, pitchCubic
        case rotationRadians, rmsErrorMm, maxErrorMm, outlierCount, predicted
        case sectors, invertMapping
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        innerRadiusMm = try container.decode(Double.self, forKey: .innerRadiusMm)
        outerRadiusMm = try container.decode(Double.self, forKey: .outerRadiusMm)
        pitchLinear = try container.decodeIfPresent(Double.self, forKey: .pitchLinear) ?? 0
        pitchQuadratic = try container.decodeIfPresent(Double.self, forKey: .pitchQuadratic) ?? 0
        pitchCubic = try container.decodeIfPresent(Double.self, forKey: .pitchCubic) ?? 0
        rotationRadians = try container.decodeIfPresent(Double.self, forKey: .rotationRadians) ?? 0
        rmsErrorMm = try container.decodeIfPresent(Double.self, forKey: .rmsErrorMm) ?? .infinity
        maxErrorMm = try container.decodeIfPresent(Double.self, forKey: .maxErrorMm) ?? .infinity
        outlierCount = try container.decodeIfPresent(Int.self, forKey: .outlierCount) ?? 0
        predicted = try container.decodeIfPresent([CalibrationPoint].self, forKey: .predicted) ?? []
        sectors = try container.decodeIfPresent(Int64.self, forKey: .sectors)
        invertMapping = try container.decodeIfPresent(Bool.self, forKey: .invertMapping)
    }

    public init(
        innerRadiusMm: Double,
        outerRadiusMm: Double,
        pitchLinear: Double = 0,
        pitchQuadratic: Double = 0,
        pitchCubic: Double = 0,
        rotationRadians: Double = 0,
        rmsErrorMm: Double = 0,
        maxErrorMm: Double = 0,
        outlierCount: Int = 0,
        predicted: [CalibrationPoint] = [],
        sectors: Int64? = nil,
        invertMapping: Bool? = nil
    ) {
        self.innerRadiusMm = innerRadiusMm
        self.outerRadiusMm = outerRadiusMm
        self.pitchLinear = pitchLinear
        self.pitchQuadratic = pitchQuadratic
        self.pitchCubic = pitchCubic
        self.rotationRadians = rotationRadians
        self.rmsErrorMm = rmsErrorMm
        self.maxErrorMm = maxErrorMm
        self.outlierCount = outlierCount
        self.predicted = predicted
        self.sectors = sectors
        self.invertMapping = invertMapping
    }
}

public struct CalibrationFitResult: Codable, Sendable, Equatable {
    public let succeeded: Bool
    public let reliable: Bool
    public let boundaryLimited: Bool
    public let warnings: [String]
    public let bestIndex: Int
    public let candidates: [CalibrationCandidate]

    private enum CodingKeys: String, CodingKey {
        case succeeded, reliable, boundaryLimited, warnings, bestIndex, candidates
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        succeeded = try container.decodeIfPresent(Bool.self, forKey: .succeeded) ?? false
        reliable = try container.decodeIfPresent(Bool.self, forKey: .reliable) ?? false
        boundaryLimited = try container.decodeIfPresent(Bool.self, forKey: .boundaryLimited) ?? false
        warnings = try container.decodeIfPresent([String].self, forKey: .warnings) ?? []
        bestIndex = try container.decodeIfPresent(Int.self, forKey: .bestIndex) ?? -1
        candidates = try container.decodeIfPresent([CalibrationCandidate].self, forKey: .candidates) ?? []
    }

    public init(
        succeeded: Bool,
        reliable: Bool,
        boundaryLimited: Bool,
        warnings: [String],
        bestIndex: Int,
        candidates: [CalibrationCandidate]
    ) {
        self.succeeded = succeeded
        self.reliable = reliable
        self.boundaryLimited = boundaryLimited
        self.warnings = warnings
        self.bestIndex = bestIndex
        self.candidates = candidates
    }

    /// 与 Windows 一致的「可应用」判定：平均偏差 ≤0.35 mm，最大偏差 ≤0.8 mm。
    public static let applicableRmsMm = 0.35
    public static let applicableMaxMm = 0.8

    public func isApplicable(_ candidate: CalibrationCandidate) -> Bool {
        succeeded
            && candidate.rmsErrorMm <= CalibrationFitResult.applicableRmsMm
            && candidate.maxErrorMm <= CalibrationFitResult.applicableMaxMm
    }
}

public struct CalibrationCoarseResult: Codable, Sendable, Equatable {
    public let succeeded: Bool
    public let warnings: [String]
    public let bestIndex: Int
    public let candidates: [CalibrationCandidate]

    private enum CodingKeys: String, CodingKey {
        case succeeded, warnings, bestIndex, candidates
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        succeeded = try container.decodeIfPresent(Bool.self, forKey: .succeeded) ?? false
        warnings = try container.decodeIfPresent([String].self, forKey: .warnings) ?? []
        bestIndex = try container.decodeIfPresent(Int.self, forKey: .bestIndex) ?? -1
        candidates = try container.decodeIfPresent([CalibrationCandidate].self, forKey: .candidates) ?? []
    }

    public init(succeeded: Bool, warnings: [String], bestIndex: Int, candidates: [CalibrationCandidate]) {
        self.succeeded = succeeded
        self.warnings = warnings
        self.bestIndex = bestIndex
        self.candidates = candidates
    }

    /// 粗调只要求拟合成功且没有歧义警告。
    public var isApplicable: Bool {
        succeeded && warnings.isEmpty && bestIndex >= 0 && bestIndex < candidates.count
    }
}

public struct CalibrationSessionSummary: Codable, Sendable, Equatable {
    public let family: String?
    public let innerRadiusMm: Double?
    public let outerRadiusMm: Double?
    public let sectors: Int64?
    public let density: Double?
    public let pitchLinear: Double?
    public let pitchQuadratic: Double?
    public let pitchCubic: Double?
    public let invertMapping: Bool?
    public let hasPhoto: Bool?
    public let rectificationConfirmed: Bool?
    public let tracePointCount: Int?
    public let photoWidth: Int?
    public let photoHeight: Int?
    public let drawingStartSector: Int64?
    public let drawingEndSector: Int64?
}

public struct CalibrationCoarseTargetResult: Sendable, Equatable {
    public let imageURL: URL
    public let p0URL: URL
    public let size: Int
}

public enum CalibrationError: Error, Sendable, LocalizedError, Equatable {
    case invalidGeometry(String)
    case commandFailed(String)
    case malformedResult(String)
    case missingInput(String)

    public var errorDescription: String? {
        switch self {
        case .invalidGeometry(let reason):
            return "标定参数无效：\(reason)"
        case .commandFailed(let reason):
            return "标定命令失败：\(reason)"
        case .malformedResult(let reason):
            return "无法解析标定结果：\(reason)"
        case .missingInput(let reason):
            return "缺少标定输入：\(reason)"
        }
    }
}

/// 矫正图（正方形、中心即盘心）与毫米坐标之间的映射。
public struct CalibrationRectifiedGeometry: Sendable, Equatable {
    public let size: Int
    public let canvasOuterRadiusMm: Double

    public init(size: Int, canvasOuterRadiusMm: Double) {
        self.size = size
        self.canvasOuterRadiusMm = canvasOuterRadiusMm
    }

    public var millimetersPerPixel: Double {
        guard size > 0 else { return 0 }
        return (2 * canvasOuterRadiusMm) / Double(size)
    }

    /// 毫米坐标 → 矫正图像素坐标（+x 右、+y 下）。
    public func pixel(fromMM point: CalibrationPoint) -> CGPoint {
        let center = Double(size) / 2
        return CGPoint(
            x: center + point.x / millimetersPerPixel,
            y: center + point.y / millimetersPerPixel
        )
    }

    /// 矫正图像素坐标 → 毫米坐标。
    public func millimeters(fromPixel point: CGPoint) -> CalibrationPoint {
        let center = Double(size) / 2
        return CalibrationPoint(
            x: (Double(point.x) - center) * millimetersPerPixel,
            y: (Double(point.y) - center) * millimetersPerPixel
        )
    }
}

public enum CalibrationContours {
    /// 与 Windows `CalibrationPhotoService.SuggestContours` 相同的初始轮廓：
    /// 以照片中心为圆心、外缘 0.43、中心孔 0.125 的椭圆上均布点。
    public static func suggest(
        photoWidth: Int,
        photoHeight: Int,
        count: Int = 8
    ) -> (outer: [CalibrationPoint], hole: [CalibrationPoint]) {
        let safeCount = min(max(count, 5), 64)
        let centerX = Double(max(photoWidth - 1, 0)) / 2
        let centerY = Double(max(photoHeight - 1, 0)) / 2
        let radiusX = Double(photoWidth) * 0.43
        let radiusY = Double(photoHeight) * 0.43

        func make(_ ratio: Double) -> [CalibrationPoint] {
            (0..<safeCount).map { index in
                let angle = -Double.pi / 2 + 2 * Double.pi * Double(index) / Double(safeCount)
                return CalibrationPoint(
                    x: centerX + radiusX * ratio * cos(angle),
                    y: centerY + radiusY * ratio * sin(angle)
                )
            }
        }

        return (make(1), make(0.125))
    }
}

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
