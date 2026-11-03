import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住 `.discproj` 的 Windows 兼容格式、校验与原子保存（WORKFLOW.md §3）。
final class ProjectModelTests: XCTestCase {

    private var temporaryDirectory: URL!

    override func setUpWithError() throws {
        temporaryDirectory = try TestSupport.makeTemporaryDirectory("ProjectModelTests")
    }

    override func tearDownWithError() throws {
        if let temporaryDirectory {
            try? FileManager.default.removeItem(at: temporaryDirectory)
        }
        temporaryDirectory = nil
    }

    // MARK: - schema 2 camelCase 解码与往返

    func testSchema2CamelCaseProjectRoundTripsKeyFields() throws {
        let url = try writeProject(makeCDProjectObject())
        let store = ProjectStore(projectsDirectory: temporaryDirectory)
        let loaded = try store.load(from: url)

        XCTAssertEqual(loaded.schemaVersion, 2)
        XCTAssertEqual(loaded.name, "测试工程")
        XCTAssertEqual(loaded.discFamily, .cd)
        XCTAssertEqual(
            loaded.geometry,
            ProjectGeometry(
                innerRadiusMm: 24.5,
                outerRadiusMm: 56.8,
                sectors: 359_849,
                pitchLinear: 0,
                pitchQuadratic: 0,
                pitchCubic: 0
            )
        )
        XCTAssertEqual(loaded.layers.count, 2)
        let top = loaded.layers[0]
        XCTAssertEqual(top.id, "layer-top")
        XCTAssertEqual(top.name, "Top")
        XCTAssertEqual(top.imagePath, "/tmp/top.png")
        XCTAssertTrue(top.visible)
        XCTAssertEqual(top.translateXMm, 1.25, accuracy: 0.000_001)
        XCTAssertEqual(top.translateYMm, -2.5, accuracy: 0.000_001)
        XCTAssertEqual(top.rotationDeg, 30, accuracy: 0.000_001)
        XCTAssertEqual(top.scale, 1.5, accuracy: 0.000_001)
        XCTAssertEqual(top.scalePercent, 150, accuracy: 0.000_001)

        XCTAssertEqual(loaded.outputs.outputPath, "/tmp/out.wav")
        XCTAssertFalse(loaded.outputs.writeCue)
        XCTAssertEqual(loaded.outputs.dataDirectory, "/tmp/data")
        XCTAssertEqual(loaded.outputs.volumeLabel, "MY_CD")
        XCTAssertFalse(loaded.outputs.interleave)
        XCTAssertTrue(loaded.outputs.filesAtOuterRing)
        XCTAssertTrue(loaded.outputs.hasBundledFiles)

        XCTAssertEqual(loaded.calibrationSessionPath, "/tmp/calibration.json")
        XCTAssertEqual(loaded.createdUtc, "2026-01-02T03:04:05.678+08:00")
        XCTAssertEqual(loaded.modifiedUtc, "2026-01-02T04:05:06.789+08:00")
        XCTAssertEqual(
            loaded.tone,
            ProjectTone(ditherStrength: 0.5, levels: 4, brightness: -10, srgbLinearization: true)
        )
        XCTAssertNil(loaded.bluRay)

        // 直接再编码 → 再解码，语义完全一致。
        let reencoded = try JSONEncoder().encode(loaded)
        let decodedAgain = try JSONDecoder().decode(ProjectDocument.self, from: reencoded)
        XCTAssertEqual(decodedAgain, loaded)
    }

    func testSchema2BluRayProjectCarriesBluRaySettings() throws {
        let url = try writeProject(makeBluRayProjectObject())
        let store = ProjectStore(projectsDirectory: temporaryDirectory)
        let loaded = try store.load(from: url)

        XCTAssertEqual(loaded.discFamily, .bd)
        XCTAssertEqual(loaded.bluRay, ProjectBluRaySettings(invertMapping: true))
        XCTAssertEqual(loaded.bluRaySettings.invertMapping, true)
        XCTAssertEqual(loaded.canvasOuterRadiusMm, 58.0, "BD 画布外半径取生成外半径")
        XCTAssertEqual(loaded.geometry.sectors, 12_219_392)
        XCTAssertEqual(loaded.tone?.levels, 2, "BD 影调固定两级")
    }

    func testSchema1IsNormalizedToCurrentSchema() throws {
        let store = ProjectStore(projectsDirectory: temporaryDirectory)

        let cdURL = try writeProject(makeCDProjectObject(schemaVersion: 1, includeTone: false))
        let cd = try store.load(from: cdURL)
        XCTAssertEqual(cd.schemaVersion, 2, "读取 schema 1 后应按当前 schema 归一化")
        XCTAssertEqual(cd.discFamily, .cd)
        XCTAssertNil(cd.tone, "旧工程保留 tone 缺失状态")
        XCTAssertEqual(cd.toneOrLegacy, .legacyBinary, "缺 tone 时按硬阈值两级处理")

        var dvdObject = makeCDProjectObject(schemaVersion: 1, includeTone: false)
        dvdObject["discFamily"] = "dvd"
        dvdObject["geometry"] = [
            "innerRadiusMm": 24.0,
            "outerRadiusMm": 58.0,
            "sectors": 2_295_104,
            "pitchLinear": 0,
            "pitchQuadratic": 0,
            "pitchCubic": 0,
        ]
        dvdObject["outputs"] = [
            "outputPath": "/tmp/dvd.iso",
            "writeCue": true,
            "dataDirectory": "",
            "volumeLabel": "DISC_IMAGE",
            "interleave": false,
            "filesAtOuterRing": false,
        ]
        let dvd = try store.load(from: try writeProject(dvdObject))
        XCTAssertEqual(dvd.schemaVersion, 2)
        XCTAssertEqual(dvd.discFamily, .dvd)
        XCTAssertEqual(dvd.tone, .legacyBinary)
    }

    func testSchema1BluRayIsReadAsCD() throws {
        // schema 1 没有蓝光；若文件声明 bd，与 Windows 的行为对齐：按 CD 读取。
        var object = makeBluRayProjectObject(schemaVersion: 1)
        object["bluRay"] = nil
        let loaded = try ProjectStore(projectsDirectory: temporaryDirectory)
            .load(from: try writeProject(object))
        XCTAssertEqual(loaded.schemaVersion, 2)
        XCTAssertEqual(loaded.discFamily, .cd)
        XCTAssertNil(loaded.bluRay)
    }

    func testUnknownFieldsAreIgnored() throws {
        var object = makeCDProjectObject()
        object["futureSection"] = ["a": 1]
        object["imageProcessing"] = ["mode": "legacy"]
        let loaded = try ProjectStore(projectsDirectory: temporaryDirectory)
            .load(from: try writeProject(object))
        XCTAssertEqual(loaded.name, "测试工程")

        let reencoded = try JSONEncoder().encode(loaded)
        let text = try XCTUnwrap(String(data: reencoded, encoding: .utf8))
        XCTAssertFalse(text.contains("futureSection"))
        XCTAssertFalse(text.contains("imageProcessing"))
    }

    // MARK: - 校验

    func testNegativeOrInvertedRadiusThrowsProjectModelError() throws {
        var negative = makeCDProjectObject()
        negative["geometry"] = [
            "innerRadiusMm": -1.0,
            "outerRadiusMm": 56.8,
            "sectors": 359_849,
            "pitchLinear": 0,
            "pitchQuadratic": 0,
            "pitchCubic": 0,
        ]
        XCTAssertThrowsError(try loadProject(negative)) { error in
            guard case ProjectModelError.invalidGeometry = error else {
                return XCTFail("负半径应抛 invalidGeometry，实际是 \(error)")
            }
        }

        var inverted = makeCDProjectObject()
        inverted["geometry"] = [
            "innerRadiusMm": 56.8,
            "outerRadiusMm": 24.5,
            "sectors": 359_849,
            "pitchLinear": 0,
            "pitchQuadratic": 0,
            "pitchCubic": 0,
        ]
        XCTAssertThrowsError(try loadProject(inverted)) { error in
            guard case ProjectModelError.invalidGeometry = error else {
                return XCTFail("内半径大于外半径应抛 invalidGeometry，实际是 \(error)")
            }
        }
    }

    func testEmptyOutputPathThrowsProjectModelError() throws {
        var object = makeCDProjectObject()
        object["outputs"] = [
            "outputPath": "   ",
            "writeCue": true,
            "dataDirectory": "",
            "volumeLabel": "CD_IMAGE",
            "interleave": true,
            "filesAtOuterRing": false,
        ]
        XCTAssertThrowsError(try loadProject(object)) { error in
            guard case ProjectModelError.invalidOutputs(let reason) = error else {
                return XCTFail("空输出路径应抛 invalidOutputs，实际是 \(error)")
            }
            XCTAssertTrue(reason.contains("输出"))
        }
    }

    func testUnsupportedSchemaThrowsProjectModelError() throws {
        var object = makeCDProjectObject()
        object["schemaVersion"] = 3
        XCTAssertThrowsError(try loadProject(object)) { error in
            XCTAssertEqual(error as? ProjectModelError, .unsupportedSchema(3))
        }
    }

    func testMissingRequiredFieldIsRejected() throws {
        var object = makeCDProjectObject()
        object.removeValue(forKey: "geometry")
        XCTAssertThrowsError(try loadProject(object), "缺少 geometry 的工程必须被拒绝") { error in
            guard case ProjectModelError.malformedDocument(let reason) = error else {
                return XCTFail("缺字段应抛 malformedDocument，实际是 \(error)")
            }
            XCTAssertTrue(reason.contains("geometry"), "错误信息应包含字段名：\(reason)")
        }
    }

    func testMalformedJSONAndUnreadableFileThrowProjectModelErrors() throws {
        let malformedURL = temporaryDirectory.appendingPathComponent("malformed.discproj")
        try Data("{ this is not a project".utf8).write(to: malformedURL)
        XCTAssertThrowsError(try ProjectStore(projectsDirectory: temporaryDirectory).load(from: malformedURL)) { error in
            guard case ProjectModelError.malformedDocument = error else {
                return XCTFail("坏 JSON 应抛 malformedDocument，实际是 \(error)")
            }
        }

        let missingURL = temporaryDirectory.appendingPathComponent("missing.discproj")
        XCTAssertThrowsError(try ProjectStore(projectsDirectory: temporaryDirectory).load(from: missingURL)) { error in
            guard case ProjectModelError.unreadable = error else {
                return XCTFail("文件不存在应抛 unreadable，实际是 \(error)")
            }
        }
    }

    func testInvalidLayerAndBluRayGeometryAreRejected() throws {
        var badLayer = makeCDProjectObject()
        badLayer["layers"] = [[
            "id": "layer-1",
            "name": "Layer",
            "imagePath": "",
            "visible": true,
            "translateXMm": 0,
            "translateYMm": 0,
            "rotationDeg": 0,
            "scale": 1,
        ]]
        XCTAssertThrowsError(try loadProject(badLayer)) { error in
            guard case ProjectModelError.invalidLayer = error else {
                return XCTFail("空 imagePath 应抛 invalidLayer，实际是 \(error)")
            }
        }

        var badBD = makeBluRayProjectObject()
        badBD["geometry"] = [
            "innerRadiusMm": 24.0,
            "outerRadiusMm": 58.0,
            "sectors": 12_219_390, // 不是 32 的倍数
            "pitchLinear": 0,
            "pitchQuadratic": 0,
            "pitchCubic": 0,
        ]
        XCTAssertThrowsError(try loadProject(badBD)) { error in
            guard case ProjectModelError.invalidGeometry = error else {
                return XCTFail("BD 扇区未按 32 对齐应抛 invalidGeometry，实际是 \(error)")
            }
        }
    }

    // MARK: - 保存

    func testSaveWritesNewFileAtomicallyAndReloads() throws {
        let source = try writeProject(makeCDProjectObject())
        let store = ProjectStore(projectsDirectory: temporaryDirectory)
        let document = try store.load(from: source)

        let destination = temporaryDirectory
            .appendingPathComponent("saved", isDirectory: true)
            .appendingPathComponent("project.discproj", isDirectory: false)
        XCTAssertFalse(FileManager.default.fileExists(atPath: destination.path))

        try store.save(document, to: destination)

        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.path))
        let directoryContents = try FileManager.default.contentsOfDirectory(
            atPath: destination.deletingLastPathComponent().path
        )
        XCTAssertEqual(directoryContents, ["project.discproj"], "原子写不应留下 .tmp 文件")

        let reloaded = try store.load(from: destination)
        XCTAssertEqual(reloaded.name, document.name)
        XCTAssertEqual(reloaded.discFamily, document.discFamily)
        XCTAssertEqual(reloaded.geometry, document.geometry)
        XCTAssertEqual(reloaded.layers, document.layers)
        XCTAssertEqual(reloaded.outputs, document.outputs)
        XCTAssertEqual(reloaded.tone, document.tone)
        XCTAssertEqual(reloaded.schemaVersion, 2)
    }

    func testSaveOverwritesExistingProjectFile() throws {
        let source = try writeProject(makeCDProjectObject())
        let store = ProjectStore(projectsDirectory: temporaryDirectory)
        var document = try store.load(from: source)
        let destination = temporaryDirectory.appendingPathComponent("overwrite.discproj")

        try store.save(document, to: destination)
        document.name = "覆盖后的工程"
        try store.save(document, to: destination)

        let reloaded = try store.load(from: destination)
        XCTAssertEqual(reloaded.name, "覆盖后的工程")
        let contents = try FileManager.default.contentsOfDirectory(
            atPath: temporaryDirectory.path
        )
        XCTAssertEqual(contents.filter { $0.hasPrefix(".overwrite.discproj") }, [], "原子写不应留下临时文件")
    }

    func testSaveRewritesModifiedUtcAndKeepsCreatedUtc() throws {
        let source = try writeProject(makeCDProjectObject())
        let store = ProjectStore(projectsDirectory: temporaryDirectory)
        var document = try store.load(from: source)
        let originalModified = document.modifiedUtc
        let originalCreated = document.createdUtc

        document.name = "改名后的工程"
        let destination = temporaryDirectory.appendingPathComponent("renamed.discproj")
        try store.save(document, to: destination)

        let reloaded = try store.load(from: destination)
        XCTAssertEqual(reloaded.name, "改名后的工程")
        XCTAssertEqual(reloaded.createdUtc, originalCreated)
        XCTAssertNotEqual(reloaded.modifiedUtc, originalModified, "save 应刷新 modifiedUtc")
        XCTAssertFalse(reloaded.modifiedUtc.isEmpty)
    }

    func testSaveRejectsInvalidDocumentWithoutWriting() throws {
        var document = try ProjectStore(projectsDirectory: temporaryDirectory)
            .load(from: try writeProject(makeCDProjectObject()))
        document.geometry.innerRadiusMm = -5
        let destination = temporaryDirectory.appendingPathComponent("invalid.discproj")

        XCTAssertThrowsError(try ProjectStore(projectsDirectory: temporaryDirectory).save(document, to: destination)) { error in
            guard case ProjectModelError.invalidGeometry = error else {
                return XCTFail("校验失败应抛 ProjectModelError，实际是 \(error)")
            }
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: destination.path))
    }

    // MARK: - 新建工程

    func testMakeFromPresetUsesGeometryAndFamilyDefaults() throws {
        let presets = try PresetCatalog.decode(try TestSupport.fixtureData(named: "built-in-disc-presets"))
        let cdPreset = try XCTUnwrap(presets.presets(for: .cd).first)
        let dvdPreset = try XCTUnwrap(presets.presets(for: .dvd).first)
        let bdPreset = try XCTUnwrap(presets.presets(for: .bd).first)

        let cd = ProjectDocument.make(name: "CD", family: .cd, preset: cdPreset, outputPath: "/tmp/cd.wav")
        XCTAssertEqual(cd.discFamily, .cd)
        XCTAssertEqual(cd.geometry, cdPreset.geometry)
        XCTAssertEqual(cd.outputs.outputPath, "/tmp/cd.wav")
        XCTAssertEqual(cd.outputs.volumeLabel, "CD_IMAGE")
        XCTAssertEqual(cd.tone, ProjectTone())
        XCTAssertNil(cd.bluRay)
        XCTAssertEqual(cd.schemaVersion, 2)
        XCTAssertEqual(cd.canvasOuterRadiusMm, 57.5)

        let dvd = ProjectDocument.make(name: "DVD", family: .dvd, preset: dvdPreset, outputPath: "/tmp/dvd.iso")
        XCTAssertEqual(dvd.outputs.volumeLabel, "DISC_IMAGE")
        XCTAssertEqual(dvd.canvasOuterRadiusMm, dvdPreset.geometry.outerRadiusMm)
        XCTAssertNil(dvd.bluRay)

        let bd = ProjectDocument.make(name: "BD", family: .bd, preset: bdPreset, outputPath: "/tmp/bd.iso")
        XCTAssertEqual(bd.discFamily, .bd)
        XCTAssertEqual(bd.outputs.volumeLabel, "BD_IMAGE")
        XCTAssertEqual(bd.bluRay, ProjectBluRaySettings(invertMapping: bdPreset.invertMapping))
        XCTAssertEqual(
            bd.geometry.sectors % ProjectDocument.bluRaySectorsPerCluster,
            0,
            "BD 扇区必须按 32 对齐"
        )
        XCTAssertNoThrow(try bd.validate())
    }

    // MARK: - 最近工程

    func testRecentProjectsSortsByModifiedUtcAndDeduplicates() throws {
        let fileURL = temporaryDirectory.appendingPathComponent("recent.json")
        let store = RecentProjectsStore(fileURL: fileURL)

        let documents: [(ProjectDocument, URL)] = [
            (makeRecentProject(name: "旧工程", path: "/tmp/old.discproj", modified: "2026-01-01T00:00:00.000Z"), URL(fileURLWithPath: "/tmp/old.discproj")),
            (makeRecentProject(name: "新工程", path: "/tmp/new.discproj", modified: "2026-03-01T00:00:00.000Z"), URL(fileURLWithPath: "/tmp/new.discproj")),
            (makeRecentProject(name: "中工程", path: "/tmp/mid.discproj", modified: "2026-02-01T00:00:00.000Z"), URL(fileURLWithPath: "/tmp/mid.discproj")),
        ]
        for (document, url) in documents {
            _ = try store.record(document, at: url)
        }

        let listed = store.load()
        XCTAssertEqual(listed.map(\.name), ["新工程", "中工程", "旧工程"], "应按 modifiedUtc 倒序")
        XCTAssertEqual(listed.map(\.path), ["/tmp/new.discproj", "/tmp/mid.discproj", "/tmp/old.discproj"])

        // 重新记录同路径会更新而不是重复。
        let updated = makeRecentProject(name: "新工程改名", path: "/tmp/new.discproj", modified: "2026-04-01T00:00:00.000Z")
        _ = try store.record(updated, at: URL(fileURLWithPath: "/tmp/new.discproj"))
        let afterUpdate = store.load()
        XCTAssertEqual(afterUpdate.count, 3)
        XCTAssertEqual(afterUpdate.first?.name, "新工程改名")
        XCTAssertEqual(afterUpdate.filter { $0.path == "/tmp/new.discproj" }.count, 1)
    }

    func testRecentProjectsCapsAtMaximumAndCanClear() throws {
        let store = RecentProjectsStore(fileURL: temporaryDirectory.appendingPathComponent("recent.json"))
        for index in 0..<(RecentProjectsStore.maximumCount + 3) {
            let document = makeRecentProject(
                name: "工程 \(index)",
                path: "/tmp/project-\(index).discproj",
                modified: String(format: "2026-01-%02dT00:00:00.000Z", index + 1)
            )
            _ = try store.record(document, at: URL(fileURLWithPath: "/tmp/project-\(index).discproj"))
        }

        XCTAssertEqual(store.load().count, RecentProjectsStore.maximumCount)
        XCTAssertEqual(store.load().first?.name, "工程 14", "上限裁剪应保留最新工程")
        XCTAssertFalse(store.load().contains { $0.name == "工程 0" })

        try store.removeAll()
        XCTAssertEqual(store.load(), [])
    }

    func testCorruptRecentProjectsFileFallsBackToEmpty() throws {
        let fileURL = temporaryDirectory.appendingPathComponent("recent.json")
        try Data("{ not valid json".utf8).write(to: fileURL)
        XCTAssertEqual(RecentProjectsStore(fileURL: fileURL).load(), [])

        let missing = RecentProjectsStore(fileURL: temporaryDirectory.appendingPathComponent("missing.json"))
        XCTAssertEqual(missing.load(), [])
    }

    func testRecentProjectsRecordsThumbnailAndFamily() throws {
        let store = RecentProjectsStore(fileURL: temporaryDirectory.appendingPathComponent("recent.json"))
        var document = makeRecentProject(
            name: "带缩略图",
            path: "/tmp/thumb.discproj",
            modified: "2026-05-01T00:00:00.000Z"
        )
        document.thumbnailPngBase64 = "aGVsbG8="
        let url = URL(fileURLWithPath: "/tmp/thumb.discproj")
        let listed = try store.record(document, at: url)

        XCTAssertEqual(listed.first?.thumbnailPngBase64, "aGVsbG8=")
        XCTAssertEqual(listed.first?.family, document.discFamily)
        XCTAssertEqual(listed.first?.fileURL.path, "/tmp/thumb.discproj")
    }

    // MARK: - Helpers

    private func loadProject(_ object: [String: Any]) throws -> ProjectDocument {
        let url = try writeProject(object)
        return try ProjectStore(projectsDirectory: temporaryDirectory).load(from: url)
    }

    private func writeProject(_ object: [String: Any]) throws -> URL {
        let url = temporaryDirectory.appendingPathComponent("\(UUID().uuidString).discproj")
        let data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
        try data.write(to: url)
        return url
    }

    private func makeCDProjectObject(
        schemaVersion: Int = 2,
        includeTone: Bool = true
    ) -> [String: Any] {
        var object: [String: Any] = [
            "schemaVersion": schemaVersion,
            "name": "测试工程",
            "discFamily": "cd",
            "geometry": [
                "innerRadiusMm": 24.5,
                "outerRadiusMm": 56.8,
                "sectors": 359_849,
                "pitchLinear": 0,
                "pitchQuadratic": 0,
                "pitchCubic": 0,
            ],
            "layers": [
                [
                    "id": "layer-top",
                    "name": "Top",
                    "imagePath": "/tmp/top.png",
                    "visible": true,
                    "translateXMm": 1.25,
                    "translateYMm": -2.5,
                    "rotationDeg": 30,
                    "scale": 1.5,
                ],
                [
                    "id": "layer-bottom",
                    "name": "Bottom",
                    "imagePath": "/tmp/bottom.png",
                    "visible": false,
                    "translateXMm": 0,
                    "translateYMm": 0,
                    "rotationDeg": 0,
                    "scale": 1,
                ],
            ],
            "outputs": [
                "outputPath": "/tmp/out.wav",
                "writeCue": false,
                "dataDirectory": "/tmp/data",
                "volumeLabel": "MY_CD",
                "interleave": false,
                "filesAtOuterRing": true,
            ],
            "calibrationSessionPath": "/tmp/calibration.json",
            "createdUtc": "2026-01-02T03:04:05.678+08:00",
            "modifiedUtc": "2026-01-02T04:05:06.789+08:00",
        ]
        if includeTone {
            object["tone"] = [
                "ditherStrength": 0.5,
                "levels": 4,
                "brightness": -10,
                "srgbLinearization": true,
            ]
        }
        return object
    }

    private func makeBluRayProjectObject(schemaVersion: Int = 2) -> [String: Any] {
        [
            "schemaVersion": schemaVersion,
            "name": "蓝光工程",
            "discFamily": "bd",
            "geometry": [
                "innerRadiusMm": 24.0,
                "outerRadiusMm": 58.0,
                "sectors": 12_219_392,
                "pitchLinear": 0,
                "pitchQuadratic": 0,
                "pitchCubic": 0,
            ],
            "layers": [] as [Any],
            "outputs": [
                "outputPath": "/tmp/bd.iso",
                "writeCue": true,
                "dataDirectory": "",
                "volumeLabel": "BD_IMAGE",
                "interleave": true,
                "filesAtOuterRing": false,
            ],
            "createdUtc": "2026-01-02T03:04:05.678+08:00",
            "modifiedUtc": "2026-01-02T04:05:06.789+08:00",
            "tone": [
                "ditherStrength": 0.75,
                "levels": 2,
                "brightness": 0,
                "srgbLinearization": false,
            ],
            "bluRay": ["invertMapping": true],
        ]
    }

    private func makeRecentProject(name: String, path: String, modified: String) -> ProjectDocument {
        ProjectDocument(
            name: name,
            discFamily: .cd,
            geometry: ProjectGeometry(
                innerRadiusMm: 24.5,
                outerRadiusMm: 56.8,
                sectors: 359_849
            ),
            outputs: ProjectOutputs(outputPath: path),
            createdUtc: modified,
            modifiedUtc: modified
        )
    }
}
