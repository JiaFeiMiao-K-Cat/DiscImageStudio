import Foundation

/// 从 CLI 的进度行里提取数值进度。
public enum ProgressParsing {
    private static let percentExpression = try? NSRegularExpression(
        pattern: #"(-?[0-9]+(?:\.[0-9]+)?)\s*%"#,
        options: []
    )

    /// 匹配 "… 42.3%" / "… 42 %"，返回 0…1；没有百分比时返回 nil。
    public static func fraction(from line: String) -> Double? {
        guard let expression = percentExpression else { return nil }
        let range = NSRange(line.startIndex..<line.endIndex, in: line)
        let matches = expression.matches(in: line, options: [], range: range)
        guard let match = matches.last, match.numberOfRanges > 1,
              let valueRange = Range(match.range(at: 1), in: line),
              let percent = Double(line[valueRange])
        else { return nil }
        return min(max(percent / 100.0, 0), 1)
    }

    /// 从 "CD data progress 12.0%" 里得到 "CD data"；不像进度行时返回 nil。
    public static func stage(from line: String) -> String? {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        guard let progressRange = trimmed.range(of: " progress", options: .caseInsensitive) else {
            return nil
        }
        let head = trimmed[trimmed.startIndex..<progressRange.lowerBound]
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return head.isEmpty ? nil : head
    }
}
