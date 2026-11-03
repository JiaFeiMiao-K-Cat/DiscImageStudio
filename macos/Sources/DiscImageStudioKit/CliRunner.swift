import Foundation

/// 一次 CLI 进程调用的描述。`prefixArguments` 用于 `dotnet run --project … --` 这类回退。
public struct CliInvocation: Sendable, Equatable {
    public let executableURL: URL
    public let prefixArguments: [String]
    public let workingDirectoryURL: URL?
    public let label: String

    public init(
        executableURL: URL,
        prefixArguments: [String] = [],
        workingDirectoryURL: URL? = nil,
        label: String
    ) {
        self.executableURL = executableURL
        self.prefixArguments = prefixArguments
        self.workingDirectoryURL = workingDirectoryURL
        self.label = label
    }

    public var debugCommandLine: String {
        CommandArgumentBuilder.displayCommand(
            executable: executableURL.path,
            arguments: prefixArguments
        )
    }
}

public struct CliResult: Sendable, Equatable {
    public let exitCode: Int32
    public let duration: TimeInterval

    public init(exitCode: Int32, duration: TimeInterval) {
        self.exitCode = exitCode
        self.duration = duration
    }

    public var succeeded: Bool { exitCode == 0 }
}

public struct LogLine: Sendable, Equatable, Identifiable {
    public enum Stream: String, Sendable, Equatable {
        case stdout
        case stderr
        case system
    }

    public let id: UUID
    public let stream: Stream
    public let text: String
    public let timestamp: Date

    public init(id: UUID = UUID(), stream: Stream, text: String, timestamp: Date = Date()) {
        self.id = id
        self.stream = stream
        self.text = text
        self.timestamp = timestamp
    }
}

public enum CliEvent: Sendable, Equatable {
    case started(commandLine: String)
    case line(LogLine)
    case finished(CliResult)
}

public struct CliRunOutput: Sendable, Equatable {
    public let result: CliResult
    public let standardOutput: String
    public let standardError: String

    public init(result: CliResult, standardOutput: String, standardError: String) {
        self.result = result
        self.standardOutput = standardOutput
        self.standardError = standardError
    }
}

public enum CliRunnerError: Error, Sendable, LocalizedError, Equatable {
    case finishedWithoutResult
    case timedOut(seconds: Int)

    public var errorDescription: String? {
        switch self {
        case .finishedWithoutResult:
            return "CLI 进程没有返回结果。"
        case .timedOut(let seconds):
            return "CLI 在 \(seconds) 秒内没有完成，已终止。"
        }
    }
}

/// 一个正在运行（或已结束）的 CLI 任务。
public final class CliJob: Sendable {
    public let events: AsyncStream<CliEvent>
    private let controller: ProcessController

    fileprivate init(events: AsyncStream<CliEvent>, controller: ProcessController) {
        self.events = events
        self.controller = controller
    }

    /// 幂等。先发 SIGTERM，3 秒后仍未退出则 SIGKILL。
    public func cancel() {
        controller.cancel()
    }

    public var isRunning: Bool {
        controller.isRunning
    }
}

public struct CliRunner: Sendable {
    public let invocation: CliInvocation

    public init(invocation: CliInvocation) {
        self.invocation = invocation
    }

    /// 启动一个任务；`started` 事件在方法返回前后立即产生，随后是 stdout/stderr 行与 `finished`。
    public func start(arguments: [String], environment: [String: String]? = nil) -> CliJob {
        let (stream, continuation) = AsyncStream.makeStream(
            of: CliEvent.self,
            bufferingPolicy: .unbounded
        )
        let controller = ProcessController(
            invocation: invocation,
            arguments: arguments,
            environment: environment,
            continuation: continuation
        )
        let job = CliJob(events: stream, controller: controller)
        controller.launch()
        return job
    }

    /// 收集全部输出，用于 `catalog --json` 等短命令。超时会把任务取消并抛出 `timedOut`。
    public func runAndCollect(
        arguments: [String],
        environment: [String: String]? = nil,
        timeout: Duration = .seconds(60)
    ) async throws -> CliRunOutput {
        let job = start(arguments: arguments, environment: environment)
        var standardOutput = ""
        var standardError = ""
        var result: CliResult?
        let timeoutFlag = TimeoutFlag()
        let timeoutSeconds = Int(timeout.components.seconds)

        let watchdog = Task { [job, timeoutFlag] in
            do {
                try await Task.sleep(for: timeout)
                if job.isRunning {
                    timeoutFlag.mark()
                    job.cancel()
                }
            } catch {
                // 正常结束：任务被取消（sleep 被取消）
            }
        }

        for await event in job.events {
            switch event {
            case .started:
                break
            case .line(let line):
                switch line.stream {
                case .stdout: standardOutput += line.text + "\n"
                case .stderr: standardError += line.text + "\n"
                case .system: break
                }
            case .finished(let finished):
                result = finished
            }
        }
        watchdog.cancel()

        guard let result else { throw CliRunnerError.finishedWithoutResult }
        if timeoutFlag.timedOut, !result.succeeded {
            throw CliRunnerError.timedOut(seconds: timeoutSeconds)
        }
        return CliRunOutput(result: result, standardOutput: standardOutput, standardError: standardError)
    }
}

// MARK: - 进程控制

/// `runAndCollect` 的看门狗与事件循环之间的单向标志。
private final class TimeoutFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var value = false

    func mark() {
        lock.lock()
        value = true
        lock.unlock()
    }

    var timedOut: Bool {
        lock.lock()
        defer { lock.unlock() }
        return value
    }
}

/// 把 `Process` 的管道读取转换成 `AsyncStream<CliEvent>`。
/// 所有可变状态由 `lock` 保护；回调来自 Foundation 的私有队列。
private final class ProcessController: @unchecked Sendable {
    private let invocation: CliInvocation
    private let arguments: [String]
    private let environment: [String: String]?
    private let continuation: AsyncStream<CliEvent>.Continuation
    private let lock = NSLock()
    private let process = Process()
    private let startedAt = Date()

    private var stdoutBuffer = Data()
    private var stderrBuffer = Data()
    private var finished = false
    private var cancelled = false
    private var started = false

    init(
        invocation: CliInvocation,
        arguments: [String],
        environment: [String: String]?,
        continuation: AsyncStream<CliEvent>.Continuation
    ) {
        self.invocation = invocation
        self.arguments = arguments
        self.environment = environment
        self.continuation = continuation
    }

    var isRunning: Bool {
        lock.lock()
        defer { lock.unlock() }
        return started && !finished
    }

    func launch() {
        let fullArguments = invocation.prefixArguments + arguments
        let commandLine = CommandArgumentBuilder.displayCommand(
            executable: invocation.executableURL.path,
            arguments: fullArguments
        )
        continuation.yield(.started(commandLine: commandLine))

        let standardOutput = Pipe()
        let standardError = Pipe()
        process.executableURL = invocation.executableURL
        process.arguments = fullArguments
        process.currentDirectoryURL = invocation.workingDirectoryURL
            ?? FileManager.default.homeDirectoryForCurrentUser
        process.standardOutput = standardOutput
        process.standardError = standardError
        process.standardInput = FileHandle.nullDevice
        process.environment = Self.makeEnvironment(overrides: environment)

        standardOutput.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard let self else { return }
            if data.isEmpty {
                handle.readabilityHandler = nil
                return
            }
            self.consume(data, stream: .stdout)
        }
        standardError.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard let self else { return }
            if data.isEmpty {
                handle.readabilityHandler = nil
                return
            }
            self.consume(data, stream: .stderr)
        }
        process.terminationHandler = { [weak self] finishedProcess in
            self?.finish(exitCode: finishedProcess.terminationStatus)
        }

        lock.lock()
        started = true
        lock.unlock()

        do {
            try process.run()
        } catch {
            continuation.yield(.line(LogLine(
                stream: .system,
                text: "无法启动 CLI（\(invocation.executableURL.path)）：\(error.localizedDescription)"
            )))
            finish(exitCode: -1)
            return
        }

        lock.lock()
        let shouldCancelImmediately = cancelled
        lock.unlock()
        if shouldCancelImmediately {
            process.terminate()
            scheduleForceKill()
        }
    }

    func cancel() {
        lock.lock()
        cancelled = true
        let running = started && !finished
        lock.unlock()

        guard running else { return }
        continuation.yield(.line(LogLine(stream: .system, text: "正在取消任务…")))
        process.terminate()
        scheduleForceKill()
    }

    private func scheduleForceKill() {
        DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + 3) { [process] in
            guard process.isRunning else { return }
            kill(process.processIdentifier, SIGKILL)
        }
    }

    private func consume(_ data: Data, stream: LogLine.Stream) {
        lock.lock()
        defer { lock.unlock() }
        var buffer = stream == .stdout ? stdoutBuffer : stderrBuffer
        buffer.append(data)
        var lines: [String] = []
        while let newline = buffer.firstIndex(of: 0x0A) {
            let lineData = buffer[buffer.startIndex..<newline]
            buffer.removeSubrange(buffer.startIndex...newline)
            lines.append(Self.text(from: lineData))
        }
        if stream == .stdout {
            stdoutBuffer = buffer
        } else {
            stderrBuffer = buffer
        }
        for text in lines {
            continuation.yield(.line(LogLine(stream: stream, text: text)))
        }
    }

    private func finish(exitCode: Int32) {
        lock.lock()
        if finished {
            lock.unlock()
            return
        }
        finished = true
        let stdoutTail = Self.takeRemainder(&stdoutBuffer)
        let stderrTail = Self.takeRemainder(&stderrBuffer)
        let duration = Date().timeIntervalSince(startedAt)
        lock.unlock()

        if !stdoutTail.isEmpty {
            continuation.yield(.line(LogLine(stream: .stdout, text: stdoutTail)))
        }
        if !stderrTail.isEmpty {
            continuation.yield(.line(LogLine(stream: .stderr, text: stderrTail)))
        }
        continuation.yield(.finished(CliResult(exitCode: exitCode, duration: duration)))
        continuation.finish()
    }

    private static func takeRemainder(_ buffer: inout Data) -> String {
        guard !buffer.isEmpty else { return "" }
        let text = self.text(from: buffer)
        buffer.removeAll(keepingCapacity: false)
        return text
    }

    /// 按字节切分已完成的单行数据；UTF-8 多字节序列不会包含 0x0A，因此逐行解码是安全的。
    private static func text(from data: Data) -> String {
        var string = String(decoding: data, as: UTF8.self)
        if string.hasSuffix("\r") {
            string.removeLast()
        }
        return string
    }

    private static func makeEnvironment(overrides: [String: String]?) -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        environment["DOTNET_NOLOGO"] = "1"
        environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
        if let overrides {
            environment.merge(overrides) { _, new in new }
        }
        return environment
    }
}
