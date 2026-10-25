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

    /// What the local-network link would say about the made-up devices (made-up, in memory): one on this network, two not.
    static func peers() -> [String: PeerState] {
        let now = Timestamp.now()
        return [
            "demo-pc": PeerState(online: true, installed: [
                LanInstall(key: "github:example-org/pixel-player", version: "2.3.0", installedAt: now - Timestamp.days(20), latest: "2.4.1"),
                LanInstall(key: "github:example-org/quick-notes", version: "1.8.2", latest: "1.8.2"),
                LanInstall(key: "github:sample-dev/retro-launcher", version: "0.9.0", latest: "0.10.0"),
                LanInstall(key: "github:fake-labs/disk-peek", version: "3.1.0", latest: "3.1.0"),
            ], asOf: now),
            "demo-handheld": PeerState(online: false, installed: [
                LanInstall(key: "github:example-org/quick-notes", version: "1.7.0", latest: "1.8.2"),
            ], asOf: now - Timestamp.hours(40)),
            "demo-laptop": PeerState(online: false, installed: [
                LanInstall(key: "github:example-org/pixel-player", version: "2.4.1", latest: "2.4.1"),
            ], asOf: now - Timestamp.days(9)),
        ]
    }

    /// Made-up pairings and blocked attempts for the "Find my devices" screen (nothing is paired or blocked in reality).
    @MainActor static func lan() -> LanModel {
        let now = Timestamp.now()
        let pairs = [
            LanPairRecord(pairId: "00112233445566778899aabbccddeeff", peerName: "Demo PC", peerPlatform: "Windows", role: .joiner, createdAt: now - Timestamp.days(12), peerDeviceId: "demo-pc"),
            LanPairRecord(pairId: "ffeeddccbbaa99887766554433221100", peerName: "Demo Handheld", peerPlatform: "Android", role: .host, createdAt: now - Timestamp.days(3), peerDeviceId: "demo-handheld", paused: true),
        ]
        let blocked = (0..<3).map { LanModel.BlockedAttempt(at: Date().addingTimeInterval(Double(-$0) * 600), address: "192.0.2.\(40 + $0)") }
        return LanModel(showing: pairs, blocked: blocked, secrets: LanPairSecrets(store: InMemorySecrets()))
    }

    /// Made-up release information for the made-up apps: some with Mac downloads, some Windows-only, one never checked, a couple with problems.
    static func releases() -> [String: AppRelease] {
        let now = Timestamp.now()
        func ago(days: Int64) -> Timestamp { now - Timestamp.days(days) }
        func asset(_ name: String, mb: Int64) -> AssetInfo { AssetInfo(name: name, url: "https://example.test/download/" + name, size: mb * 1_048_576, downloads: 100) }
        func entry(_ tag: String, days: Int64, notes: String, assets: [AssetInfo], desc: String, stars: Int, history: [(String, Int64, Bool)] = [], pre: Bool = false, language: String? = nil, topics: [String] = []) -> AppRelease {
            var e = AppRelease()
            e.latestTag = tag; e.latestName = "Version " + tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV")); e.latestNotes = notes
            e.latestPublished = ago(days: days); e.latestPrerelease = pre; e.releaseUrl = "https://example.test/releases/" + tag; e.assets = assets
            e.lastChecked = now - Timestamp.hours(1) / 6
            var meta = RepoMeta(); meta.description = desc; meta.stars = stars; meta.fetched = now; meta.language = language; meta.topics = topics; e.meta = meta
            var list: [ReleaseSummary] = []
            var top = ReleaseSummary(); top.tag = tag; top.name = e.latestName; top.notes = notes; top.published = ago(days: days); top.prerelease = pre; top.url = e.releaseUrl; top.assets = assets
            list.append(top)
            for (t, d, p) in history { var s = ReleaseSummary(); s.tag = t; s.published = ago(days: d); s.prerelease = p; s.url = "https://example.test/releases/" + t; list.append(s) }
            e.releases = list
            return e
        }
        let notes = "## What's new\n- Faster start-up and a calmer interface\n- **Fixed** a crash when opening large files\n- Updated the translations\n\n## Known issues\n- Dark mode icons are being redrawn"
        var out: [String: AppRelease] = [:]
        out["github:example-org/pixel-player"] = entry("v2.4.1", days: 3, notes: notes, assets: [asset("PixelPlayer-2.4.1-universal.dmg", mb: 48), asset("PixelPlayer-2.4.1-setup.exe", mb: 41)], desc: "A small, fast media player for video and audio.", stars: 12_400, history: [("v2.4.0", 20, false), ("v2.3.2", 51, false), ("v2.4.2-beta.1", 1, true)], language: "C++", topics: ["video", "audio"])
        out["github:example-org/quick-notes"] = entry("v1.9.0", days: 9, notes: notes, assets: [asset("QuickNotes-1.9.0-macos-arm64.zip", mb: 22), asset("QuickNotes-1.9.0-windows-x64.zip", mb: 25)], desc: "Take notes and keep a to-do list without leaving the keyboard.", stars: 3_150, history: [("v1.8.0", 40, false)])
        out["github:sample-dev/retro-launcher"] = entry("v1.8.0", days: 30, notes: "Adds controller profiles.", assets: [asset("RetroLauncher-1.8.0-win64.zip", mb: 60), asset("RetroLauncher-1.8.0-linux-x86_64.AppImage", mb: 62)], desc: "A front end for browsing and starting your game collection.", stars: 860)
        out["codeberg:sample-dev/tile-puzzle"] = entry("0.7.2", days: 70, notes: "Small fixes.", assets: [asset("tile-puzzle-0.7.2-macos-universal.zip", mb: 9)], desc: "A relaxing tile puzzle with hand-made levels.", stars: 95)
        out["github:demo-studio/frame-cutter"] = entry("v3.0.0-rc2", days: 2, notes: "Release candidate.", assets: [asset("FrameCutter-3.0.0-rc2.dmg", mb: 75)], desc: "Cut and join video clips without re-encoding.", stars: 5_900, history: [("v2.9.4", 25, false)], pre: true)
        out["github:demo-studio/wave-mixer"] = entry("v0.12.0", days: 120, notes: "Adds a new equalizer.", assets: [asset("WaveMixer-0.12.0.pkg", mb: 33)], desc: "A multi-track audio mixer for quick recordings.", stars: 1_020)
        out["github:fake-labs/disk-peek"] = entry("v4.1.0", days: 14, notes: notes, assets: [asset("DiskPeek-4.1.0-setup.exe", mb: 12), asset("DiskPeek-4.1.0-portable.zip", mb: 11)], desc: "See what is using your disk space, folder by folder.", stars: 22_800)
        var empty = AppRelease(); empty.lastChecked = now - Timestamp.hours(1) / 6; empty.lastError = "No releases have been published for this repository."
        var m = RepoMeta(); m.description = "Check which network ports are open."; m.stars = 40; m.fetched = now; empty.meta = m
        out["github:example-org/port-checker"] = empty
        var broken = AppRelease(); broken.lastChecked = now - Timestamp.hours(1) / 6; broken.lastError = "git.example.test returned an error (503)."
        out["forgejo:git.example.test:sample-dev/tiny-server"] = broken
        out["github:demo-studio/old-tool"] = entry("v1.0.4", days: 400, notes: "Final release.", assets: [asset("OldTool-1.0.4.dmg", mb: 5)], desc: "Replaced by something newer.", stars: 12)
        out["gitlab:fake-labs/pdf-stitch"] = entry("v2.2.0", days: 6, notes: notes, assets: [asset("pdf-stitch-2.2.0-macos.dmg", mb: 18)], desc: "Merge and split PDF files from the command line or a small window.", stars: 410)
        // clip-shelf is left out on purpose: "not checked yet"
        return out
    }

    /// Made-up projects for the Discover page.
    static func discover() -> [DiscoverItem] {
        let now = Timestamp.now()
        func repo(_ full: String, stars: Int, _ description: String, _ language: String?) -> RepoInfo {
            var r = RepoInfo(); r.fullName = full; r.name = String(full.split(separator: "/")[1]); r.ownerLogin = String(full.split(separator: "/")[0])
            r.stars = stars; r.description = description; r.language = language; r.createdAt = now - Timestamp.days(200)
            return r
        }
        let baseline = DiscoverService.baseline(ranks: ["demo-labs/rapid-sketch": [4, 7000], "demo-labs/menu-weather": [1, 11_800], "sample-co/clip-tamer": [2, 4200], "example-dev/quiet-mail": [5, 2000]], age: 20 * 3600)
        let repos = [
            repo("demo-labs/menu-weather", stars: 12_900, "A calm weather forecast that lives in your menu bar.", "Swift"),
            repo("sample-co/clip-tamer", stars: 4_700, "Keeps a searchable history of everything you copy.", "Swift"),
            repo("demo-labs/rapid-sketch", stars: 8_400, "Sketch ideas fast, with layers and a calm interface.", "Rust"),
            repo("example-dev/tiny-recorder", stars: 1_950, "Record a window or the whole screen, and share a short clip.", "C++"),
            repo("example-dev/quiet-mail", stars: 2_350, "A small mail app that stays out of your way.", "Kotlin"),
            repo("fake-works/disk-doctor", stars: 980, "Find what is filling your disk and clean it up safely.", "Go"),
        ]
        var items = DiscoverService.order(repos, mode: .trending, baseline: baseline).enumerated().map { DiscoverService.annotate($1, rank: $0 + 1, baseline: baseline) }
        for i in items.indices { items[i].hasMacBuild = i != 4 }
        return items
    }
}
#endif
