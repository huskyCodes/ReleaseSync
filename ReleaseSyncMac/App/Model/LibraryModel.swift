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
        let token: String?, passphrase: String?
        do {
            token = try secrets.read(SecretTarget.gitHubToken)
            passphrase = try secrets.read(SecretTarget.syncPassphrase)
        } catch {
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

    // MARK: secrets, as the settings screen uses them

    func hasSecret(_ target: String) -> Bool { ((try? secrets.read(target)) ?? nil)?.isEmpty == false }

    /// Stores a secret in the Keychain. A token pasted with a stray space or line break at the end would be refused by GitHub,
    /// so tokens are trimmed; a passphrase is kept exactly as typed. Call `refresh()` afterwards.
    func save(_ secret: String, as target: String) throws {
        try secrets.write(target, target == SecretTarget.syncPassphrase ? secret : secret.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    func remove(_ target: String) throws {
        try secrets.delete(target)
    }
}
