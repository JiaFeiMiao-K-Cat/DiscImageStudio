import Foundation

public enum PreviewMode: String, Sendable, Equatable, CaseIterable {
    case designProjection
    case artifactReadback

    public var displayName: String {
        switch self {
        case .designProjection: return "设计投影"
        case .artifactReadback: return "成品读回"
        }
    }
}

public enum PreviewError: Error, Sendable, LocalizedError, Equatable {
    case invalidGeometry(String)
    case missingArtifact
    case unsupportedFamily(String)

    public var errorDescription: String? {
        switch self {
        case .invalidGeometry(let reason):
            return "预览参数无效：\(reason)"
        case .missingArtifact:
            return "请先生成或选择要读回的成品文件。"
        case .unsupportedFamily(let reason):
            return reason
        }
    }
}

/// 预览页的实测参数与精度设置（生成几何仍取自工程）。
public struct PreviewRequest: Sendable, Equatable {
    public var mode: PreviewMode
    public var previewSize: Int
    public var samplesPerSector: Int
    public var actualInnerRadiusMm: Double
    public var actualOuterRadiusMm: Double
    public var actualPitchLinear: Double
    public var actualPitchQuadratic: Double
    public var actualPitchCubic: Double
    public var invertMapping: Bool
    public var artifactURL: URL?
    public var deinterleave: Bool
    public var byteStep: Int
    /// 强制把 DVD ISO 按光栅 motif 上色（普通单色 ISO 也可用）。
    public var gratingPreview: Bool

    public init(
        mode: PreviewMode = .designProjection,
        previewSize: Int = 1600,
        samplesPerSector: Int = 16,
        actualInnerRadiusMm: Double,
        actualOuterRadiusMm: Double,
        actualPitchLinear: Double = 0,
        actualPitchQuadratic: Double = 0,
        actualPitchCubic: Double = 0,
        invertMapping: Bool = false,
        artifactURL: URL? = nil,
        deinterleave: Bool = true,
        byteStep: Int = 48,
        gratingPreview: Bool = false
    ) {
        self.mode = mode
        self.previewSize = previewSize
        self.samplesPerSector = samplesPerSector
        self.actualInnerRadiusMm = actualInnerRadiusMm
        self.actualOuterRadiusMm = actualOuterRadiusMm
        self.actualPitchLinear = actualPitchLinear
        self.actualPitchQuadratic = actualPitchQuadratic
        self.actualPitchCubic = actualPitchCubic
        self.invertMapping = invertMapping
        self.artifactURL = artifactURL
        self.deinterleave = deinterleave
        self.byteStep = byteStep
        self.gratingPreview = gratingPreview
    }

    public func defaultRequest(for project: ProjectDocument) -> PreviewRequest {
        PreviewRequest(
            mode: .designProjection,
            previewSize: Self.defaultPreviewSize(for: project.discFamily),
            samplesPerSector: 16,
            actualInnerRadiusMm: project.geometry.innerRadiusMm,
            actualOuterRadiusMm: project.geometry.outerRadiusMm,
            actualPitchLinear: project.geometry.pitchLinear,
            actualPitchQuadratic: project.geometry.pitchQuadratic,
            actualPitchCubic: project.geometry.pitchCubic,
            invertMapping: project.bluRaySettings.invertMapping
        )
    }

    public static func defaultPreviewSize(for family: DiscFamily) -> Int {
        switch family {
        case .cd: return 1600
        case .dvd: return 1200
        case .bd: return 2048
        }
    }
}

public struct PreviewPlan: Sendable, Equatable {
    public let family: DiscFamily
    public let mode: PreviewMode
    public let arguments: [String]
    public let outputURL: URL

    public init(family: DiscFamily, mode: PreviewMode, arguments: [String], outputURL: URL) {
        self.family = family
        self.mode = mode
        self.arguments = arguments
        self.outputURL = outputURL
    }

    public var commandLine: String {
        CommandArgumentBuilder.displayCommand(executable: "disc-image-studio", arguments: arguments)
    }
}

/// 把工程 + 预览请求转换成与 Windows 版 `LivePreviewViewModel` 一致的 CLI 参数。
public struct PreviewService: Sendable {
    public let cliInvocation: CliInvocation

    public init(cliInvocation: CliInvocation) {
        self.cliInvocation = cliInvocation
    }

    public static func makePlan(
        project: ProjectDocument,
        compositeURL: URL,
        request: PreviewRequest,
        outputURL: URL
    ) throws -> PreviewPlan {
        let geometry = project.geometry
        guard geometry.innerRadiusMm > 0,
              geometry.outerRadiusMm > geometry.innerRadiusMm,
              request.actualInnerRadiusMm > 0,
              request.actualOuterRadiusMm > request.actualInnerRadiusMm
        else {
            throw PreviewError.invalidGeometry("内半径必须为正且小于外半径。")
        }

        let tone = project.toneOrLegacy
        let size = clampPreviewSize(request.previewSize, for: project.discFamily)
        let samples = min(max(request.samplesPerSector, 1), 16)

        var arguments: [String]
        switch (project.discFamily, request.mode) {
        case (.cd, .designProjection):
            arguments = [
                "cd-preview-warp",
                "--input", compositeURL.path,
                "--output", outputURL.path,
                "--r0", ExportService.number(geometry.innerRadiusMm),
                "--r1", ExportService.number(geometry.outerRadiusMm),
                "--sectors", String(geometry.sectors),
                "--velocity", "1200",
                "--theta0", "0",
                "--outer", "57.5",
                "--actual-r0", ExportService.number(request.actualInnerRadiusMm),
                "--actual-r1", ExportService.number(request.actualOuterRadiusMm),
                "--tone-dither", ExportService.number(tone.ditherStrength),
                "--tone-levels", String(tone.levels),
                "--tone-brightness", ExportService.number(tone.brightness),
                "--tone-gamma", tone.srgbLinearization ? "true" : "false",
                "--size", String(size),
                "--samples-per-sector", String(samples),
            ]
            if project.outputs.hasBundledFiles {
                arguments += [
                    "--data-dir", project.outputs.dataDirectory,
                    "--volume-label", ExportService.label(project.outputs.volumeLabel, fallback: "CD_IMAGE"),
                    "--file-position", project.outputs.filesAtOuterRing ? "outer" : "inner",
                ]
            }
        case (.cd, .artifactReadback):
            guard let artifact = request.artifactURL else { throw PreviewError.missingArtifact }
            arguments = [
                "cd-preview-track",
                "--track", artifact.path,
                "--output", outputURL.path,
                "--r0", ExportService.number(geometry.innerRadiusMm),
                "--r1", ExportService.number(geometry.outerRadiusMm),
                "--sectors", String(geometry.sectors),
                "--velocity", "1200",
                "--theta0", "0",
                "--outer", "57.5",
                "--size", String(size),
                "--byte-step", String(max(request.byteStep, 1)),
                "--deinterleave", request.deinterleave ? "true" : "false",
            ]
        case (.dvd, .designProjection):
            arguments = [
                "calibrate",
                "--image", compositeURL.path,
                "--output", outputURL.path,
                "--total-sectors", String(geometry.sectors),
                "--lba", "0",
                "--fill-sectors", String(geometry.sectors),
                "--generated-inner-radius-mm", ExportService.number(geometry.innerRadiusMm),
                "--generated-outer-radius-mm", ExportService.number(geometry.outerRadiusMm),
                "--actual-inner-radius-mm", ExportService.number(request.actualInnerRadiusMm),
                "--actual-outer-radius-mm", ExportService.number(request.actualOuterRadiusMm),
                "--pitch-linear", ExportService.number(geometry.pitchLinear),
                "--pitch-quadratic", ExportService.number(geometry.pitchQuadratic),
                "--pitch-cubic", ExportService.number(geometry.pitchCubic),
                "--actual-pitch-linear", ExportService.number(request.actualPitchLinear),
                "--actual-pitch-quadratic", ExportService.number(request.actualPitchQuadratic),
                "--actual-pitch-cubic", ExportService.number(request.actualPitchCubic),
                "--spiral-direction", "cw",
                "--image-threshold", "128",
                "--alpha-threshold", "1",
                "--tone-dither", ExportService.number(tone.ditherStrength),
                "--tone-levels", "2",
                "--tone-brightness", ExportService.number(tone.brightness),
                "--tone-gamma", tone.srgbLinearization ? "true" : "false",
                "--preview-size", String(size),
                "--samples-per-sector", String(samples),
            ]
        case (.dvd, .artifactReadback):
            guard let artifact = request.artifactURL else { throw PreviewError.missingArtifact }
            arguments = [
                "simulate",
                "--iso", artifact.path,
                "--output", outputURL.path,
                "--total-sectors", String(geometry.sectors),
                "--inner-radius-mm", ExportService.number(request.actualInnerRadiusMm),
                "--outer-radius-mm", ExportService.number(request.actualOuterRadiusMm),
                "--pitch-linear", ExportService.number(geometry.pitchLinear),
                "--pitch-quadratic", ExportService.number(geometry.pitchQuadratic),
                "--pitch-cubic", ExportService.number(geometry.pitchCubic),
                "--spiral-direction", "cw",
                "--preview-size", String(size),
                "--samples-per-sector", String(samples),
            ]
            if request.gratingPreview {
                arguments += ["--grating", "true"]
            }
        case (.bd, .designProjection):
            arguments = [
                "bd-preview",
                "--input", compositeURL.path,
                "--output", outputURL.path,
                "--total-sectors", String(geometry.sectors),
                "--r0", ExportService.number(geometry.innerRadiusMm),
                "--r1", ExportService.number(geometry.outerRadiusMm),
                "--actual-r0", ExportService.number(request.actualInnerRadiusMm),
                "--actual-r1", ExportService.number(request.actualOuterRadiusMm),
                "--invert", request.invertMapping ? "true" : "false",
                "--tone-dither", ExportService.number(tone.ditherStrength),
                "--tone-levels", "2",
                "--tone-brightness", ExportService.number(tone.brightness),
                "--tone-gamma", tone.srgbLinearization ? "true" : "false",
                "--size", String(size),
            ]
        case (.bd, .artifactReadback):
            guard let artifact = request.artifactURL else { throw PreviewError.missingArtifact }
            arguments = [
                "bd-preview-readback",
                "--iso", artifact.path,
                "--output", outputURL.path,
                "--size", String(size),
            ]
        }

        return PreviewPlan(
            family: project.discFamily,
            mode: request.mode,
            arguments: arguments,
            outputURL: outputURL
        )
    }

    public func run(_ plan: PreviewPlan) -> CliJob {
        let runner = CliRunner(invocation: cliInvocation)
        return runner.start(arguments: plan.arguments)
    }

    public static func clampPreviewSize(_ value: Int, for family: DiscFamily) -> Int {
        let range: ClosedRange<Int> = family == .bd ? 64...8192 : 256...8192
        return min(max(value, range.lowerBound), range.upperBound)
    }
}
