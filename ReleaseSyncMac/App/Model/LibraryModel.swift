import Foundation
import Observation
import ReleaseSyncKit

/// What the app knows about the person's synced library, and how it got it. This version only reads: it never changes the sync file.
@MainActor
@Observable
final class LibraryModel {
    enum Phase: Equatable {
        case needsToken
        /// The library is locked; `wrong` is true when a passphrase was tried and didn't open it.
        case needsPassphrase(wrong: Bool)
        case loading
        case loaded
        /// The token works but there is no ReleaseSync library in the account yet.
        case noLibrary
        case failed(String)
        /// macOS wouldn't let this app use what it saved in the Keychain (the question was declined, or this build isn't trusted for the item).
        case keychainBlocked(String)
    }

    private(set) var phase: Phase = .needsToken
    private(set) var doc: SyncDoc?
    private(set) var lastFetched: Date?
    /// Showing made-up data (a debug launch option); nothing is fetched.
    private(set) var isDemo = false

    // release data: what the sites say about each tracked app (latest version, notes, downloads), kept encrypted between runs
    private(set) var releases: [String: AppRelease] = [:]
    private(set) var isChecking = false
    private(set) var checkProgress: (done: Int, total: Int)?
    /// GitHub's allowance for requests, as of the last answer (shared with Discover).
    let meter: RateMeter
    private(set) var lastCheck: Date?
    private let client: SourceClient
    private let releaseStore: ReleaseStore
    private var cacheLoaded = false

    private let secrets: SecretStore
    /// What has been read from the Keychain this time the app is open ("nothing saved" is remembered too), so macOS is asked once per item, not on every sync.
    private var cache: [String: String?] = [:]
    private let makeTransport: @Sendable (String) -> SyncTransport
    /// Counts refreshes, so the answer to an older one never overwrites a newer one.
    private var generation = 0

    init(secrets: SecretStore, makeTransport: @escaping @Sendable (String) -> SyncTransport = { GistTransport(token: $0) },
         client: SourceClient = SourceClient(), blobs: BlobStore? = nil, meter: RateMeter? = nil) {
        self.meter = meter ?? RateMeter()
        self.secrets = secrets
        self.makeTransport = makeTransport
        self.client = client
        self.releaseStore = ReleaseStore(client: client, blobs: blobs)
    }

    /// A library to show without any account (made-up data).
    init(showing doc: SyncDoc, releases: [String: AppRelease] = [:], secrets: SecretStore, meter: RateMeter? = nil) {
        self.meter = meter ?? RateMeter(RateStatus(limit: 5000, remaining: 4962))
        self.secrets = secrets
        self.makeTransport = { _ in MemoryTransport() }
        self.client = SourceClient()
        self.releaseStore = ReleaseStore(client: client)
        self.releases = releases
        self.doc = doc
        self.phase = .loaded
        self.lastFetched = Date()
        self.isDemo = true
    }

    /// A screen to look at without any account (`--status needsToken` and so on); nothing is fetched or read from the Keychain.
    init(previewing phase: Phase, secrets: SecretStore, meter: RateMeter? = nil) {
        self.meter = meter ?? RateMeter()
        self.secrets = secrets
        self.makeTransport = { _ in MemoryTransport() }
        self.client = SourceClient()
        self.releaseStore = ReleaseStore(client: client)
        self.phase = phase
        self.isDemo = true
    }

    var apps: [SyncApp] { doc?.apps ?? [] }
    var tryLater: [TryLaterItem] { doc?.tryLater ?? [] }
    var devices: [SyncDevice] {
        (doc?.devices.values).map { Array($0).sorted { $0.lastSeen > $1.lastSeen } } ?? []
    }
    var customGroups: [String] { doc?.customGroups ?? [] }

    var isBusy: Bool { phase == .loading }

    /// Fetches the library from the person's private Gist. If called again while a fetch is running, the newest call wins.
    func refresh() async {
        guard !isDemo else { return }
        generation += 1
        let mine = generation
        do {
            guard let token = try await secret(SecretTarget.gitHubToken), !token.isEmpty else {
                guard mine == generation else { return }
                doc = nil; phase = .needsToken
                return
            }
            await client.setToken(token)
            phase = .loading
            // the passphrase is looked up only when the library turns out to be locked: most people never get that second question from macOS
            var passphrase = cache[SecretTarget.syncPassphrase] ?? nil
            var outcome = await attempt(token: token, passphrase: passphrase)
            if case .failure(let error as SyncError) = outcome, error.kind == .passphrase, cache[SecretTarget.syncPassphrase] == nil {
                passphrase = try await secret(SecretTarget.syncPassphrase)
                if let p = passphrase, !p.isEmpty { outcome = await attempt(token: token, passphrase: p) }
            }
            guard mine == generation else { return }

            switch outcome {
            case .success(let result):
                lastFetched = Date()
                if let fetched = result.doc { doc = fetched; phase = .loaded } else { doc = nil; phase = .noLibrary }
            case .failure(let error as SyncError) where error.kind == .passphrase:
                doc = nil
                phase = .needsPassphrase(wrong: !(passphrase ?? "").isEmpty)
            case .failure(let error):
                doc = nil
                phase = .failed(error.localizedDescription)
            }
        } catch {
            guard mine == generation else { return }
            doc = nil
            if let keychain = error as? KeychainError, keychain.isAccessDenied { phase = .keychainBlocked(keychain.localizedDescription) }
            else { phase = .failed(error.localizedDescription) }
        }
    }

    // MARK: release data

    /// Shows what was remembered from earlier runs straight away.
    func loadCachedReleases() async {
        guard !isDemo, !cacheLoaded else { return }
        cacheLoaded = true
        await releaseStore.load()
        releases = await releaseStore.all()
    }

    /// Looks for new releases of the apps in the library. Quick when nothing has changed: GitHub answers "not modified" and that costs
    /// nothing against its allowance. `manual` (the person pressed the button) looks at every app; otherwise recently checked ones are left alone.
    @discardableResult
    func checkForUpdates(manual: Bool) async -> CheckSummary? {
        guard !isDemo, doc != nil, !isChecking else { return nil }
        await loadCachedReleases()
        let targets = apps.filter { !$0.hidden }.map { CheckTarget(key: $0.key, source: $0.source, owner: $0.owner, repo: $0.repo, includePrereleases: $0.includePrereleases) }
        isChecking = true
        checkProgress = (0, targets.count)
        defer { isChecking = false; checkProgress = nil }
        let summary = await releaseStore.check(targets, manual: manual) { [weak self] done, total in
            Task { @MainActor in self?.checkProgress = (done, total) }
        }
        releases = await releaseStore.all()
        await meter.update(from: client)
        lastCheck = Date()
        return summary
    }

    private func attempt(token: String, passphrase: String?) async -> Result<(doc: SyncDoc?, locked: Bool), Error> {
        do { return .success(try await SyncService.fetch(transport: makeTransport(token), passphrase: passphrase)) }
        catch { return .failure(error) }
    }

    // MARK: secrets (never read on the main thread: macOS may make the app wait while it asks permission)

    private func secret(_ target: String) async throws -> String? {
        if let hit = cache[target] { return hit }
        let secrets = self.secrets
        let value = try await Task.detached { try secrets.read(target) }.value
        cache[target] = .some(value)
        return value
    }

    /// Whether something is saved. This looks at the Keychain item without opening it, so it never triggers a question.
    func hasSecret(_ target: String) async -> Bool {
        guard !isDemo else { return false }
        let secrets = self.secrets
        return await Task.detached { (try? secrets.exists(target)) ?? false }.value
    }

    /// Stores a secret in the Keychain. A token pasted with a stray space or line break at the end would be refused by GitHub,
    /// so tokens are trimmed; a passphrase is kept exactly as typed. Call `refresh()` afterwards.
    func save(_ secret: String, as target: String) async throws {
        let value = target == SecretTarget.syncPassphrase ? secret : secret.trimmingCharacters(in: .whitespacesAndNewlines)
        let secrets = self.secrets
        try await Task.detached { try secrets.write(target, value) }.value
        cache[target] = .some(value)
    }

    func remove(_ target: String) async throws {
        let secrets = self.secrets
        try await Task.detached { try secrets.delete(target) }.value
        cache[target] = .some(nil)
    }
}
