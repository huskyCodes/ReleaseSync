import Foundation

/// Somewhere a single sync file can be kept: a shared folder, a private GitHub Gist, memory (tests).
public protocol SyncTransport: Sendable {
    var description: String { get }
    /// The file's text, or nil if nothing has been stored yet.
    func read() async throws -> String?
    func write(_ text: String) async throws
}

/// Keeps the file in memory. Several simulated devices can share one.
public actor MemoryTransport: SyncTransport {
    public private(set) var text: String?
    public private(set) var reads = 0
    public private(set) var writes = 0

    public init(text: String? = nil) { self.text = text }
    public nonisolated var description: String { "memory" }
    public func read() async throws -> String? { reads += 1; return text }
    public func write(_ t: String) async throws { writes += 1; text = t }
}

/// A file in a folder that something else (iCloud Drive, Dropbox, Syncthing…) copies between devices.
public struct FolderTransport: SyncTransport {
    public let file: URL
    public init(file: URL) { self.file = file }
    public var description: String { "shared folder" }

    public func read() async throws -> String? {
        guard FileManager.default.fileExists(atPath: file.path) else { return nil }
        var data = try Data(contentsOf: file)
        if data.starts(with: [0xEF, 0xBB, 0xBF]) { data.removeFirst(3) }
        guard let s = String(data: data, encoding: .utf8) else { throw SyncError.damaged }
        return s
    }

    public func write(_ text: String) async throws {
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: file, options: .atomic)      // written whole or not at all, with no byte-order mark
    }
}
