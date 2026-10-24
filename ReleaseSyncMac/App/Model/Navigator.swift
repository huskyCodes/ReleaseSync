import Observation
import ReleaseSyncKit

/// Which page is showing and which app's details are open. Shared so the menu bar (⌘,) and the screens can change it.
@MainActor
@Observable
final class Navigator {
    var page: Page
    var drawerApp: SyncApp?

    init(page: Page = .library) { self.page = page }

    func show(_ page: Page) { self.page = page }
    func open(_ app: SyncApp) { drawerApp = app }
    func closeDrawer() { drawerApp = nil }
}
