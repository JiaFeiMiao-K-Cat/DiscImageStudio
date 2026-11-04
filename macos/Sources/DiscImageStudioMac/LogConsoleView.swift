import AppKit
import SwiftUI
import DiscImageStudioKit

/// 日志卡：等宽字体、按 stream 着色、自动跟随底部（用户上滚时暂停）。
struct LogConsoleView: View {
    let lines: [LogLine]
    var isRunning: Bool = false
    var title: String = L("日志")
    var emptyHint: String = L("运行命令后，stdout / stderr 会逐行显示在这里。")
    var onClear: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            CardHeader(title, systemImage: "terminal", subtitle: subtitle) {
                HStack(spacing: Spacing.xs) {
                    Button {
                        copyLog()
                    } label: {
                        Label("复制", systemImage: "doc.on.doc")
                    }
                    .controlSize(.small)
                    .disabled(lines.isEmpty)
                    .help(L("把全部日志复制到剪贴板"))

                    if let onClear {
                        Button {
                            onClear()
                        } label: {
                            Label("清空", systemImage: "trash")
                        }
                        .controlSize(.small)
                        .disabled(lines.isEmpty)
                        .help(L("清空显示；不影响正在运行的任务"))
                    }
                }
            }

            LogTextView(lines: lines)
                .frame(minHeight: 200, idealHeight: 260)
                .clipShape(RoundedRectangle(cornerRadius: DesignSystem.controlRadius, style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: DesignSystem.controlRadius, style: .continuous)
                        .strokeBorder(DesignSystem.hairline.opacity(0.4), lineWidth: 1)
                }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }

    private var subtitle: String {
        if lines.isEmpty {
            return emptyHint
        }
        if isRunning {
            return L("共 %@ 行 · 自动跟随输出", String(lines.count))
        }
        return L("共 %@ 行", String(lines.count))
    }

    private func copyLog() {
        let text = lines.map(\.text).joined(separator: "\n")
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
    }
}

/// 用 NSTextView 承载日志：可选中/复制、等宽、按 stream 着色，并在用户停留底部时自动滚动。
struct LogTextView: NSViewRepresentable {
    let lines: [LogLine]

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSTextView.scrollableTextView()
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false
        scrollView.autohidesScrollers = true
        scrollView.borderType = .noBorder
        scrollView.drawsBackground = false
        scrollView.contentView.drawsBackground = false

        guard let textView = scrollView.documentView as? NSTextView else {
            return scrollView
        }
        textView.isEditable = false
        textView.isSelectable = true
        textView.drawsBackground = false
        textView.isRichText = true
        textView.usesFindBar = true
        textView.isAutomaticQuoteSubstitutionEnabled = false
        textView.isAutomaticDashSubstitutionEnabled = false
        textView.isAutomaticTextReplacementEnabled = false
        textView.textContainerInset = NSSize(width: 12, height: 10)
        textView.font = Self.baseFont
        textView.isHorizontallyResizable = false
        textView.isVerticallyResizable = true
        textView.autoresizingMask = [.width]
        textView.textContainer?.widthTracksTextView = true

        context.coordinator.textView = textView
        context.coordinator.scrollView = scrollView
        context.coordinator.update(with: lines)
        return scrollView
    }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        context.coordinator.update(with: lines)
    }

    // MARK: - 协调器

    @MainActor
    final class Coordinator {
        weak var textView: NSTextView?
        weak var scrollView: NSScrollView?
        private var renderedCount = 0
        private var lastRenderedID: UUID?

        func update(with lines: [LogLine]) {
            guard let textView, let storage = textView.textStorage else { return }
            storage.beginEditing()
            defer { storage.endEditing() }

            let shouldFollow = isPinnedToBottom()
            let prefixIntact = renderedCount > 0
                && lines.count >= renderedCount
                && lines[renderedCount - 1].id == lastRenderedID

            if !prefixIntact {
                storage.setAttributedString(NSAttributedString())
                for line in lines {
                    storage.append(Self.attributed(line))
                }
            } else if lines.count > renderedCount {
                for line in lines[renderedCount..<lines.count] {
                    storage.append(Self.attributed(line))
                }
            }

            renderedCount = lines.count
            lastRenderedID = lines.last?.id

            if shouldFollow {
                scrollToBottom()
            }
        }

        private func isPinnedToBottom() -> Bool {
            guard let textView, let scrollView else { return true }
            guard textView.bounds.height > 0 else { return true }
            let visibleMaxY = scrollView.contentView.bounds.maxY
            return visibleMaxY >= textView.bounds.height - 16
        }

        private func scrollToBottom() {
            guard let textView, let scrollView else { return }
            if let container = textView.textContainer {
                textView.layoutManager?.ensureLayout(for: container)
            }
            let visibleHeight = scrollView.contentView.bounds.height
            let bottom = max(0, textView.bounds.height - visibleHeight)
            scrollView.contentView.scroll(to: NSPoint(x: 0, y: bottom))
            scrollView.reflectScrolledClipView(scrollView.contentView)
        }

        static func attributed(_ line: LogLine) -> NSAttributedString {
            let color: NSColor
            let font: NSFont
            switch line.stream {
            case .stdout:
                color = .labelColor
                font = baseFont
            case .stderr:
                color = .systemRed
                font = baseFont
            case .system:
                color = .secondaryLabelColor
                font = NSFont.monospacedSystemFont(ofSize: 11.5, weight: .regular)
            }

            let paragraph = NSMutableParagraphStyle()
            paragraph.lineSpacing = 2
            paragraph.lineBreakMode = .byWordWrapping

            var attributes: [NSAttributedString.Key: Any] = [
                .font: font,
                .foregroundColor: color,
                .paragraphStyle: paragraph,
            ]
            if line.stream == .system {
                attributes[.obliqueness] = 0.08
            }
            return NSAttributedString(string: line.text + "\n", attributes: attributes)
        }

        static let baseFont = NSFont.monospacedSystemFont(ofSize: 11.5, weight: .regular)
    }

    static let baseFont = NSFont.monospacedSystemFont(ofSize: 11.5, weight: .regular)
}
