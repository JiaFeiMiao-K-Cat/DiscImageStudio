import Foundation

/// 盘片预设的三类定义，字段与 Core 的 `DiscPresetJsonStore` schema 4 一致。
public struct CdDiscPreset: Codable, Sendable, Equatable, Identifiable {
    public let id: String
    public let displayName: String
    public let description: String
    public let sectors: Int64
    public let innerRadiusMm: Double
    public let outerRadiusMm: Double
    public let linearVelocityMmPerSecond: Double?
}

public struct DvdDiscPreset: Codable, Sendable, Equatable, Identifiable {
    public let id: String
    public let displayName: String
    public let description: String
    public let totalSectors: UInt32
    public let innerRadiusMm: Double
    public let outerRadiusMm: Double
    public let channelBitLengthNm: Double?
    public let pitchLinear: Double?
    public let pitchQuadratic: Double?
    public let pitchCubic: Double?
}

public struct BluRayDiscPreset: Codable, Sendable, Equatable, Identifiable {
    public let id: String
    public let displayName: String
    public let description: String
    public let totalSectors: UInt32
    public let innerRadiusMm: Double
    public let outerRadiusMm: Double
    public let invertMapping: Bool?
}

public struct DiscPresetJsonDocument: Codable, Sendable, Equatable {
    public let schemaVersion: Int
    public let cdPresets: [CdDiscPreset]
    public let dvdPresets: [DvdDiscPreset]
    public let bluRayPresets: [BluRayDiscPreset]?
    public let disabledCdPresetIds: [String]?
    public let disabledDvdPresetIds: [String]?
    public let disabledBluRayPresetIds: [String]?
}

/// 界面用的统一预设投影。
public struct DiscPreset: Sendable, Equatable, Identifiable {
    public let id: String
    public let displayName: String
    public let description: String
    public let family: DiscFamily
    public let geometry: ProjectGeometry
    public let invertMapping: Bool

    public init(
        id: String,
        displayName: String,
        description: String,
        family: DiscFamily,
        geometry: ProjectGeometry,
        invertMapping: Bool = false
    ) {
        self.id = id
        self.displayName = displayName
        self.description = description
        self.family = family
        self.geometry = geometry
        self.invertMapping = invertMapping
    }
}

public enum PresetCatalogError: Error, Sendable, LocalizedError, Equatable {
    case malformed(String)
    case unsupportedSchema(Int)

    public var errorDescription: String? {
        switch self {
        case .malformed(let reason):
            return "无法解析盘片预设：\(reason)"
        case .unsupportedSchema(let version):
            return "盘片预设 schemaVersion=\(version) 不受支持。"
        }
    }
}

public struct PresetCatalog: Sendable, Equatable {
    public static let supportedSchemaVersion = 4

    public let document: DiscPresetJsonDocument

    public init(document: DiscPresetJsonDocument) {
        self.document = document
    }

    public static func decode(_ data: Data) throws -> PresetCatalog {
        let document: DiscPresetJsonDocument
        do {
            document = try JSONDecoder().decode(DiscPresetJsonDocument.self, from: data)
        } catch {
            throw PresetCatalogError.malformed(error.localizedDescription)
        }
        guard document.schemaVersion >= 2, document.schemaVersion <= Self.supportedSchemaVersion else {
            throw PresetCatalogError.unsupportedSchema(document.schemaVersion)
        }
        return PresetCatalog(document: document)
    }

    public func presets(for family: DiscFamily) -> [DiscPreset] {
        switch family {
        case .cd:
            let disabled = Set(document.disabledCdPresetIds ?? [])
            return document.cdPresets
                .filter { !disabled.contains($0.id) }
                .map { preset in
                    DiscPreset(
                        id: preset.id,
                        displayName: preset.displayName,
                        description: preset.description,
                        family: .cd,
                        geometry: ProjectGeometry(
                            innerRadiusMm: preset.innerRadiusMm,
                            outerRadiusMm: preset.outerRadiusMm,
                            sectors: preset.sectors
                        )
                    )
                }
        case .dvd:
            let disabled = Set(document.disabledDvdPresetIds ?? [])
            return document.dvdPresets
                .filter { !disabled.contains($0.id) }
                .map { preset in
                    DiscPreset(
                        id: preset.id,
                        displayName: preset.displayName,
                        description: preset.description,
                        family: .dvd,
                        geometry: ProjectGeometry(
                            innerRadiusMm: preset.innerRadiusMm,
                            outerRadiusMm: preset.outerRadiusMm,
                            sectors: Int64(preset.totalSectors),
                            pitchLinear: preset.pitchLinear ?? 0,
                            pitchQuadratic: preset.pitchQuadratic ?? 0,
                            pitchCubic: preset.pitchCubic ?? 0
                        )
                    )
                }
        case .bd:
            let disabled = Set(document.disabledBluRayPresetIds ?? [])
            return (document.bluRayPresets ?? [])
                .filter { !disabled.contains($0.id) }
                .map { preset in
                    DiscPreset(
                        id: preset.id,
                        displayName: preset.displayName,
                        description: preset.description,
                        family: .bd,
                        geometry: ProjectGeometry(
                            innerRadiusMm: preset.innerRadiusMm,
                            outerRadiusMm: preset.outerRadiusMm,
                            sectors: Int64(preset.totalSectors)
                        ),
                        invertMapping: preset.invertMapping ?? false
                    )
                }
        }
    }

    public var allPresets: [DiscPreset] {
        DiscFamily.allCases.flatMap { presets(for: $0) }
    }

    public func preset(id: String) -> DiscPreset? {
        allPresets.first { $0.id == id }
    }
}

/// 通过 CLI 的 `presets --json` 读取内置 + 用户预设（合并与禁用规则在 C# 侧维护）。
public struct PresetLoader: Sendable {
    public let cliInvocation: CliInvocation

    public init(cliInvocation: CliInvocation) {
        self.cliInvocation = cliInvocation
    }

    public func load() async throws -> PresetCatalog {
        let runner = CliRunner(invocation: cliInvocation)
        let output: CliRunOutput
        do {
            output = try await runner.runAndCollect(arguments: ["presets", "--json"])
        } catch {
            throw PresetCatalogError.malformed(error.localizedDescription)
        }
        guard output.result.succeeded else {
            let detail = output.standardError.trimmingCharacters(in: .whitespacesAndNewlines)
            throw PresetCatalogError.malformed(detail.isEmpty ? "CLI 退出码 \(output.result.exitCode)" : detail)
        }
        return try PresetCatalog.decode(Data(output.standardOutput.utf8))
    }
}
