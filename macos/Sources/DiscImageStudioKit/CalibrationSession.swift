import Foundation

/// `.calibration.json`（schema 6）里 GUI 需要的部分。
/// 只解码 `target.parameters` 与 `photo`，其余字段（控制块、候选等）保持不解析。
public struct CalibrationSessionDocument: Codable, Sendable, Equatable {
    public struct TargetInfo: Codable, Sendable, Equatable {
        public let id: String?
        public let parameters: Parameters
    }

    public struct Parameters: Codable, Sendable, Equatable {
        public let kind: String
        public let innerRadiusMm: Double
        public let outerRadiusMm: Double
        public let sectors: Int64
        public let linearDensity: Double
        public let pitchLinear: Double
        public let pitchQuadratic: Double
        public let pitchCubic: Double
        public let bluRayInvertMapping: Bool?
        public let drawingStartSector: Int64?
        public let drawingEndSectorExclusive: Int64?

        private enum CodingKeys: String, CodingKey {
            case kind, innerRadiusMm, outerRadiusMm, sectors, linearDensity
            case pitchLinear, pitchQuadratic, pitchCubic
            case bluRayInvertMapping, drawingStartSector, drawingEndSectorExclusive
        }

        public init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            kind = try container.decode(String.self, forKey: .kind)
            innerRadiusMm = try container.decode(Double.self, forKey: .innerRadiusMm)
            outerRadiusMm = try container.decode(Double.self, forKey: .outerRadiusMm)
            sectors = try container.decode(Int64.self, forKey: .sectors)
            linearDensity = try container.decodeIfPresent(Double.self, forKey: .linearDensity) ?? 0
            pitchLinear = try container.decodeIfPresent(Double.self, forKey: .pitchLinear) ?? 0
            pitchQuadratic = try container.decodeIfPresent(Double.self, forKey: .pitchQuadratic) ?? 0
            pitchCubic = try container.decodeIfPresent(Double.self, forKey: .pitchCubic) ?? 0
            bluRayInvertMapping = try container.decodeIfPresent(Bool.self, forKey: .bluRayInvertMapping)
            drawingStartSector = try container.decodeIfPresent(Int64.self, forKey: .drawingStartSector)
            drawingEndSectorExclusive = try container.decodeIfPresent(Int64.self, forKey: .drawingEndSectorExclusive)
        }

        public var family: DiscFamily {
            switch kind {
            case "Dvd": return .dvd
            case "BluRay": return .bd
            default: return .cd
            }
        }

        public var projectGeometry: ProjectGeometry {
            ProjectGeometry(
                innerRadiusMm: innerRadiusMm,
                outerRadiusMm: outerRadiusMm,
                sectors: sectors,
                pitchLinear: pitchLinear,
                pitchQuadratic: pitchQuadratic,
                pitchCubic: pitchCubic
            )
        }

        public var density: Double { linearDensity }
        public var invertMapping: Bool { bluRayInvertMapping ?? false }
        public var drawingStart: Int64 { drawingStartSector ?? 0 }
        public var drawingEnd: Int64 { drawingEndSectorExclusive ?? 0 }
    }

    public struct Trace: Codable, Sendable, Equatable {
        public let armIndex: Int
        public let points: [CalibrationPoint]
    }

    public struct PhotoInfo: Codable, Sendable, Equatable {
        public let imagePngBase64: String
        public let outerBoundary: [CalibrationPoint]
        public let holeBoundary: [CalibrationPoint]
        public let physicalOuterRadiusMm: Double
        public let rectificationConfirmed: Bool
        public let traces: [Trace]

        public var discSize: CalibrationDiscSize {
            physicalOuterRadiusMm <= 45 ? .mini : .standard
        }

        public var polygonPoints: [CalibrationPoint] {
            traces.first?.points ?? []
        }

        public var pngData: Data? {
            Data(base64Encoded: imagePngBase64)
        }
    }

    public let target: TargetInfo
    public let photo: PhotoInfo?
}

/// 扫描工程 `calibration/` 目录下的 `.calibration.json`，作为标定记录列表的唯一来源。
public struct CalibrationSessionCatalog: Sendable {
    public struct Entry: Sendable, Equatable, Identifiable {
        public let path: URL
        public let modifiedAt: Date
        public let document: CalibrationSessionDocument
        public let summary: CalibrationSessionSummary?

        public var id: String { path.path }
        public var family: DiscFamily { document.target.parameters.family }
        public var geometry: ProjectGeometry { document.target.parameters.projectGeometry }
        public var density: Double { document.target.parameters.density }
        public var isConfirmed: Bool { document.photo?.rectificationConfirmed ?? false }
        public var tracePointCount: Int { document.photo?.polygonPoints.count ?? 0 }
        public var targetID: String? { document.target.id }
    }

    public let workspace: URL
    private let cliInvocation: CliInvocation?

    public init(workspace: URL, cliInvocation: CliInvocation? = nil) {
        self.workspace = workspace
        self.cliInvocation = cliInvocation
    }

    public func entries() -> [Entry] {
        let urls = (try? FileManager.default.contentsOfDirectory(
            at: workspace,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]
        )) ?? []
        return urls
            .filter { $0.pathExtension == "json" && $0.lastPathComponent.hasSuffix(".calibration.json") }
            .compactMap { url in
                guard let attributes = try? url.resourceValues(forKeys: [.contentModificationDateKey]),
                      let data = try? Data(contentsOf: url),
                      let document = try? JSONDecoder().decode(CalibrationSessionDocument.self, from: data)
                else { return nil }
                return Entry(
                    path: url,
                    modifiedAt: attributes.contentModificationDate ?? .distantPast,
                    document: document,
                    summary: nil
                )
            }
            .sorted { $0.modifiedAt > $1.modifiedAt }
    }

    /// 读取一个会话的完整内容；照片 PNG 由调用方决定写到哪里。
    public func loadSession(at url: URL) throws -> CalibrationSessionDocument {
        let data = try Data(contentsOf: url)
        do {
            return try JSONDecoder().decode(CalibrationSessionDocument.self, from: data)
        } catch {
            throw CalibrationError.malformedResult("无法解析标定会话：\(error.localizedDescription)")
        }
    }

    /// 删除会话及其同名靶标/照片/矫正图；只删除本目录下由命名约定生成的文件。
    public func delete(_ entry: Entry) throws {
        let id = CalibrationWorkspaceLayout.id(fromSessionURL: entry.path)
        let layout = CalibrationWorkspaceLayout(workspace: workspace)
        let files = [
            layout.sessionURL(id: id),
            layout.targetImageURL(id: id),
            layout.photoURL(id: id),
            layout.rectifiedURL(id: id),
            layout.coarseImageURL(id: id),
            layout.coarseP0URL(id: id),
        ]
        for url in files {
            try? FileManager.default.removeItem(at: url)
        }
    }

    /// 把会话复制到指定位置（导出）。
    public func export(_ entry: Entry, to destination: URL) throws {
        try FileManager.default.createDirectory(
            at: destination.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        if FileManager.default.fileExists(atPath: destination.path) {
            try FileManager.default.removeItem(at: destination)
        }
        try FileManager.default.copyItem(at: entry.path, to: destination)
    }
}

public extension CalibrationSessionDocument.PhotoInfo {
    /// 把内嵌的缩小照片写到磁盘，用于重新矫正或展示。
    func writePhoto(to url: URL) throws {
        guard let data = pngData else {
            throw CalibrationError.malformedResult("标定会话中的照片数据无效。")
        }
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try data.write(to: url, options: .atomic)
    }
}

/// 工程 `calibration/` 目录的命名约定：一次标定 = 一个 `<id>.calibration.json`，
/// 靶标/照片/矫正图/粗调图案都以同一个 `<id>` 命名，便于目录扫描与删除。
public struct CalibrationWorkspaceLayout: Sendable {
    public let workspace: URL

    public init(workspace: URL) {
        self.workspace = workspace
    }

    public func sessionURL(id: String) -> URL {
        workspace.appendingPathComponent("\(id).calibration.json", isDirectory: false)
    }

    public func targetImageURL(id: String) -> URL {
        workspace.appendingPathComponent("target-\(id).png", isDirectory: false)
    }

    public func photoURL(id: String) -> URL {
        workspace.appendingPathComponent("photo-\(id).png", isDirectory: false)
    }

    public func rectifiedURL(id: String) -> URL {
        workspace.appendingPathComponent("rectified-\(id).png", isDirectory: false)
    }

    public func coarseImageURL(id: String) -> URL {
        workspace.appendingPathComponent("coarse-\(id).png", isDirectory: false)
    }

    public func coarseP0URL(id: String) -> URL {
        workspace.appendingPathComponent("coarse-\(id)-p0.json", isDirectory: false)
    }

    /// 会话文件名（不含 `.calibration.json` 后缀）。
    public static func id(fromSessionURL url: URL) -> String {
        let name = url.lastPathComponent
        let suffix = ".calibration.json"
        guard name.hasSuffix(suffix) else { return url.deletingPathExtension().lastPathComponent }
        return String(name.dropLast(suffix.count))
    }
}

public extension CalibrationSessionCatalog {
    func layout() -> CalibrationWorkspaceLayout {
        CalibrationWorkspaceLayout(workspace: workspace)
    }
}
