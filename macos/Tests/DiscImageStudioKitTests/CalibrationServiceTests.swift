import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住标定命令的参数拼装与错误映射（macos/CALIBRATION.md §1）。
/// 全部用临时目录里的假 CLI 脚本记录 argv，不依赖真实 CLI。
final class CalibrationServiceTests: XCTestCase {

    private var temporaryDirectory: URL!
    private var workspace: URL!
    private var photoURL: URL!
    private var targetURL: URL!
    private var sessionURL: URL!

    override func setUpWithError() throws {
        temporaryDirectory = try TestSupport.makeTemporaryDirectory("CalibrationServiceTests")
        workspace = temporaryDirectory.appendingPathComponent("calibration", isDirectory: true)
        photoURL = temporaryDirectory.appendingPathComponent("photo.png")
        targetURL = temporaryDirectory.appendingPathComponent("target.png")
        sessionURL = temporaryDirectory.appendingPathComponent("base.calibration.json")
        try Data("data".utf8).write(to: photoURL)
        try Data("data".utf8).write(to: sessionURL)
    }

    override func tearDownWithError() throws {
        if let temporaryDirectory {
            try? FileManager.default.removeItem(at: temporaryDirectory)
        }
        temporaryDirectory = nil
        workspace = nil
        photoURL = nil
        targetURL = nil
        sessionURL = nil
    }

    // MARK: - calibration-target

    func testGenerateTargetCDArgumentsAndOutputs() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL)
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        let request = CalibrationTargetRequest(
            family: .cd,
            geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
            density: 1200,
            size: 2400,
            outputURL: targetURL,
            sessionURL: sessionURL
        )

        let result = try await service.generateTarget(request)
        XCTAssertEqual(result.size, 2400)
        XCTAssertEqual(result.family, .cd)
        XCTAssertEqual(result.imageURL, targetURL)
        XCTAssertEqual(result.sessionURL, sessionURL)
        XCTAssertTrue(FileManager.default.fileExists(atPath: targetURL.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: sessionURL.path))

        XCTAssertEqual(
            try readArgv(argvURL),
            [
                "calibration-target",
                "--family", "cd",
                "--r0", "24.5",
                "--r1", "56.8",
                "--sectors", "359849",
                "--density", "1200.0",
                "--size", "2400",
                "--output", targetURL.path,
                "--session", sessionURL.path,
            ]
        )
    }

    func testGenerateTargetDVDAndBDArguments() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL)
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )

        _ = try await service.generateTarget(CalibrationTargetRequest(
            family: .dvd,
            geometry: ProjectGeometry(
                innerRadiusMm: 24.0,
                outerRadiusMm: 58.0,
                sectors: 2_295_104,
                pitchLinear: 0.001,
                pitchQuadratic: -0.002,
                pitchCubic: 0.003
            ),
            density: 133.3,
            size: 2400,
            outputURL: targetURL,
            sessionURL: sessionURL
        ))
        XCTAssertEqual(
            try readArgv(argvURL).suffix(6),
            ["--pitch-linear", "0.001", "--pitch-quadratic", "-0.002", "--pitch-cubic", "0.003"]
        )

        try Data().write(to: argvURL)
        let bdTarget = temporaryDirectory.appendingPathComponent("bd-target.png")
        let bdSession = temporaryDirectory.appendingPathComponent("bd.calibration.json")
        _ = try await service.generateTarget(CalibrationTargetRequest(
            family: .bd,
            geometry: ProjectGeometry(innerRadiusMm: 24.0, outerRadiusMm: 58.0, sectors: 12_219_392),
            density: 74.5,
            invertMapping: true,
            drawingStart: 64,
            drawingEnd: 1000,
            size: 2400,
            outputURL: bdTarget,
            sessionURL: bdSession
        ))
        XCTAssertEqual(
            try readArgv(argvURL),
            [
                "calibration-target",
                "--family", "bd",
                "--r0", "24.0",
                "--r1", "58.0",
                "--sectors", "12219392",
                "--density", "74.5",
                "--size", "2400",
                "--output", bdTarget.path,
                "--session", bdSession.path,
                "--invert",
                "--drawing-start", "64",
                "--drawing-end", "1000",
            ]
        )
    }

    func testGenerateTargetRejectsInvalidGeometryBeforeRunningCLI() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL)
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        let request = CalibrationTargetRequest(
            family: .cd,
            geometry: ProjectGeometry(innerRadiusMm: 56.8, outerRadiusMm: 24.5, sectors: 359_849),
            density: 1200,
            outputURL: targetURL,
            sessionURL: sessionURL
        )
        do {
            _ = try await service.generateTarget(request)
            XCTFail("内半径大于外半径应抛 invalidGeometry")
        } catch let error as CalibrationError {
            guard case .invalidGeometry = error else {
                return XCTFail("应抛 invalidGeometry，实际是 \(error)")
            }
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: argvURL.path), "参数无效时不应调用 CLI")
    }

    func testGenerateTargetReportsMissingOutputs() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let noOutput = try makeFakeCLI(argvURL: argvURL, createTargetFiles: false)
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: noOutput, label: "fake CLI"),
            workspace: workspace
        )

        do {
            _ = try await service.generateTarget(CalibrationTargetRequest(
                family: .cd,
                geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
                density: 1200,
                outputURL: targetURL,
                sessionURL: sessionURL
            ))
            XCTFail("缺少输出文件应抛 commandFailed")
        } catch let error as CalibrationError {
            guard case .commandFailed(let reason) = error else {
                return XCTFail("应抛 commandFailed，实际是 \(error)")
            }
            XCTAssertEqual(reason, "标靶图片没有生成。")
        }
    }

    // MARK: - calibration-coarse-target

    func testGenerateCoarseTargetArguments() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL)
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        let coarseURL = temporaryDirectory.appendingPathComponent("coarse.png")
        let p0URL = temporaryDirectory.appendingPathComponent("p0.json")

        let result = try await service.generateCoarseTarget(
            family: .dvd,
            geometry: ProjectGeometry(
                innerRadiusMm: 24.0,
                outerRadiusMm: 58.0,
                sectors: 2_295_104,
                pitchLinear: 0.001,
                pitchQuadratic: -0.002,
                pitchCubic: 0.003
            ),
            density: 133.3,
            outputURL: coarseURL,
            p0URL: p0URL
        )
        XCTAssertEqual(result.size, 2400)
        XCTAssertEqual(result.imageURL, coarseURL)
        XCTAssertEqual(result.p0URL, p0URL)
        XCTAssertTrue(FileManager.default.fileExists(atPath: coarseURL.path))

        XCTAssertEqual(
            try readArgv(argvURL),
            [
                "calibration-coarse-target",
                "--family", "dvd",
                "--r0", "24.0",
                "--r1", "58.0",
                "--sectors", "2295104",
                "--density", "133.3",
                "--size", "2400",
                "--output", coarseURL.path,
                "--json", p0URL.path,
                "--pitch-linear", "0.001",
                "--pitch-quadratic", "-0.002",
                "--pitch-cubic", "0.003",
            ]
        )
    }

    // MARK: - calibration-photo

    func testPreparePhotoArguments() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let payload = #"{"succeeded": true, "message": "", "photoWidth": 2000, "photoHeight": 1500, "resizedPhotoPath": "/tmp/resized.png"}"#
        let cli = try makeFakeCLI(
            argvURL: argvURL,
            payloads: ["calibration-photo": payload]
        )
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        let resizedURL = temporaryDirectory.appendingPathComponent("resized.png")

        let result = try await service.preparePhoto(
            photoURL: photoURL,
            discSize: .standard,
            resizedOut: resizedURL
        )
        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.photoWidth, 2000)
        XCTAssertEqual(result.photoHeight, 1500)

        let arguments = try readArgv(argvURL)
        XCTAssertEqual(Array(arguments.prefix(8)), [
            "calibration-photo",
            "--photo", photoURL.path,
            "--disc-size", "120",
            "--resized-out", resizedURL.path,
            "--json",
        ])
        XCTAssertEqual(arguments.count, 9)
        XCTAssertTrue(arguments[8].hasPrefix(workspace.path + "/result-"))
        XCTAssertTrue(arguments[8].hasSuffix(".json"))
    }

    func testRectifyPhotoEncodesOuterAndHolePoints() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let payload = #"{"succeeded": true, "photoWidth": 2000, "photoHeight": 1500, "rectifiedSize": 1200, "canvasOuterRadiusMm": 60, "discSize": 120, "rectifiedImagePath": "/tmp/rectified.png"}"#
        let cli = try makeFakeCLI(
            argvURL: argvURL,
            payloads: ["calibration-photo": payload]
        )
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        let resizedURL = temporaryDirectory.appendingPathComponent("resized.png")
        let rectifiedURL = temporaryDirectory.appendingPathComponent("rectified.png")
        let outer = [
            CalibrationPoint(x: 1.5, y: -2.25),
            CalibrationPoint(x: 2.0, y: 0.0),
            CalibrationPoint(x: 0.0, y: 2.0),
            CalibrationPoint(x: -2.0, y: 0.0),
            CalibrationPoint(x: 0.0, y: -2.0),
        ]
        let hole = [
            CalibrationPoint(x: 0.5, y: 0.5),
            CalibrationPoint(x: 0.0, y: 1.0),
            CalibrationPoint(x: -0.5, y: 0.5),
            CalibrationPoint(x: -1.0, y: 0.0),
            CalibrationPoint(x: 0.0, y: -1.0),
        ]

        let result = try await service.rectifyPhoto(
            photoURL: photoURL,
            discSize: .mini,
            outer: outer,
            hole: hole,
            resizedOut: resizedURL,
            rectifiedOut: rectifiedURL,
            size: 1200
        )
        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.rectifiedSize, 1200)

        let arguments = try readArgv(argvURL)
        XCTAssertEqual(Array(arguments.prefix(9)), [
            "calibration-photo",
            "--photo", photoURL.path,
            "--disc-size", "80",
            "--outer", "1.5,-2.25;2.0,0.0;0.0,2.0;-2.0,0.0;0.0,-2.0",
            "--hole", "0.5,0.5;0.0,1.0;-0.5,0.5;-1.0,0.0;0.0,-1.0",
        ])
        XCTAssertEqual(Array(arguments[9...12]), ["--size", "1200", "--resized-out", resizedURL.path])
        XCTAssertEqual(Array(arguments[13...14]), ["--rectified-out", rectifiedURL.path])
        XCTAssertEqual(Array(arguments[15...16]), ["--json", arguments[16]])
    }

    func testRectifyPhotoRejectsTooFewPoints() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL)
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        do {
            _ = try await service.rectifyPhoto(
                photoURL: photoURL,
                discSize: .standard,
                outer: Array(repeating: CalibrationPoint(x: 0, y: 0), count: 4),
                hole: Array(repeating: CalibrationPoint(x: 0, y: 0), count: 4),
                resizedOut: temporaryDirectory.appendingPathComponent("resized.png"),
                rectifiedOut: temporaryDirectory.appendingPathComponent("rectified.png")
            )
            XCTFail("控制点不足应抛 invalidGeometry")
        } catch let error as CalibrationError {
            guard case .invalidGeometry = error else {
                return XCTFail("应抛 invalidGeometry，实际是 \(error)")
            }
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: argvURL.path))
    }

    // MARK: - calibration-fit / coarse-fit

    func testFitArgumentsAndResultDecoding() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let payload = #"""
        {
          "succeeded": true,
          "reliable": true,
          "warnings": [],
          "bestIndex": 0,
          "candidates": [
            {"innerRadiusMm": 24.1, "outerRadiusMm": 57.9, "rmsErrorMm": 0.1, "maxErrorMm": 0.3,
             "predicted": [{"x": 1.0, "y": 2.0}]}
          ]
        }
        """#
        let cli = try makeFakeCLI(
            argvURL: argvURL,
            payloads: ["calibration-fit": payload]
        )
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        let points = [
            CalibrationPoint(x: 24.5, y: 0),
            CalibrationPoint(x: 25.0, y: 0.1),
            CalibrationPoint(x: 26.0, y: 0.2),
            CalibrationPoint(x: 27.0, y: 0.3),
            CalibrationPoint(x: 28.0, y: 0.4),
            CalibrationPoint(x: 29.0, y: 0.5),
        ]

        let result = try await service.fit(
            sessionURL: sessionURL,
            points: points,
            radiusRange: 0.02,
            autoRange: false,
            fitPitch: false
        )
        XCTAssertTrue(result.succeeded)
        XCTAssertTrue(result.reliable)
        XCTAssertEqual(result.candidates.count, 1)
        XCTAssertEqual(result.candidates[0].rmsErrorMm, 0.1)
        XCTAssertEqual(result.candidates[0].predicted, [CalibrationPoint(x: 1, y: 2)])

        let arguments = try readArgv(argvURL)
        XCTAssertEqual(Array(arguments.prefix(6)), [
            "calibration-fit",
            "--session", sessionURL.path,
            "--points", "24.5,0.0;25.0,0.1;26.0,0.2;27.0,0.3;28.0,0.4;29.0,0.5",
            "--json",
        ])
        XCTAssertEqual(Array(arguments[7...10]), ["--auto-range", "false", "--fit-pitch", "false"])
        XCTAssertEqual(Array(arguments[11...12]), ["--radius-range", "0.02"])
        XCTAssertTrue(arguments[6].hasPrefix(workspace.path + "/result-"))
    }

    func testFitRejectsTooFewPoints() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL)
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        do {
            _ = try await service.fit(
                sessionURL: sessionURL,
                points: Array(repeating: CalibrationPoint(x: 1, y: 1), count: 5)
            )
            XCTFail("精调少于 6 点应抛 invalidGeometry")
        } catch let error as CalibrationError {
            guard case .invalidGeometry = error else {
                return XCTFail("应抛 invalidGeometry，实际是 \(error)")
            }
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: argvURL.path))
    }

    func testFitCoarseArguments() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let payload = #"""
        {"succeeded": true, "warnings": [], "bestIndex": 0,
         "candidates": [{"innerRadiusMm": 24.15, "outerRadiusMm": 57.88, "rotationRadians": 0.1, "rmsErrorMm": 0.18, "maxErrorMm": 0.42}]}
        """#
        let cli = try makeFakeCLI(
            argvURL: argvURL,
            payloads: ["calibration-coarse-fit": payload]
        )
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        let points = [
            CalibrationPoint(x: 24.0, y: 0),
            CalibrationPoint(x: 24.5, y: 0.1),
            CalibrationPoint(x: 25.0, y: 0.2),
            CalibrationPoint(x: 25.5, y: 0.3),
            CalibrationPoint(x: 26.0, y: 0.4),
            CalibrationPoint(x: 26.5, y: 0.5),
            CalibrationPoint(x: 27.0, y: 0.6),
            CalibrationPoint(x: 27.5, y: 0.7),
        ]

        let result = try await service.fitCoarse(
            family: .dvd,
            geometry: ProjectGeometry(
                innerRadiusMm: 24.0,
                outerRadiusMm: 58.0,
                sectors: 2_295_104,
                pitchLinear: 0.001,
                pitchQuadratic: -0.002,
                pitchCubic: 0.003
            ),
            density: 133.3,
            points: points
        )
        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.candidates.first?.innerRadiusMm, 24.15)
        XCTAssertTrue(result.isApplicable)

        let arguments = try readArgv(argvURL)
        XCTAssertEqual(Array(arguments.prefix(13)), [
            "calibration-coarse-fit",
            "--family", "dvd",
            "--r0", "24.0",
            "--r1", "58.0",
            "--sectors", "2295104",
            "--density", "133.3",
            "--points", "24.0,0.0;24.5,0.1;25.0,0.2;25.5,0.3;26.0,0.4;26.5,0.5;27.0,0.6;27.5,0.7",
        ])
        XCTAssertEqual(Array(arguments[13...14]), ["--json", arguments[14]])
        XCTAssertTrue(arguments[14].hasPrefix(workspace.path + "/result-"))
        XCTAssertEqual(Array(arguments.suffix(6)), ["--pitch-linear", "0.001", "--pitch-quadratic", "-0.002", "--pitch-cubic", "0.003"])
    }

    func testFitCoarseRejectsTooFewPoints() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL)
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        do {
            _ = try await service.fitCoarse(
                family: .cd,
                geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
                density: 1200,
                points: Array(repeating: CalibrationPoint(x: 1, y: 1), count: 7)
            )
            XCTFail("粗调少于 8 点应抛 invalidGeometry")
        } catch let error as CalibrationError {
            guard case .invalidGeometry = error else {
                return XCTFail("应抛 invalidGeometry，实际是 \(error)")
            }
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: argvURL.path))
    }

    // MARK: - 会话读写

    func testWriteSessionThenReadSessionArguments() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let readPayload = #"{"family": "CompactDisc", "innerRadiusMm": 24.5, "outerRadiusMm": 56.8, "sectors": 359849, "hasPhoto": true, "rectificationConfirmed": true, "tracePointCount": 6}"#
        let cli = try makeFakeCLI(
            argvURL: argvURL,
            payloads: ["calibration-session-read": readPayload]
        )
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        let resizedURL = temporaryDirectory.appendingPathComponent("resized.png")
        let canonicalURL = temporaryDirectory.appendingPathComponent("session.calibration.json")
        let points = [
            CalibrationPoint(x: 24.5, y: 0),
            CalibrationPoint(x: 25.0, y: 0.1),
            CalibrationPoint(x: 26.0, y: 0.2),
            CalibrationPoint(x: 27.0, y: 0.3),
            CalibrationPoint(x: 28.0, y: 0.4),
            CalibrationPoint(x: 29.0, y: 0.5),
        ]

        let summary = try await service.writeSession(
            baseSessionURL: sessionURL,
            photoURL: resizedURL,
            discSize: .standard,
            outer: Array(repeating: CalibrationPoint(x: 1, y: 2), count: 5),
            hole: Array(repeating: CalibrationPoint(x: 0.5, y: 0.5), count: 5),
            points: points,
            outputURL: canonicalURL
        )
        XCTAssertEqual(summary.family, "CompactDisc")
        XCTAssertEqual(summary.tracePointCount, 6)

        let arguments = try readArgv(argvURL)
        XCTAssertEqual(Array(arguments.prefix(17)), [
            "calibration-session-write",
            "--session", sessionURL.path,
            "--photo", resizedURL.path,
            "--disc-size", "120",
            "--outer", "1.0,2.0;1.0,2.0;1.0,2.0;1.0,2.0;1.0,2.0",
            "--hole", "0.5,0.5;0.5,0.5;0.5,0.5;0.5,0.5;0.5,0.5",
            "--points", "24.5,0.0;25.0,0.1;26.0,0.2;27.0,0.3;28.0,0.4;29.0,0.5",
            "--confirmed", "true",
            "--output", canonicalURL.path,
        ])
        // 第二次调用是 calibration-session-read。
        XCTAssertEqual(Array(arguments[17...20]), [
            "calibration-session-read",
            "--session", canonicalURL.path,
            "--json",
        ])
    }

    func testReadSessionArguments() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let payload = #"{"family": "CompactDisc", "innerRadiusMm": 24.5, "outerRadiusMm": 56.8}"#
        let cli = try makeFakeCLI(
            argvURL: argvURL,
            payloads: ["calibration-session-read": payload]
        )
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        let summary = try await service.readSession(sessionURL)
        XCTAssertEqual(summary.innerRadiusMm, 24.5)
        XCTAssertEqual(summary.outerRadiusMm, 56.8)

        let arguments = try readArgv(argvURL)
        XCTAssertEqual(Array(arguments.prefix(3)), [
            "calibration-session-read",
            "--session", sessionURL.path,
        ])
        XCTAssertEqual(Array(arguments[3...4]), ["--json", arguments[4]])
        XCTAssertTrue(arguments[4].hasPrefix(workspace.path + "/result-"))
    }

    // MARK: - 错误映射

    func testNonZeroExitMapsToCommandFailedWithStderr() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL, exitCode: 2, stderr: "boom")
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        do {
            _ = try await service.fit(
                sessionURL: sessionURL,
                points: Array(repeating: CalibrationPoint(x: 1, y: 1), count: 6)
            )
            XCTFail("非 0 退出应抛 commandFailed")
        } catch let error as CalibrationError {
            guard case .commandFailed(let reason) = error else {
                return XCTFail("应抛 commandFailed，实际是 \(error)")
            }
            XCTAssertEqual(reason, "boom")
        }
    }

    func testNonZeroExitWithoutStderrReportsExitCode() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL, exitCode: 7)
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        do {
            _ = try await service.readSession(sessionURL)
            XCTFail("非 0 退出应抛 commandFailed")
        } catch let error as CalibrationError {
            guard case .commandFailed(let reason) = error else {
                return XCTFail("应抛 commandFailed，实际是 \(error)")
            }
            XCTAssertEqual(reason, "退出码 7")
        }
    }

    func testMissingResultFileMapsToMalformedResult() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL) // 不写任何 --json 结果
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        do {
            _ = try await service.readSession(sessionURL)
            XCTFail("缺少结果 JSON 应抛 malformedResult")
        } catch let error as CalibrationError {
            guard case .malformedResult(let reason) = error else {
                return XCTFail("应抛 malformedResult，实际是 \(error)")
            }
            XCTAssertEqual(reason, "结果文件不存在。")
        }
    }

    func testBadResultJSONMapsToMalformedResult() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(
            argvURL: argvURL,
            payloads: ["calibration-session-read": "not json"]
        )
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        do {
            _ = try await service.readSession(sessionURL)
            XCTFail("坏 JSON 应抛 malformedResult")
        } catch let error as CalibrationError {
            guard case .malformedResult = error else {
                return XCTFail("应抛 malformedResult，实际是 \(error)")
            }
        }
    }

    func testCoarseTargetMissingPatternReportsCommandFailed() async throws {
        let argvURL = temporaryDirectory.appendingPathComponent("argv.txt")
        let cli = try makeFakeCLI(argvURL: argvURL, createTargetFiles: false)
        let service = CalibrationService(
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI"),
            workspace: workspace
        )
        do {
            _ = try await service.generateCoarseTarget(
                family: .cd,
                geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849),
                density: 1200,
                outputURL: temporaryDirectory.appendingPathComponent("coarse.png"),
                p0URL: temporaryDirectory.appendingPathComponent("p0.json")
            )
            XCTFail("缺少图案文件应抛 commandFailed")
        } catch let error as CalibrationError {
            guard case .commandFailed(let reason) = error else {
                return XCTFail("应抛 commandFailed，实际是 \(error)")
            }
            XCTAssertEqual(reason, "粗调图案没有生成。")
        }
    }

    // MARK: - Helpers

    private func readArgv(_ url: URL) throws -> [String] {
        let text = try String(contentsOf: url, encoding: .utf8)
        return text.split(separator: "\n", omittingEmptySubsequences: true).map(String.init)
    }

    private func makeFakeCLI(
        argvURL: URL,
        payloads: [String: String] = [:],
        createTargetFiles: Bool = true,
        exitCode: Int = 0,
        stderr: String = ""
    ) throws -> URL {
        let escapedStderr = stderr.replacingOccurrences(of: "\"", with: "\\\"")
        for (command, payload) in payloads {
            try Data(payload.utf8).write(
                to: workspace.deletingLastPathComponent()
                    .appendingPathComponent("payload-\(command).json", isDirectory: false)
            )
        }
        let directory = workspace.deletingLastPathComponent()
        let contents = """
        #!/bin/sh
        printf '%s\\n' "$@" >> "\(argvURL.path)"
        cmd="$1"
        shift
        json=""
        output=""
        session=""
        while [ $# -gt 0 ]; do
          case "$1" in
            --json) json="$2"; shift 2 ;;
            --output) output="$2"; shift 2 ;;
            --session) session="$2"; shift 2 ;;
            *) shift ;;
          esac
        done
        if [ "\(createTargetFiles ? "yes" : "no")" = "yes" ]; then
          case "$cmd" in
            calibration-target)
              [ -n "$output" ] && printf 'png' > "$output"
              [ -n "$session" ] && printf '{}' > "$session"
              ;;
            calibration-coarse-target)
              [ -n "$output" ] && printf 'png' > "$output"
              ;;
          esac
        fi
        if [ -n "$json" ]; then
          payload="$(dirname "$0")/payload-$cmd.json"
          if [ -f "$payload" ]; then cp "$payload" "$json"; fi
        fi
        if [ -n "\(escapedStderr)" ]; then printf '%s\\n' "\(escapedStderr)" >&2; fi
        exit \(exitCode)
        """
        return try TestSupport.writeExecutableScript(in: directory, name: "fake-calibration-cli", contents: contents)
    }
}
