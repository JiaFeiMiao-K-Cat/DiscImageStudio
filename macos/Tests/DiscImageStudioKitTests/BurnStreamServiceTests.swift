import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住 CLI → `disc-burn-stream` 流式刻录编排契约（DESIGN.md §5.2、BurnStreamService.swift）。
///
/// 全部用临时目录里的 shell 脚本模拟 CLI 与 helper，不依赖真实刻录机或 DiscRecording。
final class BurnStreamServiceTests: XCTestCase {

    // MARK: - 事件流：成功路径

    func testBurnPipesPayloadForwardLogsAndProgress() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let cli = try TestSupport.writeExecutableScript(
            in: directory,
            name: "fake-cli",
            contents: """
            #!/bin/sh
            echo "cli: generating" >&2
            head -c 4704 /dev/zero
            exit 0
            """
        )
        let helper = try TestSupport.writeExecutableScript(
            in: directory,
            name: "disc-burn-stream",
            contents: """
            #!/bin/sh
            cat > "$(dirname "$0")/payload.bin"
            echo "helper: ready" >&2
            echo "state: preparing" >&2
            echo "percent: 0" >&2
            echo "state: writing" >&2
            echo "percent: 42.3" >&2
            echo "state: verifying" >&2
            echo "state: done" >&2
            exit 0
            """
        )

        let service = BurnStreamService(
            helperURL: helper,
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI")
        )
        let plan = try service.makePlan(kind: .cdAudio, sectors: 2)
        XCTAssertEqual(plan.helperArguments, ["--kind", "cd-audio", "--sectors", "2", "--drive", "1"])
        XCTAssertEqual(plan.expectedByteCount, 4704)

        let job = service.burn(
            plan: plan,
            cliArguments: ["cd-generate", "--output", "-", "--sectors", "2"]
        )
        let collected = try await collect(from: job, timeout: .seconds(10))
        let result = try XCTUnwrap(collected.finished)

        // 1. CLI stdout 的原始字节通过管道完整到达 helper stdin。
        let payload = try Data(contentsOf: directory.appendingPathComponent("payload.bin"))
        XCTAssertEqual(payload.count, 4704)
        XCTAssertEqual(payload, Data(count: 4704), "payload 应逐字节转发")

        // 2. 两端 stderr 都转发成日志，来源标记正确。
        let helperLogs = collected.logs.filter { $0.source == .helper }.map(\.text)
        let cliLogs = collected.logs.filter { $0.source == .cli }.map(\.text)
        XCTAssertEqual(helperLogs, ["helper: ready"])
        XCTAssertEqual(cliLogs, ["cli: generating"])

        // 3. state:/percent: 变成 progress 事件，且不重复出现在日志里。
        let progress = collected.progress
        XCTAssertEqual(
            progress.map(\.state),
            [.preparing, .preparing, .writing, .writing, .verifying, .done]
        )
        XCTAssertNil(progress[0].fraction)
        XCTAssertEqual(try XCTUnwrap(progress[1].fraction), 0, accuracy: 0.000_001)
        XCTAssertEqual(try XCTUnwrap(progress[2].fraction), 0, accuracy: 0.000_001)
        XCTAssertEqual(try XCTUnwrap(progress[3].fraction), 0.423, accuracy: 0.000_001)
        XCTAssertEqual(progress[3].rawLine, "percent: 42.3")
        XCTAssertEqual(try XCTUnwrap(progress.last?.fraction), 0.423, accuracy: 0.000_001)
        XCTAssertFalse(collected.logs.contains { $0.text.hasPrefix("percent:") })
        XCTAssertFalse(collected.logs.contains { $0.text.hasPrefix("state:") })

        // 4. 结果：helper 退出码定胜负，CLI 退出码也保留。
        XCTAssertEqual(result.helperExitCode, 0)
        XCTAssertEqual(result.cliExitCode, 0)
        XCTAssertTrue(result.succeeded)
        XCTAssertFalse(result.cancelled)
        XCTAssertEqual(result.helperStandardError, "helper: ready\nstate: preparing\npercent: 0\nstate: writing\npercent: 42.3\nstate: verifying\nstate: done\n")
        XCTAssertEqual(result.cliStandardError, "cli: generating\n")

        XCTAssertEqual(collected.startedHelperCommandLine?.contains("disc-burn-stream"), true)
        XCTAssertEqual(collected.startedCLICommandLine?.contains("fake-cli"), true)
        XCTAssertFalse(job.isRunning)
    }

    // MARK: - CLI 非 0：整体失败

    func testCLINonZeroExitFailsTheWholeBurn() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let cli = try TestSupport.writeExecutableScript(
            in: directory,
            name: "fake-cli",
            contents: """
            #!/bin/sh
            echo "error: encoder failed" >&2
            head -c 100 /dev/zero
            exit 3
            """
        )
        let helper = try TestSupport.writeExecutableScript(
            in: directory,
            name: "disc-burn-stream",
            contents: """
            #!/bin/sh
            cat > "$(dirname "$0")/payload.bin"
            sleep 30
            exit 0
            """
        )

        let service = BurnStreamService(
            helperURL: helper,
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI")
        )
        let plan = try service.makePlan(kind: .cdAudio, sectors: 1)
        let job = service.burn(plan: plan, cliArguments: ["cd-generate", "--output", "-"])
        let collected = try await collect(from: job, timeout: .seconds(10))
        let result = try XCTUnwrap(collected.finished)

        XCTAssertEqual(result.cliExitCode, 3)
        XCTAssertNotEqual(result.helperExitCode, 0, "CLI 失败后 helper 必须被终止")
        XCTAssertFalse(result.succeeded, "CLI 非 0 时整体失败")
        XCTAssertFalse(result.cancelled)
        XCTAssertTrue(result.cliStandardError.contains("error: encoder failed"))
        XCTAssertTrue(
            collected.logs.contains { $0.source == .system && $0.text.contains("正在终止 disc-burn-stream") },
            "应记录终止 helper 的原因：\(collected.logs.map(\.text))"
        )
        XCTAssertFalse(job.isRunning)
    }

    func testHelperFailureIsReportedEvenWhenCLISucceeds() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let cli = try TestSupport.writeExecutableScript(
            in: directory,
            name: "fake-cli",
            contents: """
            #!/bin/sh
            head -c 2048 /dev/zero
            exit 0
            """
        )
        let helper = try TestSupport.writeExecutableScript(
            in: directory,
            name: "disc-burn-stream",
            contents: """
            #!/bin/sh
            cat > "$(dirname "$0")/payload.bin"
            echo "state: failed" >&2
            echo "error: no blank media" >&2
            exit 2
            """
        )

        let service = BurnStreamService(
            helperURL: helper,
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI")
        )
        let plan = try service.makePlan(kind: .dvdData, sectors: 1)
        let job = service.burn(plan: plan, cliArguments: ["solve", "--iso-output", "-"])
        let collected = try await collect(from: job, timeout: .seconds(10))
        let result = try XCTUnwrap(collected.finished)

        XCTAssertEqual(result.helperExitCode, 2)
        XCTAssertFalse(result.succeeded)
        XCTAssertEqual(collected.progress.last?.state, .failed)
        XCTAssertTrue(
            collected.logs.contains { $0.source == .system && $0.text.contains("流式刻录失败：no blank media") },
            "失败摘要应带 helper 的 error: 行：\(collected.logs.map(\.text))"
        )
    }

    // MARK: - 取消

    func testCancelTerminatesBothProcesses() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let cli = try TestSupport.writeExecutableScript(
            in: directory,
            name: "fake-cli",
            contents: """
            #!/bin/sh
            trap 'exit 0' TERM
            touch "$(dirname "$0")/cli-ready"
            echo "cli: waiting" >&2
            while :; do sleep 0.2; done
            """
        )
        let helper = try TestSupport.writeExecutableScript(
            in: directory,
            name: "disc-burn-stream",
            contents: """
            #!/bin/sh
            trap 'exit 0' TERM
            touch "$(dirname "$0")/helper-ready"
            echo "state: preparing" >&2
            while :; do sleep 0.2; done
            """
        )

        let service = BurnStreamService(
            helperURL: helper,
            cliInvocation: CliInvocation(executableURL: cli, label: "fake CLI")
        )
        let plan = try service.makePlan(kind: .cdAudio, sectors: 2)
        let job = service.burn(plan: plan, cliArguments: ["cd-generate", "--output", "-"])

        // 两端都真正启动后再取消，避免负载高时 helper 尚未 launch 导致少一条 SIGTERM 日志。
        try await TestSupport.waitForFile(at: directory.appendingPathComponent("cli-ready"))
        try await TestSupport.waitForFile(at: directory.appendingPathComponent("helper-ready"))

        let clock = ContinuousClock()
        let start = clock.now
        job.cancel()
        let collected = try await collect(from: job, timeout: .seconds(15))
        let elapsed = clock.now - start
        let result = try XCTUnwrap(collected.finished, "取消后仍必须收到 finished")

        XCTAssertTrue(result.cancelled)
        XCTAssertFalse(result.succeeded)
        XCTAssertLessThan(elapsed, .seconds(10), "取消应在 2 秒 SIGKILL 兜底内结束，放宽到 10 秒以防 CI 负载抖动")
        XCTAssertTrue(
            collected.logs.contains { $0.text == "正在取消流式刻录…" },
            "应记录取消动作：\(collected.logs.map(\.text))"
        )
        XCTAssertTrue(
            collected.logs.contains { $0.source == .system && $0.text.contains("已向 disc-burn-stream 发送 SIGTERM") }
        )
        XCTAssertTrue(
            collected.logs.contains { $0.source == .system && $0.text.contains("已向 CLI 发送 SIGTERM") }
        )
        XCTAssertFalse(job.isRunning)

        // 幂等。
        job.cancel()
        XCTAssertFalse(job.isRunning)
    }

    // MARK: - listDrives

    func testListDrivesDecodesHelperJSON() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let json = #"""
        [
          {
            "index": 1,
            "vendor": "PIONEER",
            "product": "BD-RW BDR-XD08",
            "firmware": "1.03",
            "cdWritable": true,
            "dvdWritable": true,
            "bdWritable": true,
            "mediaPresent": true,
            "mediaBlank": true,
            "mediaType": "CD-R"
          },
          {
            "index": 2,
            "vendor": "Apple",
            "product": "SuperDrive",
            "firmware": "2.00",
            "cdWritable": true,
            "dvdWritable": false,
            "bdWritable": false,
            "mediaPresent": false,
            "mediaBlank": false
          }
        ]
        """#
        let helper = try makeListDrivesHelper(in: directory, json: json)
        let service = BurnStreamService(
            helperURL: helper,
            cliInvocation: CliInvocation(executableURL: helper, label: "fake CLI")
        )

        let drives = try await service.listDrives()
        XCTAssertEqual(drives.count, 2)
        XCTAssertEqual(drives[0].index, 1)
        XCTAssertEqual(drives[0].vendor, "PIONEER")
        XCTAssertEqual(drives[0].product, "BD-RW BDR-XD08")
        XCTAssertEqual(drives[0].firmware, "1.03")
        XCTAssertEqual(drives[0].mediaType, "CD-R")
        XCTAssertTrue(drives[0].isMediaBlank)
        XCTAssertTrue(drives[0].isWritable(for: .cdAudio))
        XCTAssertTrue(drives[0].isWritable(for: .dvdData))
        XCTAssertFalse(drives[0].isBluRayMedia)
        XCTAssertEqual(drives[0].displayName, "BD-RW BDR-XD08（PIONEER）")

        XCTAssertEqual(drives[1].index, 2)
        XCTAssertNil(drives[1].mediaType)
        XCTAssertFalse(drives[1].mediaPresent)
        XCTAssertFalse(drives[1].isMediaBlank)
        XCTAssertFalse(drives[1].isWritable(for: .dvdData))
    }

    func testListDrivesEmptyArrayAndMissingHelper() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let helper = try makeListDrivesHelper(in: directory, json: "[]")
        let service = BurnStreamService(
            helperURL: helper,
            cliInvocation: CliInvocation(executableURL: helper, label: "fake CLI")
        )
        let empty = try await service.listDrives()
        XCTAssertEqual(empty, [])

        let missing = BurnStreamService(
            helperURL: directory.appendingPathComponent("missing-helper"),
            cliInvocation: CliInvocation(executableURL: helper, label: "fake CLI")
        )
        do {
            _ = try await missing.listDrives()
            XCTFail("helper 不存在应抛 helperMissing")
        } catch let error as StreamBurnError {
            guard case .helperMissing = error else {
                return XCTFail("应抛 helperMissing，实际是 \(error)")
            }
            XCTAssertTrue(error.errorDescription?.contains("找不到流式刻录助手") == true)
        }
    }

    func testListDrivesRejectsInvalidJSONAndNonExecutableHelper() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let helper = try makeListDrivesHelper(in: directory, json: "not json")
        let service = BurnStreamService(
            helperURL: helper,
            cliInvocation: CliInvocation(executableURL: helper, label: "fake CLI")
        )
        do {
            _ = try await service.listDrives()
            XCTFail("坏 JSON 应抛 invalidDriveList")
        } catch let error as StreamBurnError {
            guard case .invalidDriveList = error else {
                return XCTFail("应抛 invalidDriveList，实际是 \(error)")
            }
        }

        let nonExecutable = directory.appendingPathComponent("not-executable")
        try Data("#!/bin/sh\n".utf8).write(to: nonExecutable)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o644)],
            ofItemAtPath: nonExecutable.path
        )
        let blocked = BurnStreamService(
            helperURL: nonExecutable,
            cliInvocation: CliInvocation(executableURL: nonExecutable, label: "fake CLI")
        )
        do {
            _ = try await blocked.listDrives()
            XCTFail("不可执行的 helper 应抛 helperNotExecutable")
        } catch let error as StreamBurnError {
            guard case .helperNotExecutable = error else {
                return XCTFail("应抛 helperNotExecutable，实际是 \(error)")
            }
        }
    }

    // MARK: - makePlan

    func testMakePlanBuildsHelperArgumentsAndByteCount() throws {
        let service = try makeUnusedService()
        let cd = try service.makePlan(kind: .cdAudio, sectors: 100)
        XCTAssertEqual(cd.kind, .cdAudio)
        XCTAssertEqual(cd.sectors, 100)
        XCTAssertEqual(cd.driveIndex, 1)
        XCTAssertNil(cd.speed)
        XCTAssertTrue(cd.verify)
        XCTAssertTrue(cd.eject)
        XCTAssertFalse(cd.test)
        XCTAssertEqual(cd.helperArguments, ["--kind", "cd-audio", "--sectors", "100", "--drive", "1"])
        XCTAssertEqual(cd.expectedByteCount, 235_200)

        let dvd = try service.makePlan(
            kind: .dvdData,
            sectors: 10,
            drive: StreamDrive(
                index: 2,
                vendor: "PIONEER",
                product: "DVD-RW",
                firmware: "1.0",
                cdWritable: true,
                dvdWritable: true,
                bdWritable: false,
                mediaPresent: true,
                mediaBlank: true,
                mediaType: "DVD-R"
            ),
            speed: 8.0,
            verify: false,
            eject: false,
            test: true
        )
        XCTAssertEqual(
            dvd.helperArguments,
            [
                "--kind", "dvd-data", "--sectors", "10", "--drive", "2",
                "--speed", "8", "--no-verify", "--no-eject", "--test",
            ]
        )
        XCTAssertEqual(dvd.expectedByteCount, 20_480)
        XCTAssertEqual(StreamBurnKind.dvdData.blockSize, 2048)

        // 小数速度保留两位有效数字。
        let fractional = try service.makePlan(kind: .cdAudio, sectors: 1, speed: 4.5)
        XCTAssertEqual(fractional.helperArguments.last, "4.5")

        XCTAssertTrue(service.helperCommandLine(for: cd).contains("--kind cd-audio"))
    }

    func testMakePlanRejectsInvalidSectorsAndSpeed() throws {
        let service = try makeUnusedService()
        for sectors in [0, -1] {
            XCTAssertThrowsError(try service.makePlan(kind: .cdAudio, sectors: sectors)) { error in
                guard case StreamBurnError.invalidPlan(let reason) = error else {
                    return XCTFail("扇区数 \(sectors) 应抛 invalidPlan，实际是 \(error)")
                }
                XCTAssertEqual(reason, "扇区数必须大于 0。")
            }
        }
        XCTAssertThrowsError(try service.makePlan(kind: .cdAudio, sectors: 1, speed: 0)) { error in
            guard case StreamBurnError.invalidPlan(let reason) = error else {
                return XCTFail("速度 0 应抛 invalidPlan，实际是 \(error)")
            }
            XCTAssertEqual(reason, "刻录速度必须是正数。")
        }
    }

    func testMakePlanValidatesDriveCapabilitiesAndMedia() throws {
        let service = try makeUnusedService()

        func drive(
            cdWritable: Bool = true,
            dvdWritable: Bool = true,
            mediaPresent: Bool = true,
            mediaBlank: Bool = true,
            mediaType: String? = "CD-R"
        ) -> StreamDrive {
            StreamDrive(
                index: 1,
                vendor: "V",
                product: "P",
                firmware: "1",
                cdWritable: cdWritable,
                dvdWritable: dvdWritable,
                bdWritable: false,
                mediaPresent: mediaPresent,
                mediaBlank: mediaBlank,
                mediaType: mediaType
            )
        }

        XCTAssertThrowsError(try service.makePlan(kind: .cdAudio, sectors: 1, drive: drive(mediaType: "BD-R"))) { error in
            XCTAssertEqual(error as? StreamBurnError, .bluRayUnsupported)
        }
        XCTAssertThrowsError(try service.makePlan(kind: .dvdData, sectors: 1, drive: drive(dvdWritable: false))) { error in
            XCTAssertEqual(
                error as? StreamBurnError,
                .driveNotWritable(index: 1, kind: .dvdData)
            )
        }
        XCTAssertThrowsError(try service.makePlan(kind: .cdAudio, sectors: 1, drive: drive(mediaPresent: false))) { error in
            XCTAssertEqual(error as? StreamBurnError, .driveHasNoMedia(index: 1))
        }
        XCTAssertThrowsError(try service.makePlan(kind: .cdAudio, sectors: 1, drive: drive(mediaBlank: false, mediaType: "CD-RW"))) { error in
            XCTAssertEqual(
                error as? StreamBurnError,
                .mediaNotBlank(index: 1, mediaType: "CD-RW")
            )
        }
        // 盘片类型与请求不匹配。
        XCTAssertThrowsError(try service.makePlan(kind: .cdAudio, sectors: 1, drive: drive(mediaType: "DVD-R"))) { error in
            guard case StreamBurnError.invalidPlan(let reason) = error else {
                return XCTFail("类型不匹配应抛 invalidPlan，实际是 \(error)")
            }
            XCTAssertTrue(reason.contains("DVD-R"))
        }
    }

    // MARK: - 类型映射与结果判定

    func testStreamBurnKindMappingsAndResultPolicy() {
        XCTAssertEqual(StreamBurnKind.kind(forMediaType: "CD-R"), .cdAudio)
        XCTAssertEqual(StreamBurnKind.kind(forMediaType: "cd-rw"), .cdAudio)
        XCTAssertEqual(StreamBurnKind.kind(forMediaType: "DVD+R"), .dvdData)
        XCTAssertNil(StreamBurnKind.kind(forMediaType: "BD-R"))
        XCTAssertNil(StreamBurnKind.kind(forMediaType: nil))

        let success = StreamBurnResult(
            helperExitCode: 0,
            cliExitCode: 0,
            duration: 1,
            helperStandardError: "",
            cliStandardError: "",
            cancelled: false
        )
        XCTAssertTrue(success.succeeded)
        XCTAssertFalse(
            StreamBurnResult(
                helperExitCode: 0, cliExitCode: 0, duration: 1,
                helperStandardError: "", cliStandardError: "", cancelled: true
            ).succeeded
        )
        XCTAssertFalse(
            StreamBurnResult(
                helperExitCode: 2, cliExitCode: 0, duration: 1,
                helperStandardError: "", cliStandardError: "", cancelled: false
            ).succeeded
        )
    }

    // MARK: - Helpers

    private func makeUnusedService() throws -> BurnStreamService {
        let directory = try TestSupport.makeTemporaryDirectory("BurnStreamUnused")
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }
        return BurnStreamService(
            helperURL: directory.appendingPathComponent("disc-burn-stream", isDirectory: false),
            cliInvocation: CliInvocation(
                executableURL: directory.appendingPathComponent("disc-image-studio", isDirectory: false),
                label: "fake CLI"
            )
        )
    }

    private func makeListDrivesHelper(in directory: URL, json: String) throws -> URL {
        let payloadURL = directory.appendingPathComponent("drives.json", isDirectory: false)
        try Data(json.utf8).write(to: payloadURL)
        return try TestSupport.writeExecutableScript(
            in: directory,
            name: "disc-burn-stream",
            contents: """
            #!/bin/sh
            cat "$(dirname "$0")/drives.json"
            """
        )
    }

    // MARK: - 事件收集

    private struct CollectedStreamEvents {
        let events: [StreamBurnEvent]

        var logs: [StreamLogLine] {
            events.compactMap { event in
                guard case .log(let line) = event else { return nil }
                return line
            }
        }

        var progress: [StreamBurnProgress] {
            events.compactMap { event in
                guard case .progress(let progress) = event else { return nil }
                return progress
            }
        }

        var finished: StreamBurnResult? {
            events.compactMap { event in
                guard case .finished(let result) = event else { return nil }
                return result
            }.last
        }

        var startedHelperCommandLine: String? {
            events.compactMap { event in
                guard case .started(let helper, _) = event else { return nil }
                return helper
            }.first
        }

        var startedCLICommandLine: String? {
            events.compactMap { event in
                guard case .started(_, let cli) = event else { return nil }
                return cli
            }.first
        }
    }

    private func collect(
        from job: BurnStreamJob,
        timeout: Duration = .seconds(10)
    ) async throws -> CollectedStreamEvents {
        let events = try await withThrowingTaskGroup(of: [StreamBurnEvent].self) { group in
            group.addTask {
                var collected: [StreamBurnEvent] = []
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
        return CollectedStreamEvents(events: events)
    }
}
