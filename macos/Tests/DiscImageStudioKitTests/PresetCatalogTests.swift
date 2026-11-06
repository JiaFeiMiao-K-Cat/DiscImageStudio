import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住内置预设（schema 4）的解码、禁用过滤与 CLI 加载（WORKFLOW.md §3）。
final class PresetCatalogTests: XCTestCase {

    // MARK: - 内置预设夹具

    func testDecodesBuiltInSchema4Fixture() throws {
        let catalog = try PresetCatalog.decode(try TestSupport.fixtureData(named: "built-in-disc-presets"))

        XCTAssertEqual(catalog.document.schemaVersion, 4)
        XCTAssertEqual(PresetCatalog.supportedSchemaVersion, 4)
        XCTAssertGreaterThanOrEqual(catalog.document.cdPresets.count, 4)
        XCTAssertGreaterThanOrEqual(catalog.document.dvdPresets.count, 3)
        XCTAssertGreaterThanOrEqual(catalog.document.bluRayPresets?.count ?? 0, 3)

        XCTAssertEqual(catalog.presets(for: .cd).count, catalog.document.cdPresets.count)
        XCTAssertEqual(catalog.presets(for: .dvd).count, catalog.document.dvdPresets.count)
        XCTAssertEqual(catalog.presets(for: .bd).count, catalog.document.bluRayPresets?.count ?? 0)
        XCTAssertEqual(catalog.allPresets.count,
                       catalog.document.cdPresets.count
                       + catalog.document.dvdPresets.count
                       + (catalog.document.bluRayPresets?.count ?? 0))

        let cd80 = try XCTUnwrap(catalog.preset(id: "cd-80"))
        XCTAssertEqual(cd80.family, .cd)
        XCTAssertEqual(cd80.displayName, "标准 80min CD")
        XCTAssertEqual(cd80.geometry.innerRadiusMm, 24.5, accuracy: 0.000_001)
        XCTAssertEqual(cd80.geometry.outerRadiusMm, 56.8, accuracy: 0.000_001)
        XCTAssertEqual(cd80.geometry.sectors, 359_849)
        XCTAssertEqual(cd80.geometry.pitchLinear, 0)
        XCTAssertFalse(cd80.invertMapping)

        let dvd5 = try XCTUnwrap(catalog.preset(id: "dvd-5-120mm"))
        XCTAssertEqual(dvd5.family, .dvd)
        XCTAssertEqual(dvd5.geometry.innerRadiusMm, 24.0, accuracy: 0.000_001)
        XCTAssertEqual(dvd5.geometry.outerRadiusMm, 58.0, accuracy: 0.000_001)
        XCTAssertEqual(dvd5.geometry.sectors, 2_295_104)

        let bd = try XCTUnwrap(catalog.preset(id: "bd-r-sl-25gb"))
        XCTAssertEqual(bd.family, .bd)
        XCTAssertEqual(bd.geometry.sectors, 12_219_392)
        XCTAssertFalse(bd.invertMapping)
    }

    func testDvdPitchCoefficientsComeFromFixture() throws {
        let catalog = try PresetCatalog.decode(try TestSupport.fixtureData(named: "built-in-disc-presets"))
        let verbatim = try XCTUnwrap(catalog.preset(id: "verbatim-dvd-r-azo-43533"))

        XCTAssertEqual(verbatim.geometry.innerRadiusMm, 23.991460627731737, accuracy: 1e-12)
        XCTAssertEqual(verbatim.geometry.outerRadiusMm, 57.964893657925366, accuracy: 1e-12)
        XCTAssertEqual(verbatim.geometry.pitchLinear, -3.9256854319227684e-05, accuracy: 1e-15)
        XCTAssertEqual(verbatim.geometry.pitchQuadratic, 6.964795915429256e-05, accuracy: 1e-15)
        XCTAssertEqual(verbatim.geometry.pitchCubic, -4.670918950137523e-05, accuracy: 1e-15)
        XCTAssertEqual(verbatim.geometry.sectors, 2_297_888)
    }

    func testPresetsAreGroupedByFamily() throws {
        let catalog = try PresetCatalog.decode(try TestSupport.fixtureData(named: "built-in-disc-presets"))

        XCTAssertTrue(catalog.presets(for: .cd).allSatisfy { $0.family == .cd })
        XCTAssertTrue(catalog.presets(for: .dvd).allSatisfy { $0.family == .dvd })
        XCTAssertTrue(catalog.presets(for: .bd).allSatisfy { $0.family == .bd })

        // 预设会随上游增加；这里只钉住「每个文档条目都进入对应家族」，不写死列表。
        XCTAssertEqual(Set(catalog.presets(for: .cd).map(\.id)), Set(catalog.document.cdPresets.map(\.id)))
        XCTAssertEqual(Set(catalog.presets(for: .dvd).map(\.id)), Set(catalog.document.dvdPresets.map(\.id)))
        XCTAssertEqual(
            Set(catalog.presets(for: .bd).map(\.id)),
            Set((catalog.document.bluRayPresets ?? []).map(\.id))
        )
        XCTAssertTrue(catalog.presets(for: .cd).contains { $0.id == "cd-80" })
        XCTAssertTrue(catalog.presets(for: .dvd).contains { $0.id == "dvd-5-120mm" })
        XCTAssertTrue(catalog.presets(for: .bd).contains { $0.id == "bd-r-sl-25gb" })
    }

    func testDisabledPresetIdsAreFilteredPerFamily() throws {
        var object = try XCTUnwrap(
            JSONSerialization.jsonObject(
                with: try TestSupport.fixtureData(named: "built-in-disc-presets")
            ) as? [String: Any]
        )
        object["disabledCdPresetIds"] = ["cd-80", "cd-74"]
        object["disabledDvdPresetIds"] = ["dvd-5-120mm"]
        object["disabledBluRayPresetIds"] = ["bd-r-sl-25gb"]
        let data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])

        let catalog = try PresetCatalog.decode(data)
        XCTAssertFalse(catalog.presets(for: .cd).contains { $0.id == "cd-80" })
        XCTAssertFalse(catalog.presets(for: .cd).contains { $0.id == "cd-74" })
        XCTAssertFalse(catalog.presets(for: .dvd).contains { $0.id == "dvd-5-120mm" })
        XCTAssertFalse(catalog.presets(for: .bd).contains { $0.id == "bd-r-sl-25gb" })
        XCTAssertNil(catalog.preset(id: "cd-80"))
        XCTAssertEqual(
            catalog.allPresets.count,
            catalog.document.cdPresets.count - 2
            + catalog.document.dvdPresets.count - 1
            + (catalog.document.bluRayPresets?.count ?? 0) - 1
        )
    }

    // MARK: - 错误路径

    func testUnsupportedAndMalformedPresetDocumentsAreRejected() throws {
        var newer = try XCTUnwrap(
            JSONSerialization.jsonObject(
                with: try TestSupport.fixtureData(named: "built-in-disc-presets")
            ) as? [String: Any]
        )
        newer["schemaVersion"] = 5
        XCTAssertThrowsError(
            try PresetCatalog.decode(JSONSerialization.data(withJSONObject: newer))
        ) { error in
            XCTAssertEqual(error as? PresetCatalogError, .unsupportedSchema(5))
        }

        var tooOld = newer
        tooOld["schemaVersion"] = 1
        XCTAssertThrowsError(
            try PresetCatalog.decode(JSONSerialization.data(withJSONObject: tooOld))
        ) { error in
            XCTAssertEqual(error as? PresetCatalogError, .unsupportedSchema(1))
        }

        XCTAssertThrowsError(try PresetCatalog.decode(Data("not json".utf8))) { error in
            guard case PresetCatalogError.malformed = error else {
                return XCTFail("坏 JSON 应抛 malformed，实际是 \(error)")
            }
        }
    }

    // MARK: - PresetLoader

    func testPresetLoaderRunsCliAndDecodesPresets() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        try Data(try TestSupport.fixtureData(named: "built-in-disc-presets"))
            .write(to: directory.appendingPathComponent("presets.json"))
        let script = try TestSupport.writeExecutableScript(
            in: directory,
            name: "fake-cli",
            contents: """
            #!/bin/sh
            cat "$(dirname "$0")/presets.json"
            """
        )

        let loader = PresetLoader(cliInvocation: CliInvocation(executableURL: script, label: "fake CLI"))
        let catalog = try await loader.load()
        XCTAssertEqual(catalog.document.schemaVersion, 4)
        XCTAssertEqual(catalog.presets(for: .cd).count, catalog.document.cdPresets.count)
        XCTAssertEqual(catalog.presets(for: .bd).count, catalog.document.bluRayPresets?.count ?? 0)
        XCTAssertGreaterThanOrEqual(catalog.presets(for: .cd).count, 1)
    }

    func testPresetLoaderMapsNonZeroExitAndBadJSONToMalformed() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let failing = try TestSupport.writeExecutableScript(
            in: directory,
            name: "failing-cli",
            contents: """
            #!/bin/sh
            echo "presets unavailable" >&2
            exit 3
            """
        )
        let failureLoader = PresetLoader(
            cliInvocation: CliInvocation(executableURL: failing, label: "fake CLI")
        )
        do {
            _ = try await failureLoader.load()
            XCTFail("非 0 退出码应抛 PresetCatalogError.malformed")
        } catch let error as PresetCatalogError {
            guard case .malformed(let reason) = error else {
                return XCTFail("应抛 malformed，实际是 \(error)")
            }
            XCTAssertEqual(reason, "presets unavailable")
        }

        let badJSON = try TestSupport.writeExecutableScript(
            in: directory,
            name: "bad-json-cli",
            contents: """
            #!/bin/sh
            echo "not json"
            """
        )
        let badLoader = PresetLoader(
            cliInvocation: CliInvocation(executableURL: badJSON, label: "fake CLI")
        )
        do {
            _ = try await badLoader.load()
            XCTFail("坏 JSON 应抛 PresetCatalogError.malformed")
        } catch let error as PresetCatalogError {
            guard case .malformed = error else {
                return XCTFail("应抛 malformed，实际是 \(error)")
            }
        }
    }
}
