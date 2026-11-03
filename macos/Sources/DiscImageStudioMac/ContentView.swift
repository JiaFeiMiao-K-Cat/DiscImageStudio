import SwiftUI
import DiscImageStudioKit

/// 应用主窗口：左侧工作流侧边栏 + 右侧主页/工作区/设置/关于。
struct ContentView: View {
    let app: AppModel

    var body: some View {
        HStack(spacing: 0) {
            SidebarView(app: app)
            detail
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .frame(minWidth: 1180, minHeight: 760)
        .background(DesignSystem.appBackground)
        .preferredColorScheme(app.preferredColorScheme)
        .task {
            await app.bootstrap()
        }
        .sheet(isPresented: Binding(
            get: { app.showsNewProjectSheet },
            set: { app.showsNewProjectSheet = $0 }
        )) {
            NewProjectSheet(app: app)
                .preferredColorScheme(app.preferredColorScheme)
        }
    }

    @ViewBuilder
    private var detail: some View {
        switch app.route {
        case .home:
            HomeView(app: app)
        case .onboarding:
            OnboardingView(
                model: app,
                errorMessage: app.cliErrorMessage ?? "未找到 disc-image-studio CLI。"
            )
            .background(DesignSystem.appBackground)
        case .settings:
            ScrollView {
                SettingsView(model: app)
                    .frame(maxWidth: 760, alignment: .leading)
                    .padding(.horizontal, 32)
                    .padding(.vertical, 24)
                    .frame(maxWidth: .infinity, alignment: .center)
            }
            .background(DesignSystem.appBackground)
        case .about:
            AboutView(model: app)
                .background(DesignSystem.appBackground)
        case .design, .preview, .export, .burn, .calibration:
            if let project = app.project {
                WorkspaceShell(app: app, project: project) {
                    workspaceContent(project: project)
                }
                .id(ObjectIdentifier(project))
            } else {
                HomeView(app: app)
            }
        }
    }

    @ViewBuilder
    private func workspaceContent(project: ProjectModel) -> some View {
        switch app.route {
        case .design:
            DesignView(app: app, project: project)
        case .preview:
            PreviewView(app: app, project: project)
        case .export:
            ExportView(app: app, project: project)
        case .burn:
            BurnView(model: app, burnModel: app.burnModel, showsHeader: false)
        case .calibration:
            CalibrationView(app: app, project: project)
        default:
            EmptyView()
        }
    }
}
