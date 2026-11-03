import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住六种预览组合的 CLI 参数（WORKFLOW.md §5 / WPF `LivePreviewViewModel`）。
final class PreviewServiceTests: XCTestCase {

    private var temporaryDirectory: URL!
    private var compositeURL: URL!
    private var outputURL: URL!
    private var artifactURL: URL!

    override func setUpWithError() throws {
        temporaryDirectory = try TestSupport.makeTemporaryDirectory("PreviewServiceTests")
        compositeURL = temporaryDirectory.appendingPathComponent("composite.png")
        outputURL = temporaryDirectory.appendingPathComponent("preview.png")
        artifactURL = temporaryDirectory.appendingPathComponent("artifact.iso")
        try Data("data".utf8).write(to: compositeURL)
        try Data("data".utf8).write(to: artifactURL)
    }

    override func tearDownWithError() throws {
        if let temporaryDirectory {
            try? FileManager.default.removeItem(at: temporaryDirectory)
        }
        temporaryDirectory = nil
        compositeURL = nil
        outputURL = nil
        artifactURL = nil
    }

    // MARK: - CD

    func testCDDesignProjectionArguments() throws {
        let project = makeCDProject()
        let request = PreviewRequest(
            mode: .designProjection,
            previewSize: 1600,
            samplesPerSector: 16,
            actualInnerRadiusMm: 24.3,
            actualOuterRadiusMm: 56.6
        )

        let plan = try PreviewService.makePlan(
            project: project,
            compositeURL: compositeURL,
            request: request,
            outputURL: outputURL
        )
        XCTAssertEqual(plan.family, .cd)
        XCTAssertEqual(plan.mode, .designProjection)
        XCTAssertEqual(
            plan.arguments,
            [
                "cd-preview-warp",
                "--input", compositeURL.path,
                "--output", outputURL.path,
                "--r0", "24.5",
                "--r1", "56.8",
                "--sectors", "359849",
                "--velocity", "1200",
                "--theta0", "0",
                "--outer", "57.5",
                "--actual-r0", "24.3",
                "--actual-r1", "56.6",
                "--tone-dither", "0.75",
                "--tone-levels", "2",
                "--tone-brightness", "-3",
                "--tone-gamma", "true",
                "--size", "1600",
                "--samples-per-sector", "16",
            ]
        )
    }

    func testCDDesignProjectionWithBundledFiles() throws {
        var project = makeCDProject()
        project.outputs.dataDirectory = "/tmp/cd-files"
        project.outputs.volumeLabel = "CD_DATA"
        project.outputs.filesAtOuterRing = true
        let request = PreviewRequest(
            actualInnerRadiusMm: 24.3,
            actualOuterRadiusMm: 56.6
        )
        let plan = try PreviewService.makePlan(
            project: project,
            compositeURL: compositeURL,
            request: request,
            outputURL: outputURL
        )
        XCTAssertEqual(
            plan.arguments.suffix(6),
            ["--data-dir", "/tmp/cd-files", "--volume-label", "CD_DATA", "--file-position", "outer"]
        )
    }

    func testCDArtifactReadbackArguments() throws {
        let project = makeCDProject()
        let request = PreviewRequest(
            mode: .artifactReadback,
            previewSize: 1600,
            samplesPerSector: 16,
            actualInnerRadiusMm: 24.3,
            actualOuterRadiusMm: 56.6,
            artifactURL: artifactURL,
            deinterleave: false,
            byteStep: 24
        )
        let plan = try PreviewService.makePlan(
            project: project,
            compositeURL: compositeURL,
            request: request,
            outputURL: outputURL
        )
        XCTAssertEqual(
            plan.arguments,
            [
                "cd-preview-track",
                "--track", artifactURL.path,
                "--output", outputURL.path,
                "--r0", "24.5",
                "--r1", "56.8",
                "--sectors", "359849",
                "--velocity", "1200",
                "--theta0", "0",
                "--outer", "57.5",
                "--size", "1600",
                "--byte-step", "24",
                "--deinterleave", "false",
            ]
        )
    }

    // MARK: - DVD

    func testDVDDesignProjectionArguments() throws {
        let project = makeDVDProject()
        let request = PreviewRequest(
            mode: .designProjection,
            previewSize: 1200,
            samplesPerSector: 16,
            actualInnerRadiusMm: 23.9,
            actualOuterRadiusMm: 57.9,
            actualPitchLinear: 0.004,
            actualPitchQuadratic: -0.005,
            actualPitchCubic: 0.006
        )

        let plan = try PreviewService.makePlan(
            project: project,
            compositeURL: compositeURL,
            request: request,
            outputURL: outputURL
        )
        XCTAssertEqual(plan.family, .dvd)
        XCTAssertEqual(
            plan.arguments,
            [
                "calibrate",
                "--image", compositeURL.path,
                "--output", outputURL.path,
                "--total-sectors", "2295104",
                "--lba", "0",
                "--fill-sectors", "2295104",
                "--generated-inner-radius-mm", "24",
                "--generated-outer-radius-mm", "58",
                "--actual-inner-radius-mm", "23.9",
                "--actual-outer-radius-mm", "57.9",
                "--pitch-linear", "0.001",
                "--pitch-quadratic", "-0.002",
                "--pitch-cubic", "0.003",
                "--actual-pitch-linear", "0.004",
                "--actual-pitch-quadratic", "-0.005",
                "--actual-pitch-cubic", "0.006",
                "--spiral-direction", "cw",
                "--image-threshold", "128",
                "--alpha-threshold", "1",
                "--tone-dither", "0.75",
                "--tone-levels", "2",
                "--tone-brightness", "-3",
                "--tone-gamma", "true",
                "--preview-size", "1200",
                "--samples-per-sector", "16",
            ]
        )
    }

    func testDVDArtifactReadbackOmitsToneArguments() throws {
        let project = makeDVDProject()
        let request = PreviewRequest(
            mode: .artifactReadback,
            previewSize: 1200,
            samplesPerSector: 16,
            actualInnerRadiusMm: 23.9,
            actualOuterRadiusMm: 57.9,
            artifactURL: artifactURL
        )
        let plan = try PreviewService.makePlan(
            project: project,
            compositeURL: compositeURL,
            request: request,
            outputURL: outputURL
        )
        XCTAssertEqual(
            plan.arguments,
            [
                "simulate",
                "--iso", artifactURL.path,
                "--output", outputURL.path,
                "--total-sectors", "2295104",
                "--inner-radius-mm", "23.9",
                "--outer-radius-mm", "57.9",
                "--pitch-linear", "0.001",
                "--pitch-quadratic", "-0.002",
                "--pitch-cubic", "0.003",
                "--spiral-direction", "cw",
                "--preview-size", "1200",
                "--samples-per-sector", "16",
            ]
        )
        XCTAssertFalse(plan.arguments.contains { $0.hasPrefix("--tone-") }, "WPF simulate 不带 tone 参数")
    }

    // MARK: - BD

    func testBDDesignProjectionArguments() throws {
        let project = makeBDProject()
        let request = PreviewRequest(
            mode: .designProjection,
            previewSize: 2048,
            samplesPerSector: 16,
            actualInnerRadiusMm: 23.9,
            actualOuterRadiusMm: 57.9,
            invertMapping: true
        )

        let plan = try PreviewService.makePlan(
            project: project,
            compositeURL: compositeURL,
            request: request,
            outputURL: outputURL
        )
        XCTAssertEqual(plan.family, .bd)
        XCTAssertEqual(
            plan.arguments,
            [
                "bd-preview",
                "--input", compositeURL.path,
                "--output", outputURL.path,
                "--total-sectors", "12219392",
                "--r0", "24",
                "--r1", "58",
                "--actual-r0", "23.9",
                "--actual-r1", "57.9",
                "--invert", "true",
                "--tone-dither", "0.75",
                "--tone-levels", "2",
                "--tone-brightness", "-3",
                "--tone-gamma", "true",
                "--size", "2048",
            ]
        )
    }

    func testBDArtifactReadbackArguments() throws {
        let project = makeBDProject()
        let request = PreviewRequest(
            mode: .artifactReadback,
            previewSize: 2048,
            samplesPerSector: 16,
            actualInnerRadiusMm: 23.9,
            actualOuterRadiusMm: 57.9,
            artifactURL: artifactURL
        )
        let plan = try PreviewService.makePlan(
            project: project,
            compositeURL: compositeURL,
            request: request,
            outputURL: outputURL
        )
        XCTAssertEqual(
            plan.arguments,
            [
                "bd-preview-readback",
                "--iso", artifactURL.path,
                "--output", outputURL.path,
                "--size", "2048",
            ]
        )
    }

    // MARK: - 默认值与校验

    func testDefaultRequestFollowsFamilyDefaults() throws {
        let base = PreviewRequest(actualInnerRadiusMm: 1, actualOuterRadiusMm: 2)
        let cd = base.defaultRequest(for: makeCDProject())
        XCTAssertEqual(cd.previewSize, 1600)
        XCTAssertEqual(cd.samplesPerSector, 16)
        XCTAssertEqual(cd.actualInnerRadiusMm, 24.5, accuracy: 1e-9)
        XCTAssertEqual(cd.actualOuterRadiusMm, 56.8, accuracy: 1e-9)
        XCTAssertEqual(cd.mode, .designProjection)

        let dvd = base.defaultRequest(for: makeDVDProject())
        XCTAssertEqual(dvd.previewSize, 1200)
        XCTAssertEqual(dvd.actualPitchLinear, 0.001, accuracy: 1e-12)

        let bd = base.defaultRequest(for: makeBDProject())
        XCTAssertEqual(bd.previewSize, 2048)
        XCTAssertTrue(bd.invertMapping)
    }

    func testPreviewSizeAndSamplesAreClamped() {
        XCTAssertEqual(PreviewService.clampPreviewSize(100, for: .cd), 256)
        XCTAssertEqual(PreviewService.clampPreviewSize(100, for: .dvd), 256)
        XCTAssertEqual(PreviewService.clampPreviewSize(100, for: .bd), 100, "BD 下限为 64")
        XCTAssertEqual(PreviewService.clampPreviewSize(9000, for: .bd), 8192)

        let project = makeCDProject()
        let request = PreviewRequest(
            previewSize: 100,
            samplesPerSector: 99,
            actualInnerRadiusMm: 24.3,
            actualOuterRadiusMm: 56.6
        )
        let plan = try? PreviewService.makePlan(
            project: project,
            compositeURL: compositeURL,
            request: request,
            outputURL: outputURL
        )
        XCTAssertEqual(plan?.arguments.last, "16")
        XCTAssertTrue(plan?.arguments.contains("256") == true)
    }

    func testInvalidActualRadiiAreRejected() throws {
        let project = makeCDProject()

        let zeroInner = PreviewRequest(
            actualInnerRadiusMm: 0,
            actualOuterRadiusMm: 56.6
        )
        XCTAssertThrowsError(try PreviewService.makePlan(
            project: project,
            compositeURL: compositeURL,
            request: zeroInner,
            outputURL: outputURL
        )) { error in
            guard case PreviewError.invalidGeometry = error else {
                return XCTFail("实测内半径 0 应抛 invalidGeometry，实际是 \(error)")
            }
        }

        let inverted = PreviewRequest(
            actualInnerRadiusMm: 57,
            actualOuterRadiusMm: 56
        )
        XCTAssertThrowsError(try PreviewService.makePlan(
            project: project,
            compositeURL: compositeURL,
            request: inverted,
            outputURL: outputURL
        )) { error in
            guard case PreviewError.invalidGeometry = error else {
                return XCTFail("实测内半径大于外半径应抛 invalidGeometry，实际是 \(error)")
            }
        }

        var badProject = project
        badProject.geometry.outerRadiusMm = 20
        let validRequest = PreviewRequest(actualInnerRadiusMm: 24.3, actualOuterRadiusMm: 56.6)
        XCTAssertThrowsError(try PreviewService.makePlan(
            project: badProject,
            compositeURL: compositeURL,
            request: validRequest,
            outputURL: outputURL
        )) { error in
            guard case PreviewError.invalidGeometry = error else {
                return XCTFail("生成几何无效应抛 invalidGeometry，实际是 \(error)")
            }
        }
    }

    func testArtifactReadbackRequiresArtifact() throws {
        for family in [DiscFamily.cd, .dvd, .bd] {
            let project: ProjectDocument
            switch family {
            case .cd: project = makeCDProject()
            case .dvd: project = makeDVDProject()
            case .bd: project = makeBDProject()
            }
            let request = PreviewRequest(
                mode: .artifactReadback,
                previewSize: 1600,
                actualInnerRadiusMm: 24.3,
                actualOuterRadiusMm: 56.6,
                artifactURL: nil
            )
            XCTAssertThrowsError(try PreviewService.makePlan(
                project: project,
                compositeURL: compositeURL,
                request: request,
                outputURL: outputURL
            )) { error in
                XCTAssertEqual(error as? PreviewError, .missingArtifact)
            }
        }
    }

    // MARK: - Helpers

    private func makeCDProject() -> ProjectDocument {
        ProjectDocument(
            name: "CD 工程",
            discFamily: .cd,
            geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
            layers: [],
            outputs: ProjectOutputs(outputPath: "/tmp/cd.wav"),
            tone: ProjectTone(ditherStrength: 0.75, levels: 2, brightness: -3, srgbLinearization: true)
        )
    }

    private func makeDVDProject() -> ProjectDocument {
        ProjectDocument(
            name: "DVD 工程",
            discFamily: .dvd,
            geometry: ProjectGeometry(
                innerRadiusMm: 24.0,
                outerRadiusMm: 58.0,
                sectors: 2_295_104,
                pitchLinear: 0.001,
                pitchQuadratic: -0.002,
                pitchCubic: 0.003
            ),
            layers: [],
            outputs: ProjectOutputs(outputPath: "/tmp/dvd.iso"),
            tone: ProjectTone(ditherStrength: 0.75, levels: 2, brightness: -3, srgbLinearization: true)
        )
    }

    private func makeBDProject() -> ProjectDocument {
        ProjectDocument(
            name: "BD 工程",
            discFamily: .bd,
            geometry: ProjectGeometry(innerRadiusMm: 24.0, outerRadiusMm: 58.0, sectors: 12_219_392),
            layers: [],
            outputs: ProjectOutputs(outputPath: "/tmp/bd.iso"),
            tone: ProjectTone(ditherStrength: 0.75, levels: 4, brightness: -3, srgbLinearization: true),
            bluRay: ProjectBluRaySettings(invertMapping: true)
        )
    }
}
