import Foundation

/// 标定命令的 Swift 封装：所有几何/拟合计算都在 CLI 内完成，这里只负责参数拼装与 JSON 解析。
public struct CalibrationService: Sendable {
    public let cliInvocation: CliInvocation
    public let workspace: URL

    public init(cliInvocation: CliInvocation, workspace: URL) {
        self.cliInvocation = cliInvocation
        self.workspace = workspace
    }

    // MARK: - 步骤 1：标靶

    public func generateTarget(_ request: CalibrationTargetRequest) async throws -> CalibrationTargetResult {
        guard request.geometry.innerRadiusMm > 0,
              request.geometry.outerRadiusMm > request.geometry.innerRadiusMm,
              request.geometry.sectors > 0
        else {
            throw CalibrationError.invalidGeometry("内半径必须为正且小于外半径，扇区数必须大于 0。")
        }
        try ensureWorkspace()
        try ensureParentDirectory(of: request.outputURL)
        try ensureParentDirectory(of: request.sessionURL)

        var arguments = [
            "calibration-target",
            "--family", request.family.rawValue,
            "--r0", number(request.geometry.innerRadiusMm),
            "--r1", number(request.geometry.outerRadiusMm),
            "--sectors", String(request.geometry.sectors),
            "--density", number(request.density),
            "--size", String(request.size),
            "--output", request.outputURL.path,
            "--session", request.sessionURL.path,
        ]
        if request.family == .dvd {
            arguments += [
                "--pitch-linear", number(request.geometry.pitchLinear),
                "--pitch-quadratic", number(request.geometry.pitchQuadratic),
                "--pitch-cubic", number(request.geometry.pitchCubic),
            ]
        }
        if request.family == .bd {
            if request.invertMapping { arguments.append("--invert") }
            if request.drawingStart != 0 { arguments += ["--drawing-start", String(request.drawingStart)] }
            if request.drawingEnd != 0 { arguments += ["--drawing-end", String(request.drawingEnd)] }
        }

        _ = try await run(arguments)
        guard FileManager.default.fileExists(atPath: request.outputURL.path) else {
            throw CalibrationError.commandFailed("标靶图片没有生成。")
        }
        guard FileManager.default.fileExists(atPath: request.sessionURL.path) else {
            throw CalibrationError.commandFailed("标定会话没有生成。")
        }
        return CalibrationTargetResult(
            imageURL: request.outputURL,
            sessionURL: request.sessionURL,
            size: request.size,
            family: request.family
        )
    }

    public func generateCoarseTarget(
        family: DiscFamily,
        geometry: ProjectGeometry,
        density: Double,
        size: Int = 2400,
        outputURL: URL,
        p0URL: URL
    ) async throws -> CalibrationCoarseTargetResult {
        try ensureWorkspace()
        try ensureParentDirectory(of: outputURL)
        try ensureParentDirectory(of: p0URL)
        var arguments = [
            "calibration-coarse-target",
            "--family", family.rawValue,
            "--r0", number(geometry.innerRadiusMm),
            "--r1", number(geometry.outerRadiusMm),
            "--sectors", String(geometry.sectors),
            "--density", number(density),
            "--size", String(size),
            "--output", outputURL.path,
            "--json", p0URL.path,
        ]
        if family == .dvd {
            arguments += [
                "--pitch-linear", number(geometry.pitchLinear),
                "--pitch-quadratic", number(geometry.pitchQuadratic),
                "--pitch-cubic", number(geometry.pitchCubic),
            ]
        }
        _ = try await run(arguments)
        guard FileManager.default.fileExists(atPath: outputURL.path) else {
            throw CalibrationError.commandFailed("粗调图案没有生成。")
        }
        return CalibrationCoarseTargetResult(imageURL: outputURL, p0URL: p0URL, size: size)
    }

    // MARK: - 步骤 2：照片矫正

    /// 只做照片缩放，返回缩小后的尺寸；用于在照片上摆放轮廓控制点。
    public func preparePhoto(
        photoURL: URL,
        discSize: CalibrationDiscSize,
        resizedOut: URL
    ) async throws -> CalibrationPhotoResult {
        try ensureWorkspace()
        try ensureParentDirectory(of: resizedOut)
        let json = try resultFileURL()
        defer { try? FileManager.default.removeItem(at: json) }
        _ = try await run([
            "calibration-photo",
            "--photo", photoURL.path,
            "--disc-size", String(discSize.rawValue),
            "--resized-out", resizedOut.path,
            "--json", json.path,
        ])
        return try decode(CalibrationPhotoResult.self, from: json)
    }

    public func rectifyPhoto(
        photoURL: URL,
        discSize: CalibrationDiscSize,
        outer: [CalibrationPoint],
        hole: [CalibrationPoint],
        resizedOut: URL,
        rectifiedOut: URL,
        size: Int = 1200
    ) async throws -> CalibrationPhotoResult {
        guard outer.count >= 5, hole.count >= 5 else {
            throw CalibrationError.invalidGeometry("外缘与中心孔各需要至少 5 个控制点。")
        }
        try ensureWorkspace()
        try ensureParentDirectory(of: resizedOut)
        try ensureParentDirectory(of: rectifiedOut)
        let json = try resultFileURL()
        defer { try? FileManager.default.removeItem(at: json) }
        _ = try await run([
            "calibration-photo",
            "--photo", photoURL.path,
            "--disc-size", String(discSize.rawValue),
            "--outer", encode(points: outer),
            "--hole", encode(points: hole),
            "--size", String(size),
            "--resized-out", resizedOut.path,
            "--rectified-out", rectifiedOut.path,
            "--json", json.path,
        ])
        return try decode(CalibrationPhotoResult.self, from: json)
    }

    // MARK: - 步骤 3：拟合

    public func fit(
        sessionURL: URL,
        points: [CalibrationPoint],
        radiusRange: Double? = nil,
        autoRange: Bool = true,
        fitPitch: Bool = true
    ) async throws -> CalibrationFitResult {
        guard points.count >= 6 else {
            throw CalibrationError.invalidGeometry("沿同一条曲线至少需要 6 个点。")
        }
        try ensureWorkspace()
        let json = try resultFileURL()
        defer { try? FileManager.default.removeItem(at: json) }
        var arguments = [
            "calibration-fit",
            "--session", sessionURL.path,
            "--points", encode(points: points),
            "--json", json.path,
            "--auto-range", autoRange ? "true" : "false",
            "--fit-pitch", fitPitch ? "true" : "false",
        ]
        if let radiusRange {
            arguments += ["--radius-range", number(radiusRange)]
        }
        _ = try await run(arguments)
        return try decode(CalibrationFitResult.self, from: json)
    }

    public func fitCoarse(
        family: DiscFamily,
        geometry: ProjectGeometry,
        density: Double,
        points: [CalibrationPoint]
    ) async throws -> CalibrationCoarseResult {
        guard points.count >= 8 else {
            throw CalibrationError.invalidGeometry("粗调分界至少需要 8 个点。")
        }
        try ensureWorkspace()
        let json = try resultFileURL()
        defer { try? FileManager.default.removeItem(at: json) }
        var arguments = [
            "calibration-coarse-fit",
            "--family", family.rawValue,
            "--r0", number(geometry.innerRadiusMm),
            "--r1", number(geometry.outerRadiusMm),
            "--sectors", String(geometry.sectors),
            "--density", number(density),
            "--points", encode(points: points),
            "--json", json.path,
        ]
        if family == .dvd {
            arguments += [
                "--pitch-linear", number(geometry.pitchLinear),
                "--pitch-quadratic", number(geometry.pitchQuadratic),
                "--pitch-cubic", number(geometry.pitchCubic),
            ]
        }
        _ = try await run(arguments)
        return try decode(CalibrationCoarseResult.self, from: json)
    }

    // MARK: - 会话

    public func writeSession(
        baseSessionURL: URL,
        photoURL: URL,
        discSize: CalibrationDiscSize,
        outer: [CalibrationPoint],
        hole: [CalibrationPoint],
        points: [CalibrationPoint],
        outputURL: URL,
        confirmed: Bool = true
    ) async throws -> CalibrationSessionSummary {
        try ensureWorkspace()
        try ensureParentDirectory(of: outputURL)
        _ = try await run([
            "calibration-session-write",
            "--session", baseSessionURL.path,
            "--photo", photoURL.path,
            "--disc-size", String(discSize.rawValue),
            "--outer", encode(points: outer),
            "--hole", encode(points: hole),
            "--points", encode(points: points),
            "--confirmed", confirmed ? "true" : "false",
            "--output", outputURL.path,
        ])
        return try await readSession(outputURL)
    }

    public func readSession(_ sessionURL: URL) async throws -> CalibrationSessionSummary {
        let json = try resultFileURL()
        defer { try? FileManager.default.removeItem(at: json) }
        _ = try await run([
            "calibration-session-read",
            "--session", sessionURL.path,
            "--json", json.path,
        ])
        return try decode(CalibrationSessionSummary.self, from: json)
    }

    // MARK: - 文件布局

    /// 工程内的标定工作目录：`<project dir>/calibration`。
    public static func workspace(forProjectURL projectURL: URL) -> URL {
        projectURL.deletingLastPathComponent().appendingPathComponent("calibration", isDirectory: true)
    }

    public func ensureWorkspace() throws {
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
    }

    // MARK: - 内部

    private func run(_ arguments: [String]) async throws -> CliRunOutput {
        let runner = CliRunner(invocation: cliInvocation)
        let output: CliRunOutput
        do {
            output = try await runner.runAndCollect(arguments: arguments, timeout: .seconds(300))
        } catch {
            throw CalibrationError.commandFailed(error.localizedDescription)
        }
        guard output.result.succeeded else {
            let detail = output.standardError.trimmingCharacters(in: .whitespacesAndNewlines)
            throw CalibrationError.commandFailed(detail.isEmpty ? "退出码 \(output.result.exitCode)" : detail)
        }
        return output
    }

    private func resultFileURL() throws -> URL {
        try ensureWorkspace()
        return workspace.appendingPathComponent("result-\(UUID().uuidString.lowercased()).json", isDirectory: false)
    }

    private func decode<T: Decodable>(_ type: T.Type, from url: URL) throws -> T {
        guard let data = try? Data(contentsOf: url) else {
            throw CalibrationError.malformedResult("结果文件不存在。")
        }
        do {
            return try JSONDecoder().decode(type, from: data)
        } catch {
            throw CalibrationError.malformedResult(String(describing: error))
        }
    }

    private func ensureParentDirectory(of url: URL) throws {
        let directory = url.deletingLastPathComponent()
        if !FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        }
    }

    private func encode(points: [CalibrationPoint]) -> String {
        points.map { "\(number($0.x)),\(number($0.y))" }.joined(separator: ";")
    }

    private func number(_ value: Double) -> String {
        guard value.isFinite else { return "0" }
        return String(value)
    }
}
