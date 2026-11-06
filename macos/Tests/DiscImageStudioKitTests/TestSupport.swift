import Foundation
import XCTest

import DiscImageStudioKit

/// 测试内部错误；避免依赖 XCTest 的重载，便于 async 工具函数抛出。
enum TestSupportError: Error, CustomStringConvertible {
    case timeout(Duration)
    case missingFixture
    case unexpectedEventOrder([CliEvent])

    var description: String {
        switch self {
        case .timeout(let duration):
            return "等待 CLI 事件超时（\(duration)）。"
        case .missingFixture:
            return "测试资源包里找不到 Fixtures/catalog.json。"
        case .unexpectedEventOrder(let events):
            return "CLI 事件顺序不符合 started → line* → finished：\(events)。"
        }
    }
}

enum TestSupport {
    /// 在系统临时目录下创建独立目录；调用方负责删除。
    static func makeTemporaryDirectory(_ label: String = "DiscImageStudioKitTests") throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(label)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    /// 写入一个可执行 shell 脚本（0755）。脚本路径可以包含空格。
    static func writeExecutableScript(
        in directory: URL,
        name: String = "fake-cli.sh",
        contents: String
    ) throws -> URL {
        let url = directory.appendingPathComponent(name, isDirectory: false)
        try Data(contents.utf8).write(to: url)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o755)],
            ofItemAtPath: url.path
        )
        return url
    }

    /// `Package.swift` 用 `.copy("Fixtures")`，所以资源位于 bundle 的 `Fixtures/` 子目录。
    static func fixtureData(named name: String) throws -> Data {
        guard let url = Bundle.module.url(
            forResource: name,
            withExtension: "json",
            subdirectory: "Fixtures"
        ) else {
            throw TestSupportError.missingFixture
        }
        return try Data(contentsOf: url)
    }

    static func catalogFixtureData() throws -> Data {
        try fixtureData(named: "catalog")
    }

    /// 轮询等待文件出现；用于在取消测试里确认子进程已启动，而不是依赖固定 sleep。
    static func waitForFile(at url: URL, timeout: Duration = .seconds(5)) async throws {
        let deadline = ContinuousClock.now + timeout
        while ContinuousClock.now < deadline {
            if FileManager.default.fileExists(atPath: url.path) {
                return
            }
            try await Task.sleep(for: .milliseconds(50))
        }
        throw TestSupportError.timeout(timeout)
    }

    /// 收集 `CliJob.events` 直到流结束；超时抛 `TestSupportError.timeout`。
    static func collectEvents(
        from job: CliJob,
        timeout: Duration = .seconds(10)
    ) async throws -> CollectedEvents {
        let events = try await withThrowingTaskGroup(of: [CliEvent].self) { group in
            group.addTask {
                var collected: [CliEvent] = []
                for await event in job.events {
                    collected.append(event)
                }
                return collected
            }
            group.addTask {
                try await Task.sleep(for: timeout)
                throw TestSupportError.timeout(timeout)
            }

            do {
                guard let events = try await group.next() else {
                    throw TestSupportError.timeout(timeout)
                }
                group.cancelAll()
                return events
            } catch {
                group.cancelAll()
                throw error
            }
        }
        return CollectedEvents(events: events)
    }
}

/// 对一次任务的全部事件做便捷切分。
struct CollectedEvents {
    let events: [CliEvent]

    var startedCommandLines: [String] {
        events.compactMap { (event: CliEvent) -> String? in
            guard case .started(let commandLine) = event else { return nil }
            return commandLine
        }
    }

    var lines: [LogLine] {
        events.compactMap { (event: CliEvent) -> LogLine? in
            guard case .line(let line) = event else { return nil }
            return line
        }
    }

    var stdoutLines: [String] { lines.filter { $0.stream == .stdout }.map(\.text) }
    var stderrLines: [String] { lines.filter { $0.stream == .stderr }.map(\.text) }
    var systemLines: [String] { lines.filter { $0.stream == .system }.map(\.text) }

    var finished: CliResult? {
        events.compactMap { (event: CliEvent) -> CliResult? in
            guard case .finished(let result) = event else { return nil }
            return result
        }.last
    }

    /// 校验 `started → line* → finished`，且 `started`/`finished` 各恰好一次。
    func assertWellFormed(file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(startedCommandLines.count, 1, "started 事件应恰好出现一次", file: file, line: line)
        XCTAssertEqual(
            events.filter { (event: CliEvent) -> Bool in if case .finished = event { return true } else { return false } }.count,
            1,
            "finished 事件应恰好出现一次",
            file: file,
            line: line
        )
        guard let first = events.first, let last = events.last else {
            XCTFail("事件流为空", file: file, line: line)
            return
        }
        guard case .started = first else {
            XCTFail("第一个事件必须是 .started，实际是 \(first)", file: file, line: line)
            return
        }
        guard case .finished = last else {
            XCTFail("最后一个事件必须是 .finished，实际是 \(last)", file: file, line: line)
            return
        }
    }
}

/// 最小但完整的 `catalog --json` 载荷，供 CliResolver 测试里的假 CLI 使用。
enum FakeCatalogPayload {
    static let json = """
    {
      "schemaVersion": 1,
      "toolVersion": "9.9.9",
      "modules": [
        {
          "id": "cd-da",
          "displayName": "CD",
          "family": "CompactDisc",
          "description": "Fake CD module for tests.",
          "supportsCancellation": true,
          "commands": [
            {
              "name": "cd-generate",
              "displayName": "Generate CD",
              "description": "Fake generate command.",
              "capabilities": ["RawTrackGeneration"],
              "options": [
                {
                  "name": "input",
                  "displayName": "Source image",
                  "valueType": "InputFile",
                  "required": true,
                  "defaultValue": null,
                  "unit": null,
                  "choices": null
                }
              ]
            }
          ]
        }
      ]
    }
    """

    static func write(to directory: URL) throws -> URL {
        let url = directory.appendingPathComponent("payload.json", isDirectory: false)
        try Data(json.utf8).write(to: url)
        return url
    }
}
