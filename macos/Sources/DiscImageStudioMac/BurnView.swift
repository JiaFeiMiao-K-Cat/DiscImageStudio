import AppKit
import SwiftUI
import DiscImageStudioKit

/// 刻录工作区：文件模式复用系统 drutil；流式模式预留给 DiscRecording 助手。
struct BurnView: View {
    let model: AppModel
    let burnModel: BurnModel
    var showsHeader: Bool = true

    @Environment(\.colorScheme) private var colorScheme
    @State private var showsConfirmation = false
    @State private var streamAcknowledged = false
    @State private var streamEject = true
    @State private var streamVerify = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Spacing.l) {
                if showsHeader {
                    header
                }
                modeCard

                if burnModel.mode == .file {
                    driveCard
                    mediaCard
                    sourceCard
                    actionCard
                } else {
                    streamCard
                }

                if burnModel.isBurning || burnModel.lastResult != nil {
                    progressCard
                    LogConsoleView(
                        lines: burnModel.logLines,
                        isRunning: burnModel.isBurning,
                        title: L("刻录日志"),
                        emptyHint: L("刻录开始后，drutil 的日志会逐行显示在这里。"),
                        onClear: { burnModel.clearLog() }
                    )
                    resultCard
                }
            }
            .frame(maxWidth: DesignSystem.contentMaxWidth, alignment: .leading)
            .padding(Spacing.xxl)
            .frame(maxWidth: .infinity, alignment: .center)
        }
        .background(DesignSystem.appBackground)
        .task {
            await initialLoad()
        }
        .sheet(isPresented: $showsConfirmation) {
            BurnConfirmationSheet(burnModel: burnModel) {
                burnModel.startBurn()
            }
        }
    }

    // MARK: - 载入

    private func initialLoad() async {
        burnModel.updateStreamContext(project: model.project)
        burnModel.prefillIfNeeded(model.project?.burnPrefillURL ?? model.latestBurnableOutputURL)
        if burnModel.drives.isEmpty || burnModel.mediaStatus == nil {
            await burnModel.refreshDrives()
        } else {
            await burnModel.refreshMediaStatus()
        }
        if burnModel.mode == .stream {
            await burnModel.refreshStreamDrives()
        }
    }

    // MARK: - 标题

    private var header: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            HStack(alignment: .center, spacing: Spacing.m) {
                ZStack {
                    Circle()
                        .fill(DesignSystem.accent(colorScheme).opacity(0.16))
                        .frame(width: 38, height: 38)
                    Image(systemName: "opticaldisc.fill")
                        .font(.system(size: 18, weight: .semibold))
                        .foregroundStyle(DesignSystem.accent(colorScheme))
                }
                VStack(alignment: .leading, spacing: Spacing.xxs) {
                    Text("刻录到光盘")
                        .font(.system(size: 22, weight: .bold))
                    Text("把生成的 .wav / .iso / .dmg 等文件交给 macOS 系统刻录工具。")
                        .font(.system(size: 12.5))
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
                Chip(text: L("仅写入空白盘"), systemImage: "checkmark.shield", tint: .green)
                Chip(text: L("不可撤销"), systemImage: "exclamationmark.triangle", tint: .orange)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: - 模式

    private var modeCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(L("刻录方式"), systemImage: "slider.horizontal.3", subtitle: L("文件模式稳定可用；流式模式为实验性预留。"))

            HStack(spacing: 2) {
                modeButton(.file, identifier: "burn-mode-file")
                modeButton(.stream, identifier: "burn-mode-stream")
            }
            .padding(3)
            .background(DesignSystem.subtleFill, in: Capsule())
            .frame(maxWidth: 480, alignment: .leading)

            Text(modeDescription)
                .font(Typography.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private func modeButton(_ mode: BurnModel.Mode, identifier: String) -> some View {
        let selected = burnModel.mode == mode
        return Button {
            burnModel.mode = mode
            if mode == .stream {
                Task { await burnModel.refreshStreamDrives() }
            }
        } label: {
            HStack(spacing: Spacing.xs) {
                Image(systemName: mode == .file ? "doc.on.doc" : "waveform.path")
                    .font(.system(size: 10.5, weight: .semibold))
                Text(mode.shortName)
                    .font(.system(size: 12, weight: .medium))
                if mode == .stream && !burnModel.isStreamAvailable {
                    Image(systemName: "lock.fill")
                        .font(.system(size: 9))
                        .foregroundStyle(.orange)
                }
            }
            .foregroundStyle(selected ? Color.white : Color.secondary)
            .padding(.horizontal, Spacing.m)
            .padding(.vertical, Spacing.xs)
            .frame(maxWidth: .infinity)
            .background(
                selected ? DesignSystem.accent(colorScheme) : Color.clear,
                in: Capsule()
            )
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .disabled(burnModel.isBurning || burnModel.isStreamBurning)
        .accessibilityIdentifier(identifier)
        .accessibilityAddTraits(selected ? [.isSelected] : [])
    }

    private var modeDescription: String {
        switch burnModel.mode {
        case .file:
            return L("调用 /usr/bin/drutil：音频 CD 会先把 WAV 放进临时目录再用 burn -audio 写入；数据盘直接 burn 镜像文件。")
        case .stream:
            return L("流式模式用当前工程直接生成数据，经 DiscRecording 的 DRTrack producer 写入刻录机，不落完整镜像。当前支持 CD 音频与 BD 数据盘，实验性。")
        }
    }

    // MARK: - 刻录机

    private var driveCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(L("光盘刻录机"), systemImage: "externaldrive") {
                HStack(spacing: Spacing.xs) {
                    if burnModel.isLoadingDrives {
                        ProgressView()
                            .controlSize(.mini)
                    }
                    Button {
                        Task { await burnModel.refreshDrives() }
                    } label: {
                        Label("刷新刻录机", systemImage: "arrow.clockwise")
                    }
                    .controlSize(.small)
                    .disabled(burnModel.isLoadingDrives || burnModel.isBurning)
                    .accessibilityIdentifier("refresh-drives")
                }
            }

            if !burnModel.isDRUtilAvailable {
                Label("系统缺少 /usr/bin/drutil，文件模式刻录不可用。", systemImage: "xmark.octagon.fill")
                    .font(Typography.caption)
                    .foregroundStyle(.red)
            } else if burnModel.drives.isEmpty {
                VStack(alignment: .leading, spacing: Spacing.xxs) {
                    Text(burnModel.isLoadingDrives ? "正在查找刻录机…" : "没有找到光盘刻录机。")
                        .font(Typography.caption)
                        .foregroundStyle(.secondary)
                    Text("请连接 USB 刻录机，放入空白 CD/DVD-R，然后点「刷新刻录机」。")
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                }
            } else {
                HStack(spacing: Spacing.m) {
                    Text("刻录机")
                        .font(Typography.caption)
                        .foregroundStyle(.secondary)
                        .frame(width: 64, alignment: .leading)
                    Picker("刻录机", selection: driveSelection) {
                        ForEach(burnModel.drives) { drive in
                            Text(drive.displayName).tag(drive.index)
                        }
                    }
                    .labelsHidden()
                    .pickerStyle(.menu)
                    .frame(maxWidth: 420)
                    .disabled(burnModel.isBurning)
                }
                if let drive = burnModel.selectedDrive {
                    Grid(alignment: .leading, horizontalSpacing: Spacing.l, verticalSpacing: Spacing.xxs) {
                        GridRow(alignment: .firstTextBaseline) {
                            driveLabel(L("索引"))
                            Text("drutil -drive \(drive.index)")
                                .font(Typography.monoSmall)
                                .foregroundStyle(.secondary)
                        }
                        GridRow(alignment: .firstTextBaseline) {
                            driveLabel(L("支持级别"))
                            Text(drive.supportLevel.isEmpty ? "未知" : drive.supportLevel)
                                .font(.system(size: 11.5))
                        }
                    }
                }
            }

            if let error = burnModel.driveErrorMessage, !burnModel.drives.isEmpty {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(Typography.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private var driveSelection: Binding<Int> {
        Binding(
            get: { burnModel.selectedDriveIndex ?? burnModel.drives.first?.index ?? 0 },
            set: { burnModel.selectDrive($0) }
        )
    }

    private func driveLabel(_ text: String) -> some View {
        Text(L(text))
            .font(Typography.caption)
            .foregroundStyle(.secondary)
            .frame(width: 64, alignment: .leading)
    }

    // MARK: - 介质

    private var mediaCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(L("介质状态"), systemImage: "opticaldisc") {
                HStack(spacing: Spacing.xs) {
                    if burnModel.isLoadingStatus {
                        ProgressView()
                            .controlSize(.mini)
                    }
                    Button {
                        Task { await burnModel.refreshMediaStatus() }
                    } label: {
                        Label("刷新介质", systemImage: "arrow.clockwise")
                    }
                    .controlSize(.small)
                    .disabled(burnModel.isLoadingStatus || burnModel.selectedDrive == nil || burnModel.isBurning)
                }
            }

            HStack(spacing: Spacing.xs) {
                Circle()
                    .fill(burnModel.statusColor)
                    .frame(width: 8, height: 8)
                Text(burnModel.mediaStatusDescription)
                    .font(.system(size: 12))
                    .foregroundStyle(burnModel.mediaStatus == nil ? .secondary : .primary)
                Spacer(minLength: 0)
                if let status = burnModel.mediaStatus {
                    Chip(text: status.isProbablyBlank ? L("空白") : L("非空白"), systemImage: "circle.fill", tint: burnModel.statusColor)
                    if let mediaType = status.mediaType {
                        Chip(text: mediaType, systemImage: "opticaldisc")
                    }
                }
            }

            if let status = burnModel.mediaStatus {
                Grid(alignment: .leading, horizontalSpacing: Spacing.l, verticalSpacing: Spacing.xxs) {
                    GridRow(alignment: .firstTextBaseline) {
                        mediaLabel(L("类型"))
                        Text(status.mediaType ?? L("未知"))
                            .font(Typography.monoSmall)
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        mediaLabel(L("空白"))
                        Text(blankDescription(status))
                            .font(.system(size: 11.5))
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        mediaLabel(L("轨道/会话"))
                        Text(status.hasTracks ? "检测到已有轨道或会话" : "没有检测到轨道")
                            .font(.system(size: 11.5))
                            .foregroundStyle(status.hasTracks ? .orange : .secondary)
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        mediaLabel(L("来源类型"))
                        Text(burnModel.sourceKind.map { L($0.displayName) } ?? L("未选择可刻录的来源"))
                            .font(.system(size: 11.5))
                            .foregroundStyle(burnModel.sourceKind == nil ? .secondary : .primary)
                    }
                }

                DisclosureGroup(L("drutil status 原始输出")) {
                    Text(status.rawText.isEmpty ? "（无输出）" : status.rawText)
                        .font(Typography.monoSmall)
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(Spacing.s)
                        .insetSurface()
                        .padding(.top, Spacing.xs)
                }
                .font(Typography.caption)
                .foregroundStyle(.secondary)
            } else if burnModel.isLoadingStatus {
                Text("正在读取介质状态…")
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private func blankDescription(_ status: MediaStatus) -> String {
        if let indicator = status.blankIndicator {
            return indicator ? L("是（drutil 明确报告空白）") : L("否（drutil 明确报告非空白）")
        }
        return status.hasTracks ? L("否（检测到已有轨道）") : L("可能是空白盘（无明确标记、无轨道）")
    }

    private func mediaLabel(_ text: String) -> some View {
        Text(L(text))
            .font(Typography.caption)
            .foregroundStyle(.secondary)
            .frame(width: 64, alignment: .leading)
    }

    // MARK: - 来源

    private var sourceCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(
                L("来源文件"),
                systemImage: "doc",
                subtitle: L("支持 .wav / .aif（音频 CD）与 .iso / .dmg / .cue / .bin / .toc（数据盘）。")
            ) {
                if let latest = model.latestBurnableOutputURL,
                   latest.path != burnModel.sourceURL?.path {
                    Button("使用最近生成结果") {
                        burnModel.setSourcePath(latest.path)
                    }
                    .controlSize(.small)
                    .help(Formatting.abbreviatedPath(latest.path))
                }
            }

            HStack(spacing: Spacing.xs) {
                TextField("选择或输入要刻录的文件路径", text: sourceBinding)
                    .textFieldStyle(.roundedBorder)
                    .font(Typography.monoSmall)
                    .disabled(burnModel.isBurning)
                    .accessibilityLabel(L("要刻录的源文件"))
                Button("选择…") {
                    chooseSource()
                }
                .controlSize(.small)
                .disabled(burnModel.isBurning)
                if !burnModel.sourcePath.isEmpty {
                    Button {
                        burnModel.clearSource()
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.tertiary)
                    }
                    .buttonStyle(.borderless)
                    .controlSize(.small)
                    .disabled(burnModel.isBurning)
                    .help(L("清除"))
                }
            }

            if !burnModel.sourcePath.isEmpty {
                HStack(spacing: Spacing.xs) {
                    if let kind = burnModel.sourceKind {
                        Chip(text: L(kind.displayName), systemImage: "opticaldisc", tint: DesignSystem.accent(colorScheme))
                    } else {
                        Chip(text: L("不支持的文件类型"), systemImage: "xmark.circle.fill", tint: .orange)
                    }
                    if let detail = burnModel.sourceDetail {
                        Text(detail)
                            .font(Typography.caption2)
                            .foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 0)
                }
                PathStatusHint(path: burnModel.sourcePath, kind: .inputFile)
            }

            if let latest = model.latestBurnableOutputURL, burnModel.sourceURL == nil {
                Text(L("提示：最近生成的可刻录文件是 %@。", String(Formatting.abbreviatedPath(latest.path))))
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private var sourceBinding: Binding<String> {
        Binding(
            get: { burnModel.sourcePath },
            set: { burnModel.setSourcePath($0) }
        )
    }

    // MARK: - 操作

    private var actionCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(L("开始刻录"), systemImage: "play.circle")

            HStack(spacing: Spacing.m) {
                if burnModel.isBurning {
                    Button(role: .destructive) {
                        burnModel.cancel()
                    } label: {
                        Label(burnModel.wasCancelled ? "正在取消…" : "取消刻录", systemImage: "stop.fill")
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.red)
                    .keyboardShortcut(".", modifiers: .command)
                    .disabled(burnModel.wasCancelled)
                    .accessibilityIdentifier("cancel-burn")
                } else {
                    Button {
                        showsConfirmation = true
                    } label: {
                        Label("刻录到光盘", systemImage: "opticaldisc.fill")
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(DesignSystem.accent(colorScheme))
                    .disabled(!burnModel.canBurn)
                    .accessibilityIdentifier("burn-disc")
                    .help(burnModel.canBurn ? L("打开确认弹层后开始刻录") : (burnModel.blockReason ?? ""))

                    if burnModel.isStreamAvailable == false, burnModel.mode == .stream {
                        Text("流式刻录助手不可用")
                            .font(Typography.caption)
                            .foregroundStyle(.orange)
                    }
                }

                Spacer(minLength: 0)
            }

            if let reason = burnModel.blockReason, !burnModel.isBurning {
                Label(reason, systemImage: "info.circle.fill")
                    .font(Typography.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let error = burnModel.burnErrorMessage {
                Label(error, systemImage: "exclamationmark.octagon.fill")
                    .font(Typography.caption)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Text("刻录会不可逆地写入光盘。每次开始前都会要求确认盘片是空白且可报废的测试介质；蓝光仅实验性支持数据镜像（未实机验证）。")
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    // MARK: - 流式刻录

    private var streamCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(
                L("从当前工程生成并刻录"),
                systemImage: "waveform.path.ecg",
                subtitle: L("生成数据直接写入刻录机，不落完整镜像。")
            ) {
                Chip(text: L("实验性"), systemImage: "flask", tint: .orange)
            }

            if !burnModel.isStreamAvailable {
                Text(burnModel.streamUnavailableReason ?? L("流式刻录助手不可用。"))
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Text("流式模式需要 DiscRecording 辅助程序 disc-burn-stream。可以切回文件模式，先生成 .wav 或 .iso 再刻录。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
                Button("切回文件模式") { burnModel.mode = .file }
                    .controlSize(.small)
            } else if let reason = burnModel.streamUnsupportedReason {
                Label(reason, systemImage: "info.circle.fill")
                    .font(Typography.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
                Button("切回文件模式") { burnModel.mode = .file }
                    .controlSize(.small)
            } else if let project = burnModel.streamProject {
                Grid(alignment: .leading, horizontalSpacing: Spacing.l, verticalSpacing: Spacing.xs) {
                    GridRow(alignment: .firstTextBaseline) {
                        streamLabel(L("工程"))
                        Text(project.document.name)
                            .font(.system(size: 12))
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        streamLabel(L("盘型"))
                        Text(streamFamilyName)
                            .font(.system(size: 12))
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        streamLabel(L("流式类型"))
                        Text(burnModel.streamKind.map { L($0.displayName) } ?? "—")
                            .font(.system(size: 12))
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        streamLabel(L("扇区"))
                        Text(burnModel.streamSectors.map { "\($0)" } ?? "—")
                            .font(Typography.monoSmall)
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        streamLabel(L("预计数据"))
                        Text(streamExpectedBytes)
                            .font(Typography.monoSmall)
                            .foregroundStyle(.secondary)
                    }
                }

                HStack(spacing: Spacing.s) {
                    Text("刻录机")
                        .font(Typography.caption)
                        .foregroundStyle(.secondary)
                        .frame(width: 64, alignment: .leading)
                    Picker("刻录机", selection: streamDriveSelection) {
                        ForEach(burnModel.streamDrives) { drive in
                            Text(drive.displayName).tag(drive.index)
                        }
                    }
                    .labelsHidden()
                    .pickerStyle(.menu)
                    .frame(maxWidth: 420)
                    .disabled(burnModel.isStreamBurning)
                    Button {
                        Task { await burnModel.refreshStreamDrives() }
                    } label: {
                        Image(systemName: "arrow.clockwise")
                    }
                    .buttonStyle(.borderless)
                    .disabled(burnModel.isStreamBurning)
                    .help(L("刷新流式刻录机与介质"))
                }

                if !burnModel.streamDrives.isEmpty {
                    Text(burnModel.streamDriveSummary)
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                }

                HStack(spacing: Spacing.s) {
                    Text("写入速度")
                        .font(Typography.caption)
                        .foregroundStyle(.secondary)
                        .frame(width: 64, alignment: .leading)
                    Picker("写入速度", selection: streamSpeedSelection) {
                        Text("自动（驱动器最高）").tag(Double?.none)
                        Text("1×").tag(Double?.some(1.0))
                        Text("2×").tag(Double?.some(2.0))
                        Text("4×").tag(Double?.some(4.0))
                        Text("8×").tag(Double?.some(8.0))
                    }
                    .labelsHidden()
                    .pickerStyle(.segmented)
                    .frame(maxWidth: 260)
                    .disabled(burnModel.isStreamBurning)
                    Text("默认 2×；生成速度跟不上时会出现 underrun，可降到 1×。")
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                }

                if burnModel.isStreamBurning || !burnModel.streamLogLines.isEmpty {
                    VStack(alignment: .leading, spacing: 2) {
                        ForEach(Array(burnModel.streamLogLines.suffix(6))) { line in
                            Text(line.text)
                                .font(Typography.monoSmall)
                                .foregroundStyle(line.stream == .system ? .secondary : .primary)
                                .lineLimit(1)
                                .truncationMode(.middle)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(Spacing.s)
                    .insetSurface()
                }

                Toggle("写入后校验（重读整张盘，较慢）", isOn: $streamVerify)
                    .toggleStyle(.checkbox)
                    .font(.system(size: 12.5, weight: .medium))

                Toggle("完成后弹出光盘", isOn: $streamEject)
                    .toggleStyle(.checkbox)
                    .font(.system(size: 12.5, weight: .medium))

                Toggle("这是空白盘，且是可报废的测试介质", isOn: $streamAcknowledged)
                    .toggleStyle(.checkbox)
                    .font(.system(size: 12.5, weight: .medium))

                Text("流式写入会直接消耗盘片且不可撤销；首次使用请用可报废 CD-R / BD-R。CD/DVD 也可改用文件模式；BD 只支持流式模式。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)

                HStack(spacing: Spacing.m) {
                    if burnModel.isStreamBurning {
                        Button(role: .destructive) {
                            burnModel.cancelStreamingBurn()
                        } label: {
                            Label(burnModel.streamWasCancelled ? "正在取消…" : "取消刻录", systemImage: "stop.fill")
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(.red)
                        .disabled(burnModel.streamWasCancelled)
                    } else {
                        Button {
                            burnModel.streamEject = streamEject
                            burnModel.streamVerify = streamVerify
                            Task { await burnModel.startStreamingBurn() }
                        } label: {
                            Label("生成并刻录", systemImage: "play.circle.fill")
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(DesignSystem.accent(colorScheme))
                        .disabled(burnModel.streamBlockReason != nil || !streamAcknowledged)
                        .accessibilityIdentifier("stream-burn")
                    }
                    Spacer(minLength: 0)
                }

                if let reason = burnModel.streamBlockReason, !burnModel.isStreamBurning {
                    Label(reason, systemImage: "info.circle.fill")
                        .font(Typography.caption)
                        .foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let error = burnModel.streamErrorMessage, !burnModel.isStreamBurning {
                    Label(error, systemImage: "exclamationmark.octagon.fill")
                        .font(Typography.caption)
                        .foregroundStyle(.red)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            if burnModel.isStreamBurning || burnModel.streamResult != nil {
                Divider().opacity(0.4)
                streamProgressSection
                LogConsoleView(
                    lines: burnModel.streamLogLines,
                    isRunning: burnModel.isStreamBurning,
                    title: L("流式刻录日志"),
                    emptyHint: L("开始后，生成与写入的日志会逐行显示在这里。"),
                    onClear: { burnModel.clearStreamLog() }
                )
                streamResultSection
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private var streamProgressSection: some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack(spacing: Spacing.m) {
                if let progress = burnModel.streamProgress, burnModel.isStreamBurning {
                    ProgressView(value: min(max(progress, 0), 1))
                        .tint(DesignSystem.accent(colorScheme))
                } else if burnModel.isStreamBurning {
                    ProgressView()
                        .progressViewStyle(.linear)
                } else {
                    ProgressView(value: burnModel.streamResult?.succeeded == true ? 1 : 0)
                        .tint(burnModel.streamResult?.succeeded == true ? .green : .secondary)
                }
                Text(streamProgressText)
                    .font(Typography.monoSmall)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .frame(width: 58, alignment: .trailing)
            }
            Text(burnModel.streamStage ?? (burnModel.isStreamBurning ? "准备中…" : "未开始"))
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
            if let last = burnModel.streamLogLines.last {
                Text(last.text)
                    .font(Typography.monoSmall)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                    .truncationMode(.middle)
                    .textSelection(.enabled)
            }
        }
    }

    private var streamResultSection: some View {
        Group {
            if let result = burnModel.streamResult {
                VStack(alignment: .leading, spacing: Spacing.xs) {
                    HStack(spacing: Spacing.xs) {
                        Chip(
                            text: result.succeeded ? L("成功") : (burnModel.streamWasCancelled ? L("已取消") : L("失败")),
                            systemImage: result.succeeded ? "checkmark.circle.fill" : (burnModel.streamWasCancelled ? "stop.circle.fill" : "xmark.circle.fill"),
                            tint: result.succeeded ? .green : (burnModel.streamWasCancelled ? .orange : .red)
                        )
                        if let kind = burnModel.streamPlan?.kind {
                            Chip(text: L(kind.displayName), systemImage: "opticaldisc")
                        }
                    }
                    Text(result.succeeded
                         ? L("流式刻录完成，用时 %@；请等待光盘弹出。", String(Formatting.duration(result.duration)))
                         : L("流式刻录未完成（助手退出码 %@），请查看日志。", String(result.helperExitCode)))
                        .font(Typography.caption)
                        .foregroundStyle(result.succeeded ? .green : .red)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    private var streamFamilyName: String {
        switch burnModel.streamProject?.document.discFamily {
        case .cd: return "CD"
        case .dvd: return "DVD"
        case .bd: return L("蓝光")
        case nil: return "—"
        }
    }

    private var streamExpectedBytes: String {
        guard let sectors = burnModel.streamSectors, let kind = burnModel.streamKind else { return "—" }
        let bytes = Int64(sectors) * Int64(kind.blockSize)
        return ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    private var streamProgressText: String {
        if burnModel.isStreamBurning {
            return burnModel.streamProgress.map { "\(Int(($0 * 100).rounded()))%" } ?? L("进行中")
        }
        if let result = burnModel.streamResult {
            return result.succeeded ? "100%" : L("中断")
        }
        return "—"
    }

    private var streamSpeedSelection: Binding<Double?> {
        Binding(
            get: { burnModel.streamSpeed },
            set: { burnModel.streamSpeed = $0 }
        )
    }

    private var streamDriveSelection: Binding<Int> {
        Binding(
            get: { burnModel.selectedStreamDriveIndex ?? burnModel.streamDrives.first?.index ?? 0 },
            set: { burnModel.selectStreamDrive($0) }
        )
    }

    private func streamLabel(_ text: String) -> some View {
        Text(L(text))
            .font(Typography.caption)
            .foregroundStyle(.secondary)
            .frame(width: 64, alignment: .leading)
    }

    // MARK: - 进度与结果

    private var progressCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(L("刻录进度"), systemImage: "flame") {
                if let result = burnModel.lastResult {
                    Chip(
                        text: result.succeeded ? L("成功") : (burnModel.wasCancelled ? L("已取消") : L("失败")),
                        systemImage: result.succeeded ? "checkmark.circle.fill" : (burnModel.wasCancelled ? "stop.circle.fill" : "xmark.circle.fill"),
                        tint: result.succeeded ? .green : (burnModel.wasCancelled ? .orange : .red)
                    )
                }
            }

            HStack(spacing: Spacing.m) {
                if let progress = burnModel.progress, burnModel.isBurning {
                    ProgressView(value: min(max(progress, 0), 1))
                        .tint(DesignSystem.accent(colorScheme))
                } else if burnModel.isBurning {
                    ProgressView()
                        .progressViewStyle(.linear)
                } else {
                    ProgressView(value: burnModel.lastResult?.succeeded == true ? 1 : 0)
                        .tint(burnModel.lastResult?.succeeded == true ? .green : .secondary)
                }
                Text(progressText)
                    .font(Typography.monoSmall)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .frame(width: 58, alignment: .trailing)
            }

            if burnModel.isBurning {
                Text(burnModel.stage ?? L("正在调用 drutil…"))
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
            } else if let result = burnModel.lastResult {
                Text(burnModel.wasCancelled
                     ? L("已取消（退出码 %@）。", String(result.exitCode))
                     : (result.succeeded
                        ? L("刻录完成，用时 %@；光盘已弹出。", String(Formatting.duration(result.duration)))
                        : L("刻录失败，退出码 %@，用时 %@。", String(result.exitCode), String(Formatting.duration(result.duration)))))
                    .font(Typography.caption)
                    .foregroundStyle(result.succeeded ? .green : .red)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private var progressText: String {
        if burnModel.isBurning {
            return burnModel.progress.map { "\(Int(($0 * 100).rounded()))%" } ?? L("进行中")
        }
        if let result = burnModel.lastResult {
            return result.succeeded ? "100%" : L("中断")
        }
        return "—"
    }

    @ViewBuilder
    private var resultCard: some View {
        if let result = burnModel.lastResult {
            VStack(alignment: .leading, spacing: Spacing.m) {
                CardHeader(L("刻录结果"), systemImage: "checkmark.seal")
                Grid(alignment: .leading, horizontalSpacing: Spacing.l, verticalSpacing: Spacing.xs) {
                    GridRow(alignment: .firstTextBaseline) {
                        resultLabel(L("退出码"))
                        Text("\(result.exitCode)")
                            .font(Typography.monoSmall)
                            .foregroundStyle(result.succeeded ? .green : .red)
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        resultLabel(L("耗时"))
                        Text(Formatting.duration(result.duration))
                            .font(.system(size: 11.5))
                    }
                    if let kind = burnModel.plan?.kind ?? burnModel.sourceKind {
                        GridRow(alignment: .firstTextBaseline) {
                            resultLabel(L("刻录方式"))
                            Text(L(kind.displayName))
                                .font(.system(size: 11.5))
                        }
                    }
                    if let drive = burnModel.selectedDrive {
                        GridRow(alignment: .firstTextBaseline) {
                            resultLabel(L("刻录机"))
                            Text(drive.displayName)
                                .font(.system(size: 11.5))
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                Text(result.succeeded
                     ? L("请取出光盘并在其他设备上验证内容。")
                     : L("可展开上方「刻录日志」查看 drutil 的完整输出。"))
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .cardSurface(padding: Spacing.l)
        }
    }

    private func resultLabel(_ text: String) -> some View {
        Text(L(text))
            .font(Typography.caption)
            .foregroundStyle(.secondary)
            .frame(width: 64, alignment: .leading)
    }

    // MARK: - 选择文件

    private func chooseSource() {
        guard let url = FilePanels.chooseInputFile(
            title: L("选择要刻录的文件"),
            currentPath: burnModel.sourcePath
        ) else { return }
        burnModel.setSourcePath(url.path)
    }
}

// MARK: - 二次确认

/// 刻录前的强制确认：必须明确勾选「空白且可报废」，否则「开始刻录」保持禁用。
struct BurnConfirmationSheet: View {
    let burnModel: BurnModel
    let onConfirm: () -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var acknowledged = false

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.l) {
            HStack(spacing: Spacing.s) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 20))
                    .foregroundStyle(.orange)
                VStack(alignment: .leading, spacing: 2) {
                    Text("确认刻录")
                        .font(.system(size: 17, weight: .semibold))
                    Text("刻录不可撤销，请确认盘片状态。")
                        .font(Typography.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }

            VStack(alignment: .leading, spacing: Spacing.xs) {
                summaryRow(L("来源"), burnModel.sourceURL.map { Formatting.abbreviatedPath($0.path) } ?? L("未选择"))
                summaryRow(L("刻录方式"), burnModel.sourceKind.map { L($0.displayName) } ?? L("未知"))
                summaryRow(L("刻录机"), burnModel.selectedDrive?.displayName ?? L("未选择"))
                summaryRow(L("介质"), burnModel.mediaStatusSummary)
            }
            .padding(Spacing.m)
            .frame(maxWidth: .infinity, alignment: .leading)
            .insetSurface()

            Toggle("这是空白盘，且是可报废的测试介质", isOn: $acknowledged)
                .toggleStyle(.checkbox)
                .font(.system(size: 12.5, weight: .medium))

            Text("如果盘片里已有数据或不是空白盘，请立即取消并更换盘片。蓝光仅实验性支持数据镜像。")
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
                .fixedSize(horizontal: false, vertical: true)

            HStack(spacing: Spacing.s) {
                Spacer(minLength: 0)
                Button("取消") {
                    dismiss()
                }
                .keyboardShortcut(.cancelAction)

                Button("开始刻录") {
                    onConfirm()
                    dismiss()
                }
                .buttonStyle(.borderedProminent)
                .tint(.red)
                .disabled(!acknowledged)
                .keyboardShortcut(.defaultAction)
                .accessibilityIdentifier("burn-confirm")
            }
        }
        .padding(Spacing.xl)
        .frame(width: 520)
    }

    private func summaryRow(_ label: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
            Text(L(label))
                .font(Typography.caption)
                .foregroundStyle(.secondary)
                .frame(width: 64, alignment: .leading)
            Text(value)
                .font(Typography.caption)
                .textSelection(.enabled)
                .lineLimit(2)
                .truncationMode(.middle)
            Spacer(minLength: 0)
        }
    }
}
