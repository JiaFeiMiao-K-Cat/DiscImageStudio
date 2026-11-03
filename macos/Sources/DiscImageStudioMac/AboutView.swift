import SwiftUI
import DiscImageStudioKit

/// 关于页：版本、许可证、上游链接与第三方声明。
struct AboutView: View {
    let model: AppModel

    private let repositoryURL = URL(string: "https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio")!
    private let releasesURL = URL(string: "https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/releases")!
    private let licenseURL = URL(string: "https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/main/LICENSE")!
    private let noticesURL = URL(string: "https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/main/THIRD-PARTY-NOTICES.txt")!

    var body: some View {
        ScrollView {
            VStack(spacing: Spacing.xl) {
                header
                infoCard
                linksCard
                footer
            }
            .frame(maxWidth: 620)
            .padding(Spacing.xxl)
            .frame(maxWidth: .infinity, alignment: .center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    // MARK: - 头图

    private var header: some View {
        VStack(spacing: Spacing.m) {
            DiscMark(size: 96)
            VStack(spacing: Spacing.xxs) {
                Text("Disc Image Studio")
                    .font(.system(size: 26, weight: .bold))
                Text("全能的光盘盘面可见图像生成工作台")
                    .font(.system(size: 12.5))
                    .foregroundStyle(.secondary)
                Text("macOS 图形界面 · \(model.appVersionText)")
                    .font(Typography.caption)
                    .foregroundStyle(.tertiary)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.top, Spacing.m)
    }

    // MARK: - 信息

    private var infoCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("版本信息", systemImage: "info.circle")
            Grid(alignment: .leading, horizontalSpacing: Spacing.l, verticalSpacing: Spacing.xs) {
                GridRow(alignment: .firstTextBaseline) {
                    label("图形界面")
                    Text(model.appVersionText)
                        .font(.system(size: 11.5))
                }
                GridRow(alignment: .firstTextBaseline) {
                    label("CLI")
                    Text(model.resolvedCli.map { "已找到 · \($0.label)" } ?? "未连接")
                        .font(.system(size: 11.5))
                }
                if let cli = model.resolvedCli {
                    GridRow(alignment: .firstTextBaseline) {
                        label("运行方式")
                        Text(cli.label)
                            .font(.system(size: 11.5))
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        label("位置")
                        Text(Formatting.abbreviatedPath(cli.detail))
                            .font(Typography.monoSmall)
                            .foregroundStyle(.secondary)
                            .textSelection(.enabled)
                            .lineLimit(2)
                    }
                }
                GridRow(alignment: .firstTextBaseline) {
                    label("许可证")
                    Text("GPL-3.0-or-later")
                        .font(.system(size: 11.5, weight: .medium))
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private func label(_ text: String) -> some View {
        Text(text)
            .font(Typography.caption)
            .foregroundStyle(.secondary)
            .frame(width: 64, alignment: .leading)
    }

    // MARK: - 链接

    private var linksCard: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader("链接", systemImage: "link")
            VStack(alignment: .leading, spacing: Spacing.s) {
                Link(destination: repositoryURL) {
                    Label("上游仓库（GitHub）", systemImage: "arrow.up.right.square")
                }
                Link(destination: releasesURL) {
                    Label("发布页与更新日志", systemImage: "shippingbox")
                }
                Button {
                    openBundledFile(named: "LICENSE", fileExtension: nil, fallback: licenseURL)
                } label: {
                    Label("许可证全文（GPL-3.0-or-later）", systemImage: "doc.text")
                }
                .buttonStyle(.link)
                Button {
                    openBundledFile(named: "THIRD-PARTY-NOTICES", fileExtension: "txt", fallback: noticesURL)
                } label: {
                    Label("第三方声明", systemImage: "doc.on.doc")
                }
                .buttonStyle(.link)
            }
            .font(.system(size: 12))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    /// 优先打开应用包内的许可证/声明文件；没有内置时回退到上游仓库链接。
    private func openBundledFile(named name: String, fileExtension: String?, fallback: URL) {
        if let bundled = Bundle.main.url(forResource: name, withExtension: fileExtension) {
            NSWorkspace.shared.open(bundled)
            return
        }
        NSWorkspace.shared.open(fallback)
    }

    // MARK: - 页脚

    private var footer: some View {
        VStack(spacing: Spacing.xxs) {
            Text("macOS 图形界面为实验性功能，当前不包含物理刻录；生成的镜像需要配合 hdiutil / drutil 等工具使用。")
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            Text("Copyright © Disc Image Studio contributors. 在 GPL-3.0-or-later 下分发。")
                .font(Typography.caption2)
                .foregroundStyle(.quaternary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
    }
}
