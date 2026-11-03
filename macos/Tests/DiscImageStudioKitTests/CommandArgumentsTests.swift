import Foundation
import XCTest

import DiscImageStudioKit

/// 钉住参数构造、表单默认值与校验契约（DESIGN.md §2.2）。
final class CommandArgumentsTests: XCTestCase {

    // MARK: - arguments(for:values:)

    func testArgumentsFollowOptionOrderNotDictionaryOrder() {
        let command = makeCommand()
        let values = [
            "volume-label": "MY_DISC",
            "interleave": "true",
            "input": "/tmp/source.img",
            "sectors": "1000",
            "r0": "24.5",
        ]

        XCTAssertEqual(
            CommandArgumentBuilder.arguments(for: command, values: values),
            [
                "--input", "/tmp/source.img",
                "--volume-label", "MY_DISC",
                "--r0", "24.5",
                "--sectors", "1000",
                "--interleave", "true",
            ]
        )
    }

    func testValuesAreTrimmedAndBlankValuesSkipped() {
        let command = makeCommand()
        let values = [
            "input": "  /tmp/with space.img\t",
            "output": "   ",
            "r0": "\n 56.8 ",
            "volume-label": "\t\n",
        ]

        XCTAssertEqual(
            CommandArgumentBuilder.arguments(for: command, values: values),
            ["--input", "/tmp/with space.img", "--r0", "56.8"]
        )
    }

    func testUnknownKeysAreIgnored() {
        let command = makeCommand()
        let values = [
            "input": "/tmp/source.img",
            "legacy-option": "should-not-appear",
            "--output": "/tmp/out.iso",
        ]

        XCTAssertEqual(
            CommandArgumentBuilder.arguments(for: command, values: values),
            ["--input", "/tmp/source.img"]
        )
    }

    func testBooleanValuesAreNormalizedToLowercase() {
        let command = makeCommand()
        let cases: [(String, String?)] = [
            ("true", "true"),
            ("TRUE", "true"),
            (" 1 ", "true"),
            ("yes", "true"),
            ("YES", "true"),
            ("false", "false"),
            ("FALSE", "false"),
            ("0", "false"),
            ("no", "false"),
            ("outside", nil),
            ("2", nil),
            ("", nil),
        ]

        for (raw, expected) in cases {
            let arguments = CommandArgumentBuilder.arguments(
                for: command,
                values: ["interleave": raw]
            )
            if let expected {
                XCTAssertEqual(
                    arguments,
                    ["--interleave", expected],
                    "Boolean 值 \(raw.debugDescription) 应归一化成 \(expected)"
                )
            } else {
                XCTAssertEqual(
                    arguments,
                    [],
                    "非法 Boolean 值 \(raw.debugDescription) 应被丢弃"
                )
            }
        }
    }

    func testArgumentsSkipMissingValuesEntirely() {
        let command = makeCommand()
        XCTAssertEqual(CommandArgumentBuilder.arguments(for: command, values: [:]), [])
    }

    // MARK: - displayCommand

    func testDisplayCommandQuotesUnsafeValuesLikeShell() {
        XCTAssertEqual(
            CommandArgumentBuilder.displayCommand(
                executable: "/usr/local/bin/disc-image-studio",
                arguments: ["cd-generate", "--input", "/tmp/source.img", "--interleave", "true"]
            ),
            "/usr/local/bin/disc-image-studio cd-generate --input /tmp/source.img --interleave true"
        )

        XCTAssertEqual(
            CommandArgumentBuilder.displayCommand(
                executable: "/Applications/Disc Image Studio.app/Contents/Resources/cli/disc-image-studio",
                arguments: ["--data-dir", "/tmp/my files", "--volume-label", "it's"]
            ),
            "'/Applications/Disc Image Studio.app/Contents/Resources/cli/disc-image-studio' --data-dir '/tmp/my files' --volume-label 'it'\\''s'"
        )

        XCTAssertEqual(
            CommandArgumentBuilder.displayCommand(executable: "cli", arguments: ["", "$HOME"]),
            "cli '' '$HOME'"
        )
    }

    // MARK: - DefaultValues

    func testDefaultValuesFollowDocumentedRules() {
        let command = DiscCommandDescriptor(
            name: "demo",
            displayName: "Demo",
            description: "",
            options: [
                option("with-default", .string, defaultValue: "CD_IMAGE"),
                option("required-required", .inputFile, required: true),
                option("my-bool", .boolean),
                option("bool-default", .boolean, defaultValue: "true"),
                option("choice-with-default", .choice, defaultValue: "outer", choices: ["inner", "outer"]),
                option("choice-first", .choice, choices: ["inner", "outer"]),
                option("choice-empty", .choice),
                option("plain", .string),
            ]
        )

        XCTAssertEqual(
            DefaultValues.make(for: command),
            [
                "with-default": "CD_IMAGE",
                "required-required": "",
                "my-bool": "false",
                "bool-default": "true",
                "choice-with-default": "outer",
                "choice-first": "inner",
                "choice-empty": "",
                "plain": "",
            ]
        )
    }

    // MARK: - CommandValidation

    func testValidationReportsMissingRequiredValues() {
        let command = makeCommand()
        let issues = CommandValidation.issues(for: command, values: ["output": "  "])

        XCTAssertEqual(issues.count, 2)
        XCTAssertEqual(issues.map(\.optionName), ["input", "output"])
        XCTAssertEqual(issues[0].message, "「Source image」为必填项。")
        XCTAssertEqual(issues[1].message, "「Output image」为必填项。")
        XCTAssertEqual(issues[0].id, "input")
    }

    func testValidationReportsTypeErrors() {
        let command = makeCommand()
        let issues = CommandValidation.issues(
            for: command,
            values: [
                "input": "/tmp/source.img",
                "output": "/tmp/out.iso",
                "r0": "abc",
                "sectors": "12.5",
                "file-position": "middle",
                "interleave": "maybe",
            ]
        )

        XCTAssertEqual(
            issues.map(\.optionName),
            ["file-position", "r0", "sectors", "interleave"]
        )
        XCTAssertEqual(issues[0].message, "「File position」的取值必须是 inner / outer。")
        XCTAssertEqual(issues[1].message, "「Inner radius」必须是数字。")
        XCTAssertEqual(issues[2].message, "「Program sectors」必须是整数。")
        XCTAssertEqual(issues[3].message, "「Interleave」必须是 true 或 false。")
    }

    func testValidationAcceptsValidValuesAndIgnoresUnknownKeys() {
        let command = makeCommand()
        let issues = CommandValidation.issues(
            for: command,
            values: [
                "input": "/tmp/source.img",
                "output": "/tmp/out.iso",
                "r0": " 56.8 ",
                "sectors": "359849",
                "file-position": "OUTER",
                "interleave": "yes",
                "unknown": "ignored",
            ]
        )
        XCTAssertEqual(issues, [])
    }

    func testValidationOnlyEnforcesChoiceWhenChoicesExist() {
        let command = DiscCommandDescriptor(
            name: "demo",
            displayName: "Demo",
            description: "",
            options: [option("free-choice", .choice)]
        )
        XCTAssertEqual(
            CommandValidation.issues(for: command, values: ["free-choice": "anything"]),
            []
        )
    }

    // MARK: - Helpers

    private func makeCommand() -> DiscCommandDescriptor {
        DiscCommandDescriptor(
            name: "cd-generate",
            displayName: "Generate CD",
            description: "",
            options: [
                option("input", .inputFile, displayName: "Source image", required: true),
                option("output", .outputFile, displayName: "Output image", required: true),
                option("data-dir", .inputFolder),
                option("volume-label", .string, defaultValue: "CD_IMAGE"),
                option("file-position", .choice, displayName: "File position", defaultValue: "inner", choices: ["inner", "outer"]),
                option("r0", .decimal, displayName: "Inner radius", defaultValue: "24.5", unit: "mm"),
                option("sectors", .integer, displayName: "Program sectors", defaultValue: "359849"),
                option("interleave", .boolean, displayName: "Interleave", defaultValue: "true"),
            ]
        )
    }

    private func option(
        _ name: String,
        _ valueType: DiscOptionValueType,
        displayName: String? = nil,
        required: Bool = false,
        defaultValue: String? = nil,
        unit: String? = nil,
        choices: [String]? = nil
    ) -> DiscOptionDefinition {
        DiscOptionDefinition(
            name: name,
            displayName: displayName ?? name,
            valueType: valueType,
            required: required,
            defaultValue: defaultValue,
            unit: unit,
            choices: choices
        )
    }
}
