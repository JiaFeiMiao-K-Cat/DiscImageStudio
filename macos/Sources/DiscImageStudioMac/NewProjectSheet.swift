import AppKit
import SwiftUI
import DiscImageStudioKit

/// 新建工程 sheet：盘型 + 预设 + 名称 + 保存位置 + 输出位置。
struct NewProjectSheet: View {
    let app: AppModel

    @Environment(\.dismiss) private var dismiss
    @State private var family: DiscFamily
    @State private var selectedPresetID: String
    @State private var name: String = "未命名工程"
    @State private var projectDirectory: URL
    @State private var outputDirectory: URL
    @State private var errorMessage: String?

    init(app: AppModel) {
        self.app = app
        let family = app.newProjectFamily
        let presets = app.presetsFor(family)
        let initialPresetID = app.newProjectPresetID.flatMap { id in
            presets.contains(where: { $0.id == id }) ? id : nil
        } ?? presets.first?.id ?? ""
        let projectDirectory = app.defaultProjectsDirectory
        let outputDirectory = Formatting.expandedURL(for: app.settings.defaultOutputDirectory ?? "")
            ?? projectDirectory.appendingPathComponent("outputs", isDirectory: true)
        _family = State(initialValue: family)
        _selectedPresetID = State(initialValue: initialPresetID)
        _projectDirectory = State(initialValue: projectDirectory)
        _outputDirectory = State(initialValue: outputDirectory)
    }

    private var presets: [DiscPreset] { app.presetsFor(family) }

    private var selectedPreset: DiscPreset? {
        presets.first { $0.id == selectedPresetID } ?? presets.first
    }

    private var trimmedName: String {
        name.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var safeFileName: String {
        let base = trimmedName.isEmpty ? "未命名工程" : trimmedName
        return base
            .replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: ":", with: "-")
    }

    private var projectURL: URL {
        projectDirectory.appendingPathComponent(safeFileName).appendingPathExtension(ProjectStore.fileExtension)
    }

    private var outputExtension: String {
        switch family {
        case .cd: return "wav"
        case .dvd, .bd: return "iso"
        }
    }

    private var outputURL: URL {
        outputDirectory.appendingPathComponent(safeFileName).appendingPathExtension(outputExtension)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.l) {
            HStack(alignment: .firstTextBaseline) {
                Text("新建工程")
                    .font(.system(size: 18, weight: .semibold))
                Spacer()
                if app.usingFallbackPresets {
                    Chip(text: "内置兜底预设", systemImage: "exclamationmark.triangle", tint: .orange)
                }
            }

            VStack(alignment: .leading, spacing: 6) {
                FieldLabel("盘型")
                SegmentedControl(
                    items: DiscFamily.allCases.map {
                        .init(id: $0, title: $0.familyLabel, systemImage: "opticaldisc")
                    },
                    selection: Binding(
                        get: { family },
                        set: { newValue in
                            family = newValue
                            if let first = app.presetsFor(newValue).first {
                                selectedPresetID = first.id
                            }
                        }
                    )
                )
            }

            VStack(alignment: .leading, spacing: 6) {
                FieldLabel("盘片预设")
                Picker("盘片预设", selection: $selectedPresetID) {
                    ForEach(presets) { preset in
                        Text(preset.displayName).tag(preset.id)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .frame(maxWidth: 360, alignment: .leading)
                if let preset = selectedPreset {
                    Text(preset.description)
                        .font(Typography.caption2)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            VStack(alignment: .leading, spacing: 6) {
                FieldLabel("工程名")
                TextField("工程名", text: $name)
                    .textFieldStyle(.roundedBorder)
            }

            pathRow(
                label: "保存位置",
                value: projectDirectory,
                detail: "工程文件：\(Formatting.abbreviatedPath(projectURL.path))"
            ) {
                chooseDirectory(title: "选择工程保存位置", current: projectDirectory) { projectDirectory = $0 }
            }

            pathRow(
                label: "输出位置",
                value: outputDirectory,
                detail: "首次输出：\(Formatting.abbreviatedPath(outputURL.path))"
            ) {
                chooseDirectory(title: "选择输出位置", current: outputDirectory) { outputDirectory = $0 }
            }

            if let preset = selectedPreset {
                HStack(spacing: Spacing.xs) {
                    Chip(text: "\(format(preset.geometry.innerRadiusMm))–\(format(preset.geometry.outerRadiusMm)) mm", systemImage: "ruler")
                    Chip(text: "\(preset.geometry.sectors) 扇区", systemImage: "square.stack.3d.up")
                    if family == .bd {
                        Chip(text: "BD-R 单层", systemImage: "opticaldisc")
                    }
                }
            }

            if let errorMessage {
                Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                    .font(Typography.caption)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: 0)

            HStack {
                Spacer()
                Button("取消") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("创建工程") { create() }
                    .buttonStyle(.borderedProminent)
                    .tint(DesignSystem.brand)
                    .keyboardShortcut(.defaultAction)
                    .disabled(trimmedName.isEmpty || selectedPreset == nil)
            }
        }
        .padding(Spacing.xl)
        .frame(width: 600, height: 590)
        .background(DesignSystem.appBackground)
    }

    private func pathRow(
        label: String,
        value: URL,
        detail: String,
        browse: @escaping () -> Void
    ) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            FieldLabel(label)
            HStack(spacing: Spacing.xs) {
                Text(Formatting.abbreviatedPath(value.path))
                    .font(Typography.monoSmall)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 7)
                    .padding(.vertical, 5)
                    .background(DesignSystem.surface2, in: RoundedRectangle(cornerRadius: 6))
                Button("选择…", action: browse)
                    .controlSize(.small)
            }
            Text(detail)
                .font(Typography.caption2)
                .foregroundStyle(.tertiary)
                .lineLimit(1)
                .truncationMode(.middle)
        }
    }

    private func chooseDirectory(title: String, current: URL, apply: @escaping (URL) -> Void) {
        if let url = FilePanels.chooseDirectory(title: title, currentPath: current.path) {
            apply(url)
        }
    }

    private func create() {
        guard let preset = selectedPreset else {
            errorMessage = "请选择盘片预设。"
            return
        }
        guard !trimmedName.isEmpty else {
            errorMessage = "工程名不能为空。"
            return
        }
        let fileManager = FileManager.default
        if fileManager.fileExists(atPath: projectURL.path) {
            errorMessage = "工程文件已存在，请改名或选择其他保存位置。"
            return
        }
        do {
            try fileManager.createDirectory(at: projectDirectory, withIntermediateDirectories: true)
            try fileManager.createDirectory(at: outputDirectory, withIntermediateDirectories: true)
        } catch {
            errorMessage = "无法创建目录：\(error.localizedDescription)"
            return
        }
        let created = app.createProject(
            name: trimmedName,
            family: family,
            preset: preset,
            projectURL: projectURL,
            outputPath: outputURL.path
        )
        if created {
            dismiss()
        } else {
            errorMessage = app.projectError ?? "创建工程失败。"
        }
    }

    private func format(_ value: Double) -> String {
        value == value.rounded() ? String(Int(value)) : String(format: "%.2f", value)
    }
}
