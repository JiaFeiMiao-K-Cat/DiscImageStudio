import Foundation

/// 把表单状态转换成 CLI 命令行的 `--name value` 序列，并做最小校验。
public enum CommandArgumentBuilder {
    /// 按 `command.options` 的顺序输出；值为空（trim 后）时跳过；未在 options 中的键忽略。
    public static func arguments(for command: DiscCommandDescriptor, values: [String: String]) -> [String] {
        var arguments: [String] = []
        for option in command.options {
            guard let raw = values[option.name] else { continue }
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { continue }
            if option.valueType == .boolean {
                guard let flag = normalizedBoolean(trimmed) else { continue }
                arguments.append("--\(option.name)")
                arguments.append(flag)
            } else {
                arguments.append("--\(option.name)")
                arguments.append(trimmed)
            }
        }
        return arguments
    }

    /// 仅用于展示与日志。单引号包裹含空格或 shell 特殊字符的参数。
    public static func displayCommand(executable: String, arguments: [String]) -> String {
        ([executable] + arguments).map(quote).joined(separator: " ")
    }

    public static func normalizedBoolean(_ raw: String) -> String? {
        switch raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
        case "true", "1", "yes", "on": return "true"
        case "false", "0", "no", "off": return "false"
        default: return nil
        }
    }

    private static func quote(_ value: String) -> String {
        guard !value.isEmpty else { return "''" }
        let safe = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_./:=@,+"))
        if value.unicodeScalars.allSatisfy({ safe.contains($0) }) {
            return value
        }
        return "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }
}

/// 表单的初始值：与 DESIGN.md §2.2 的默认规则一致。
public enum DefaultValues {
    public static func make(for command: DiscCommandDescriptor) -> [String: String] {
        var values: [String: String] = [:]
        for option in command.options {
            if let defaultValue = option.defaultValue {
                values[option.name] = defaultValue
            } else if option.valueType == .boolean {
                values[option.name] = "false"
            } else if option.valueType == .choice, let first = option.choices?.first {
                values[option.name] = first
            } else {
                values[option.name] = ""
            }
        }
        return values
    }
}

/// 类型与必填校验；不做业务范围判断（范围仍由 CLI 负责）。
public enum CommandValidation {
    public struct Issue: Sendable, Equatable, Identifiable {
        public let optionName: String
        public let message: String

        public var id: String { optionName }
    }

    public static func issues(for command: DiscCommandDescriptor, values: [String: String]) -> [Issue] {
        var issues: [Issue] = []
        for option in command.options {
            let raw = values[option.name]?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            if raw.isEmpty {
                if option.required {
                    issues.append(Issue(optionName: option.name, message: "「\(option.displayName)」为必填项。"))
                }
                continue
            }

            switch option.valueType {
            case .integer:
                if Int(raw) == nil {
                    issues.append(Issue(optionName: option.name, message: "「\(option.displayName)」必须是整数。"))
                }
            case .decimal:
                if Double(raw) == nil {
                    issues.append(Issue(optionName: option.name, message: "「\(option.displayName)」必须是数字。"))
                }
            case .choice:
                let choices = option.choices ?? []
                if !choices.isEmpty, !choices.contains(where: { $0.caseInsensitiveCompare(raw) == .orderedSame }) {
                    issues.append(Issue(optionName: option.name, message: "「\(option.displayName)」的取值必须是 \(choices.joined(separator: " / "))。"))
                }
            case .boolean:
                if CommandArgumentBuilder.normalizedBoolean(raw) == nil {
                    issues.append(Issue(optionName: option.name, message: "「\(option.displayName)」必须是 true 或 false。"))
                }
            default:
                break
            }
        }
        return issues
    }
}
