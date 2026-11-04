import AppKit
import SwiftUI
import DiscImageStudioKit

/// 标定照片 / 矫正图监视器：缩放平移、轮廓控制点、描点与预测折线。
struct CalibrationCanvas: View {
    enum Space: Equatable {
        case imagePixels(width: Int, height: Int)
        case discMm(canvasOuterRadiusMm: Double, imageSize: Int)
    }

    let imageURL: URL?
    let space: Space

    var outerBoundary: [CalibrationPoint] = []
    var holeBoundary: [CalibrationPoint] = []
    var tracePoints: [CalibrationPoint] = []
    var predicted: [CalibrationPoint] = []
    var outlierIndices: Set<Int> = []
    var selectedTraceIndex: Int?
    var isPhotoEditable = false
    var isTraceEditable = false

    var onMoveOuter: ((Int, CalibrationPoint) -> Void)?
    var onMoveHole: ((Int, CalibrationPoint) -> Void)?
    var onSelectTrace: ((Int?) -> Void)?
    var onMoveTrace: ((Int, CalibrationPoint) -> Void)?
    var onAddTrace: ((CalibrationPoint) -> Void)?
    var onInteractionEnded: (() -> Void)?

    @State private var zoom: CGFloat = 1
    @State private var pan: CGSize = .zero
    @State private var image: NSImage?
    @State private var drag: DragMode = .none
    @State private var magnifyBaseZoom: CGFloat?

    private enum DragMode {
        case none
        case pan(start: CGSize)
        case boundary(kind: Kind, index: Int)
        case trace(index: Int)
    }

    private enum Kind {
        case outer
        case hole
    }

    var body: some View {
        VStack(spacing: 0) {
            GeometryReader { geometry in
                let viewSize = geometry.size
                let mapping = CanvasMapping(
                    viewSize: viewSize,
                    imageSize: imagePixelSize,
                    space: space,
                    zoom: zoom,
                    pan: pan
                )
                Canvas { context, size in
                    context.fill(Path(CGRect(origin: .zero, size: size)), with: .color(Color(red: 0.035, green: 0.035, blue: 0.043)))
                    if let image {
                        context.draw(Image(nsImage: image), in: mapping.imageRect)
                    } else {
                        context.draw(
                            Text("尚未载入图片")
                                .font(.system(size: 12))
                                .foregroundStyle(Color.secondary),
                            at: CGPoint(x: size.width / 2, y: size.height / 2)
                        )
                    }
                    drawBoundary(context: &context, mapping: mapping)
                    drawPrediction(context: &context, mapping: mapping)
                    drawTracePoints(context: &context, mapping: mapping)
                }
                .contentShape(Rectangle())
                .gesture(
                    DragGesture(minimumDistance: 0, coordinateSpace: .local)
                        .onChanged { value in
                            handleChanged(value, mapping: mapping)
                        }
                        .onEnded { value in
                            handleEnded(value, mapping: mapping)
                        }
                )
                .simultaneousGesture(
                    MagnificationGesture()
                        .onChanged { value in
                            if magnifyBaseZoom == nil { magnifyBaseZoom = zoom }
                            zoom = min(max((magnifyBaseZoom ?? 1) * value, 0.3), 6)
                        }
                        .onEnded { _ in magnifyBaseZoom = nil }
                )
            }

            Divider().overlay(Color.white.opacity(0.08))

            HStack(spacing: 8) {
                Button {
                    zoom = max(0.3, zoom - 0.1)
                } label: { Image(systemName: "minus") }
                .buttonStyle(.borderless)
                .help(L("缩小"))

                Text("\(Int((zoom * 100).rounded()))%")
                    .font(.system(size: 10.5, design: .monospaced))
                    .foregroundStyle(.secondary)
                    .frame(width: 44)

                Button {
                    zoom = min(6, zoom + 0.1)
                } label: { Image(systemName: "plus") }
                .buttonStyle(.borderless)
                .help(L("放大"))

                Button {
                    zoom = 1
                    pan = .zero
                } label: { Image(systemName: "arrow.counterclockwise") }
                .buttonStyle(.borderless)
                .help(L("适应窗口 / 重置视图"))

                Spacer(minLength: 0)

                Text(hintText)
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
            }
            .padding(.horizontal, 12)
            .frame(height: 38)
        }
        .background(Color(red: 0.035, green: 0.035, blue: 0.043))
        .clipShape(RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                .strokeBorder(Color.white.opacity(0.08), lineWidth: 1)
        }
        .task(id: imageURL) {
            loadImage()
        }
        .onAppear {
            loadImage()
        }
        .onChange(of: imageURL) { _, _ in
            zoom = 1
            pan = .zero
            loadImage()
        }
    }

    // MARK: - 计算

    private func loadImage() {
        guard let imageURL else {
            image = nil
            return
        }
        if let loaded = NSImage(contentsOf: imageURL), loaded.size.width > 0 {
            image = loaded
        }
    }

    private var imagePixelSize: CGSize {
        switch space {
        case .imagePixels(let width, let height):
            return CGSize(width: max(width, 1), height: max(height, 1))
        case .discMm(_, let imageSize):
            return CGSize(width: max(imageSize, 1), height: max(imageSize, 1))
        }
    }

    private var hintText: String {
        if isPhotoEditable {
            return L("拖动控制点对齐盘缘与中心孔 · 拖动空白平移")
        }
        if isTraceEditable {
            return L("点击空白补点 · 拖动点修正 · Delete 删除选中点")
        }
        return L("拖动画布平移 · 滚轮/双指缩放")
    }

    // MARK: - 绘制

    private func drawBoundary(context: inout GraphicsContext, mapping: CanvasMapping) {
        drawPoints(
            context: &context,
            mapping: mapping,
            points: outerBoundary,
            color: DesignSystem.brandEmphasis,
            radius: 5
        )
        drawPoints(
            context: &context,
            mapping: mapping,
            points: holeBoundary,
            color: Color(red: 0.96, green: 0.65, blue: 0.14),
            radius: 5
        )
    }

    private func drawPoints(
        context: inout GraphicsContext,
        mapping: CanvasMapping,
        points: [CalibrationPoint],
        color: Color,
        radius: CGFloat
    ) {
        for point in points {
            let viewPoint = mapping.viewPoint(for: point)
            let rect = CGRect(
                x: viewPoint.x - radius,
                y: viewPoint.y - radius,
                width: radius * 2,
                height: radius * 2
            )
            context.fill(Path(ellipseIn: rect), with: .color(color))
            context.stroke(Path(ellipseIn: rect), with: .color(.black.opacity(0.5)), lineWidth: 1)
        }
    }

    private func drawPrediction(context: inout GraphicsContext, mapping: CanvasMapping) {
        guard predicted.count >= 2 else { return }
        var path = Path()
        let first = mapping.viewPoint(forMm: predicted[0])
        path.move(to: first)
        for point in predicted.dropFirst() {
            path.addLine(to: mapping.viewPoint(forMm: point))
        }
        context.stroke(
            path,
            with: .color(Color(red: 1.0, green: 0.66, blue: 0.30)),
            style: StrokeStyle(lineWidth: 1.6, lineCap: .round, lineJoin: .round)
        )
    }

    private func drawTracePoints(context: inout GraphicsContext, mapping: CanvasMapping) {
        for (index, point) in tracePoints.enumerated() {
            let viewPoint = mapping.viewPoint(forMm: point)
            let outlier = outlierIndices.contains(index)
            let color = outlier
                ? Color.red
                : Color(red: 0.34, green: 0.78, blue: 1.0)
            let radius: CGFloat = selectedTraceIndex == index ? 7 : 5
            let rect = CGRect(
                x: viewPoint.x - radius,
                y: viewPoint.y - radius,
                width: radius * 2,
                height: radius * 2
            )
            context.fill(Path(ellipseIn: rect), with: .color(color))
            context.stroke(
                Path(ellipseIn: rect),
                with: .color(selectedTraceIndex == index ? .white : .black.opacity(0.55)),
                lineWidth: selectedTraceIndex == index ? 1.8 : 1
            )
            context.draw(
                Text("\(index + 1)")
                    .font(.system(size: 9, weight: .semibold, design: .monospaced))
                    .foregroundStyle(Color.white.opacity(0.9)),
                at: CGPoint(x: viewPoint.x + 10, y: viewPoint.y - 9)
            )
        }
    }

    // MARK: - 交互

    private func handleChanged(_ value: DragGesture.Value, mapping: CanvasMapping) {
        if case .none = drag {
            drag = beginDrag(at: value.startLocation, mapping: mapping)
        }
        switch drag {
        case .none:
            break
        case .pan(let start):
            pan = CGSize(
                width: start.width + value.translation.width,
                height: start.height + value.translation.height
            )
        case .boundary(let kind, let index):
            let point = mapping.calibrationPoint(from: value.location)
            switch kind {
            case .outer: onMoveOuter?(index, point)
            case .hole: onMoveHole?(index, point)
            }
        case .trace(let index):
            let point = mapping.calibrationPoint(from: value.location)
            onMoveTrace?(index, point)
        }
    }

    private func handleEnded(_ value: DragGesture.Value, mapping: CanvasMapping) {
        let wasPan: Bool
        if case .pan = drag { wasPan = true } else { wasPan = false }
        if isTraceEditable, !wasPan,
           case .none = drag,
           value.translation.width.magnitude < 4,
           value.translation.height.magnitude < 4 {
            onAddTrace?(mapping.calibrationPoint(from: value.startLocation))
        }
        if isTraceEditable, wasPan,
           value.translation.width.magnitude < 4,
           value.translation.height.magnitude < 4 {
            onAddTrace?(mapping.calibrationPoint(from: value.startLocation))
        }
        drag = .none
        onInteractionEnded?()
    }

    private func beginDrag(at location: CGPoint, mapping: CanvasMapping) -> DragMode {
        if isPhotoEditable {
            if let index = nearestBoundaryIndex(location, mapping: mapping, points: outerBoundary) {
                return .boundary(kind: .outer, index: index)
            }
            if let index = nearestBoundaryIndex(location, mapping: mapping, points: holeBoundary) {
                return .boundary(kind: .hole, index: index)
            }
        }
        if isTraceEditable {
            if let index = nearestTraceIndex(location, mapping: mapping) {
                onSelectTrace?(index)
                return .trace(index: index)
            }
            onSelectTrace?(nil)
        }
        return .pan(start: pan)
    }

    private func nearestBoundaryIndex(
        _ location: CGPoint,
        mapping: CanvasMapping,
        points: [CalibrationPoint]
    ) -> Int? {
        for (index, point) in points.enumerated() {
            if distance(location, mapping.viewPoint(for: point)) <= 12 {
                return index
            }
        }
        return nil
    }

    private func nearestTraceIndex(_ location: CGPoint, mapping: CanvasMapping) -> Int? {
        var best: (index: Int, distance: CGFloat)?
        for (index, point) in tracePoints.enumerated() {
            let distance = distance(location, mapping.viewPoint(forMm: point))
            guard distance <= 12 else { continue }
            if best == nil || distance < best!.distance {
                best = (index, distance)
            }
        }
        return best?.index
    }

    private func distance(_ a: CGPoint, _ b: CGPoint) -> CGFloat {
        hypot(a.x - b.x, a.y - b.y)
    }
}

// MARK: - 坐标映射

private struct CanvasMapping {
    let viewSize: CGSize
    let imageSize: CGSize
    let space: CalibrationCanvas.Space
    let zoom: CGFloat
    let pan: CGSize

    var scale: CGFloat {
        min(viewSize.width / imageSize.width, viewSize.height / imageSize.height) * zoom
    }

    var imageRect: CGRect {
        let width = imageSize.width * scale
        let height = imageSize.height * scale
        return CGRect(
            x: (viewSize.width - width) / 2 + pan.width,
            y: (viewSize.height - height) / 2 + pan.height,
            width: width,
            height: height
        )
    }

    /// 照片/毫米 → 图像像素坐标。
    func imagePoint(for calibrationPoint: CalibrationPoint) -> CGPoint {
        switch space {
        case .imagePixels:
            return CGPoint(x: calibrationPoint.x, y: calibrationPoint.y)
        case .discMm(let outerRadiusMm, let imageSize):
            let size = CGFloat(imageSize)
            let half = size / 2
            let radius = max(CGFloat(outerRadiusMm), 1)
            return CGPoint(
                x: half + CGFloat(calibrationPoint.x) / radius * half,
                y: half + CGFloat(calibrationPoint.y) / radius * half
            )
        }
    }

    /// 图像像素 → 毫米；照片模式下也返回像素坐标。
    func calibrationPoint(from viewPoint: CGPoint) -> CalibrationPoint {
        let imagePoint = CGPoint(
            x: (viewPoint.x - imageRect.minX) / scale,
            y: (viewPoint.y - imageRect.minY) / scale
        )
        switch space {
        case .imagePixels:
            return CalibrationPoint(x: Double(imagePoint.x), y: Double(imagePoint.y))
        case .discMm(let outerRadiusMm, let imageSize):
            let half = CGFloat(imageSize) / 2
            let radius = max(CGFloat(outerRadiusMm), 1)
            return CalibrationPoint(
                x: Double((imagePoint.x - half) / half * radius),
                y: Double((imagePoint.y - half) / half * radius)
            )
        }
    }

    func viewPoint(for calibrationPoint: CalibrationPoint) -> CGPoint {
        viewPoint(forImagePoint: imagePoint(for: calibrationPoint))
    }

    /// 描点/预测折线使用毫米坐标；照片模式下不会被调用。
    func viewPoint(forMm point: CalibrationPoint) -> CGPoint {
        viewPoint(forImagePoint: imagePoint(for: point))
    }

    private func viewPoint(forImagePoint imagePoint: CGPoint) -> CGPoint {
        CGPoint(
            x: imageRect.minX + imagePoint.x * scale,
            y: imageRect.minY + imagePoint.y * scale
        )
    }
}
