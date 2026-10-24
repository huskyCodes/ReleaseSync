import Foundation
import ReleaseSyncKit

/// Where a project lives on the web, and what to call the place. These names are used only to say what ReleaseSync works with.
enum RepoLinks {
    static func sourceName(_ source: String) -> String {
        switch source {
        case "github": return "GitHub"
        case "gitlab": return "GitLab"
        case "codeberg": return "Codeberg"
        default:
            if source.hasPrefix("forgejo:") { return String(source.dropFirst("forgejo:".count)) }
            return source
        }
    }

    /// The project's page, or nil if the pieces don't look like a real address (the library file is data, not instructions).
    static func url(source: String, owner: String, repo: String) -> URL? {
        let host: String
        switch source {
        case "github": host = "github.com"
        case "gitlab": host = "gitlab.com"
        case "codeberg": host = "codeberg.org"
        default:
            guard source.hasPrefix("forgejo:") else { return nil }
            host = String(source.dropFirst("forgejo:".count))
            guard host.range(of: #"^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(:[0-9]{1,5})?$"#, options: .regularExpression) != nil else { return nil }
        }
        // letters, digits, dots, dashes and underscores only (an owner may have sub-groups), and no "." or ".." steps
        let segments = owner.split(separator: "/", omittingEmptySubsequences: false) + [Substring(repo)]
        guard segments.allSatisfy({ $0.range(of: #"^[A-Za-z0-9_.-]+$"#, options: .regularExpression) != nil && $0 != "." && $0 != ".." }) else { return nil }
        return URL(string: "https://\(host)/\(owner)/\(repo)")
    }
}

extension SyncApp {
    var title: String { isBlankText(displayName) ? repo : displayName! }
    var fullName: String { "\(owner)/\(repo)" }
    var url: URL? { RepoLinks.url(source: source, owner: owner, repo: repo) }
    var tagList: [String] {
        (tags ?? "").split(whereSeparator: { $0 == "," || $0 == ";" }).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }
}

extension TryLaterItem {
    var fullName: String { "\(owner)/\(repo)" }
    var url: URL? { RepoLinks.url(source: source, owner: owner, repo: repo) }
}

extension InstallMode {
    var friendlyName: String {
        switch self {
        case .auto: return "Chosen automatically"
        case .installer: return "Run the installer"
        case .portable: return "Unpack and run"
        }
    }
}

func isBlankText(_ s: String?) -> Bool { s?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true }

extension Timestamp {
    /// "3 hours ago". A time that was never set shows as "never".
    var friendlyAgo: String {
        if self == .zero { return "never" }
        return RelativeDateTimeFormatter.named.localizedString(for: date, relativeTo: Date())
    }
    var friendlyDate: String {
        self == .zero ? "unknown" : date.formatted(date: .abbreviated, time: .omitted)
    }
}

private extension RelativeDateTimeFormatter {
    static let named: RelativeDateTimeFormatter = { let f = RelativeDateTimeFormatter(); f.unitsStyle = .full; f.dateTimeStyle = .named; return f }()
}

extension Int {
    /// 1200 becomes "1.2K".
    var compact: String { formatted(.number.notation(.compactName)) }
}

extension SyncDevice {
    var symbolName: String {
        switch platform.lowercased() {
        case "windows": return "pc"
        case "mac": return "laptopcomputer"
        case "iphone", "ios": return "iphone"
        case "ipad": return "ipad"
        case "android": return "smartphone"
        default: return "desktopcomputer"
        }
    }
}
