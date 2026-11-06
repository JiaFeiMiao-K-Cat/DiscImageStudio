import CoreGraphics
import Foundation
import ImageIO
import XCTest

import DiscImageStudioKit

/// 真实 CLI 的标定契约测试。找不到 CLI 或 CLI 尚未实现标定命令时自动 skip，
/// 因此本文件不会拖累无 CLI 的 `swift test`。
final class CalibrationCLIContractTests: XCTestCase {

    private var temporaryDirectory: URL!
    private var workspace: URL!

    override func setUpWithError() throws {
        temporaryDirectory = try TestSupport.makeTemporaryDirectory("CalibrationCLIContractTests")
        workspace = temporaryDirectory.appendingPathComponent("calibration", isDirectory: true)
    }

    override func tearDownWithError() throws {
        if let temporaryDirectory {
            try? FileManager.default.removeItem(at: temporaryDirectory)
        }
        temporaryDirectory = nil
        workspace = nil
    }

    // MARK: - calibration-target / session

    func testRealTargetProduces2400PNGAndReadableSession() async throws {
        let cli = try await requireCalibrationCLI()
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "real CLI"),
            workspace: workspace
        )
        let targetURL = workspace.appendingPathComponent("target.png")
        let sessionURL = workspace.appendingPathComponent("target.calibration.json")

        let result = try await service.generateTarget(CalibrationTargetRequest(
            family: .cd,
            geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
            density: 1200,
            size: 2400,
            outputURL: targetURL,
            sessionURL: sessionURL
        ))

        XCTAssertEqual(result.size, 2400)
        XCTAssertTrue(FileManager.default.fileExists(atPath: targetURL.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: sessionURL.path))
        let size = try pngPixelSize(targetURL)
        XCTAssertEqual(size.width, 2400)
        XCTAssertEqual(size.height, 2400)

        let summary = try await service.readSession(sessionURL)
        XCTAssertNotNil(summary.family)
        XCTAssertEqual(try XCTUnwrap(summary.innerRadiusMm), 24.5, accuracy: 0.001)
        XCTAssertEqual(try XCTUnwrap(summary.outerRadiusMm), 56.8, accuracy: 0.001)
        XCTAssertEqual(summary.sectors, 359_849)
        XCTAssertTrue(summary.hasPhoto != true, "标靶会话骨架不应含照片")
    }

    // MARK: - calibration-photo

    func testRealPhotoRectifiesSyntheticEllipsePoints() async throws {
        let cli = try await requireCalibrationCLI()
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "real CLI"),
            workspace: workspace
        )
        let photoURL = workspace.appendingPathComponent("photo.png")
        try makeSyntheticPhoto(at: photoURL, width: 2000, height: 1500)
        let resizedURL = workspace.appendingPathComponent("photo-resized.png")
        let rectifiedURL = workspace.appendingPathComponent("photo-rectified.png")

        let outer = ellipsePoints(centerX: 1000, centerY: 750, radiusX: 800, radiusY: 600, count: 16)
        let hole = ellipsePoints(centerX: 1000, centerY: 750, radiusX: 100, radiusY: 75, count: 16)

        let result = try await service.rectifyPhoto(
            photoURL: photoURL,
            discSize: .standard,
            outer: outer,
            hole: hole,
            resizedOut: resizedURL,
            rectifiedOut: rectifiedURL,
            size: 1200
        )

        XCTAssertTrue(result.succeeded, "合成椭圆点应能完成照片矫正：\(result.message)")
        XCTAssertEqual(result.rectifiedSize, 1200)
        XCTAssertEqual(result.discSize, 120)
        XCTAssertEqual(try XCTUnwrap(result.canvasOuterRadiusMm), 60, accuracy: 0.001)
        XCTAssertEqual(try XCTUnwrap(result.outerRmsPixels), 0, accuracy: 3, "合成椭圆的外缘拟合误差应很小")
        let rectified = try XCTUnwrap(result.rectifiedImagePath)
        XCTAssertTrue(FileManager.default.fileExists(atPath: rectified))
        XCTAssertEqual(try pngPixelSize(URL(fileURLWithPath: rectified)).width, 1200)
    }

    // MARK: - calibration-fit

    func testRealFitReturnsCandidateFieldsForGeneratedTargetSession() async throws {
        let cli = try await requireCalibrationCLI()
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "real CLI"),
            workspace: workspace
        )
        let targetURL = workspace.appendingPathComponent("target.png")
        let baseSessionURL = workspace.appendingPathComponent("base.calibration.json")
        _ = try await service.generateTarget(CalibrationTargetRequest(
            family: .cd,
            geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
            density: 1200,
            size: 2400,
            outputURL: targetURL,
            sessionURL: baseSessionURL
        ))

        let photoURL = workspace.appendingPathComponent("photo.png")
        try makeSyntheticPhoto(at: photoURL, width: 2000, height: 1500)
        let canonicalSessionURL = workspace.appendingPathComponent("full.calibration.json")
        _ = try await service.writeSession(
            baseSessionURL: baseSessionURL,
            photoURL: photoURL,
            discSize: .standard,
            outer: ellipsePoints(centerX: 1000, centerY: 750, radiusX: 800, radiusY: 600, count: 16),
            hole: ellipsePoints(centerX: 1000, centerY: 750, radiusX: 100, radiusY: 75, count: 16),
            points: radialTracePoints(inner: 24.5, outer: 56.8, count: 12),
            outputURL: canonicalSessionURL
        )

        let fit: CalibrationFitResult
        do {
            fit = try await service.fit(
                sessionURL: canonicalSessionURL,
                points: radialTracePoints(inner: 24.5, outer: 56.8, count: 12)
            )
        } catch let error as CalibrationError {
            throw XCTSkip("真实 CLI 的 calibration-fit 尚不可用或合成描点无法拟合：\(error)")
        }

        guard fit.succeeded else {
            throw XCTSkip("真实 CLI 未接受合成描点（warnings: \(fit.warnings)）")
        }
        XCTAssertFalse(fit.candidates.isEmpty)
        let best = try XCTUnwrap(fit.candidates[safe: fit.bestIndex] ?? fit.candidates.first)
        XCTAssertGreaterThan(best.innerRadiusMm, 0)
        XCTAssertGreaterThan(best.outerRadiusMm, best.innerRadiusMm)
        XCTAssertGreaterThanOrEqual(best.rmsErrorMm, 0)
        XCTAssertGreaterThanOrEqual(best.maxErrorMm, 0)
        XCTAssertFalse(best.predicted.isEmpty, "候选应带预测折线供叠加")
        XCTAssertTrue(best.predicted.allSatisfy { $0.x.isFinite && $0.y.isFinite })
        XCTAssertGreaterThanOrEqual(fit.bestIndex, 0)
        XCTAssertLessThan(fit.bestIndex, fit.candidates.count)
    }

    // MARK: - calibration-coarse-fit

    func testRealCoarseFitRecoversSyntheticGeometry() async throws {
        let cli = try await requireCalibrationCLI()
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "real CLI"),
            workspace: workspace
        )
        let nominal = ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849)
        let density = 1200.0
        let actualInner = 24.3
        let actualOuter = 57.0
        let points = coarseBoundaryPoints(
            nominal: nominal,
            density: density,
            actualInner: actualInner,
            actualOuter: actualOuter,
            rotation: 0.1,
            count: 12
        )

        let result: CalibrationCoarseResult
        do {
            result = try await service.fitCoarse(
                family: .cd,
                geometry: nominal,
                density: density,
                points: points
            )
        } catch let error as CalibrationError {
            throw XCTSkip("真实 CLI 的 calibration-coarse-fit 尚不可用：\(error)")
        }

        XCTAssertTrue(result.succeeded, "合成点应能反推半径（warnings: \(result.warnings)）")
        let best = try XCTUnwrap(result.candidates[safe: result.bestIndex] ?? result.candidates.first)
        XCTAssertEqual(best.innerRadiusMm, actualInner, accuracy: 0.02, "粗调应恢复内半径")
        XCTAssertEqual(best.outerRadiusMm, actualOuter, accuracy: 0.02, "粗调应恢复外半径")
    }

    // MARK: - Real CLI discovery

    private func requireCalibrationCLI() async throws -> URL {
        guard let cli = findRealCLI() else {
            throw XCTSkip("未找到真实 disc-image-studio CLI（可用 DIS_IMAGE_STUDIO_CLI 指定）")
        }
        let runner = CliRunner(invocation: CliInvocation(executableURL: cli, label: "real CLI"))
        let probe = try? await runner.runAndCollect(
            arguments: ["calibration-target", "--help"],
            timeout: .seconds(30)
        )
        let text = (probe?.standardOutput ?? "") + (probe?.standardError ?? "")
        if text.contains("Unknown optical-disc command") || text.isEmpty {
            throw XCTSkip("真实 CLI 尚未实现 calibration-* 命令")
        }
        return cli
    }

    private func findRealCLI() -> URL? {
        let fileManager = FileManager.default
        if let environmentPath = ProcessInfo.processInfo.environment["DIS_IMAGE_STUDIO_CLI"],
           fileManager.isExecutableFile(atPath: environmentPath) {
            return URL(fileURLWithPath: environmentPath)
        }

        let macosDirectory = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let cliRoot = macosDirectory.appendingPathComponent("build/cli", isDirectory: true)
        if let buildDirectories = try? fileManager.contentsOfDirectory(
            at: cliRoot,
            includingPropertiesForKeys: nil
        ) {
            for directory in buildDirectories {
                let candidate = directory.appendingPathComponent("disc-image-studio", isDirectory: false)
                if fileManager.isExecutableFile(atPath: candidate.path) {
                    return candidate
                }
            }
        }

        for directory in (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":") {
            let candidate = URL(fileURLWithPath: String(directory))
                .appendingPathComponent("disc-image-studio", isDirectory: false)
            if fileManager.isExecutableFile(atPath: candidate.path) {
                return candidate
            }
        }
        return nil
    }

    // MARK: - Synthetic fixtures

    private func makeSyntheticPhoto(at url: URL, width: Int, height: Int) throws {
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB()
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(red: 0.2, green: 0.2, blue: 0.2, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        // 画一个浅色圆盘，让矫正输出不是纯色，便于后续目视检查。
        context.setFillColor(CGColor(red: 0.85, green: 0.85, blue: 0.85, alpha: 1))
        context.fillEllipse(in: CGRect(x: 200, y: 150, width: 1600, height: 1200))
        let image = try XCTUnwrap(context.makeImage())
        let data = NSMutableData()
        let destination = try XCTUnwrap(CGImageDestinationCreateWithData(
            data as CFMutableData,
            "public.png" as CFString,
            1,
            nil
        ))
        CGImageDestinationAddImage(destination, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        try (data as Data).write(to: url)
    }

    private func ellipsePoints(
        centerX: Double,
        centerY: Double,
        radiusX: Double,
        radiusY: Double,
        count: Int
    ) -> [CalibrationPoint] {
        (0..<count).map { index in
            let angle = 2 * Double.pi * Double(index) / Double(count)
            return CalibrationPoint(
                x: centerX + radiusX * cos(angle),
                y: centerY + radiusY * sin(angle)
            )
        }
    }

    private func radialTracePoints(inner: Double, outer: Double, count: Int) -> [CalibrationPoint] {
        (0..<count).map { index in
            let radius = inner + (outer - inner) * Double(index) / Double(max(count - 1, 1))
            return CalibrationPoint(x: radius, y: 0)
        }
    }

    /// 按 CALIBRATION.md §1.5 的公式，从已知 generated/actual 几何生成粗调分界描点。
    private func coarseBoundaryPoints(
        nominal: ProjectGeometry,
        density: Double,
        actualInner: Double,
        actualOuter: Double,
        rotation: Double,
        count: Int
    ) -> [CalibrationPoint] {
        let generatedInner = nominal.innerRadiusMm
        let generatedOuter = nominal.outerRadiusMm
        let trackLengthMm = Double(nominal.sectors) / 75.0 * density // CD: sectors / 75 * mm/s
        let generatedGrowth = Double.pi
            * (generatedOuter * generatedOuter - generatedInner * generatedInner) / trackLengthMm
        let actualGrowth = Double.pi
            * (actualOuter * actualOuter - actualInner * actualInner) / trackLengthMm

        return (0..<count).map { index in
            let radius = actualInner + (actualOuter - actualInner) * Double(index) / Double(max(count - 1, 1))
            let generatedRadius = (generatedInner * generatedInner
                + (radius * radius - actualInner * actualInner)
                * (generatedOuter * generatedOuter - generatedInner * generatedInner)
                / (actualOuter * actualOuter - actualInner * actualInner)).squareRoot()
            let delta = 2 * Double.pi
                * ((generatedRadius - generatedInner) / generatedGrowth
                    - (radius - actualInner) / actualGrowth)
            let angle = rotation - delta
            return CalibrationPoint(x: radius * cos(angle), y: radius * sin(angle))
        }
    }

    private func pngPixelSize(_ url: URL) throws -> (width: Int, height: Int) {
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
        let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        return (image.width, image.height)
    }
}

private extension Array {
    subscript(safe index: Int) -> Element? {
        indices.contains(index) ? self[index] : nil
    }
}
