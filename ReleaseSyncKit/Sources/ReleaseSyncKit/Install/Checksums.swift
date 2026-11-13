import Foundation

/// Finding the checksum a project published for a download, the way the Windows app's `Verifier` does: GitHub's own digest first, then a
/// checksum file next to the download ("app.dmg.sha256", "SHA256SUMS", "checksums.txt"…).
public enum Checksums {
    private static let hashLine = try! NSRegularExpression(pattern: #"\b[0-9a-fA-F]{64}\b"#)
    private static let named = try! NSRegularExpression(pattern: #"(sha-?256|checksums?)"#, options: .caseInsensitive)
    private static let signature = try! NSRegularExpression(pattern: #"\.(sig|asc|minisig|pem|crt)$"#, options: .caseInsensitive)
    private static let shaPrefix = try! NSRegularExpression(pattern: #"^SHA-?256\s*"#, options: .caseInsensitive)
    private static let own = [".sha256", ".sha256sum", ".sha256.txt", ".sha-256"]

    /// The checksum GitHub itself lists for a download ("sha256:…"), if any.
    public static func published(by asset: AssetInfo) -> String? {
        guard let d = asset.digest, d.lowercased().hasPrefix("sha256:") else { return nil }
        let hex = String(d.dropFirst(7)).trimmingCharacters(in: .whitespaces)
        return hex.isEmpty ? nil : hex
    }

    /// The other downloads of the release that may hold a checksum for `asset`, best candidates first: its own ".sha256" file, then any file
    /// whose name mentions sha256 or checksums (signatures and certificates left out).
    public static func candidateFiles(for asset: AssetInfo, among siblings: [AssetInfo]) -> [AssetInfo] {
        let others = siblings.filter { $0.name != asset.name }
        func isOwn(_ a: AssetInfo) -> Bool { own.contains { a.name.caseInsensitiveCompare(asset.name + $0) == .orderedSame } }
        func has(_ r: NSRegularExpression, _ s: String) -> Bool { r.firstMatch(in: s, range: NSRange(s.startIndex..., in: s)) != nil }
        return others.filter(isOwn) + others.filter { has(named, $0.name) && !has(signature, $0.name) && !isOwn($0) }
    }

    /// Reads "hash  filename", "SHA256 (filename) = hash", or a lone hash for a per-file checksum file.
    public static func hash(in text: String, for fileName: String, checksumFileName: String) -> String? {
        let lines = text.replacingOccurrences(of: "\r", with: "").split(separator: "\n", omittingEmptySubsequences: true)
            .map(String.init).filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        func match(_ l: String) -> String? {
            guard let m = hashLine.firstMatch(in: l, range: NSRange(l.startIndex..., in: l)), let r = Range(m.range, in: l) else { return nil }
            return String(l[r])
        }
        for l in lines {
            guard let h = match(l) else { continue }
            var rest = l.replacingOccurrences(of: h, with: "").trimmingCharacters(in: .whitespaces)
            rest = shaPrefix.stringByReplacingMatches(in: rest, range: NSRange(rest.startIndex..., in: rest), withTemplate: "").trimmingCharacters(in: .whitespaces)
            rest = rest.trimmingCharacters(in: CharacterSet(charactersIn: "() =*"))
            let base = rest.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? ""
            if !rest.isEmpty, base.caseInsensitiveCompare(fileName) == .orderedSame { return h }
        }
        if checksumFileName.lowercased().hasPrefix(fileName.lowercased()), lines.count == 1, let h = match(lines[0]) { return h }
        return nil
    }
}
