import Foundation
import Observation
import UserNotifications
import ReleaseSyncKit

/// Checks for new versions on a schedule while ReleaseSync is open, tells the person once about each new version, and installs the updates of
/// apps they set to update themselves. Nothing runs when the app is closed (there is no background helper).
@MainActor
@Observable
final class UpdateScheduler {
    /// Which keys have an update the person hasn't been told about yet, given what they were told before (`told`: key to the tag).
    static func announcements(waiting: [(key: String, tag: String)], told: [String: String]) -> [String] {
        waiting.filter { told[$0.key] != $0.tag }.map(\.key)
    }

    private let library: LibraryModel
    private let install: InstallModel
    private let prefs: ViewPrefs
    private let log: ActivityLog
    private let defaults: UserDefaults
    private var loop: Task<Void, Never>?
    /// Posts a notification; replaced in tests.
    var notify: @MainActor (String, String) async -> Void = { title, body in
        let center = UNUserNotificationCenter.current()
        guard (try? await center.requestAuthorization(options: [.alert, .sound])) == true else { return }
        let content = UNMutableNotificationContent(); content.title = title; content.body = body
        try? await center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }

    init(library: LibraryModel, install: InstallModel, prefs: ViewPrefs, log: ActivityLog, defaults: UserDefaults = .standard) {
        self.library = library; self.install = install; self.prefs = prefs; self.log = log; self.defaults = defaults
    }

    func start() {
        loop?.cancel()
        loop = Task { [weak self] in
            while !Task.isCancelled {
                let hours = self?.prefs.checkEveryHours ?? 0
                try? await Task.sleep(for: .seconds(hours > 0 ? Double(hours) * 3600 : 600))
                guard let self, !Task.isCancelled, self.prefs.checkEveryHours > 0 else { continue }
                await self.checkAndAnnounce()
            }
        }
    }

    func stop() { loop?.cancel() }

    /// One scheduled round: look for new releases, then tell the person and update what they asked to be updated.
    func checkAndAnnounce() async {
        guard library.phase == .loaded, !library.isChecking, !library.isDemo else { return }
        guard let summary = await library.checkForUpdates(manual: false) else { return }
        await install.reconcile(releases: library.releases)
        log.add("Checked \(summary.checked) app\(summary.checked == 1 ? "" : "s")", .info)
        let apps = library.apps.filter { !$0.hidden }
        let waiting = apps.compactMap { app -> (SyncApp, ReleaseFacts)? in let f = install.facts(app, library.releases[app.key]); return f.hasUpdate ? (app, f) : nil }
        log.add("\(waiting.count) update\(waiting.count == 1 ? "" : "s") available", .info)

        // apps set to update themselves
        let auto = waiting.filter { $0.0.autoUpdate }
        if !auto.isEmpty {
            log.add("Updating \(auto.count) app\(auto.count == 1 ? "" : "s") automatically", .info)
            for (app, f) in auto { await install.install(app, info: f.info, unattended: true) }
        }

        guard prefs.notifyOfUpdates else { return }
        var told = (defaults.dictionary(forKey: "UpdatesTold") as? [String: String]) ?? [:]
        let still = waiting.filter { (app, _) in install.facts(app, library.releases[app.key]).hasUpdate }
        let fresh = Set(Self.announcements(waiting: still.map { ($0.0.key, $0.1.latestTag) }, told: told))
        guard !fresh.isEmpty else { return }
        let names = still.filter { fresh.contains($0.0.key) }.map { $0.0.title }
        for (app, f) in still where fresh.contains(app.key) { told[app.key] = f.latestTag }
        defaults.set(told, forKey: "UpdatesTold")
        await notify(names.count == 1 ? "\(names[0]) has an update" : "\(names.count) updates are available",
                     names.count == 1 ? "A new version is ready to install in ReleaseSync." : names.prefix(4).joined(separator: ", ") + (names.count > 4 ? " and more" : ""))
    }
}
