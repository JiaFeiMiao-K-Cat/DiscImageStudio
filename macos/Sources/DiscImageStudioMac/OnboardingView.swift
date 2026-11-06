import AppKit
import SwiftUI
import DiscImageStudioKit

/// 未找到 CLI 时的引导页：三条获取途径 + 手动选择可执行文件。
struct OnboardingView: View {
    let model: AppModel
    let errorMessage: String

    @State private var isRefreshing = false
    @State private var alertMessage: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Spacing.xl) {
                hero
                errorCard
                ways
                actions
            }
            .frame(maxWidth: 820, alignment: .leading)
            .padding(Spacing.xxl)
            .frame(maxWidth: .infinity, alignment: .center)
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .alert(L("提示"), isPresented: alertBinding) {
            Button("好", role: .cancel) {}
        } message: {
            Text(alertMessage ?? "")
        }
    }

    private var alertBinding: Binding<Bool> {
        Binding(
            get: { alertMessage != nil },
            set: { if !$0 { alertMessage = nil } }
        )
    }

    // MARK: - 头图

    private var hero: some View {
        HStack(alignment: .center, spacing: Spacing.xl) {
            DiscMark(size: 88)
            VStack(alignment: .leading, spacing: Spacing.s) {
                Text("还差一个 disc-image-studio CLI")
                    .font(.system(size: 24, weight: .bold))
                Text("图形界面本身不包含光盘编码逻辑，所有任务都交给 CLI 执行。准备好 CLI 后，界面会自动读取命令目录并生成表单。")
                    .font(.system(size: 12.5))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: - 错误信息

    private var errorCard: some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack(spacing: Spacing.xs) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange)
                Text("未找到可用的 CLI")
                    .font(.system(size: 12.5, weight: .semibold))
                Spacer(minLength: 0)
                if model.isLoadingCatalog {
                    ProgressView()
                        .controlSize(.mini)
                }
            }
            Text(errorMessage)
                .font(.system(size: 11.5))
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(Spacing.m)
        .background(Color.orange.opacity(0.07), in: RoundedRectangle(cornerRadius: DesignSystem.controlRadius, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: DesignSystem.controlRadius, style: .continuous)
                .strokeBorder(Color.orange.opacity(0.25), lineWidth: 1)
        }
    }

    // MARK: - 三条途径

    private var ways: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(L("三种获取方式"), systemImage: "arrow.down.circle")

            wayCard(
                number: 1,
                title: L("使用发布包"),
                detail: L("从 GitHub Releases 下载 macOS 版 CLI，把可执行文件 disc-image-studio 放进应用的 Contents/Resources/cli/ 目录；用源码直接运行时请用下面两种方式。"),
                actionTitle: nil,
                action: nil
            )
            wayCard(
                number: 2,
                title: L("指定可执行文件"),
                detail: L("如果 CLI 已经解压到任意位置（或已经通过 dotnet tool 安装），直接选中那个可执行文件。路径会写入设置并立即生效。"),
                actionTitle: L("选择 CLI 可执行文件…"),
                action: chooseCLI,
                primary: true
            )
            wayCard(
                number: 3,
                title: L("使用源码目录（dotnet run）"),
                detail: L("选择仓库根目录或 src/DiscImageStudio.Cli 目录；界面会用 dotnet run -c Release 启动 CLI。需要本机安装 .NET 10 SDK。"),
                actionTitle: L("选择源码目录…"),
                action: chooseSourceDirectory
            )

            Text("自动查找顺序：环境变量 DIS_IMAGE_STUDIO_CLI → 设置里的自定义路径 → App 内置 → PATH（含 ~/.local/bin、~/.dotnet/tools、/usr/local/bin、/opt/homebrew/bin）→ 源码 dotnet run。")
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private func wayCard(
        number: Int,
        title: String,
        detail: String,
        actionTitle: String?,
        action: (() -> Void)?,
        primary: Bool = false
    ) -> some View {
        HStack(alignment: .top, spacing: Spacing.m) {
            Text("\(number)")
                .font(.system(size: 12, weight: .bold, design: .rounded))
                .foregroundStyle(.white)
                .frame(width: 22, height: 22)
                .background(DesignSystem.brand, in: Circle())
            VStack(alignment: .leading, spacing: Spacing.xs) {
                Text(L(title))
                    .font(.system(size: 13, weight: .semibold))
                Text(detail)
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if let actionTitle, let action {
                    wayActionButton(actionTitle, action: action, primary: primary)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    @ViewBuilder
    private func wayActionButton(_ title: String, action: @escaping () -> Void, primary: Bool) -> some View {
        if primary {
            Button(title, action: action)
                .controlSize(.small)
                .buttonStyle(.borderedProminent)
                .accessibilityIdentifier("choose-cli")
        } else {
            Button(title, action: action)
                .controlSize(.small)
                .buttonStyle(.bordered)
        }
    }

    // MARK: - 底部操作

    private var actions: some View {
        HStack(spacing: Spacing.m) {
            Button {
                refresh()
            } label: {
                Label(isRefreshing ? "正在重新检测…" : "重新检测", systemImage: "arrow.clockwise")
            }
            .disabled(isRefreshing || model.isLoadingCatalog)

            Button("打开设置…") {
                model.showSettings()
            }

            Spacer(minLength: 0)

            Text("也可以使用环境变量 DIS_IMAGE_STUDIO_CLI 指定路径后重启应用。")
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
        }
    }

    private func refresh() {
        isRefreshing = true
        Task {
            await model.refreshCatalog()
            isRefreshing = false
        }
    }

    // MARK: - 选择文件

    private func chooseCLI() {
        guard let url = FilePanels.chooseExecutableFile(title: L("选择 disc-image-studio 可执行文件")) else {
            return
        }
        guard FileManager.default.isExecutableFile(atPath: url.path) else {
            alertMessage = L("所选文件不可执行。请选择 disc-image-studio 发布包里的可执行文件，或先执行 chmod +x 添加执行权限。")
            return
        }
        Task {
            await model.updateCustomCliPath(url.path)
        }
    }

    private func chooseSourceDirectory() {
        guard let url = FilePanels.chooseDirectory(
            title: L("选择仓库根目录或 src/DiscImageStudio.Cli 目录"),
            currentPath: model.settings.devProjectPath
        ) else {
            return
        }
        let fileManager = FileManager.default
        let candidates = [
            url.appendingPathComponent("src/DiscImageStudio.Cli/DiscImageStudio.Cli.csproj"),
            url.appendingPathComponent("DiscImageStudio.Cli.csproj"),
        ]
        guard candidates.contains(where: { fileManager.fileExists(atPath: $0.path) }) else {
            alertMessage = L("这个目录里没有找到 DiscImageStudio.Cli.csproj。请选择仓库根目录，或 src/DiscImageStudio.Cli 目录。")
            return
        }
        Task {
            await model.updateDevProjectPath(url.path)
        }
    }
}
