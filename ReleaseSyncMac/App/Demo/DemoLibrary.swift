#if DEBUG
import Foundation
import ReleaseSyncKit

/// A made-up library for trying the app without an account: `ReleaseSync --demo`. Every name here is invented.
enum DemoLibrary {
    static func make() -> SyncDoc {
        let now = Timestamp.now()
        func ago(hours: Int64) -> Timestamp { now - Timestamp.hours(hours) }

        func app(_ owner: String, _ repo: String, name: String? = nil, group: String? = nil, pinned: Bool = false, hidden: Bool = false,
                 tags: String? = nil, note: String? = nil, source: String = "github", days: Int64 = 30, edit: ((inout SyncApp) -> Void)? = nil) -> SyncApp {
            var a = SyncApp()
            a.source = source; a.owner = owner; a.repo = repo; a.key = SyncApp.makeKey(source: source, owner: owner, repo: repo)
            a.displayName = name; a.group = group; a.pinned = pinned; a.hidden = hidden; a.tags = tags; a.note = note
            a.addedAt = now - Timestamp.days(days); a.modifiedAt = now - Timestamp.days(days / 2)
            edit?(&a)
            return a
        }

        func later(_ owner: String, _ repo: String, _ description: String, stars: Int, language: String?, topics: [String], note: String? = nil, hours: Int64, source: String = "github") -> TryLaterItem {
            var i = TryLaterItem()
            i.source = source; i.owner = owner; i.repo = repo; i.description = description; i.stars = stars; i.language = language; i.topics = topics
            i.note = note; i.savedAt = ago(hours: hours)
            return i
        }

        var doc = SyncDoc()
        doc.updatedAt = now
        doc.writtenBy = "Demo PC"
        doc.customGroups = ["Games", "Media", "Utilities"]
        doc.apps = [
            app("example-org", "pixel-player", name: "Pixel Player", group: "Media", pinned: true, tags: "video, audio", note: "Plays everything I throw at it."),
            app("example-org", "quick-notes", name: "Quick Notes", group: "Utilities", pinned: true, tags: "notes"),
            app("sample-dev", "retro-launcher", group: "Games", tags: "emulator, retro", note: "Needs the portable build.") { $0.mode = .portable },
            app("sample-dev", "tile-puzzle", group: "Games", source: "codeberg"),
            app("demo-studio", "frame-cutter", group: "Media", tags: "video, editor") { $0.includePrereleases = true },
            app("demo-studio", "wave-mixer", group: "Media", tags: "audio") { $0.autoUpdate = true },
            app("fake-labs", "disk-peek", name: "Disk Peek", group: "Utilities", tags: "disk, cleanup"),
            app("fake-labs", "clip-shelf", group: "Utilities"),
            app("example-org", "port-checker"),
            app("sample-dev", "tiny-server", source: "forgejo:git.example.test"),
            app("demo-studio", "old-tool", hidden: true, note: "Replaced by something newer."),
            app("fake-labs", "pdf-stitch", source: "gitlab") { $0.ignoreUpdates = true; $0.skippedVersion = "2.1.0" },
        ]
        doc.tryLater = [
            later("example-org", "ray-sketch", "A small, fast drawing app with layers and a calm interface.", stars: 8421, language: "Rust", topics: ["drawing", "graphics", "desktop"], hours: 3),
            later("sample-dev", "chord-finder", "Find guitar chords by ear.", stars: 312, language: "C++", topics: ["music"], note: "Ask about the portable version.", hours: 30),
            later("demo-studio", "scan-sorter", "Sorts scanned pages into folders using simple rules.", stars: 1290, language: "Go", topics: ["documents", "automation"], hours: 24 * 6),
            later("fake-labs", "text-lens", "Copy text out of any picture on your screen.", stars: 56_300, language: "Python", topics: ["ocr", "utility"], hours: 24 * 12, source: "codeberg"),
        ]
        doc.devices = [
            "demo-pc": SyncDevice(id: "demo-pc", name: "Demo PC", platform: "Windows", appVersion: "1.9.1", lastSeen: ago(hours: 2)),
            "demo-handheld": SyncDevice(id: "demo-handheld", name: "Demo Handheld", platform: "Android", appVersion: "0.3", lastSeen: ago(hours: 40)),
            "demo-laptop": SyncDevice(id: "demo-laptop", name: "Demo Laptop", platform: "Windows", appVersion: "1.9.0", lastSeen: ago(hours: 24 * 9)),
        ]
        return doc
    }
}
#endif
