import AppKit
import SwiftUI
import UniformTypeIdentifiers
import DiscImageStudioKit

/// 统一的文件/目录选择面板。
@MainActor
enum FilePanels {
    static func chooseInputFile(title: String, currentPath: String?) -> URL? {
        let panel = NSOpenPanel()
        panel.title = title
        panel.prompt = L("选择")
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.resolvesAliases = true
        presetDirectory(of: currentPath, for: panel)
        return run(panel)
    }

    static func chooseInputFolder(title: String, currentPath: String?) -> URL? {
        let panel = NSOpenPanel()
        panel.title = title
        panel.prompt = L("选择")
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        presetDirectory(of: currentPath, for: panel)
        return run(panel)
    }

    static func choosePresetFile(title: String = L("导入预设")) -> URL? {
        let panel = NSOpenPanel()
        panel.title = title
        panel.prompt = L("导入")
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.resolvesAliases = true
        panel.allowedContentTypes = [.json]
        NSApp.activate(ignoringOtherApps: true)
        return panel.runModal() == .OK ? panel.url : nil
    }

    static func chooseImages(title: String = L("导入图片"), currentPath: String? = nil) -> [URL] {
        let panel = NSOpenPanel()
        panel.title = title
        panel.prompt = L("导入")
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        panel.resolvesAliases = true
        panel.allowedContentTypes = [.png, .jpeg, .tiff, .heic]
        presetDirectory(of: currentPath, for: panel)
        NSApp.activate(ignoringOtherApps: true)
        return panel.runModal() == .OK ? panel.urls : []
    }

    static func chooseOutputFile(
        title: String,
        currentPath: String?,
        defaultDirectory: String?,
        allowedExtensions: [String]
    ) -> URL? {
        let panel = NSSavePanel()
        panel.title = title
        panel.prompt = L("选择")
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false
        let types = allowedExtensions.compactMap { UTType(filenameExtension: $0) }
        if !types.isEmpty { panel.allowedContentTypes = types }

        if let currentPath, let currentURL = Formatting.expandedURL(for: currentPath) {
            panel.directoryURL = currentURL.deletingLastPathComponent()
            panel.nameFieldStringValue = currentURL.lastPathComponent
        } else if let defaultDirectory,
                  let directory = Formatting.expandedURL(for: defaultDirectory) {
            panel.directoryURL = directory
        }
        return run(panel)
    }

    static func chooseExecutableFile(title: String) -> URL? {
        let panel = NSOpenPanel()
        panel.title = title
        panel.prompt = L("选择")
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.resolvesAliases = true
        return run(panel)
    }

    static func chooseDirectory(title: String, currentPath: String?) -> URL? {
        let panel = NSOpenPanel()
        panel.title = title
        panel.prompt = L("选择")
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        presetDirectory(of: currentPath, for: panel)
        return run(panel)
    }

    private static func presetDirectory(of currentPath: String?, for panel: NSSavePanel) {
        guard let currentPath, let url = Formatting.expandedURL(for: currentPath) else { return }
        var isDirectory: ObjCBool = false
        if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory) {
            panel.directoryURL = isDirectory.boolValue ? url : url.deletingLastPathComponent()
        } else {
            panel.directoryURL = url.deletingLastPathComponent()
        }
    }

    private static func run(_ panel: NSSavePanel) -> URL? {
        NSApp.activate(ignoringOtherApps: true)
        return panel.runModal() == .OK ? panel.url : nil
    }
}

/// 文件/目录字段下方的状态提示。
struct PathStatusHint: View {
    enum Kind {
        case inputFile
        case outputFile
        case folder
    }

    let path: String
    let kind: Kind

    private var trimmed: String {
        path.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    var body: some View {
        if !trimmed.isEmpty, let url = Formatting.expandedURL(for: trimmed) {
            content(for: url).font(.caption2)
        }
    }

    @ViewBuilder
    private func content(for url: URL) -> some View {
        var isDirectory: ObjCBool = false
        let exists = FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory)
        switch kind {
        case .inputFile:
            if exists {
                if isDirectory.boolValue {
                    hint(L("这是一个目录。"), systemImage: "xmark.circle.fill", tint: .orange)
                } else if let size = Formatting.fileSize(at: url) {
                    hint(L("文件存在 · %@", String(size)), systemImage: "checkmark.circle.fill", tint: .secondary)
                } else {
                    hint(L("文件存在"), systemImage: "checkmark.circle.fill", tint: .secondary)
                }
            } else {
                hint(L("找不到这个文件。"), systemImage: "exclamationmark.triangle.fill", tint: .orange)
            }
        case .outputFile:
            if exists {
                if isDirectory.boolValue {
                    hint(L("这是一个目录，输出需要指定文件名。"), systemImage: "xmark.circle.fill", tint: .orange)
                } else {
                    hint(L("将覆盖已有文件"), systemImage: "exclamationmark.circle", tint: .orange)
                }
            } else {
                hint(L("运行后创建"), systemImage: "plus.circle", tint: .secondary)
            }
        case .folder:
            if exists && isDirectory.boolValue {
                hint(L("目录存在"), systemImage: "checkmark.circle.fill", tint: .secondary)
            } else if exists {
                hint(L("这不是目录。"), systemImage: "xmark.circle.fill", tint: .orange)
            } else {
                hint(L("目录不存在。"), systemImage: "exclamationmark.triangle.fill", tint: .orange)
            }
        }
    }

    private func hint(_ text: String, systemImage: String, tint: Color) -> some View {
        Label {
            Text(L(text))
        } icon: {
            Image(systemName: systemImage)
        }
        .foregroundStyle(tint)
    }
}
