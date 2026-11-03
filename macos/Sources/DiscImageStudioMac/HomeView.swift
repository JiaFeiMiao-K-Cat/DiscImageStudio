import AppKit
import SwiftUI
import DiscImageStudioKit

/// 无工程/返回主页时的起始页：打开或新建工程、从预设开始、最近工程。
struct HomeView: View {
    let app: AppModel

    private let cardColumns = [GridItem(.adaptive(minimum: 148, maximum: 180), spacing: 12)]
    private let recentColumns = Array(repeating: GridItem(.flexible(), spacing: 12), count: 3)

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                Text("Disc Image Studio")
                    .font(.system(size: 28, weight: .bold))
                Text("把图片设计成 CD / DVD / 蓝光光盘上肉眼可见的盘面图案。")
                    .font(.system(size: 12.5))
                    .foregroundStyle(.secondary)
                    .padding(.top, 6)

                actionCard
                    .padding(.top, 20)

                if let error = app.projectError {
                    errorBanner(error)
                        .padding(.top, 12)
                }

                if app.presetsErrorMessage != nil {
                    presetErrorBanner
                        .padding(.top, 16)
                }

                HStack(alignment: .firstTextBaseline) {
                    Text("从模板开始")
                        .font(.system(size: 14, weight: .semibold))
                    Spacer()
                    if app.usingFallbackPresets {
                        Text("内置兜底参数")
                            .font(Typography.caption2)
                            .foregroundStyle(.orange)
                    }
                    if app.isLoadingPresets {
                        ProgressView()
                            .controlSize(.mini)
                    }
                }
                .padding(.top, 28)
                .padding(.bottom, 10)

                LazyVGrid(columns: cardColumns, alignment: .leading, spacing: 12) {
                    blankTemplateCard
                    ForEach(app.allPresets) { preset in
                        presetCard(preset)
                    }
                }

                if !app.recents.isEmpty {
                    HStack {
                        Text("最近工程")
                            .font(.system(size: 14, weight: .semibold))
                        Spacer()
                        Button("清空") { app.clearRecents() }
                            .buttonStyle(.link)
                            .font(.caption)
                    }
                    .padding(.top, 32)
                    .padding(.bottom, 12)

                    LazyVGrid(columns: recentColumns, alignment: .leading, spacing: 12) {
                        ForEach(app.recents) { recent in
                            recentCard(recent)
                        }
                    }
                }
            }
            .frame(maxWidth: 1120, alignment: .leading)
            .padding(.horizontal, 32)
            .padding(.vertical, 28)
            .frame(maxWidth: .infinity, alignment: .center)
        }
        .background(DesignSystem.appBackground)
    }

    // MARK: - 操作卡

    private var actionCard: some View {
        HStack(spacing: Spacing.m) {
            if let project = app.project {
                Button {
                    app.continueProject()
                } label: {
                    HStack(spacing: 8) {
                        Image(systemName: "arrow.left")
                            .foregroundStyle(DesignSystem.brand)
                        Text("继续工程")
                        Text(project.document.name)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
                .controlSize(.large)
            }

            Spacer(minLength: 0)

            Button("打开工程…") {
                app.openProjectPanel()
            }
            .controlSize(.large)

            Button("新建工程") {
                app.beginNewProject()
            }
            .controlSize(.large)
            .buttonStyle(.borderedProminent)
            .tint(DesignSystem.brand)
            .keyboardShortcut("n", modifiers: .command)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .background(DesignSystem.surface1, in: RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                .strokeBorder(DesignSystem.hairline.opacity(0.5), lineWidth: 1)
        }
    }

    private func errorBanner(_ message: String) -> some View {
        HStack(spacing: Spacing.s) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
            Text(message)
                .font(Typography.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Button {
                app.projectError = nil
            } label: {
                Image(systemName: "xmark")
            }
            .buttonStyle(.borderless)
            .controlSize(.small)
        }
        .padding(Spacing.m)
        .background(Color.orange.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
    }

    private var presetErrorBanner: some View {
        HStack(alignment: .top, spacing: Spacing.s) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: 4) {
                Text("盘片预设加载失败，模板使用内置兜底参数。")
                    .font(Typography.caption)
                if let message = app.presetsErrorMessage {
                    Text(message)
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                        .lineLimit(3)
                        .textSelection(.enabled)
                }
            }
            Spacer(minLength: 0)
            Button("重试") {
                Task { await app.refreshPresets() }
            }
            .controlSize(.small)
            .disabled(app.isLoadingPresets)
        }
        .padding(Spacing.m)
        .background(Color.orange.opacity(0.07), in: RoundedRectangle(cornerRadius: 8))
    }

    // MARK: - 模板

    private var blankTemplateCard: some View {
        Button {
            app.beginNewProject()
        } label: {
            VStack(spacing: 8) {
                Image(systemName: "square.dashed")
                    .font(.system(size: 26, weight: .light))
                    .foregroundStyle(.secondary)
                Text("空白工程")
                    .font(.system(size: 12, weight: .medium))
                Text("选择盘型与预设")
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, minHeight: 116)
            .background(DesignSystem.surface1, in: RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                    .strokeBorder(DesignSystem.hairline.opacity(0.5), lineWidth: 1)
            }
            .contentShape(RoundedRectangle(cornerRadius: DesignSystem.cardRadius))
        }
        .buttonStyle(.plain)
    }

    private func presetCard(_ preset: DiscPreset) -> some View {
        Button {
            app.beginNewProject(family: preset.family, preset: preset)
        } label: {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Image(systemName: "opticaldisc")
                        .font(.system(size: 22, weight: .light))
                        .foregroundStyle(FamilyBadge.ringColor(for: preset.family))
                    Spacer()
                    Text(preset.family.familyLabel)
                        .font(Typography.badge)
                        .foregroundStyle(.secondary)
                }
                Text(preset.displayName)
                    .font(.system(size: 11.5, weight: .medium))
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Text("\(format(preset.geometry.innerRadiusMm))–\(format(preset.geometry.outerRadiusMm)) mm")
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
            }
            .padding(12)
            .frame(maxWidth: .infinity, minHeight: 128, alignment: .topLeading)
            .background(DesignSystem.surface1, in: RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                    .strokeBorder(DesignSystem.hairline.opacity(0.5), lineWidth: 1)
            }
            .contentShape(RoundedRectangle(cornerRadius: DesignSystem.cardRadius))
        }
        .buttonStyle(.plain)
        .help(preset.description)
    }

    // MARK: - 最近工程

    private func recentCard(_ recent: RecentProject) -> some View {
        Button {
            _ = app.openProject(url: recent.fileURL)
        } label: {
            HStack(spacing: 12) {
                RecentThumbnail(base64: recent.thumbnailPngBase64, family: recent.family)
                    .frame(width: 82, height: 82)
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
                        Text(recent.name)
                            .font(.system(size: 12, weight: .semibold))
                            .lineLimit(1)
                        Text(recent.family.familyLabel)
                            .font(Typography.badge)
                            .foregroundStyle(.secondary)
                    }
                    Text(Formatting.abbreviatedPath(recent.path))
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Spacer(minLength: 0)
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(DesignSystem.surface1, in: RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                    .strokeBorder(DesignSystem.hairline.opacity(0.5), lineWidth: 1)
            }
            .contentShape(RoundedRectangle(cornerRadius: DesignSystem.cardRadius))
        }
        .buttonStyle(.plain)
        .contextMenu {
            Button("从列表中移除") { app.removeRecent(recent) }
        }
        .help(recent.path)
    }

    private func format(_ value: Double) -> String {
        value == value.rounded() ? String(Int(value)) : String(format: "%.2f", value)
    }
}

/// 最近工程缩略图：base64 PNG 或默认盘面图形。
struct RecentThumbnail: View {
    let base64: String?
    let family: DiscFamily
    @State private var image: NSImage?

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(DesignSystem.surface2)
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .scaledToFit()
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            } else {
                ZStack {
                    Circle()
                        .strokeBorder(FamilyBadge.ringColor(for: family).opacity(0.5), lineWidth: 6)
                        .padding(16)
                    Circle()
                        .fill(DesignSystem.surface1)
                        .padding(30)
                }
            }
        }
        .task(id: base64) {
            guard let base64, let data = Data(base64Encoded: base64) else {
                image = nil
                return
            }
            image = NSImage(data: data)
        }
    }
}
