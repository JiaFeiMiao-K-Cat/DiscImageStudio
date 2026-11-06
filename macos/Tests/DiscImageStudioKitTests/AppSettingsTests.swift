import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住设置持久化与容错契约（DESIGN.md §2.5）。
final class AppSettingsTests: XCTestCase {

    private var temporaryDirectory: URL!

    override func setUpWithError() throws {
        temporaryDirectory = try TestSupport.makeTemporaryDirectory("AppSettingsTests")
    }

    override func tearDownWithError() throws {
        if let temporaryDirectory {
            try? FileManager.default.removeItem(at: temporaryDirectory)
        }
        temporaryDirectory = nil
    }

    // MARK: - 默认值与 Codable

    func testDefaultSettingsMatchDocumentedValues() {
        let settings = AppSettings()

        XCTAssertEqual(settings.schemaVersion, 1)
        XCTAssertEqual(AppSettings.currentSchemaVersion, 1)
        XCTAssertNil(settings.customCliPath)
        XCTAssertNil(settings.devProjectPath)
        XCTAssertNil(settings.defaultOutputDirectory)
        XCTAssertEqual(settings.appearance, .system)
        XCTAssertNil(settings.lastCommandName)
        XCTAssertTrue(settings.showsAdvancedOptions)
        XCTAssertEqual(AppearanceMode.allCases.map(\.displayName), ["跟随系统", "浅色", "深色"])
    }

    func testSettingsRoundTripThroughCodable() throws {
        let settings = AppSettings(
            customCliPath: "/usr/local/bin/disc-image-studio",
            devProjectPath: "/Users/example/DiscImageStudio",
            defaultOutputDirectory: "/Users/example/Output",
            appearance: .dark,
            lastCommandName: "cd-generate",
            showsAdvancedOptions: false
        )

        let data = try JSONEncoder().encode(settings)
        let json = try XCTUnwrap(String(data: data, encoding: .utf8))
        for key in [
            "schemaVersion", "customCliPath", "devProjectPath", "defaultOutputDirectory",
            "appearance", "lastCommandName", "showsAdvancedOptions",
        ] {
            XCTAssertTrue(json.contains("\"\(key)\""), "编码后的 JSON 应包含 camelCase 键 \(key)：\(json)")
        }

        let decoded = try JSONDecoder().decode(AppSettings.self, from: data)
        XCTAssertEqual(decoded, settings)
    }

    func testDecodingUnknownAppearanceFallsBackToSystem() throws {
        let data = Data(#"""
        {
          "schemaVersion": 1,
          "appearance": "sepia",
          "showsAdvancedOptions": false,
          "lastCommandName": "solve"
        }
        """#.utf8)

        let decoded = try JSONDecoder().decode(AppSettings.self, from: data)
        XCTAssertEqual(decoded.appearance, .system)
        XCTAssertFalse(decoded.showsAdvancedOptions)
        XCTAssertEqual(decoded.lastCommandName, "solve")
    }

    func testDecodingMissingFieldsUsesDefaults() throws {
        let decoded = try JSONDecoder().decode(AppSettings.self, from: Data(#"{"schemaVersion": 1}"#.utf8))
        XCTAssertEqual(decoded, AppSettings())
    }

    // MARK: - load() 容错

    func testLoadReturnsDefaultsWhenFileIsMissing() {
        let store = AppSettingsStore(directory: temporaryDirectory)
        XCTAssertEqual(store.load(), AppSettings())
    }

    func testLoadReturnsDefaultsWhenJSONIsCorrupt() throws {
        let store = AppSettingsStore(directory: temporaryDirectory)
        try FileManager.default.createDirectory(
            at: temporaryDirectory,
            withIntermediateDirectories: true,
            attributes: nil
        )
        try Data("{ this is not json".utf8).write(to: store.fileURL)

        XCTAssertEqual(store.load(), AppSettings())
    }

    func testLoadReturnsDefaultsWhenSchemaIsNewer() throws {
        let store = AppSettingsStore(directory: temporaryDirectory)
        try FileManager.default.createDirectory(
            at: temporaryDirectory,
            withIntermediateDirectories: true,
            attributes: nil
        )
        let payload = Data(#"""
        {
          "schemaVersion": 2,
          "appearance": "dark",
          "lastCommandName": "solve",
          "showsAdvancedOptions": false
        }
        """#.utf8)
        try payload.write(to: store.fileURL)

        XCTAssertEqual(store.load(), AppSettings())
    }

    func testLoadNormalizesExistingPathsAndTrimsWhitespace() throws {
        let existingFile = temporaryDirectory.appendingPathComponent("cli", isDirectory: false)
        try Data("#!/bin/sh\n".utf8).write(to: existingFile)
        let existingDirectory = temporaryDirectory.appendingPathComponent("output", isDirectory: true)
        try FileManager.default.createDirectory(at: existingDirectory, withIntermediateDirectories: true)
        let missing = temporaryDirectory.appendingPathComponent("missing", isDirectory: true).path

        let store = AppSettingsStore(directory: temporaryDirectory)
        let settings = AppSettings(
            customCliPath: "  \(existingFile.path)\n",
            devProjectPath: missing,
            defaultOutputDirectory: " \(existingDirectory.path) ",
            lastCommandName: "  cd-generate ",
            showsAdvancedOptions: false
        )
        try store.save(settings)

        let loaded = store.load()
        XCTAssertEqual(loaded.customCliPath, existingFile.path)
        XCTAssertNil(loaded.devProjectPath, "不存在的开发工程路径应被丢弃")
        XCTAssertEqual(loaded.defaultOutputDirectory, existingDirectory.path)
        XCTAssertEqual(loaded.lastCommandName, "cd-generate")
        XCTAssertFalse(loaded.showsAdvancedOptions)
        XCTAssertEqual(loaded.schemaVersion, 1)
    }

    // MARK: - normalized()

    func testNormalizedTrimsAndDropsNonexistentPaths() throws {
        let existingFile = temporaryDirectory.appendingPathComponent("disc-image-studio", isDirectory: false)
        try Data().write(to: existingFile)
        let existingDirectory = temporaryDirectory.appendingPathComponent("Out Put", isDirectory: true)
        try FileManager.default.createDirectory(at: existingDirectory, withIntermediateDirectories: true)
        let missingFile = temporaryDirectory.appendingPathComponent("nope", isDirectory: false).path

        var settings = AppSettings(
            customCliPath: "  \(existingFile.path)  ",
            devProjectPath: "  \(existingFile.path)  ",
            defaultOutputDirectory: " \(existingDirectory.path) ",
            lastCommandName: "   ",
            showsAdvancedOptions: false
        )
        settings.schemaVersion = 0

        let normalized = settings.normalized()
        XCTAssertEqual(normalized.schemaVersion, 1)
        XCTAssertEqual(normalized.customCliPath, existingFile.path)
        XCTAssertNil(normalized.devProjectPath, "devProjectPath 只在目录存在时保留")
        XCTAssertEqual(normalized.defaultOutputDirectory, existingDirectory.path)
        XCTAssertNil(normalized.lastCommandName)

        let withMissing = AppSettings(customCliPath: missingFile).normalized()
        XCTAssertNil(withMissing.customCliPath)
    }

    func testNormalizedKeepsExistingDevelopmentDirectory() throws {
        let projectDirectory = temporaryDirectory.appendingPathComponent("DiscImageStudio", isDirectory: true)
        try FileManager.default.createDirectory(at: projectDirectory, withIntermediateDirectories: true)

        let normalized = AppSettings(devProjectPath: " \(projectDirectory.path) ").normalized()
        XCTAssertEqual(normalized.devProjectPath, projectDirectory.path)
    }

    // MARK: - save()

    func testSaveCreatesFileAtomicallyAndWritesNormalizedSettings() throws {
        let nested = temporaryDirectory
            .appendingPathComponent("nested", isDirectory: true)
            .appendingPathComponent("DiscImageStudioMac", isDirectory: true)
        let store = AppSettingsStore(directory: nested)
        XCTAssertEqual(store.fileURL, nested.appendingPathComponent("settings.json", isDirectory: false))

        let existingDirectory = temporaryDirectory.appendingPathComponent("images", isDirectory: true)
        try FileManager.default.createDirectory(at: existingDirectory, withIntermediateDirectories: true)
        let missing = temporaryDirectory.appendingPathComponent("missing", isDirectory: false).path

        let settings = AppSettings(
            customCliPath: missing,
            defaultOutputDirectory: " \(existingDirectory.path) ",
            appearance: .light,
            showsAdvancedOptions: false
        )
        try store.save(settings)

        XCTAssertTrue(FileManager.default.fileExists(atPath: store.fileURL.path))
        let directoryContents = try FileManager.default.contentsOfDirectory(atPath: nested.path)
        XCTAssertEqual(directoryContents.sorted(), ["settings.json"], "原子写不应留下临时文件")

        let loaded = store.load()
        XCTAssertNil(loaded.customCliPath, "save 应先 normalized()，不存在的路径不会写盘")
        XCTAssertEqual(loaded.defaultOutputDirectory, existingDirectory.path)
        XCTAssertEqual(loaded.appearance, .light)
        XCTAssertFalse(loaded.showsAdvancedOptions)
        XCTAssertEqual(loaded, settings.normalized())
    }

    func testSaveOverwritesPreviousFile() throws {
        let store = AppSettingsStore(directory: temporaryDirectory)
        try store.save(AppSettings(lastCommandName: "cd-generate"))
        try store.save(AppSettings(appearance: .dark, lastCommandName: "solve"))

        let loaded = store.load()
        XCTAssertEqual(loaded.appearance, .dark)
        XCTAssertEqual(loaded.lastCommandName, "solve")
        XCTAssertTrue(loaded.showsAdvancedOptions)
    }

    func testCustomDirectoryIsUsedInsteadOfApplicationSupport() {
        let store = AppSettingsStore(directory: temporaryDirectory)
        XCTAssertEqual(
            store.fileURL,
            temporaryDirectory.appendingPathComponent("settings.json", isDirectory: false)
        )

        // 默认路径只做只读断言，避免测试触碰真实的 ~/Library/Application Support。
        let defaultStore = AppSettingsStore()
        XCTAssertTrue(
            defaultStore.fileURL.path.hasSuffix(
                "/Library/Application Support/DiscImageStudioMac/settings.json"
            )
        )
    }
}
