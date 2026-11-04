import AppKit
import SwiftUI
import DiscImageStudioKit

/// 工程工作区外壳：页眉（标题/阶段条/下一步）+ 内容 + 底部任务坞。
struct WorkspaceShell<Content: View>: View {
    let app: AppModel
    let project: ProjectModel
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider().overlay(DesignSystem.hairline.opacity(0.45))
            content()
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            Divider().overlay(DesignSystem.hairline.opacity(0.45))
            TaskDock(project: project)
        }
        .background(DesignSystem.appBackground)
    }

    // MARK: - 页眉

    private var header: some View {
        HStack(alignment: .center, spacing: Spacing.l) {
            HStack(spacing: Spacing.s) {
                Text(pageTitle)
                    .font(.system(size: 22, weight: .bold))
                Chip(text: project.family.familyLabel, systemImage: "opticaldisc", tint: DesignSystem.brand)
                if project.isComposing {
                    ProgressView()
                        .controlSize(.mini)
                        .help(L("正在合成设计稿"))
                }
            }

            Spacer(minLength: Spacing.l)

            stageBar

            Spacer(minLength: Spacing.l)

            nextButton
                .frame(minWidth: 132, alignment: .trailing)
        }
        .padding(.horizontal, Spacing.xl)
        .padding(.vertical, 14)
        .background(DesignSystem.surface1)
    }

    private var pageTitle: String {
        switch app.route {
        case .design: return L("设计")
        case .preview: return L("预览")
        case .export: return L("导出")
        case .burn: return L("刻录")
        case .calibration: return L("标定")
        default: return L("工作区")
        }
    }

    private var stageBar: some View {
        HStack(spacing: 6) {
            ForEach(Array(AppModel.stageRoutes.enumerated()), id: \.element) { index, route in
                stageItem(index: index + 1, route: route)
                if index < AppModel.stageRoutes.count - 1 {
                    Rectangle()
                        .fill(DesignSystem.hairline.opacity(0.5))
                        .frame(width: 16, height: 1)
                }
            }
        }
    }

    private func stageItem(index: Int, route: AppModel.Route) -> some View {
        let selected = app.route == route
        let completed = (app.stageIndex ?? 0) > (AppModel.stageRoutes.firstIndex(of: route) ?? 0)
        return Button {
            app.selectStage(route)
        } label: {
            HStack(spacing: 5) {
                Text("\(index)")
                    .font(.system(size: 9.5, weight: .bold, design: .rounded))
                    .foregroundStyle(selected || completed ? Color.white : Color.secondary)
                    .frame(width: 16, height: 16)
                    .background(
                        selected ? DesignSystem.brand : (completed ? DesignSystem.brand.opacity(0.55) : DesignSystem.surface3),
                        in: Circle()
                    )
                Text(stageTitle(route))
                    .font(.system(size: 11, weight: selected ? .semibold : .regular))
                    .foregroundStyle(selected ? Color.primary : Color.secondary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private func stageTitle(_ route: AppModel.Route) -> String {
        switch route {
        case .design: return L("设计")
        case .preview: return L("预览")
        case .export: return L("导出")
        case .burn: return L("刻录")
        case .calibration: return L("标定")
        default: return ""
        }
    }

    @ViewBuilder
    private var nextButton: some View {
        if let next = nextRoute, let nextTitle = nextLabel {
            Button {
                app.selectStage(next)
            } label: {
                Label(L("下一步：%@", String(nextTitle)), systemImage: "arrow.right")
                    .font(.system(size: 11.5, weight: .medium))
            }
            .controlSize(.small)
            .buttonStyle(.borderedProminent)
            .tint(DesignSystem.brand)
        } else {
            Text(" ")
                .font(.caption)
        }
    }

    private var nextRoute: AppModel.Route? {
        guard let index = app.stageIndex, index + 1 < AppModel.stageRoutes.count else { return nil }
        return AppModel.stageRoutes[index + 1]
    }

    private var nextLabel: String? {
        guard let next = nextRoute else { return nil }
        switch next {
        case .design: return L("设计")
        case .preview: return L("预览")
        case .export: return L("导出")
        case .burn: return L("刻录")
        case .calibration: return L("标定")
        default: return nil
        }
    }
}

/// 底部任务坞：任务状态/进度/取消/日志。
struct TaskDock: View {
    let project: ProjectModel
    @State private var showsLog = false

    private var task: WorkspaceTask { project.task }

    var body: some View {
        HStack(spacing: Spacing.m) {
            statusIcon
            VStack(alignment: .leading, spacing: 2) {
                Text(task.statusText)
                    .font(.system(size: 11.5, weight: .semibold))
                    .lineLimit(1)
                Text(detailText)
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            if task.isRunning {
                if let progress = task.progress {
                    ProgressView(value: min(max(progress, 0), 1))
                        .tint(DesignSystem.brand)
                        .frame(width: 180)
                    Text("\(Int((progress * 100).rounded()))%")
                        .font(Typography.monoSmall)
                        .foregroundStyle(.secondary)
                        .frame(width: 40, alignment: .trailing)
                } else {
                    ProgressView()
                        .progressViewStyle(.linear)
                        .frame(width: 180)
                }
            }

            Spacer(minLength: Spacing.m)

            if task.isRunning {
                Button("取消") {
                    project.cancelTask()
                }
                .controlSize(.small)
                .tint(.red)
                .disabled(task.wasCancelled)
                .accessibilityIdentifier("cancel-task")
            } else if let url = project.artifactDisplayURL {
                Button {
                    NSWorkspace.shared.activateFileViewerSelecting([url])
                } label: {
                    Label("在 Finder 中显示", systemImage: "folder")
                }
                .controlSize(.small)
            }

            Button {
                showsLog = true
            } label: {
                Label("日志", systemImage: "terminal")
            }
            .controlSize(.small)
            .disabled(task.lines.isEmpty)
        }
        .padding(.horizontal, Spacing.l)
        .padding(.vertical, 9)
        .background(DesignSystem.surface1)
        .sheet(isPresented: $showsLog) {
            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Text(task.title)
                        .font(.headline)
                    Spacer()
                    Button("关闭") { showsLog = false }
                        .keyboardShortcut(.cancelAction)
                }
                .padding(16)
                Divider()
                LogConsoleView(
                    lines: task.lines,
                    isRunning: task.isRunning,
                    title: L("任务日志"),
                    emptyHint: L("运行预览或生成后，CLI 日志会显示在这里。"),
                    onClear: { task.clearLog() }
                )
                .padding(16)
            }
            .frame(width: 760, height: 520)
        }
    }

    private var statusIcon: some View {
        Group {
            if task.isRunning {
                ProgressView()
                    .controlSize(.small)
            } else if let result = task.lastResult {
                Image(systemName: result.succeeded ? "checkmark.circle.fill" : "xmark.circle.fill")
                    .foregroundStyle(result.succeeded ? .green : .red)
            } else {
                Image(systemName: "circle.dashed")
                    .foregroundStyle(.tertiary)
            }
        }
        .frame(width: 18)
    }

    private var detailText: String {
        if task.isRunning {
            if let stage = task.stage, !stage.isEmpty { return stage }
            return L("%@进行中…（切换页面不会中断）", String(task.title))
        }
        if let result = task.lastResult {
            return L("退出码 %@ · 用时 %@", String(result.exitCode), String(Formatting.duration(result.duration)))
        }
        return L("运行预览或生成后，这里会显示进度与结果。")
    }
}
