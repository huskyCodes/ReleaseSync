import CryptoKit
import Foundation

struct DownloadProgress: Sendable { var done: Int64; var total: Int64 }

/// Downloads one file. Only https is allowed (including after a redirect), the size is capped, and the file only appears under its real name
/// once it has arrived whole.
protocol Downloading: Sendable {
    func download(_ url: URL, to destination: URL, expectedSize: Int64, progress: @escaping @Sendable (DownloadProgress) -> Void) async throws
    func text(_ url: URL, maxBytes: Int) async throws -> String
}

struct DownloadError: Error, LocalizedError, Equatable {
    let message: String
    var errorDescription: String? { message }
}

final class HTTPSDownloader: NSObject, Downloading, URLSessionDownloadDelegate, @unchecked Sendable {
    static let maxBytes: Int64 = 8 * 1024 * 1024 * 1024

    private struct Job {
        let destination: URL
        let progress: @Sendable (DownloadProgress) -> Void
        let continuation: CheckedContinuation<Void, Error>
        var failure: Error?
    }
    private let lock = NSLock()
    private var jobs: [Int: Job] = [:]
    private lazy var session: URLSession = {
        let c = URLSessionConfiguration.ephemeral
        c.timeoutIntervalForRequest = 60
        c.httpAdditionalHeaders = ["User-Agent": "ReleaseSync/1.0 (macOS)"]
        return URLSession(configuration: c, delegate: self, delegateQueue: nil)
    }()

    private func checked(_ url: URL) throws {
        guard url.scheme == "https", url.host != nil else { throw DownloadError(message: "ReleaseSync only downloads over https, and this link isn't.") }
    }

    func download(_ url: URL, to destination: URL, expectedSize: Int64, progress: @escaping @Sendable (DownloadProgress) -> Void) async throws {
        try checked(url)
        try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        let task = session.downloadTask(with: url)
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                lock.lock(); jobs[task.taskIdentifier] = Job(destination: destination, progress: progress, continuation: cont); lock.unlock()
                task.resume()
            }
        } onCancel: { task.cancel() }
    }

    func text(_ url: URL, maxBytes: Int) async throws -> String {
        try checked(url)
        var request = URLRequest(url: url); request.timeoutInterval = 15
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode), data.count <= maxBytes else { throw DownloadError(message: "No checksum file.") }
        return String(decoding: data, as: UTF8.self)
    }

    // MARK: delegate (called on the session's queue)

    /// Redirects must stay on https.
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(request.url?.scheme == "https" ? request : nil)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        if totalBytesWritten > Self.maxBytes || totalBytesExpectedToWrite > Self.maxBytes {
            fail(downloadTask.taskIdentifier, DownloadError(message: "That file is larger than ReleaseSync will download."))
            downloadTask.cancel(); return
        }
        lock.lock(); let job = jobs[downloadTask.taskIdentifier]; lock.unlock()
        job?.progress(DownloadProgress(done: totalBytesWritten, total: max(totalBytesExpectedToWrite, 0)))
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        lock.lock(); let job = jobs[downloadTask.taskIdentifier]; lock.unlock()
        guard let job else { return }
        guard let http = downloadTask.response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            fail(downloadTask.taskIdentifier, DownloadError(message: "The download failed (the site answered \((downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0)).")); return
        }
        do {
            try? FileManager.default.removeItem(at: job.destination)
            try FileManager.default.moveItem(at: location, to: job.destination)      // the file only gets its real name once it has arrived whole
        } catch { fail(downloadTask.taskIdentifier, error) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock(); let job = jobs.removeValue(forKey: task.taskIdentifier); lock.unlock()
        guard let job else { return }
        if let failure = job.failure { job.continuation.resume(throwing: failure) }
        else if let error { job.continuation.resume(throwing: (error as? URLError)?.code == .cancelled ? CancellationError() : error) }
        else { job.continuation.resume() }
    }

    private func fail(_ id: Int, _ error: Error) {
        lock.lock(); jobs[id]?.failure = error; lock.unlock()
    }
}

enum FileHash {
    /// SHA-256 of a file as lower-case hex, read in pieces so a large download isn't held in memory.
    static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}
