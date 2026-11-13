import Foundation

struct ProcessResult: Sendable {
    let status: Int32
    let output: String
    let error: String
    var combined: String { output + (error.isEmpty ? "" : "\n" + error) }
}

/// Runs one of macOS's own tools with an exact list of arguments (never through a shell), waits for it, and gives back what it printed.
protocol ProcessRunning: Sendable {
    func run(_ executable: String, _ arguments: [String], timeout: TimeInterval) async throws -> ProcessResult
}

private final class Buffers: @unchecked Sendable {
    private let lock = NSLock()
    private var out = Data(), err = Data()
    func append(out d: Data) { lock.lock(); out.append(d); lock.unlock() }
    func append(err d: Data) { lock.lock(); err.append(d); lock.unlock() }
    func result(status: Int32) -> ProcessResult {
        lock.lock(); defer { lock.unlock() }
        return ProcessResult(status: status, output: String(decoding: out, as: UTF8.self), error: String(decoding: err, as: UTF8.self))
    }
}

struct ProcessRunner: ProcessRunning {
    struct Failed: Error, LocalizedError { let tool: String; let reason: String; var errorDescription: String? { "\(tool) \(reason)" } }

    func run(_ executable: String, _ arguments: [String], timeout: TimeInterval = 600) async throws -> ProcessResult {
        try await withCheckedThrowingContinuation { cont in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: executable)
            process.arguments = arguments
            process.environment = ["PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "LANG": "en_US.UTF-8"]
            let out = Pipe(), err = Pipe()
            process.standardOutput = out; process.standardError = err
            process.standardInput = FileHandle.nullDevice
            let buffers = Buffers()
            out.fileHandleForReading.readabilityHandler = { h in buffers.append(out: h.availableData) }
            err.fileHandleForReading.readabilityHandler = { h in buffers.append(err: h.availableData) }
            let timer = DispatchWorkItem { if process.isRunning { process.terminate() } }
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: timer)
            process.terminationHandler = { p in
                timer.cancel()
                out.fileHandleForReading.readabilityHandler = nil; err.fileHandleForReading.readabilityHandler = nil
                buffers.append(out: out.fileHandleForReading.readDataToEndOfFile()); buffers.append(err: err.fileHandleForReading.readDataToEndOfFile())
                cont.resume(returning: buffers.result(status: p.terminationStatus))
            }
            do { try process.run() } catch { timer.cancel(); cont.resume(throwing: Failed(tool: (executable as NSString).lastPathComponent, reason: "couldn't start: \(error.localizedDescription)")) }
        }
    }
}
