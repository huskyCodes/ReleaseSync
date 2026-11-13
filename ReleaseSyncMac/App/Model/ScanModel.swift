import AppKit
import Foundation
import Observation
import ReleaseSyncKit

/// Looking through the apps on this Mac and working out which ones come from projects ReleaseSync can follow (the Windows "Scan this PC").
/// It only reads each app's Info.plist; it asks no website.
@MainActor
@Observable
final class ScanModel {
    struct Row: Identifiable, Equatable {
        var id: String { app.path }
        let app: ScannedApp
        let match: AppMatcher.Match?
    }

    private(set) var rows: [Row] = []
    private(set) var isScanning = false
    private(set) var lastScan: Date?
    private let folders: [URL]

    init(folders: [URL]? = nil) {
        let fm = FileManager.default
        self.folders = folders ?? [URL(fileURLWithPath: "/Applications"), fm.homeDirectoryForCurrentUser.appendingPathComponent("Applications")]
    }

    /// Made-up rows for the demo.
    init(showing rows: [Row]) { self.folders = []; self.rows = rows; self.lastScan = Date() }

    func scan(library: [SyncApp]) async {
        guard !isScanning else { return }
        isScanning = true
        defer { isScanning = false }
        let folders = self.folders
        let found = await Task.detached(priority: .utility) { Self.findApps(in: folders) }.value
        let lib = library.map { (key: $0.key, repo: $0.repo, displayName: $0.displayName) }
        rows = found.map { Row(app: $0, match: AppMatcher.match($0, library: lib)) }
            .sorted { a, b in
                func rank(_ r: Row) -> Int { if case .library? = r.match { return 0 }; if case .feed? = r.match { return 1 }; return 2 }
                return rank(a) != rank(b) ? rank(a) < rank(b) : a.app.name.localizedCaseInsensitiveCompare(b.app.name) == .orderedAscending
            }
        lastScan = Date()
    }

    /// Apps in the given folders and one level of folders inside them, without Apple's own.
    nonisolated static func findApps(in folders: [URL]) -> [ScannedApp] {
        let fm = FileManager.default
        var apps: [ScannedApp] = []
        var seen = Set<String>()
        func look(_ dir: URL, depth: Int) {
            guard let items = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]) else { return }
            for url in items {
                if url.pathExtension.lowercased() == "app" {
                    guard seen.insert(url.path).inserted, let app = read(url) else { continue }
                    apps.append(app)
                } else if depth == 0, (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true { look(url, depth: 1) }
            }
        }
        for f in folders { look(f, depth: 0) }
        return apps
    }

    nonisolated static func read(_ app: URL) -> ScannedApp? {
        guard let dict = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")) as? [String: Any] else { return nil }
        let id = dict["CFBundleIdentifier"] as? String
        if id?.hasPrefix("com.apple.") == true { return nil }
        let name = (dict["CFBundleDisplayName"] as? String) ?? (dict["CFBundleName"] as? String) ?? app.deletingPathExtension().lastPathComponent
        return ScannedApp(name: name, bundleId: id, version: (dict["CFBundleShortVersionString"] as? String) ?? (dict["CFBundleVersion"] as? String),
                          path: app.path, updateFeed: dict["SUFeedURL"] as? String)
    }
}
