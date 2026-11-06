import Foundation

public enum AppearanceMode: String, Codable, Sendable, CaseIterable {
    case system
    case light
    case dark

    public var displayName: String {
        switch self {
        case .system: return "跟随系统"
        case .light: return "浅色"
        case .dark: return "深色"
        }
    }
}

public struct AppSettings: Codable, Sendable, Equatable {
    public static let currentSchemaVersion = 1

    public var schemaVersion: Int
    public var customCliPath: String?
    public var devProjectPath: String?
    public var defaultOutputDirectory: String?
    public var appearance: AppearanceMode
    public var lastCommandName: String?
    public var showsAdvancedOptions: Bool

    public init(
        schemaVersion: Int = AppSettings.currentSchemaVersion,
        customCliPath: String? = nil,
        devProjectPath: String? = nil,
        defaultOutputDirectory: String? = nil,
        appearance: AppearanceMode = .system,
        lastCommandName: String? = nil,
        showsAdvancedOptions: Bool = true
    ) {
        self.schemaVersion = schemaVersion
        self.customCliPath = customCliPath
        self.devProjectPath = devProjectPath
        self.defaultOutputDirectory = defaultOutputDirectory
        self.appearance = appearance
        self.lastCommandName = lastCommandName
        self.showsAdvancedOptions = showsAdvancedOptions
    }

    /// 手改坏的文件也应能读出来：缺失字段回落默认值，未知枚举回落跟随系统。
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = AppSettings()
        schemaVersion = try container.decodeIfPresent(Int.self, forKey: .schemaVersion)
            ?? defaults.schemaVersion
        customCliPath = try container.decodeIfPresent(String.self, forKey: .customCliPath)
        devProjectPath = try container.decodeIfPresent(String.self, forKey: .devProjectPath)
        defaultOutputDirectory = try container.decodeIfPresent(String.self, forKey: .defaultOutputDirectory)
        appearance = (try? container.decodeIfPresent(AppearanceMode.self, forKey: .appearance))
            .flatMap { $0 } ?? defaults.appearance
        lastCommandName = try container.decodeIfPresent(String.self, forKey: .lastCommandName)
        showsAdvancedOptions = try container.decodeIfPresent(Bool.self, forKey: .showsAdvancedOptions)
            ?? defaults.showsAdvancedOptions
    }

    /// 去掉首尾空白，丢弃已不存在的路径；未知外观回落跟随系统。
    public func normalized() -> AppSettings {
        var settings = self
        settings.schemaVersion = AppSettings.currentSchemaVersion
        settings.customCliPath = normalizedExistingPath(customCliPath)
        settings.devProjectPath = normalizedExistingDirectory(devProjectPath)
        settings.defaultOutputDirectory = normalizedExistingDirectory(defaultOutputDirectory)
        settings.lastCommandName = settings.lastCommandName?.trimmingCharacters(in: .whitespacesAndNewlines)
        if settings.lastCommandName?.isEmpty == true {
            settings.lastCommandName = nil
        }
        return settings
    }

    private func normalizedExistingPath(_ raw: String?) -> String? {
        guard let raw else { return nil }
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        let expanded = (trimmed as NSString).expandingTildeInPath
        guard FileManager.default.fileExists(atPath: expanded) else { return nil }
        return expanded
    }

    private func normalizedExistingDirectory(_ raw: String?) -> String? {
        guard let path = normalizedExistingPath(raw) else { return nil }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory),
              isDirectory.boolValue else { return nil }
        return path
    }
}

public struct AppSettingsStore: Sendable {
    public static let directoryName = "DiscImageStudioMac"

    public let fileURL: URL

    public init(directory: URL? = nil) {
        let base = directory ?? AppSettingsStore.defaultDirectory
        self.fileURL = base.appendingPathComponent("settings.json", isDirectory: false)
    }

    public static var defaultDirectory: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Library/Application Support", isDirectory: true)
        return support.appendingPathComponent(directoryName, isDirectory: true)
    }

    /// 文件缺失、损坏或 schema 高于当前版本时回落默认值，不抛错。
    public func load() -> AppSettings {
        guard let data = try? Data(contentsOf: fileURL),
              let decoded = try? JSONDecoder().decode(AppSettings.self, from: data)
        else {
            return AppSettings()
        }
        guard decoded.schemaVersion <= AppSettings.currentSchemaVersion else {
            return AppSettings()
        }
        return decoded.normalized()
    }

    public func save(_ settings: AppSettings) throws {
        let directory = fileURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(settings.normalized())
        try data.write(to: fileURL, options: .atomic)
    }
}
