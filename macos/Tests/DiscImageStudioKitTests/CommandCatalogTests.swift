import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住 `catalog --json` 快照解码契约（DESIGN.md §1、§2.1）。
final class CommandCatalogTests: XCTestCase {

    // MARK: - 快照解码

    func testFixtureSnapshotDecodesWithExpectedSchemaAndModules() throws {
        let catalog = try loadFixtureCatalog()

        XCTAssertEqual(catalog.schemaVersion, CommandCatalog.supportedSchemaVersion)
        XCTAssertEqual(catalog.schemaVersion, 1)

        XCTAssertEqual(
            catalog.modules.count,
            3,
            "Fixtures/catalog.json 应是真实 CLI 的 `catalog --json` 输出（含三个模块），而不是占位空目录。"
        )
        XCTAssertEqual(catalog.modules.map(\.id), ["cd-da", "dvd-data", "bluray-data"])
        XCTAssertEqual(catalog.modules.map(\.family), [.compactDisc, .dvd, .bluRay])
        XCTAssertEqual(catalog.modules.map(\.supportsCancellation), [true, false, true])
        XCTAssertEqual(catalog.modules.map(\.displayName), ["CD", "DVD data disc", "Blu-ray BD-R"])
        for module in catalog.modules {
            XCTAssertFalse(module.description.isEmpty, "模块 \(module.id) 的 description 不应为空")
            XCTAssertFalse(module.commands.isEmpty, "模块 \(module.id) 应至少有一个命令")
        }

        // toolVersion 允许为 null（DESIGN §1），但真实构建应能取到程序集版本，且不含提交哈希。
        if let toolVersion = catalog.toolVersion {
            XCTAssertFalse(toolVersion.isEmpty)
            XCTAssertFalse(toolVersion.contains("+"), "toolVersion 不应包含 + 之后的提交哈希")
        }
    }

    func testFixtureSnapshotCommandSetsMatchCliContract() throws {
        let catalog = try loadFixtureCatalog()

        let commandsByModule = Dictionary(
            uniqueKeysWithValues: catalog.modules.map { module in
                (module.id, module.commands.map(\.name))
            }
        )
        XCTAssertEqual(commandsByModule["cd-da"], ["cd-generate", "cd-preview-warp", "cd-preview-track"])
        XCTAssertEqual(commandsByModule["dvd-data"], ["solve", "encode", "calibrate", "simulate", "selftest"])
        XCTAssertEqual(commandsByModule["bluray-data"], ["bd-info", "bd-probe", "bd-encode", "bd-generate"])

        // entries 按模块顺序、模块内命令顺序展开。
        XCTAssertEqual(
            catalog.entries.map(\.command.name),
            [
                "cd-generate", "cd-preview-warp", "cd-preview-track",
                "solve", "encode", "calibrate", "simulate", "selftest",
                "bd-info", "bd-probe", "bd-encode", "bd-generate",
            ]
        )
        XCTAssertEqual(catalog.entries.first?.module.id, "cd-da")
        XCTAssertEqual(catalog.entries.first?.family, .compactDisc)
        XCTAssertEqual(catalog.entries.last?.family, .bluRay)

        // 能力标志按 C# 枚举声明顺序序列化，None 时为空数组。
        let cdGenerate = try XCTUnwrap(catalog.entry(named: "cd-generate"))
        XCTAssertEqual(
            cdGenerate.command.capabilities,
            ["RawTrackGeneration", "DataImageGeneration", "HybridFilesystem"]
        )
        let selftest = try XCTUnwrap(catalog.entry(named: "selftest"))
        XCTAssertEqual(selftest.command.capabilities, [])
    }

    func testFixtureSnapshotOptionsCarryAllFields() throws {
        let catalog = try loadFixtureCatalog()
        let cdGenerate = try XCTUnwrap(catalog.entry(named: "cd-generate"))
        let options = Dictionary(
            uniqueKeysWithValues: cdGenerate.command.options.map { ($0.name, $0) }
        )

        // 必填文件参数：defaultValue/unit/choices 都是 null，但仍要保留字段（Codable 可选）。
        let input = try XCTUnwrap(options["input"])
        XCTAssertEqual(input.displayName, "Source image")
        XCTAssertEqual(input.valueType, .inputFile)
        XCTAssertTrue(input.required)
        XCTAssertNil(input.defaultValue)
        XCTAssertNil(input.unit)
        XCTAssertNil(input.choices)
        XCTAssertNil(input.unitLabel)
        XCTAssertEqual(input.id, "input")

        // Decimal + 单位 + 默认值。
        let r0 = try XCTUnwrap(options["r0"])
        XCTAssertEqual(r0.displayName, "Inner radius")
        XCTAssertEqual(r0.valueType, .decimal)
        XCTAssertFalse(r0.required)
        XCTAssertEqual(r0.defaultValue, "24.5")
        XCTAssertEqual(r0.unit, "mm")
        XCTAssertEqual(r0.unitLabel, "mm")
        XCTAssertNil(r0.choices)
        XCTAssertTrue(r0.valueType.isNumeric)
        XCTAssertFalse(r0.valueType.isFile)

        // Choice + 候选值。
        let filePosition = try XCTUnwrap(options["file-position"])
        XCTAssertEqual(filePosition.valueType, .choice)
        XCTAssertFalse(filePosition.required)
        XCTAssertEqual(filePosition.defaultValue, "inner")
        XCTAssertEqual(filePosition.choices, ["inner", "outer"])
        XCTAssertNil(filePosition.unit)

        // Boolean + 默认值。
        let interleave = try XCTUnwrap(options["interleave"])
        XCTAssertEqual(interleave.valueType, .boolean)
        XCTAssertEqual(interleave.defaultValue, "true")
        XCTAssertFalse(interleave.required)
        XCTAssertNil(interleave.choices)

        // InputFile 的 isFile / isInput 约定（UI 的「选择…」按钮依赖它）。
        XCTAssertTrue(DiscOptionValueType.inputFile.isFile)
        XCTAssertTrue(DiscOptionValueType.inputFolder.isFile)
        XCTAssertTrue(DiscOptionValueType.outputFile.isFile)
        XCTAssertTrue(DiscOptionValueType.outputFile.isOutput)
        XCTAssertTrue(DiscOptionValueType.inputFolder.isInput)
        XCTAssertFalse(DiscOptionValueType.string.isFile)
    }

    func testEntryLookupIsCaseInsensitive() throws {
        let catalog = try loadFixtureCatalog()

        let lower = try XCTUnwrap(catalog.entry(named: "cd-generate"))
        let upper = try XCTUnwrap(catalog.entry(named: "CD-GENERATE"))
        let mixed = try XCTUnwrap(catalog.entry(named: "Solve"))
        XCTAssertEqual(lower.command.name, "cd-generate")
        XCTAssertEqual(upper.command.name, lower.command.name)
        XCTAssertEqual(upper.module.id, lower.module.id)
        XCTAssertEqual(mixed.command.name, "solve")
        XCTAssertEqual(mixed.module.id, "dvd-data")
        XCTAssertNil(catalog.entry(named: "no-such-command"))
        XCTAssertNil(catalog.entry(named: ""))

        XCTAssertEqual(catalog.module(forCommandName: "BD-GENERATE")?.id, "bluray-data")
    }

    func testPrimaryCommandForEachFamily() throws {
        let catalog = try loadFixtureCatalog()

        XCTAssertEqual(catalog.primaryCommand(for: .compactDisc)?.command.name, "cd-generate")
        XCTAssertEqual(catalog.primaryCommand(for: .dvd)?.command.name, "solve")
        XCTAssertEqual(catalog.primaryCommand(for: .bluRay)?.command.name, "bd-generate")

        XCTAssertEqual(catalog.primaryCommand(for: .compactDisc)?.module.id, "cd-da")
        XCTAssertEqual(catalog.primaryCommand(for: .dvd)?.module.id, "dvd-data")
        XCTAssertEqual(catalog.primaryCommand(for: .bluRay)?.module.id, "bluray-data")
    }

    // MARK: - 错误路径

    func testMalformedJSONThrowsCommandCatalogError() {
        let payloads: [Data] = [
            Data("not json at all".utf8),
            Data(#"{"schemaVersion": 1, "modules": [}"#.utf8),
            Data(#"{"schemaVersion": "one", "modules": []}"#.utf8),
        ]

        for data in payloads {
            XCTAssertThrowsError(try CommandCatalog.decode(data)) { error in
                guard case CommandCatalogError.malformedJSON = error else {
                    return XCTFail("坏 JSON 应抛 malformedJSON，实际是 \(error)")
                }
            }
        }
    }

    func testUnknownEnumValueIsRejectedRatherThanSilentlyDropped() {
        let data = Data(#"""
        {
          "schemaVersion": 1,
          "toolVersion": null,
          "modules": [
            {
              "id": "hd-dvd",
              "displayName": "HD DVD",
              "family": "HdDvd",
              "description": "未知家族",
              "supportsCancellation": true,
              "commands": []
            }
          ]
        }
        """#.utf8)

        XCTAssertThrowsError(try CommandCatalog.decode(data)) { error in
            guard case CommandCatalogError.malformedJSON = error else {
                return XCTFail("未知 family 应解码失败，实际是 \(error)")
            }
        }
    }

    func testUnsupportedSchemaVersionThrows() throws {
        var object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: try TestSupport.catalogFixtureData()) as? [String: Any]
        )
        object["schemaVersion"] = 2
        let data = try JSONSerialization.data(withJSONObject: object)

        XCTAssertThrowsError(try CommandCatalog.decode(data)) { error in
            XCTAssertEqual(
                error as? CommandCatalogError,
                .unsupportedSchema(found: 2, supported: 1)
            )
            XCTAssertEqual(
                (error as? CommandCatalogError)?.errorDescription,
                "命令目录 schema 版本 2 不受支持（当前支持 1），请更新图形界面或 CLI。"
            )
        }
    }

    // MARK: - Helpers

    private func loadFixtureCatalog() throws -> CommandCatalog {
        try CommandCatalog.decode(try TestSupport.catalogFixtureData())
    }
}
