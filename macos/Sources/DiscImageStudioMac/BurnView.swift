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
                    streamUnavailableCard
                }

                if burnModel.isBurning || burnModel.lastResult != nil {
                    progressCard
                    LogConsoleView(
                        lines: burnModel.logLines,
                        isRunning: burnModel.isBurning,
                        title: "刻录日志",
                        emptyHint: "刻录开始后，drutil 的日志会逐行显示在这里。",
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
        burnModel.prefillIfNeeded(model.project?.burnPrefillURL ?? model.latestBurnableOutputURL)
        if burnModel.drives.isEmpty || burnModel.mediaStatus == nil {
            await burnModel.refreshDrives()
        } else {
            await burnModel.refreshMediaStatus()
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
                Chip(text: "仅写入空白盘", systemImage: "checkmark.shield", tint: .green)
                Chip(text: "不可撤销", systemImage: "exclamationmark.triangle", tint: .orange)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: - 模式

    private var modeCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("刻录方式", systemImage: "slider.horizontal.3", subtitle: "文件模式稳定可用；流式模式为实验性预留。")

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
        .disabled(burnModel.isBurning)
        .accessibilityIdentifier(identifier)
        .accessibilityAddTraits(selected ? [.isSelected] : [])
    }

    private var modeDescription: String {
        switch burnModel.mode {
        case .file:
            return "调用 /usr/bin/drutil：音频 CD 会先把 WAV 放进临时目录再用 burn -audio 写入；数据盘直接 burn 镜像文件。"
        case .stream:
            return "流式模式通过 DiscRecording 的 DRTrack producer 把 CLI 输出直接写入刻录机，不落完整镜像。"
        }
    }

    // MARK: - 刻录机

    private var driveCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("光盘刻录机", systemImage: "externaldrive") {
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
                            driveLabel("索引")
                            Text("drutil -drive \(drive.index)")
                                .font(Typography.monoSmall)
                                .foregroundStyle(.secondary)
                        }
                        GridRow(alignment: .firstTextBaseline) {
                            driveLabel("支持级别")
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
        Text(text)
            .font(Typography.caption)
            .foregroundStyle(.secondary)
            .frame(width: 64, alignment: .leading)
    }

    // MARK: - 介质

    private var mediaCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("介质状态", systemImage: "opticaldisc") {
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
                    Chip(text: status.isProbablyBlank ? "空白" : "非空白", systemImage: "circle.fill", tint: burnModel.statusColor)
                    if let mediaType = status.mediaType {
                        Chip(text: mediaType, systemImage: "opticaldisc")
                    }
                }
            }

            if let status = burnModel.mediaStatus {
                Grid(alignment: .leading, horizontalSpacing: Spacing.l, verticalSpacing: Spacing.xxs) {
                    GridRow(alignment: .firstTextBaseline) {
                        mediaLabel("类型")
                        Text(status.mediaType ?? "未知")
                            .font(Typography.monoSmall)
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        mediaLabel("空白")
                        Text(blankDescription(status))
                            .font(.system(size: 11.5))
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        mediaLabel("轨道/会话")
                        Text(status.hasTracks ? "检测到已有轨道或会话" : "没有检测到轨道")
                            .font(.system(size: 11.5))
                            .foregroundStyle(status.hasTracks ? .orange : .secondary)
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        mediaLabel("来源类型")
                        Text(burnModel.sourceKind?.displayName ?? "未选择可刻录的来源")
                            .font(.system(size: 11.5))
                            .foregroundStyle(burnModel.sourceKind == nil ? .secondary : .primary)
                    }
                }

                DisclosureGroup("drutil status 原始输出") {
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
            return indicator ? "是（drutil 明确报告空白）" : "否（drutil 明确报告非空白）"
        }
        return status.hasTracks ? "否（检测到已有轨道）" : "可能是空白盘（无明确标记、无轨道）"
    }

    private func mediaLabel(_ text: String) -> some View {
        Text(text)
            .font(Typography.caption)
            .foregroundStyle(.secondary)
            .frame(width: 64, alignment: .leading)
    }

    // MARK: - 来源

    private var sourceCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(
                "来源文件",
                systemImage: "doc",
                subtitle: "支持 .wav / .aif（音频 CD）与 .iso / .dmg / .cue / .bin / .toc（数据盘）。"
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
                    .accessibilityLabel("要刻录的源文件")
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
                    .help("清除")
                }
            }

            if !burnModel.sourcePath.isEmpty {
                HStack(spacing: Spacing.xs) {
                    if let kind = burnModel.sourceKind {
                        Chip(text: kind.displayName, systemImage: "opticaldisc", tint: DesignSystem.accent(colorScheme))
                    } else {
                        Chip(text: "不支持的文件类型", systemImage: "xmark.circle.fill", tint: .orange)
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
                Text("提示：最近生成的可刻录文件是 \(Formatting.abbreviatedPath(latest.path))。")
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
            CardHeader("开始刻录", systemImage: "play.circle")

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
                    .help(burnModel.canBurn ? "打开确认弹层后开始刻录" : (burnModel.blockReason ?? ""))

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

    // MARK: - 流式模式占位

    private var streamUnavailableCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            if burnModel.isStreamAvailable {
                CardHeader("流式刻录助手已就绪", systemImage: "waveform.path.ecg") {
                    Chip(text: "实验性", systemImage: "flask", tint: .orange)
                }
                Text("已找到 disc-burn-stream（DiscRecording 的 DRTrack producer + DRBurn）。")
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Text("完整的流式刻录需要把生成命令（例如 cd-generate --output -）的参数接进管道；这部分尚未串联。请先使用文件模式，或等待后续版本。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                CardHeader("流式刻录助手不可用", systemImage: "waveform.path.badge.minus") {
                    Chip(text: "实验性", systemImage: "flask", tint: .orange)
                }
                Text(burnModel.streamUnavailableReason ?? "流式刻录助手不可用。")
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Text("流式模式需要 DiscRecording 辅助程序 disc-burn-stream。文件模式已经可用：先生成 .wav 或 .iso，再切回「文件模式」用系统的 drutil 刻录。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Button("切回文件模式") {
                burnModel.mode = .file
            }
            .controlSize(.small)
            .buttonStyle(.borderedProminent)
            .tint(DesignSystem.accent(colorScheme))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    // MARK: - 进度与结果

    private var progressCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("刻录进度", systemImage: "flame") {
                if let result = burnModel.lastResult {
                    Chip(
                        text: result.succeeded ? "成功" : (burnModel.wasCancelled ? "已取消" : "失败"),
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
                Text(burnModel.stage ?? "正在调用 drutil…")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
            } else if let result = burnModel.lastResult {
                Text(burnModel.wasCancelled
                     ? "已取消（退出码 \(result.exitCode)）。"
                     : (result.succeeded
                        ? "刻录完成，用时 \(Formatting.duration(result.duration))；光盘已弹出。"
                        : "刻录失败，退出码 \(result.exitCode)，用时 \(Formatting.duration(result.duration))。"))
                    .font(Typography.caption)
                    .foregroundStyle(result.succeeded ? .green : .red)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private var progressText: String {
        if burnModel.isBurning {
            return burnModel.progress.map { "\(Int(($0 * 100).rounded()))%" } ?? "进行中"
        }
        if let result = burnModel.lastResult {
            return result.succeeded ? "100%" : "中断"
        }
        return "—"
    }

    @ViewBuilder
    private var resultCard: some View {
        if let result = burnModel.lastResult {
            VStack(alignment: .leading, spacing: Spacing.m) {
                CardHeader("刻录结果", systemImage: "checkmark.seal")
                Grid(alignment: .leading, horizontalSpacing: Spacing.l, verticalSpacing: Spacing.xs) {
                    GridRow(alignment: .firstTextBaseline) {
                        resultLabel("退出码")
                        Text("\(result.exitCode)")
                            .font(Typography.monoSmall)
                            .foregroundStyle(result.succeeded ? .green : .red)
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        resultLabel("耗时")
                        Text(Formatting.duration(result.duration))
                            .font(.system(size: 11.5))
                    }
                    if let kind = burnModel.plan?.kind ?? burnModel.sourceKind {
                        GridRow(alignment: .firstTextBaseline) {
                            resultLabel("刻录方式")
                            Text(kind.displayName)
                                .font(.system(size: 11.5))
                        }
                    }
                    if let drive = burnModel.selectedDrive {
                        GridRow(alignment: .firstTextBaseline) {
                            resultLabel("刻录机")
                            Text(drive.displayName)
                                .font(.system(size: 11.5))
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                Text(result.succeeded
                     ? "请取出光盘并在其他设备上验证内容。"
                     : "可展开上方「刻录日志」查看 drutil 的完整输出。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .cardSurface(padding: Spacing.l)
        }
    }

    private func resultLabel(_ text: String) -> some View {
        Text(text)
            .font(Typography.caption)
            .foregroundStyle(.secondary)
            .frame(width: 64, alignment: .leading)
    }

    // MARK: - 选择文件

    private func chooseSource() {
        guard let url = FilePanels.chooseInputFile(
            title: "选择要刻录的文件",
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
                summaryRow("来源", burnModel.sourceURL.map { Formatting.abbreviatedPath($0.path) } ?? "未选择")
                summaryRow("刻录方式", burnModel.sourceKind?.displayName ?? "未知")
                summaryRow("刻录机", burnModel.selectedDrive?.displayName ?? "未选择")
                summaryRow("介质", burnModel.mediaStatusSummary)
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
            Text(label)
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
