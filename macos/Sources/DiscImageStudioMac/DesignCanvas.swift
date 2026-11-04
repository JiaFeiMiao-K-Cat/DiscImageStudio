import AppKit
import SwiftUI
import DiscImageStudioKit

/// 毫米坐标画布：盘体轮廓 / 中心孔 / 图层拖动 / 选框控制点 / 平移缩放。
struct DiscCanvas: View {
    let project: ProjectModel

    @State private var zoom: CGFloat = 1
    @State private var pan: CGSize = .zero
    @State private var dragMode: DragMode = .none
    @State private var magnifyBaseZoom: CGFloat?

    private enum DragMode {
        case none
        case pan(start: CGSize)
        case move(id: String, startX: Double, startY: Double, startMm: CGPoint)
        case resize(id: String, startScale: Double, startDistance: CGFloat)
        case rotate(id: String, startRotation: Double, startAngle: Double)
    }

    var body: some View {
        let layers = project.layers
        let selectedID = project.selectedLayerID
        let radius = project.canvasOuterRadiusMm
        let innerRadius = project.document.geometry.innerRadiusMm

        VStack(spacing: 0) {
            GeometryReader { geometry in
                let footerHeight: CGFloat = 38
                let area = CGRect(
                    x: 8,
                    y: 8,
                    width: max(10, geometry.size.width - 16),
                    height: max(10, geometry.size.height - footerHeight - 16)
                )
                let square = min(area.width, area.height)
                let center = CGPoint(
                    x: area.midX + pan.width,
                    y: area.midY + pan.height
                )
                let pxPerMm = (square / 2) / CGFloat(radius) * zoom

                Canvas { context, size in
                    drawBackground(context: &context, size: size)
                    drawDiscBody(
                        context: &context,
                        center: center,
                        pxPerMm: pxPerMm,
                        outerRadiusMm: CGFloat(radius)
                    )
                    drawLayers(
                        context: &context,
                        layers: layers,
                        center: center,
                        pxPerMm: pxPerMm,
                        canvasOuterRadiusMm: CGFloat(radius)
                    )
                    drawDiscForeground(
                        context: &context,
                        center: center,
                        pxPerMm: pxPerMm,
                        innerRadiusMm: innerRadius,
                        outerRadiusMm: CGFloat(radius)
                    )
                    if let selectedID,
                       let layer = layers.first(where: { $0.id == selectedID }) {
                        drawSelection(
                            context: &context,
                            layer: layer,
                            center: center,
                            pxPerMm: pxPerMm,
                            canvasOuterRadiusMm: CGFloat(radius)
                        )
                    }
                }
                .contentShape(Rectangle())
                .gesture(
                    DragGesture(minimumDistance: 0, coordinateSpace: .local)
                        .onChanged { value in
                            handleDrag(
                                value,
                                center: center,
                                pxPerMm: pxPerMm,
                                radius: CGFloat(radius),
                                layers: layers
                            )
                        }
                        .onEnded { _ in
                            dragMode = .none
                        }
                )
                .simultaneousGesture(
                    MagnificationGesture()
                        .onChanged { value in
                            if magnifyBaseZoom == nil { magnifyBaseZoom = zoom }
                            zoom = min(max((magnifyBaseZoom ?? 1) * value, 0.25), 4)
                        }
                        .onEnded { _ in
                            magnifyBaseZoom = nil
                        }
                )
            }

            Divider().overlay(Color.white.opacity(0.08))

            HStack(spacing: 8) {
                Button {
                    zoom = max(0.25, zoom - 0.1)
                } label: {
                    Image(systemName: "minus")
                }
                .buttonStyle(.borderless)
                .help(L("缩小"))

                Text("\(Int((zoom * 100).rounded()))%")
                    .font(.system(size: 10.5, design: .monospaced))
                    .foregroundStyle(.secondary)
                    .frame(width: 44)

                Button {
                    zoom = min(4, zoom + 0.1)
                } label: {
                    Image(systemName: "plus")
                }
                .buttonStyle(.borderless)
                .help(L("放大"))

                Button {
                    zoom = 1
                    pan = .zero
                } label: {
                    Image(systemName: "arrow.counterclockwise")
                }
                .buttonStyle(.borderless)
                .help(L("适应 / 重置视图"))

                Spacer(minLength: 0)

                Text("拖动空白平移 · 拖动图层移动 · 角点缩放")
                    .font(Typography.caption2)
                    .foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 12)
            .frame(height: 38)
            .buttonStyle(.borderless)
        }
        .background(Color(red: 0.035, green: 0.035, blue: 0.043))
        .clipShape(RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: DesignSystem.cardRadius, style: .continuous)
                .strokeBorder(Color.white.opacity(0.09), lineWidth: 1)
        }
    }

    // MARK: - 绘制

    private func drawBackground(context: inout GraphicsContext, size: CGSize) {
        context.fill(Path(CGRect(origin: .zero, size: size)), with: .color(Color(red: 0.035, green: 0.035, blue: 0.043)))
    }

    private func drawDiscBody(
        context: inout GraphicsContext,
        center: CGPoint,
        pxPerMm: CGFloat,
        outerRadiusMm: CGFloat
    ) {
        let discRect = CGRect(
            x: center.x - outerRadiusMm * pxPerMm,
            y: center.y - outerRadiusMm * pxPerMm,
            width: outerRadiusMm * 2 * pxPerMm,
            height: outerRadiusMm * 2 * pxPerMm
        )
        context.fill(Path(ellipseIn: discRect), with: .color(Color.white.opacity(0.035)))
    }

    /// 盘体轮廓与中心孔画在图层之上，保证任何设计图都不会把它藏起来。
    private func drawDiscForeground(
        context: inout GraphicsContext,
        center: CGPoint,
        pxPerMm: CGFloat,
        innerRadiusMm: Double,
        outerRadiusMm: CGFloat
    ) {
        let discRect = CGRect(
            x: center.x - outerRadiusMm * pxPerMm,
            y: center.y - outerRadiusMm * pxPerMm,
            width: outerRadiusMm * 2 * pxPerMm,
            height: outerRadiusMm * 2 * pxPerMm
        )
        context.stroke(Path(ellipseIn: discRect), with: .color(Color.white.opacity(0.28)), lineWidth: 1.2)

        let dataRing = CGRect(
            x: center.x - CGFloat(innerRadiusMm) * pxPerMm,
            y: center.y - CGFloat(innerRadiusMm) * pxPerMm,
            width: CGFloat(innerRadiusMm) * 2 * pxPerMm,
            height: CGFloat(innerRadiusMm) * 2 * pxPerMm
        )
        context.stroke(
            Path(ellipseIn: dataRing),
            with: .color(Color.white.opacity(0.22)),
            style: StrokeStyle(lineWidth: 1, dash: [4, 4])
        )

        let holeRadius: CGFloat = 15 * pxPerMm
        let holeRect = CGRect(
            x: center.x - holeRadius,
            y: center.y - holeRadius,
            width: holeRadius * 2,
            height: holeRadius * 2
        )
        context.fill(Path(ellipseIn: holeRect), with: .color(Color.black))
        context.stroke(Path(ellipseIn: holeRect), with: .color(Color.white.opacity(0.3)), lineWidth: 1)
    }

    private func drawLayers(
        context: inout GraphicsContext,
        layers: [DesignLayer],
        center: CGPoint,
        pxPerMm: CGFloat,
        canvasOuterRadiusMm: CGFloat
    ) {
        for layer in layers.reversed() where layer.visible {
            guard let image = DesignImageCache.shared.image(layer.imagePath),
                  let pixelSize = DesignComposer.imagePixelSize(layer.imagePath) else { continue }
            let fitted = DesignComposer.fittedSizeMm(
                pixelWidth: pixelSize.width,
                pixelHeight: pixelSize.height,
                canvasOuterRadiusMm: Double(canvasOuterRadiusMm)
            )
            let width = CGFloat(fitted.widthMm * layer.scale) * pxPerMm
            let height = CGFloat(fitted.heightMm * layer.scale) * pxPerMm
            guard width > 0, height > 0 else { continue }

            var layerContext = context
            layerContext.translateBy(
                x: center.x + CGFloat(layer.translateXMm) * pxPerMm,
                y: center.y + CGFloat(layer.translateYMm) * pxPerMm
            )
            layerContext.rotate(by: .degrees(layer.rotationDeg))
            layerContext.draw(
                Image(nsImage: image),
                in: CGRect(x: -width / 2, y: -height / 2, width: width, height: height)
            )
        }
    }

    private func drawSelection(
        context: inout GraphicsContext,
        layer: DesignLayer,
        center: CGPoint,
        pxPerMm: CGFloat,
        canvasOuterRadiusMm: CGFloat
    ) {
        guard let geometry = selectionGeometry(
            layer: layer,
            center: center,
            pxPerMm: pxPerMm,
            canvasOuterRadiusMm: canvasOuterRadiusMm
        ) else { return }

        var path = Path()
        path.move(to: geometry.corners[0])
        for corner in geometry.corners.dropFirst() {
            path.addLine(to: corner)
        }
        path.closeSubpath()
        context.stroke(path, with: .color(DesignSystem.brand), lineWidth: 1.2)

        for corner in geometry.corners {
            let rect = CGRect(x: corner.x - 3.5, y: corner.y - 3.5, width: 7, height: 7)
            context.fill(Path(rect), with: .color(.white))
            context.stroke(Path(rect), with: .color(DesignSystem.brand), lineWidth: 1)
        }

        var handleLine = Path()
        handleLine.move(to: CGPoint(x: geometry.corners[0].x, y: geometry.corners[0].y))
        context.stroke(handleLine, with: .color(DesignSystem.brand), lineWidth: 1)

        var rotationLine = Path()
        let topMid = CGPoint(
            x: (geometry.corners[0].x + geometry.corners[1].x) / 2,
            y: (geometry.corners[0].y + geometry.corners[1].y) / 2
        )
        rotationLine.move(to: topMid)
        rotationLine.addLine(to: geometry.rotationHandle)
        context.stroke(rotationLine, with: .color(DesignSystem.brand.opacity(0.8)), lineWidth: 1)
        context.fill(
            Path(ellipseIn: CGRect(
                x: geometry.rotationHandle.x - 4,
                y: geometry.rotationHandle.y - 4,
                width: 8,
                height: 8
            )),
            with: .color(DesignSystem.brand)
        )
    }

    // MARK: - 交互

    private func handleDrag(
        _ value: DragGesture.Value,
        center: CGPoint,
        pxPerMm: CGFloat,
        radius: CGFloat,
        layers: [DesignLayer]
    ) {
        if case .none = dragMode {
            dragMode = makeDragMode(
                start: value.startLocation,
                center: center,
                pxPerMm: pxPerMm,
                radius: radius
            )
        }

        switch dragMode {
        case .none:
            break
        case .pan(let start):
            pan = CGSize(
                width: start.width + value.translation.width,
                height: start.height + value.translation.height
            )
        case .move(let id, let startX, let startY, let startMm):
            let currentMm = mmPoint(
                value.location,
                center: center,
                pxPerMm: pxPerMm
            )
            project.mutateLayer(id: id) {
                $0.translateXMm = startX + Double(currentMm.x - startMm.x)
                $0.translateYMm = startY + Double(currentMm.y - startMm.y)
            }
        case .resize(let id, let startScale, let startDistance):
            let layerCenter = layerCenterPoint(id: id, center: center, pxPerMm: pxPerMm)
            let distance = distance(value.location, layerCenter)
            guard startDistance > 1 else { break }
            let scale = min(max(startScale * Double(distance / startDistance), 0.05), 20)
            project.mutateLayer(id: id) { $0.scale = scale }
        case .rotate(let id, let startRotation, let startAngle):
            let layerCenter = layerCenterPoint(id: id, center: center, pxPerMm: pxPerMm)
            let angle = atan2(
                Double(value.location.y - layerCenter.y),
                Double(value.location.x - layerCenter.x)
            ) * 180 / .pi
            project.mutateLayer(id: id) { $0.rotationDeg = startRotation + (angle - startAngle) }
        }
    }

    private func makeDragMode(
        start: CGPoint,
        center: CGPoint,
        pxPerMm: CGFloat,
        radius: CGFloat
    ) -> DragMode {
        if let selected = project.selectedLayer,
           let selection = selectionGeometry(
                layer: selected,
                center: center,
                pxPerMm: pxPerMm,
                canvasOuterRadiusMm: radius
           ) {
            if distance(start, selection.rotationHandle) <= 10 {
                let layerCenter = layerCenterPoint(id: selected.id, center: center, pxPerMm: pxPerMm)
                let angle = atan2(
                    Double(start.y - layerCenter.y),
                    Double(start.x - layerCenter.x)
                ) * 180 / .pi
                return .rotate(id: selected.id, startRotation: selected.rotationDeg, startAngle: angle)
            }
            if let corner = selection.corners.first(where: { distance(start, $0) <= 10 }) {
                let layerCenter = layerCenterPoint(id: selected.id, center: center, pxPerMm: pxPerMm)
                _ = corner
                return .resize(
                    id: selected.id,
                    startScale: selected.scale,
                    startDistance: max(distance(start, layerCenter), 1)
                )
            }
        }

        if let hit = hitTest(start, center: center, pxPerMm: pxPerMm, radius: radius) {
            project.selectLayer(hit.id)
            return .move(
                id: hit.id,
                startX: hit.translateXMm,
                startY: hit.translateYMm,
                startMm: mmPoint(start, center: center, pxPerMm: pxPerMm)
            )
        }

        project.selectLayer(nil)
        return .pan(start: pan)
    }

    private func hitTest(
        _ point: CGPoint,
        center: CGPoint,
        pxPerMm: CGFloat,
        radius: CGFloat
    ) -> DesignLayer? {
        for layer in project.layers where layer.visible {
            guard let geometry = selectionGeometry(
                layer: layer,
                center: center,
                pxPerMm: pxPerMm,
                canvasOuterRadiusMm: radius
            ) else { continue }
            if distance(point, geometry.center) <= geometry.halfDiagonal {
                let local = rotated(
                    CGPoint(x: point.x - geometry.center.x, y: point.y - geometry.center.y),
                    byDegrees: -layer.rotationDeg
                )
                if abs(local.x) <= geometry.widthPx / 2, abs(local.y) <= geometry.heightPx / 2 {
                    return layer
                }
            }
        }
        return nil
    }

    private struct SelectionGeometry {
        let center: CGPoint
        let corners: [CGPoint]
        let rotationHandle: CGPoint
        let widthPx: CGFloat
        let heightPx: CGFloat
        let halfDiagonal: CGFloat
    }

    private func selectionGeometry(
        layer: DesignLayer,
        center: CGPoint,
        pxPerMm: CGFloat,
        canvasOuterRadiusMm: CGFloat
    ) -> SelectionGeometry? {
        guard let pixelSize = DesignComposer.imagePixelSize(layer.imagePath) else { return nil }
        let fitted = DesignComposer.fittedSizeMm(
            pixelWidth: pixelSize.width,
            pixelHeight: pixelSize.height,
            canvasOuterRadiusMm: Double(canvasOuterRadiusMm)
        )
        let widthPx = CGFloat(fitted.widthMm * layer.scale) * pxPerMm
        let heightPx = CGFloat(fitted.heightMm * layer.scale) * pxPerMm
        guard widthPx > 0, heightPx > 0 else { return nil }
        let layerCenter = CGPoint(
            x: center.x + CGFloat(layer.translateXMm) * pxPerMm,
            y: center.y + CGFloat(layer.translateYMm) * pxPerMm
        )
        let halfW = widthPx / 2
        let halfH = heightPx / 2
        let localCorners = [
            CGPoint(x: -halfW, y: -halfH),
            CGPoint(x: halfW, y: -halfH),
            CGPoint(x: halfW, y: halfH),
            CGPoint(x: -halfW, y: halfH),
        ]
        let corners = localCorners.map { corner in
            let rotatedPoint = rotated(corner, byDegrees: layer.rotationDeg)
            return CGPoint(x: layerCenter.x + rotatedPoint.x, y: layerCenter.y + rotatedPoint.y)
        }
        let rotationLocal = CGPoint(x: 0, y: -halfH - 22)
        let rotationRotated = rotated(rotationLocal, byDegrees: layer.rotationDeg)
        let rotationHandle = CGPoint(
            x: layerCenter.x + rotationRotated.x,
            y: layerCenter.y + rotationRotated.y
        )
        return SelectionGeometry(
            center: layerCenter,
            corners: corners,
            rotationHandle: rotationHandle,
            widthPx: widthPx,
            heightPx: heightPx,
            halfDiagonal: sqrt(halfW * halfW + halfH * halfH)
        )
    }

    private func layerCenterPoint(id: String, center: CGPoint, pxPerMm: CGFloat) -> CGPoint {
        guard let layer = project.layers.first(where: { $0.id == id }) else { return center }
        return CGPoint(
            x: center.x + CGFloat(layer.translateXMm) * pxPerMm,
            y: center.y + CGFloat(layer.translateYMm) * pxPerMm
        )
    }

    private func mmPoint(_ point: CGPoint, center: CGPoint, pxPerMm: CGFloat) -> CGPoint {
        CGPoint(
            x: (point.x - center.x) / pxPerMm,
            y: (point.y - center.y) / pxPerMm
        )
    }

    private func rotated(_ point: CGPoint, byDegrees degrees: Double) -> CGPoint {
        let radians = degrees * Double.pi / 180
        // 显式用 Foundation 的 Double 版本，避免 CGFloat/Double 重载在旧编译器上歧义。
        let cosine = CGFloat(Foundation.cos(radians))
        let sine = CGFloat(Foundation.sin(radians))
        return CGPoint(
            x: point.x * cosine - point.y * sine,
            y: point.x * sine + point.y * cosine
        )
    }

    private func distance(_ a: CGPoint, _ b: CGPoint) -> CGFloat {
        hypot(a.x - b.x, a.y - b.y)
    }
}

/// 画布用的 NSImage 缓存（按路径）。
@MainActor
final class DesignImageCache {
    static let shared = DesignImageCache()
    private var cache: [String: NSImage] = [:]

    func image(_ path: String) -> NSImage? {
        if let cached = cache[path] { return cached }
        guard let image = NSImage(contentsOfFile: path) else { return nil }
        cache[path] = image
        return image
    }

    func invalidate() {
        cache.removeAll()
    }
}
