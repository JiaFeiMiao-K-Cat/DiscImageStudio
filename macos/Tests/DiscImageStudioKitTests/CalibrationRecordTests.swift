import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住 GUI 标定记录持久化与应用参数（macos/CALIBRATION.md §2/§3）。
final class CalibrationRecordTests: XCTestCase {

    private var temporaryDirectory: URL!
    private var workspace: URL!

    override func setUpWithError() throws {
        temporaryDirectory = try TestSupport.makeTemporaryDirectory("CalibrationRecordTests")
        workspace = temporaryDirectory.appendingPathComponent("calibration", isDirectory: true)
    }

    override func tearDownWithError() throws {
        if let temporaryDirectory {
            try? FileManager.default.removeItem(at: temporaryDirectory)
        }
        temporaryDirectory = nil
        workspace = nil
    }

    // MARK: - Store

    func testSaveCreatesWorkspaceAndLoadsRecord() throws {
        let store = CalibrationRecordStore(workspace: workspace)
        XCTAssertFalse(FileManager.default.fileExists(atPath: workspace.path))

        let record = makeRecord(id: "record-1", createdAt: "2026-01-01T10:00:00.000Z")
        let saved = try store.save(record)

        XCTAssertTrue(FileManager.default.fileExists(atPath: workspace.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.fileURL.path))
        XCTAssertEqual(saved.map(\.id), ["record-1"])

        let loaded = store.load()
        XCTAssertEqual(loaded.count, 1)
        XCTAssertEqual(loaded[0], record, "记录应完整往返（含边界点/候选/覆盖字段）")
        XCTAssertEqual(store.record(id: "record-1")?.family, .dvd)
        XCTAssertNil(store.record(id: "missing"))
    }

    func testLoadSortsByCreatedAtDescending() throws {
        let store = CalibrationRecordStore(workspace: workspace)
        let records = [
            makeRecord(id: "old", createdAt: "2026-01-01T00:00:00.000Z"),
            makeRecord(id: "new", createdAt: "2026-03-01T00:00:00.000Z"),
            makeRecord(id: "mid", createdAt: "2026-02-01T00:00:00.000Z"),
        ]
        for record in records {
            _ = try store.save(record)
        }

        XCTAssertEqual(store.load().map(\.id), ["new", "mid", "old"])
    }

    func testSaveReplacesRecordWithSameID() throws {
        let store = CalibrationRecordStore(workspace: workspace)
        var record = makeRecord(id: "same", createdAt: "2026-01-01T00:00:00.000Z")
        _ = try store.save(record)

        record.selectedCandidateIndex = 1
        record.appliedAt = "2026-01-02T00:00:00.000Z"
        record.appliedInnerRadiusMm = 24.1
        record.appliedOuterRadiusMm = 57.9
        let saved = try store.save(record)

        XCTAssertEqual(saved.count, 1)
        XCTAssertEqual(store.load().count, 1)
        let loaded = try XCTUnwrap(store.record(id: "same"))
        XCTAssertEqual(loaded.selectedCandidateIndex, 1)
        XCTAssertEqual(loaded.appliedAt, "2026-01-02T00:00:00.000Z")
        XCTAssertEqual(loaded.appliedInnerRadiusMm, 24.1)
        XCTAssertTrue(loaded.isApplied)
    }

    func testDeleteRemovesOnlyMatchingRecord() throws {
        let store = CalibrationRecordStore(workspace: workspace)
        _ = try store.save(makeRecord(id: "keep", createdAt: "2026-01-01T00:00:00.000Z"))
        _ = try store.save(makeRecord(id: "remove", createdAt: "2026-01-02T00:00:00.000Z"))

        let remaining = try store.delete(id: "remove")
        XCTAssertEqual(remaining.map(\.id), ["keep"])
        XCTAssertEqual(store.load().map(\.id), ["keep"])
        XCTAssertNil(store.record(id: "remove"))

        // 删除不存在的 id 不应改变文件内容。
        XCTAssertEqual(try store.delete(id: "missing").map(\.id), ["keep"])
    }

    func testCorruptOrMissingFileFallsBackToEmpty() throws {
        let store = CalibrationRecordStore(workspace: workspace)
        XCTAssertEqual(store.load(), [])

        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        try Data("{ not json".utf8).write(to: store.fileURL)
        XCTAssertEqual(store.load(), [])

        try Data("[]".utf8).write(to: store.fileURL)
        XCTAssertEqual(store.load(), [])
    }

    func testRecordComputedProperties() {
        let fineCandidate = CalibrationCandidate(
            innerRadiusMm: 24.1,
            outerRadiusMm: 57.9,
            rmsErrorMm: 0.1,
            maxErrorMm: 0.3
        )
        let secondCandidate = CalibrationCandidate(
            innerRadiusMm: 24.2,
            outerRadiusMm: 57.8,
            rmsErrorMm: 0.2,
            maxErrorMm: 0.4
        )
        let fit = CalibrationFitResult(
            succeeded: true,
            reliable: true,
            boundaryLimited: false,
            warnings: [],
            bestIndex: 0,
            candidates: [fineCandidate, secondCandidate]
        )
        var record = makeRecord(id: "computed", createdAt: "2026-01-01T00:00:00.000Z")
        record.mode = .fine
        record.fit = fit
        record.selectedCandidateIndex = 1
        record.rectifiedPath = "/tmp/rectified.png"

        XCTAssertEqual(record.candidates, [fineCandidate, secondCandidate])
        XCTAssertEqual(record.selectedCandidate, secondCandidate)
        XCTAssertFalse(record.isApplied)
        XCTAssertTrue(record.isRectificationConfirmed)

        record.mode = .coarse
        let coarse = CalibrationCoarseResult(
            succeeded: true,
            warnings: [],
            bestIndex: 0,
            candidates: [fineCandidate]
        )
        record.coarseFit = coarse
        record.selectedCandidateIndex = 5
        XCTAssertEqual(record.candidates, [fineCandidate])
        XCTAssertNil(record.selectedCandidate, "越界索引应返回 nil")

        record.mode = .fine
        record.selectedCandidateIndex = 0
        record.fit = nil
        XCTAssertEqual(record.candidates, [])
        XCTAssertNil(record.selectedCandidate)
    }

    // MARK: - applyingCalibration

    func testApplyingCalibrationToCDUpdatesOnlyRadii() {
        let project = makeProject(family: .cd)
        let candidate = CalibrationCandidate(
            innerRadiusMm: 24.123,
            outerRadiusMm: 57.886,
            pitchLinear: 9,
            pitchQuadratic: 8,
            pitchCubic: 7,
            sectors: 123,
            invertMapping: true
        )

        let updated = project.applyingCalibration(candidate)
        XCTAssertEqual(updated.geometry.innerRadiusMm, 24.123)
        XCTAssertEqual(updated.geometry.outerRadiusMm, 57.886)
        XCTAssertEqual(updated.geometry.sectors, project.geometry.sectors, "CD 不更新扇区")
        XCTAssertEqual(updated.geometry.pitchLinear, project.geometry.pitchLinear, "CD 不更新 pitch")
        XCTAssertNil(updated.bluRay)
        assertUnchanged(project, updated)
    }

    func testApplyingCalibrationToDVDUpdatesRadiiAndPitchOnly() {
        let project = makeProject(family: .dvd)
        let candidate = CalibrationCandidate(
            innerRadiusMm: 23.98,
            outerRadiusMm: 57.96,
            pitchLinear: -3.925e-05,
            pitchQuadratic: 6.964e-05,
            pitchCubic: -4.67e-05,
            sectors: 123,
            invertMapping: true
        )

        let updated = project.applyingCalibration(candidate)
        XCTAssertEqual(updated.geometry.innerRadiusMm, 23.98)
        XCTAssertEqual(updated.geometry.outerRadiusMm, 57.96)
        XCTAssertEqual(updated.geometry.pitchLinear, -3.925e-05)
        XCTAssertEqual(updated.geometry.pitchQuadratic, 6.964e-05)
        XCTAssertEqual(updated.geometry.pitchCubic, -4.67e-05)
        XCTAssertEqual(updated.geometry.sectors, project.geometry.sectors, "DVD 不更新扇区")
        XCTAssertNil(updated.bluRay)
        assertUnchanged(project, updated)
    }

    func testApplyingCalibrationToBDUpdatesRadiiSectorsAndPolarity() {
        let project = makeProject(family: .bd)
        let candidate = CalibrationCandidate(
            innerRadiusMm: 23.987,
            outerRadiusMm: 57.988,
            pitchLinear: 1,
            pitchQuadratic: 1,
            pitchCubic: 1,
            sectors: 12_219_360,
            invertMapping: true
        )

        let updated = project.applyingCalibration(candidate)
        XCTAssertEqual(updated.geometry.innerRadiusMm, 23.987)
        XCTAssertEqual(updated.geometry.outerRadiusMm, 57.988)
        XCTAssertEqual(updated.geometry.sectors, 12_219_360, "BD 更新扇区")
        XCTAssertEqual(updated.geometry.pitchLinear, project.geometry.pitchLinear, "BD 不更新 pitch")
        XCTAssertEqual(updated.bluRay, ProjectBluRaySettings(invertMapping: true))
        assertUnchanged(project, updated)
    }

    func testApplyingCalibrationKeepsBDFieldsWhenCandidateOmitsThem() {
        let project = makeProject(family: .bd)
        let candidate = CalibrationCandidate(innerRadiusMm: 24.1, outerRadiusMm: 57.9)

        let updated = project.applyingCalibration(candidate)
        XCTAssertEqual(updated.geometry.sectors, project.geometry.sectors, "候选缺 sectors 时保持原扇区")
        XCTAssertEqual(updated.bluRay, project.bluRay, "候选缺 invertMapping 时保持原极性")
        XCTAssertEqual(updated.geometry.innerRadiusMm, 24.1)
    }

    func testApplyingCalibrationIgnoresNonPositiveBDSectors() {
        let project = makeProject(family: .bd)
        let candidate = CalibrationCandidate(
            innerRadiusMm: 24.1,
            outerRadiusMm: 57.9,
            sectors: 0,
            invertMapping: false
        )
        let updated = project.applyingCalibration(candidate)
        XCTAssertEqual(updated.geometry.sectors, project.geometry.sectors)
        XCTAssertEqual(updated.bluRay, ProjectBluRaySettings(invertMapping: false), "显式的 false 应生效")
    }

    // MARK: - Helpers

    private func makeProject(family: DiscFamily) -> ProjectDocument {
        ProjectDocument(
            name: "标定工程",
            discFamily: family,
            geometry: ProjectGeometry(
                innerRadiusMm: 24.5,
                outerRadiusMm: 56.8,
                sectors: family == .bd ? 12_219_392 : 359_849,
                pitchLinear: family == .dvd ? 0.001 : 0,
                pitchQuadratic: family == .dvd ? -0.002 : 0,
                pitchCubic: family == .dvd ? 0.003 : 0
            ),
            layers: [
                DesignLayer(id: "layer-1", name: "L", imagePath: "/tmp/l.png")
            ],
            outputs: ProjectOutputs(
                outputPath: "/tmp/out.iso",
                writeCue: false,
                dataDirectory: "/tmp/data",
                volumeLabel: "KEEP",
                interleave: false,
                filesAtOuterRing: true
            ),
            calibrationSessionPath: "/tmp/session.calibration.json",
            thumbnailPngBase64: "aGVsbG8=",
            createdUtc: "2026-01-01T00:00:00.000Z",
            modifiedUtc: "2026-01-02T00:00:00.000Z",
            tone: ProjectTone(ditherStrength: 0.5, levels: 2, brightness: -3, srgbLinearization: true),
            bluRay: family == .bd ? ProjectBluRaySettings(invertMapping: false) : nil
        )
    }

    private func assertUnchanged(
        _ original: ProjectDocument,
        _ updated: ProjectDocument,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        XCTAssertEqual(updated.name, original.name, file: file, line: line)
        XCTAssertEqual(updated.discFamily, original.discFamily, file: file, line: line)
        XCTAssertEqual(updated.layers, original.layers, file: file, line: line)
        XCTAssertEqual(updated.outputs, original.outputs, file: file, line: line)
        XCTAssertEqual(updated.calibrationSessionPath, original.calibrationSessionPath, file: file, line: line)
        XCTAssertEqual(updated.thumbnailPngBase64, original.thumbnailPngBase64, file: file, line: line)
        XCTAssertEqual(updated.createdUtc, original.createdUtc, file: file, line: line)
        XCTAssertEqual(updated.modifiedUtc, original.modifiedUtc, file: file, line: line)
        XCTAssertEqual(updated.tone, original.tone, file: file, line: line)
    }

    private func makeRecord(id: String, createdAt: String) -> CalibrationRecord {
        let fit = CalibrationFitResult(
            succeeded: true,
            reliable: true,
            boundaryLimited: false,
            warnings: ["warning"],
            bestIndex: 0,
            candidates: [
                CalibrationCandidate(
                    innerRadiusMm: 24.1,
                    outerRadiusMm: 57.9,
                    rotationRadians: 0.1,
                    rmsErrorMm: 0.1,
                    maxErrorMm: 0.3,
                    outlierCount: 1,
                    predicted: [CalibrationPoint(x: 1, y: 2)]
                )
            ]
        )
        return CalibrationRecord(
            id: id,
            createdAt: createdAt,
            mode: .fine,
            family: .dvd,
            generatedGeometry: ProjectGeometry(
                innerRadiusMm: 24.0,
                outerRadiusMm: 58.0,
                sectors: 2_295_104,
                pitchLinear: 0.001,
                pitchQuadratic: -0.002,
                pitchCubic: 0.003
            ),
            density: 133.3,
            targetImagePath: "/tmp/target.png",
            baseSessionPath: "/tmp/base.calibration.json",
            canonicalSessionPath: "/tmp/canonical.calibration.json",
            photoPath: "/tmp/photo.png",
            photoWidth: 2000,
            photoHeight: 1500,
            discSize: 120,
            outerBoundary: [CalibrationPoint(x: 1.5, y: -2.25)],
            holeBoundary: [CalibrationPoint(x: 0.5, y: 0.5)],
            rectifiedPath: "/tmp/rectified.png",
            rectifiedSize: 1200,
            canvasOuterRadiusMm: 60,
            tracePoints: [CalibrationPoint(x: 24.5, y: 0)],
            fit: fit,
            selectedCandidateIndex: 0,
            appliedAt: nil,
            appliedInnerRadiusMm: nil,
            appliedOuterRadiusMm: nil
        )
    }
}
