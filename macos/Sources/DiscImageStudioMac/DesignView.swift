import AppKit
import SwiftUI
import DiscImageStudioKit

/// 设计页：左「图层 + 随盘文件」、中「毫米画布」、右「属性 + 影调」。
struct DesignView: View {
    let app: AppModel
    let project: ProjectModel

    var body: some View {
        GeometryReader { geometry in
            if geometry.size.width >= 900 {
                HStack(alignment: .top, spacing: 16) {
                    leftRail
                        .frame(width: 252)
                    DiscCanvas(project: project)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                    DesignInspector(project: project)
                        .frame(width: 272)
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 16)
            } else {
                ScrollView {
                    VStack(spacing: 16) {
                        DiscCanvas(project: project)
                            .frame(height: 480)
                        leftRail
                        DesignInspector(project: project)
                    }
                    .padding(16)
                }
            }
        }
        .background(DesignSystem.appBackground)
    }

    // MARK: - 左栏

    private var leftRail: some View {
        VStack(spacing: 12) {
            layersCard
                .frame(maxHeight: .infinity)
            discFilesCard
        }
    }

    private var layersCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("图层")
                    .font(.system(size: 13, weight: .semibold))
                Spacer()
                Text("\(project.layers.count) 层")
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 12)

            Divider().overlay(DesignSystem.hairline.opacity(0.4))

            if project.layers.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "photo.on.rectangle.angled")
                        .font(.system(size: 24, weight: .light))
                        .foregroundStyle(.tertiary)
                    Text("还没有图层")
                        .font(Typography.caption)
                        .foregroundStyle(.secondary)
                    Text("导入 PNG / JPEG 图片开始设计")
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .padding(.vertical, 24)
            } else {
                ScrollView {
                    LazyVStack(spacing: 2) {
                        ForEach(project.layers) { layer in
                            layerRow(layer)
                        }
                    }
                    .padding(6)
                }
            }

            Divider().overlay(DesignSystem.hairline.opacity(0.4))

            VStack(spacing: 8) {
                HStack(spacing: 6) {
                    Button {
                        guard let id = selectedLayerID else { return }
                        project.moveLayer(id: id, offset: -1)
                    } label: {
                        Image(systemName: "arrow.up")
                    }
                    .help("上移一层")
                    .disabled(selectedLayerID == nil || !canMoveUp)
                    .accessibilityLabel("上移图层")

                    Button {
                        guard let id = selectedLayerID else { return }
                        project.moveLayer(id: id, offset: 1)
                    } label: {
                        Image(systemName: "arrow.down")
                    }
                    .help("下移一层")
                    .disabled(selectedLayerID == nil || !canMoveDown)
                    .accessibilityLabel("下移图层")

                    Button {
                        project.removeSelectedLayer()
                    } label: {
                        Image(systemName: "trash")
                    }
                    .help("删除选中图层")
                    .disabled(selectedLayerID == nil)
                    .accessibilityLabel("删除图层")

                    Spacer(minLength: 0)
                }
                .buttonStyle(.borderless)
                .controlSize(.small)

                Button {
                    let urls = FilePanels.chooseImages()
                    project.addLayerImages(urls)
                } label: {
                    Label("导入图片", systemImage: "photo.badge.plus")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
                .controlSize(.regular)
            }
            .padding(12)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(DesignSystem.surface1, in: RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                .strokeBorder(DesignSystem.hairline.opacity(0.5), lineWidth: 1)
        }
    }

    private var selectedLayerID: String? { project.selectedLayerID }

    private var canMoveUp: Bool {
        guard let id = selectedLayerID,
              let index = project.layers.firstIndex(where: { $0.id == id }) else { return false }
        return index > 0
    }

    private var canMoveDown: Bool {
        guard let id = selectedLayerID,
              let index = project.layers.firstIndex(where: { $0.id == id }) else { return false }
        return index < project.layers.count - 1
    }

    private func layerRow(_ layer: DesignLayer) -> some View {
        let selected = project.selectedLayerID == layer.id
        return HStack(spacing: 8) {
            LayerThumbnail(path: layer.imagePath)
                .frame(width: 38, height: 38)
                .clipShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
            VStack(alignment: .leading, spacing: 1) {
                Text(layer.name)
                    .font(.system(size: 11.5, weight: .medium))
                    .lineLimit(1)
                    .foregroundStyle(layer.visible ? .primary : .secondary)
                Text(String(format: "%.1f, %.1f mm", layer.translateXMm, layer.translateYMm))
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
            Button {
                project.toggleVisibility(id: layer.id)
            } label: {
                Image(systemName: layer.visible ? "eye" : "eye.slash")
                    .font(.system(size: 11))
                    .foregroundStyle(layer.visible ? .secondary : .tertiary)
            }
            .buttonStyle(.borderless)
            .help(layer.visible ? "隐藏图层" : "显示图层")
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(selected ? DesignSystem.brand.opacity(0.18) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        .overlay(alignment: .leading) {
            if selected {
                RoundedRectangle(cornerRadius: 1.5)
                    .fill(DesignSystem.brand)
                    .frame(width: 2.5)
                    .padding(.vertical, 4)
            }
        }
        .contentShape(RoundedRectangle(cornerRadius: 6))
        .onTapGesture {
            project.selectLayer(layer.id)
        }
        .contextMenu {
            Button("显示/隐藏") { project.toggleVisibility(id: layer.id) }
            Button("上移") { project.moveLayer(id: layer.id, offset: -1) }
            Button("下移") { project.moveLayer(id: layer.id, offset: 1) }
            Divider()
            Button("删除", role: .destructive) { project.removeLayer(id: layer.id) }
        }
    }

    // MARK: - 随盘文件

    private var discFilesCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("随盘文件")
                    .font(.system(size: 13, weight: .semibold))
                Spacer()
                if !project.document.outputs.dataDirectory.isEmpty {
                    Chip(text: "数据盘模式", systemImage: "folder.fill", tint: DesignSystem.brand)
                }
            }
            Text("空则整盘绘制；选择文件夹后会生成带文件的镜像（CD 自动切换为数据盘模式）。")
                .font(Typography.caption2)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            CompactPathField(
                label: "文件夹",
                path: project.document.outputs.dataDirectory,
                placeholder: "未选择（整盘绘制）",
                onBrowse: {
                    if let url = FilePanels.chooseInputFolder(
                        title: "选择随盘文件目录",
                        currentPath: project.document.outputs.dataDirectory
                    ) {
                        project.setDataDirectory(url.path)
                    }
                },
                onClear: { project.setDataDirectory("") }
            )

            if !project.document.outputs.dataDirectory.isEmpty {
                SegmentedControl(
                    items: [
                        .init(id: false, title: "内圈", systemImage: "circle.dotted"),
                        .init(id: true, title: "外圈", systemImage: "circle"),
                    ],
                    selection: Binding(
                        get: { project.document.outputs.filesAtOuterRing },
                        set: { project.setFilesAtOuterRing($0) }
                    )
                )
            }

            if project.family == .cd && project.document.outputs.dataDirectory.isEmpty {
                Text("未选择随盘文件：导出默认生成 WAV 音轨/CUE（音频 CD）。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: 12)
    }
}

/// 图层列表缩略图。
struct LayerThumbnail: View {
    let path: String
    @State private var image: NSImage?

    var body: some View {
        ZStack {
            DesignSystem.surface2
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .scaledToFit()
            } else {
                Image(systemName: "photo")
                    .font(.system(size: 12))
                    .foregroundStyle(.tertiary)
            }
        }
        .task(id: path) {
            image = NSImage(contentsOfFile: path)
        }
    }
}
