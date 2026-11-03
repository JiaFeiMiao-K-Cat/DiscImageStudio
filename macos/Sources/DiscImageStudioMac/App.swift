import SwiftUI
import DiscImageStudioKit

@main
struct DiscImageStudioApp: App {
    @State private var model = AppModel()

    var body: some Scene {
        WindowGroup("Disc Image Studio") {
            ContentView(app: model)
        }
        .defaultSize(width: 1280, height: 840)
        .commands {
            CommandGroup(replacing: .appInfo) {
                Button("关于 Disc Image Studio") { model.showAbout() }
            }
            CommandGroup(replacing: .newItem) {
                Button("新建工程…") { model.beginNewProject() }
                    .keyboardShortcut("n", modifiers: .command)
                Button("打开工程…") { model.openProjectPanel() }
                    .keyboardShortcut("o", modifiers: .command)
                Divider()
                Button("保存工程") { model.project?.saveNow() }
                    .keyboardShortcut("s", modifiers: .command)
                    .disabled(model.project == nil)
            }
            CommandGroup(after: .appSettings) {
                Button("返回主页") { model.goHome() }
                    .keyboardShortcut("h", modifiers: [.command, .shift])
            }
        }

        Settings {
            SettingsView(model: model)
                .frame(width: 700, height: 760)
                .preferredColorScheme(model.preferredColorScheme)
        }
    }
}
