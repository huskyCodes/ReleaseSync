import Foundation

/// How an app is installed. Written as its name.
public enum InstallMode: String, Sendable, CaseIterable {
    case auto = "Auto", installer = "Installer", portable = "Portable"

    init?(json v: JSONValue) {
        switch v {
        case .string(let s): self.init(rawValue: InstallMode.allCases.first { invariantLower($0.rawValue) == invariantLower(s) }?.rawValue ?? "")
        case .number(let n): switch Int(n) { case 0: self = .auto; case 1: self = .installer; case 2: self = .portable; default: return nil }
        default: return nil
        }
    }
}

// Each model reads from and writes to a JSONValue. Reading ignores properties it doesn't know and fills in defaults for
// missing ones, like the C# engine. Writing uses the property order and the left-out-when-empty rules from docs/SYNC-FORMAT.md.

/// A tracked app, as far as the sync file knows it.
public struct SyncApp: Equatable, Sendable {
    public var key = ""
    public var source = "github"
    public var owner = ""
    public var repo = ""
    public var group: String?
    public var pinned = false
    public var hidden = false
    public var includePrereleases = false
    public var ignoreUpdates = false
    public var autoUpdate = false
    public var mode = InstallMode.auto
    public var customArgs: String?
    public var displayName: String?
    public var note: String?
    public var tags: String?
    public var skippedVersion: String?
    public var assetPattern: String?
    public var trustUnsigned = false
    public var addedAt = Timestamp.zero
    public var modifiedAt = Timestamp.zero

    public init() {}

    /// The identity an app has in the file: `source:owner/repo`, lower-case.
    public static func makeKey(source: String, owner: String, repo: String) -> String { invariantLower("\(source):\(owner)/\(repo)") }

    init(json v: JSONValue) throws {
        key = try v.string("Key") ?? ""
        source = try v.string("Source") ?? "github"
        owner = try v.string("Owner") ?? ""
        repo = try v.string("Repo") ?? ""
        group = try v.string("Group")
        pinned = try v.bool("Pinned") ?? false
        hidden = try v.bool("Hidden") ?? false
        includePrereleases = try v.bool("IncludePrereleases") ?? false
        ignoreUpdates = try v.bool("IgnoreUpdates") ?? false
        autoUpdate = try v.bool("AutoUpdate") ?? false
        if let m = v.present("Mode") {
            guard let mode = InstallMode(json: m) else { throw JSONError("Mode isn't one of Auto, Installer, Portable.") }
            self.mode = mode
        }
        customArgs = try v.string("CustomArgs")
        displayName = try v.string("DisplayName")
        note = try v.string("Note")
        tags = try v.string("Tags")
        skippedVersion = try v.string("SkippedVersion")
        assetPattern = try v.string("AssetPattern")
        trustUnsigned = try v.bool("TrustUnsigned") ?? false
        addedAt = try v.timestamp("AddedAt") ?? .zero
        modifiedAt = try v.timestamp("ModifiedAt") ?? .zero
    }

    var json: JSONValue {
        var m: [JSONMember] = [JSONMember("Key", .string(key)), JSONMember("Source", .string(source)), JSONMember("Owner", .string(owner)), JSONMember("Repo", .string(repo))]
        if let group { m.append(JSONMember("Group", .string(group))) }
        m += [JSONMember("Pinned", .bool(pinned)), JSONMember("Hidden", .bool(hidden)), JSONMember("IncludePrereleases", .bool(includePrereleases)),
              JSONMember("IgnoreUpdates", .bool(ignoreUpdates)), JSONMember("AutoUpdate", .bool(autoUpdate)), JSONMember("Mode", .string(mode.rawValue))]
        if let customArgs { m.append(JSONMember("CustomArgs", .string(customArgs))) }
        if let displayName { m.append(JSONMember("DisplayName", .string(displayName))) }
        if let note { m.append(JSONMember("Note", .string(note))) }
        if let tags { m.append(JSONMember("Tags", .string(tags))) }
        if let skippedVersion { m.append(JSONMember("SkippedVersion", .string(skippedVersion))) }
        if let assetPattern { m.append(JSONMember("AssetPattern", .string(assetPattern))) }
        m += [JSONMember("TrustUnsigned", .bool(trustUnsigned)), JSONMember("AddedAt", addedAt.json), JSONMember("ModifiedAt", modifiedAt.json)]
        return .object(m)
    }

    /// The text the merge compares when two versions have exactly the same time.
    var tieBreakText: String { JSONWriter.write(json) }
}

/// A repository saved for later. The file carries a small snapshot of it.
public struct TryLaterItem: Equatable, Sendable {
    public var owner = ""
    public var repo = ""
    public var source = "github"
    public var platform = "Windows"
    public var description: String?
    public var language: String?
    public var stars = 0
    public var avatarUrl: String?
    public var topics: [String]?
    public var savedAt = Timestamp.zero
    public var modifiedAt: Timestamp?
    public var note: String?

    public init() {}

    /// Not stored in the file: worked out from the source, owner and repo.
    public var key: String { SyncApp.makeKey(source: source, owner: owner, repo: repo) }

    /// When it last changed: the later of when it was saved and when its note changed.
    var stamp: Timestamp { if let m = modifiedAt, m > savedAt { return m }; return savedAt }

    init(json v: JSONValue) throws {
        owner = try v.string("Owner") ?? ""
        repo = try v.string("Repo") ?? ""
        source = try v.string("Source") ?? "github"
        platform = try v.string("Platform") ?? "Windows"
        description = try v.string("Description")
        language = try v.string("Language")
        stars = try v.int("Stars") ?? 0
        avatarUrl = try v.string("AvatarUrl")
        topics = try v.stringList("Topics")
        savedAt = try v.timestamp("SavedAt") ?? .zero
        modifiedAt = try v.timestamp("ModifiedAt")
        note = try v.string("Note")
    }

    var json: JSONValue {
        var m: [JSONMember] = [JSONMember("Owner", .string(owner)), JSONMember("Repo", .string(repo)), JSONMember("Source", .string(source)), JSONMember("Platform", .string(platform))]
        if let description { m.append(JSONMember("Description", .string(description))) }
        if let language { m.append(JSONMember("Language", .string(language))) }
        m.append(JSONMember("Stars", .int(stars)))
        if let avatarUrl { m.append(JSONMember("AvatarUrl", .string(avatarUrl))) }
        if let topics { m.append(JSONMember("Topics", .array(topics.map { .string($0) }))) }
        m.append(JSONMember("SavedAt", savedAt.json))
        if let modifiedAt { m.append(JSONMember("ModifiedAt", modifiedAt.json)) }
        if let note { m.append(JSONMember("Note", .string(note))) }
        return .object(m)
    }

    var tieBreakText: String { JSONWriter.write(json) }
}

/// One of the person's devices, as the others see it.
public struct SyncDevice: Equatable, Sendable {
    public var id = ""
    public var name = ""
    /// "Windows", "Mac", "Android", "iPhone"…
    public var platform = "Windows"
    public var appVersion = ""
    public var lastSeen = Timestamp.zero

    public init() {}
    public init(id: String, name: String, platform: String, appVersion: String, lastSeen: Timestamp = .zero) {
        self.id = id; self.name = name; self.platform = platform; self.appVersion = appVersion; self.lastSeen = lastSeen
    }

    init(json v: JSONValue) throws {
        id = try v.string("Id") ?? ""
        name = try v.string("Name") ?? ""
        platform = try v.string("Platform") ?? "Windows"
        appVersion = try v.string("AppVersion") ?? ""
        lastSeen = try v.timestamp("LastSeen") ?? .zero
    }

    var json: JSONValue {
        .object([JSONMember("Id", .string(id)), JSONMember("Name", .string(name)), JSONMember("Platform", .string(platform)),
                 JSONMember("AppVersion", .string(appVersion)), JSONMember("LastSeen", lastSeen.json)])
    }

    var tieBreakText: String { JSONWriter.write(json) }
}

/// The whole library document that is kept in sync.
public struct SyncDoc: Equatable, Sendable {
    /// The newest format this code understands. A file with a higher `version` is refused.
    public static let supportedVersion = 2

    public var version = SyncDoc.supportedVersion
    public var updatedAt = Timestamp.zero
    public var writtenBy = ""
    public var apps: [SyncApp] = []
    public var tryLater: [TryLaterItem] = []
    public var customGroups: [String] = []
    /// When each custom group was created, so a group that was deleted and later re-created isn't deleted again by an older deletion.
    public var groupStamps: [String: Timestamp] = [:]
    /// What was deleted and when: apps (`github:owner/repo`), try-later items (`later:github:owner/repo`) and groups (`group:name`).
    public var tombstones: [String: Timestamp] = [:]
    public var devices: [String: SyncDevice] = [:]

    public init() {}

    /// Reads a document from its text. Anything that isn't a readable document gives "The sync file isn't readable".
    public init(text: String) throws {
        do { try self.init(json: try JSONParser.parse(text)) } catch { throw SyncError.unreadable }
    }

    init(json v: JSONValue) throws {
        guard case .object = v else { throw JSONError("The document should be an object.") }
        version = try v.int("Version") ?? SyncDoc.supportedVersion
        updatedAt = try v.timestamp("UpdatedAt") ?? .zero
        writtenBy = try v.string("WrittenBy") ?? ""
        apps = try (v.array("Apps") ?? []).map(SyncApp.init(json:))
        tryLater = try (v.array("TryLater") ?? []).map(TryLaterItem.init(json:))
        customGroups = try v.stringList("CustomGroups") ?? []
        for m in try v.object("GroupStamps") ?? [] {
            guard case .string(let s) = m.value, let t = Timestamp(text: s) else { throw JSONError("GroupStamps should hold dates and times.") }
            groupStamps[m.key] = t
        }
        for m in try v.object("Tombstones") ?? [] {
            guard case .string(let s) = m.value, let t = Timestamp(text: s) else { throw JSONError("Tombstones should hold dates and times.") }
            tombstones[m.key] = t
        }
        for m in try v.object("Devices") ?? [] { devices[m.key] = try SyncDevice(json: m.value) }
    }

    var json: JSONValue {
        func stamps(_ d: [String: Timestamp]) -> JSONValue { .object(d.sortedOrdinal { $0.key }.map { JSONMember($0.key, $0.value.json) }) }
        return .object([
            JSONMember("Version", .int(version)), JSONMember("UpdatedAt", updatedAt.json), JSONMember("WrittenBy", .string(writtenBy)),
            JSONMember("Apps", .array(apps.map(\.json))), JSONMember("TryLater", .array(tryLater.map(\.json))),
            JSONMember("CustomGroups", .array(customGroups.map { .string($0) })),
            JSONMember("GroupStamps", stamps(groupStamps)), JSONMember("Tombstones", stamps(tombstones)),
            JSONMember("Devices", .object(devices.sortedOrdinal { $0.key }.map { JSONMember($0.key, $0.value.json) })),
        ])
    }

    /// The document as the text that goes into the sync file.
    public func text() -> String { JSONWriter.write(json) }
}
