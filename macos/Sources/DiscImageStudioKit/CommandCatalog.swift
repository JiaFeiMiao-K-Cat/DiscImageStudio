import Foundation

/// 光盘家族。原始值与 C# `OpticalDiscFamily` 枚举名一致，直接来自 CLI 的 `catalog --json`。
public enum OpticalDiscFamily: String, Codable, Sendable, CaseIterable {
    case compactDisc = "CompactDisc"
    case dvd = "Dvd"
    case bluRay = "BluRay"

    public var shortName: String {
        switch self {
        case .compactDisc: return "CD"
        case .dvd: return "DVD"
        case .bluRay: return "BD"
        }
    }

    public var displayName: String {
        switch self {
        case .compactDisc: return "CD"
        case .dvd: return "DVD"
        case .bluRay: return "蓝光 BD-R"
        }
    }
}

/// 参数值类型。原始值与 C# `DiscOptionValueType` 枚举名一致。
public enum DiscOptionValueType: String, Codable, Sendable, CaseIterable {
    case string = "String"
    case inputFile = "InputFile"
    case outputFile = "OutputFile"
    case inputFolder = "InputFolder"
    case integer = "Integer"
    case decimal = "Decimal"
    case boolean = "Boolean"
    case choice = "Choice"

    public var isFile: Bool {
        switch self {
        case .inputFile, .outputFile, .inputFolder: return true
        default: return false
        }
    }

    public var isInput: Bool {
        switch self {
        case .inputFile, .inputFolder: return true
        default: return false
        }
    }

    public var isOutput: Bool {
        self == .outputFile
    }

    public var isNumeric: Bool {
        switch self {
        case .integer, .decimal: return true
        default: return false
        }
    }
}

public struct DiscOptionDefinition: Codable, Sendable, Equatable, Identifiable {
    public let name: String
    public let displayName: String
    public let valueType: DiscOptionValueType
    public let required: Bool
    public let defaultValue: String?
    public let unit: String?
    public let choices: [String]?

    public init(
        name: String,
        displayName: String,
        valueType: DiscOptionValueType,
        required: Bool = false,
        defaultValue: String? = nil,
        unit: String? = nil,
        choices: [String]? = nil
    ) {
        self.name = name
        self.displayName = displayName
        self.valueType = valueType
        self.required = required
        self.defaultValue = defaultValue
        self.unit = unit
        self.choices = choices
    }

    public var id: String { name }

    /// 单位标签，例如 "mm" / "px"。没有单位时返回 nil。
    public var unitLabel: String? {
        guard let unit, !unit.isEmpty else { return nil }
        return unit
    }
}

public struct DiscCommandDescriptor: Codable, Sendable, Equatable, Identifiable {
    public let name: String
    public let displayName: String
    public let description: String
    public let capabilities: [String]
    public let options: [DiscOptionDefinition]

    public init(
        name: String,
        displayName: String,
        description: String,
        capabilities: [String] = [],
        options: [DiscOptionDefinition] = []
    ) {
        self.name = name
        self.displayName = displayName
        self.description = description
        self.capabilities = capabilities
        self.options = options
    }

    public var id: String { name }

    /// 是否会产生用户可见的镜像/图片文件（用于「快速生成」入口）。
    public var producesArtifact: Bool {
        capabilities.contains("RawTrackGeneration")
            || capabilities.contains("DataImageGeneration")
            || capabilities.contains("GeometryPreview")
            || capabilities.contains("EncodedOutputPreview")
    }
}

public struct DiscModuleDescriptor: Codable, Sendable, Equatable, Identifiable {
    public let id: String
    public let displayName: String
    public let family: OpticalDiscFamily
    public let description: String
    public let supportsCancellation: Bool
    public let commands: [DiscCommandDescriptor]

    public init(
        id: String,
        displayName: String,
        family: OpticalDiscFamily,
        description: String,
        supportsCancellation: Bool,
        commands: [DiscCommandDescriptor]
    ) {
        self.id = id
        self.displayName = displayName
        self.family = family
        self.description = description
        self.supportsCancellation = supportsCancellation
        self.commands = commands
    }

}

/// 一个命令加上它所属的模块，便于在侧栏和详情页之间传递。
public struct CommandEntry: Sendable, Equatable, Identifiable {
    public let module: DiscModuleDescriptor
    public let command: DiscCommandDescriptor

    public init(module: DiscModuleDescriptor, command: DiscCommandDescriptor) {
        self.module = module
        self.command = command
    }

    public var id: String { command.name }
    public var family: OpticalDiscFamily { module.family }
    public var title: String { command.displayName }
}

public struct CommandCatalog: Codable, Sendable, Equatable {
    public static let supportedSchemaVersion = 1

    public let schemaVersion: Int
    public let toolVersion: String?
    public let modules: [DiscModuleDescriptor]

    public init(schemaVersion: Int, toolVersion: String?, modules: [DiscModuleDescriptor]) {
        self.schemaVersion = schemaVersion
        self.toolVersion = toolVersion
        self.modules = modules
    }

    /// 按模块顺序、模块内命令顺序展开。
    public var entries: [CommandEntry] {
        modules.flatMap { module in
            module.commands.map { CommandEntry(module: module, command: $0) }
        }
    }

    /// 命令名在目录内全局唯一，大小写不敏感匹配。
    public func entry(named name: String) -> CommandEntry? {
        entries.first { $0.command.name.caseInsensitiveCompare(name) == .orderedSame }
    }

    public func module(forCommandName name: String) -> DiscModuleDescriptor? {
        entry(named: name)?.module
    }

    /// 每个家族的首选「生成」命令，用于概览页的快捷入口。
    public func primaryCommand(for family: OpticalDiscFamily) -> CommandEntry? {
        let preferred: [String]
        switch family {
        case .compactDisc: preferred = ["cd-generate"]
        case .dvd: preferred = ["solve"]
        case .bluRay: preferred = ["bd-generate"]
        }
        for name in preferred {
            if let entry = entry(named: name) { return entry }
        }
        return entries.first { $0.family == family && $0.command.producesArtifact }
    }
}

extension CommandCatalog {
    /// 从 `catalog --json` 的原始数据解码；附带 schema 校验。
    public static func decode(_ data: Data) throws -> CommandCatalog {
        let catalog: CommandCatalog
        do {
            catalog = try JSONDecoder().decode(CommandCatalog.self, from: data)
        } catch {
            throw CommandCatalogError.malformedJSON(String(describing: error))
        }
        guard catalog.schemaVersion == CommandCatalog.supportedSchemaVersion else {
            throw CommandCatalogError.unsupportedSchema(
                found: catalog.schemaVersion,
                supported: CommandCatalog.supportedSchemaVersion
            )
        }
        return catalog
    }
}

public enum CommandCatalogError: Error, Sendable, LocalizedError, Equatable {
    case malformedJSON(String)
    case unsupportedSchema(found: Int, supported: Int)

    public var errorDescription: String? {
        switch self {
        case .malformedJSON(let detail):
            return "无法解析命令目录 JSON：\(detail)"
        case .unsupportedSchema(let found, let supported):
            return "命令目录 schema 版本 \(found) 不受支持（当前支持 \(supported)），请更新图形界面或 CLI。"
        }
    }
}
