import SwiftUI
import DiscImageStudioKit

/// 标定页（简化版）：拍摄说明 + 手工实测半径/三次项写回预览参数。
struct CalibrationView: View {
    let app: AppModel
    let project: ProjectModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                hero
                explanationCard
                measurementCard
            }
            .frame(maxWidth: 900, alignment: .leading)
            .padding(.horizontal, 24)
            .padding(.vertical, 18)
            .frame(maxWidth: .infinity, alignment: .center)
        }
        .background(DesignSystem.appBackground)
    }

    private var hero: some View {
        HStack(spacing: 14) {
            ZStack {
                Circle()
                    .fill(DesignSystem.brand.opacity(0.14))
                    .frame(width: 44, height: 44)
                Image(systemName: "scope")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(DesignSystem.brand)
            }
            VStack(alignment: .leading, spacing: 3) {
                Text("盘片标定（简化版）")
                    .font(.system(size: 18, weight: .semibold))
                Text("用实拍盘片测量内/外数据半径，写入预览的「实测参数」；完整照片框选与曲线拟合将在后续迭代提供。")
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
    }

    private var explanationCard: some View {
        SectionCard("拍摄要点", systemImage: "camera") {
            VStack(alignment: .leading, spacing: 6) {
                bullet("在同一台刻录机上用固定速度写入测试图案，拍摄时让盘面充满画面、避免反光。")
                bullet("镜头光轴尽量垂直盘面；保留盘心孔与盘缘作为比例参考。")
                bullet("测量生成几何与实拍外缘/中心孔的偏差，可先填实测半径与 DVD 三次项。")
                bullet("实测参数只影响预览/标定，不会改变导出使用的生成几何。")
            }
        } trailing: {
            EmptyView()
        }
    }

    private func bullet(_ text: String) -> some View {
        HStack(alignment: .top, spacing: 6) {
            Image(systemName: "circle.fill")
                .font(.system(size: 4))
                .foregroundStyle(DesignSystem.brand)
                .padding(.top, 5)
            Text(text)
                .font(Typography.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var measurementCard: some View {
        SectionCard("手工实测参数", systemImage: "ruler") {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 12) {
                    numericField("实测内半径 (mm)", binding: Binding(
                        get: { project.actualInnerRadiusMm },
                        set: { project.setMeasuredInner($0) }
                    ), digits: 3)
                    numericField("实测外半径 (mm)", binding: Binding(
                        get: { project.actualOuterRadiusMm },
                        set: { project.setMeasuredOuter($0) }
                    ), digits: 3)
                }

                if project.family == .dvd {
                    HStack(spacing: 12) {
                        numericField("三次项·线性", binding: Binding(
                            get: { project.actualPitchLinear },
                            set: { project.setMeasuredPitch(linear: $0) }
                        ), digits: 8)
                        numericField("二次项", binding: Binding(
                            get: { project.actualPitchQuadratic },
                            set: { project.setMeasuredPitch(quadratic: $0) }
                        ), digits: 8)
                        numericField("三次项", binding: Binding(
                            get: { project.actualPitchCubic },
                            set: { project.setMeasuredPitch(cubic: $0) }
                        ), digits: 8)
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

                Divider().overlay(DesignSystem.hairline.opacity(0.4))

                HStack(spacing: 10) {
                    Button {
                        project.applyCalibrationToPreview()
                        app.selectStage(.preview)
                    } label: {
                        Label("写回预览参数并前往预览", systemImage: "arrow.right")
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(DesignSystem.brand)

                    Button("重置为生成几何") {
                        project.actualInnerRadiusMm = project.document.geometry.innerRadiusMm
                        project.actualOuterRadiusMm = project.document.geometry.outerRadiusMm
                        project.actualPitchLinear = project.document.geometry.pitchLinear
                        project.actualPitchQuadratic = project.document.geometry.pitchQuadratic
                        project.actualPitchCubic = project.document.geometry.pitchCubic
                    }
                    .controlSize(.small)
                    Spacer(minLength: 0)
                }

                if let deviation = project.deviationSummary {
                    Label(deviation, systemImage: "ruler")
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                } else {
                    Text("当前实测参数与生成几何一致。")
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                }
            }
        } trailing: {
            EmptyView()
        }
    }

    private func numericField(_ label: String, binding: Binding<Double>, digits: Int) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            FieldLabel(label)
            TextField("", value: binding, format: .number.precision(.fractionLength(digits)))
                .textFieldStyle(.roundedBorder)
                .font(Typography.monoSmall)
        }
    }
}
