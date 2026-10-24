import Foundation

/// The pages of the app, in the same order and with the same names as the Windows sidebar.
enum Page: String, CaseIterable, Identifiable {
    case library, updates, discover, tryLater, scan, importExport, activity, settings
    var id: String { rawValue }

    static let primary: [Page] = [.library, .updates, .discover, .tryLater, .scan, .importExport, .activity]

    var title: String {
        switch self {
        case .library: "Library"
        case .updates: "Updates"
        case .discover: "Discover"
        case .tryLater: "Try later"
        case .scan: "Scan Mac"
        case .importExport: "Import & Export"
        case .activity: "Activity"
        case .settings: "Settings"
        }
    }

    var symbol: String {
        switch self {
        case .library: "books.vertical"
        case .updates: "arrow.triangle.2.circlepath"
        case .discover: "globe"
        case .tryLater: "bookmark"
        case .scan: "magnifyingglass"
        case .importExport: "arrow.up.arrow.down"
        case .activity: "clock.arrow.circlepath"
        case .settings: "gearshape"
        }
    }

    /// Whether this version of the Mac app can do this page yet. The others show a plain "coming" screen.
    var available: Bool { self == .library || self == .tryLater || self == .discover || self == .settings }

    /// What a page that isn't ready says.
    var comingMessage: String {
        switch self {
        case .updates: "Checking your apps for new versions and installing them is the next thing coming to the Mac. It already works in ReleaseSync on Windows."
        case .discover: "Browsing trending, popular and new projects is coming to the Mac. It already works in ReleaseSync on Windows."
        case .scan: "Finding the apps already on this Mac that come from GitHub is coming. It already works on Windows."
        case .importExport: "Importing a list of links and exporting your library is coming to the Mac. It already works on Windows."
        case .activity: "Update checks and installs will show up here once the Mac app can do them."
        default: ""
        }
    }
}
