import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住 `drutil` 刻录路径的解析、计划生成与清理契约（BurnService.swift）。
final class BurnServiceTests: XCTestCase {

    /// 本机 `drutil list` 的真实表头（3 个空格缩进 + 固定列宽）。
    private let realDrutilHeader = "   Vendor   Product           Rev   Bus       SupportLevel"
    private let vendorColumn = 3
    private let productColumn = 12
    private let revisionColumn = 30
    private let busColumn = 36
    private let supportLevelColumn = 46

    // MARK: - parseDrives

    func testParseDrivesReadsRealColumnLayout() throws {
        let text = [
            realDrutilHeader,
            driveLine(index: 1, vendor: "Apple", product: "SuperDrive", revision: "2.00", bus: "USB", supportLevel: "Unsupported"),
            driveLine(index: 2, vendor: "PIONEER", product: "DVD-RW  DVR-112D", revision: "BC14", bus: "ATAPI", supportLevel: "Apple Shipping"),
            "",
        ].joined(separator: "\n")

        let drives = BurnService.parseDrives(text)
        XCTAssertEqual(drives.count, 2)

        XCTAssertEqual(drives[0].index, 1)
        XCTAssertEqual(drives[0].vendor, "Apple")
        XCTAssertEqual(drives[0].product, "SuperDrive")
        XCTAssertEqual(drives[0].revision, "2.00")
        XCTAssertEqual(drives[0].bus, "USB")
        XCTAssertEqual(drives[0].supportLevel, "Unsupported")
        XCTAssertEqual(drives[0].id, 1)
        XCTAssertEqual(drives[0].displayName, "SuperDrive（Apple） · USB")

        XCTAssertEqual(drives[1].index, 2)
        XCTAssertEqual(drives[1].vendor, "PIONEER")
        XCTAssertEqual(drives[1].product, "DVD-RW  DVR-112D")
        XCTAssertEqual(drives[1].revision, "BC14")
        XCTAssertEqual(drives[1].bus, "ATAPI")
        XCTAssertEqual(drives[1].supportLevel, "Apple Shipping")
        XCTAssertEqual(drives[1].id, 2)
    }

    func testParseDrivesReturnsEmptyWithoutHeaderOrRows() {
        XCTAssertEqual(BurnService.parseDrives(""), [])
        XCTAssertEqual(BurnService.parseDrives("no drives here\n"), [])
        XCTAssertEqual(BurnService.parseDrives(realDrutilHeader + "\n\n"), [])
        XCTAssertEqual(BurnService.parseDrives(realDrutilHeader + "\n   \n"), [])
    }

    // MARK: - status / toc 解析

    func testParseMediaType() {
        XCTAssertEqual(BurnService.parseMediaType("Type: CD-R  Name: /dev/disk4"), "CD-R")
        XCTAssertEqual(BurnService.parseMediaType("  Type: DVD-R\n"), "DVD-R")
        XCTAssertEqual(BurnService.parseMediaType("Type: BD-R\nBlank: Yes"), "BD-R")
        XCTAssertNil(BurnService.parseMediaType("Type:\n"))
        XCTAssertNil(BurnService.parseMediaType("Name: /dev/disk4\n"))
        XCTAssertNil(BurnService.parseMediaType(""))
    }

    func testParseBlankIndicator() {
        XCTAssertEqual(BurnService.parseBlankIndicator("Type: CD-R\nBlank: Yes\n"), true)
        XCTAssertEqual(BurnService.parseBlankIndicator("Blank: No\n"), false)
        XCTAssertEqual(BurnService.parseBlankIndicator("blank: yes"), true)
        XCTAssertEqual(BurnService.parseBlankIndicator("Blank: true"), true)
        XCTAssertEqual(BurnService.parseBlankIndicator("Blank: false"), false)
        XCTAssertNil(BurnService.parseBlankIndicator("Blank: Unknown\n"))
        XCTAssertNil(BurnService.parseBlankIndicator("Type: CD-R\n"))
        XCTAssertNil(BurnService.parseBlankIndicator(""))
    }

    func testParseHasTracks() {
        XCTAssertFalse(BurnService.parseHasTracks(""))
        XCTAssertFalse(BurnService.parseHasTracks("   \n\t"))
        XCTAssertFalse(BurnService.parseHasTracks("No media inserted.\n"))
        XCTAssertTrue(BurnService.parseHasTracks("Track 01: 00:02:00\n"))
        XCTAssertTrue(BurnService.parseHasTracks("Track 1: data\nTrack 2: data\n"))
        XCTAssertTrue(BurnService.parseHasTracks("Session 1 has 2 tracks\n"))
    }

    // MARK: - BurnKind

    func testBurnKindFromFileExtension() {
        XCTAssertEqual(BurnKind.kind(for: URL(fileURLWithPath: "/tmp/a.wav")), .audioCD)
        XCTAssertEqual(BurnKind.kind(for: URL(fileURLWithPath: "/tmp/a.WAV")), .audioCD)
        XCTAssertEqual(BurnKind.kind(for: URL(fileURLWithPath: "/tmp/a.aiff")), .audioCD)
        XCTAssertEqual(BurnKind.kind(for: URL(fileURLWithPath: "/tmp/a.iso")), .dataImage)
        XCTAssertEqual(BurnKind.kind(for: URL(fileURLWithPath: "/tmp/a.cue")), .dataImage)
        XCTAssertEqual(BurnKind.kind(for: URL(fileURLWithPath: "/tmp/a.dmg")), .dataImage)
        XCTAssertEqual(BurnKind.kind(for: URL(fileURLWithPath: "/tmp/a.bin")), .dataImage)
        XCTAssertEqual(BurnKind.kind(for: URL(fileURLWithPath: "/tmp/a.toc")), .dataImage)
        XCTAssertNil(BurnKind.kind(for: URL(fileURLWithPath: "/tmp/a.txt")))
        XCTAssertNil(BurnKind.kind(for: URL(fileURLWithPath: "/tmp/a")))
    }

    func testBurnKindFromMediaType() {
        XCTAssertEqual(BurnKind.kind(forMediaType: "CD-R"), .audioCD)
        XCTAssertEqual(BurnKind.kind(forMediaType: "cd-rw"), .audioCD)
        XCTAssertEqual(BurnKind.kind(forMediaType: "DVD-R"), .dataImage)
        XCTAssertEqual(BurnKind.kind(forMediaType: "DVD+R DL"), .dataImage)
        XCTAssertNil(BurnKind.kind(forMediaType: "BD-R"))
        XCTAssertNil(BurnKind.kind(forMediaType: "unknown"))
        XCTAssertNil(BurnKind.kind(forMediaType: nil))
    }

    // MARK: - mediaStatus 组合

    func testMediaStatusCombinesStatusAndToc() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = try makeService(
            in: directory,
            status: "Type: DVD-R  Name: /dev/disk4\nBlank: Yes\n",
            toc: "Track 01: 00:02:00\n",
            list: realDrutilHeader + "\n"
                + driveLine(index: 1, vendor: "Apple", product: "SuperDrive", revision: "2.00", bus: "USB", supportLevel: "Unsupported") + "\n"
        )

        let status = try await service.mediaStatus(drive: nil)
        XCTAssertEqual(status.mediaType, "DVD-R")
        XCTAssertEqual(status.blankIndicator, true)
        XCTAssertEqual(status.hasTracks, true)
        XCTAssertTrue(status.isPresent)
        XCTAssertTrue(status.isProbablyBlank, "drutil status 明确 Blank: Yes 时优先于 toc 的既有轨道")
        XCTAssertFalse(status.isBluRay)
        XCTAssertTrue(status.isWritable)
        XCTAssertTrue(status.rawText.contains("Blank: Yes"))

        let drives = try await service.listDrives()
        XCTAssertEqual(drives.map(\.vendor), ["Apple"])
    }

    func testMediaStatusWithoutMedia() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = try makeService(in: directory, status: "", toc: "")

        let status = try await service.mediaStatus(drive: nil)
        XCTAssertFalse(status.isPresent)
        XCTAssertNil(status.mediaType)
        XCTAssertNil(status.blankIndicator)
        XCTAssertFalse(status.hasTracks)
        XCTAssertTrue(status.isProbablyBlank)
        XCTAssertFalse(status.isBluRay)
        XCTAssertTrue(status.isWritable)
    }

    func testMediaStatusRewritesDrutilFailure() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let script = try TestSupport.writeExecutableScript(
            in: directory,
            name: "drutil",
            contents: """
            #!/bin/sh
            echo "drutil: no drives" >&2
            exit 2
            """
        )
        let service = BurnService(drutilURL: script, stagingRoot: directory)

        do {
            _ = try await service.listDrives()
            XCTFail("drutil list 失败应抛 BurnError.drutilFailed")
        } catch let error as BurnError {
            guard case .drutilFailed(let reason) = error else {
                return XCTFail("应映射为 drutilFailed，实际是 \(error)")
            }
            XCTAssertEqual(reason, "drutil: no drives")
        }
    }

    // MARK: - makePlan：数据盘

    func testMakePlanDataImageWithDrive() throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let iso = try makeFile(named: "disc.iso", in: directory, contents: "ISO")
        let service = BurnService(stagingRoot: directory.appendingPathComponent("staging", isDirectory: true))
        let drive = try secondDrive()

        let plan = try service.makePlan(for: iso, drive: drive)
        XCTAssertEqual(plan.kind, .dataImage)
        XCTAssertEqual(plan.sourceURL, iso.standardizedFileURL)
        XCTAssertEqual(
            plan.commandArguments,
            ["-drive", "2", "burn", "-eject", iso.path]
        )
        XCTAssertNil(plan.stagingDirectory)
        XCTAssertTrue(plan.commandLine.contains("-eject"))
    }

    func testMakePlanDataImageWithoutDrive() throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let cue = try makeFile(named: "disc.cue", in: directory, contents: "FILE ...")
        let service = BurnService(stagingRoot: directory.appendingPathComponent("staging", isDirectory: true))

        let plan = try service.makePlan(for: cue, drive: nil)
        XCTAssertEqual(plan.kind, .dataImage)
        XCTAssertEqual(plan.commandArguments, ["burn", "-eject", cue.path])
    }

    // MARK: - makePlan：音频 CD 暂存

    func testMakePlanAudioStagesSingleWavAndDiscardRemovesIt() throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let sourceWav = try makeFile(named: "track.wav", in: directory, contents: "RIFF....WAVE")
        let stagingRoot = directory.appendingPathComponent("staging", isDirectory: true)
        let service = BurnService(stagingRoot: stagingRoot)

        let plan = try service.makePlan(for: sourceWav, drive: nil)
        XCTAssertEqual(plan.kind, .audioCD)

        let staging = try XCTUnwrap(plan.stagingDirectory)
        XCTAssertTrue(staging.path.hasPrefix(stagingRoot.standardizedFileURL.path + "/"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: staging.path))
        XCTAssertEqual(
            plan.commandArguments,
            ["burn", "-eject", "-audio", staging.path]
        )

        let contents = try FileManager.default.contentsOfDirectory(atPath: staging.path)
        XCTAssertEqual(contents, ["track.wav"], "音频 CD 暂存目录应恰好包含一个 WAV")
        XCTAssertEqual(
            try Data(contentsOf: staging.appendingPathComponent("track.wav")),
            Data("RIFF....WAVE".utf8)
        )

        service.discard(plan)
        XCTAssertFalse(FileManager.default.fileExists(atPath: staging.path), "discard 应删除暂存目录")
        XCTAssertTrue(FileManager.default.fileExists(atPath: sourceWav.path), "源 WAV 不应被删除")
    }

    func testDiscardIgnoresDirectoriesOutsideStagingRoot() throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let sourceWav = try makeFile(named: "track.wav", in: directory, contents: "WAV")
        let ownerService = BurnService(
            stagingRoot: directory.appendingPathComponent("staging", isDirectory: true)
        )
        let plan = try ownerService.makePlan(for: sourceWav, drive: nil)
        let staging = try XCTUnwrap(plan.stagingDirectory)

        let foreignService = BurnService(
            stagingRoot: directory.appendingPathComponent("other-root", isDirectory: true)
        )
        foreignService.discard(plan)
        XCTAssertTrue(
            FileManager.default.fileExists(atPath: staging.path),
            "stagingRoot 之外的暂存目录不应被 foreignService 删除"
        )

        ownerService.discard(plan)
        XCTAssertFalse(FileManager.default.fileExists(atPath: staging.path))
    }

    // MARK: - makePlan：来源校验

    func testMakePlanRejectsUnsupportedOrMissingSource() throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = BurnService(stagingRoot: directory)
        let textFile = try makeFile(named: "notes.txt", in: directory, contents: "x")
        let missingISO = directory.appendingPathComponent("missing.iso", isDirectory: false)

        XCTAssertThrowsError(try service.makePlan(for: textFile, drive: nil)) { error in
            guard case BurnError.unsupportedSource(let path) = error else {
                return XCTFail("不支持的扩展名应抛 unsupportedSource，实际是 \(error)")
            }
            XCTAssertEqual(path, textFile.standardizedFileURL.path)
        }
        XCTAssertThrowsError(try service.makePlan(for: missingISO, drive: nil)) { error in
            guard case BurnError.unsupportedSource = error else {
                return XCTFail("不存在的文件应抛 unsupportedSource，实际是 \(error)")
            }
        }
    }

    // MARK: - makePlan：介质校验

    func testMakePlanRejectsMissingMedia() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let iso = try makeFile(named: "disc.iso", in: directory, contents: "ISO")
        let service = try makeService(in: directory, status: "", toc: "")
        let status = try await service.mediaStatus(drive: nil)

        XCTAssertThrowsError(try service.makePlan(for: iso, drive: nil, status: status)) { error in
            XCTAssertEqual(error as? BurnError, .noMedia)
        }
    }

    func testMakePlanRejectsBluRayMedia() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let iso = try makeFile(named: "disc.iso", in: directory, contents: "ISO")
        let service = try makeService(in: directory, status: "Type: BD-R\nBlank: Yes\n", toc: "")
        let status = try await service.mediaStatus(drive: nil)

        XCTAssertThrowsError(try service.makePlan(for: iso, drive: nil, status: status)) { error in
            XCTAssertEqual(error as? BurnError, .bluRayUnsupported)
        }
    }

    func testMakePlanRejectsNonBlankMedia() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let iso = try makeFile(named: "disc.iso", in: directory, contents: "ISO")

        // Blank: No
        let explicitService = try makeService(
            in: try makeEmptyDirectory(named: "explicit", in: directory),
            status: "Type: DVD-R\nBlank: No\n",
            toc: ""
        )
        let explicitStatus = try await explicitService.mediaStatus(drive: nil)
        XCTAssertThrowsError(try explicitService.makePlan(for: iso, drive: nil, status: explicitStatus)) { error in
            guard case BurnError.mediaNotWritable(let reason) = error else {
                return XCTFail("非空白盘应抛 mediaNotWritable，实际是 \(error)")
            }
            XCTAssertTrue(reason.contains("非空白"), "错误原因应说明盘片非空白：\(reason)")
        }

        // 没有 Blank 行，但 toc 报告 Track
        let tocService = try makeService(
            in: try makeEmptyDirectory(named: "toc", in: directory),
            status: "Type: DVD-R\n",
            toc: "Track 01: 00:02:00\n"
        )
        let tocStatus = try await tocService.mediaStatus(drive: nil)
        XCTAssertThrowsError(try tocService.makePlan(for: iso, drive: nil, status: tocStatus)) { error in
            guard case BurnError.mediaNotWritable = error else {
                return XCTFail("检测到轨道应抛 mediaNotWritable，实际是 \(error)")
            }
        }
    }

    func testMakePlanAcceptsBlankMedia() async throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let iso = try makeFile(named: "disc.iso", in: directory, contents: "ISO")
        let service = try makeService(in: directory, status: "Type: DVD-R\nBlank: Yes\n", toc: "")
        let status = try await service.mediaStatus(drive: nil)

        let plan = try service.makePlan(for: iso, drive: nil, status: status)
        XCTAssertEqual(plan.kind, .dataImage)
        XCTAssertEqual(plan.commandArguments, ["burn", "-eject", iso.path])
    }

    // MARK: - 可用性

    func testIsAvailableReflectsDrutilExecutable() throws {
        let directory = try TestSupport.makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let script = try makeFakeDrutil(in: directory)
        XCTAssertTrue(BurnService(drutilURL: script).isAvailable)
        XCTAssertFalse(
            BurnService(drutilURL: directory.appendingPathComponent("missing")).isAvailable
        )
    }

    // MARK: - Helpers

    private func driveLine(
        index: Int,
        vendor: String,
        product: String,
        revision: String,
        bus: String,
        supportLevel: String
    ) -> String {
        func pad(_ text: String, to width: Int) -> String {
            text.count >= width ? text : text + String(repeating: " ", count: width - text.count)
        }
        return pad(String(index), to: vendorColumn)
            + pad(vendor, to: productColumn - vendorColumn)
            + pad(product, to: revisionColumn - productColumn)
            + pad(revision, to: busColumn - revisionColumn)
            + pad(bus, to: supportLevelColumn - busColumn)
            + supportLevel
    }

    private func secondDrive() throws -> OpticalDrive {
        let text = [
            realDrutilHeader,
            driveLine(index: 1, vendor: "Apple", product: "SuperDrive", revision: "2.00", bus: "USB", supportLevel: "Unsupported"),
            driveLine(index: 2, vendor: "PIONEER", product: "DVD-RW  DVR-112D", revision: "BC14", bus: "ATAPI", supportLevel: "Apple Shipping"),
        ].joined(separator: "\n")
        let drives = BurnService.parseDrives(text)
        XCTAssertEqual(drives.count, 2)
        return try XCTUnwrap(drives.last)
    }

    private func makeEmptyDirectory(named name: String, in parent: URL) throws -> URL {
        let url = parent.appendingPathComponent(name, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func makeFile(named name: String, in directory: URL, contents: String) throws -> URL {
        let url = directory.appendingPathComponent(name, isDirectory: false)
        try Data(contents.utf8).write(to: url)
        return url
    }

    private func makeFakeDrutil(
        in directory: URL,
        list: String = "",
        status: String = "",
        toc: String = ""
    ) throws -> URL {
        try Data(list.utf8).write(to: directory.appendingPathComponent("list.txt", isDirectory: false))
        try Data(status.utf8).write(to: directory.appendingPathComponent("status.txt", isDirectory: false))
        try Data(toc.utf8).write(to: directory.appendingPathComponent("toc.txt", isDirectory: false))
        return try TestSupport.writeExecutableScript(
            in: directory,
            name: "drutil",
            contents: """
            #!/bin/sh
            last=""
            for argument in "$@"; do last="$argument"; done
            case "$last" in
              list) cat "$(dirname "$0")/list.txt" ;;
              status) cat "$(dirname "$0")/status.txt" ;;
              toc) cat "$(dirname "$0")/toc.txt" ;;
            esac
            """
        )
    }

    private func makeService(
        in directory: URL,
        status: String,
        toc: String,
        list: String = ""
    ) throws -> BurnService {
        let script = try makeFakeDrutil(in: directory, list: list, status: status, toc: toc)
        return BurnService(
            drutilURL: script,
            stagingRoot: directory.appendingPathComponent("staging", isDirectory: true)
        )
    }
}
