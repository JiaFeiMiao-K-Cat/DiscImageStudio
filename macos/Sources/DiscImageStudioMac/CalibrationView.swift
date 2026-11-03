import AppKit
import SwiftUI
import DiscImageStudioKit

/// 完整标定向导：标靶 → 照片矫正 → 描点/反推 → 应用 → 记录。
struct CalibrationView: View {
    let app: AppModel
    let project: ProjectModel

    @State private var model: CalibrationModel?

    var body: some View {
        Group {
            if let model {
                content(model)
            } else {
                ProgressView("正在加载标定记录…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .background(DesignSystem.appBackground)
        .task {
            if model == nil {
                let created = project.calibrationModel()
                model = created
                await created.restoreIfNeeded()
            }
        }
    }

    @ViewBuilder
    private func content(_ model: CalibrationModel) -> some View {
        GeometryReader { geometry in
            if geometry.size.width >= 1000 {
                VStack(spacing: 12) {
                    header(model)
                    banners(model)
                    HStack(alignment: .top, spacing: 14) {
                        monitorCard(model)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                        stepsPanel(model)
                            .frame(width: 340)
                    }
                    recordsCard(model)
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 14)
            } else {
                ScrollView {
                    VStack(spacing: 12) {
                        header(model)
                        banners(model)
                        monitorCard(model)
                            .frame(height: 480)
                        stepsPanel(model)
                        recordsCard(model)
                    }
                    .padding(16)
                }
            }
        }
    }

    // MARK: - 头部与横幅

    private func header(_ model: CalibrationModel) -> some View {
        HStack(alignment: .center, spacing: 10) {
            ZStack {
                Circle()
                    .fill(DesignSystem.brand.opacity(0.14))
                    .frame(width: 36, height: 36)
                Image(systemName: "scope")
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(DesignSystem.brand)
            }
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text("辅助标定")
                        .font(.system(size: 16, weight: .semibold))
                    Chip(text: model.mode == .fine ? "精调" : "粗调", systemImage: "circle.dotted", tint: model.mode == .fine ? DesignSystem.brand : .orange)
                    if model.isBusy {
                        ProgressView().controlSize(.mini)
                    }
                }
                Text(model.geometrySummary)
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
            Spacer(minLength: 0)
            Button {
                model.openCalibrationDirectory()
            } label: {
                Label("打开 calibration 目录", systemImage: "folder")
            }
            .controlSize(.small)
        }
    }

    @ViewBuilder
    private func banners(_ model: CalibrationModel) -> some View {
        if let note = model.restoredSessionNote {
            banner(note, color: .orange, systemImage: "info.circle.fill")
        }
        if let error = model.errorMessage {
            HStack(alignment: .top, spacing: 8) {
                banner(error, color: .red, systemImage: "exclamationmark.triangle.fill")
                Button {
                    model.errorMessage = nil
                } label: {
                    Image(systemName: "xmark")
                }
                .buttonStyle(.borderless)
            }
        }
        if let applied = model.appliedMessage {
            banner(applied, color: .green, systemImage: "checkmark.circle.fill")
        } else if let status = model.statusMessage, model.errorMessage == nil {
            banner(status, color: .secondary, systemImage: "info.circle")
        }
    }

    private func banner(_ text: String, color: Color, systemImage: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: systemImage)
                .foregroundStyle(color)
            Text(text)
                .font(Typography.caption)
                .foregroundStyle(color == .secondary ? .secondary : color)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
        .background(color.opacity(0.07), in: RoundedRectangle(cornerRadius: 7))
    }

    // MARK: - 监视器

    private func monitorCard(_ model: CalibrationModel) -> some View {
        let canvas = canvasContent(model)
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: "photo")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(DesignSystem.brand)
                Text(monitorTitle(model))
                    .font(.system(size: 12, weight: .semibold))
                if let url = model.monitorImageURL {
                    Text(url.lastPathComponent)
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                }
                Spacer(minLength: 0)

                if model.step == .photo {
                    Toggle("显示矫正预览", isOn: Binding(
                        get: { model.showRectifiedPreview },
                        set: { model.showRectifiedPreview = $0 }
                    ))
                    .toggleStyle(.checkbox)
                    .font(Typography.caption2)
                    .disabled(model.rectifiedURL == nil)
                }
                if model.step == .fit {
                    Toggle("叠加预测", isOn: Binding(
                        get: { model.showPrediction },
                        set: { model.showPrediction = $0 }
                    ))
                    .toggleStyle(.checkbox)
                    .font(Typography.caption2)
                    .disabled(model.selectedCandidate == nil)
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 9)

            Divider().overlay(DesignSystem.hairline.opacity(0.4))

            ZStack {
                canvas
                if model.isBusy {
                    Color.black.opacity(0.5)
                    VStack(spacing: 10) {
                        ProgressView()
                            .controlSize(.large)
                        Text(model.busyText)
                            .font(Typography.caption)
                            .foregroundStyle(.secondary)
                        Button("取消等待") {
                            model.cancelCurrentOperation()
                        }
                        .controlSize(.small)
                    }
                    .padding(20)
                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
                }
            }

            Divider().overlay(DesignSystem.hairline.opacity(0.4))

            legend(model)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
        }
        .background(DesignSystem.surface1)
        .clipShape(RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                .strokeBorder(DesignSystem.hairline.opacity(0.5), lineWidth: 1)
        }
    }

    private func monitorTitle(_ model: CalibrationModel) -> String {
        switch model.step {
        case .target: return "标定图案预览"
        case .photo:
            if model.showRectifiedPreview && model.rectifiedURL != nil { return "矫正预览" }
            return "原始照片与轮廓"
        case .fit: return "矫正图 / 描点与预测"
        }
    }

    @ViewBuilder
    private func canvasContent(_ model: CalibrationModel) -> some View {
        switch model.step {
        case .target:
            CalibrationCanvas(
                imageURL: model.targetImageURL,
                space: .imagePixels(width: 2400, height: 2400)
            )
        case .photo:
            if model.showRectifiedPreview, model.rectifiedURL != nil {
                CalibrationCanvas(
                    imageURL: model.rectifiedURL,
                    space: .discMm(canvasOuterRadiusMm: model.canvasOuterRadiusMm, imageSize: model.rectifiedSize)
                )
            } else {
                CalibrationCanvas(
                    imageURL: model.photoURL,
                    space: .imagePixels(width: max(model.photoWidth, 1), height: max(model.photoHeight, 1)),
                    outerBoundary: model.outerBoundary,
                    holeBoundary: model.holeBoundary,
                    isPhotoEditable: true,
                    onMoveOuter: { index, point in
                        model.moveBoundary(kind: .outer, index: index, to: point)
                    },
                    onMoveHole: { index, point in
                        model.moveBoundary(kind: .hole, index: index, to: point)
                    },
                    onInteractionEnded: { model.persistAfterInteraction() }
                )
            }
        case .fit:
            CalibrationCanvas(
                imageURL: model.rectifiedURL,
                space: .discMm(canvasOuterRadiusMm: model.canvasOuterRadiusMm, imageSize: model.rectifiedSize),
                tracePoints: model.tracePoints,
                predicted: model.predictedPoints,
                outlierIndices: model.outlierIndices,
                selectedTraceIndex: model.selectedPointIndex,
                isTraceEditable: true,
                onSelectTrace: { model.selectTracePoint($0) },
                onMoveTrace: { index, point in
                    model.moveTracePoint(index: index, to: point)
                },
                onAddTrace: { point in
                    model.addTracePoint(point)
                },
                onInteractionEnded: { model.persistAfterInteraction() }
            )
            .focusable()
            .onDeleteCommand {
                model.deleteSelectedPoint()
            }
        }
    }

    @ViewBuilder
    private func legend(_ model: CalibrationModel) -> some View {
        if model.step == .photo {
            if model.showRectifiedPreview, model.rectifiedURL != nil {
                Text("矫正预览为只读；切换回照片可继续调整轮廓，修改轮廓会使矫正与后描点失效。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
            } else {
                HStack(spacing: 14) {
                    legendDot(color: DesignSystem.brandEmphasis, text: "盘片实体外缘")
                    legendDot(color: Color(red: 0.96, green: 0.65, blue: 0.14), text: "中心孔边缘")
                    Spacer(minLength: 0)
                    Button("重置轮廓") { model.resetBoundaries() }
                        .controlSize(.small)
                        .disabled(model.photoURL == nil)
                }
            }
        } else if model.step == .fit {
            HStack(spacing: 8) {
                Button("删除选中点") { model.deleteSelectedPoint() }
                    .controlSize(.small)
                    .disabled(model.selectedPointIndex == nil)
                Button("清空") { model.clearTracePoints() }
                    .controlSize(.small)
                    .disabled(model.tracePoints.isEmpty)
                    .accessibilityIdentifier("calibration-clear-points")
                Text(model.traceSummary)
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Spacer(minLength: 0)
                legendDot(color: Color(red: 0.34, green: 0.78, blue: 1.0), text: "描点")
                legendDot(color: Color(red: 1.0, green: 0.66, blue: 0.30), text: "预测")
                if !model.outlierIndices.isEmpty {
                    legendDot(color: .red, text: "偏离较大")
                }
            }
        } else {
            Text("图案按生成几何裁切环带；请在同一生成参数下刻录并拍照。")
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
        }
    }

    private func legendDot(color: Color, text: String) -> some View {
        HStack(spacing: 5) {
            Circle()
                .fill(color)
                .frame(width: 7, height: 7)
            Text(text)
                .font(Typography.caption2)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - 三步面板

    private func stepsPanel(_ model: CalibrationModel) -> some View {
        VStack(spacing: 10) {
            stepCard(model, step: .target, enabled: true) {
                targetStep(model)
            }
            stepCard(model, step: .photo, enabled: model.targetImageURL != nil) {
                photoStep(model)
            }
            stepCard(model, step: .fit, enabled: model.rectificationConfirmed) {
                fitStep(model)
            }
        }
    }

    private func stepCard<Content: View>(
        _ model: CalibrationModel,
        step: CalibrationModel.Step,
        enabled: Bool,
        @ViewBuilder content: () -> Content
    ) -> some View {
        let active = model.step == step
        return VStack(alignment: .leading, spacing: 0) {
            Button {
                model.selectStep(step)
            } label: {
                HStack(spacing: 8) {
                    Text("\(step.rawValue)")
                        .font(.system(size: 10, weight: .bold, design: .rounded))
                        .foregroundStyle(.white)
                        .frame(width: 20, height: 20)
                        .background(active ? DesignSystem.brand : (enabled ? DesignSystem.surface3 : DesignSystem.surface2), in: Circle())
                    VStack(alignment: .leading, spacing: 1) {
                        Text(step.title)
                            .font(.system(size: 12.5, weight: .semibold))
                        Text(step.subtitle)
                            .font(Typography.caption2)
                            .foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 0)
                    Image(systemName: active ? "chevron.down" : "chevron.right")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(.tertiary)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 9)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(!enabled)

            if active {
                Divider().overlay(DesignSystem.hairline.opacity(0.4))
                content()
                    .padding(12)
            }
        }
        .background(DesignSystem.surface1)
        .clipShape(RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                .strokeBorder(active ? DesignSystem.brand.opacity(0.5) : DesignSystem.hairline.opacity(0.45), lineWidth: 1)
        }
        .opacity(enabled ? 1 : 0.55)
    }

    private func targetStep(_ model: CalibrationModel) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            SegmentedControl(
                items: CalibrationMode.allCases.map {
                    .init(
                        id: $0,
                        title: $0 == .fine ? "精调靶标" : "粗调图案",
                        systemImage: $0 == .fine ? "scope" : "circle.lefthalf.filled"
                    )
                },
                selection: Binding(
                    get: { model.mode },
                    set: { model.setMode($0) }
                )
            )

            Button {
                model.generateTarget()
            } label: {
                Label(model.mode == .fine ? "生成精调标靶" : "生成粗调图案", systemImage: "wand.and.stars")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(DesignSystem.brand)
            .disabled(model.isBusy || model.service == nil)
            .accessibilityIdentifier("calibration-generate-target")

            if model.mode == .coarse {
                Text("粗调用于 issue #49 的两张法的第一步：半黑白盘可先消除累计绕圈，再用精调靶标做细标定；本工具不自动识别环数，仍需人工沿分界描点。")
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text("内置同心圆 + 十字靶标；生成时固定当前工程几何。参数应用后必须重新生成标靶再刻下一张。")
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let target = model.targetImageURL {
                HStack(spacing: 6) {
                    Image(systemName: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                    Text(target.lastPathComponent)
                        .font(Typography.monoSmall)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                HStack(spacing: 8) {
                    Button("在 Finder 中显示") { model.revealTargetInFinder() }
                        .controlSize(.small)
                    Button("下一步：照片矫正") { model.selectStep(.photo) }
                        .controlSize(.small)
                        .buttonStyle(.borderedProminent)
                        .tint(DesignSystem.brand)
                }
                HStack(spacing: 8) {
                    Button("前往导出") { app.selectStage(.export) }
                        .controlSize(.small)
                    Button("前往刻录") { app.selectStage(.burn) }
                        .controlSize(.small)
                }
                Text("生成/刻录请保持本记录保存的生成参数；刻录时在刻录页选择本图案文件。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text("尚未生成标靶。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
            }
        }
    }

    private func photoStep(_ model: CalibrationModel) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Button {
                model.importPhoto()
            } label: {
                Label("导入照片…", systemImage: "photo.badge.plus")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(DesignSystem.brand)
            .disabled(model.isBusy || model.targetImageURL == nil)
            .accessibilityIdentifier("calibration-import-photo")

            VStack(alignment: .leading, spacing: 4) {
                FieldLabel("实体盘片规格")
                SegmentedControl(
                    items: CalibrationDiscSize.allCases.map {
                        .init(id: $0, title: $0 == .standard ? "12 cm" : "8 cm", systemImage: "opticaldisc")
                    },
                    selection: Binding(
                        get: { model.discSize },
                        set: { model.setDiscSize($0) }
                    )
                )
            }

            if let photo = model.photoURL {
                Text("照片 \(model.photoWidth)×\(model.photoHeight) px · \(photo.lastPathComponent)")
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text("外缘 \(model.outerBoundary.count) 点 / 中心孔 \(model.holeBoundary.count) 点")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
            }

            HStack(spacing: 8) {
                Button("计算矫正") { model.rectifyPhoto() }
                    .controlSize(.small)
                    .disabled(model.isBusy || model.photoURL == nil)
                    .accessibilityIdentifier("calibration-rectify")
                Button("确认矫正并进入描点") { model.confirmRectification() }
                    .controlSize(.small)
                    .buttonStyle(.borderedProminent)
                    .tint(DesignSystem.brand)
                    .disabled(model.isBusy || model.rectifiedURL == nil)
                    .accessibilityIdentifier("calibration-confirm-rectification")
            }

            if let rectified = model.rectifiedURL {
                Text("矫正图：\(rectified.lastPathComponent) · 外半径 \(String(format: "%.1f", model.canvasOuterRadiusMm)) mm")
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                if model.rectificationConfirmed {
                    Label("已确认，可进入描点", systemImage: "checkmark.circle.fill")
                        .font(Typography.caption2)
                        .foregroundStyle(.green)
                }
            }
            if model.photoURL != nil {
                Button("重置轮廓为默认 8+8 点") { model.resetBoundaries() }
                    .controlSize(.small)
                    .disabled(model.isBusy)
            }
        }
    }

    private func fitStep(_ model: CalibrationModel) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Button {
                    model.fit()
                } label: {
                    Label("反推参数", systemImage: "function")
                }
                .buttonStyle(.borderedProminent)
                .tint(DesignSystem.brand)
                .disabled(model.isBusy || model.tracePoints.count < model.minimumPointCount || !model.rectificationConfirmed)
                .accessibilityIdentifier("calibration-fit")

                Button("返回照片矫正") { model.backToPhoto() }
                    .controlSize(.small)
            }

            Text(model.traceSummary)
                .font(Typography.caption2)
                .foregroundStyle(model.tracePoints.count < model.minimumPointCount ? .orange : .secondary)
                .fixedSize(horizontal: false, vertical: true)

            if model.mode == .coarse {
                Text("粗调：沿黑白分界从内到外打点；应用前必须核对橙色预测线与描点是否重合。")
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if !model.fitWarnings.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(Array(model.fitWarnings.enumerated()), id: \.offset) { _, warning in
                        Text("• \(warning)")
                            .font(Typography.caption2)
                            .foregroundStyle(.orange)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }

            if model.boundaryLimited {
                Label("搜索结果命中了搜索边界：参数可能被范围截住，请核对叠加线。", systemImage: "exclamationmark.triangle.fill")
                    .font(Typography.caption2)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if !model.candidates.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        FieldLabel("候选（\(model.candidates.count)）")
                        Spacer()
                        Text(model.mode == .fine ? "门槛：平均 ≤0.35 / 最大 ≤0.8 mm" : "粗调：warnings 必须为空")
                            .font(Typography.caption2)
                            .foregroundStyle(.tertiary)
                    }
                    ScrollView {
                        VStack(spacing: 2) {
                            ForEach(Array(model.candidates.enumerated()), id: \.offset) { index, candidate in
                                candidateRow(model, index: index, candidate: candidate)
                            }
                        }
                    }
                    .frame(maxHeight: 168)
                }
            }

            if let reason = model.applyDisabledReason, !model.candidates.isEmpty {
                Text(reason)
                    .font(Typography.caption2)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Button {
                model.applyParameters()
            } label: {
                Label("应用参数到工程", systemImage: "checkmark.seal")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(.green)
            .disabled(model.isBusy || !model.canApply)
            .accessibilityIdentifier("calibration-apply")

            if let applied = model.appliedMessage {
                Label(applied, systemImage: "checkmark.circle.fill")
                    .font(Typography.caption2)
                    .foregroundStyle(.green)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func candidateRow(
        _ model: CalibrationModel,
        index: Int,
        candidate: CalibrationCandidate
    ) -> some View {
        let selected = model.selectedCandidateIndex == index
        return Button {
            model.selectCandidate(index)
        } label: {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                    .font(.system(size: 11))
                    .foregroundStyle(selected ? DesignSystem.brand : Color(nsColor: .tertiaryLabelColor))
                    .padding(.top, 1)
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 8) {
                        Text("r0 \(format(candidate.innerRadiusMm)) / r1 \(format(candidate.outerRadiusMm))")
                            .font(Typography.monoSmall)
                        if candidate.outlierCount > 0 {
                            Text("偏离 \(candidate.outlierCount)")
                                .font(Typography.caption2)
                                .foregroundStyle(.red)
                        }
                    }
                    Text(String(
                        format: "平均 %.3f mm · 最大 %.3f mm",
                        candidate.rmsErrorMm,
                        candidate.maxErrorMm
                    ))
                    .font(Typography.caption2)
                    .foregroundStyle(candidate.rmsErrorMm <= CalibrationFitResult.applicableRmsMm
                                     && candidate.maxErrorMm <= CalibrationFitResult.applicableMaxMm
                                     ? Color(nsColor: .secondaryLabelColor) : Color.orange)
                    if model.family == .dvd {
                        Text(String(
                            format: "pitch %.6g / %.6g / %.6g",
                            candidate.pitchLinear,
                            candidate.pitchQuadratic,
                            candidate.pitchCubic
                        ))
                        .font(Typography.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 7)
            .padding(.vertical, 5)
            .background(selected ? DesignSystem.brand.opacity(0.13) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
            .contentShape(RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
    }

    // MARK: - 记录

    private func recordsCard(_ model: CalibrationModel) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("标定记录")
                    .font(.system(size: 13, weight: .semibold))
                Text("\(model.records.count) 条")
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                Spacer()
                Button("刷新") {
                    model.records = CalibrationRecordStore(workspace: model.workspace).load()
                }
                .controlSize(.small)
            }

            if model.records.isEmpty {
                Text("本工程还没有标定记录。完成步骤 1 后会自动保存记录。")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.vertical, 6)
            } else {
                ScrollView {
                    VStack(spacing: 2) {
                        ForEach(model.records) { record in
                            recordRow(model, record: record)
                        }
                    }
                }
                .frame(maxHeight: 140)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: 12)
    }

    private func recordRow(_ model: CalibrationModel, record: CalibrationRecord) -> some View {
        HStack(spacing: 8) {
            Image(systemName: record.mode == .fine ? "scope" : "circle.lefthalf.filled")
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .frame(width: 16)
            Text(displayDate(record.createdAt))
                .font(Typography.monoSmall)
                .foregroundStyle(.secondary)
                .frame(width: 92, alignment: .leading)
            Text(record.mode == .fine ? "精调" : "粗调")
                .font(Typography.caption2)
                .frame(width: 34, alignment: .leading)
            Text(record.family.familyLabel)
                .font(Typography.caption2)
                .frame(width: 28, alignment: .leading)
            Text("r0 \(format(record.generatedGeometry.innerRadiusMm)) / r1 \(format(record.generatedGeometry.outerRadiusMm))")
                .font(Typography.monoSmall)
                .lineLimit(1)
            Spacer(minLength: 0)
            if let candidate = record.selectedCandidate {
                Text(String(format: "RMS %.3f", candidate.rmsErrorMm))
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
                    .frame(width: 82, alignment: .trailing)
            } else {
                Text("未拟合")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .frame(width: 82, alignment: .trailing)
            }
            if record.isApplied {
                Chip(text: "已应用", systemImage: "checkmark.circle.fill", tint: .green)
            } else {
                Chip(text: "未应用", systemImage: "circle")
            }
            Button("打开") { model.openRecord(record) }
                .controlSize(.small)
            Button("删除", role: .destructive) { model.deleteRecord(record) }
                .controlSize(.small)
            Button("导出") { model.exportRecord(record) }
                .controlSize(.small)
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 4)
        .background(record.id == model.currentRecord?.id ? DesignSystem.brand.opacity(0.1) : Color.clear, in: RoundedRectangle(cornerRadius: 5))
    }

    // MARK: - 工具

    private func format(_ value: Double) -> String {
        if value == value.rounded() { return String(Int(value)) }
        return String(format: "%.3f", value)
    }

    private func displayDate(_ text: String) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let date = formatter.date(from: text) ?? {
            formatter.formatOptions = [.withInternetDateTime]
            return formatter.date(from: text)
        }()
        guard let date else { return text }
        let output = DateFormatter()
        output.dateFormat = "MM-dd HH:mm"
        return output.string(from: date)
    }
}
