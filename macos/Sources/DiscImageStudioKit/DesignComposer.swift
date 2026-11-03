import CoreGraphics
import Foundation
import ImageIO

/// 把图层栈合成为生成管线消费的正方形源图：中心即盘心，边长 = 2 × 画布外半径。
/// 与 Windows 的 `DesignComposer` 保持同一套规则（较大边跨内容环直径、mm 坐标、顺时针旋转）。
public struct DesignComposer: Sendable {
    public static let previewSize = 2048
    public static let exportSize = 8192

    public init() {}

    /// 图层在缩放 1.0 时的物理尺寸：较大边等于 `2 × canvasOuterRadiusMm`。
    public static func fittedSizeMm(
        pixelWidth: Int,
        pixelHeight: Int,
        canvasOuterRadiusMm: Double
    ) -> (widthMm: Double, heightMm: Double) {
        guard pixelWidth > 0, pixelHeight > 0 else { return (0, 0) }
        let diameter = canvasOuterRadiusMm * 2
        let width = Double(pixelWidth)
        let height = Double(pixelHeight)
        if width >= height {
            return (diameter, diameter * height / width)
        }
        return (diameter * width / height, diameter)
    }

    /// 自上而下渲染（数组第 0 项是最上层），透明背景。
    public func renderPNGData(
        layers: [DesignLayer],
        canvasOuterRadiusMm: Double,
        outputSize: Int = DesignComposer.exportSize
    ) throws -> Data {
        guard canvasOuterRadiusMm > 0 else {
            throw DesignComposerError.invalidCanvas
        }
        let size = max(64, outputSize)
        guard let context = CGContext(
            data: nil,
            width: size,
            height: size,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            throw DesignComposerError.contextUnavailable
        }
        context.interpolationQuality = .high
        let pixelsPerMm = Double(size) / (canvasOuterRadiusMm * 2)
        let center = Double(size) / 2

        // 数组索引 0 是最上层；绘制自底向上。
        for layer in layers.reversed() where layer.visible {
            guard let image = DesignComposer.loadImage(layer.imagePath) else { continue }
            let base = DesignComposer.fittedSizeMm(
                pixelWidth: image.width,
                pixelHeight: image.height,
                canvasOuterRadiusMm: canvasOuterRadiusMm
            )
            guard base.widthMm > 0, base.heightMm > 0, layer.scale > 0 else { continue }
            let widthPixels = base.widthMm * layer.scale * pixelsPerMm
            let heightPixels = base.heightMm * layer.scale * pixelsPerMm
            guard widthPixels > 0, heightPixels > 0 else { continue }

            context.saveGState()
            context.translateBy(
                x: center + layer.translateXMm * pixelsPerMm,
                y: center - layer.translateYMm * pixelsPerMm
            )
            // mm 坐标 +y 向下；CoreGraphics 的 +y 向上，因此顺时针旋转取负角度。
            context.rotate(by: -layer.rotationDeg * .pi / 180)
            context.draw(image, in: CGRect(
                x: -widthPixels / 2,
                y: -heightPixels / 2,
                width: widthPixels,
                height: heightPixels
            ))
            context.restoreGState()
        }

        guard let rendered = context.makeImage() else {
            throw DesignComposerError.renderFailed
        }
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            data as CFMutableData,
            "public.png" as CFString,
            1,
            nil
        ) else {
            throw DesignComposerError.encodeFailed
        }
        CGImageDestinationAddImage(destination, rendered, nil)
        guard CGImageDestinationFinalize(destination) else {
            throw DesignComposerError.encodeFailed
        }
        return data as Data
    }

    public func render(
        layers: [DesignLayer],
        canvasOuterRadiusMm: Double,
        outputSize: Int = DesignComposer.exportSize,
        to destination: URL
    ) throws {
        let data = try renderPNGData(
            layers: layers,
            canvasOuterRadiusMm: canvasOuterRadiusMm,
            outputSize: outputSize
        )
        try FileManager.default.createDirectory(
            at: destination.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try data.write(to: destination, options: .atomic)
    }

    /// 读取图片尺寸（不解码整幅）。失败返回 nil。
    public static func imagePixelSize(_ path: String) -> (width: Int, height: Int)? {
        guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0
        else { return nil }
        return (width, height)
    }

    private static func loadImage(_ path: String) -> CGImage? {
        let url = URL(fileURLWithPath: path)
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceThumbnailMaxPixelSize: 8192,
            kCGImageSourceCreateThumbnailWithTransform: true,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
            ?? CGImageSourceCreateImageAtIndex(source, 0, nil)
    }
}

public enum DesignComposerError: Error, Sendable, LocalizedError, Equatable {
    case invalidCanvas
    case contextUnavailable
    case renderFailed
    case encodeFailed

    public var errorDescription: String? {
        switch self {
        case .invalidCanvas:
            return "画布外半径必须大于 0。"
        case .contextUnavailable:
            return "无法创建图像上下文。"
        case .renderFailed:
            return "无法渲染合成图。"
        case .encodeFailed:
            return "无法写出 PNG。"
        }
    }
}
