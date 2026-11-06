import CoreGraphics
import Foundation
import ImageIO
import XCTest

import DiscImageStudioKit

/// 钉住设计图层合成规则（WORKFLOW.md §3：mm 坐标、+y 向下、透明背景、fit 规则）。
final class DesignComposerTests: XCTestCase {

    private var temporaryDirectory: URL!
    private let composer = DesignComposer()

    override func setUpWithError() throws {
        temporaryDirectory = try TestSupport.makeTemporaryDirectory("DesignComposerTests")
    }

    override func tearDownWithError() throws {
        if let temporaryDirectory {
            try? FileManager.default.removeItem(at: temporaryDirectory)
        }
        temporaryDirectory = nil
    }

    // MARK: - fit 规则

    func testFittedSizeUsesLongerEdgeAsCanvasDiameter() {
        let square = DesignComposer.fittedSizeMm(
            pixelWidth: 100,
            pixelHeight: 100,
            canvasOuterRadiusMm: 10
        )
        XCTAssertEqual(square.widthMm, 20, accuracy: 1e-9)
        XCTAssertEqual(square.heightMm, 20, accuracy: 1e-9)

        let wide = DesignComposer.fittedSizeMm(
            pixelWidth: 200,
            pixelHeight: 50,
            canvasOuterRadiusMm: 10
        )
        XCTAssertEqual(wide.widthMm, 20, accuracy: 1e-9, "较大边应等于 2×外半径")
        XCTAssertEqual(wide.heightMm, 5, accuracy: 1e-9)

        let tall = DesignComposer.fittedSizeMm(
            pixelWidth: 50,
            pixelHeight: 200,
            canvasOuterRadiusMm: 10
        )
        XCTAssertEqual(tall.widthMm, 5, accuracy: 1e-9)
        XCTAssertEqual(tall.heightMm, 20, accuracy: 1e-9, "较大边（高）应等于 2×外半径")

        let zero = DesignComposer.fittedSizeMm(pixelWidth: 0, pixelHeight: 100, canvasOuterRadiusMm: 10)
        XCTAssertEqual(zero.widthMm, 0)
        XCTAssertEqual(zero.heightMm, 0)
    }

    func testImagePixelSizeReadsGeneratedPNG() throws {
        let url = try makeSolidPNG(name: "wide.png", pixelWidth: 200, pixelHeight: 50, color: .red)
        let size = try XCTUnwrap(DesignComposer.imagePixelSize(url.path))
        XCTAssertEqual(size.width, 200)
        XCTAssertEqual(size.height, 50)
        XCTAssertNil(DesignComposer.imagePixelSize("/tmp/does-not-exist.png"))
    }

    // MARK: - 合成：位置与透明背景

    func testLayerPositionUsesMillimetresWithYPointingDown() throws {
        let url = try makeSolidPNG(name: "block.png", pixelWidth: 100, pixelHeight: 100, color: .red)
        let layer = DesignLayer(
            name: "Red block",
            imagePath: url.path,
            translateXMm: 5,
            translateYMm: 5,
            scale: 0.1
        )

        // canvas 半径 10mm → 直径 20mm；输出 200px → 10 px/mm。
        let data = try composer.renderPNGData(
            layers: [layer],
            canvasOuterRadiusMm: 10,
            outputSize: 200
        )
        let image = try decode(data)
        XCTAssertEqual(image.width, 200)
        XCTAssertEqual(image.height, 200)

        // +x 向右、+y 向下：图块中心落在右下象限 (150,150)。
        XCTAssertTrue(try isRed(image, x: 150, y: 150), "+y 应指向画布下方")
        XCTAssertFalse(try isRed(image, x: 150, y: 50), "+y 不应指向画布上方")
        XCTAssertFalse(try isRed(image, x: 50, y: 150), "+x 应指向画布右方")

        // 画布中心与其余角保持透明。
        XCTAssertTrue(try isTransparent(image, x: 100, y: 100))
        XCTAssertTrue(try isTransparent(image, x: 50, y: 50))
        XCTAssertTrue(try isTransparent(image, x: 199, y: 0))
    }

    func testNegativeTranslationMovesLayerUpLeft() throws {
        let url = try makeSolidPNG(name: "block.png", pixelWidth: 100, pixelHeight: 100, color: .red)
        let layer = DesignLayer(
            name: "Red block",
            imagePath: url.path,
            translateXMm: -5,
            translateYMm: -5,
            scale: 0.1
        )
        let image = try decode(try composer.renderPNGData(
            layers: [layer],
            canvasOuterRadiusMm: 10,
            outputSize: 200
        ))
        XCTAssertTrue(try isRed(image, x: 50, y: 50))
        XCTAssertTrue(try isRed(image, x: 55, y: 55), "图块应覆盖左上象限的 50-55 区域")
        XCTAssertFalse(try isRed(image, x: 150, y: 150))
    }

    func testLayerArrayIndexZeroIsDrawnOnTop() throws {
        let url = try makeSolidPNG(name: "block.png", pixelWidth: 100, pixelHeight: 100, color: .red)
        let top = DesignLayer(name: "top", imagePath: url.path, scale: 0.2)
        let bottomURL = try makeSolidPNG(name: "bottom.png", pixelWidth: 100, pixelHeight: 100, color: .blue)
        let bottom = DesignLayer(name: "bottom", imagePath: bottomURL.path, scale: 0.2)

        let data = try composer.renderPNGData(
            layers: [top, bottom],
            canvasOuterRadiusMm: 10,
            outputSize: 200
        )
        let image = try decode(data)
        XCTAssertTrue(try isRed(image, x: 100, y: 100), "数组索引 0 应是最上层")

        let reversed = try decode(try composer.renderPNGData(
            layers: [bottom, top],
            canvasOuterRadiusMm: 10,
            outputSize: 200
        ))
        XCTAssertTrue(try isBlue(reversed, x: 100, y: 100), "反转数组后蓝色应盖住红色")
    }

    // MARK: - 缩放与旋转

    func testScaleChangesRenderedExtent() throws {
        let url = try makeSolidPNG(name: "wide.png", pixelWidth: 200, pixelHeight: 50, color: .red)
        let full = DesignLayer(name: "full", imagePath: url.path, scale: 1)
        let half = DesignLayer(name: "half", imagePath: url.path, scale: 0.5)

        let fullImage = try decode(try composer.renderPNGData(
            layers: [full],
            canvasOuterRadiusMm: 10,
            outputSize: 200
        ))
        XCTAssertTrue(try isRed(fullImage, x: 160, y: 100), "scale 1 时宽度应铺满 200px")

        let halfImage = try decode(try composer.renderPNGData(
            layers: [half],
            canvasOuterRadiusMm: 10,
            outputSize: 200
        ))
        XCTAssertTrue(try isRed(halfImage, x: 140, y: 100), "scale 0.5 时宽度应缩到 100px")
        XCTAssertTrue(try isTransparent(halfImage, x: 160, y: 100))
    }

    func testRotationSwapsWidthAndHeight() throws {
        let url = try makeSolidPNG(name: "wide.png", pixelWidth: 200, pixelHeight: 50, color: .red)
        let flat = DesignLayer(name: "flat", imagePath: url.path, rotationDeg: 0, scale: 1)
        let rotated = DesignLayer(name: "rotated", imagePath: url.path, rotationDeg: 90, scale: 1)

        let flatImage = try decode(try composer.renderPNGData(
            layers: [flat],
            canvasOuterRadiusMm: 10,
            outputSize: 200
        ))
        XCTAssertTrue(try isRed(flatImage, x: 160, y: 100))
        XCTAssertTrue(try isTransparent(flatImage, x: 100, y: 160))

        let rotatedImage = try decode(try composer.renderPNGData(
            layers: [rotated],
            canvasOuterRadiusMm: 10,
            outputSize: 200
        ))
        XCTAssertTrue(try isRed(rotatedImage, x: 100, y: 160), "旋转 90° 后应变成竖向长条")
        XCTAssertTrue(try isTransparent(rotatedImage, x: 160, y: 100))
    }

    // MARK: - 容错

    func testInvisibleAndUnreadableLayersAreSkipped() throws {
        let validURL = try makeSolidPNG(name: "valid.png", pixelWidth: 100, pixelHeight: 100, color: .red)
        let layers = [
            DesignLayer(name: "missing", imagePath: "/tmp/definitely-missing.png"),
            DesignLayer(name: "hidden", imagePath: validURL.path, visible: false, translateXMm: 5, translateYMm: 5),
            DesignLayer(name: "valid", imagePath: validURL.path, translateXMm: 5, translateYMm: 5, scale: 0.1),
        ]

        let data = try composer.renderPNGData(
            layers: layers,
            canvasOuterRadiusMm: 10,
            outputSize: 200
        )
        let image = try decode(data)
        XCTAssertEqual(image.width, 200)
        XCTAssertTrue(try isRed(image, x: 150, y: 150), "坏路径应被跳过而不影响其他图层")
        XCTAssertTrue(try isTransparent(image, x: 50, y: 50))
    }

    func testAllLayersUnreadableProducesTransparentPNG() throws {
        let data = try composer.renderPNGData(
            layers: [DesignLayer(name: "missing", imagePath: "/tmp/nope.png")],
            canvasOuterRadiusMm: 10,
            outputSize: 128
        )
        let image = try decode(data)
        XCTAssertEqual(image.width, 128)
        XCTAssertTrue(try isTransparent(image, x: 64, y: 64))
    }

    // MARK: - 输出与错误

    func testRenderWritesDecodablePNGToDisk() throws {
        let sourceURL = try makeSolidPNG(name: "block.png", pixelWidth: 100, pixelHeight: 100, color: .red)
        let outputURL = temporaryDirectory
            .appendingPathComponent("nested", isDirectory: true)
            .appendingPathComponent("composite.png")

        try composer.render(
            layers: [DesignLayer(name: "red", imagePath: sourceURL.path, scale: 0.1)],
            canvasOuterRadiusMm: 10,
            outputSize: 256,
            to: outputURL
        )

        XCTAssertTrue(FileManager.default.fileExists(atPath: outputURL.path))
        let data = try Data(contentsOf: outputURL)
        let image = try decode(data)
        XCTAssertEqual(image.width, 256)
        XCTAssertEqual(image.height, 256)
        XCTAssertTrue(try isTransparent(image, x: 0, y: 0))
    }

    func testInvalidCanvasIsRejectedAndOutputSizeIsClamped() throws {
        XCTAssertThrowsError(try composer.renderPNGData(
            layers: [],
            canvasOuterRadiusMm: 0,
            outputSize: 128
        )) { error in
            XCTAssertEqual(error as? DesignComposerError, .invalidCanvas)
        }

        let data = try composer.renderPNGData(layers: [], canvasOuterRadiusMm: 10, outputSize: 1)
        let image = try decode(data)
        XCTAssertEqual(image.width, 64, "输出尺寸下限为 64px")
        XCTAssertEqual(image.height, 64)
    }

    // MARK: - Helpers

    private enum TestColor {
        case red
        case blue

        var cgColor: CGColor {
            switch self {
            case .red: return CGColor(red: 1, green: 0, blue: 0, alpha: 1)
            case .blue: return CGColor(red: 0, green: 0, blue: 1, alpha: 1)
            }
        }
    }

    private func makeSolidPNG(
        name: String,
        pixelWidth: Int,
        pixelHeight: Int,
        color: TestColor
    ) throws -> URL {
        guard let context = CGContext(
            data: nil,
            width: pixelWidth,
            height: pixelHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            throw TestSupportError.missingFixture
        }
        context.setFillColor(color.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: pixelWidth, height: pixelHeight))
        let image = try XCTUnwrap(context.makeImage())

        let data = NSMutableData()
        let destination = try XCTUnwrap(CGImageDestinationCreateWithData(
            data as CFMutableData,
            "public.png" as CFString,
            1,
            nil
        ))
        CGImageDestinationAddImage(destination, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))

        let url = temporaryDirectory.appendingPathComponent(name, isDirectory: false)
        try (data as Data).write(to: url)
        return url
    }

    private struct DecodedImage {
        let width: Int
        let height: Int
        let pixels: [UInt8]
    }

    /// 解码 PNG 到 RGBA（row 0 = PNG 顶行），用于像素级断言。
    private func decode(_ data: Data) throws -> DecodedImage {
        let source = try XCTUnwrap(CGImageSourceCreateWithData(data as CFData, nil))
        let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        let width = image.width
        let height = image.height
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        let context = try XCTUnwrap(CGContext(
            data: &pixels,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return DecodedImage(width: width, height: height, pixels: pixels)
    }

    private func rgba(_ image: DecodedImage, x: Int, y: Int) throws -> (r: UInt8, g: UInt8, b: UInt8, a: UInt8) {
        guard x >= 0, y >= 0, x < image.width, y < image.height else {
            throw TestSupportError.missingFixture
        }
        let offset = (y * image.width + x) * 4
        return (
            image.pixels[offset],
            image.pixels[offset + 1],
            image.pixels[offset + 2],
            image.pixels[offset + 3]
        )
    }

    private func isRed(_ image: DecodedImage, x: Int, y: Int) throws -> Bool {
        let pixel = try rgba(image, x: x, y: y)
        return pixel.r > 200 && pixel.g < 40 && pixel.b < 40 && pixel.a > 200
    }

    private func isBlue(_ image: DecodedImage, x: Int, y: Int) throws -> Bool {
        let pixel = try rgba(image, x: x, y: y)
        return pixel.b > 200 && pixel.r < 40 && pixel.g < 90 && pixel.a > 200
    }

    private func isTransparent(_ image: DecodedImage, x: Int, y: Int) throws -> Bool {
        let pixel = try rgba(image, x: x, y: y)
        return pixel.a < 10
    }
}
