import AppKit
import SwiftUI
import DiscImageStudioKit

/// 设置页：CLI 路径、默认工程/输出位置、缓存、外观、预设与最近工程。
/// 所有改动即时保存并生效，无需重启。
struct SettingsView: View {
    let model: AppModel

    @State private var alertMessage: String?

    private var settings: AppSettings {
        model.settings
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Spacing.l) {
                header
                if let saveError = model.settingsSaveError {
                    errorBanner(saveError)
                }
                cliCard
                projectCard
                outputCard
                appearanceCard
                languageCard
                catalogCard
            }
            .frame(maxWidth: 700, alignment: .leading)
            .padding(Spacing.xxl)
            .frame(maxWidth: .infinity, alignment: .top)
        }
        .background(DesignSystem.appBackground)
        .frame(minWidth: 560, minHeight: 520)
        .alert("提示", isPresented: alertBinding) {
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

    private var header: some View {
        VStack(alignment: .leading, spacing: Spacing.xxs) {
            Text("设置")
                .font(.system(size: 24, weight: .bold))
            Text("改动会立即保存到 \(Formatting.abbreviatedPath(model.settingsFileURL.path))。")
                .font(Typography.caption)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
        }
    }

    private func errorBanner(_ message: String) -> some View {
        Label(message, systemImage: "exclamationmark.triangle.fill")
            .font(Typography.caption)
            .foregroundStyle(.red)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(Spacing.s)
            .background(Color.red.opacity(0.08), in: RoundedRectangle(cornerRadius: DesignSystem.controlRadius))
    }

    // MARK: - CLI

    private var cliCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("CLI", systemImage: "terminal", subtitle: "所有生成任务都通过 CLI 子进程执行；这里只决定使用哪一个。") {
                if model.isLoadingCatalog {
                    ProgressView()
                        .controlSize(.mini)
                }
            }

            statusBlock

            if let error = model.cliErrorMessage {
                Text(error)
                    .font(Typography.caption)
                    .foregroundStyle(.orange)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Divider()

            HStack(spacing: Spacing.s) {
                Button("选择可执行文件…") { chooseCLI() }
                    .controlSize(.small)
                Button("使用源码目录（dotnet run）…") { chooseSourceDirectory() }
                    .controlSize(.small)
            }

            HStack(spacing: Spacing.s) {
                Button("使用内置 CLI") {
                    Task { await model.useBundledCli() }
                }
                .controlSize(.small)
                .disabled(model.isLoadingCatalog)
                .help("清除自定义可执行文件路径，优先使用 App 内置 CLI；没有内置时继续按顺序查找。")

                Button("在 PATH 中查找") {
                    Task { await model.searchPathForCli() }
                }
                .controlSize(.small)
                .disabled(model.isLoadingCatalog)
                .help("清除自定义路径与源码目录，只在内置与 PATH 中查找。")

                Spacer(minLength: 0)

                Button {
                    Task { await model.refreshCatalog() }
                } label: {
                    Label("重新加载预设与 CLI", systemImage: "arrow.clockwise")
                }
                .controlSize(.small)
                .disabled(model.isLoadingCatalog)
            }

            if let customPath = settings.customCliPath {
                pathRow(label: "自定义 CLI", path: customPath) {
                    Task { await model.updateCustomCliPath(nil) }
                }
            }
            if let devPath = settings.devProjectPath {
                pathRow(label: "源码目录", path: devPath) {
                    Task { await model.updateDevProjectPath(nil) }
                }
            }
            if settings.customCliPath == nil && settings.devProjectPath == nil {
                Text("当前未指定自定义路径或源码目录，使用自动查找。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
            }

            Text("查找优先级：环境变量 DIS_IMAGE_STUDIO_CLI → 自定义路径 → App 内置 → PATH（/usr/local/bin、/opt/homebrew/bin、~/.local/bin、~/.dotnet/tools）→ 源码 dotnet run。")
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private var statusBlock: some View {
        HStack(alignment: .top, spacing: Spacing.s) {
            Circle()
                .fill(model.cliErrorMessage == nil && model.resolvedCli != nil ? Color.green : Color.orange)
                .frame(width: 8, height: 8)
                .padding(.top, 5)
            VStack(alignment: .leading, spacing: 2) {
                Text(statusHeadline)
                    .font(.system(size: 12.5, weight: .semibold))
                if let detail = statusDetail {
                    Text(detail)
                        .font(Typography.monoSmall)
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                        .lineLimit(2)
                        .truncationMode(.middle)
                }
                if let version = model.toolVersion {
                    Text("CLI 版本 \(version)")
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                }
            }
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var statusHeadline: String {
        if model.isLoadingCatalog { return "正在检测 CLI…" }
        guard let cli = model.resolvedCli else { return "未找到可用的 CLI" }
        return "已找到 CLI · \(cli.label)"
    }

    private var statusDetail: String? {
        guard let cli = model.resolvedCli else { return nil }
        return cli.detail
    }

    private func pathRow(label: String, path: String, onClear: @escaping () -> Void) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
            Text(label)
                .font(Typography.caption)
                .foregroundStyle(.secondary)
                .frame(width: 72, alignment: .leading)
            Text(Formatting.abbreviatedPath(path))
                .font(Typography.monoSmall)
                .textSelection(.enabled)
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer(minLength: 0)
            Button(action: onClear) {
                Image(systemName: "xmark.circle.fill")
                    .foregroundStyle(.tertiary)
            }
            .buttonStyle(.borderless)
            .controlSize(.small)
            .help("清除")
        }
    }

    // MARK: - 工程与缓存

    private var projectCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(
                "工程与缓存",
                systemImage: "folder.badge.gearshape",
                subtitle: "新建工程默认保存到这里；预览/合成缓存位于 ~/Library/Caches/DiscImageStudioMac。"
            )

            HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
                Text("默认工程位置")
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
                    .frame(width: 92, alignment: .leading)
                Text(Formatting.abbreviatedPath(model.defaultProjectsDirectory.path))
                    .font(Typography.monoSmall)
                    .textSelection(.enabled)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 0)
                Button("选择…") {
                    if let url = FilePanels.chooseDirectory(
                        title: "选择默认工程位置",
                        currentPath: model.defaultProjectsDirectory.path
                    ) {
                        model.setDefaultProjectsDirectory(url)
                    }
                }
                .controlSize(.small)
                Button("重置") {
                    model.setDefaultProjectsDirectory(nil)
                }
                .controlSize(.small)
            }

            HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
                Text("缓存")
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
                    .frame(width: 92, alignment: .leading)
                Text(model.cacheSizeText())
                    .font(.system(size: 11.5))
                Text("含设计合成、预览与导出临时图")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                Spacer(minLength: 0)
                Button("打开缓存目录") {
                    try? FileManager.default.createDirectory(
                        at: model.cacheDirectory,
                        withIntermediateDirectories: true
                    )
                    NSWorkspace.shared.open(model.cacheDirectory)
                }
                .controlSize(.small)
                Button("清空缓存") {
                    model.clearCache()
                }
                .controlSize(.small)
            }

            HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
                Text("最近工程")
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
                    .frame(width: 92, alignment: .leading)
                Text("\(model.recents.count) 个")
                    .font(.system(size: 11.5))
                Spacer(minLength: 0)
                Button("清空最近工程") {
                    model.clearRecents()
                }
                .controlSize(.small)
                .disabled(model.recents.isEmpty)
            }

            if let error = model.cacheActionError {
                Text(error)
                    .font(Typography.caption2)
                    .foregroundStyle(.red)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    // MARK: - 默认输出目录

    private var outputCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(
                "默认输出目录",
                systemImage: "folder",
                subtitle: "为输出文件面板提供默认目录；不会自动创建目录。"
            )
            HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
                if let directory = settings.defaultOutputDirectory {
                    Text(Formatting.abbreviatedPath(directory))
                        .font(Typography.monoSmall)
                        .textSelection(.enabled)
                        .lineLimit(1)
                        .truncationMode(.middle)
                } else {
                    Text("未设置（使用系统保存面板的默认位置）")
                        .font(Typography.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
                Button("选择…") { chooseOutputDirectory() }
                    .controlSize(.small)
                if settings.defaultOutputDirectory != nil {
                    Button("清除") {
                        model.updateDefaultOutputDirectory(nil)
                    }
                    .controlSize(.small)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    // MARK: - 外观

    private var appearanceCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("外观", systemImage: "circle.lefthalf.filled", subtitle: "立即应用到主窗口与设置窗口。")
            HStack(spacing: Spacing.m) {
                Text("外观")
                    .font(.system(size: 12.5))
                Picker("外观", selection: appearanceBinding) {
                    ForEach(AppearanceMode.allCases, id: \.self) { mode in
                        Text(mode.displayName).tag(mode)
                    }
                }
                .labelsHidden()
                .pickerStyle(.segmented)
                .frame(maxWidth: 320)
                Spacer(minLength: 0)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private var appearanceBinding: Binding<AppearanceMode> {
        Binding(
            get: { model.settings.appearance },
            set: { model.updateAppearance($0) }
        )
    }

    // MARK: - 选项

    private var optionsCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("表单", systemImage: "slider.horizontal.3")
            Toggle("显示高级参数", isOn: advancedBinding)
                .toggleStyle(.switch)
                .font(.system(size: 12.5))
            Text("关闭后，表单会隐藏「高级参数」分组，只保留输入输出、盘片几何与影调。")
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private var advancedBinding: Binding<Bool> {
        Binding(
            get: { model.settings.showsAdvancedOptions },
            set: { model.updateShowsAdvancedOptions($0) }
        )
    }

    // MARK: - 语言

    private var languageCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("语言", systemImage: "globe", subtitle: "界面文案当前仅为简体中文。")
            HStack {
                Text("界面语言")
                    .font(.system(size: 12))
                Spacer()
                Text("简体中文")
                    .font(.system(size: 11.5))
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    // MARK: - 命令目录

    private var catalogCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("盘片预设", systemImage: "square.stack.3d.up", subtitle: "通过 presets --json 读取；CLI 更新后可手动重新加载。")

            Grid(alignment: .leading, horizontalSpacing: Spacing.l, verticalSpacing: Spacing.xs) {
                GridRow(alignment: .firstTextBaseline) {
                    catalogLabel("CLI 来源")
                    Text(model.resolvedCli?.label ?? "未知")
                        .font(.system(size: 11.5))
                }
                GridRow(alignment: .firstTextBaseline) {
                    catalogLabel("预设状态")
                    Text(model.usingFallbackPresets ? "内置兜底参数" : "已从 CLI 加载")
                        .font(.system(size: 11.5))
                        .foregroundStyle(model.usingFallbackPresets ? .orange : .primary)
                }
                GridRow(alignment: .firstTextBaseline) {
                    catalogLabel("内容")
                    Text(catalogSummary)
                        .font(.system(size: 11.5))
                }
                GridRow(alignment: .firstTextBaseline) {
                    catalogLabel("设置文件")
                    Text(Formatting.abbreviatedPath(model.settingsFileURL.path))
                        .font(Typography.monoSmall)
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
            }

            HStack(spacing: Spacing.s) {
                Button {
                    Task { await model.refreshCatalog() }
                } label: {
                    Label("重新加载预设与 CLI", systemImage: "arrow.clockwise")
                }
                .controlSize(.small)
                .disabled(model.isLoadingCatalog)

                Button {
                    model.revealSettingsFile()
                } label: {
                    Label("打开设置文件所在目录", systemImage: "folder")
                }
                .controlSize(.small)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private var catalogSummary: String {
        if let presets = model.presets {
            return "\(presets.presets(for: .cd).count) 个 CD · \(presets.presets(for: .dvd).count) 个 DVD · \(presets.presets(for: .bd).count) 个 BD"
        }
        return "使用内置兜底参数"
    }

    private func catalogLabel(_ text: String) -> some View {
        Text(text)
            .font(Typography.caption)
            .foregroundStyle(.secondary)
            .frame(width: 88, alignment: .leading)
    }

    // MARK: - 动作

    private func chooseCLI() {
        guard let url = FilePanels.chooseExecutableFile(title: "选择 disc-image-studio 可执行文件") else {
            return
        }
        guard FileManager.default.isExecutableFile(atPath: url.path) else {
            alertMessage = "所选文件不可执行。请选择 disc-image-studio 发布包里的可执行文件，或先执行 chmod +x 添加执行权限。"
            return
        }
        Task { await model.updateCustomCliPath(url.path) }
    }

    private func chooseSourceDirectory() {
        guard let url = FilePanels.chooseDirectory(
            title: "选择仓库根目录或 src/DiscImageStudio.Cli 目录",
            currentPath: settings.devProjectPath
        ) else {
            return
        }
        let candidates = [
            url.appendingPathComponent("src/DiscImageStudio.Cli/DiscImageStudio.Cli.csproj"),
            url.appendingPathComponent("DiscImageStudio.Cli.csproj"),
        ]
        guard candidates.contains(where: { FileManager.default.fileExists(atPath: $0.path) }) else {
            alertMessage = "这个目录里没有找到 DiscImageStudio.Cli.csproj。请选择仓库根目录，或 src/DiscImageStudio.Cli 目录。"
            return
        }
        Task { await model.updateDevProjectPath(url.path) }
    }

    private func chooseOutputDirectory() {
        guard let url = FilePanels.chooseDirectory(
            title: "选择默认输出目录",
            currentPath: settings.defaultOutputDirectory
        ) else {
            return
        }
        model.updateDefaultOutputDirectory(url.path)
    }
}
