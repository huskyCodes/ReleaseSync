import ReleaseSyncKit
import SwiftUI

@main
struct ReleaseSyncApp: App {
    @State private var model: LibraryModel
    @State private var prefs = ViewPrefs()
    @State private var toasts = Toasts()
    @State private var nav = Navigator()

    init() {
        // under tests the app is only a host: it must not read the real Keychain (macOS may ask permission and wait for an answer)
        let underTest = ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
        let keychain = KeychainSecretStore()
        var model = underTest ? LibraryModel(previewing: .needsToken, secrets: InMemorySecrets())
                              : LibraryModel(secrets: keychain, blobs: EncryptedFileBlobStore(url: EncryptedFileBlobStore.defaultURL("releases.cache"), secrets: keychain))
        var nav = Navigator()
        #if DEBUG
        // `--demo` shows a made-up library, so the app can be tried and photographed without an account; `--page <name>` opens a page
        if CommandLine.arguments.contains("--demo") { model = LibraryModel(showing: DemoLibrary.make(), releases: DemoLibrary.releases(), secrets: InMemorySecrets()) }
        if let i = CommandLine.arguments.firstIndex(of: "--status"), i + 1 < CommandLine.arguments.count {
            let phases: [String: LibraryModel.Phase] = ["needsToken": .needsToken, "needsPassphrase": .needsPassphrase(wrong: false), "wrongPassphrase": .needsPassphrase(wrong: true),
                                                         "noLibrary": .noLibrary, "failed": .failed("GitHub didn't accept your token. Add a fresh one in Settings."), "loading": .loading]
            if let phase = phases[CommandLine.arguments[i + 1]] { model = LibraryModel(previewing: phase, secrets: KeychainSecretStore()) }
        }
        if CommandLine.arguments.contains("--drawer") { nav.drawerApp = model.apps.first }
        if let i = CommandLine.arguments.firstIndex(of: "--page"), i + 1 < CommandLine.arguments.count, let page = Page(rawValue: CommandLine.arguments[i + 1]) { nav = Navigator(page: page) }
        #endif
        _model = State(initialValue: model)
        _nav = State(initialValue: nav)
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model).environment(prefs).environment(toasts).environment(nav)
                .preferredColorScheme(prefs.appearance == .system ? nil : (prefs.appearance == .dark ? .dark : .light))
                #if DEBUG
                .task { await WindowSnapshot.takeIfAsked() }
                #endif
        }
        .defaultSize(width: 1180, height: 760)
        .commands {
            CommandGroup(replacing: .newItem) {}
            // the Windows app keeps Settings in its sidebar; on the Mac ⌘, goes to the same page
            CommandGroup(replacing: .appSettings) {
                Button("Settings…") { nav.show(.settings) }.keyboardShortcut(",", modifiers: .command)
            }
            CommandMenu("Library") {
                Button("Check for Updates") { Task { await checkNow(model, toasts) } }.keyboardShortcut("r", modifiers: .command).disabled(model.isBusy || model.isChecking)
                Divider()
                ForEach(Page.primary) { page in
                    Button(page.title) { nav.show(page) }.keyboardShortcut(KeyEquivalent(Character(String((Page.primary.firstIndex(of: page) ?? 0) + 1))), modifiers: .command)
                }
            }
        }
    }
}
