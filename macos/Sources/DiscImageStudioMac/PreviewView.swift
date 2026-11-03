import AppKit
import SwiftUI
import DiscImageStudioKit

/// 预览页：设计投影 / 成品读回，左侧参数 inspector + 右侧 PNG 监视器。
struct PreviewView: View {
    let app: AppModel
    let project: ProjectModel

    @State private var showsGeometryEditor = false
    @State private var showsAdvanced = false

    var body: some View {
        HStack(alignment: .top, spacing: 16) {
            inspector
                .frame(width: 320)
            PreviewMonitor(project: project, onExportPreview: exportPreview)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 16)
        .background(DesignSystem.appBackground)
        .sheet(isPresented: $showsGeometryEditor) {
            GeometryEditSheet(project: project)
        }
    }

    // MARK: - Inspector

    private var inspector: some View {
        ScrollView {
            VStack(spacing: 12) {
                modeCard
                parametersCard
                refreshCard
            }
        }
        .scrollIndicators(.never)
    }

    private var modeCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            SegmentedControl(
                items: PreviewMode.allCases.map {
                    .init(
                        id: $0,
                        title: $0.displayName,
                        systemImage: $0 == .designProjection ? "sparkles" : "arrow.uturn.backward"
                    )
                },
                selection: Binding(
                    get: { project.previewMode },
                    set: { project.setPreviewMode($0) }
                )
            )
            Text(project.previewMode == .designProjection
                 ? "把设计页的合成图按生成几何快速投影，适合调整图片与影调。"
                 : "读取已生成镜像/音轨中实际写入的数据，适合最终核对。")
                .font(Typography.caption2)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: 12)
    }

    private var parametersCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeader(title: "预览参数", systemImage: "slider.horizontal.3") {
                Button("编辑…") { showsGeometryEditor = true }
                    .controlSize(.small)
            }

            VStack(alignment: .leading, spacing: 4) {
                FieldLabel("源图")
                Text(project.sourceSummary)
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if let url = project.designCompositeURL {
                    Text(Formatting.abbreviatedPath(url.path))
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
            }

            VStack(alignment: .leading, spacing: 4) {
                FieldLabel("生成几何")
                Text(project.geometrySummary)
                    .font(Typography.monoSmall)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if project.previewMode == .artifactReadback {
                VStack(alignment: .leading, spacing: 4) {
                    FieldLabel("成品文件")
                    HStack(spacing: 6) {
                        Text(project.artifactDisplayURL.map { Formatting.abbreviatedPath($0.path) } ?? "尚未选择成品")
                            .font(Typography.caption2)
                            .foregroundStyle(project.artifactDisplayURL == nil ? .tertiary : .secondary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                        Spacer(minLength: 0)
                        Button("选择…") { chooseArtifact() }
                            .controlSize(.small)
                    }
                }
            }

            VStack(alignment: .leading, spacing: 4) {
                FieldLabel("预览尺寸（px）")
                HStack(spacing: 8) {
                    TextField(
                        "",
                        value: Binding(
                            get: { project.previewSize },
                            set: { project.setPreviewSize($0) }
                        ),
                        format: .number
                    )
                    .textFieldStyle(.roundedBorder)
                    .font(Typography.monoSmall)
                    .frame(width: 92)
                    Text("上限 8192")
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                    Spacer(minLength: 0)
                }
            }

            DisclosureGroup(isExpanded: $showsAdvanced) {
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 8) {
                        measuredField("实测内半径", binding: Binding(
                            get: { project.actualInnerRadiusMm },
                            set: { project.setMeasuredInner($0) }
                        ))
                        measuredField("实测外半径", binding: Binding(
                            get: { project.actualOuterRadiusMm },
                            set: { project.setMeasuredOuter($0) }
                        ))
                    }

                    if project.family == .dvd {
                        HStack(spacing: 8) {
                            measuredField("线性项", binding: Binding(
                                get: { project.actualPitchLinear },
                                set: { project.setMeasuredPitch(linear: $0) }
                            ))
                            measuredField("二次项", binding: Binding(
                                get: { project.actualPitchQuadratic },
                                set: { project.setMeasuredPitch(quadratic: $0) }
                            ))
                            measuredField("三次项", binding: Binding(
                                get: { project.actualPitchCubic },
                                set: { project.setMeasuredPitch(cubic: $0) }
                            ))
                        }
                    }

                    if project.family == .bd {
                        Toggle("BD 极性（3T/6T 反转）", isOn: Binding(
                            get: { project.invertMapping },
                            set: { project.setInvertMapping($0) }
                        ))
                        .toggleStyle(.switch)
                        .font(.system(size: 11.5))
                    }

                    HStack(spacing: 8) {
                        VStack(alignment: .leading, spacing: 4) {
                            FieldLabel("每扇区采样")
                            TextField(
                                "",
                                value: Binding(
                                    get: { project.samplesPerSector },
                                    set: { project.samplesPerSector = min(max($0, 1), 16) }
                                ),
                                format: .number
                            )
                            .textFieldStyle(.roundedBorder)
                            .font(Typography.monoSmall)
                        }
                        if project.previewMode == .artifactReadback {
                            VStack(alignment: .leading, spacing: 4) {
                                FieldLabel("字节步长")
                                TextField(
                                    "",
                                    value: Binding(
                                        get: { project.byteStep },
                                        set: { project.byteStep = max($0, 1) }
                                    ),
                                    format: .number
                                )
                                .textFieldStyle(.roundedBorder)
                                .font(Typography.monoSmall)
                            }
                        }
                        Spacer(minLength: 0)
                    }

                    if project.previewMode == .artifactReadback && project.family == .cd {
                        Toggle("读回时反交织（CD-DA）", isOn: Binding(
                            get: { project.deinterleave },
                            set: { project.deinterleave = $0 }
                        ))
                        .toggleStyle(.switch)
                        .font(.system(size: 11.5))
                    }

                    Text("实测参数只用于预览/标定，不改变导出所用的生成几何。")
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.top, 8)
            } label: {
                Text("实测参数与精度")
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: 12)
    }

    private func measuredField(_ label: String, binding: Binding<Double>) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            FieldLabel(label)
            TextField("", value: binding, format: .number.precision(.fractionLength(3)))
                .textFieldStyle(.roundedBorder)
                .font(Typography.monoSmall)
        }
    }

    private var refreshCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Button {
                project.refreshPreview()
            } label: {
                Label(project.isPreviewing ? "正在生成预览…" : "刷新预览", systemImage: "arrow.clockwise")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(DesignSystem.brand)
            .controlSize(.large)
            .disabled(!project.canPreview)

            if let error = project.previewError {
                Text(error)
                    .font(Typography.caption2)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: 12)
    }

    private func chooseArtifact() {
        if let url = FilePanels.chooseInputFile(
            title: "选择成品文件",
            currentPath: project.artifactURL?.path
        ) {
            project.setArtifactURL(url)
        }
    }

    private func exportPreview() {
        guard let previewURL = project.previewURL else { return }
        let suggested = "预览-\(project.document.name).png"
        guard let destination = FilePanels.chooseOutputFile(
            title: "导出预览 PNG",
            currentPath: previewURL.deletingLastPathComponent().appendingPathComponent(suggested).path,
            defaultDirectory: previewURL.deletingLastPathComponent().path,
            allowedExtensions: ["png"]
        ) else { return }
        do {
            if FileManager.default.fileExists(atPath: destination.path) {
                try FileManager.default.removeItem(at: destination)
            }
            try FileManager.default.copyItem(at: previewURL, to: destination)
            NSWorkspace.shared.activateFileViewerSelecting([destination])
        } catch {
            project.previewError = "导出预览失败：\(error.localizedDescription)"
        }
    }
}

/// 预览监视器。
private struct PreviewMonitor: View {
    let project: ProjectModel
    let onExportPreview: () -> Void
    @State private var image: NSImage?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(project.previewMode.displayName)
                    .font(.system(size: 13, weight: .semibold))
                if let url = project.previewURL {
                    Text(url.lastPathComponent)
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                }
                Spacer()
                if let url = project.previewURL {
                    Button {
                        NSWorkspace.shared.activateFileViewerSelecting([url])
                    } label: {
                        Image(systemName: "folder")
                    }
                    .buttonStyle(.borderless)
                    .help("在 Finder 中显示")
                }
                Button {
                    onExportPreview()
                } label: {
                    Label("导出预览 PNG", systemImage: "square.and.arrow.up")
                }
                .controlSize(.small)
                .disabled(project.previewURL == nil)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)

            Divider().overlay(DesignSystem.hairline.opacity(0.4))

            ZStack {
                Color(red: 0.035, green: 0.035, blue: 0.043)
                if let image {
                    Image(nsImage: image)
                        .resizable()
                        .interpolation(.high)
                        .aspectRatio(contentMode: .fit)
                        .padding(16)
                        .onTapGesture(count: 2) {
                            if let url = project.previewURL {
                                NSWorkspace.shared.open(url)
                            }
                        }
                } else if project.isPreviewing {
                    VStack(spacing: 8) {
                        ProgressView()
                        Text("正在生成预览…")
                            .font(Typography.caption)
                            .foregroundStyle(.secondary)
                    }
                } else {
                    VStack(spacing: 8) {
                        Image(systemName: "eye.slash")
                            .font(.system(size: 26, weight: .light))
                            .foregroundStyle(.tertiary)
                        Text("还没有预览")
                            .font(Typography.caption)
                            .foregroundStyle(.secondary)
                        Text("设置参数后点「刷新预览」")
                            .font(Typography.caption2)
                            .foregroundStyle(.tertiary)
                    }
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .task(id: "\(project.previewURL?.path ?? "")#\(project.previewRevision)") {
                guard let url = project.previewURL,
                      FileManager.default.fileExists(atPath: url.path) else {
                    image = nil
                    return
                }
                image = NSImage(contentsOf: url)
            }

            Divider().overlay(DesignSystem.hairline.opacity(0.4))

            HStack(spacing: 8) {
                if let deviation = project.deviationSummary {
                    Label(deviation, systemImage: "ruler")
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                } else {
                    Text("实测几何与生成几何一致")
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                }
                Spacer()
                Text("双击图片用「预览」打开")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
        }
        .background(DesignSystem.surface1)
        .clipShape(RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                .strokeBorder(DesignSystem.hairline.opacity(0.5), lineWidth: 1)
        }
    }
}

/// 预览页/导出页共用的轻量卡片标题。
struct SectionHeader<Trailing: View>: View {
    let title: String
    let systemImage: String
    @ViewBuilder var trailing: () -> Trailing

    init(
        title: String,
        systemImage: String,
        @ViewBuilder trailing: @escaping () -> Trailing = { EmptyView() }
    ) {
        self.title = title
        self.systemImage = systemImage
        self.trailing = trailing
    }

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: systemImage)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(DesignSystem.brand)
            Text(title)
                .font(.system(size: 13, weight: .semibold))
            Spacer(minLength: 0)
            trailing()
        }
    }
}

/// 生成几何编辑 sheet。
struct GeometryEditSheet: View {
    let project: ProjectModel
    @Environment(\.dismiss) private var dismiss

    @State private var inner: Double
    @State private var outer: Double
    @State private var sectors: Double
    @State private var pitchLinear: Double
    @State private var pitchQuadratic: Double
    @State private var pitchCubic: Double
    @State private var errorMessage: String?

    init(project: ProjectModel) {
        self.project = project
        _inner = State(initialValue: project.document.geometry.innerRadiusMm)
        _outer = State(initialValue: project.document.geometry.outerRadiusMm)
        _sectors = State(initialValue: Double(project.document.geometry.sectors))
        _pitchLinear = State(initialValue: project.document.geometry.pitchLinear)
        _pitchQuadratic = State(initialValue: project.document.geometry.pitchQuadratic)
        _pitchCubic = State(initialValue: project.document.geometry.pitchCubic)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("工程生成几何")
                .font(.system(size: 16, weight: .semibold))
            Text("这些参数参与导出与预览；修改后需要重新合成/预览。")
                .font(Typography.caption)
                .foregroundStyle(.secondary)

            HStack(spacing: 10) {
                numeric("内半径 (mm)", value: $inner, digits: 3)
                numeric("外半径 (mm)", value: $outer, digits: 3)
            }
            numeric("总扇区数", value: $sectors, digits: 0)

            if project.family == .dvd {
                HStack(spacing: 10) {
                    numeric("线性项", value: $pitchLinear, digits: 8)
                    numeric("二次项", value: $pitchQuadratic, digits: 8)
                    numeric("三次项", value: $pitchCubic, digits: 8)
                }
            }

            if let errorMessage {
                Text(errorMessage)
                    .font(Typography.caption)
                    .foregroundStyle(.red)
            }

            HStack {
                Spacer()
                Button("取消") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("应用") { apply() }
                    .buttonStyle(.borderedProminent)
                    .tint(DesignSystem.brand)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 520)
        .background(DesignSystem.appBackground)
    }

    private func numeric(_ label: String, value: Binding<Double>, digits: Int) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            FieldLabel(label)
            TextField("", value: value, format: .number.precision(.fractionLength(digits)))
                .textFieldStyle(.roundedBorder)
                .font(Typography.monoSmall)
        }
    }

    private func apply() {
        guard inner > 0, outer > inner, sectors > 0 else {
            errorMessage = "内半径必须为正且小于外半径，扇区数必须大于 0。"
            return
        }
        let geometry = ProjectGeometry(
            innerRadiusMm: inner,
            outerRadiusMm: outer,
            sectors: Int64(sectors.rounded()),
            pitchLinear: pitchLinear,
            pitchQuadratic: pitchQuadratic,
            pitchCubic: pitchCubic
        )
        do {
            var document = project.document
            document.geometry = geometry
            try document.validate()
        } catch {
            errorMessage = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            return
        }
        project.setGeometry(geometry)
        dismiss()
    }
}
