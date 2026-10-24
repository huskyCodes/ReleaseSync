import Foundation
import Observation

/// Short messages that appear in the corner and fade after a few seconds (the Windows app's toasts).
@MainActor
@Observable
final class Toasts {
    struct Toast: Identifiable, Equatable {
        enum Kind { case info, success, warn, error }
        let id = UUID()
        let message: String
        let kind: Kind
    }

    private(set) var items: [Toast] = []

    func show(_ message: String, _ kind: Toast.Kind = .info) {
        let toast = Toast(message: message, kind: kind)
        items.append(toast)
        if items.count > 3 { items.removeFirst() }
        Task { try? await Task.sleep(for: .seconds(kind == .error ? 8 : 4)); dismiss(toast) }
    }

    func dismiss(_ toast: Toast) { items.removeAll { $0.id == toast.id } }
}
