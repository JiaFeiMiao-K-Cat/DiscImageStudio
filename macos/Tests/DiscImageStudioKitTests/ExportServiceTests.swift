import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住四类工程的导出参数（WORKFLOW.md §5 / WPF `ExportViewModel`）。
final class ExportServiceTests: XCTestCase {

    private var temporaryDirectory: URL!
    private var compositeURL: URL!

    override func setUpWithError() throws {
        temporaryDirectory = try TestSupport.makeTemporaryDirectory("ExportServiceTests")
        compositeURL = temporaryDirectory.appendingPathComponent("composite.png")
        try Data("png".utf8).write(to: compositeURL)
    }

    override func tearDownWithError() throws {
        if let temporaryDirectory {
            try? FileManager.default.removeItem(at: temporaryDirectory)
        }
        temporaryDirectory = nil
        compositeURL = nil
    }

    // MARK: - CD 音轨

    func testCDAudioPlanMatchesWindowsArguments() throws {
        let project = makeProject(
            family: .cd,
            geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
            outputs: ProjectOutputs(outputPath: "/tmp/track.wav", writeCue: true, interleave: true),
            tone: ProjectTone(ditherStrength: 0.75, levels: 2, brightness: -3, srgbLinearization: true)
        )

        let plan = try ExportService.makePlan(project: project, compositeURL: compositeURL)
        XCTAssertEqual(plan.family, .cd)
        XCTAssertEqual(plan.outputURL.path, "/tmp/track.wav")
        XCTAssertEqual(
            plan.arguments,
            [
                "cd-generate",
                "--input", compositeURL.path,
                "--output", "/tmp/track.wav",
                "--r0", "24.5",
                "--r1", "56.8",
                "--sectors", "359849",
                "--tone-dither", "0.75",
                "--tone-levels", "2",
                "--tone-brightness", "-3",
                "--tone-gamma", "true",
                "--interleave", "true",
                "--cue", "true",
            ]
        )
    }

    func testCDDataPlanUsesDataDirectoryAndVolumeLabel() throws {
        let project = makeProject(
            family: .cd,
            geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
            outputs: ProjectOutputs(
                outputPath: "/tmp/cd.iso",
                writeCue: false,
                dataDirectory: "/tmp/files",
                volumeLabel: "MY_CD",
                interleave: false,
                filesAtOuterRing: true
            ),
            tone: ProjectTone(ditherStrength: 0, levels: 4, brightness: 0, srgbLinearization: false)
        )

        let plan = try ExportService.makePlan(project: project, compositeURL: compositeURL)
        XCTAssertEqual(
            plan.arguments,
            [
                "cd-generate",
                "--input", compositeURL.path,
                "--output", "/tmp/cd.iso",
                "--r0", "24.5",
                "--r1", "56.8",
                "--sectors", "359849",
                "--tone-dither", "0",
                "--tone-levels", "4",
                "--tone-brightness", "0",
                "--tone-gamma", "false",
                "--data-dir", "/tmp/files",
                "--volume-label", "MY_CD",
                "--file-position", "outer",
            ]
        )
    }

    func testCDWithoutToneFallsBackToLegacyBinaryTone() throws {
        let project = makeProject(
            family: .cd,
            geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
            outputs: ProjectOutputs(outputPath: "/tmp/track.wav"),
            tone: nil
        )
        let plan = try ExportService.makePlan(project: project, compositeURL: compositeURL)
        XCTAssertEqual(plan.arguments.suffix(4), ["--interleave", "true", "--cue", "true"])
        XCTAssertEqual(Array(plan.arguments[11...14]), ["--tone-dither", "0", "--tone-levels", "2"])
        XCTAssertEqual(plan.arguments[15], "--tone-brightness")
        XCTAssertEqual(plan.arguments[16], "0")
        XCTAssertEqual(plan.arguments[17], "--tone-gamma")
        XCTAssertEqual(plan.arguments[18], "false")
    }

    // MARK: - DVD

    func testDVDPlanMatchesWindowsArgumentsWithFillSectors() throws {
        let project = makeProject(
            family: .dvd,
            geometry: ProjectGeometry(
                innerRadiusMm: 24.0,
                outerRadiusMm: 58.0,
                sectors: 2_295_104,
                pitchLinear: 0.001,
                pitchQuadratic: -0.002,
                pitchCubic: 0.003
            ),
            outputs: ProjectOutputs(outputPath: "/tmp/dvd.iso", volumeLabel: ""),
            tone: ProjectTone(ditherStrength: 0.75, levels: 4, brightness: -3, srgbLinearization: true)
        )

        let plan = try ExportService.makePlan(project: project, compositeURL: compositeURL)
        XCTAssertEqual(
            plan.arguments,
            [
                "solve",
                "--image", compositeURL.path,
                "--iso-output", "/tmp/dvd.iso",
                "--total-sectors", "2295104",
                "--inner-radius-mm", "24",
                "--outer-radius-mm", "58",
                "--pitch-linear", "0.001",
                "--pitch-quadratic", "-0.002",
                "--pitch-cubic", "0.003",
                "--spiral-direction", "cw",
                "--image-threshold", "128",
                "--alpha-threshold", "1",
                "--tone-dither", "0.75",
                "--tone-levels", "2",
                "--tone-brightness", "-3",
                "--tone-gamma", "true",
                "--algorithm", "dispersion",
                "--iterations-per-block", "1",
                "--fast-output", "true",
                "--volume-label", "DISC_IMAGE",
                "--fill-sectors", "2295104",
            ]
        )
    }

    func testDVDPlanWithBundledFilesUsesDataDir() throws {
        let project = makeProject(
            family: .dvd,
            geometry: ProjectGeometry(innerRadiusMm: 24.0, outerRadiusMm: 58.0, sectors: 2_295_104),
            outputs: ProjectOutputs(
                outputPath: "/tmp/dvd.iso",
                dataDirectory: "/tmp/dvd-data",
                volumeLabel: "MY_DVD",
                filesAtOuterRing: true
            ),
            tone: ProjectTone()
        )
        let plan = try ExportService.makePlan(project: project, compositeURL: compositeURL)
        XCTAssertEqual(plan.arguments.suffix(4), ["--data-dir", "/tmp/dvd-data", "--file-position", "outer"])
        XCTAssertFalse(plan.arguments.contains("--fill-sectors"))
        XCTAssertTrue(plan.arguments.contains("MY_DVD"))
    }

    // MARK: - BD

    func testBDPlanMatchesWindowsArguments() throws {
        let project = makeProject(
            family: .bd,
            geometry: ProjectGeometry(innerRadiusMm: 24.0, outerRadiusMm: 58.0, sectors: 12_219_392),
            outputs: ProjectOutputs(outputPath: "/tmp/bd.iso", volumeLabel: "MY_BD"),
            tone: ProjectTone(ditherStrength: 0.25, levels: 4, brightness: 5, srgbLinearization: false),
            invertMapping: true
        )

        let plan = try ExportService.makePlan(project: project, compositeURL: compositeURL)
        XCTAssertEqual(plan.family, .bd)
        XCTAssertEqual(
            plan.arguments,
            [
                "bd-generate",
                "--input", compositeURL.path,
                "--output", "/tmp/bd.iso",
                "--total-sectors", "12219392",
                "--r0", "24",
                "--r1", "58",
                "--invert", "true",
                "--volume-label", "MY_BD",
                "--tone-dither", "0.25",
                "--tone-levels", "2",
                "--tone-brightness", "5",
                "--tone-gamma", "false",
            ]
        )
    }

    func testBDPlanWithBundledFilesAppendsDataDir() throws {
        let project = makeProject(
            family: .bd,
            geometry: ProjectGeometry(innerRadiusMm: 24.0, outerRadiusMm: 58.0, sectors: 12_219_392),
            outputs: ProjectOutputs(
                outputPath: "/tmp/bd.iso",
                dataDirectory: "/tmp/bd-data",
                volumeLabel: "BD",
                filesAtOuterRing: false
            ),
            tone: ProjectTone()
        )
        let plan = try ExportService.makePlan(project: project, compositeURL: compositeURL)
        XCTAssertEqual(plan.arguments.suffix(4), ["--data-dir", "/tmp/bd-data", "--file-position", "inner"])
        XCTAssertFalse(plan.arguments.contains("--fill-sectors"))
    }

    // MARK: - 输出与校验

    func testOutputURLOverrideAndExtensionRules() throws {
        let project = makeProject(
            family: .cd,
            geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
            outputs: ProjectOutputs(outputPath: "/tmp/original.wav"),
            tone: ProjectTone()
        )

        let override = temporaryDirectory.appendingPathComponent("override.RAW")
        let plan = try ExportService.makePlan(
            project: project,
            compositeURL: compositeURL,
            outputURL: override
        )
        XCTAssertEqual(plan.outputURL.path, override.path)
        XCTAssertEqual(plan.arguments[4], override.path)

        var badExtension = project
        badExtension.outputs.outputPath = "/tmp/track.iso"
        XCTAssertThrowsError(try ExportService.makePlan(project: badExtension, compositeURL: compositeURL)) { error in
            guard case ExportError.unsupportedExtension(let expected, let actual) = error else {
                return XCTFail("CD 音轨使用 .iso 应抛 unsupportedExtension，实际是 \(error)")
            }
            XCTAssertEqual(expected, "wav / raw")
            XCTAssertEqual(actual, "iso")
        }

        var dataProject = project
        dataProject.outputs.dataDirectory = "/tmp/files"
        dataProject.outputs.outputPath = "/tmp/cd.wav"
        XCTAssertThrowsError(try ExportService.makePlan(project: dataProject, compositeURL: compositeURL)) { error in
            guard case ExportError.unsupportedExtension(let expected, _) = error else {
                return XCTFail("CD 数据盘应要求 .iso，实际是 \(error)")
            }
            XCTAssertEqual(expected, "iso")
        }
    }

    func testMissingAndInvalidOutputsAreRejected() throws {
        var project = makeProject(
            family: .dvd,
            geometry: ProjectGeometry(innerRadiusMm: 24.0, outerRadiusMm: 58.0, sectors: 2_295_104),
            outputs: ProjectOutputs(outputPath: "   "),
            tone: ProjectTone()
        )
        XCTAssertThrowsError(try ExportService.makePlan(project: project, compositeURL: compositeURL)) { error in
            XCTAssertEqual(error as? ExportError, .missingOutput)
        }

        project.outputs.outputPath = "/tmp/dvd.iso"
        project.geometry.outerRadiusMm = 20
        XCTAssertThrowsError(try ExportService.makePlan(project: project, compositeURL: compositeURL)) { error in
            guard case ExportError.invalidGeometry = error else {
                return XCTFail("外半径小于内半径应抛 invalidGeometry，实际是 \(error)")
            }
        }
    }

    // MARK: - Helpers

    private func makeProject(
        family: DiscFamily,
        geometry: ProjectGeometry,
        outputs: ProjectOutputs,
        tone: ProjectTone?,
        invertMapping: Bool = false
    ) -> ProjectDocument {
        ProjectDocument(
            name: "导出测试",
            discFamily: family,
            geometry: geometry,
            layers: [],
            outputs: outputs,
            tone: tone,
            bluRay: family == .bd ? ProjectBluRaySettings(invertMapping: invertMapping) : nil
        )
    }
}
