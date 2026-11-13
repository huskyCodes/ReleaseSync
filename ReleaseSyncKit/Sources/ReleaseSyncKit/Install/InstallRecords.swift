import Foundation

/// What this Mac knows about an app ReleaseSync installed (or was told about). It belongs to this Mac, so it is not part of the synced library.
public struct InstallRecord: Codable, Equatable, Sendable {
    /// The app's key in the library (`github:owner/repo`).
    public var key: String
    /// The release tag it was installed from.
    public var version: String
    public var bundlePath: String
    public var bundleId: String?
    /// The version the app itself reports (CFBundleShortVersionString) when last looked at.
    public var bundleVersion: String?
    public var installedAt: Timestamp
    public var assetName: String
    public var sha256: String?
    public var signer: String?
    public var notarized: Bool?

    public init(key: String, version: String, bundlePath: String, bundleId: String? = nil, bundleVersion: String? = nil, installedAt: Timestamp, assetName: String,
                sha256: String? = nil, signer: String? = nil, notarized: Bool? = nil) {
        self.key = key; self.version = version; self.bundlePath = bundlePath; self.bundleId = bundleId; self.bundleVersion = bundleVersion
        self.installedAt = installedAt; self.assetName = assetName; self.sha256 = sha256; self.signer = signer; self.notarized = notarized
    }
}

/// The install records, kept in one small JSON file (never secrets). Writes are atomic, and a damaged file is set aside rather than lost.
public actor InstallStore {
    private let url: URL
    private var records: [String: InstallRecord] = [:]
    private var loaded = false

    public init(url: URL) { self.url = url }

    public static func defaultURL() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first ?? FileManager.default.temporaryDirectory
        return base.appendingPathComponent("ReleaseSync", isDirectory: true).appendingPathComponent("installs.json")
    }

    private func load() {
        guard !loaded else { return }
        loaded = true
        guard let data = try? Data(contentsOf: url) else { return }
        if let list = try? JSONDecoder().decode([InstallRecord].self, from: data) {
            records = Dictionary(list.map { ($0.key, $0) }, uniquingKeysWith: { _, new in new })
        } else {
            try? FileManager.default.moveItem(at: url, to: url.appendingPathExtension("damaged"))      // keep it for the person to look at; start fresh
        }
    }

    public func all() -> [String: InstallRecord] { load(); return records }
    public func record(for key: String) -> InstallRecord? { load(); return records[key] }

    public func set(_ record: InstallRecord) throws { load(); records[record.key] = record; try save() }
    public func remove(_ key: String) throws { load(); records[key] = nil; try save() }

    private func save() throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let list = records.values.sorted { $0.key < $1.key }
        let enc = JSONEncoder(); enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try enc.encode(list).write(to: url, options: .atomic)
    }
}

/// Picking the app to install out of what a disk image or zip holds.
public enum InstallPlanner {
    /// The `.app` to install among the top-level items, or nil when there is none. One app: that one. Several: the one named like the project,
    /// else the one whose name contains it, else the first by name.
    public static func chooseApp(from names: [String], repo: String, displayName: String? = nil) -> String? {
        let apps = names.filter { $0.lowercased().hasSuffix(".app") && !$0.hasPrefix(".") }.sorted { $0.localizedStandardCompare($1) == .orderedAscending }
        if apps.count <= 1 { return apps.first }
        func norm(_ s: String) -> String { s.lowercased().filter { $0.isLetter || $0.isNumber } }
        let wanted = [repo, displayName].compactMap { $0 }.map(norm).filter { !$0.isEmpty }
        func bare(_ n: String) -> String { norm(String(n.dropLast(4))) }
        if let exact = apps.first(where: { wanted.contains(bare($0)) }) { return exact }
        if let part = apps.first(where: { a in wanted.contains { bare(a).contains($0) || $0.contains(bare(a)) } }) { return part }
        return apps.first
    }

    /// A file name safe to create on disk: nothing that could climb out of a folder.
    public static func safeName(_ s: String) -> String {
        var out = s.map { c -> Character in "/\\:\0".contains(c) || c.isNewline ? "_" : c }.reduce(into: "") { $0.append($1) }
        out = out.trimmingCharacters(in: .whitespaces)
        while out.hasPrefix(".") { out.removeFirst() }
        return out.isEmpty ? "download" : String(out.prefix(120))
    }
}
