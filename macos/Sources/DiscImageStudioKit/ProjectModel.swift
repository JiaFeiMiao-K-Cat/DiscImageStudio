import Foundation

/// 光盘家族。原始值与 `.discproj` 的 `discFamily` 字段一致（"cd"/"dvd"/"bd"）。
public enum DiscFamily: String, Codable, Sendable, CaseIterable {
    case cd
    case dvd
    case bd

    public var displayName: String {
        switch self {
        case .cd: return "CD"
        case .dvd: return "DVD"
        case .bd: return "蓝光 BD-R"
        }
    }

    /// 与 Windows 版工程文件一致的短标签。
    public var familyLabel: String {
        switch self {
        case .cd: return "CD"
        case .dvd: return "DVD"
        case .bd: return "BD"
        }
    }

    /// CD 的设计画布外半径固定 57.5 mm；DVD/BD 取工程生成外半径。
    public var fixedCanvasOuterRadiusMm: Double? {
        self == .cd ? 57.5 : nil
    }
}

public struct ProjectGeometry: Codable, Sendable, Equatable {
    public var innerRadiusMm: Double
    public var outerRadiusMm: Double
    public var sectors: Int64
    public var pitchLinear: Double
    public var pitchQuadratic: Double
    public var pitchCubic: Double

    public init(
        innerRadiusMm: Double,
        outerRadiusMm: Double,
        sectors: Int64,
        pitchLinear: Double = 0,
        pitchQuadratic: Double = 0,
        pitchCubic: Double = 0
    ) {
        self.innerRadiusMm = innerRadiusMm
        self.outerRadiusMm = outerRadiusMm
        self.sectors = sectors
        self.pitchLinear = pitchLinear
        self.pitchQuadratic = pitchQuadratic
        self.pitchCubic = pitchCubic
    }
}

public struct DesignLayer: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var name: String
    public var imagePath: String
    public var visible: Bool
    public var translateXMm: Double
    public var translateYMm: Double
    public var rotationDeg: Double
    public var scale: Double

    public init(
        id: String = UUID().uuidString.lowercased(),
        name: String,
        imagePath: String,
        visible: Bool = true,
        translateXMm: Double = 0,
        translateYMm: Double = 0,
        rotationDeg: Double = 0,
        scale: Double = 1
    ) {
        self.id = id
        self.name = name
        self.imagePath = imagePath
        self.visible = visible
        self.translateXMm = translateXMm
        self.translateYMm = translateYMm
        self.rotationDeg = rotationDeg
        self.scale = scale
    }

    /// 画布属性面板里的百分比投影。
    public var scalePercent: Double {
        get { scale * 100 }
        set { scale = newValue / 100 }
    }
}

public struct ProjectOutputs: Codable, Sendable, Equatable {
    public var outputPath: String
    public var writeCue: Bool
    public var dataDirectory: String
    public var volumeLabel: String
    public var interleave: Bool
    public var filesAtOuterRing: Bool

    public init(
        outputPath: String = "",
        writeCue: Bool = true,
        dataDirectory: String = "",
        volumeLabel: String = "",
        interleave: Bool = true,
        filesAtOuterRing: Bool = false
    ) {
        self.outputPath = outputPath
        self.writeCue = writeCue
        self.dataDirectory = dataDirectory
        self.volumeLabel = volumeLabel
        self.interleave = interleave
        self.filesAtOuterRing = filesAtOuterRing
    }

    private enum CodingKeys: String, CodingKey {
        case outputPath
        case writeCue
        case dataDirectory
        case volumeLabel
        case interleave
        case filesAtOuterRing
    }

    /// C# 侧对 `filesAtOuterRing` 使用 WhenWritingDefault，false 时字段缺失，因此必须容错解码。
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = ProjectOutputs()
        outputPath = try container.decodeIfPresent(String.self, forKey: .outputPath) ?? defaults.outputPath
        writeCue = try container.decodeIfPresent(Bool.self, forKey: .writeCue) ?? defaults.writeCue
        dataDirectory = try container.decodeIfPresent(String.self, forKey: .dataDirectory) ?? defaults.dataDirectory
        volumeLabel = try container.decodeIfPresent(String.self, forKey: .volumeLabel) ?? defaults.volumeLabel
        interleave = try container.decodeIfPresent(Bool.self, forKey: .interleave) ?? defaults.interleave
        filesAtOuterRing = try container.decodeIfPresent(Bool.self, forKey: .filesAtOuterRing) ?? defaults.filesAtOuterRing
    }

    public var hasBundledFiles: Bool {
        !dataDirectory.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

public struct ProjectTone: Codable, Sendable, Equatable {
    public var ditherStrength: Double
    public var levels: Int
    public var brightness: Double
    public var srgbLinearization: Bool

    public init(
        ditherStrength: Double = 0.75,
        levels: Int = 2,
        brightness: Double = 0,
        srgbLinearization: Bool = false
    ) {
        self.ditherStrength = ditherStrength
        self.levels = levels
        self.brightness = brightness
        self.srgbLinearization = srgbLinearization
    }

    /// 旧工程没有 tone 节时的行为：硬阈值、两级。
    public static var legacyBinary: ProjectTone {
        ProjectTone(ditherStrength: 0, levels: 2, brightness: 0, srgbLinearization: false)
    }

    public var ditherPercent: Double {
        get { ditherStrength * 100 }
        set { ditherStrength = min(max(newValue / 100, 0), 1) }
    }

    /// DVD/BD 只有两级；CD 允许 2 或 4 级。
    public func normalized(for family: DiscFamily) -> ProjectTone {
        var tone = self
        if family != .cd {
            tone.levels = 2
        }
        tone.ditherStrength = min(max(tone.ditherStrength, 0), 1)
        tone.brightness = min(max(tone.brightness, -64), 64)
        if tone.levels != 4 { tone.levels = 2 }
        return tone
    }
}

public struct ProjectBluRaySettings: Codable, Sendable, Equatable {
    public var invertMapping: Bool

    public init(invertMapping: Bool = false) {
        self.invertMapping = invertMapping
    }
}

public enum ProjectModelError: Error, Sendable, LocalizedError, Equatable {
    case unsupportedSchema(Int)
    case malformedDocument(String)
    case unreadable(String)
    case invalidGeometry(String)
    case invalidLayer(String)
    case invalidOutputs(String)
    case writeFailed(String)

    public var errorDescription: String? {
        switch self {
        case .unsupportedSchema(let version):
            return "不支持的工程 schemaVersion=\(version)（当前支持 1 与 2）。"
        case .malformedDocument(let reason):
            return "工程文件格式无效：\(reason)"
        case .unreadable(let reason):
            return "无法读取工程文件：\(reason)"
        case .invalidGeometry(let reason):
            return "工程几何参数无效：\(reason)"
        case .invalidLayer(let reason):
            return "图层无效：\(reason)"
        case .invalidOutputs(let reason):
            return "输出配置无效：\(reason)"
        case .writeFailed(let reason):
            return "保存工程失败：\(reason)"
        }
    }
}

/// 与 Windows 版 `ProjectDocument` 完全兼容的 `.discproj`（camelCase，schemaVersion 2）。
public struct ProjectDocument: Codable, Sendable, Equatable {
    public static let currentSchemaVersion = 2
    public static let legacySchemaVersion = 1
    public static let bluRaySectorsPerLayer: Int64 = 12_219_392
    public static let bluRaySectorsPerCluster: Int64 = 32

    public var schemaVersion: Int
    public var name: String
    public var discFamily: DiscFamily
    public var geometry: ProjectGeometry
    public var layers: [DesignLayer]
    public var outputs: ProjectOutputs
    public var calibrationSessionPath: String?
    public var thumbnailPngBase64: String?
    public var createdUtc: String
    public var modifiedUtc: String
    public var tone: ProjectTone?
    public var bluRay: ProjectBluRaySettings?

    public init(
        schemaVersion: Int = ProjectDocument.currentSchemaVersion,
        name: String,
        discFamily: DiscFamily,
        geometry: ProjectGeometry,
        layers: [DesignLayer] = [],
        outputs: ProjectOutputs = ProjectOutputs(),
        calibrationSessionPath: String? = nil,
        thumbnailPngBase64: String? = nil,
        createdUtc: String = ProjectDocument.timestamp(),
        modifiedUtc: String? = nil,
        tone: ProjectTone? = nil,
        bluRay: ProjectBluRaySettings? = nil
    ) {
        self.schemaVersion = schemaVersion
        self.name = name
        self.discFamily = discFamily
        self.geometry = geometry
        self.layers = layers
        self.outputs = outputs
        self.calibrationSessionPath = calibrationSessionPath
        self.thumbnailPngBase64 = thumbnailPngBase64
        self.createdUtc = createdUtc
        self.modifiedUtc = modifiedUtc ?? createdUtc
        self.tone = tone
        self.bluRay = bluRay
    }

    /// ISO 8601，带毫秒与偏移量，与 .NET `DateTimeOffset` 的默认 JSON 形式一致。
    public static func timestamp(_ date: Date = Date()) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }

    public var canvasOuterRadiusMm: Double {
        discFamily.fixedCanvasOuterRadiusMm ?? geometry.outerRadiusMm
    }

    public var toneOrLegacy: ProjectTone {
        (tone ?? .legacyBinary).normalized(for: discFamily)
    }

    public var bluRaySettings: ProjectBluRaySettings {
        bluRay ?? ProjectBluRaySettings()
    }

    public var isBluRay: Bool { discFamily == .bd }

    /// 新建工程的工厂：几何取自预设，影调取照片起点，输出路径由调用方给出。
    public static func make(
        name: String,
        family: DiscFamily,
        preset: DiscPreset,
        outputPath: String
    ) -> ProjectDocument {
        var geometry = preset.geometry
        if family == .bd {
            // BD 工程要求 32 扇区对齐且不超过单层容量。
            geometry.sectors = max(
                ProjectDocument.bluRaySectorsPerCluster,
                (geometry.sectors / ProjectDocument.bluRaySectorsPerCluster) * ProjectDocument.bluRaySectorsPerCluster
            )
        }
        let label: String
        switch family {
        case .cd: label = "CD_IMAGE"
        case .dvd: label = "DISC_IMAGE"
        case .bd: label = "BD_IMAGE"
        }
        return ProjectDocument(
            name: name,
            discFamily: family,
            geometry: geometry,
            layers: [],
            outputs: ProjectOutputs(outputPath: outputPath, volumeLabel: label),
            tone: ProjectTone(),
            bluRay: family == .bd ? ProjectBluRaySettings(invertMapping: preset.invertMapping) : nil
        )
    }

    /// 读入后按当前 schema 归一化：schema 1 视为 CD/DVD；DVD/BD 强制两级影调。
    public mutating func normalizeAfterLoad() {
        schemaVersion = ProjectDocument.currentSchemaVersion
        if discFamily != .cd {
            tone = (tone ?? .legacyBinary).normalized(for: discFamily)
        }
        if discFamily == .bd {
            if bluRay == nil { bluRay = ProjectBluRaySettings() }
        } else {
            bluRay = nil
        }
    }

    public func validate() throws {
        guard schemaVersion == ProjectDocument.currentSchemaVersion
            || schemaVersion == ProjectDocument.legacySchemaVersion
        else {
            throw ProjectModelError.unsupportedSchema(schemaVersion)
        }
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ProjectModelError.invalidGeometry("工程名不能为空。")
        }
        guard geometry.innerRadiusMm.isFinite, geometry.innerRadiusMm > 0,
              geometry.outerRadiusMm.isFinite, geometry.outerRadiusMm > geometry.innerRadiusMm
        else {
            throw ProjectModelError.invalidGeometry("内半径必须为正且小于外半径。")
        }
        guard geometry.sectors > 0 else {
            throw ProjectModelError.invalidGeometry("扇区数必须大于 0。")
        }
        if discFamily == .dvd, geometry.sectors > Int64(UInt32.max) {
            throw ProjectModelError.invalidGeometry("DVD 总扇区数超出引擎支持范围。")
        }
        if discFamily == .bd {
            guard geometry.sectors <= ProjectDocument.bluRaySectorsPerLayer else {
                throw ProjectModelError.invalidGeometry("蓝光总扇区数超出单层容量。")
            }
            guard geometry.sectors % ProjectDocument.bluRaySectorsPerCluster == 0 else {
                throw ProjectModelError.invalidGeometry("蓝光总扇区数必须是 32 的倍数。")
            }
        }
        for layer in layers {
            guard !layer.id.isEmpty, !layer.name.isEmpty else {
                throw ProjectModelError.invalidLayer("图层缺少 id 或名称。")
            }
            guard !layer.imagePath.isEmpty else {
                throw ProjectModelError.invalidLayer("图层「\(layer.name)」没有图片路径。")
            }
            guard layer.scale.isFinite, layer.scale > 0 else {
                throw ProjectModelError.invalidLayer("图层「\(layer.name)」的缩放必须为正数。")
            }
            guard layer.translateXMm.isFinite, layer.translateYMm.isFinite,
                  layer.rotationDeg.isFinite
            else {
                throw ProjectModelError.invalidLayer("图层「\(layer.name)」的变换参数必须是有限数。")
            }
        }
        guard !outputs.outputPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ProjectModelError.invalidOutputs("请选择输出位置。")
        }
    }
}
