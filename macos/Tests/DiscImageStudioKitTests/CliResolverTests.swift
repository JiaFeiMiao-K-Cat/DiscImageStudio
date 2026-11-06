import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住 CLI 发现优先级与错误映射契约（DESIGN.md §2.4）。
final class CliResolverTests: XCTestCase {

    // MARK: - 候选顺序

    func testEnvironmentCandidateIsFirstAndResolves() throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let script = try makeCatalogScript(in: directory)
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)

        let resolver = makeResolver(environment: [
            "DIS_IMAGE_STUDIO_CLI": script.path,
            "PATH": emptyPath.path,
        ])

        let first = try XCTUnwrap(resolver.candidates.first)
        XCTAssertEqual(first.source, .environment)
        XCTAssertEqual(first.detail, script.path)
        XCTAssertEqual(first.invocation.executableURL.standardizedFileURL, script.standardizedFileURL)
        XCTAssertEqual(first.invocation.label, "环境变量 DIS_IMAGE_STUDIO_CLI")
        XCTAssertEqual(first.label, "环境变量")
        XCTAssertEqual(resolver.resolve(), first)
    }

    func testEnvironmentCandidatePrecedesCustomPathCandidate() throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let environmentScript = try makeCatalogScript(
            in: try makeEmptyDirectory(named: "env", in: directory)
        )
        let customScript = try makeCatalogScript(
            in: try makeEmptyDirectory(named: "custom", in: directory)
        )
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)

        var settings = AppSettings()
        settings.customCliPath = customScript.path
        let resolver = makeResolver(
            settings: settings,
            environment: [
                "DIS_IMAGE_STUDIO_CLI": environmentScript.path,
                "PATH": emptyPath.path,
            ]
        )

        let candidates = resolver.candidates
        XCTAssertGreaterThanOrEqual(candidates.count, 2)
        XCTAssertEqual(candidates[0].source, .environment)
        XCTAssertEqual(candidates[0].detail, environmentScript.path)
        XCTAssertEqual(candidates[1].source, .custom)
        XCTAssertEqual(candidates[1].detail, customScript.path)
        XCTAssertEqual(candidates[1].invocation.label, "设置中的 CLI")
    }

    func testCustomPathCandidateWorksWithoutEnvironmentVariable() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let script = try makeCatalogScript(in: directory)
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)

        var settings = AppSettings()
        settings.customCliPath = script.path
        let resolver = makeResolver(
            settings: settings,
            environment: ["PATH": emptyPath.path]
        )

        let first = try XCTUnwrap(resolver.candidates.first)
        XCTAssertEqual(first.source, .custom)
        let catalog = try await resolver.loadCatalog(from: first)
        XCTAssertEqual(catalog.schemaVersion, 1)
    }

    func testCandidatePathMayContainSpaces() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let spacedDirectory = directory.appendingPathComponent("cli dir", isDirectory: true)
        try FileManager.default.createDirectory(at: spacedDirectory, withIntermediateDirectories: true)
        let script = try makeCatalogScript(in: spacedDirectory, name: "disc image studio")
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)

        let resolver = makeResolver(environment: [
            "DIS_IMAGE_STUDIO_CLI": script.path,
            "PATH": emptyPath.path,
        ])

        let first = try XCTUnwrap(resolver.resolve())
        XCTAssertEqual(first.source, .environment)
        XCTAssertTrue(first.detail.contains("cli dir"))
        let catalog = try await resolver.loadCatalog(from: first)
        XCTAssertEqual(catalog.toolVersion, "9.9.9")
        XCTAssertEqual(catalog.entry(named: "cd-generate")?.module.id, "cd-da")
    }

    func testInvalidCustomPathsProduceNoEnvironmentOrCustomCandidates() throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)
        let nonExecutable = directory.appendingPathComponent("not-executable", isDirectory: false)
        try Data("#!/bin/sh\n".utf8).write(to: nonExecutable)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o644)],
            ofItemAtPath: nonExecutable.path
        )
        let directoryPath = try makeEmptyDirectory(named: "a-directory", in: directory)

        var settings = AppSettings()
        settings.customCliPath = directoryPath.path
        let resolver = makeResolver(
            settings: settings,
            environment: [
                "DIS_IMAGE_STUDIO_CLI": directory.appendingPathComponent("missing").path,
                "PATH": emptyPath.path,
            ]
        )

        let candidates = resolver.candidates
        XCTAssertFalse(candidates.contains { $0.source == .environment })
        XCTAssertFalse(candidates.contains { $0.source == .custom })
        XCTAssertFalse(candidates.contains { $0.invocation.executableURL.path == nonExecutable.path })
    }

    // MARK: - loadCatalog

    func testLoadCatalogDecodesEnvironmentCliOutput() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let script = try makeCatalogScript(in: directory)
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)

        let resolver = makeResolver(environment: [
            "DIS_IMAGE_STUDIO_CLI": script.path,
            "PATH": emptyPath.path,
        ])
        let cli = try XCTUnwrap(resolver.resolve())
        let catalog = try await resolver.loadCatalog(from: cli)

        XCTAssertEqual(catalog.schemaVersion, 1)
        XCTAssertEqual(catalog.toolVersion, "9.9.9")
        XCTAssertEqual(catalog.modules.map(\.id), ["cd-da"])
        let entry = try XCTUnwrap(catalog.entry(named: "cd-generate"))
        XCTAssertEqual(entry.module.family, .compactDisc)
        XCTAssertEqual(entry.command.options.first?.name, "input")
        XCTAssertTrue(entry.command.options.first?.required == true)
    }

    func testResolveAndLoadCatalogUsesFirstWorkingCandidate() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let script = try makeCatalogScript(in: directory)
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)

        let resolver = makeResolver(environment: [
            "DIS_IMAGE_STUDIO_CLI": script.path,
            "PATH": emptyPath.path,
        ])
        let (cli, catalog) = try await resolver.resolveAndLoadCatalog()

        XCTAssertEqual(cli.source, .environment)
        XCTAssertEqual(catalog.entry(named: "cd-generate")?.command.displayName, "Generate CD")
    }

    func testResolveAndLoadCatalogFallsThroughFailingCandidate() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let failingScript = try makeFailingScript(
            in: try makeEmptyDirectory(named: "failing", in: directory)
        )
        let workingScript = try makeCatalogScript(
            in: try makeEmptyDirectory(named: "working", in: directory)
        )
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)

        var settings = AppSettings()
        settings.customCliPath = workingScript.path
        let resolver = makeResolver(
            settings: settings,
            environment: [
                "DIS_IMAGE_STUDIO_CLI": failingScript.path,
                "PATH": emptyPath.path,
            ]
        )
        let (cli, catalog) = try await resolver.resolveAndLoadCatalog()

        XCTAssertEqual(cli.source, .custom)
        XCTAssertEqual(catalog.schemaVersion, 1)
    }

    // MARK: - 错误映射

    func testNonZeroExitMapsToCatalogFailed() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let script = try makeFailingScript(in: directory)
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)
        let resolver = makeResolver(environment: [
            "DIS_IMAGE_STUDIO_CLI": script.path,
            "PATH": emptyPath.path,
        ])
        let cli = try XCTUnwrap(resolver.resolve())

        do {
            _ = try await resolver.loadCatalog(from: cli)
            XCTFail("非 0 退出码应抛 CliResolverError")
        } catch let error as CliResolverError {
            guard case .catalogFailed(let reason) = error else {
                return XCTFail("应映射为 catalogFailed，实际是 \(error)")
            }
            XCTAssertEqual(reason, "boom")
            XCTAssertEqual(error.errorDescription, "CLI 未返回可用的命令目录：boom")
        } catch {
            XCTFail("应抛 CliResolverError，实际是 \(error)")
        }
    }

    func testMalformedCatalogJSONMapsToCatalogFailed() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let script = try makePayloadScript(
            in: directory,
            name: "bad-json",
            payload: "definitely not json"
        )
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)
        let resolver = makeResolver(environment: [
            "DIS_IMAGE_STUDIO_CLI": script.path,
            "PATH": emptyPath.path,
        ])
        let cli = try XCTUnwrap(resolver.resolve())

        do {
            _ = try await resolver.loadCatalog(from: cli)
            XCTFail("坏 JSON 应抛 CliResolverError.catalogFailed")
        } catch let error as CliResolverError {
            guard case .catalogFailed(let reason) = error else {
                return XCTFail("应映射为 catalogFailed，实际是 \(error)")
            }
            XCTAssertTrue(reason.contains("无法解析命令目录 JSON"), "错误原因应说明 JSON 解析失败：\(reason)")
        } catch {
            XCTFail("应抛 CliResolverError，实际是 \(error)")
        }
    }

    func testUnsupportedSchemaMapsToUnsupportedSchema() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let script = try makePayloadScript(
            in: directory,
            name: "newer-schema",
            payload: #"{"schemaVersion": 2, "toolVersion": null, "modules": []}"#
        )
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)
        let resolver = makeResolver(environment: [
            "DIS_IMAGE_STUDIO_CLI": script.path,
            "PATH": emptyPath.path,
        ])
        let cli = try XCTUnwrap(resolver.resolve())

        do {
            _ = try await resolver.loadCatalog(from: cli)
            XCTFail("schemaVersion != 1 应抛 CliResolverError.unsupportedSchema")
        } catch let error as CliResolverError {
            guard case .unsupportedSchema(let found, let supported) = error else {
                return XCTFail("应映射为 unsupportedSchema，实际是 \(error)")
            }
            XCTAssertEqual(found, 2)
            XCTAssertEqual(supported, 1)
        } catch {
            XCTFail("应抛 CliResolverError，实际是 \(error)")
        }
    }

    func testResolveAndLoadCatalogThrowsWhenEveryCandidateFails() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let script = try makeFailingScript(in: directory)
        let emptyPath = try makeEmptyDirectory(named: "path", in: directory)
        let resolver = makeResolver(environment: [
            "DIS_IMAGE_STUDIO_CLI": script.path,
            "PATH": emptyPath.path,
        ])

        do {
            _ = try await resolver.resolveAndLoadCatalog()
            XCTFail("所有候选都失败时应抛 CliResolverError")
        } catch is CliResolverError {
            // 预期：错误类型是 CliResolverError（具体映射由上面的单测覆盖）。
        } catch {
            XCTFail("应抛 CliResolverError，实际是 \(error)")
        }
    }

    // MARK: - Helpers

    private func makeResolver(
        settings: AppSettings = AppSettings(),
        environment: [String: String]
    ) -> CliResolver {
        CliResolver(
            settings: settings,
            bundle: Bundle(for: CliResolverTests.self),
            environment: environment,
            fileManager: .default
        )
    }

    private func makeEmptyDirectory(named name: String, in parent: URL) throws -> URL {
        let url = parent.appendingPathComponent(name, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func makeCatalogScript(
        in directory: URL,
        name: String = "disc-image-studio"
    ) throws -> URL {
        try makePayloadScript(in: directory, name: name, payload: FakeCatalogPayload.json)
    }

    private func makePayloadScript(in directory: URL, name: String, payload: String) throws -> URL {
        let payloadURL = directory.appendingPathComponent("\(name)-payload.json", isDirectory: false)
        try Data(payload.utf8).write(to: payloadURL)
        let contents = """
        #!/bin/sh
        cat "$(dirname "$0")/\(name)-payload.json"
        """
        return try TestSupport.writeExecutableScript(in: directory, name: name, contents: contents)
    }

    private func makeFailingScript(in directory: URL) throws -> URL {
        try TestSupport.writeExecutableScript(
            in: directory,
            name: "failing-cli",
            contents: """
            #!/bin/sh
            echo "boom" >&2
            exit 3
            """
        )
    }
}
