import Foundation

/// Which of a release's downloads installs on a Mac. The Mac counterpart of `AssetPicker` in ReleaseSync.Core (which knows Windows files).
/// Understands `.dmg`, `.pkg` and `.zip`; knows Apple silicon from Intel; and skips builds for other systems, source code and checksums.
public enum MacAssets {
    public enum Arch: String, Sendable { case arm64, x64
        /// The Mac this is running on.
        public static var current: Arch {
            #if arch(arm64)
            return .arm64
            #else
            return .x64
            #endif
        }
    }

    public enum Kind: String, Sendable { case dmg, pkg, zip, unknown }

    private static let installExtensions = [".dmg", ".pkg", ".zip"]
    private static let otherSystems: Set<String> = ["linux", "windows", "win", "win32", "win64", "msvc", "w64", "android", "ios", "freebsd", "ubuntu", "debian", "fedora", "appimage", "deb", "rpm", "snap", "flatpak", "wasm", "exe", "msi", "musl", "gnu"]
    private static let macTokens: Set<String> = ["mac", "macos", "osx", "darwin", "apple", "macosx"]
    private static let junk = ["symbols", "debug", "source", "sbom", "checksum", "docs", "-src", "_src", ".src", "unins", "dsym"]

    public static func kind(of name: String) -> Kind {
        let n = name.lowercased()
        if n.hasSuffix(".dmg") { return .dmg }
        if n.hasSuffix(".pkg") { return .pkg }
        if n.hasSuffix(".zip") { return .zip }
        return .unknown
    }

    private static func tokens(_ name: String) -> [String] {
        var out: [String] = [], cur = ""
        for c in name.lowercased() { if c.isASCII && (c.isLetter || c.isNumber) { cur.append(c) } else if !cur.isEmpty { out.append(cur); cur = "" } }
        if !cur.isEmpty { out.append(cur) }
        return out
    }

    private enum Build { case arm64, x64, universal, unspecified }

    private static func build(of name: String, _ t: [String]) -> Build {
        let n = name.lowercased()
        if n.contains("universal") || t.contains("fat") { return .universal }
        if n.contains("arm64") || n.contains("aarch64") || n.contains("apple-silicon") || n.contains("applesilicon") || t.contains("silicon") || t.contains("m1") || t.contains("m2") { return .arm64 }
        if n.contains("x86_64") || n.contains("x86-64") || t.contains("x64") || t.contains("amd64") || t.contains("intel") || t.contains("x86") { return .x64 }
        return .unspecified
    }

    /// How well a download suits this Mac; nil when it can't be used (not a Mac package, for another system, source code, a build that can't run here).
    static func score(_ a: AssetInfo, arch: Arch) -> Int? {
        let name = a.name.lowercased()
        guard installExtensions.contains(where: { name.hasSuffix($0) }), a.state != "uploading" else { return nil }
        if junk.contains(where: { name.contains($0) }) { return nil }
        let t = tokens(a.name)
        let k = kind(of: a.name)
        var score = 0
        if t.contains(where: otherSystems.contains) { score -= 200 }          // a zip for Linux or Windows
        let mac = t.contains(where: macTokens.contains)
        if mac { score += 30 }

        switch (build(of: a.name, t), arch) {
        case (.universal, _): score += 35
        case (.arm64, .arm64), (.x64, .x64): score += 40
        case (.unspecified, _): score += 20
        case (.x64, .arm64): score -= 40           // runs, through Rosetta
        case (.arm64, .x64): score -= 300          // can't run
        }
        switch k {
        case .dmg: score += 25
        case .pkg: score += 15
        case .zip: score += mac ? 10 : 0
        case .unknown: break
        }
        return score
    }

    /// True when at least one download looks like it installs on a Mac (not just Windows or Linux builds, or source code).
    /// A `.dmg` or `.pkg` only runs on a Mac; a plain `.zip` needs "mac" in its name to count.
    public static func looksLikeMac(_ assets: [AssetInfo]?, arch: Arch = .current) -> Bool {
        (assets ?? []).contains { a in
            guard let s = score(a, arch: arch), s > -100 else { return false }
            let k = kind(of: a.name)
            return k == .dmg || k == .pkg || tokens(a.name).contains(where: macTokens.contains)
        }
    }

    /// All downloads that could install on this Mac, best first.
    public static func choices(_ assets: [AssetInfo]?, arch: Arch = .current) -> [AssetInfo] {
        (assets ?? []).compactMap { a in score(a, arch: arch).map { (a, $0) } }.filter { $0.1 > -100 }.sorted { $0.1 > $1.1 }.map(\.0)
    }

    /// The best download for this Mac, or the best one matching the person's saved pattern (a regular expression, ignoring case) if there is one.
    public static func pick(_ assets: [AssetInfo]?, pattern: String? = nil, arch: Arch = .current) -> AssetInfo? {
        let scored = (assets ?? []).compactMap { a in score(a, arch: arch).map { (a: a, s: $0) } }
        guard !scored.isEmpty else { return nil }
        if let pattern, !pattern.isEmpty, let rx = try? NSRegularExpression(pattern: pattern, options: .caseInsensitive) {
            let matches = scored.filter { rx.firstMatch(in: $0.a.name, range: NSRange($0.a.name.startIndex..., in: $0.a.name)) != nil }
            if let best = matches.max(by: { $0.s < $1.s }) { return best.a }
        }
        let best = scored.max { $0.s < $1.s }!
        return best.s > -100 ? best.a : nil       // something only built for another system isn't offered as the Mac download
    }
}
