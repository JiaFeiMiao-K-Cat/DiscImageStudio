import Foundation

/// 把运行时拼接/传入的 UI 文案交给 main bundle 的 .strings 查找。
/// 字面量 `Text("…")` 本身走 LocalizedStringKey；自定义组件传 String 时用这里。
func L(_ key: String) -> String {
    NSLocalizedString(key, comment: "")
}

/// 带格式参数的版本：key 里使用 %@ / %d / %.1f 等占位符。
func L(_ key: String, _ arguments: CVarArg...) -> String {
    let format = NSLocalizedString(key, comment: "")
    guard !arguments.isEmpty else { return format }
    return String(format: format, locale: Locale.current, arguments: arguments)
}
