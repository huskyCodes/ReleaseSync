import ReleaseSyncKit
import SwiftUI

@main
struct ReleaseSyncApp: App {
    @State private var model: LibraryModel

    init() {
        var model = LibraryModel(secrets: KeychainSecretStore())
        #if DEBUG
        // `--demo` shows a made-up library, so the app can be tried and photographed without an account
        if CommandLine.arguments.contains("--demo") { model = LibraryModel(showing: DemoLibrary.make(), secrets: KeychainSecretStore()) }
        #endif
        _model = State(initialValue: model)
    }

    var body: some Scene {
        WindowGroup {
            RootView().environment(model)
        }
        .defaultSize(width: 1000, height: 660)
        .commands {
            CommandGroup(replacing: .newItem) {}
        }

        Settings {
            SettingsView().environment(model)
        }
    }
}
