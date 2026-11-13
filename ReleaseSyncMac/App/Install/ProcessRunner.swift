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
            var outData = Data(), errData = Data()
            let lock = NSLock()
            out.fileHandleForReading.readabilityHandler = { h in let d = h.availableData; lock.lock(); outData.append(d); lock.unlock() }
            err.fileHandleForReading.readabilityHandler = { h in let d = h.availableData; lock.lock(); errData.append(d); lock.unlock() }
            let timer = DispatchWorkItem { if process.isRunning { process.terminate() } }
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: timer)
            process.terminationHandler = { p in
                timer.cancel()
                out.fileHandleForReading.readabilityHandler = nil; err.fileHandleForReading.readabilityHandler = nil
                lock.lock()
                outData.append(out.fileHandleForReading.readDataToEndOfFile()); errData.append(err.fileHandleForReading.readDataToEndOfFile())
                let result = ProcessResult(status: p.terminationStatus, output: String(decoding: outData, as: UTF8.self), error: String(decoding: errData, as: UTF8.self))
                lock.unlock()
                cont.resume(returning: result)
            }
            do { try process.run() } catch { timer.cancel(); cont.resume(throwing: Failed(tool: (executable as NSString).lastPathComponent, reason: "couldn't start: \(error.localizedDescription)")) }
        }
    }
}
