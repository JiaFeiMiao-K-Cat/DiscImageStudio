import AppKit
import Foundation
import Observation
import SwiftUI
import UniformTypeIdentifiers
import DiscImageStudioKit

/// 应用级状态：设置、CLI/预设、最近工程、当前工程与导航路由。
@MainActor
@Observable
final class AppModel {
    enum Route: Hashable {
        case home
        case onboarding
        case design
        case preview
        case export
        case burn
        case calibration
        case settings
        case about
    }

    static let stageRoutes: [Route] = [.design, .preview, .export, .burn, .calibration]

    var route: Route = .home
    var settings: AppSettings
    var resolvedCli: ResolvedCli?
    var cliErrorMessage: String?
    var presets: PresetCatalog?
    var presetsErrorMessage: String?
    var isLoadingCatalog = false
    var isLoadingPresets = false
    var recents: [RecentProject] = []
    var project: ProjectModel?
    var projectError: String?
    var settingsSaveError: String?
    var cacheActionError: String?
    var showsNewProjectSheet = false
    var newProjectFamily: DiscFamily = .cd
    var newProjectPresetID: String?

    let burnModel = BurnModel()

    @ObservationIgnored private let settingsStore: AppSettingsStore
    @ObservationIgnored private let recentsStore: RecentProjectsStore
    @ObservationIgnored private var didBootstrap = false

    init(settingsStore: AppSettingsStore = AppSettingsStore()) {
        self.settingsStore = settingsStore
        self.recentsStore = RecentProjectsStore()
        self.settings = settingsStore.load()
        self.recents = recentsStore.load()
    }

    // MARK: - 派生

    var preferredColorScheme: ColorScheme? {
        switch settings.appearance {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }

    var isCLIReady: Bool { resolvedCli != nil }

    var appVersionText: String {
        if let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String,
           !version.isEmpty, !version.hasPrefix("__") {
            return version
        }
        return L("开发构建")
    }

    /// 兼容旧设置/关于页展示；工作流层没有读取 CLI 版本。
    var toolVersion: String? { nil }

    var settingsFileURL: URL { settingsStore.fileURL }

    var cacheDirectory: URL { ProjectModel.cacheDirectory }

    var defaultProjectsDirectory: URL {
        let raw = UserDefaults.standard.string(forKey: "DiscImageStudioMac.defaultProjectsDirectory")
        if let raw, let url = Formatting.expandedURL(for: raw) {
            return url
        }
        return ProjectStore.defaultProjectsDirectory
    }

    var latestBurnableOutputURL: URL? {
        project?.latestArtifactURL
    }

    var stageIndex: Int? {
        Self.stageRoutes.firstIndex(of: route)
    }

    func presetsFor(_ family: DiscFamily) -> [DiscPreset] {
        if let loaded = presets?.presets(for: family), !loaded.isEmpty {
            return loaded
        }
        return Self.fallbackPresets.filter { $0.family == family }
    }

    var usingFallbackPresets: Bool { presets == nil }

    var allPresets: [DiscPreset] {
        DiscFamily.allCases.flatMap { presetsFor($0) }
    }

    func preset(id: String) -> DiscPreset? {
        allPresets.first { $0.id == id }
    }

    // MARK: - 生命周期

    func bootstrap() async {
        guard !didBootstrap else { return }
        didBootstrap = true
        recents = recentsStore.load()
        await refreshEnvironment()
    }

    /// 兼容旧视图（Onboarding/Settings）的命名。
    func refreshCatalog() async {
        await refreshEnvironment()
    }

    func refreshEnvironment() async {
        guard !isLoadingCatalog else { return }
        isLoadingCatalog = true
        isLoadingPresets = true
        defer {
            isLoadingCatalog = false
            isLoadingPresets = false
        }

        let resolver = CliResolver(settings: settings)
        let candidates = resolver.candidates
        guard !candidates.isEmpty else {
            resolvedCli = nil
            presets = nil
            cliErrorMessage = CliResolverError.notFound(searched: []).localizedDescription
            presetsErrorMessage = cliErrorMessage
            project?.updateCLI(nil)
            burnModel.updateCLI(nil)
            return
        }

        var loadedCatalog: PresetCatalog?
        var lastPresetError: Error?
        var resolvedCandidate: ResolvedCli = candidates[0]
        for candidate in candidates {
            do {
                let loaded = try await PresetLoader(cliInvocation: candidate.invocation).load()
                loadedCatalog = loaded
                resolvedCandidate = candidate
                break
            } catch {
                lastPresetError = error
            }
        }

        resolvedCli = resolvedCandidate
        cliErrorMessage = nil
        presets = loadedCatalog
        if loadedCatalog != nil {
            presetsErrorMessage = nil
        } else {
            let message = (lastPresetError as? LocalizedError)?.errorDescription
                ?? lastPresetError?.localizedDescription
                ?? L("无法读取盘片预设。")
            presetsErrorMessage = message
        }
        project?.updateCLI(resolvedCandidate.invocation)
        burnModel.updateCLI(resolvedCandidate)
    }

    func refreshPresets() async {
        await refreshEnvironment()
    }

    // MARK: - 工程

    func beginNewProject(family: DiscFamily? = nil, preset: DiscPreset? = nil) {
        newProjectFamily = family ?? .cd
        newProjectPresetID = preset?.id ?? presetsFor(family ?? .cd).first?.id
        showsNewProjectSheet = true
    }

    @discardableResult
    func createProject(
        name: String,
        family: DiscFamily,
        preset: DiscPreset,
        projectURL: URL,
        outputPath: String
    ) -> Bool {
        do {
            var document = ProjectDocument.make(
                name: name,
                family: family,
                preset: preset,
                outputPath: outputPath
            )
            document.tone = ProjectTone()
            try ProjectStore().save(document, to: projectURL)
            openProjectModel(document, url: projectURL)
            recordRecent(document, url: projectURL)
            route = .design
            return true
        } catch {
            projectError = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            return false
        }
    }

    @discardableResult
    func openProject(url: URL) -> Bool {
        do {
            let document = try ProjectStore().load(from: url)
            openProjectModel(document, url: url)
            recordRecent(document, url: url)
            route = .design
            projectError = nil
            return true
        } catch {
            projectError = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            return false
        }
    }

    func openProjectPanel() {
        let panel = NSOpenPanel()
        panel.title = L("打开工程")
        panel.prompt = L("打开")
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [UTType(filenameExtension: ProjectStore.fileExtension) ?? .data]
        panel.directoryURL = defaultProjectsDirectory
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        _ = openProject(url: url)
    }

    private func openProjectModel(_ document: ProjectDocument, url: URL) {
        project?.saveNow()
        let model = ProjectModel(
            document: document,
            fileURL: url,
            cliInvocation: resolvedCli?.invocation,
            defaultOutputDirectory: settings.defaultOutputDirectory
        ) { [weak self] document, url in
            self?.recordRecent(document, url: url)
        }
        project = model
        projectError = nil
    }

    func closeProject() {
        project?.saveNow()
        project = nil
        route = .home
    }

    func goHome() {
        project?.saveNow()
        route = .home
    }

    func continueProject() {
        guard project != nil else { return }
        route = .design
    }

    func showSettings() {
        route = .settings
    }

    func showAbout() {
        route = .about
    }

    func showOnboarding() {
        route = .onboarding
    }

    func selectStage(_ route: Route) {
        guard project != nil, Self.stageRoutes.contains(route) else { return }
        self.route = route
    }

    func nextStage() {
        guard project != nil, let index = stageIndex, index + 1 < Self.stageRoutes.count else { return }
        route = Self.stageRoutes[index + 1]
    }

    // MARK: - 最近工程

    private func recordRecent(_ document: ProjectDocument, url: URL) {
        recents = (try? recentsStore.record(document, at: url)) ?? recents
    }

    func removeRecent(_ recent: RecentProject) {
        try? recentsStore.remove(path: recent.path)
        recents = recentsStore.load()
    }

    func clearRecents() {
        try? recentsStore.removeAll()
        recents = []
    }

    // MARK: - 刻录

    func openBurn(sourceURL: URL? = nil) {
        guard let project else {
            projectError = L("请先打开或新建工程，再从导出结果开始刻录。")
            return
        }
        project.burnPrefillURL = sourceURL ?? project.latestArtifactURL
        route = .burn
    }

    // MARK: - 设置

    @discardableResult
    func saveSettings() -> Bool {
        settings = settings.normalized()
        do {
            try settingsStore.save(settings)
            settingsSaveError = nil
            return true
        } catch {
            settingsSaveError = L("无法保存设置：%@", String(error.localizedDescription))
            return false
        }
    }

    func updateAppearance(_ mode: AppearanceMode) {
        settings.appearance = mode
        _ = saveSettings()
    }

    func updateShowsAdvancedOptions(_ shows: Bool) {
        settings.showsAdvancedOptions = shows
        _ = saveSettings()
    }

    func updateDefaultOutputDirectory(_ path: String?) {
        settings.defaultOutputDirectory = path
        _ = saveSettings()
    }

    func setDefaultProjectsDirectory(_ url: URL?) {
        if let url {
            UserDefaults.standard.set(url.path, forKey: "DiscImageStudioMac.defaultProjectsDirectory")
        } else {
            UserDefaults.standard.removeObject(forKey: "DiscImageStudioMac.defaultProjectsDirectory")
        }
    }

    func updateCustomCliPath(_ path: String?) async {
        settings.customCliPath = path
        _ = saveSettings()
        await refreshEnvironment()
    }

    func updateDevProjectPath(_ path: String?) async {
        settings.devProjectPath = path
        _ = saveSettings()
        await refreshEnvironment()
    }

    func clearCliOverrides() async {
        settings.customCliPath = nil
        settings.devProjectPath = nil
        _ = saveSettings()
        await refreshEnvironment()
    }

    func useBundledCli() async {
        settings.customCliPath = nil
        _ = saveSettings()
        await refreshEnvironment()
    }

    func searchPathForCli() async {
        settings.customCliPath = nil
        settings.devProjectPath = nil
        _ = saveSettings()
        await refreshEnvironment()
    }

    func revealSettingsFile() {
        let directory = settingsStore.fileURL.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        NSWorkspace.shared.activateFileViewerSelecting([settingsStore.fileURL])
    }

    func openCLIDirectory() {
        guard let cli = resolvedCli else {
            showSettings()
            return
        }
        if cli.source == .dotnetProject {
            NSWorkspace.shared.open(URL(fileURLWithPath: cli.detail).deletingLastPathComponent())
        } else {
            NSWorkspace.shared.open(cli.invocation.executableURL.deletingLastPathComponent())
        }
    }

    // MARK: - 缓存

    func cacheSizeText() -> String {
        let size = Self.directorySize(ProjectModel.cacheDirectory)
        return ByteCountFormatter.string(fromByteCount: size, countStyle: .file)
    }

    func clearCache() {
        let fileManager = FileManager.default
        let directory = ProjectModel.cacheDirectory
        do {
            let contents = try fileManager.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: nil
            )
            for url in contents {
                try? fileManager.removeItem(at: url)
            }
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
            cacheActionError = nil
            project?.handleCacheCleared()
        } catch {
            cacheActionError = L("清空缓存失败：%@", String(error.localizedDescription))
        }
    }

    private static func directorySize(_ directory: URL) -> Int64 {
        guard let enumerator = FileManager.default.enumerator(
            at: directory,
            includingPropertiesForKeys: [.fileSizeKey],
            options: [.skipsHiddenFiles]
        ) else { return 0 }
        var total: Int64 = 0
        for case let url as URL in enumerator {
            if let values = try? url.resourceValues(forKeys: [.fileSizeKey]),
               let size = values.fileSize {
                total += Int64(size)
            }
        }
        return total
    }

    // MARK: - 兜底预设（CLI 的 presets --json 不可用时仍可新建工程）

    static let fallbackPresets: [DiscPreset] = [
        DiscPreset(
            id: "fallback-cd-80",
            displayName: L("标准 80min CD（兜底）"),
            description: L("CLI 预设暂不可用时的内置 CD 参数（24.5–56.8 mm，359,849 扇区）。"),
            family: .cd,
            geometry: ProjectGeometry(innerRadiusMm: 24.5, outerRadiusMm: 56.8, sectors: 359_849)
        ),
        DiscPreset(
            id: "fallback-dvd-5",
            displayName: L("标准 12cm 单层 DVD（兜底）"),
            description: L("CLI 预设暂不可用时的内置 DVD 参数（24–58 mm，2,295,104 扇区）。"),
            family: .dvd,
            geometry: ProjectGeometry(innerRadiusMm: 24, outerRadiusMm: 58, sectors: 2_295_104)
        ),
        DiscPreset(
            id: "fallback-bd-25",
            displayName: L("标准 25GB 单层 BD（兜底）"),
            description: L("CLI 预设暂不可用时的内置 BD 参数（24–58 mm，2,218,392 扇区）。"),
            family: .bd,
            geometry: ProjectGeometry(innerRadiusMm: 24, outerRadiusMm: 58, sectors: 12_219_392)
        ),
    ]
}
