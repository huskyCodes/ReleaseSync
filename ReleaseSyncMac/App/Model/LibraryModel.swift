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
    }

    private(set) var phase: Phase = .needsToken
    private(set) var doc: SyncDoc?
    private(set) var lastFetched: Date?
    /// Showing made-up data (a debug launch option); nothing is fetched.
    private(set) var isDemo = false

    private let secrets: SecretStore
    private let makeTransport: @Sendable (String) -> SyncTransport
    /// Counts refreshes, so the answer to an older one never overwrites a newer one.
    private var generation = 0

    init(secrets: SecretStore, makeTransport: @escaping @Sendable (String) -> SyncTransport = { GistTransport(token: $0) }) {
        self.secrets = secrets
        self.makeTransport = makeTransport
    }

    /// A library to show without any account (made-up data).
    init(showing doc: SyncDoc, secrets: SecretStore) {
        self.secrets = secrets
        self.makeTransport = { _ in MemoryTransport() }
        self.doc = doc
        self.phase = .loaded
        self.lastFetched = Date()
        self.isDemo = true
    }

    /// A screen to look at without any account (`--status needsToken` and so on); nothing is fetched or read from the Keychain.
    init(previewing phase: Phase, secrets: SecretStore) {
        self.secrets = secrets
        self.makeTransport = { _ in MemoryTransport() }
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
        // the Keychain can make the app wait (macOS may ask whether this build may use an item), so it is never read on the main thread
        let secrets = self.secrets
        let token: String?, passphrase: String?
        do {
            (token, passphrase) = try await Task.detached { (try secrets.read(SecretTarget.gitHubToken), try secrets.read(SecretTarget.syncPassphrase)) }.value
        } catch {
            guard mine == generation else { return }
            doc = nil; phase = .failed(error.localizedDescription)
            return
        }
        guard let token, !token.isEmpty else { doc = nil; phase = .needsToken; return }

        phase = .loading
        let outcome: Result<(doc: SyncDoc?, locked: Bool), Error>
        do { outcome = .success(try await SyncService.fetch(transport: makeTransport(token), passphrase: passphrase)) }
        catch { outcome = .failure(error) }
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
    }

    // MARK: secrets, as the settings screen uses them (all off the main thread, see above)

    func hasSecret(_ target: String) async -> Bool {
        guard !isDemo else { return false }
        let secrets = self.secrets
        return await Task.detached { ((try? secrets.read(target)) ?? nil)?.isEmpty == false }.value
    }

    /// Stores a secret in the Keychain. A token pasted with a stray space or line break at the end would be refused by GitHub,
    /// so tokens are trimmed; a passphrase is kept exactly as typed. Call `refresh()` afterwards.
    func save(_ secret: String, as target: String) async throws {
        let value = target == SecretTarget.syncPassphrase ? secret : secret.trimmingCharacters(in: .whitespacesAndNewlines)
        let secrets = self.secrets
        try await Task.detached { try secrets.write(target, value) }.value
    }

    func remove(_ target: String) async throws {
        let secrets = self.secrets
        try await Task.detached { try secrets.delete(target) }.value
    }
}
