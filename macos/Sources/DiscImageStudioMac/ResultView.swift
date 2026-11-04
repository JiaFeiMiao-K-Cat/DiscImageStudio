import AppKit
import SwiftUI
import DiscImageStudioKit

/// 通用任务结果卡：退出码、耗时、输出路径、stderr 尾部与 PNG 预览。
struct ResultView: View {
    var title: String = L("结果")
    let result: CliResult?
    let outputURL: URL?
    var intendedOutputURL: URL?
    var summary: String?
    var stderrLines: [String] = []
    var wasCancelled: Bool = false
    var cliLabel: String?
    var previewRevision: Int = 0
    var burnableOutputURL: URL?
    var onReveal: ((URL) -> Void)?
    var onBurn: ((URL) -> Void)?

    @State private var showsFullStderr = false

    var body: some View {
        if let result {
            VStack(alignment: .leading, spacing: Spacing.m) {
                CardHeader(title, systemImage: "checkmark.seal") {
                    Chip(
                        text: result.succeeded ? L("成功") : (wasCancelled ? L("已取消") : L("失败")),
                        systemImage: result.succeeded
                            ? "checkmark.circle.fill"
                            : (wasCancelled ? "stop.circle.fill" : "xmark.circle.fill"),
                        tint: result.succeeded ? .green : (wasCancelled ? .orange : .red)
                    )
                }

                Grid(alignment: .leading, horizontalSpacing: Spacing.xl, verticalSpacing: Spacing.xs) {
                    GridRow(alignment: .firstTextBaseline) {
                        metricLabel(L("退出码"))
                        HStack(spacing: Spacing.xs) {
                            Text("\(result.exitCode)")
                                .font(Typography.monoSmall)
                                .foregroundStyle(result.succeeded ? .green : .red)
                            Text(exitDescription(result))
                                .font(Typography.caption2)
                                .foregroundStyle(.secondary)
                        }
                    }
                    GridRow(alignment: .firstTextBaseline) {
                        metricLabel(L("总耗时"))
                        Text(Formatting.duration(result.duration))
                            .font(.system(size: 11.5))
                    }
                    if let cliLabel {
                        GridRow(alignment: .firstTextBaseline) {
                            metricLabel(L("运行方式"))
                            Text(cliLabel)
                                .font(.system(size: 11.5))
                                .foregroundStyle(.secondary)
                        }
                    }
                    if let summary, !summary.isEmpty {
                        GridRow(alignment: .firstTextBaseline) {
                            metricLabel(L("最近输出"))
                            Text(summary)
                                .font(Typography.monoSmall)
                                .foregroundStyle(.secondary)
                                .textSelection(.enabled)
                                .lineLimit(2)
                        }
                    }
                }

                outputSection(result)
                stderrSection(result)

                if result.succeeded, let outputURL, previewRevision > 0,
                   outputURL.pathExtension.lowercased() == "png" {
                    ImagePreviewView(url: outputURL, reloadToken: previewRevision)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .cardSurface(padding: Spacing.l)
        }
    }

    private func exitDescription(_ result: CliResult) -> String {
        if wasCancelled { return L("任务被取消") }
        switch result.exitCode {
        case 0: return L("命令执行成功")
        case -1: return L("CLI 无法启动")
        default: return L("命令返回非零退出码")
        }
    }

    private func metricLabel(_ text: String) -> some View {
        Text(L(text))
            .font(Typography.caption)
            .foregroundStyle(.secondary)
            .frame(width: 64, alignment: .leading)
    }

    @ViewBuilder
    private func outputSection(_ result: CliResult) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
            Text("输出文件")
                .font(Typography.caption)
                .foregroundStyle(.secondary)
                .frame(width: 64, alignment: .leading)

            if let outputURL {
                Text(Formatting.abbreviatedPath(outputURL.path))
                    .font(Typography.monoSmall)
                    .textSelection(.enabled)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: Spacing.s)
                if let onReveal {
                    Button {
                        onReveal(outputURL)
                    } label: {
                        Label("在 Finder 中显示", systemImage: "folder")
                    }
                    .controlSize(.small)
                    .accessibilityIdentifier("reveal-output")
                }
                if let burnableOutputURL, let onBurn {
                    Button {
                        onBurn(burnableOutputURL)
                    } label: {
                        Label("刻录此文件", systemImage: "opticaldisc.fill")
                    }
                    .controlSize(.small)
                }
            } else if let intendedOutputURL {
                Text(Formatting.abbreviatedPath(intendedOutputURL.path))
                    .font(Typography.monoSmall)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: Spacing.s)
                Text(result.succeeded ? "未找到输出文件" : "未生成")
                    .font(Typography.caption2)
                    .foregroundStyle(.orange)
            } else {
                Text("未指定输出路径")
                    .font(Typography.caption2)
                    .foregroundStyle(.secondary)
            }
        }
    }

    @ViewBuilder
    private func stderrSection(_ result: CliResult) -> some View {
        if !result.succeeded && !stderrLines.isEmpty {
            VStack(alignment: .leading, spacing: Spacing.xs) {
                HStack(spacing: Spacing.s) {
                    Label("错误输出（stderr 尾部）", systemImage: "exclamationmark.triangle.fill")
                        .font(Typography.caption)
                        .foregroundStyle(.red)
                    Spacer(minLength: 0)
                    Button(showsFullStderr ? "收起完整日志" : "查看完整日志") {
                        showsFullStderr.toggle()
                    }
                    .controlSize(.small)
                }
                Text((showsFullStderr ? stderrLines : Array(stderrLines.suffix(12))).joined(separator: "\n"))
                    .font(Typography.monoSmall)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(Spacing.s)
                    .insetSurface()
            }
        }
    }
}
