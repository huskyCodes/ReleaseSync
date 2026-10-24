import ReleaseSyncKit
import SwiftUI

/// What the library knows about an app on this Mac. Version and update details come from the project's releases, which this version
/// of the Mac app doesn't look up yet, so it says so plainly instead of guessing.
private enum AppFacts {
    static let versionText = "Not installed"
    static let statusText = "Not checked yet"
    static func blurb(_ app: SyncApp) -> String {
        if !isBlankText(app.note) { return app.note! }
        if !app.tagList.isEmpty { return app.tagList.joined(separator: " · ") }
        return RepoLinks.sourceName(app.source)
    }
}

private func menu(for app: SyncApp, nav: Navigator) -> some View {
    Group {
        Button("Open details") { nav.open(app) }
        if let url = app.url { Link("Open on \(RepoLinks.sourceName(app.source))", destination: url) }
    }
}

struct AppCard: View {
    @Environment(Navigator.self) private var nav
    let app: SyncApp

    var body: some View {
        ClickableCard(dimmed: app.hidden, action: { nav.open(app) }) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .top, spacing: 12) {
                    Avatar(name: app.title)
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 6) {
                            Text(app.title).font(.cardTitle).foregroundStyle(Theme.text).lineLimit(1)
                            if app.pinned { Image(systemName: "star.fill").font(.system(size: 11)).foregroundStyle(Theme.warn).help("Favorite") }
                        }
                        Text(app.fullName).font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                }
                Text(AppFacts.blurb(app)).font(.dim).foregroundStyle(Theme.dim).lineLimit(2)
                    .frame(maxWidth: .infinity, minHeight: 36, alignment: .topLeading).padding(.vertical, 12)
                HStack {
                    Label(AppFacts.versionText, systemImage: "shippingbox").font(.system(size: 12.5, weight: .semibold)).foregroundStyle(Theme.text).labelStyle(.titleAndIcon).lineLimit(1)
                    Spacer(minLength: 10)
                    Pill(text: AppFacts.statusText, dot: true)
                }
                HStack(spacing: 8) {
                    Text("Added \(app.addedAt.friendlyDate)").font(.faint).foregroundStyle(Theme.faint)
                    Spacer()
                    if let group = app.group, !isBlankText(group) { Pill(text: group) }
                }
                .frame(minHeight: Theme.buttonHeight).padding(.top, 14)
            }
        }
        .contextMenu { menu(for: app, nav: nav) }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}

struct AppRow: View {
    @Environment(Navigator.self) private var nav
    let app: SyncApp

    var body: some View {
        ClickableCard(radius: Theme.rowRadius, padding: EdgeInsets(top: 12, leading: 16, bottom: 12, trailing: 16), dimmed: app.hidden, action: { nav.open(app) }) {
            HStack(spacing: 14) {
                Avatar(name: app.title, size: Theme.rowAvatar)
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 6) {
                        Text(app.title).font(.h3).foregroundStyle(Theme.text).lineLimit(1)
                        if app.pinned { Image(systemName: "star.fill").font(.system(size: 11)).foregroundStyle(Theme.warn) }
                    }
                    Text(AppFacts.blurb(app)).font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
                }
                Spacer(minLength: 10)
                VStack(alignment: .leading, spacing: 5) {
                    Text(AppFacts.versionText).font(.system(size: 12.5, weight: .semibold)).foregroundStyle(Theme.text)
                    Pill(text: AppFacts.statusText, dot: true)
                }
                .frame(minWidth: 170, alignment: .leading)
            }
        }
        .contextMenu { menu(for: app, nav: nav) }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}
