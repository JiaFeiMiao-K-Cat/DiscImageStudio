import SwiftUI
import DiscImageStudioKit

// MARK: - 设计令牌

/// 全局视觉令牌：品牌色、语义色、圆角、间距、字体层级。
/// 颜色一律使用 `Color(nsColor:)` 或系统语义色，深色/浅色自动适配。
enum DesignSystem {
    /// 品牌蓝 #3385FF（浅色强调）/ #74C0FF（深色强调），与上游 WPF 令牌保持一致。
    static let brand = Color(red: 0x33 / 255, green: 0x85 / 255, blue: 0xFF / 255)
    static let brandEmphasis = Color(red: 0x74 / 255, green: 0xC0 / 255, blue: 0xFF / 255)

    /// 卡片圆角 12 pt，控件 8 pt。
    static let cardRadius: CGFloat = 12
    static let controlRadius: CGFloat = 8

    /// 详情区最小宽度与内容最大宽度。
    static let detailMinWidth: CGFloat = 720
    static let contentMaxWidth: CGFloat = 980

    /// 侧栏宽度范围 240–280 pt。
    static let sidebarMinWidth: CGFloat = 240
    static let sidebarIdealWidth: CGFloat = 264
    static let sidebarMaxWidth: CGFloat = 280

    /// 在给定外观下的品牌强调色。
    static func accent(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? brandEmphasis : brand
    }

    static let cardBackground = Color(nsColor: .controlBackgroundColor)
    static let subtleFill = Color(nsColor: .quaternaryLabelColor).opacity(0.08)
    static let hairline = Color(nsColor: .separatorColor)
}

// MARK: - 间距与字体

enum Spacing {
    static let xxs: CGFloat = 4
    static let xs: CGFloat = 6
    static let s: CGFloat = 8
    static let m: CGFloat = 12
    static let l: CGFloat = 16
    static let xl: CGFloat = 24
    static let xxl: CGFloat = 32
}

enum Typography {
    static let pageTitle = Font.system(size: 26, weight: .bold)
    static let sectionTitle = Font.headline
    static let body = Font.body
    static let secondary = Font.callout
    static let caption = Font.caption
    static let caption2 = Font.caption2
    static let mono = Font.system(.body, design: .monospaced)
    static let monoSmall = Font.system(size: 11.5, design: .monospaced)
    static let badge = Font.system(size: 10.5, weight: .semibold)
}

// MARK: - 卡片与分组

extension View {
    /// 统一的卡片外观：材质底、1 pt 描边、12 pt 圆角。
    func cardSurface(padding: CGFloat = Spacing.l) -> some View {
        self
            .padding(padding)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                    .strokeBorder(DesignSystem.hairline.opacity(0.55), lineWidth: 1)
            }
    }

    /// 卡片内的浅色内衬（用于日志、代码块）。
    func insetSurface(radius: CGFloat = DesignSystem.controlRadius) -> some View {
        self
            .background(DesignSystem.subtleFill, in: RoundedRectangle(cornerRadius: radius, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: radius, style: .continuous)
                    .strokeBorder(DesignSystem.hairline.opacity(0.35), lineWidth: 1)
            }
    }
}

/// 卡片标题：图标 + 标题 + 可选副标题 + 尾部内容。
struct CardHeader<Trailing: View>: View {
    let title: String
    let systemImage: String
    var subtitle: String?
    @ViewBuilder var trailing: () -> Trailing

    init(
        _ title: String,
        systemImage: String,
        subtitle: String? = nil,
        @ViewBuilder trailing: @escaping () -> Trailing = { EmptyView() }
    ) {
        self.title = title
        self.systemImage = systemImage
        self.subtitle = subtitle
        self.trailing = trailing
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
            Image(systemName: systemImage)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.secondary)
                .frame(width: 16)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(Typography.sectionTitle)
                if let subtitle, !subtitle.isEmpty {
                    Text(subtitle)
                        .font(Typography.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: Spacing.s)
            trailing()
        }
    }
}

// MARK: - 徽标

/// 家族徽标：SF Symbol 圆环 + 家族短名，不依赖位图。
struct FamilyBadge: View {
    let family: OpticalDiscFamily
    var compact: Bool = false

    static func ringColor(for family: OpticalDiscFamily) -> Color {
        switch family {
        case .compactDisc: return Color(nsColor: .systemGray)
        case .dvd: return Color(red: 0.16, green: 0.35, blue: 0.66)
        case .bluRay: return Color(red: 0.83, green: 0.63, blue: 0.09)
        }
    }

    static func ringColor(for family: DiscFamily) -> Color {
        switch family {
        case .cd: return Color(nsColor: .systemGray)
        case .dvd: return Color(red: 0.16, green: 0.35, blue: 0.66)
        case .bd: return Color(red: 0.83, green: 0.63, blue: 0.09)
        }
    }

    private var ringColor: Color {
        Self.ringColor(for: family)
    }

    var body: some View {
        HStack(spacing: compact ? 4 : 6) {
            ZStack {
                Circle()
                    .fill(ringColor.opacity(0.16))
                    .frame(width: compact ? 18 : 22, height: compact ? 18 : 22)
                Image(systemName: "opticaldisc")
                    .font(.system(size: compact ? 10 : 12, weight: .semibold))
                    .foregroundStyle(ringColor)
            }
            Text(family.shortName)
                .font(compact ? Typography.badge : .system(size: 12, weight: .semibold))
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(family.displayName)
    }
}

/// 小号胶囊标签：能力、必填、状态等。
struct Chip: View {
    let text: String
    var systemImage: String?
    var tint: Color?

    var body: some View {
        HStack(spacing: 4) {
            if let systemImage {
                Image(systemName: systemImage)
                    .font(.system(size: 9, weight: .bold))
            }
            Text(text)
                .font(Typography.badge)
        }
        .foregroundStyle(tint ?? .secondary)
        .padding(.horizontal, 7)
        .padding(.vertical, 3)
        .background(
            (tint ?? Color(nsColor: .secondaryLabelColor)).opacity(0.12),
            in: Capsule(style: .continuous)
        )
        .accessibilityElement(children: .combine)
    }
}

/// 必填徽标。
struct RequiredBadge: View {
    var body: some View {
        Text("必填")
            .font(Typography.badge)
            .foregroundStyle(.orange)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(Color.orange.opacity(0.12), in: Capsule(style: .continuous))
            .accessibilityLabel("必填项")
    }
}

// MARK: - 能力标签

extension DiscCommandDescriptor {
    /// 能力标志的中文展示名（未知标志回退为原始名称）。
    var capabilityLabels: [String] {
        capabilities.map { capability in
            switch capability {
            case "RawTrackGeneration": return "音频轨道"
            case "DataImageGeneration": return "数据镜像"
            case "HybridFilesystem": return "混合文件系统"
            case "GeometryPreview": return "几何预览"
            case "EncodedOutputPreview": return "编码预览"
            case "ErrorCorrectionEncoding": return "纠错编码"
            case "None": return "无"
            default: return capability
            }
        }
    }
}

// MARK: - 格式化

enum Formatting {
    /// 时长：小于 60 秒显示 "12.3 秒"，否则 "1 分 05 秒"。
    static func duration(_ seconds: TimeInterval) -> String {
        guard seconds.isFinite, seconds >= 0 else { return "—" }
        if seconds < 60 {
            return String(format: "%.1f 秒", seconds)
        }
        let total = Int(seconds.rounded())
        let minutes = total / 60
        let remainder = total % 60
        return String(format: "%d 分 %02d 秒", minutes, remainder)
    }

    /// 文件大小；传目录或不存在时返回 nil。
    static func fileSize(at url: URL) -> String? {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory),
              !isDirectory.boolValue,
              let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attributes[.size] as? NSNumber
        else { return nil }
        return ByteCountFormatter.string(fromByteCount: size.int64Value, countStyle: .file)
    }

    /// 路径展示：把用户主目录缩写为 "~"。
    static func abbreviatedPath(_ path: String) -> String {
        (path as NSString).abbreviatingWithTildeInPath
    }

    /// 把用户输入的路径展开 `~` 并标准化。
    static func expandedURL(for rawPath: String) -> URL? {
        let trimmed = rawPath.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        let expanded = (trimmed as NSString).expandingTildeInPath
        return URL(fileURLWithPath: expanded)
    }
}

// MARK: - 光盘标记

/// 品牌光盘图形：同心圆环 + 品牌色渐变，用于概览、引导与关于页。
struct DiscMark: View {
    var size: CGFloat = 64

    var body: some View {
        ZStack {
            Circle()
                .fill(
                    AngularGradient(
                        colors: [
                            DesignSystem.brandEmphasis,
                            DesignSystem.brand,
                            Color(red: 0.44, green: 0.36, blue: 0.95),
                            DesignSystem.brand.opacity(0.65),
                            DesignSystem.brandEmphasis,
                        ],
                        center: .center
                    )
                )
            Circle()
                .strokeBorder(Color.white.opacity(0.28), lineWidth: max(1, size * 0.02))
                .padding(size * 0.12)
            Circle()
                .strokeBorder(Color.white.opacity(0.16), lineWidth: max(1, size * 0.012))
                .padding(size * 0.24)
            Circle()
                .fill(.background)
                .padding(size * 0.405)
            Circle()
                .strokeBorder(Color.white.opacity(0.18), lineWidth: max(1, size * 0.016))
                .padding(size * 0.34)
        }
        .frame(width: size, height: size)
        .compositingGroup()
        .shadow(color: .black.opacity(0.18), radius: size * 0.06, y: size * 0.03)
        .accessibilityHidden(true)
    }
}

// MARK: - 工作流表面色（接近 Windows 视觉令牌，仍随系统浅色/深色自适应）

extension DesignSystem {
    /// 深色 #121212 / 浅色 #F5F5F7。
    static let appBackground = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(calibratedRed: 0x12 / 255, green: 0x12 / 255, blue: 0x12 / 255, alpha: 1)
            : NSColor(calibratedRed: 0xF5 / 255, green: 0xF5 / 255, blue: 0xF7 / 255, alpha: 1)
    })

    /// 侧栏 / 页面背景：深色 #18181B / 浅色 #EFEFF2。
    static let surface1 = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(calibratedRed: 0x18 / 255, green: 0x18 / 255, blue: 0x1B / 255, alpha: 1)
            : NSColor(calibratedRed: 0xFF / 255, green: 0xFF / 255, blue: 0xFF / 255, alpha: 1)
    })

    /// 卡片 / 控件底：深色 #26262B / 浅色 #F2F2F5。
    static let surface2 = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(calibratedRed: 0x26 / 255, green: 0x26 / 255, blue: 0x2B / 255, alpha: 1)
            : NSColor(calibratedRed: 0xF2 / 255, green: 0xF2 / 255, blue: 0xF5 / 255, alpha: 1)
    })

    /// 更亮的表面：深色 #2E2E33 / 浅色 #E8E8ED。
    static let surface3 = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(calibratedRed: 0x2E / 255, green: 0x2E / 255, blue: 0x33 / 255, alpha: 1)
            : NSColor(calibratedRed: 0xE8 / 255, green: 0xE8 / 255, blue: 0xED / 255, alpha: 1)
    })

    static let stageBarActive = brand
    static let stageBarInactive = Color(nsColor: .tertiaryLabelColor)
}

/// 页面卡片：标题栏 + 内容，风格接近 Windows 的 SectionCard。
struct SectionCard<Content: View, Trailing: View>: View {
    let title: String
    let systemImage: String
    var subtitle: String?
    @ViewBuilder var content: () -> Content
    @ViewBuilder var trailing: () -> Trailing

    init(
        _ title: String,
        systemImage: String,
        subtitle: String? = nil,
        @ViewBuilder content: @escaping () -> Content,
        @ViewBuilder trailing: @escaping () -> Trailing = { EmptyView() }
    ) {
        self.title = title
        self.systemImage = systemImage
        self.subtitle = subtitle
        self.content = content
        self.trailing = trailing
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
                Image(systemName: systemImage)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(DesignSystem.accent(.light))
                    .frame(width: 16)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.system(size: 13, weight: .semibold))
                    if let subtitle, !subtitle.isEmpty {
                        Text(subtitle)
                            .font(Typography.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                Spacer(minLength: Spacing.s)
                trailing()
            }
            content()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface(padding: Spacing.l)
    }
}

/// 工作流分段控件（设计投影/成品读回、内圈/外圈等）。
struct SegmentedControl<Value: Hashable>: View {
    struct Item: Identifiable {
        let id: Value
        let title: String
        var systemImage: String?
    }

    let items: [Item]
    @Binding var selection: Value

    var body: some View {
        HStack(spacing: 2) {
            ForEach(items) { item in
                let selected = item.id == selection
                Button {
                    selection = item.id
                } label: {
                    HStack(spacing: 5) {
                        if let image = item.systemImage {
                            Image(systemName: image)
                                .font(.system(size: 10, weight: .medium))
                        }
                        Text(item.title)
                            .font(.system(size: 11.5, weight: selected ? .semibold : .regular))
                    }
                    .foregroundStyle(selected ? Color.white : Color.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 5)
                    .background(selected ? DesignSystem.brand : Color.clear, in: Capsule())
                    .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(selected ? .isSelected : [])
            }
        }
        .padding(2)
        .background(DesignSystem.surface2, in: Capsule())
    }
}

/// 表单小标签。
struct FieldLabel: View {
    let text: String

    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.system(size: 10.5, weight: .medium))
            .foregroundStyle(.secondary)
    }
}

/// 单行路径字段：只读文本 + 选择按钮。
struct CompactPathField: View {
    let label: String
    let path: String
    var placeholder: String = "未选择"
    var buttonTitle: String = "选择…"
    var onBrowse: () -> Void
    var onClear: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            FieldLabel(label)
            HStack(spacing: Spacing.xs) {
                Text(path.isEmpty ? placeholder : Formatting.abbreviatedPath(path))
                    .font(Typography.monoSmall)
                    .foregroundStyle(path.isEmpty ? .tertiary : .primary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 7)
                    .padding(.vertical, 5)
                    .background(DesignSystem.surface2, in: RoundedRectangle(cornerRadius: 6))
                Button(buttonTitle, action: onBrowse)
                    .controlSize(.small)
                if let onClear, !path.isEmpty {
                    Button {
                        onClear()
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.tertiary)
                    }
                    .buttonStyle(.borderless)
                    .controlSize(.small)
                }
            }
        }
    }
}
