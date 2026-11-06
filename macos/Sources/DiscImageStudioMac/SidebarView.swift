import SwiftUI
import DiscImageStudioKit

/// 工作流侧边栏：无工程时为「主页」；打开工程后为 设计/预览/导出/刻录/标定。
struct SidebarView: View {
    let app: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header

            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    if app.project != nil {
                        stageRows
                        Divider()
                            .overlay(DesignSystem.hairline.opacity(0.4))
                            .padding(.horizontal, 12)
                            .padding(.vertical, 8)
                        homeRow
                    } else {
                        homeRow
                    }
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 10)
            }

            Spacer(minLength: 0)

            Divider().overlay(DesignSystem.hairline.opacity(0.4))
            VStack(spacing: 2) {
                navRow(.settings, title: L("设置"), systemImage: "gearshape", shortcut: "⌘,")
                navRow(.about, title: L("关于"), systemImage: "info.circle")
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 10)

            if app.cliErrorMessage != nil {
                HStack(spacing: 6) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                    Text("CLI 未就绪")
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                    Spacer(minLength: 0)
                    Button("配置") { app.showOnboarding() }
                        .buttonStyle(.link)
                        .font(.caption2)
                }
                .padding(.horizontal, 14)
                .padding(.bottom, 10)
            }
        }
        .frame(width: 264)
        .frame(maxHeight: .infinity)
        .background(DesignSystem.surface1)
        .overlay(alignment: .trailing) {
            Rectangle()
                .fill(DesignSystem.hairline.opacity(0.45))
                .frame(width: 1)
        }
    }

    // MARK: - 头部

    private var header: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            if let project = app.project {
                HStack(spacing: 8) {
                    Button {
                        app.goHome()
                    } label: {
                        Image(systemName: "chevron.left")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(.secondary)
                            .frame(width: 24, height: 24)
                    }
                    .buttonStyle(.plain)
                    .help(L("返回主页（工程保持打开）"))

                    VStack(alignment: .leading, spacing: 2) {
                        Text(project.document.name)
                            .font(.system(size: 13, weight: .semibold))
                            .lineLimit(1)
                            .truncationMode(.tail)
                        Text(project.family.familyLabel)
                            .font(Typography.badge)
                            .foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 0)
                    saveStateView(project)
                }
            } else {
                HStack(spacing: 8) {
                    DiscMark(size: 26)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Disc Image Studio")
                            .font(.system(size: 13, weight: .semibold))
                        Text("光盘盘面工作台")
                            .font(Typography.caption2)
                            .foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 0)
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 18)
        .padding(.bottom, 14)
    }

    @ViewBuilder
    private func saveStateView(_ project: ProjectModel) -> some View {
        switch project.saveState {
        case .saved:
            Image(systemName: "checkmark.circle")
                .font(.system(size: 11))
                .foregroundStyle(.tertiary)
                .help(L("已保存"))
        case .unsaved:
            Image(systemName: "circle.fill")
                .font(.system(size: 6))
                .foregroundStyle(.orange)
                .help(L("有未保存的修改"))
        case .saving:
            ProgressView()
                .controlSize(.mini)
                .help(L("正在保存"))
        case .failed:
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 11))
                .foregroundStyle(.red)
                .help(project.saveError ?? L("保存失败"))
        }
    }

    // MARK: - 阶段导航

    private var stageRows: some View {
        ForEach(Array(AppModel.stageRoutes.enumerated()), id: \.element) { index, route in
            stageRow(index: index + 1, route: route)
        }
    }

    private func stageRow(index: Int, route: AppModel.Route) -> some View {
        let selected = app.route == route
        return Button {
            app.selectStage(route)
        } label: {
            HStack(spacing: 10) {
                Image(systemName: icon(for: route))
                    .font(.system(size: 13, weight: .medium))
                    .frame(width: 18)
                Text(title(for: route))
                    .font(.system(size: 12.5, weight: selected ? .semibold : .regular))
                Spacer(minLength: 0)
                Text("\(index)")
                    .font(.system(size: 9.5, weight: .semibold, design: .rounded))
                    .foregroundStyle(selected ? Color.white.opacity(0.8) : Color.secondary.opacity(0.6))
            }
            .foregroundStyle(selected ? Color.white : Color.primary.opacity(0.85))
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .background(selected ? DesignSystem.brand : Color.clear, in: RoundedRectangle(cornerRadius: 7))
            .contentShape(RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private var homeRow: some View {
        let selected = app.route == .home
        return Button {
            app.goHome()
        } label: {
            HStack(spacing: 10) {
                Image(systemName: "house")
                    .font(.system(size: 13, weight: .medium))
                    .frame(width: 18)
                Text("主页")
                    .font(.system(size: 12.5, weight: selected ? .semibold : .regular))
                Spacer(minLength: 0)
            }
            .foregroundStyle(selected ? Color.white : Color.primary.opacity(0.85))
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .background(selected ? DesignSystem.brand : Color.clear, in: RoundedRectangle(cornerRadius: 7))
            .contentShape(RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private func navRow(
        _ route: AppModel.Route,
        title: String,
        systemImage: String,
        shortcut: String? = nil
    ) -> some View {
        let selected = app.route == route
        return Button {
            if route == .settings { app.showSettings() } else { app.showAbout() }
        } label: {
            HStack(spacing: 10) {
                Image(systemName: systemImage)
                    .font(.system(size: 13, weight: .medium))
                    .frame(width: 18)
                Text(L(title))
                    .font(.system(size: 12.5, weight: selected ? .semibold : .regular))
                Spacer(minLength: 0)
                if let shortcut {
                    Text(shortcut)
                        .font(.system(size: 10))
                        .foregroundStyle(.tertiary)
                }
            }
            .foregroundStyle(selected ? Color.white : Color.primary.opacity(0.85))
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(selected ? DesignSystem.brand : Color.clear, in: RoundedRectangle(cornerRadius: 7))
            .contentShape(RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
    }

    private func title(for route: AppModel.Route) -> String {
        switch route {
        case .design: return L("设计")
        case .preview: return L("预览")
        case .export: return L("导出")
        case .burn: return L("刻录")
        case .calibration: return L("标定")
        default: return ""
        }
    }

    private func icon(for route: AppModel.Route) -> String {
        switch route {
        case .design: return "pencil.and.outline"
        case .preview: return "eye"
        case .export: return "square.and.arrow.up"
        case .burn: return "opticaldisc"
        case .calibration: return "scope"
        default: return "circle"
        }
    }
}
