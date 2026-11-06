import Foundation

public struct ExportPlan: Sendable, Equatable {
    public let family: DiscFamily
    public let arguments: [String]
    public let outputURL: URL

    public init(family: DiscFamily, arguments: [String], outputURL: URL) {
        self.family = family
        self.arguments = arguments
        self.outputURL = outputURL
    }

    public var commandLine: String {
        CommandArgumentBuilder.displayCommand(executable: "disc-image-studio", arguments: arguments)
    }
}

public enum ExportError: Error, Sendable, LocalizedError, Equatable {
    case missingOutput
    case unsupportedExtension(expected: String, actual: String)
    case invalidGeometry(String)

    public var errorDescription: String? {
        switch self {
        case .missingOutput:
            return "请先在导出页选择输出位置。"
        case .unsupportedExtension(let expected, let actual):
            return "输出扩展名 \(actual) 与当前盘型的预期 \(expected) 不一致。"
        case .invalidGeometry(let reason):
            return "生成几何无效：\(reason)"
        }
    }
}

/// 把工程转换成与 Windows 版 `ExportViewModel` 完全一致的 CLI 参数。
public struct ExportService: Sendable {
    public let cliInvocation: CliInvocation

    public init(cliInvocation: CliInvocation) {
        self.cliInvocation = cliInvocation
    }

    public static func makePlan(
        project: ProjectDocument,
        compositeURL: URL,
        outputURL: URL? = nil
    ) throws -> ExportPlan {
        let output = try resolveOutput(project: project, outputURL: outputURL)
        let tone = project.toneOrLegacy
        let geometry = project.geometry
        guard geometry.innerRadiusMm > 0,
              geometry.outerRadiusMm > geometry.innerRadiusMm,
              geometry.sectors > 0
        else {
            throw ExportError.invalidGeometry("内/外半径与扇区数不合法。")
        }

        var arguments: [String]
        switch project.discFamily {
        case .cd:
            arguments = [
                "cd-generate",
                "--input", compositeURL.path,
                "--output", output.path,
                "--r0", number(geometry.innerRadiusMm),
                "--r1", number(geometry.outerRadiusMm),
                "--sectors", String(geometry.sectors),
                "--tone-dither", number(tone.ditherStrength),
                "--tone-levels", String(tone.levels),
                "--tone-brightness", number(tone.brightness),
                "--tone-gamma", tone.srgbLinearization ? "true" : "false",
            ]
            if project.outputs.hasBundledFiles {
                arguments += [
                    "--data-dir", project.outputs.dataDirectory,
                    "--volume-label", label(project.outputs.volumeLabel, fallback: "CD_IMAGE"),
                    "--file-position", project.outputs.filesAtOuterRing ? "outer" : "inner",
                ]
            } else {
                arguments += [
                    "--interleave", project.outputs.interleave ? "true" : "false",
                    "--cue", project.outputs.writeCue ? "true" : "false",
                ]
            }
        case .dvd:
            arguments = [
                "solve",
                "--image", compositeURL.path,
                "--iso-output", output.path,
                "--total-sectors", String(geometry.sectors),
                "--inner-radius-mm", number(geometry.innerRadiusMm),
                "--outer-radius-mm", number(geometry.outerRadiusMm),
                "--pitch-linear", number(geometry.pitchLinear),
                "--pitch-quadratic", number(geometry.pitchQuadratic),
                "--pitch-cubic", number(geometry.pitchCubic),
                "--spiral-direction", "cw",
                "--image-threshold", "128",
                "--alpha-threshold", "1",
                "--tone-dither", number(tone.ditherStrength),
                "--tone-levels", "2",
                "--tone-brightness", number(tone.brightness),
                "--tone-gamma", tone.srgbLinearization ? "true" : "false",
                "--algorithm", "dispersion",
                "--iterations-per-block", "1",
                "--fast-output", "true",
                "--volume-label", label(project.outputs.volumeLabel, fallback: "DISC_IMAGE"),
            ]
            if project.outputs.hasBundledFiles {
                arguments += [
                    "--data-dir", project.outputs.dataDirectory,
                    "--file-position", project.outputs.filesAtOuterRing ? "outer" : "inner",
                ]
            } else {
                arguments += ["--fill-sectors", String(geometry.sectors)]
            }
        case .bd:
            arguments = [
                "bd-generate",
                "--input", compositeURL.path,
                "--output", output.path,
                "--total-sectors", String(geometry.sectors),
                "--r0", number(geometry.innerRadiusMm),
                "--r1", number(geometry.outerRadiusMm),
                "--invert", project.bluRaySettings.invertMapping ? "true" : "false",
                "--volume-label", label(project.outputs.volumeLabel, fallback: "BD_IMAGE"),
                "--tone-dither", number(tone.ditherStrength),
                "--tone-levels", "2",
                "--tone-brightness", number(tone.brightness),
                "--tone-gamma", tone.srgbLinearization ? "true" : "false",
            ]
            if project.outputs.hasBundledFiles {
                arguments += [
                    "--data-dir", project.outputs.dataDirectory,
                    "--file-position", project.outputs.filesAtOuterRing ? "outer" : "inner",
                ]
            }
        }

        return ExportPlan(family: project.discFamily, arguments: arguments, outputURL: output)
    }

    public func run(_ plan: ExportPlan) -> CliJob {
        let runner = CliRunner(invocation: cliInvocation)
        return runner.start(arguments: plan.arguments)
    }

    public static func resolveOutput(project: ProjectDocument, outputURL: URL?) throws -> URL {
        let raw = outputURL?.path ?? project.outputs.outputPath
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw ExportError.missingOutput }
        let expanded = (trimmed as NSString).expandingTildeInPath
        let url = URL(fileURLWithPath: expanded).standardizedFileURL
        let expected: [String]
        switch project.discFamily {
        case .cd:
            expected = project.outputs.hasBundledFiles ? ["iso"] : ["wav", "raw"]
        case .dvd, .bd:
            expected = ["iso"]
        }
        let actual = url.pathExtension.lowercased()
        if !actual.isEmpty, !expected.contains(actual) {
            throw ExportError.unsupportedExtension(
                expected: expected.joined(separator: " / "),
                actual: actual
            )
        }
        return url
    }

    static func number(_ value: Double) -> String {
        guard value.isFinite else { return "0" }
        if value == value.rounded(), abs(value) < 1e15 {
            return String(Int64(value))
        }
        return String(value)
    }

    static func label(_ value: String, fallback: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? fallback : trimmed
    }
}
