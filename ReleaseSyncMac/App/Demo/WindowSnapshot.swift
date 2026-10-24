#if DEBUG
import AppKit

/// `ReleaseSync --demo --snapshot /path/to/folder` saves a picture of the app's own window and quits, so the layout can be checked
/// without screen-recording access. Debug builds only. Optional `--page library|trylater|devices|settings` and `--appearance dark|light`.
enum WindowSnapshot {
    static func value(after flag: String) -> String? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: flag), i + 1 < a.count else { return nil }
        return a[i + 1]
    }

    @MainActor static func takeIfAsked() async {
        guard let folder = value(after: "--snapshot") else { return }
        try? FileManager.default.createDirectory(atPath: folder, withIntermediateDirectories: true)
        func note(_ s: String) { try? (s + "\n").write(toFile: folder + "/log.txt", atomically: false, encoding: .utf8) }
        note("started")
        if let look = value(after: "--appearance") { NSApp.appearance = NSAppearance(named: look == "dark" ? .darkAqua : .aqua) }
        try? await Task.sleep(for: .seconds(5))
        note("windows: \(NSApp.windows.map { "\($0.className) visible=\($0.isVisible) content=\($0.contentView != nil)" })")
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.contentView != nil }), let view = window.contentView?.superview ?? window.contentView else {
            FileHandle.standardError.write(Data("no window to photograph\n".utf8)); NSApp.terminate(nil); return
        }
        func dump(_ v: NSView, _ depth: Int) -> String {
            let line = String(repeating: "  ", count: depth) + "\(type(of: v)) frame=\(v.frame.debugDescription) hidden=\(v.isHidden)\n"
            return depth >= 7 ? line : line + v.subviews.map { dump($0, depth + 1) }.joined()
        }
        try? dump(view, 0).write(toFile: folder + "/tree.txt", atomically: false, encoding: .utf8)
        let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds)!
        view.cacheDisplay(in: view.bounds, to: rep)
        let name = value(after: "--name") ?? "window"
        try? FileManager.default.createDirectory(atPath: folder, withIntermediateDirectories: true)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: folder).appendingPathComponent(name + ".png"))
        NSApp.terminate(nil)
    }
}
#endif
