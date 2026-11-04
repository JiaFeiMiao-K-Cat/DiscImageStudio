import Foundation

// BurnStreamService.swift
//
// Orchestrates the experimental macOS streaming burn path described in
// macos/DESIGN.md §5.2:
//
//     CLI（--output -，stdout 输出原始块） --pipe--> disc-burn-stream --> 刻录机
//
// The Swift side owns no disc logic at all: the `disc-burn-stream` helper
// (macos/Sources/DiscBurnStream/) talks to DiscRecording, while the CLI only
// produces the raw byte stream. This file connects the two processes and turns
// their output into an event stream the GUI can render.
//
// Public API:
//
//   StreamBurnKind      cd-audio (2352-byte blocks) / dvd-data (2048-byte blocks)
//   StreamDrive         one `--list-drives` entry
//   StreamBurnPlan      a validated burn request + the exact helper arguments
//   StreamBurnState     preparing / writing / verifying / done / failed
//   StreamBurnProgress  state + fraction parsed from the helper's stderr
//   StreamLogLine       one forwarded log line, tagged helper or cli
//   StreamBurnResult    both exit codes, aggregated stderr, cancellation flag
//   StreamBurnEvent     started / log / progress / finished
//   BurnStreamJob       events + cancel() + isRunning
//
//   BurnStreamService(helperURL:cliInvocation:)
//     var isAvailable: Bool
//     func listDrives() async throws -> [StreamDrive]
//     func makePlan(kind:sectors:drive:speed:verify:eject:test:) throws -> StreamBurnPlan
//     func burn(plan:cliArguments:) -> BurnStreamJob
//
// Process wiring: the CLI's standard output and the helper's standard input are
// the two ends of a single `Pipe`, so the payload never passes through this
// process. Both stderr streams are read line by line; the helper's `state:` and
// `percent:` lines become `.progress` events (one event per state change and per
// percentage change) and are not repeated as log lines. Every other stderr line
// becomes a `.log` event.
//
// Success policy: the helper's exit code decides whether the disc was burned.
// A non-zero CLI exit terminates the helper (which then fails); the CLI's exit
// code is still reported in `StreamBurnResult` because the CLI may legitimately
// be killed by SIGPIPE once the helper stops reading.
//
// Cancellation: SIGTERM to both processes, SIGKILL after 2 seconds.

// MARK: - Kinds

/// The two kinds of disc the streaming helper accepts.
public enum StreamBurnKind: String, Sendable, Equatable, CaseIterable {
    case cdAudio = "cd-audio"
    case dvdData = "dvd-data"
    /// DiscRecording 公开了 BD 能力与 `kDRBurnStrategyBDDAO`；这是实验性路径，
    /// 没有实机 BD-R 验证前不要视为与 CD/DVD 同等可靠。
    case bdData = "bd-data"

    public var displayName: String {
        switch self {
        case .cdAudio: return "音频 CD（流式）"
        case .dvdData: return "DVD 数据盘（流式）"
        case .bdData: return "蓝光 BD-R（流式，实验性）"
        }
    }

    /// Size of one raw block on stdin: 2352 bytes for CD-DA, 2048 for data.
    public var blockSize: Int {
        switch self {
        case .cdAudio: return 2352
        case .dvdData, .bdData: return 2048
        }
    }

    /// Best-effort mapping from a `StreamDrive.mediaType` ("CD-R", "DVD-R", "BD-R", …).
    public static func kind(forMediaType mediaType: String?) -> StreamBurnKind? {
        guard let mediaType else { return nil }
        let upper = mediaType.uppercased()
        if upper.hasPrefix("CD") { return .cdAudio }
        if upper.hasPrefix("DVD") { return .dvdData }
        if upper.hasPrefix("BD") { return .bdData }
        return nil
    }
}

// MARK: - Drives

/// One entry of `disc-burn-stream --list-drives`.
public struct StreamDrive: Sendable, Equatable, Identifiable, Codable {
    /// Internal helper index, 1-based.
    public let index: Int
    public let vendor: String
    public let product: String
    public let firmware: String
    public let cdWritable: Bool
    public let dvdWritable: Bool
    public let bdWritable: Bool
    public let mediaPresent: Bool
    public let mediaBlank: Bool
    /// "CD-R", "DVD-R", "BD-R", …; nil when no media is present.
    public let mediaType: String?

    public var id: Int { index }

    public init(
        index: Int,
        vendor: String,
        product: String,
        firmware: String,
        cdWritable: Bool,
        dvdWritable: Bool,
        bdWritable: Bool,
        mediaPresent: Bool,
        mediaBlank: Bool,
        mediaType: String?
    ) {
        self.index = index
        self.vendor = vendor
        self.product = product
        self.firmware = firmware
        self.cdWritable = cdWritable
        self.dvdWritable = dvdWritable
        self.bdWritable = bdWritable
        self.mediaPresent = mediaPresent
        self.mediaBlank = mediaBlank
        self.mediaType = mediaType
    }

    public var displayName: String {
        var name = product.isEmpty ? "光盘驱动器" : product
        if !vendor.isEmpty {
            name += "（\(vendor)）"
        }
        return name
    }

    public var isMediaBlank: Bool {
        mediaPresent && mediaBlank
    }

    public func isWritable(for kind: StreamBurnKind) -> Bool {
        switch kind {
        case .cdAudio: return cdWritable
        case .dvdData: return dvdWritable
        case .bdData: return bdWritable
        }
    }

    public var isBluRayMedia: Bool {
        mediaType?.uppercased().hasPrefix("BD") == true
    }
}

// MARK: - Plan

/// A validated streaming burn request plus the exact helper arguments.
public struct StreamBurnPlan: Sendable, Equatable {
    public let kind: StreamBurnKind
    public let sectors: Int
    public let driveIndex: Int
    /// Speed factor (1× CD is 176.4 KB/s, 1× DVD is 1385 KB/s); nil means the
    /// drive's maximum.
    public let speed: Double?
    public let verify: Bool
    public let eject: Bool
    public let test: Bool
    /// Arguments passed to `disc-burn-stream` (executable excluded).
    public let helperArguments: [String]

    public init(
        kind: StreamBurnKind,
        sectors: Int,
        driveIndex: Int,
        speed: Double?,
        verify: Bool,
        eject: Bool,
        test: Bool,
        helperArguments: [String]
    ) {
        self.kind = kind
        self.sectors = sectors
        self.driveIndex = driveIndex
        self.speed = speed
        self.verify = verify
        self.eject = eject
        self.test = test
        self.helperArguments = helperArguments
    }

    /// Number of payload bytes the CLI is expected to write to stdout.
    public var expectedByteCount: Int64 {
        Int64(sectors) * Int64(kind.blockSize)
    }
}

// MARK: - Events

public enum StreamBurnState: String, Sendable, Equatable, CaseIterable {
    case preparing
    case writing
    case verifying
    case done
    case failed
}

/// Progress reported by the helper (`state:` / `percent:` lines).
public struct StreamBurnProgress: Sendable, Equatable {
    public let state: StreamBurnState
    /// 0…1; nil until the helper reports its first `percent:` line.
    public let fraction: Double?
    /// The helper line this update came from.
    public let rawLine: String

    public init(state: StreamBurnState, fraction: Double?, rawLine: String) {
        self.state = state
        self.fraction = fraction
        self.rawLine = rawLine
    }
}

/// One log line forwarded from either child process.
public struct StreamLogLine: Sendable, Equatable, Identifiable {
    public enum Source: String, Sendable, Equatable {
        case helper
        case cli
        case system
    }

    public let id: UUID
    public let source: Source
    public let text: String
    public let timestamp: Date

    public init(id: UUID = UUID(), source: Source, text: String, timestamp: Date = Date()) {
        self.id = id
        self.source = source
        self.text = text
        self.timestamp = timestamp
    }
}

public struct StreamBurnResult: Sendable, Equatable {
    /// Exit code of `disc-burn-stream`; -1 when it could not be launched.
    public let helperExitCode: Int32
    /// Exit code of the CLI; nil when it never ran.
    public let cliExitCode: Int32?
    public let duration: TimeInterval
    /// Complete stderr of both processes, kept for diagnostics.
    public let helperStandardError: String
    public let cliStandardError: String
    public let cancelled: Bool

    public init(
        helperExitCode: Int32,
        cliExitCode: Int32?,
        duration: TimeInterval,
        helperStandardError: String,
        cliStandardError: String,
        cancelled: Bool
    ) {
        self.helperExitCode = helperExitCode
        self.cliExitCode = cliExitCode
        self.duration = duration
        self.helperStandardError = helperStandardError
        self.cliStandardError = cliStandardError
        self.cancelled = cancelled
    }

    /// The helper's verdict is authoritative: it knows whether the disc was
    /// written. A CLI killed by SIGPIPE after the helper succeeded does not
    /// turn a good burn into a failure.
    public var succeeded: Bool {
        !cancelled && helperExitCode == 0
    }
}

public enum StreamBurnEvent: Sendable, Equatable {
    case started(helperCommandLine: String, cliCommandLine: String)
    case log(StreamLogLine)
    case progress(StreamBurnProgress)
    case finished(StreamBurnResult)
}

// MARK: - Job

/// A running (or finished) streaming burn. Consume `events` like `CliJob.events`.
public final class BurnStreamJob: Sendable {
    public let events: AsyncStream<StreamBurnEvent>
    private let controller: BurnStreamController

    fileprivate init(events: AsyncStream<StreamBurnEvent>, controller: BurnStreamController) {
        self.events = events
        self.controller = controller
    }

    /// Idempotent. SIGTERM to both processes, SIGKILL after 2 seconds.
    public func cancel() {
        controller.cancel()
    }

    public var isRunning: Bool {
        controller.isRunning
    }
}

// MARK: - Errors

public enum StreamBurnError: Error, Sendable, LocalizedError, Equatable {
    case helperMissing(String)
    case helperNotExecutable(String)
    case helperFailed(reason: String)
    case invalidDriveList(reason: String)
    case invalidPlan(reason: String)
    case driveNotWritable(index: Int, kind: StreamBurnKind)
    case driveHasNoMedia(index: Int)
    case mediaNotBlank(index: Int, mediaType: String?)
    case bluRayUnsupported

    public var errorDescription: String? {
        switch self {
        case .helperMissing(let path):
            return "找不到流式刻录助手 disc-burn-stream（\(path)）。请重新安装应用或检查打包内容。"
        case .helperNotExecutable(let path):
            return "流式刻录助手不可执行：\(path)"
        case .helperFailed(let reason):
            return "disc-burn-stream 执行失败：\(reason)"
        case .invalidDriveList(let reason):
            return "无法解析刻录机列表：\(reason)"
        case .invalidPlan(let reason):
            return "流式刻录参数无效：\(reason)"
        case .driveNotWritable(let index, let kind):
            return "第 \(index) 台刻录机不支持\(kind == .cdAudio ? " CD-R" : " DVD-R") 写入。"
        case .driveHasNoMedia(let index):
            return "第 \(index) 台刻录机里没有光盘。请放入空白盘后再试。"
        case .mediaNotBlank(let index, let mediaType):
            let type = mediaType.map { "（\($0)）" } ?? ""
            return "第 \(index) 台刻录机中的盘片\(type)不是空白盘，流式刻录会拒绝写入。请改用空白盘片。"
        case .bluRayUnsupported:
            return "蓝光流式写入仅支持空白 BD-R，且尚未经实机验证；BD-RE 与多层蓝光请改用文件模式或第三方软件。"
        }
    }
}

// MARK: - Service

public struct BurnStreamService: Sendable {
    /// The native helper executable.
    public let helperURL: URL
    /// How to launch the C# CLI (executable + optional dotnet prefix arguments).
    public let cliInvocation: CliInvocation

    public init(helperURL: URL, cliInvocation: CliInvocation) {
        self.helperURL = helperURL
        self.cliInvocation = cliInvocation
    }

    public var isAvailable: Bool {
        FileManager.default.isExecutableFile(atPath: helperURL.path)
    }

    // MARK: Drives

    /// Runs `disc-burn-stream --list-drives` and decodes the JSON array.
    /// No drives is a valid answer: an empty array.
    public func listDrives() async throws -> [StreamDrive] {
        let path = helperURL.path
        guard FileManager.default.fileExists(atPath: path) else {
            throw StreamBurnError.helperMissing(path)
        }
        guard FileManager.default.isExecutableFile(atPath: path) else {
            throw StreamBurnError.helperNotExecutable(path)
        }

        let runner = CliRunner(invocation: CliInvocation(executableURL: helperURL, label: "disc-burn-stream"))
        let output: CliRunOutput
        do {
            output = try await runner.runAndCollect(arguments: ["--list-drives"], timeout: .seconds(30))
        } catch {
            throw StreamBurnError.helperFailed(reason: error.localizedDescription)
        }
        guard output.result.succeeded else {
            let detail = output.standardError.trimmingCharacters(in: .whitespacesAndNewlines)
            throw StreamBurnError.helperFailed(reason: detail.isEmpty ? "退出码 \(output.result.exitCode)" : detail)
        }

        let data = Data(output.standardOutput.utf8)
        do {
            return try JSONDecoder().decode([StreamDrive].self, from: data)
        } catch {
            throw StreamBurnError.invalidDriveList(reason: error.localizedDescription)
        }
    }

    // MARK: Planning

    /// Validates a streaming burn request and builds the helper arguments.
    ///
    /// `drive` may be nil when the caller only wants the arguments (the helper
    /// then falls back to drive 1); when a drive is supplied its capabilities
    /// and media are checked here so the GUI can fail before any process starts.
    public func makePlan(
        kind: StreamBurnKind,
        sectors: Int,
        drive: StreamDrive? = nil,
        speed: Double? = nil,
        verify: Bool = true,
        eject: Bool = true,
        test: Bool = false
    ) throws -> StreamBurnPlan {
        guard sectors > 0 else {
            throw StreamBurnError.invalidPlan(reason: "扇区数必须大于 0。")
        }
        if let speed, !(speed > 0) {
            throw StreamBurnError.invalidPlan(reason: "刻录速度必须是正数。")
        }

        if let drive {
            if drive.isBluRayMedia {
                guard kind == .bdData else {
                    throw StreamBurnError.invalidPlan(reason: "蓝光介质需要使用 bd-data 流式刻录。")
                }
                guard drive.mediaType?.uppercased() == "BD-R" else {
                    throw StreamBurnError.bluRayUnsupported
                }
            } else if kind == .bdData {
                throw StreamBurnError.invalidPlan(reason: "bd-data 需要空白 BD-R 介质。")
            }
            guard drive.isWritable(for: kind) else {
                throw StreamBurnError.driveNotWritable(index: drive.index, kind: kind)
            }
            guard drive.mediaPresent else {
                throw StreamBurnError.driveHasNoMedia(index: drive.index)
            }
            guard drive.mediaBlank else {
                throw StreamBurnError.mediaNotBlank(index: drive.index, mediaType: drive.mediaType)
            }
            if let mediaKind = StreamBurnKind.kind(forMediaType: drive.mediaType), mediaKind != kind {
                throw StreamBurnError.invalidPlan(
                    reason: "盘片类型 \(drive.mediaType ?? "未知") 与 \(kind.displayName) 不匹配。"
                )
            }
        }

        var arguments = ["--kind", kind.rawValue, "--sectors", String(sectors)]
        arguments += ["--drive", String(drive?.index ?? 1)]
        if let speed {
            arguments += ["--speed", Self.format(speed: speed)]
        }
        if !verify { arguments.append("--no-verify") }
        if !eject { arguments.append("--no-eject") }
        if test { arguments.append("--test") }

        return StreamBurnPlan(
            kind: kind,
            sectors: sectors,
            driveIndex: drive?.index ?? 1,
            speed: speed,
            verify: verify,
            eject: eject,
            test: test,
            helperArguments: arguments
        )
    }

    /// Helper command line for logging / display only.
    public func helperCommandLine(for plan: StreamBurnPlan) -> String {
        CommandArgumentBuilder.displayCommand(executable: helperURL.path, arguments: plan.helperArguments)
    }

    // MARK: Burning

    /// Starts the CLI and the helper and returns their combined event stream.
    ///
    /// `cliArguments` are the CLI arguments that make it write raw blocks to
    /// stdout (`--output -`), e.g. `["cd-generate", "--output", "-", "--sectors", "1234"]`.
    public func burn(plan: StreamBurnPlan, cliArguments: [String]) -> BurnStreamJob {
        let (stream, continuation) = AsyncStream.makeStream(
            of: StreamBurnEvent.self,
            bufferingPolicy: .unbounded
        )
        let controller = BurnStreamController(
            plan: plan,
            cliInvocation: cliInvocation,
            cliArguments: cliArguments,
            helperURL: helperURL,
            continuation: continuation
        )
        let job = BurnStreamJob(events: stream, controller: controller)
        controller.launch()
        return job
    }

    // MARK: Helpers

    private static func format(speed: Double) -> String {
        if speed == speed.rounded() {
            return String(Int(speed))
        }
        var text = String(format: "%.2f", speed)
        while text.hasSuffix("0") {
            text.removeLast()
        }
        if text.hasSuffix(".") {
            text.removeLast()
        }
        return text
    }
}

// MARK: - Controller

/// Owns the two `Process` objects, the payload pipe and both stderr pipes.
/// All mutable state is guarded by `lock`; callbacks arrive on Foundation's
/// private queues, so the main thread is never blocked.
private final class BurnStreamController: @unchecked Sendable {
    private let plan: StreamBurnPlan
    private let cliInvocation: CliInvocation
    private let cliArguments: [String]
    private let helperURL: URL
    private let continuation: AsyncStream<StreamBurnEvent>.Continuation
    private let startedAt = Date()
    private let lock = NSLock()

    private let payloadPipe = Pipe()
    private let helperErrorPipe = Pipe()
    private let cliErrorPipe = Pipe()
    private let helperProcess = Process()
    private let cliProcess = Process()

    private var helperBuffer = Data()
    private var cliBuffer = Data()
    private var helperStandardError = ""
    private var cliStandardError = ""
    private var helperExitCode: Int32?
    private var cliExitCode: Int32?
    private var helperLaunched = false
    private var cliLaunched = false
    private var helperOutputDone = false
    private var cliOutputDone = false
    private var finished = false
    private var cancelled = false
    /// nil until the helper reports its first state line.
    private var currentState: StreamBurnState?
    private var currentFraction: Double?

    init(
        plan: StreamBurnPlan,
        cliInvocation: CliInvocation,
        cliArguments: [String],
        helperURL: URL,
        continuation: AsyncStream<StreamBurnEvent>.Continuation
    ) {
        self.plan = plan
        self.cliInvocation = cliInvocation
        self.cliArguments = cliArguments
        self.helperURL = helperURL
        self.continuation = continuation
    }

    var isRunning: Bool {
        lock.lock()
        defer { lock.unlock() }
        return (cliLaunched || helperLaunched) && !finished
    }

    // MARK: Launch

    func launch() {
        continuation.yield(.started(
            helperCommandLine: CommandArgumentBuilder.displayCommand(
                executable: helperURL.path,
                arguments: plan.helperArguments
            ),
            cliCommandLine: CommandArgumentBuilder.displayCommand(
                executable: cliInvocation.executableURL.path,
                arguments: cliInvocation.prefixArguments + cliArguments
            )
        ))

        configureCLI()
        configureHelper()

        do {
            try cliProcess.run()
            lock.lock()
            cliLaunched = true
            lock.unlock()
            startReading(cliErrorPipe.fileHandleForReading, source: .cli)
        } catch {
            emitSystemLine("无法启动 CLI（\(cliInvocation.executableURL.path)）：\(error.localizedDescription)")
            lock.lock()
            cliExitCode = -1
            lock.unlock()
            finishIfComplete()
            return
        }

        do {
            try helperProcess.run()
            lock.lock()
            helperLaunched = true
            lock.unlock()
            startReading(helperErrorPipe.fileHandleForReading, source: .helper)
        } catch {
            emitSystemLine("无法启动 disc-burn-stream（\(helperURL.path)）：\(error.localizedDescription)")
            lock.lock()
            helperExitCode = -1
            lock.unlock()
            // No reader for the payload pipe any more: stop the CLI.
            terminate(cliProcess, label: "CLI")
            scheduleForceKill(after: 2)
            finishIfComplete()
        }
    }

    private func configureCLI() {
        cliProcess.executableURL = cliInvocation.executableURL
        cliProcess.arguments = cliInvocation.prefixArguments + cliArguments
        cliProcess.currentDirectoryURL = cliInvocation.workingDirectoryURL
            ?? FileManager.default.homeDirectoryForCurrentUser
        cliProcess.standardInput = FileHandle.nullDevice
        cliProcess.standardOutput = payloadPipe
        cliProcess.standardError = cliErrorPipe
        cliProcess.environment = Self.makeEnvironment()

        cliProcess.terminationHandler = { [weak self] process in
            self?.processExited(process.terminationStatus, source: .cli)
        }
    }

    private func configureHelper() {
        helperProcess.executableURL = helperURL
        helperProcess.arguments = plan.helperArguments
        helperProcess.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser
        helperProcess.standardInput = payloadPipe
        helperProcess.standardOutput = FileHandle.nullDevice
        helperProcess.standardError = helperErrorPipe
        helperProcess.environment = Self.makeEnvironment()

        helperProcess.terminationHandler = { [weak self] process in
            self?.processExited(process.terminationStatus, source: .helper)
        }
    }

    // MARK: Cancellation

    func cancel() {
        lock.lock()
        if cancelled || finished {
            lock.unlock()
            return
        }
        cancelled = true
        let helperRunning = helperLaunched && helperProcess.isRunning
        let cliRunning = cliLaunched && cliProcess.isRunning
        lock.unlock()

        emitSystemLine("正在取消流式刻录…")
        if helperRunning {
            terminate(helperProcess, label: "disc-burn-stream")
        }
        if cliRunning {
            terminate(cliProcess, label: "CLI")
        }
        if helperRunning || cliRunning {
            scheduleForceKill(after: 2)
        }
    }

    /// SIGTERM, unless the process already exited. Callers must not hold `lock`.
    private func terminate(_ process: Process, label: String) {
        guard process.isRunning else { return }
        process.terminate()
        emitSystemLine("已向 \(label) 发送 SIGTERM")
    }

    private func scheduleForceKill(after seconds: Double) {
        DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + seconds) { [weak self] in
            guard let self else { return }
            self.lock.lock()
            let helperRunning = self.helperLaunched && self.helperProcess.isRunning
            let cliRunning = self.cliLaunched && self.cliProcess.isRunning
            let alreadyFinished = self.finished
            self.lock.unlock()
            if alreadyFinished { return }
            if helperRunning {
                kill(self.helperProcess.processIdentifier, SIGKILL)
            }
            if cliRunning {
                kill(self.cliProcess.processIdentifier, SIGKILL)
            }
        }
    }

    // MARK: Process output

    private func consume(_ data: Data, source: StreamLogLine.Source) {
        let lines: [String]
        lock.lock()
        var buffer = source == .helper ? helperBuffer : cliBuffer
        buffer.append(data)
        var complete: [String] = []
        while let newline = buffer.firstIndex(of: 0x0A) {
            let lineData = buffer[buffer.startIndex..<newline]
            buffer.removeSubrange(buffer.startIndex...newline)
            complete.append(Self.text(from: lineData))
        }
        if source == .helper {
            helperBuffer = buffer
        } else {
            cliBuffer = buffer
        }
        lines = complete
        lock.unlock()

        for line in lines {
            forward(line, source: source)
        }
    }

    /// Reads one stderr pipe on a background thread until EOF. Blocking reads
    /// keep byte order deterministic: the termination handler can never race
    /// with the reader, so the aggregated stderr strings are reproducible.
    private func startReading(_ handle: FileHandle, source: StreamLogLine.Source) {
        DispatchQueue.global(qos: .utility).async { [weak self] in
            guard let self else { return }
            while true {
                let data = try? handle.read(upToCount: 8192)
                guard let data, !data.isEmpty else { break }
                self.consume(data, source: source)
            }
            self.outputFinished(source)
        }
    }

    /// Called from the reader thread once EOF is reached: emit the last partial
    /// line, mark this side's output as complete and maybe finish the job.
    private func outputFinished(_ source: StreamLogLine.Source) {
        var tailLine: String?
        lock.lock()
        if source == .helper {
            tailLine = Self.takeRemainder(&helperBuffer)
            helperOutputDone = true
        } else {
            tailLine = Self.takeRemainder(&cliBuffer)
            cliOutputDone = true
        }
        lock.unlock()

        if let tailLine, !tailLine.isEmpty {
            forward(tailLine, source: source)
        }
        finishIfComplete()
    }

    private func processExited(_ status: Int32, source: StreamLogLine.Source) {
        lock.lock()
        if source == .helper {
            helperExitCode = status
        } else {
            cliExitCode = status
        }
        lock.unlock()

        if source == .helper {
            onHelperExited(status)
        } else {
            onCLIExited(status)
        }
        finishIfComplete()
    }

    private func onHelperExited(_ status: Int32) {
        lock.lock()
        let cancelled = self.cancelled
        lock.unlock()
        if cancelled { return }

        emitSystemLine("disc-burn-stream 已退出（退出码 \(status)）")
        // The helper stops reading stdin when it finishes; the CLI may still be
        // writing. Give it a moment to die of SIGPIPE, then stop it ourselves.
        DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + 1.0) { [weak self] in
            guard let self else { return }
            self.lock.lock()
            let cliRunning = self.cliLaunched && self.cliProcess.isRunning && !self.finished
            self.lock.unlock()
            guard cliRunning else { return }
            self.terminate(self.cliProcess, label: "CLI")
            self.scheduleForceKill(after: 2)
        }
    }

    private func onCLIExited(_ status: Int32) {
        lock.lock()
        let cancelled = self.cancelled
        let helperStillRunning = helperLaunched && helperProcess.isRunning && helperExitCode == nil
        lock.unlock()
        if cancelled { return }

        if status != 0 && helperStillRunning {
            emitSystemLine("CLI 以退出码 \(status) 结束，正在终止 disc-burn-stream")
            terminate(helperProcess, label: "disc-burn-stream")
            scheduleForceKill(after: 2)
        } else {
            emitSystemLine("CLI 已退出（退出码 \(status)）")
        }
    }

    private func finishIfComplete() {
        lock.lock()
        if finished {
            lock.unlock()
            return
        }
        // A launched process is done once it has exited and its stderr reader
        // has seen EOF; a process that never launched can never report either.
        // At least one exit code must be known, otherwise a launch is in flight.
        let helperDone = !helperLaunched || (helperExitCode != nil && helperOutputDone)
        let cliDone = !cliLaunched || (cliExitCode != nil && cliOutputDone)
        guard helperDone, cliDone, helperExitCode != nil || cliExitCode != nil else {
            lock.unlock()
            return
        }
        finished = true
        let result = StreamBurnResult(
            helperExitCode: helperExitCode ?? -1,
            cliExitCode: cliExitCode,
            duration: Date().timeIntervalSince(startedAt),
            helperStandardError: helperStandardError,
            cliStandardError: cliStandardError,
            cancelled: cancelled
        )
        lock.unlock()

        publishResult(result)
    }

    private func publishResult(_ result: StreamBurnResult) {
        if !result.succeeded && !result.cancelled {
            let detail = Self.lastErrorLine(in: result.helperStandardError)
                ?? Self.lastErrorLine(in: result.cliStandardError)
            if let detail {
                emitSystemLine("流式刻录失败：\(detail)")
            }
        } else if result.succeeded {
            emitSystemLine("流式刻录完成（用时 \(String(format: "%.1f", result.duration)) 秒）")
        }
        continuation.yield(.finished(result))
        continuation.finish()
    }

    // MARK: Line forwarding

    private func forward(_ line: String, source: StreamLogLine.Source) {
        if source == .helper {
            // Raw text is always recorded for diagnostics (and for
            // StreamBurnResult.helperStandardError).
            record(line, source: .helper)

            if let progress = parseHelperProgress(line) {
                lock.lock()
                let previousState = currentState
                let previousFraction = currentFraction
                currentState = progress.state
                currentFraction = progress.fraction
                let changed = previousState != progress.state || previousFraction != progress.fraction
                lock.unlock()

                if changed {
                    continuation.yield(.progress(progress))
                }
                return
            }

            continuation.yield(.log(StreamLogLine(source: .helper, text: line)))
            return
        }

        record(line, source: .cli)
        continuation.yield(.log(StreamLogLine(source: .cli, text: line)))
    }

    /// Converts a `state:` or `percent:` helper line into a progress update.
    /// The two line kinds complement each other: a state line carries the last
    /// known fraction, a percent line carries the current state.
    private func parseHelperProgress(_ line: String) -> StreamBurnProgress? {
        if let state = Self.parseState(line) {
            lock.lock()
            let fraction = currentFraction
            lock.unlock()
            return StreamBurnProgress(state: state, fraction: fraction, rawLine: line)
        }
        if let fraction = Self.parsePercent(line) {
            lock.lock()
            let state = currentState ?? .preparing
            lock.unlock()
            return StreamBurnProgress(state: state, fraction: fraction, rawLine: line)
        }
        return nil
    }

    /// `record` only aggregates the full stderr text; it never emits an event,
    /// so `state:`/`percent:` lines do not show up twice in the log console.
    private func record(_ line: String, source: StreamLogLine.Source) {
        lock.lock()
        if source == .helper {
            helperStandardError += line + "\n"
        } else {
            cliStandardError += line + "\n"
        }
        lock.unlock()
    }

    /// Parses a `state: preparing|writing|verifying|done|failed` helper line.
    private static func parseState(_ line: String) -> StreamBurnState? {
        guard line.hasPrefix("state:") else { return nil }
        let raw = line.dropFirst("state:".count).trimmingCharacters(in: .whitespaces)
        return StreamBurnState(rawValue: raw)
    }

    /// Parses a `percent: 42.3` helper line into 0…1.
    private static func parsePercent(_ line: String) -> Double? {
        guard line.hasPrefix("percent:") else { return nil }
        let raw = line.dropFirst("percent:".count).trimmingCharacters(in: .whitespaces)
        guard let percent = Double(raw) else { return nil }
        return min(max(percent / 100.0, 0), 1)
    }

    private func emitSystemLine(_ text: String) {
        continuation.yield(.log(StreamLogLine(source: .system, text: text)))
    }

    // MARK: Static helpers

    private static func text(from data: Data) -> String {
        var string = String(decoding: data, as: UTF8.self)
        if string.hasSuffix("\r") {
            string.removeLast()
        }
        return string
    }

    private static func takeRemainder(_ buffer: inout Data) -> String {
        guard !buffer.isEmpty else { return "" }
        let string = text(from: buffer)
        buffer.removeAll(keepingCapacity: false)
        return string
    }

    private static func lastErrorLine(in text: String) -> String? {
        for line in text.split(separator: "\n").reversed() {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("error:") {
                return String(trimmed.dropFirst("error:".count)).trimmingCharacters(in: .whitespaces)
            }
        }
        return nil
    }

    /// Same environment policy as `CliRunner`: inherit, force the dotnet
    /// telemetry/nologo switches off (the CLI may be launched through
    /// `dotnet run`).
    private static func makeEnvironment() -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        environment["DOTNET_NOLOGO"] = "1"
        environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
        return environment
    }
}
