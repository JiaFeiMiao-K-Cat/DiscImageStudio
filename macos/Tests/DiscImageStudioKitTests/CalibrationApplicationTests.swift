import XCTest

@testable import DiscImageStudioKit

/// 标定候选 → 工程几何的应用规则（与 Windows CalibrationViewModel.ApplyParameters 对齐）。
final class CalibrationApplicationTests: XCTestCase {
    func testCdCandidateUpdatesRadiiOnly() throws {
        let project = try makeProject(family: .cd)
        let candidate = CalibrationCandidate(innerRadiusMm: 24.53, outerRadiusMm: 56.74)
        let updated = project.applyingCalibration(candidate)

        XCTAssertEqual(updated.geometry.innerRadiusMm, 24.53)
        XCTAssertEqual(updated.geometry.outerRadiusMm, 56.74)
        XCTAssertEqual(updated.geometry.sectors, project.geometry.sectors)
        XCTAssertEqual(updated.discFamily, .cd)
        XCTAssertEqual(updated.name, project.name)
        XCTAssertEqual(updated.outputs, project.outputs)
        XCTAssertEqual(updated.layers, project.layers)
        XCTAssertEqual(updated.tone, project.tone)
        XCTAssertNil(updated.bluRay)
    }

    func testDvdCandidateAlsoUpdatesCubicPitch() throws {
        let project = try makeProject(family: .dvd)
        let candidate = CalibrationCandidate(
            innerRadiusMm: 24.1,
            outerRadiusMm: 57.9,
            pitchLinear: -0.0002,
            pitchQuadratic: 0.0003,
            pitchCubic: -0.0001
        )
        let updated = project.applyingCalibration(candidate)

        XCTAssertEqual(updated.geometry.innerRadiusMm, 24.1)
        XCTAssertEqual(updated.geometry.outerRadiusMm, 57.9)
        XCTAssertEqual(updated.geometry.pitchLinear, -0.0002)
        XCTAssertEqual(updated.geometry.pitchQuadratic, 0.0003)
        XCTAssertEqual(updated.geometry.pitchCubic, -0.0001)
        XCTAssertEqual(updated.geometry.sectors, project.geometry.sectors)
        XCTAssertEqual(updated.discFamily, .dvd)
    }

    func testBluRayCandidateUpdatesSectorsAndPolarity() throws {
        let project = try makeProject(family: .bd)
        let candidate = CalibrationCandidate(
            innerRadiusMm: 24.2,
            outerRadiusMm: 57.8,
            sectors: 12_219_392,
            invertMapping: true
        )
        let updated = project.applyingCalibration(candidate)

        XCTAssertEqual(updated.geometry.innerRadiusMm, 24.2)
        XCTAssertEqual(updated.geometry.outerRadiusMm, 57.8)
        XCTAssertEqual(updated.geometry.sectors, 12_219_392)
        XCTAssertEqual(updated.bluRaySettings.invertMapping, true)
        XCTAssertEqual(updated.discFamily, .bd)
    }

    func testCandidateWithoutOptionalFieldsKeepsSectorsAndPolarity() throws {
        let project = try makeProject(family: .bd)
        let candidate = CalibrationCandidate(innerRadiusMm: 24.2, outerRadiusMm: 57.8)
        let updated = project.applyingCalibration(candidate)

        XCTAssertEqual(updated.geometry.sectors, project.geometry.sectors)
        XCTAssertEqual(updated.bluRaySettings.invertMapping, project.bluRaySettings.invertMapping)
    }

    private func makeProject(family: DiscFamily) throws -> ProjectDocument {
        let geometry: ProjectGeometry
        switch family {
        case .cd:
            geometry = ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849)
        case .dvd:
            geometry = ProjectGeometry(innerRadiusMm: 24, outerRadiusMm: 58, sectors: 2_295_104)
        case .bd:
            geometry = ProjectGeometry(innerRadiusMm: 24, outerRadiusMm: 58, sectors: 12_219_392)
        }
        let preset = DiscPreset(
            id: "test-\(family.rawValue)",
            displayName: "Test",
            description: "",
            family: family,
            geometry: geometry,
            invertMapping: false
        )
        return ProjectDocument.make(
            name: "标定测试",
            family: family,
            preset: preset,
            outputPath: "/tmp/test-\(family.rawValue).iso"
        )
    }
}
