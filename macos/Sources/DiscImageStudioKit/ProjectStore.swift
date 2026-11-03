import CoreGraphics
import Foundation
import ImageIO

/// `.discproj` 的读写、缩略图与默认目录。文件格式与 Windows 版 `ProjectJsonStore` 对齐。
public struct ProjectStore: Sendable {
    public let projectsDirectory: URL

    public init(projectsDirectory: URL? = nil) {
        self.projectsDirectory = projectsDirectory ?? ProjectStore.defaultProjectsDirectory
    }

    public static var defaultProjectsDirectory: URL {
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Documents", isDirectory: true)
        return documents.appendingPathComponent("DiscImageStudio Projects", isDirectory: true)
    }

    public static var fileExtension: String { "discproj" }

    public func load(from url: URL) throws -> ProjectDocument {
        let data: Data
        do {
            data = try Data(contentsOf: url)
        } catch {
            throw ProjectModelError.unreadable(error.localizedDescription)
        }

        var document: ProjectDocument
        do {
            document = try JSONDecoder().decode(ProjectDocument.self, from: data)
        } catch let error as DecodingError {
            throw ProjectModelError.malformedDocument(ProjectStore.describe(error))
        } catch {
            throw ProjectModelError.malformedDocument(error.localizedDescription)
        }

        // 先校验原始 schema 版本，再归一化（normalizeAfterLoad 会把版本改写成 2）。
        guard document.schemaVersion == ProjectDocument.currentSchemaVersion
            || document.schemaVersion == ProjectDocument.legacySchemaVersion
        else {
            throw ProjectModelError.unsupportedSchema(document.schemaVersion)
        }
        if document.schemaVersion == ProjectDocument.legacySchemaVersion, document.discFamily == .bd {
            // 旧 schema 没有蓝光；没有任何历史文件会合法地是 BD，按 CD 处理。
            document.discFamily = .cd
        }
        document.normalizeAfterLoad()
        try document.validate()
        return document
    }

    private static func describe(_ error: DecodingError) -> String {
        switch error {
        case .keyNotFound(let key, _):
            return "缺少字段 «\(key.stringValue)»"
        case .typeMismatch(let type, let context):
            return "字段类型不匹配（期望 \(type)）：\(context.codingPath.map(\.stringValue).joined(separator: "."))"
        case .valueNotFound(let type, let context):
            return "字段缺少取值（期望 \(type)）：\(context.codingPath.map(\.stringValue).joined(separator: "."))"
        case .dataCorrupted(let context):
            return "JSON 数据损坏：\(context.debugDescription)"
        @unknown default:
            return "无法解析工程 JSON。"
        }
    }

    /// 校验后以原子写保存。工程时间戳与 schema 版本在此统一更新。
    public func save(_ document: ProjectDocument, to url: URL) throws {
        var clean = document
        clean.schemaVersion = ProjectDocument.currentSchemaVersion
        clean.modifiedUtc = ProjectDocument.timestamp()
        if clean.createdUtc.isEmpty { clean.createdUtc = clean.modifiedUtc }
        try clean.validate()

        let directory = url.deletingLastPathComponent()
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        } catch {
            throw ProjectModelError.writeFailed(error.localizedDescription)
        }

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data: Data
        do {
            data = try encoder.encode(clean)
        } catch {
            throw ProjectModelError.writeFailed(error.localizedDescription)
        }

        let temporary = directory.appendingPathComponent(".\(url.lastPathComponent).\(UUID().uuidString).tmp")
        do {
            try data.write(to: temporary, options: .atomic)
            if FileManager.default.fileExists(atPath: url.path) {
                _ = try FileManager.default.replaceItemAt(url, withItemAt: temporary)
            } else {
                try FileManager.default.moveItem(at: temporary, to: url)
            }
        } catch {
            try? FileManager.default.removeItem(at: temporary)
            throw ProjectModelError.writeFailed(error.localizedDescription)
        }
    }

    /// 为「新建工程」生成一个不冲突的文件名。
    public func suggestedURL(name: String) -> URL {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let base = trimmed.isEmpty ? "未命名工程" : trimmed
        let sanitized = base
            .replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: ":", with: "-")
        return projectsDirectory
            .appendingPathComponent(sanitized, isDirectory: false)
            .appendingPathExtension(ProjectStore.fileExtension)
    }

    /// 生成 256 px 缩略图（base64 PNG），用于主页最近工程卡片。
    public func thumbnailBase64(fromComposite compositeURL: URL, maxSize: Int = 256) -> String? {
        guard let source = CGImageSourceCreateWithURL(compositeURL as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil)
        else { return nil }
        let width = image.width
        let height = image.height
        guard width > 0, height > 0 else { return nil }
        let scale = min(Double(maxSize) / Double(width), Double(maxSize) / Double(height), 1)
        let targetWidth = max(1, Int(Double(width) * scale))
        let targetHeight = max(1, Int(Double(height) * scale))
        guard let context = CGContext(
            data: nil,
            width: targetWidth,
            height: targetHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: targetWidth, height: targetHeight))
        guard let scaled = context.makeImage() else { return nil }

        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            data as CFMutableData,
            "public.png" as CFString,
            1,
            nil
        ) else { return nil }
        CGImageDestinationAddImage(destination, scaled, nil)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return (data as Data).base64EncodedString()
    }
}

// MARK: - 最近工程

public struct RecentProject: Codable, Sendable, Equatable, Identifiable {
    public let path: String
    public let name: String
    public let family: DiscFamily
    public let modifiedUtc: String
    public let thumbnailPngBase64: String?

    public var id: String { path }

    public init(
        path: String,
        name: String,
        family: DiscFamily,
        modifiedUtc: String,
        thumbnailPngBase64: String?
    ) {
        self.path = path
        self.name = name
        self.family = family
        self.modifiedUtc = modifiedUtc
        self.thumbnailPngBase64 = thumbnailPngBase64
    }

    public var fileURL: URL {
        URL(fileURLWithPath: path)
    }
}

/// 主页「最近工程」列表，存放在 `~/Library/Application Support/DiscImageStudioMac/recent-projects.json`。
public struct RecentProjectsStore: Sendable {
    public static let maximumCount = 12

    public let fileURL: URL

    public init(fileURL: URL? = nil) {
        self.fileURL = fileURL ?? AppSettingsStore.defaultDirectory
            .appendingPathComponent("recent-projects.json", isDirectory: false)
    }

    public func load() -> [RecentProject] {
        guard let data = try? Data(contentsOf: fileURL),
              let projects = try? JSONDecoder().decode([RecentProject].self, from: data)
        else { return [] }
        return projects.sorted { RecentProjectsStore.date($0.modifiedUtc) > RecentProjectsStore.date($1.modifiedUtc) }
            .prefix(RecentProjectsStore.maximumCount)
            .map { $0 }
    }

    @discardableResult
    public func record(_ project: ProjectDocument, at url: URL) throws -> [RecentProject] {
        var projects = load().filter { $0.path != url.path }
        projects.insert(RecentProject(
            path: url.path,
            name: project.name,
            family: project.discFamily,
            modifiedUtc: project.modifiedUtc.isEmpty ? ProjectDocument.timestamp() : project.modifiedUtc,
            thumbnailPngBase64: project.thumbnailPngBase64
        ), at: 0)
        let trimmed = Array(projects.prefix(RecentProjectsStore.maximumCount))
        try write(trimmed)
        return trimmed
    }

    public func remove(path: String) throws {
        try write(load().filter { $0.path != path })
    }

    public func removeAll() throws {
        try write([])
    }

    private func write(_ projects: [RecentProject]) throws {
        let directory = fileURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(projects).write(to: fileURL, options: .atomic)
    }

    private static func date(_ text: String) -> Date {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: text) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: text) ?? .distantPast
    }
}
