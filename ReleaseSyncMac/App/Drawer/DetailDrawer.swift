import ReleaseSyncKit
import SwiftUI

/// The panel that slides in from the right with an app's details (the Windows `DetailDrawer`). Everything here is read-only for now.
struct DetailDrawer: View {
    let app: SyncApp
    let close: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            header.padding(.top, 22).padding(.leading, 24).padding(.trailing, 16).padding(.bottom, 16)
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    if !isBlankText(app.note) { label("YOUR NOTES"); notes }
                    label("VERSION")
                    card {
                        row("Installed", "Not installed on this Mac")
                        row("Latest release", "Not checked yet")
                        row("Added", app.addedAt.friendlyDate)
                        row("Last changed", app.modifiedAt.friendlyDate, last: true)
                    }
                    label("ORGANIZE")
                    card {
                        row("Group", isBlankText(app.group) ? "Automatic" : app.group!)
                        row("Favorite", app.pinned ? "Yes" : "No")
                        row("Hidden", app.hidden ? "Yes" : "No", last: isBlankText(app.tags))
                        if !app.tagList.isEmpty {
                            HStack(alignment: .top) {
                                Text("Tags").font(.dim).foregroundStyle(Theme.dim)
                                Spacer(minLength: 12)
                                ChipFlow { ForEach(app.tagList, id: \.self) { Pill(text: $0) } }.frame(maxWidth: 240, alignment: .trailing)
                            }.padding(.vertical, 9)
                        }
                    }
                    label("OPTIONS")
                    card {
                        row("Install", app.mode.friendlyName)
                        row("Pre-releases", app.includePrereleases ? "Included" : "Not included")
                        row("Update automatically", app.autoUpdate ? "Yes" : "No")
                        row("Updates", app.ignoreUpdates ? "Ignored" : "Watched")
                        if let v = app.skippedVersion, !v.isEmpty { row("Skipped version", v) }
                        if let p = app.assetPattern, !p.isEmpty { row("Preferred file", p) }
                        if let a = app.customArgs, !a.isEmpty { row("Installer arguments", a) }
                        row("Unsigned apps", app.trustUnsigned ? "Trusted" : "Not trusted", last: true)
                    }
                    Text("To change any of this, use ReleaseSync on your other device for now. The Mac app only reads your library.")
                        .font(.faint).foregroundStyle(Theme.faint).fixedSize(horizontal: false, vertical: true)
                }
                .padding(.horizontal, 24).padding(.bottom, 28)
            }
        }
    }

    private var header: some View {
        HStack(alignment: .top, spacing: 16) {
            Avatar(name: app.title, size: 58)
            VStack(alignment: .leading, spacing: 3) {
                Text(app.title).font(.system(size: 20, weight: .semibold)).foregroundStyle(Theme.text).lineLimit(2)
                if let url = app.url {
                    Link(destination: url) {
                        HStack(spacing: 6) { Text(app.fullName).font(.system(size: 12.5)); Image(systemName: "arrow.up.right").font(.system(size: 9, weight: .semibold)) }
                    }
                    .foregroundStyle(Theme.accentText).help("Open on \(RepoLinks.sourceName(app.source))")
                } else {
                    Text(app.fullName).font(.system(size: 12.5)).foregroundStyle(Theme.dim)
                }
                HStack(spacing: 8) {
                    Pill(text: "Not checked yet", dot: true)
                    Pill(text: RepoLinks.sourceName(app.source))
                }
                .padding(.top, 7)
            }
            Spacer(minLength: 0)
            IconButton(symbol: "xmark", help: "Close (Esc)", action: close)
        }
    }

    private var notes: some View {
        Text(app.note ?? "").font(.system(size: 12.5)).foregroundStyle(Theme.dim).lineSpacing(3).textSelection(.enabled)
            .frame(maxWidth: .infinity, alignment: .leading).card(padding: EdgeInsets(top: 14, leading: 16, bottom: 14, trailing: 16))
            .padding(.bottom, 22)
    }

    private func label(_ text: String) -> some View {
        Text(text).font(.label).foregroundStyle(Theme.faint).padding(.bottom, 8)
    }

    private func card<C: View>(@ViewBuilder _ content: () -> C) -> some View {
        VStack(spacing: 0, content: content).card(padding: EdgeInsets(top: 6, leading: 16, bottom: 6, trailing: 16)).padding(.bottom, 22)
    }

    private func row(_ name: String, _ value: String, last: Bool = false) -> some View {
        VStack(spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                Text(name).font(.dim).foregroundStyle(Theme.dim)
                Spacer(minLength: 12)
                Text(value).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text).lineLimit(2).multilineTextAlignment(.trailing).textSelection(.enabled)
            }
            .padding(.vertical, 9)
            if !last { Divider().overlay(Theme.line.opacity(0.6)) }
        }
    }
}
