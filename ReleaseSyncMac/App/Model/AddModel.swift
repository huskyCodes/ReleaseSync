import Foundation
import Observation
import ReleaseSyncKit

/// Finding a project and adding it to the library (the Windows add dialog): look it up, show what it offers for the Mac, add it, and install it if asked.
@MainActor
@Observable
final class AddModel {
    struct Preview: Equatable {
        var info: RepoInfo
        var latest: ReleaseInfo?
        /// Downloads that could install on this Mac, best first.
        var choices: [AssetInfo]
        var chosen: AssetInfo?
        var inLibrary: Bool
    }

    enum Stage: Equatable {
        case closed
        case loading(String)
        case preview(Preview)
        case results(query: String, [RepoInfo])
        case failed(String)
        /// Changes to the library are switched off; asks the person before turning them on.
        case needsPermission
    }

    private(set) var stage = Stage.closed
    var installNow = true
    private let library: LibraryModel
    private let install: InstallModel
    private let toasts: Toasts
    private let log: ActivityLog
    private let allowChanges: () -> Void
    private var pending: (() async -> Void)?

    init(library: LibraryModel, install: InstallModel, toasts: Toasts, log: ActivityLog, allowChanges: @escaping () -> Void) {
        self.library = library; self.install = install; self.toasts = toasts; self.log = log; self.allowChanges = allowChanges
    }

    var isOpen: Bool { stage != .closed }
    func close() { stage = .closed; pending = nil }

    // MARK: looking up

    /// Whatever the person typed: a link or owner/repo gives a preview; anything else is a search.
    func open(_ input: String) async {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        guard !library.isDemo else { toasts.show("This is a demo, so nothing is looked up.", .info); return }
        guard library.phase == .loaded else { toasts.show("Connect your library first (Settings).", .info); return }
        if let ref = RepoRef.parse(text) { await preview(source: ref.source, owner: ref.owner, repo: ref.repo) }
        else { await search(text) }
    }

    func open(repo: RepoInfo) async {
        guard !library.isDemo else { toasts.show("This is a demo, so nothing is looked up.", .info); return }
        await preview(source: repo.source, owner: repo.ownerLogin, repo: repo.name, known: repo)
    }

    private func search(_ query: String) async {
        stage = .loading("Searching GitHub for “\(query)”…")
        await library.prepareClient()
        do {
            let found = try await library.sourceClient.search(query + " archived:false", sort: "stars", perPage: 20)
            await library.meter.update(from: library.sourceClient)
            stage = .results(query: query, found)
        } catch { stage = .failed((error as? LocalizedError)?.errorDescription ?? error.localizedDescription) }
    }

    private func preview(source: String, owner: String, repo: String, known: RepoInfo? = nil) async {
        stage = .loading("Looking up \(owner)/\(repo)…")
        await library.prepareClient()
        let client = library.sourceClient
        do {
            let info: RepoInfo
            if let known, known.description != nil || !known.topics.isEmpty { info = known } else { info = try await client.repo(source: source, owner: owner, repo: repo) }
            let releases = (try? await client.releases(source: source, owner: owner, repo: repo, etag: nil))?.releases.filter { !$0.draft } ?? []
            await library.meter.update(from: client)
            let latest = releases.first { !$0.prerelease } ?? releases.first
            let key = SyncApp.makeKey(source: info.source, owner: info.ownerLogin, repo: info.name)
            stage = .preview(Preview(info: info, latest: latest, choices: MacAssets.choices(latest?.assets), chosen: MacAssets.pick(latest?.assets), inLibrary: library.apps.contains { $0.key == key }))
            installNow = latest != nil && MacAssets.pick(latest?.assets) != nil
        } catch { stage = .failed((error as? LocalizedError)?.errorDescription ?? error.localizedDescription) }
    }

    func choose(_ asset: AssetInfo) {
        guard case .preview(var p) = stage else { return }
        p.chosen = asset
        stage = .preview(p)
    }

    // MARK: adding

    /// A pattern that matches the chosen file again in later releases (the version numbers can change): the name with each run of digits and dots made flexible.
    static func assetPattern(for name: String) -> String {
        let digits = try! NSRegularExpression(pattern: #"\d+(?:\.\d+)*"#)
        var out = "^", last = name.startIndex
        for m in digits.matches(in: name, range: NSRange(name.startIndex..., in: name)) {
            guard let r = Range(m.range, in: name) else { continue }
            out += NSRegularExpression.escapedPattern(for: String(name[last..<r.lowerBound])) + #"[0-9.]+"#
            last = r.upperBound
        }
        return out + NSRegularExpression.escapedPattern(for: String(name[last...])) + "$"
    }

    func add() async {
        guard case .preview(let p) = stage, !p.inLibrary else { return }
        let install = installNow && p.chosen != nil
        await withPermission { [self] in
            let info = p.info
            let auto = MacAssets.pick(p.latest?.assets)
            let pattern = p.chosen.flatMap { $0 != auto ? Self.assetPattern(for: $0.name) : nil }
            let problem = await library.edit { LibraryEdits.addApp(source: info.source, owner: info.ownerLogin, repo: info.name, assetPattern: pattern, in: &$0, now: $1) }
            if let problem { toasts.show(problem, .error); return }
            stage = .closed
            log.add("Added \(info.fullName)", .success)
            let key = SyncApp.makeKey(source: info.source, owner: info.ownerLogin, repo: info.name)
            await library.checkForUpdates(manual: false)
            guard let app = library.apps.first(where: { $0.key == key }) else { return }
            if install, let release = library.releases[key] {
                toasts.show("Added \(app.title). Installing…", .info)
                await self.install.install(app, info: release)
            } else { toasts.show("Added \(app.title) to your library", .success) }
        }
    }

    func saveForLater(_ info: RepoInfo) async {
        guard !library.isDemo else { toasts.show("This is a demo, so nothing is saved.", .info); return }
        await withPermission { [self] in
            let problem = await library.edit { LibraryEdits.saveForLater(info, in: &$0, now: $1) }
            if let problem { toasts.show(problem, .error) } else { log.add("Saved \(info.fullName) to Try later", .info); toasts.show("Saved \(info.name) for later", .success) }
        }
    }

    /// Opens a Try later item's page to add it to the library.
    func open(_ item: TryLaterItem) async {
        var info = RepoInfo()
        info.source = item.source; info.ownerLogin = item.owner; info.name = item.repo; info.fullName = item.fullName
        info.description = item.description; info.language = item.language; info.stars = item.stars; info.avatarUrl = item.avatarUrl; info.topics = item.topics ?? []
        await open(repo: info)
    }

    func removeFromTryLater(_ item: TryLaterItem) async {
        await withPermission { [self] in
            let key = item.key
            let problem = await library.edit { LibraryEdits.removeTryLater(key, in: &$0, now: $1) }
            if let problem { toasts.show(problem, .error) } else { log.add("Removed \(item.fullName) from Try later", .info); toasts.show("Removed \(item.repo) from Try later", .info) }
        }
    }

    // MARK: importing a list

    private(set) var importStatus = ""
    private(set) var isImporting = false

    /// Adds every project in `text` (links, owner/repo lines, or a ReleaseSync or Obtainium export) in one go.
    func importList(_ text: String, group: String, installAfter: Bool) async {
        guard !isImporting else { return }
        guard !library.isDemo else { importStatus = "This is a demo, so nothing is imported."; return }
        guard library.phase == .loaded else { importStatus = "Connect your library first (Settings)."; return }
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { importStatus = "Paste some GitHub links or choose a file first."; return }
        let parsed = Importer.parse(text)
        let known = Set(library.apps.map(\.key))
        let fresh = parsed.entries.filter { !known.contains(SyncApp.makeKey(source: "github", owner: $0.owner, repo: $0.repo)) }
        let already = parsed.entries.count - fresh.count
        if fresh.isEmpty {
            importStatus = parsed.entries.isEmpty ? "No GitHub links were found in that." : "All \(already) projects are already in your library."
            return
        }
        isImporting = true
        defer { isImporting = false }
        let chosenGroup = group.trimmingCharacters(in: .whitespacesAndNewlines)
        await withPermission { [self] in
            importStatus = "Importing \(fresh.count)…"
            let problem = await library.edit { doc, now in
                var changed = false
                for e in fresh {
                    if LibraryEdits.addApp(source: "github", owner: e.owner, repo: e.repo, in: &doc, now: now) {
                        changed = true
                        let key = SyncApp.makeKey(source: "github", owner: e.owner, repo: e.repo)
                        if let name = e.name, !name.isEmpty, name.caseInsensitiveCompare(e.repo) != .orderedSame { _ = LibraryEdits.setDisplayName(key, name, in: &doc, now: now) }
                        if e.prereleases { _ = LibraryEdits.setIncludePrereleases(key, true, in: &doc, now: now) }
                        let g = !chosenGroup.isEmpty ? chosenGroup : (e.group ?? "")
                        if !g.trimmingCharacters(in: .whitespaces).isEmpty { _ = LibraryEdits.setGroup(key, g, in: &doc, now: now) }
                    }
                }
                return changed
            }
            if let problem { importStatus = problem; toasts.show(problem, .error); return }
            var parts = ["Imported \(fresh.count) project\(fresh.count == 1 ? "" : "s")"]
            if already > 0 { parts.append("\(already) already in your library") }
            if parsed.skipped > 0 { parts.append("\(parsed.skipped) skipped (not on GitHub)") }
            importStatus = parts.joined(separator: "  ·  ")
            log.add(importStatus, .success); toasts.show("Imported \(fresh.count) project\(fresh.count == 1 ? "" : "s")", .success)
            await library.checkForUpdates(manual: false)
            if installAfter {
                for e in fresh {
                    let key = SyncApp.makeKey(source: "github", owner: e.owner, repo: e.repo)
                    if let app = library.apps.first(where: { $0.key == key }), let info = library.releases[key] { await self.install.install(app, info: info) }
                }
            }
        }
    }

    // MARK: asking before the first change

    private func withPermission(_ work: @escaping () async -> Void) async {
        if library.editingAllowed() { await work(); return }
        pending = work
        stage = .needsPermission
    }

    /// The person said yes to "Let this Mac change my library".
    func allowChangesAndContinue() async {
        allowChanges()
        await library.refresh()          // lists this Mac as a device
        let work = pending
        pending = nil
        if case .needsPermission = stage { stage = .closed }
        await work?()
    }
}
