import CoreGraphics
import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住标定结果 JSON 的容错解析与「可应用」阈值（macos/CALIBRATION.md §3/§4）。
final class CalibrationModelsTests: XCTestCase {

    // MARK: - CalibrationPhotoResult

    func testPhotoResultMissingFieldsUseDefaults() throws {
        let minimal = try decode(CalibrationPhotoResult.self, from: #"{"succeeded": true}"#)
        XCTAssertTrue(minimal.succeeded)
        XCTAssertEqual(minimal.message, "")
        XCTAssertEqual(minimal.photoWidth, 0)
        XCTAssertEqual(minimal.photoHeight, 0)
        XCTAssertNil(minimal.rectifiedSize)
        XCTAssertNil(minimal.canvasOuterRadiusMm)
        XCTAssertNil(minimal.discSize)
        XCTAssertNil(minimal.outerRmsPixels)
        XCTAssertNil(minimal.holeRmsPixels)
        XCTAssertNil(minimal.transformDiscToSource)
        XCTAssertNil(minimal.resizedPhotoPath)
        XCTAssertNil(minimal.rectifiedImagePath)

        let empty = try decode(CalibrationPhotoResult.self, from: "{}")
        XCTAssertFalse(empty.succeeded, "缺 succeeded 时应视为失败")
        XCTAssertEqual(empty.photoWidth, 0)
    }

    func testPhotoResultFullPayload() throws {
        let json = #"""
        {
          "succeeded": true,
          "message": "",
          "photoWidth": 2000,
          "photoHeight": 1500,
          "rectifiedSize": 1200,
          "canvasOuterRadiusMm": 60,
          "discSize": 120,
          "outerRmsPixels": 1.7,
          "holeRmsPixels": 2.3,
          "transformDiscToSource": [1,2,3,4,5,6,7,8,9],
          "resizedPhotoPath": "/tmp/photo.png",
          "rectifiedImagePath": "/tmp/rectified.png"
        }
        """#
        let result = try decode(CalibrationPhotoResult.self, from: json)
        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.photoWidth, 2000)
        XCTAssertEqual(result.photoHeight, 1500)
        XCTAssertEqual(result.rectifiedSize, 1200)
        XCTAssertEqual(result.canvasOuterRadiusMm, 60)
        XCTAssertEqual(result.discSize, 120)
        XCTAssertEqual(result.outerRmsPixels, 1.7)
        XCTAssertEqual(result.holeRmsPixels, 2.3)
        XCTAssertEqual(result.transformDiscToSource, [1, 2, 3, 4, 5, 6, 7, 8, 9])
        XCTAssertEqual(result.resizedPhotoPath, "/tmp/photo.png")
        XCTAssertEqual(result.rectifiedImagePath, "/tmp/rectified.png")
    }

    func testPhotoResultWrongFieldTypeThrows() {
        XCTAssertThrowsError(
            try decode(CalibrationPhotoResult.self, from: #"{"photoWidth": "wide"}"#)
        )
    }

    // MARK: - CalibrationCandidate

    func testCandidateMissingPitchAndSectorsFallsBackToDefaults() throws {
        let candidate = try decode(
            CalibrationCandidate.self,
            from: #"{"innerRadiusMm": 24.1, "outerRadiusMm": 57.9}"#
        )
        XCTAssertEqual(candidate.innerRadiusMm, 24.1)
        XCTAssertEqual(candidate.outerRadiusMm, 57.9)
        XCTAssertEqual(candidate.pitchLinear, 0)
        XCTAssertEqual(candidate.pitchQuadratic, 0)
        XCTAssertEqual(candidate.pitchCubic, 0)
        XCTAssertEqual(candidate.rotationRadians, 0)
        XCTAssertTrue(candidate.rmsErrorMm.isInfinite, "缺 rmsErrorMm 应回落 infinity")
        XCTAssertTrue(candidate.maxErrorMm.isInfinite)
        XCTAssertEqual(candidate.outlierCount, 0)
        XCTAssertEqual(candidate.predicted, [])
        XCTAssertNil(candidate.sectors)
        XCTAssertNil(candidate.invertMapping)
        XCTAssertEqual(candidate.id, "24.1-57.9-inf")
    }

    func testCandidateFullPayloadAndIdentifiable() throws {
        let json = #"""
        {
          "innerRadiusMm": 24.146,
          "outerRadiusMm": 57.886,
          "pitchLinear": 0.0001,
          "pitchQuadratic": -0.0002,
          "pitchCubic": 0.0003,
          "rotationRadians": 0.123,
          "rmsErrorMm": 0.12,
          "maxErrorMm": 0.31,
          "outlierCount": 2,
          "predicted": [{"x": 1.5, "y": -2.25}],
          "sectors": 359849,
          "invertMapping": true
        }
        """#
        let candidate = try decode(CalibrationCandidate.self, from: json)
        XCTAssertEqual(candidate.innerRadiusMm, 24.146)
        XCTAssertEqual(candidate.pitchLinear, 0.0001)
        XCTAssertEqual(candidate.pitchQuadratic, -0.0002)
        XCTAssertEqual(candidate.pitchCubic, 0.0003)
        XCTAssertEqual(candidate.rotationRadians, 0.123)
        XCTAssertEqual(candidate.rmsErrorMm, 0.12)
        XCTAssertEqual(candidate.maxErrorMm, 0.31)
        XCTAssertEqual(candidate.outlierCount, 2)
        XCTAssertEqual(candidate.predicted, [CalibrationPoint(x: 1.5, y: -2.25)])
        XCTAssertEqual(candidate.sectors, 359849)
        XCTAssertEqual(candidate.invertMapping, true)
        XCTAssertEqual(candidate.id, "24.146-57.886-0.12")
    }

    func testCandidateMissingRequiredRadiusThrows() {
        XCTAssertThrowsError(try decode(CalibrationCandidate.self, from: #"{"outerRadiusMm": 57.9}"#))
        XCTAssertThrowsError(try decode(CalibrationCandidate.self, from: #"{"innerRadiusMm": 24.1}"#))
    }

    // MARK: - Fit / Coarse 结果容错

    func testFitResultMissingWarningsAndCandidatesUseDefaults() throws {
        let result = try decode(CalibrationFitResult.self, from: #"{"succeeded": true}"#)
        XCTAssertTrue(result.succeeded)
        XCTAssertFalse(result.reliable)
        XCTAssertFalse(result.boundaryLimited)
        XCTAssertEqual(result.warnings, [])
        XCTAssertEqual(result.bestIndex, -1)
        XCTAssertEqual(result.candidates, [])
    }

    func testFitResultFullPayload() throws {
        let json = #"""
        {
          "succeeded": true,
          "reliable": true,
          "boundaryLimited": true,
          "warnings": ["搜索命中边界"],
          "bestIndex": 0,
          "candidates": [
            {"innerRadiusMm": 24.1, "outerRadiusMm": 57.9, "rmsErrorMm": 0.1, "maxErrorMm": 0.3}
          ]
        }
        """#
        let result = try decode(CalibrationFitResult.self, from: json)
        XCTAssertTrue(result.reliable)
        XCTAssertTrue(result.boundaryLimited)
        XCTAssertEqual(result.warnings, ["搜索命中边界"])
        XCTAssertEqual(result.bestIndex, 0)
        XCTAssertEqual(result.candidates.count, 1)
        XCTAssertEqual(result.candidates[0].innerRadiusMm, 24.1)
    }

    func testCoarseResultMissingWarningsUseDefaults() throws {
        let result = try decode(CalibrationCoarseResult.self, from: #"{"succeeded": true, "bestIndex": 0}"#)
        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.warnings, [])
        XCTAssertEqual(result.bestIndex, 0)
        XCTAssertEqual(result.candidates, [])

        let empty = try decode(CalibrationCoarseResult.self, from: "{}")
        XCTAssertFalse(empty.succeeded)
        XCTAssertEqual(empty.bestIndex, -1)
        XCTAssertEqual(empty.warnings, [])
    }

    // MARK: - 可应用阈值

    func testFitIsApplicableUsesRmsAndMaxThresholds() {
        let result = CalibrationFitResult(
            succeeded: true,
            reliable: true,
            boundaryLimited: false,
            warnings: [],
            bestIndex: 0,
            candidates: []
        )
        func candidate(rms: Double, max: Double) -> CalibrationCandidate {
            CalibrationCandidate(innerRadiusMm: 24, outerRadiusMm: 58, rmsErrorMm: rms, maxErrorMm: max)
        }

        XCTAssertEqual(CalibrationFitResult.applicableRmsMm, 0.35)
        XCTAssertEqual(CalibrationFitResult.applicableMaxMm, 0.8)
        XCTAssertTrue(result.isApplicable(candidate(rms: 0.35, max: 0.8)), "阈值边界应包含")
        XCTAssertTrue(result.isApplicable(candidate(rms: 0.1, max: 0.2)))
        XCTAssertFalse(result.isApplicable(candidate(rms: 0.351, max: 0.2)))
        XCTAssertFalse(result.isApplicable(candidate(rms: 0.1, max: 0.801)))

        let failed = CalibrationFitResult(
            succeeded: false,
            reliable: true,
            boundaryLimited: false,
            warnings: [],
            bestIndex: 0,
            candidates: []
        )
        XCTAssertFalse(failed.isApplicable(candidate(rms: 0, max: 0)))
    }

    func testCoarseIsApplicableRequiresSuccessNoWarningsAndValidBestIndex() {
        let candidate = CalibrationCandidate(innerRadiusMm: 24, outerRadiusMm: 58, rmsErrorMm: 0.1, maxErrorMm: 0.2)
        XCTAssertTrue(
            CalibrationCoarseResult(succeeded: true, warnings: [], bestIndex: 0, candidates: [candidate])
                .isApplicable
        )
        XCTAssertFalse(
            CalibrationCoarseResult(succeeded: true, warnings: ["方向不唯一"], bestIndex: 0, candidates: [candidate])
                .isApplicable
        )
        XCTAssertFalse(
            CalibrationCoarseResult(succeeded: true, warnings: [], bestIndex: -1, candidates: [candidate])
                .isApplicable
        )
        XCTAssertFalse(
            CalibrationCoarseResult(succeeded: true, warnings: [], bestIndex: 1, candidates: [candidate])
                .isApplicable
        )
        XCTAssertFalse(
            CalibrationCoarseResult(succeeded: false, warnings: [], bestIndex: 0, candidates: [candidate])
                .isApplicable
        )
    }

    // MARK: - 其他模型

    func testSessionSummaryToleratesMissingFields() throws {
        let summary = try decode(CalibrationSessionSummary.self, from: "{}")
        XCTAssertNil(summary.family)
        XCTAssertNil(summary.innerRadiusMm)
        XCTAssertNil(summary.sectors)
        XCTAssertNil(summary.hasPhoto)
        XCTAssertNil(summary.tracePointCount)

        let full = try decode(
            CalibrationSessionSummary.self,
            from: #"""
            {
              "family": "CompactDisc",
              "innerRadiusMm": 24.5,
              "outerRadiusMm": 56.8,
              "sectors": 359849,
              "density": 1200,
              "pitchLinear": 0,
              "pitchQuadratic": 0,
              "pitchCubic": 0,
              "invertMapping": false,
              "hasPhoto": true,
              "rectificationConfirmed": true,
              "tracePointCount": 12,
              "photoWidth": 2000,
              "photoHeight": 1500,
              "drawingStartSector": 0,
              "drawingEndSector": 359849
            }
            """#
        )
        XCTAssertEqual(full.family, "CompactDisc")
        XCTAssertEqual(full.innerRadiusMm, 24.5)
        XCTAssertEqual(full.tracePointCount, 12)
        XCTAssertEqual(full.hasPhoto, true)
    }

    func testCalibrationPointCodableAndCGPointBridge() throws {
        let point = CalibrationPoint(x: -1.5, y: 2.25)
        let data = try JSONEncoder().encode(point)
        XCTAssertEqual(try JSONDecoder().decode(CalibrationPoint.self, from: data), point)
        XCTAssertEqual(point.cgPoint, CGPoint(x: -1.5, y: 2.25))
        XCTAssertEqual(CalibrationPoint(CGPoint(x: 3.5, y: -4)), CalibrationPoint(x: 3.5, y: -4))
    }

    func testDiscSizeAndDensityDefaults() {
        XCTAssertEqual(CalibrationDiscSize.standard.rawValue, 120)
        XCTAssertEqual(CalibrationDiscSize.mini.rawValue, 80)
        XCTAssertEqual(CalibrationDiscSize.standard.physicalOuterRadiusMm, 60)
        XCTAssertEqual(CalibrationDiscSize.mini.physicalOuterRadiusMm, 40)
        XCTAssertEqual(CalibrationDiscSize.standard.displayName, "标准 12 cm 光盘")
        XCTAssertEqual(CalibrationDiscSize.mini.displayName, "8 cm 小盘")

        XCTAssertEqual(CalibrationDensity.default(for: .cd), 1200)
        XCTAssertEqual(CalibrationDensity.default(for: .dvd), 133.3)
        XCTAssertEqual(CalibrationDensity.default(for: .bd), 74.5)
    }

    func testCalibrationErrorDescriptions() {
        XCTAssertEqual(
            CalibrationError.invalidGeometry("bad").errorDescription,
            "标定参数无效：bad"
        )
        XCTAssertEqual(
            CalibrationError.commandFailed("boom").errorDescription,
            "标定命令失败：boom"
        )
        XCTAssertEqual(
            CalibrationError.malformedResult("bad json").errorDescription,
            "无法解析标定结果：bad json"
        )
        XCTAssertEqual(
            CalibrationError.missingInput("photo").errorDescription,
            "缺少标定输入：photo"
        )
    }

    // MARK: - Helpers

    private func decode<T: Decodable>(_ type: T.Type, from json: String) throws -> T {
        try JSONDecoder().decode(type, from: Data(json.utf8))
    }
}
