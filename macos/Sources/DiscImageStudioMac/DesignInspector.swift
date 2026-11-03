import AppKit
import SwiftUI
import DiscImageStudioKit

/// 右栏：图层属性 + 影调（含盘面缩略）。
struct DesignInspector: View {
    let project: ProjectModel

    var body: some View {
        ScrollView {
            VStack(spacing: 12) {
                propertiesCard
                toneCard
            }
        }
        .scrollIndicators(.never)
    }

    // MARK: - 属性

    private var propertiesCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("属性")
                    .font(.system(size: 13, weight: .semibold))
                Spacer()
                if let layer = project.selectedLayer {
                    Text(layer.visible ? "可见" : "已隐藏")
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                }
            }

            if let layer = project.selectedLayer {
                VStack(alignment: .leading, spacing: 4) {
                    FieldLabel("图层名")
                    TextField("图层名", text: nameBinding)
                        .textFieldStyle(.roundedBorder)
                        .font(.system(size: 11.5))
                }

                Grid(horizontalSpacing: 8, verticalSpacing: 8) {
                    GridRow {
                        numericField("X (mm)", binding: xBinding, digits: 2)
                        numericField("Y (mm)", binding: yBinding, digits: 2)
                    }
                    GridRow {
                        numericField("旋转 (°)", binding: rotationBinding, digits: 1)
                        numericField("缩放 (%)", binding: scaleBinding, digits: 0)
                    }
                }

                Text(Formatting.abbreviatedPath(layer.imagePath))
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(layer.imagePath)
            } else {
                VStack(spacing: 6) {
                    Image(systemName: "hand.point.up.left")
                        .font(.system(size: 20, weight: .light))
                        .foregroundStyle(.tertiary)
                    Text("选中一个图层以编辑属性")
                        .font(Typography.caption)
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 18)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: 12)
    }

    private func numericField(_ label: String, binding: Binding<Double>, digits: Int) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            FieldLabel(label)
            TextField(
                "",
                value: binding,
                format: .number.precision(.fractionLength(digits))
            )
            .textFieldStyle(.roundedBorder)
            .font(.system(size: 11.5, design: .monospaced))
            .multilineTextAlignment(.trailing)
        }
    }

    private var nameBinding: Binding<String> {
        Binding(
            get: { project.selectedLayer?.name ?? "" },
            set: { value in
                project.updateSelectedLayer { $0.name = value }
            }
        )
    }

    private var xBinding: Binding<Double> {
        Binding(
            get: { project.selectedLayer?.translateXMm ?? 0 },
            set: { value in
                project.updateSelectedLayer { $0.translateXMm = value }
            }
        )
    }

    private var yBinding: Binding<Double> {
        Binding(
            get: { project.selectedLayer?.translateYMm ?? 0 },
            set: { value in
                project.updateSelectedLayer { $0.translateYMm = value }
            }
        )
    }

    private var rotationBinding: Binding<Double> {
        Binding(
            get: { project.selectedLayer?.rotationDeg ?? 0 },
            set: { value in
                project.updateSelectedLayer { $0.rotationDeg = value }
            }
        )
    }

    private var scaleBinding: Binding<Double> {
        Binding(
            get: { (project.selectedLayer?.scale ?? 1) * 100 },
            set: { value in
                project.updateSelectedLayer { $0.scale = max(value / 100, 0.01) }
            }
        )
    }

    // MARK: - 影调

    private var toneCard: some View {
        let tone = project.tone
        return VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("影调")
                    .font(.system(size: 13, weight: .semibold))
                Spacer()
                if project.isComposing {
                    ProgressView().controlSize(.mini)
                }
            }

            CompositeDiscThumbnail(
                url: project.designCompositeURL,
                revision: project.designCompositeRevision
            )
            .frame(height: 176)

            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    FieldLabel("抖动强度")
                    Spacer()
                    Text("\(Int(tone.ditherPercent.rounded()))%")
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                }
                Slider(
                    value: Binding(
                        get: { tone.ditherPercent },
                        set: { project.setDitherPercent($0) }
                    ),
                    in: 0...100,
                    step: 1
                )
            }

            VStack(alignment: .leading, spacing: 4) {
                FieldLabel("灰阶档位")
                if project.family == .cd {
                    SegmentedControl(
                        items: [
                            .init(id: 2, title: "2 级"),
                            .init(id: 4, title: "4 级"),
                        ],
                        selection: Binding(
                            get: { tone.levels },
                            set: { project.setLevels($0) }
                        )
                    )
                } else {
                    Text("2 级（DVD / 蓝光固定）")
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                        .padding(.vertical, 4)
                }
            }

            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    FieldLabel("亮度")
                    Spacer()
                    Text(String(format: "%+.0f", tone.brightness))
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                }
                Slider(
                    value: Binding(
                        get: { tone.brightness },
                        set: { project.setBrightness($0) }
                    ),
                    in: -64...64,
                    step: 1
                )
            }

            Toggle("sRGB 线性化", isOn: Binding(
                get: { tone.srgbLinearization },
                set: { project.setSRGBLinearization($0) }
            ))
            .toggleStyle(.switch)
            .font(.system(size: 11.5))

            if let error = project.composeError {
                Text(error)
                    .font(Typography.caption2)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: 12)
    }
}

/// 盘面合成缩略图（深色底，等比适配）。
struct CompositeDiscThumbnail: View {
    let url: URL?
    let revision: Int
    @State private var image: NSImage?

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(Color(red: 0.04, green: 0.04, blue: 0.05))
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .interpolation(.high)
                    .scaledToFit()
                    .padding(6)
            } else {
                VStack(spacing: 6) {
                    Image(systemName: "circle.dashed")
                        .font(.system(size: 22, weight: .light))
                        .foregroundStyle(.tertiary)
                    Text("导入图层后显示盘面预览")
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                }
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(Color.white.opacity(0.08), lineWidth: 1)
        }
        .task(id: "\(url?.path ?? "")#\(revision)") {
            guard let url, FileManager.default.fileExists(atPath: url.path) else {
                image = nil
                return
            }
            image = NSImage(contentsOf: url)
        }
    }
}
