import Foundation
import XCTest

import DiscImageStudioKit

/// 用临时目录里的真实 shell 脚本钉住进程事件流契约（DESIGN.md §2.3）。
final class CliRunnerTests: XCTestCase {

    // MARK: - 生命周期与流

    func testLineEventsPreserveOrderAndSeparateStreams() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            echo "out-1"
            echo "err-1" >&2
            echo "out-2"
            echo "err-2" >&2
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))
        let job = runner.start(arguments: ["--demo"])

        let collected = try await TestSupport.collectEvents(from: job)
        collected.assertWellFormed()

        XCTAssertEqual(
            collected.startedCommandLines,
            [CommandArgumentBuilder.displayCommand(executable: script.path, arguments: ["--demo"])]
        )
        XCTAssertEqual(collected.stdoutLines, ["out-1", "out-2"])
        XCTAssertEqual(collected.stderrLines, ["err-1", "err-2"])
        XCTAssertEqual(collected.systemLines, [])
        XCTAssertEqual(collected.finished?.exitCode, 0)
        XCTAssertEqual(collected.finished?.succeeded, true)
        XCTAssertGreaterThanOrEqual(collected.finished?.duration ?? -1, 0)
        XCTAssertFalse(job.isRunning)
    }

    func testTrailingLinesWithoutNewlineAreFlushedOnExit() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            printf 'stdout-tail'
            printf 'stderr-tail' >&2
            exit 3
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))
        let collected = try await TestSupport.collectEvents(from: runner.start(arguments: []))

        collected.assertWellFormed()
        XCTAssertEqual(collected.stdoutLines, ["stdout-tail"])
        XCTAssertEqual(collected.stderrLines, ["stderr-tail"])
        XCTAssertEqual(collected.finished?.exitCode, 3)
        XCTAssertEqual(collected.finished?.succeeded, false)
    }

    func testWindowsLineEndingsAreStripped() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            printf 'windows\\r\\nnext\\r\\n'
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))
        let collected = try await TestSupport.collectEvents(from: runner.start(arguments: []))

        XCTAssertEqual(collected.stdoutLines, ["windows", "next"])
    }

    func testPrefixArgumentsPrecedeRunArguments() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            printf '%s\\n' "$@"
            """
        )
        let invocation = CliInvocation(
            executableURL: script,
            prefixArguments: ["run", "--"],
            label: "dotnet run"
        )
        let collected = try await TestSupport.collectEvents(
            from: CliRunner(invocation: invocation).start(arguments: ["catalog", "--json"])
        )

        XCTAssertEqual(collected.stdoutLines, ["run", "--", "catalog", "--json"])
        XCTAssertEqual(collected.finished?.exitCode, 0)
    }

    func testWorkingDirectoryIsApplied() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let workingDirectory = directory.appendingPathComponent("work dir", isDirectory: true)
        try FileManager.default.createDirectory(at: workingDirectory, withIntermediateDirectories: true)

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            pwd
            """
        )
        let invocation = CliInvocation(
            executableURL: script,
            workingDirectoryURL: workingDirectory,
            label: "脚本"
        )
        let collected = try await TestSupport.collectEvents(
            from: CliRunner(invocation: invocation).start(arguments: [])
        )

        let reportedDirectory = try XCTUnwrap(collected.stdoutLines.first)
        XCTAssertEqual(
            URL(fileURLWithPath: reportedDirectory).resolvingSymlinksInPath().path,
            workingDirectory.resolvingSymlinksInPath().path
        )
    }

    func testEnvironmentOverridesAndDotnetDefaults() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            printf '%s\\n' "$DOTNET_CLI_TELEMETRY_OPTOUT" "$DOTNET_NOLOGO" "$DOTNET_SKIP_FIRST_TIME_EXPERIENCE" "$DIS_TEST_MARKER"
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))
        let collected = try await TestSupport.collectEvents(
            from: runner.start(arguments: [], environment: ["DIS_TEST_MARKER": "marker-value"])
        )

        XCTAssertEqual(collected.stdoutLines, ["1", "1", "1", "marker-value"])
    }

    // MARK: - runAndCollect

    func testRunAndCollectAggregatesBothStreams() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            echo "out-a"
            echo "err-a" >&2
            echo "out-b"
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))
        let output = try await runner.runAndCollect(arguments: ["--flag", "value"])

        XCTAssertEqual(output.result.exitCode, 0)
        XCTAssertTrue(output.result.succeeded)
        XCTAssertEqual(output.standardOutput, "out-a\nout-b\n")
        XCTAssertEqual(output.standardError, "err-a\n")
    }

    func testRunAndCollectAddsTrailingNewlineToTailLine() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            printf 'tail-without-newline'
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))
        let output = try await runner.runAndCollect(arguments: [])

        XCTAssertEqual(output.standardOutput, "tail-without-newline\n")
        XCTAssertEqual(output.standardError, "")
    }

    func testRunAndCollectReturnsNonZeroExitWithoutThrowing() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            echo "partial output"
            echo "boom" >&2
            exit 4
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))
        let output = try await runner.runAndCollect(arguments: [])

        XCTAssertEqual(output.result.exitCode, 4)
        XCTAssertFalse(output.result.succeeded)
        XCTAssertEqual(output.standardOutput, "partial output\n")
        XCTAssertEqual(output.standardError, "boom\n")
    }

    func testRunAndCollectThrowsTimedOut() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            sleep 5
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))

        let clock = ContinuousClock()
        let start = clock.now
        do {
            _ = try await runner.runAndCollect(arguments: [], timeout: .milliseconds(200))
            XCTFail("超时应抛 CliRunnerError.timedOut")
        } catch let error as CliRunnerError {
            guard case .timedOut = error else {
                return XCTFail("应抛 CliRunnerError.timedOut，实际是 \(error)")
            }
            XCTAssertTrue(
                error.errorDescription?.contains("没有完成") == true,
                "超时错误应给出可读说明：\(String(describing: error.errorDescription))"
            )
        } catch {
            XCTFail("应抛 CliRunnerError.timedOut，实际是 \(error)")
        }
        let elapsed = clock.now - start
        XCTAssertLessThan(elapsed, .seconds(10), "超时后应在取消子进程后立即返回，而不是等脚本自然结束")
    }

    func testExecutablePathWithSpacesIsQuotedAndRuns() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let spacedDirectory = directory.appendingPathComponent("cli dir", isDirectory: true)
        try FileManager.default.createDirectory(at: spacedDirectory, withIntermediateDirectories: true)
        let script = try TestSupport.writeExecutableScript(
            in: spacedDirectory,
            name: "disc image studio",
            contents: """
            #!/bin/sh
            printf '%s\\n' "$@"
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))
        let collected = try await TestSupport.collectEvents(
            from: runner.start(arguments: ["--input", "/tmp/my source.img"])
        )

        XCTAssertEqual(collected.stdoutLines, ["--input", "/tmp/my source.img"])
        XCTAssertEqual(
            collected.startedCommandLines,
            [
                "'\(script.path)' --input '/tmp/my source.img'",
            ]
        )
    }

    // MARK: - 取消

    func testCancelEndsJobPromptlyAndIsIdempotent() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            trap 'exit 0' TERM
            touch "$(dirname "$0")/ready"
            echo ready
            while :; do sleep 0.2; done
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))
        let job = runner.start(arguments: [])
        XCTAssertTrue(job.isRunning)

        // 等待脚本真正启动（而不是依赖固定 sleep），再取消，避免负载高时误判。
        try await TestSupport.waitForFile(at: directory.appendingPathComponent("ready"))

        let clock = ContinuousClock()
        let start = clock.now
        job.cancel()
        let collected = try await TestSupport.collectEvents(from: job, timeout: .seconds(15))
        let elapsed = clock.now - start

        XCTAssertNotNil(collected.finished, "取消后仍必须收到 .finished")
        XCTAssertLessThan(elapsed, .seconds(10), "取消应在 3 秒 SIGKILL 兜底内结束，放宽到 10 秒以防 CI 负载抖动")
        XCTAssertFalse(job.isRunning)
        XCTAssertTrue(collected.stdoutLines.contains("ready"))

        // 已结束的任务再次 cancel 无副作用。
        job.cancel()
        job.cancel()
        XCTAssertFalse(job.isRunning)
    }

    func testCancelAfterFinishDoesNotProduceExtraEvents() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = try TestSupport.writeExecutableScript(
            in: directory,
            contents: """
            #!/bin/sh
            echo done
            """
        )
        let runner = CliRunner(invocation: CliInvocation(executableURL: script, label: "脚本"))
        let job = runner.start(arguments: [])
        let collected = try await TestSupport.collectEvents(from: job)
        XCTAssertEqual(collected.stdoutLines, ["done"])
        XCTAssertFalse(job.isRunning)

        job.cancel()
        XCTAssertFalse(job.isRunning)
    }

    // MARK: - 启动失败

    func testMissingExecutableFinishesWithMinusOneAndSystemLine() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let missing = directory.appendingPathComponent("does-not-exist.sh", isDirectory: false)
        let runner = CliRunner(invocation: CliInvocation(executableURL: missing, label: "脚本"))
        let collected = try await TestSupport.collectEvents(from: runner.start(arguments: []))

        collected.assertWellFormed()
        XCTAssertEqual(collected.finished?.exitCode, -1)
        XCTAssertEqual(collected.finished?.succeeded, false)
        XCTAssertTrue(collected.stdoutLines.isEmpty)
        XCTAssertEqual(collected.systemLines.count, 1)
        let systemLine = try XCTUnwrap(collected.systemLines.first)
        XCTAssertTrue(systemLine.contains("无法启动 CLI"), "系统行应说明启动失败：\(systemLine)")
        XCTAssertTrue(systemLine.contains(missing.path), "系统行应包含可执行文件路径：\(systemLine)")
    }
}
