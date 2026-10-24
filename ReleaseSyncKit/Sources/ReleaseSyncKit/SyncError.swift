import Foundation

/// Something went wrong during a sync. The messages are plain and friendly, and the same as the Windows app's.
public struct SyncError: Error, Equatable, LocalizedError, Sendable {
    public enum Kind: String, Sendable {
        /// Can't reach the file, it is damaged, or it is from a newer version.
        case general = "SyncException"
        /// The file is locked and the passphrase is missing or wrong.
        case passphrase = "SyncPassphraseException"
    }

    public var kind: Kind
    public var message: String
    public var errorDescription: String? { message }

    public init(_ kind: Kind, _ message: String) { self.kind = kind; self.message = message }

    static func general(_ message: String) -> SyncError { SyncError(.general, message) }
    static func passphrase(_ message: String) -> SyncError { SyncError(.passphrase, message) }

    static let unreadable = general("The sync file isn't readable, so nothing was changed.")
    static let damaged = general("The sync file is damaged.")
    static let newer = general("The sync file was made by a newer version of ReleaseSync. Update ReleaseSync and try again.")
    static let locked = passphrase("This sync file is locked. Enter the passphrase you set up on your other device.")
    static let wrongPassphrase = passphrase("That passphrase doesn't open the sync file.")
}
