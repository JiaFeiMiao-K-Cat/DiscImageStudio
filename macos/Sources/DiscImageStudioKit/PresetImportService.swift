import Foundation

public struct PresetImportSummary: Codable, Sendable, Equatable {
    public let ok: Bool
    public let path: String
    public let cdPresets: Int
    public let dvdPresets: Int
    public let bluRayPresets: Int

    public init(ok: Bool, path: String, cdPresets: Int, dvdPresets: Int, bluRayPresets: Int) {
        self.ok = ok
        self.path = path
        self.cdPresets = cdPresets
        self.dvdPresets = dvdPresets
        self.bluRayPresets = bluRayPresets
    }
}

public enum PresetImportError: Error, Sendable, LocalizedError, Equatable {
    case cliMissing
    case invalidResponse(String)
    case cliFailed(Int32, String)

    public var errorDescription: String? {
        switch self {
        case .cliMissing:
            return "找不到 disc-image-studio CLI；请先在设置里指定可执行文件。"
        case .invalidResponse(let reason):
            return "预设导入返回了无法解析的结果：\(reason)"
        case .cliFailed(let code, let message):
            let detail = message.trimmingCharacters(in: .whitespacesAndNewlines)
            return detail.isEmpty
                ? "预设导入失败（退出码 \(code)）。"
                : "预设导入失败（退出码 \(code)）：\(detail)"
        }
    }
}

/// 把 schema 4 预设 JSON 合并进用户预设文件（`presets-import`）。
public struct PresetImportService: Sendable {
    public let cliInvocation: CliInvocation

    public init(cliInvocation: CliInvocation) {
        self.cliInvocation = cliInvocation
    }

    public func importPresets(from fileURL: URL) async throws -> PresetImportSummary {
        let runner = CliRunner(invocation: cliInvocation)
        let output = try await runner.runAndCollect(
            arguments: ["presets-import", "--file", fileURL.path],
            timeout: .seconds(60)
        )
        guard output.result.exitCode == 0 else {
            throw PresetImportError.cliFailed(output.result.exitCode, output.standardError)
        }
        guard let data = output.standardOutput.data(using: .utf8) else {
            throw PresetImportError.invalidResponse("输出不是 UTF-8")
        }
        do {
            let summary = try JSONDecoder().decode(PresetImportSummary.self, from: data)
            guard summary.ok else {
                throw PresetImportError.invalidResponse(output.standardOutput)
            }
            return summary
        } catch let error as PresetImportError {
            throw error
        } catch {
            throw PresetImportError.invalidResponse(error.localizedDescription)
        }
    }
}
