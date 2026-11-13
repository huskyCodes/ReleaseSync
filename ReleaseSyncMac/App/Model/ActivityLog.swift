import Foundation
import Observation

/// What the app has done and what went wrong, newest last, for the Activity page. Kept in memory for this run only; it never holds a path
/// outside the apps' own names, a token or a passphrase.
@MainActor
@Observable
final class ActivityLog {
    struct Entry: Identifiable, Equatable {
        enum Kind { case info, success, warn, error }
        let id = UUID()
        let at: Date
        let text: String
        let kind: Kind
    }

    private(set) var entries: [Entry] = []
    static let limit = 500

    func add(_ text: String, _ kind: Entry.Kind = .info, at: Date = Date()) {
        entries.append(Entry(at: at, text: text, kind: kind))
        if entries.count > Self.limit { entries.removeFirst(entries.count - Self.limit) }
    }

    func clear() { entries = [] }
}
