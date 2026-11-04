import AppKit
import SwiftUI
import DiscImageStudioKit

/// 导出页：源图 → 输出文件（按盘型/随盘文件切换）→ 生成 → 进度/结果。
struct ExportView: View {
    let app: AppModel
    let project: ProjectModel

    @State private var showsGeometryEditor = false

    private var isCdDataMode: Bool { project.family == .cd && project.document.outputs.hasBundledFiles }
    private var expectedExtension: String {
        switch project.family {
        case .cd: return project.document.outputs.hasBundledFiles ? "iso" : "wav"
        case .dvd, .bd: return "iso"
        }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                sourceCard
                outputCard
                if project.task.kind == .export, let result = project.task.lastResult {
                    ResultView(
                        title: L("导出结果"),
                        result: result,
                        outputURL: project.artifactDisplayURL,
                        intendedOutputURL: project.exportOutputURL,
                        summary: lastStdoutLine,
                        stderrLines: project.task.stderrLines,
                        wasCancelled: project.task.wasCancelled,
                        cliLabel: app.resolvedCli?.label,
                        burnableOutputURL: project.artifactDisplayURL,
                        onReveal: { url in
                            NSWorkspace.shared.activateFileViewerSelecting([url])
                        },
                        onBurn: { url in
                            app.openBurn(sourceURL: url)
                        }
                    )
                }
            }
            .frame(maxWidth: 980, alignment: .leading)
            .padding(.horizontal, 24)
            .padding(.vertical, 18)
            .frame(maxWidth: .infinity, alignment: .center)
        }
        .background(DesignSystem.appBackground)
        .sheet(isPresented: $showsGeometryEditor) {
            GeometryEditSheet(project: project)
        }
    }

    private var lastStdoutLine: String? {
        project.task.lines.reversed().first { line in
            line.stream == .stdout && !line.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }?.text
    }

    // MARK: - 源图

    private var sourceCard: some View {
        SectionCard(L("源图片"), systemImage: "photo") {
            HStack(spacing: 10) {
                Image(systemName: "square.stack.3d.down.right")
                    .font(.system(size: 14))
                    .foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 2) {
                    Text(project.sourceSummary)
                        .font(.system(size: 12))
                    Text(project.geometrySummary)
                        .font(Typography.monoSmall)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                }
                Spacer(minLength: 0)
                Button("工程设置…") { showsGeometryEditor = true }
                    .controlSize(.small)
            }
            if project.layers.isEmpty {
                Button("前往设计导入图片") {
                    app.selectStage(.design)
                }
                .controlSize(.small)
            }
        } trailing: {
            EmptyView()
        }
    }

    // MARK: - 输出

    private var outputCard: some View {
        SectionCard(L("输出文件"), systemImage: "folder") {
            VStack(alignment: .leading, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    FieldLabel(outputLabel)
                    HStack(spacing: 6) {
                        TextField("输出路径", text: outputPathBinding)
                            .textFieldStyle(.roundedBorder)
                            .font(Typography.monoSmall)
                        Button("选择…") { chooseOutput() }
                            .controlSize(.small)
                    }
                    if let hint = outputHint {
                        Text(hint)
                            .font(Typography.caption2)
                            .foregroundStyle(.orange)
                    }
                }

                if project.family == .cd && !isCdDataMode {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("CD-DA 编码")
                            .font(Typography.caption)
                            .foregroundStyle(.secondary)
                        Toggle("应用 CD-DA 延迟交织", isOn: Binding(
                            get: { project.document.outputs.interleave },
                            set: { project.setInterleave($0) }
                        ))
                        .toggleStyle(.switch)
                        .font(.system(size: 11.5))
                        if project.document.outputs.outputPath.lowercased().hasSuffix(".wav") {
                            Toggle("同时导出 CUE 文件", isOn: Binding(
                                get: { project.document.outputs.writeCue },
                                set: { project.setWriteCue($0) }
                            ))
                            .toggleStyle(.switch)
                            .font(.system(size: 11.5))
                        }
                    }
                }

                if project.family != .cd || isCdDataMode {
                    VStack(alignment: .leading, spacing: 4) {
                        FieldLabel(L("卷标"))
                        TextField("卷标", text: Binding(
                            get: { project.document.outputs.volumeLabel },
                            set: { project.setVolumeLabel($0) }
                        ))
                        .textFieldStyle(.roundedBorder)
                        .font(Typography.monoSmall)
                        .frame(maxWidth: 280, alignment: .leading)
                    }
                }

                VStack(alignment: .leading, spacing: 8) {
                    FieldLabel(L("随盘文件（可选）"))
                    CompactPathField(
                        label: L("文件夹"),
                        path: project.document.outputs.dataDirectory,
                        placeholder: L("未选择（整盘绘制）"),
                        onBrowse: {
                            if let url = FilePanels.chooseInputFolder(
                                title: L("选择随盘文件目录"),
                                currentPath: project.document.outputs.dataDirectory
                            ) {
                                project.setDataDirectory(url.path)
                            }
                        },
                        onClear: { project.setDataDirectory("") }
                    )
                    if project.document.outputs.hasBundledFiles {
                        SegmentedControl(
                            items: [
                                .init(id: false, title: L("内圈"), systemImage: "circle.dotted"),
                                .init(id: true, title: L("外圈"), systemImage: "circle"),
                            ],
                            selection: Binding(
                                get: { project.document.outputs.filesAtOuterRing },
                                set: { project.setFilesAtOuterRing($0) }
                            )
                        )
                        .frame(maxWidth: 280, alignment: .leading)
                    }
                    Text(modeHint)
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                HStack(spacing: 10) {
                    Button {
                        project.generate()
                    } label: {
                        Label(generateTitle, systemImage: "gearshape.2")
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(DesignSystem.brand)
                    .disabled(!project.canExport)
                    .accessibilityIdentifier("generate-output")

                    if project.isExporting {
                        ProgressView()
                            .controlSize(.small)
                        Button("取消") { project.cancelTask() }
                            .controlSize(.small)
                            .tint(.red)
                    } else {
                        Text("写入文件不会改动光盘。")
                            .font(Typography.caption2)
                            .foregroundStyle(.tertiary)
                    }
                    Spacer(minLength: 0)
                }

                if let error = project.exportError {
                    Text(error)
                        .font(Typography.caption)
                        .foregroundStyle(.red)
                        .fixedSize(horizontal: false, vertical: true)
                        .textSelection(.enabled)
                }
            }
        } trailing: {
            EmptyView()
        }
    }

    private var outputLabel: String {
        switch project.family {
        case .cd: return isCdDataMode ? L("输出 ISO") : L("输出音轨（WAV / RAW）")
        case .dvd, .bd: return L("输出 ISO")
        }
    }

    private var outputHint: String? {
        let path = project.document.outputs.outputPath
        if path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return L("请选择输出位置。")
        }
        let ext = (path as NSString).pathExtension.lowercased()
        if !ext.isEmpty && ext != expectedExtension {
            return L("当前盘型/模式预期扩展名为 .%@，当前是 .%@。", String(expectedExtension), String(ext))
        }
        return nil
    }

    private var modeHint: String {
        switch project.family {
        case .cd:
            return project.document.outputs.hasBundledFiles
                ? L("已选择随盘文件：生成 Mode 1 数据 ISO，文件写入内/外圈；输出扩展名应为 .iso。")
                : L("未选择随盘文件：生成 CD-DA 音轨（WAV/RAW）；输出扩展名应为 .wav 或 .raw。")
        case .dvd:
            return L("生成 DVD ISO；未选择随盘文件时填充整盘，选择后把文件写入内/外圈。")
        case .bd:
            return L("生成 BD-R ISO；未选择随盘文件时整盘绘制，选择后写入 UDF 文件系统。")
        }
    }

    private var generateTitle: String {
        switch project.family {
        case .cd: return isCdDataMode ? L("生成数据 ISO") : L("生成音轨")
        case .dvd: return L("生成 DVD 镜像")
        case .bd: return L("生成蓝光镜像")
        }
    }

    private var outputPathBinding: Binding<String> {
        Binding(
            get: { project.document.outputs.outputPath },
            set: { project.setOutputPath($0) }
        )
    }

    private func chooseOutput() {
        let current = project.document.outputs.outputPath
        let name = project.document.name
        let suggested = current.isEmpty
            ? project.defaultOutputDirectory.map { "\($0)/\(name).\(expectedExtension)" }
            : current
        guard let url = FilePanels.chooseOutputFile(
            title: L("选择输出文件"),
            currentPath: suggested,
            defaultDirectory: project.defaultOutputDirectory,
            allowedExtensions: [expectedExtension]
        ) else { return }
        project.setOutputPath(url.path)
    }
}
