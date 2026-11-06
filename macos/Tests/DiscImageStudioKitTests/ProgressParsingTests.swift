import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住进度行解析契约（DESIGN.md §2.6）。
final class ProgressParsingTests: XCTestCase {

    func testFractionParsesDecimalPercent() throws {
        let fraction = try XCTUnwrap(ProgressParsing.fraction(from: "CD data progress 42.3%"))
        XCTAssertEqual(fraction, 0.423, accuracy: 0.000_001)
    }

    func testFractionParsesIntegerPercentWithSpace() throws {
        let fraction = try XCTUnwrap(ProgressParsing.fraction(from: "DVD data progress 42 %"))
        XCTAssertEqual(fraction, 0.42, accuracy: 0.000_001)
    }

    func testFractionUsesLastPercentageOnLine() throws {
        let fraction = try XCTUnwrap(ProgressParsing.fraction(from: "step 10% → 75%"))
        XCTAssertEqual(fraction, 0.75, accuracy: 0.000_001)
    }

    func testFractionReturnsNilWithoutPercentage() {
        XCTAssertNil(ProgressParsing.fraction(from: "正在生成镜像…"))
        XCTAssertNil(ProgressParsing.fraction(from: "100 percent complete"))
        XCTAssertNil(ProgressParsing.fraction(from: ""))
    }

    func testFractionClampsOutOfRangeValues() throws {
        XCTAssertEqual(try XCTUnwrap(ProgressParsing.fraction(from: "100%")), 1, accuracy: 0.000_001)
        XCTAssertEqual(try XCTUnwrap(ProgressParsing.fraction(from: "150%")), 1, accuracy: 0.000_001)
        XCTAssertEqual(try XCTUnwrap(ProgressParsing.fraction(from: "0%")), 0, accuracy: 0.000_001)

        // 负值必须钳制到 0，不能因为忽略负号而变成 5%。
        XCTAssertEqual(
            try XCTUnwrap(ProgressParsing.fraction(from: "CD data progress -5%")),
            0,
            accuracy: 0.000_001
        )
    }

    func testStageExtractsKeywordBeforeProgress() throws {
        XCTAssertEqual(ProgressParsing.stage(from: "CD data progress 12.0%"), "CD data")
        XCTAssertEqual(ProgressParsing.stage(from: "  DVD geometry progress 3%"), "DVD geometry")
        XCTAssertEqual(ProgressParsing.stage(from: "bd-encode Progress 1%"), "bd-encode")
    }

    func testStageReturnsNilForNonProgressLines() {
        XCTAssertNil(ProgressParsing.stage(from: "progress 12%"))
        XCTAssertNil(ProgressParsing.stage(from: "CD data done"))
        XCTAssertNil(ProgressParsing.stage(from: ""))
        XCTAssertNil(ProgressParsing.stage(from: "   "))
    }
}
