import ReleaseSyncKit
import SwiftUI

@main
struct ReleaseSyncApp: App {
    @State private var model: LibraryModel
    @State private var prefs: ViewPrefs
    @State private var toasts: Toasts
    @State private var activity: ActivityLog
    @State private var install: InstallModel
    @State private var add: AddModel
    @State private var scan = ScanModel()
    @State private var scheduler: UpdateScheduler
    @State private var nav = Navigator()
    @State private var discover: DiscoverModel
    @State private var lan: LanModel

    init() {
        // under tests the app is only a host: it must not read the real Keychain (macOS may ask permission and wait for an answer)
        let underTest = ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
        let keychain = KeychainSecretStore()
        let client = SourceClient()
        let meter = RateMeter()
        let prefs = ViewPrefs()
        var model = underTest ? LibraryModel(previewing: .needsToken, secrets: InMemorySecrets(), meter: meter)
                              : LibraryModel(secrets: keychain, client: client, blobs: EncryptedFileBlobStore(url: EncryptedFileBlobStore.defaultURL("releases.cache"), secrets: keychain), meter: meter)
        var discover = DiscoverModel(client: client, prefs: prefs, meter: meter,
                                     blobs: underTest ? nil : EncryptedFileBlobStore(url: EncryptedFileBlobStore.defaultURL("discover.cache"), secrets: keychain))
        var nav = Navigator()
        let toasts = Toasts(), activity = ActivityLog()
        var install = underTest ? InstallModel(showing: [:], log: activity, toasts: toasts)
                                : InstallModel(log: activity, toasts: toasts)
        var lan = LanModel(secrets: LanPairSecrets(store: underTest ? InMemorySecrets() : keychain), available: underTest ? false : LanModel.networkingBuilt)
        #if DEBUG
        // `--demo` shows a made-up library, so the app can be tried and photographed without an account; `--page <name>` opens a page
        if CommandLine.arguments.contains("--demo") {
            model = LibraryModel(showing: DemoLibrary.make(), releases: DemoLibrary.releases(), peers: DemoLibrary.peers(), secrets: InMemorySecrets())
            discover = DiscoverModel(showing: DemoLibrary.discover(), client: client, prefs: prefs, meter: RateMeter(RateStatus(limit: 5000, remaining: 4962)))
            lan = DemoLibrary.lan()
            install = InstallModel(showing: DemoLibrary.installs(), log: activity, toasts: toasts)
        }
        if let i = CommandLine.arguments.firstIndex(of: "--status"), i + 1 < CommandLine.arguments.count {
            let phases: [String: LibraryModel.Phase] = ["needsToken": .needsToken, "needsPassphrase": .needsPassphrase(wrong: false), "wrongPassphrase": .needsPassphrase(wrong: true),
                                                         "noLibrary": .noLibrary, "failed": .failed("GitHub didn't accept your token. Add a fresh one in Settings."), "loading": .loading]
            if let phase = phases[CommandLine.arguments[i + 1]] { model = LibraryModel(previewing: phase, secrets: keychain) }
        }
        if CommandLine.arguments.contains("--drawer") { nav.drawerApp = model.apps.first }
        if let i = CommandLine.arguments.firstIndex(of: "--page"), i + 1 < CommandLine.arguments.count, let page = Page(rawValue: CommandLine.arguments[i + 1]) { nav = Navigator(page: page) }
        #endif
        model.thisDevice = DeviceIdentity.make()
        model.editingAllowed = { [prefs] in prefs.allowLibraryChanges }
        _prefs = State(initialValue: prefs)
        _discover = State(initialValue: discover)
        _lan = State(initialValue: lan)
        _toasts = State(initialValue: toasts)
        _activity = State(initialValue: activity)
        _install = State(initialValue: install)
        _scheduler = State(initialValue: UpdateScheduler(library: model, install: install, prefs: prefs, log: activity))
        _add = State(initialValue: AddModel(library: model, install: install, toasts: toasts, log: activity, allowChanges: { prefs.allowLibraryChanges = true }))
        _model = State(initialValue: model)
        _nav = State(initialValue: nav)
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model).environment(prefs).environment(toasts).environment(nav).environment(discover).environment(lan).environment(activity).environment(install).environment(add).environment(scan).environment(scheduler)
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
