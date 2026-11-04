import AppKit
import SwiftUI

/// PNG 输出预览：等比适配、双击用「预览」打开。
struct ImagePreviewView: View {
    let url: URL?
    var reloadToken: Int = 0

    @State private var image: NSImage?
    @State private var failed = false

    private var reloadKey: String {
        "\(url?.path ?? "none")#\(reloadToken)"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack(spacing: Spacing.xs) {
                Label("PNG 预览", systemImage: "photo")
                    .font(Typography.caption)
                    .foregroundStyle(.secondary)
                if let url {
                    Text(url.lastPathComponent)
                        .font(Typography.monoSmall)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                }
                Spacer(minLength: 0)
                Button("在预览中打开") {
                    openInPreview()
                }
                .controlSize(.small)
                .disabled(url == nil)
            }

            ZStack {
                RoundedRectangle(cornerRadius: DesignSystem.controlRadius, style: .continuous)
                    .fill(Color(nsColor: .textBackgroundColor).opacity(0.5))

                if let image {
                    Image(nsImage: image)
                        .resizable()
                        .interpolation(.high)
                        .aspectRatio(contentMode: .fit)
                        .frame(maxWidth: .infinity, maxHeight: 440)
                        .padding(Spacing.s)
                } else if failed {
                    VStack(spacing: Spacing.xs) {
                        Image(systemName: "photo.badge.exclamationmark")
                            .font(.system(size: 24))
                            .foregroundStyle(.secondary)
                        Text("无法加载预览图片")
                            .font(Typography.caption)
                            .foregroundStyle(.secondary)
                    }
                    .padding(Spacing.xl)
                } else {
                    ProgressView()
                        .controlSize(.small)
                        .padding(Spacing.xl)
                }
            }
            .frame(minHeight: 180)
            .overlay {
                RoundedRectangle(cornerRadius: DesignSystem.controlRadius, style: .continuous)
                    .strokeBorder(DesignSystem.hairline.opacity(0.4), lineWidth: 1)
            }
            .contentShape(Rectangle())
            .onTapGesture(count: 2) {
                openInPreview()
            }
            .help(L("双击用「预览」打开"))

            Text("双击图片可用「预览」打开完整分辨率。")
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
        }
        .task(id: reloadKey) {
            load()
        }
    }

    private func load() {
        guard let url, FileManager.default.fileExists(atPath: url.path) else {
            image = nil
            failed = true
            return
        }
        if let loaded = NSImage(contentsOf: url), loaded.size.width > 0 {
            image = loaded
            failed = false
        } else {
            image = nil
            failed = true
        }
    }

    private func openInPreview() {
        guard let url, FileManager.default.fileExists(atPath: url.path) else { return }
        if let previewApp = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.Preview") {
            NSWorkspace.shared.open(
                [url],
                withApplicationAt: previewApp,
                configuration: NSWorkspace.OpenConfiguration()
            )
        } else {
            NSWorkspace.shared.open(url)
        }
    }
}
