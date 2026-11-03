import Foundation
import XCTest

@testable import DiscImageStudioKit

/// `.calibration.json` 单文件方案的解码、目录扫描与命名约定。
final class CalibrationSessionTests: XCTestCase {
    private static let onePixelPNGBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="

    func testDecodesTargetParametersAndPhoto() throws {
        let json = makeSessionJSON(family: "Dvd", inner: 24, outer: 58, sectors: 2_295_104,
                                   density: 133.3, photo: true)
        let data = try JSONSerialization.data(withJSONObject: json)
        let document = try JSONDecoder().decode(CalibrationSessionDocument.self, from: data)

        XCTAssertEqual(document.target.id, "abc123")
        XCTAssertEqual(document.target.parameters.family, .dvd)
        XCTAssertEqual(document.target.parameters.innerRadiusMm, 24)
        XCTAssertEqual(document.target.parameters.outerRadiusMm, 58)
        XCTAssertEqual(document.target.parameters.sectors, 2_295_104)
        XCTAssertEqual(document.target.parameters.density, 133.3)
        XCTAssertEqual(document.target.parameters.projectGeometry.innerRadiusMm, 24)
        XCTAssertEqual(document.target.parameters.projectGeometry.outerRadiusMm, 58)

        let photo = try XCTUnwrap(document.photo)
        XCTAssertTrue(photo.rectificationConfirmed)
        XCTAssertEqual(photo.discSize, .standard)
        XCTAssertEqual(photo.outerBoundary.count, 8)
        XCTAssertEqual(photo.holeBoundary.count, 8)
        XCTAssertEqual(photo.traces.count, 1)
        XCTAssertEqual(photo.polygonPoints.count, 3)
        XCTAssertEqual(photo.traces[0].armIndex, 0)
        XCTAssertNotNil(photo.pngData)
    }

    func testMiniDiscSizeAndUnconfirmedSession() throws {
        let json = makeSessionJSON(family: "Cd", inner: 24.5, outer: 56.8, sectors: 359_849,
                                   density: 1_200, photo: true,
                                   confirmed: false, boundaryRadius: 38, traceCount: 0)
        let data = try JSONSerialization.data(withJSONObject: json)
        let document = try JSONDecoder().decode(CalibrationSessionDocument.self, from: data)
        let photo = try XCTUnwrap(document.photo)
        XCTAssertFalse(photo.rectificationConfirmed)
        XCTAssertTrue(photo.polygonPoints.isEmpty)
        // 8 cm 盘的实体外半径是 40 mm。
        XCTAssertEqual(photo.discSize, .mini)
    }

    func testSessionWithoutPhotoDecodes() throws {
        let json = makeSessionJSON(family: "Cd", inner: 24.5, outer: 56.8, sectors: 359_849,
                                   density: 1_200, photo: false)
        let data = try JSONSerialization.data(withJSONObject: json)
        let document = try JSONDecoder().decode(CalibrationSessionDocument.self, from: data)
        XCTAssertNil(document.photo)
        XCTAssertEqual(document.target.parameters.family, .cd)
    }

    func testWritesEmbeddedPhotoToDisk() throws {
        let json = makeSessionJSON(family: "Cd", inner: 24.5, outer: 56.8, sectors: 359_849,
                                   density: 1_200, photo: true)
        let data = try JSONSerialization.data(withJSONObject: json)
        let document = try JSONDecoder().decode(CalibrationSessionDocument.self, from: data)
        let photo = try XCTUnwrap(document.photo)

        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("photo.png")
        try photo.writePhoto(to: url)
        let written = try Data(contentsOf: url)
        XCTAssertEqual(Array(written.prefix(8)), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
    }

    func testWorkspaceLayoutNaming() {
        let workspace = URL(fileURLWithPath: "/tmp/project/calibration")
        let layout = CalibrationWorkspaceLayout(workspace: workspace)
        XCTAssertEqual(layout.sessionURL(id: "abc").lastPathComponent, "abc.calibration.json")
        XCTAssertEqual(layout.targetImageURL(id: "abc").lastPathComponent, "target-abc.png")
        XCTAssertEqual(layout.photoURL(id: "abc").lastPathComponent, "photo-abc.png")
        XCTAssertEqual(layout.rectifiedURL(id: "abc").lastPathComponent, "rectified-abc.png")
        XCTAssertEqual(layout.coarseImageURL(id: "abc").lastPathComponent, "coarse-abc.png")
        XCTAssertEqual(
            CalibrationWorkspaceLayout.id(fromSessionURL: layout.sessionURL(id: "abc")),
            "abc"
        )
    }

    func testCatalogScansSessionsAndIgnoresMalformedFiles() throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let layout = CalibrationWorkspaceLayout(workspace: directory)

        for (index, name) in ["first", "second"].enumerated() {
            let json = makeSessionJSON(family: "Cd", inner: 24.5, outer: 56.8, sectors: 359_849,
                                       density: 1_200, photo: index == 1)
            let data = try JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted])
            try data.write(to: layout.sessionURL(id: name))
            // 让排序稳定：第一个文件更旧。
            try FileManager.default.setAttributes(
                [.modificationDate: Date().addingTimeInterval(TimeInterval(-1000 * (index + 1)))],
                ofItemAtPath: layout.sessionURL(id: name).path
            )
        }
        try Data("not json".utf8).write(to: layout.sessionURL(id: "broken"))

        let catalog = CalibrationSessionCatalog(workspace: directory)
        let entries = catalog.entries()
        XCTAssertEqual(entries.count, 2)
        XCTAssertEqual(entries.map(\.path.lastPathComponent), ["first.calibration.json", "second.calibration.json"])
        XCTAssertFalse(entries[0].isConfirmed)
        XCTAssertTrue(entries[1].isConfirmed)
        XCTAssertEqual(entries[0].targetID, "abc123")
        XCTAssertEqual(entries[0].document.target.parameters.family, .cd)

        let loadedFirst = try catalog.loadSession(at: entries[0].path)
        XCTAssertNil(loadedFirst.photo)
        let loadedSecond = try catalog.loadSession(at: entries[1].path)
        XCTAssertNotNil(loadedSecond.photo)

        try catalog.delete(entries[0])
        XCTAssertEqual(catalog.entries().map(\.path.lastPathComponent), ["second.calibration.json"])
    }

    // MARK: - Fixture

    private func makeSessionJSON(
        family: String,
        inner: Double,
        outer: Double,
        sectors: Int64,
        density: Double,
        photo: Bool,
        confirmed: Bool = true,
        boundaryRadius: Double = 900,
        traceCount: Int = 3
    ) -> [String: Any] {
        var parameters: [String: Any] = [:]
        parameters["kind"] = family
        parameters["innerRadiusMm"] = inner
        parameters["outerRadiusMm"] = outer
        parameters["sectors"] = sectors
        parameters["linearDensity"] = density
        parameters["pitchLinear"] = 0.0
        parameters["pitchQuadratic"] = 0.0
        parameters["pitchCubic"] = 0.0
        parameters["bluRayInvertMapping"] = false
        parameters["drawingStartSector"] = 0
        parameters["drawingEndSectorExclusive"] = 0

        var options: [String: Any] = [:]
        options["largeCellMm"] = 8.0
        options["mediumCellMm"] = 4.0
        options["smallCellMm"] = 2.0
        options["sectorGapMm"] = 1.0

        var target: [String: Any] = [:]
        target["schemaVersion"] = 6
        target["id"] = "abc123"
        target["parameters"] = parameters
        target["options"] = options
        target["cells"] = [[String: Any]]()

        var root: [String: Any] = [:]
        root["target"] = target
        root["observations"] = [[String: Any]]()
        root["fitOptions"] = NSNull()
        root["cdInterleave"] = true

        if photo {
            let count = 8
            let center = boundaryRadius / 0.43
            var outerPoints: [[String: Double]] = []
            var holePoints: [[String: Double]] = []
            for index in 0..<count {
                let angle = 2.0 * Double.pi * Double(index) / Double(count)
                let ox = center + boundaryRadius * cos(angle)
                let oy = center + boundaryRadius * sin(angle)
                outerPoints.append(["x": ox, "y": oy])
                let hr = boundaryRadius * 0.125
                let hx = center + hr * cos(angle)
                let hy = center + hr * sin(angle)
                holePoints.append(["x": hx, "y": hy])
            }

            var tracePoints: [[String: Double]] = []
            for index in 0..<traceCount {
                let span = outer - inner - 10
                let ratio = Double(index) / Double(max(traceCount - 1, 1))
                let radius = inner + 5 + span * ratio
                tracePoints.append(["x": radius, "y": 0.0])
            }
            var trace: [String: Any] = [:]
            trace["armIndex"] = 0
            trace["points"] = tracePoints
            var traces: [[String: Any]] = []
            if traceCount > 0 { traces.append(trace) }

            var photoInfo: [String: Any] = [:]
            photoInfo["imagePngBase64"] = Self.onePixelPNGBase64
            photoInfo["outerBoundary"] = outerPoints
            photoInfo["holeBoundary"] = holePoints
            photoInfo["physicalOuterRadiusMm"] = boundaryRadius > 500 ? 60.0 : 40.0
            photoInfo["rectificationConfirmed"] = confirmed
            photoInfo["traces"] = traces
            root["photo"] = photoInfo
        }
        return root
    }
}
