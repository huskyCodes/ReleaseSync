import ReleaseSyncKit
import SwiftUI

private func menu(for app: SyncApp, nav: Navigator) -> some View {
    Group {
        Button("Open details") { nav.open(app) }
        if let url = app.url { Link("Open on \(RepoLinks.sourceName(app.source))", destination: url) }
    }
}

/// An app in the grid.
struct AppCard: View {
    @Environment(Navigator.self) private var nav
    @Environment(LibraryModel.self) private var model
    let app: SyncApp

    var body: some View {
        let facts = ReleaseFacts(app, model.releases[app.key])
        ClickableCard(dimmed: app.hidden, action: { nav.open(app) }) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .top, spacing: 12) {
                    Avatar(name: app.title, url: facts.avatarURL(allowed: !model.isDemo))
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 6) {
                            Text(app.title).font(.cardTitle).foregroundStyle(Theme.text).lineLimit(1)
                            if app.pinned { Image(systemName: "star.fill").font(.system(size: 11)).foregroundStyle(Theme.warn).help("Favorite") }
                        }
                        Text(app.fullName).font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                }
                Text(facts.blurb).font(.dim).foregroundStyle(Theme.dim).lineLimit(2)
                    .frame(maxWidth: .infinity, minHeight: 36, alignment: .topLeading).padding(.vertical, 12)
                HStack {
                    Label(facts.versionText, systemImage: "shippingbox").font(.system(size: 12.5, weight: .semibold)).foregroundStyle(Theme.text).labelStyle(.titleAndIcon).lineLimit(1)
                    Spacer(minLength: 10)
                    Pill(text: facts.statusText, kind: facts.statusKind, dot: true)
                }
                HStack(spacing: 8) {
                    Text(facts.releasedText ?? "Added \(app.addedAt.friendlyDate)").font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
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

/// An app in the list.
struct AppRow: View {
    @Environment(Navigator.self) private var nav
    @Environment(LibraryModel.self) private var model
    let app: SyncApp

    var body: some View {
        let facts = ReleaseFacts(app, model.releases[app.key])
        ClickableCard(radius: Theme.rowRadius, padding: EdgeInsets(top: 12, leading: 16, bottom: 12, trailing: 16), dimmed: app.hidden, action: { nav.open(app) }) {
            HStack(spacing: 14) {
                Avatar(name: app.title, size: Theme.rowAvatar, url: facts.avatarURL(allowed: !model.isDemo))
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 6) {
                        Text(app.title).font(.h3).foregroundStyle(Theme.text).lineLimit(1)
                        if app.pinned { Image(systemName: "star.fill").font(.system(size: 11)).foregroundStyle(Theme.warn) }
                    }
                    Text(facts.blurb).font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
                }
                Spacer(minLength: 10)
                VStack(alignment: .leading, spacing: 5) {
                    Text(facts.versionText).font(.system(size: 12.5, weight: .semibold)).foregroundStyle(Theme.text).lineLimit(1)
                    Pill(text: facts.statusText, kind: facts.statusKind, dot: true)
                }
                .frame(minWidth: 170, alignment: .leading)
            }
        }
        .contextMenu { menu(for: app, nav: nav) }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}
