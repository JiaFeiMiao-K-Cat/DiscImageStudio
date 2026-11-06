import Foundation

public enum CliSource: String, Sendable, Equatable {
    case environment
    case custom
    case bundled
    case path
    case dotnetProject
}

public struct ResolvedCli: Sendable, Equatable {
    public let invocation: CliInvocation
    public let source: CliSource
    public let detail: String

    public init(invocation: CliInvocation, source: CliSource, detail: String) {
        self.invocation = invocation
        self.source = source
        self.detail = detail
    }

    public var label: String {
        switch source {
        case .environment: return "环境变量"
        case .custom: return "自定义路径"
        case .bundled: return "App 内置"
        case .path: return "PATH"
        case .dotnetProject: return "dotnet run"
        }
    }
}

public enum CliResolverError: Error, Sendable, LocalizedError {
    case notFound(searched: [String])
    case launchFailed(String)
    case catalogFailed(reason: String)
    case unsupportedSchema(found: Int, supported: Int)

    public var errorDescription: String? {
        switch self {
        case .notFound(let searched):
            if searched.isEmpty {
                return "没有找到 disc-image-studio。请下载 macOS 版 CLI，或在设置里指定可执行文件路径。"
            }
            return "没有找到 disc-image-studio。已尝试：\n" + searched.joined(separator: "\n")
        case .launchFailed(let reason):
            return "无法启动 CLI：\(reason)"
        case .catalogFailed(let reason):
            return "CLI 未返回可用的命令目录：\(reason)"
        case .unsupportedSchema(let found, let supported):
            return "命令目录 schema 版本 \(found) 不受支持（当前支持 \(supported)）。请更新图形界面或 CLI。"
        }
    }
}

/// 按优先级发现可用的 CLI：环境变量 → 自定义路径 → App 内置 → PATH → 源码 dotnet run。
public struct CliResolver: Sendable {
    private let settings: AppSettings
    private let environment: [String: String]
    private let resourceDirectory: URL?
    private let homeDirectory: URL

    public init(
        settings: AppSettings,
        bundle: Bundle = .main,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        fileManager: FileManager = .default
    ) {
        self.settings = settings
        self.environment = environment
        self.resourceDirectory = bundle.resourceURL
        self.homeDirectory = fileManager.homeDirectoryForCurrentUser
    }

    public var candidates: [ResolvedCli] {
        var result: [ResolvedCli] = []

        if let environmentPath = environment["DIS_IMAGE_STUDIO_CLI"],
           let resolved = executableCandidate(
               path: environmentPath,
               source: .environment,
               label: "环境变量 DIS_IMAGE_STUDIO_CLI"
           ) {
            result.append(resolved)
        }

        if let custom = settings.customCliPath,
           let resolved = executableCandidate(path: custom, source: .custom, label: "设置中的 CLI") {
            result.append(resolved)
        }

        if let resourceDirectory {
            let bundled = resourceDirectory
                .appendingPathComponent("cli", isDirectory: true)
                .appendingPathComponent("disc-image-studio", isDirectory: false)
            if isExecutable(bundled) {
                result.append(ResolvedCli(
                    invocation: CliInvocation(executableURL: bundled, label: "App 内置 CLI"),
                    source: .bundled,
                    detail: bundled.path
                ))
            }
        }

        for directory in searchDirectories {
            for name in ["disc-image-studio", "disc-image-studio-cli"] {
                let candidate = directory.appendingPathComponent(name, isDirectory: false)
                if isExecutable(candidate) {
                    result.append(ResolvedCli(
                        invocation: CliInvocation(executableURL: candidate, label: "PATH 中的 CLI"),
                        source: .path,
                        detail: candidate.path
                    ))
                }
            }
        }

        if let development = developmentCandidate() {
            result.append(development)
        }

        return result
    }

    public func resolve() -> ResolvedCli? {
        candidates.first
    }

    public func loadCatalog(from cli: ResolvedCli) async throws -> CommandCatalog {
        let runner = CliRunner(invocation: cli.invocation)
        let output: CliRunOutput
        do {
            output = try await runner.runAndCollect(arguments: ["catalog", "--json"])
        } catch {
            throw CliResolverError.catalogFailed(reason: error.localizedDescription)
        }

        guard output.result.succeeded else {
            let detail = output.standardError.trimmingCharacters(in: .whitespacesAndNewlines)
            let message = detail.isEmpty
                ? "退出码 \(output.result.exitCode)"
                : detail
            throw CliResolverError.catalogFailed(reason: message)
        }

        do {
            return try CommandCatalog.decode(Data(output.standardOutput.utf8))
        } catch let error as CommandCatalogError {
            switch error {
            case .unsupportedSchema(let found, let supported):
                throw CliResolverError.unsupportedSchema(found: found, supported: supported)
            case .malformedJSON:
                throw CliResolverError.catalogFailed(reason: error.localizedDescription)
            }
        } catch {
            throw CliResolverError.catalogFailed(reason: error.localizedDescription)
        }
    }

    /// 逐个尝试候选，返回第一个能成功输出命令目录的 CLI。
    public func resolveAndLoadCatalog() async throws -> (cli: ResolvedCli, catalog: CommandCatalog) {
        let candidates = candidates
        guard !candidates.isEmpty else {
            throw CliResolverError.notFound(searched: [])
        }

        var lastError: Error?
        for candidate in candidates {
            do {
                let catalog = try await loadCatalog(from: candidate)
                return (candidate, catalog)
            } catch {
                lastError = error
            }
        }

        if let error = lastError {
            throw error
        }
        throw CliResolverError.notFound(searched: candidates.map(\.detail))
    }

    // MARK: - 候选构造

    private func executableCandidate(
        path: String,
        source: CliSource,
        label: String
    ) -> ResolvedCli? {
        let expanded = (path as NSString).expandingTildeInPath
        guard !expanded.isEmpty else { return nil }
        let url = URL(fileURLWithPath: expanded).standardizedFileURL
        guard isExecutable(url) else { return nil }
        return ResolvedCli(
            invocation: CliInvocation(executableURL: url, label: label),
            source: source,
            detail: url.path
        )
    }

    private var searchDirectories: [URL] {
        var directories: [URL] = []
        var seen = Set<String>()

        func append(_ url: URL) {
            let normalized = url.standardizedFileURL
            guard !seen.contains(normalized.path) else { return }
            seen.insert(normalized.path)
            directories.append(normalized)
        }

        if let path = environment["PATH"] {
            for entry in path.split(separator: ":") where !entry.isEmpty {
                append(URL(fileURLWithPath: String(entry), isDirectory: true))
            }
        }
        append(homeDirectory.appendingPathComponent(".local/bin", isDirectory: true))
        append(homeDirectory.appendingPathComponent(".dotnet/tools", isDirectory: true))
        append(URL(fileURLWithPath: "/usr/local/bin", isDirectory: true))
        append(URL(fileURLWithPath: "/opt/homebrew/bin", isDirectory: true))
        return directories
    }

    private func developmentCandidate() -> ResolvedCli? {
        guard let raw = settings.devProjectPath else { return nil }
        let expanded = (raw as NSString).expandingTildeInPath.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !expanded.isEmpty else { return nil }
        let root = URL(fileURLWithPath: expanded).standardizedFileURL
        guard let project = locateProject(startingAt: root),
              let dotnet = dotnetExecutable() else { return nil }
        return ResolvedCli(
            invocation: CliInvocation(
                executableURL: dotnet,
                prefixArguments: ["run", "--project", project.path, "-c", "Release", "--"],
                workingDirectoryURL: project.deletingLastPathComponent().deletingLastPathComponent(),
                label: "dotnet run（源码）"
            ),
            source: .dotnetProject,
            detail: project.path
        )
    }

    private func locateProject(startingAt url: URL) -> URL? {
        let candidates: [URL]
        if url.pathExtension == "csproj" {
            candidates = [url]
        } else {
            candidates = [
                url.appendingPathComponent("src/DiscImageStudio.Cli/DiscImageStudio.Cli.csproj"),
                url.appendingPathComponent("DiscImageStudio.Cli.csproj"),
                url.appendingPathComponent("src/DiscImageStudio.Cli/DiscImageStudio.Cli.csproj"),
                url.appendingPathComponent("DiscImageStudio.Cli/DiscImageStudio.Cli.csproj")
            ]
        }
        return candidates.first { FileManager.default.fileExists(atPath: $0.path) }
    }

    private func dotnetExecutable() -> URL? {
        var candidates: [URL] = [
            homeDirectory.appendingPathComponent(".dotnet/dotnet"),
            URL(fileURLWithPath: "/usr/local/share/dotnet/dotnet"),
            URL(fileURLWithPath: "/opt/homebrew/bin/dotnet"),
            URL(fileURLWithPath: "/usr/local/bin/dotnet")
        ]
        if let root = environment["DOTNET_ROOT"], !root.isEmpty {
            candidates.insert(URL(fileURLWithPath: root).appendingPathComponent("dotnet"), at: 0)
        }
        if let path = environment["PATH"] {
            for entry in path.split(separator: ":") where !entry.isEmpty {
                candidates.append(URL(fileURLWithPath: String(entry)).appendingPathComponent("dotnet"))
            }
        }
        return candidates.first { isExecutable($0) }
    }

    private func isExecutable(_ url: URL) -> Bool {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory),
              !isDirectory.boolValue else { return false }
        return FileManager.default.isExecutableFile(atPath: url.path)
    }
}
