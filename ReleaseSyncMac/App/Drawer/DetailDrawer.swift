import ReleaseSyncKit
import SwiftUI

/// The panel that slides in from the right with an app's details (the Windows `DetailDrawer`). Everything here is read-only for now.
struct DetailDrawer: View {
    @Environment(LibraryModel.self) private var model
    let app: SyncApp
    let close: () -> Void

    var body: some View {
        let facts = ReleaseFacts(app, model.releases[app.key])
        VStack(spacing: 0) {
            header(facts).padding(.top, 22).padding(.leading, 24).padding(.trailing, 16).padding(.bottom, 16)
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    if let d = facts.description { Text(d).font(.system(size: 13)).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true).padding(.bottom, 18) }
                    if let error = facts.error, !facts.hasRelease { problem(error) }
                    label("VERSION")
                    card {
                        row("Installed", "Not installed on this Mac")
                        row("Latest release", facts.hasRelease ? facts.latestTag + (facts.info?.latestPrerelease == true ? "  (pre-release)" : "") : (facts.checked ? "None found" : "Not checked yet"))
                        if let r = facts.info?.latestPublished { row("Released", r.friendlyAgo) }
                        row("Last checked", facts.info?.lastChecked?.friendlyAgo ?? "Not yet", last: true)
                    }
                    if facts.hasRelease { notes(facts) }
                    if let releases = facts.info?.releases, !releases.isEmpty { versions(releases, latest: facts.latestTag) }
                    if facts.hasRelease { download(facts) }
                    if !isBlankText(app.note) { label("YOUR NOTES"); notesCard }
                    label("ORGANIZE")
                    card {
                        row("Group", isBlankText(app.group) ? "Automatic" : app.group!)
                        row("Favorite", app.pinned ? "Yes" : "No")
                        row("Hidden", app.hidden ? "Yes" : "No", last: app.tagList.isEmpty)
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

    // MARK: pieces

    private func header(_ facts: ReleaseFacts) -> some View {
        HStack(alignment: .top, spacing: 16) {
            Avatar(name: app.title, size: 58, url: facts.avatarURL(allowed: !model.isDemo))
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
                    Pill(text: facts.statusText, kind: facts.statusKind, dot: true)
                    Pill(text: RepoLinks.sourceName(app.source))
                    if facts.stars > 0 {
                        HStack(spacing: 4) {
                            Image(systemName: "star.fill").font(.system(size: 11)).foregroundStyle(Theme.warn)
                            Text(facts.stars.compact).font(.system(size: 12, weight: .semibold)).foregroundStyle(Theme.text)
                        }.padding(.leading, 4)
                    }
                }
                .padding(.top, 7)
            }
            Spacer(minLength: 0)
            IconButton(symbol: "xmark", help: "Close (Esc)", action: close)
        }
    }

    private func problem(_ text: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Theme.danger).padding(.top, 2)
            Text(text).font(.system(size: 13)).foregroundStyle(Theme.text).fixedSize(horizontal: false, vertical: true)
        }
        .padding(.horizontal, 14).padding(.vertical, 11).frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.dangerSoft, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .padding(.bottom, 18)
    }

    private func notes(_ facts: ReleaseFacts) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                label("WHAT'S NEW", bottom: 0)
                Spacer()
                if let u = facts.info?.releaseUrl, let url = URL(string: u), url.scheme == "https" {
                    Link("Full release page", destination: url).font(.system(size: 12, weight: .semibold)).foregroundStyle(Theme.accentText)
                }
            }
            .padding(.bottom, 8)
            let text = Fmt.stripMarkdown(facts.info?.latestNotes)
            Text(text.isEmpty ? "No release notes were provided." : text).font(.system(size: 12.5)).foregroundStyle(Theme.dim).lineSpacing(4).textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading).card(padding: EdgeInsets(top: 14, leading: 16, bottom: 14, trailing: 16)).padding(.bottom, 22)
        }
    }

    private func versions(_ releases: [ReleaseSummary], latest: String) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            label("ALL VERSIONS")
            VStack(spacing: 0) {
                ForEach(Array(releases.enumerated()), id: \.offset) { i, r in
                    HStack(spacing: 8) {
                        Text(r.tag).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text).lineLimit(1)
                        if r.prerelease { Pill(text: "pre-release", kind: .warn) }
                        if r.tag == latest { Pill(text: "latest", kind: .accent) }
                        Spacer(minLength: 8)
                        if let p = r.published { Text(p.friendlyAgo).font(.faint).foregroundStyle(Theme.faint) }
                        if let u = r.url, let url = URL(string: u), url.scheme == "https" {
                            Link(destination: url) { Image(systemName: "arrow.up.right.square").font(.system(size: 12)).foregroundStyle(Theme.dim) }.help("Open this release")
                        }
                    }
                    .padding(.vertical, 8)
                    if i < releases.count - 1 { Divider().overlay(Theme.line.opacity(0.6)) }
                }
            }
            .card(padding: EdgeInsets(top: 6, leading: 16, bottom: 6, trailing: 16)).padding(.bottom, 22)
        }
    }

    private func download(_ facts: ReleaseFacts) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            label("DOWNLOAD")
            VStack(alignment: .leading, spacing: 8) {
                if let asset = facts.macAsset {
                    Label {
                        Text(asset.name).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text).lineLimit(2).textSelection(.enabled)
                    } icon: { Image(systemName: "arrow.down.circle").foregroundStyle(Theme.accentText) }
                    Text("This is the file ReleaseSync would use on this Mac" + (asset.size > 0 ? " (\(Fmt.size(asset.size)))." : ".")).font(.faint).foregroundStyle(Theme.faint)
                } else {
                    Label("No download for the Mac in this release", systemImage: "exclamationmark.triangle.fill").font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.warn)
                    Text("ReleaseSync looks for a .dmg, .pkg or .zip made for macOS. Other systems' files are left out.").font(.faint).foregroundStyle(Theme.faint)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .card(padding: EdgeInsets(top: 14, leading: 16, bottom: 14, trailing: 16)).padding(.bottom, 22)
        }
    }

    private var notesCard: some View {
        Text(app.note ?? "").font(.system(size: 12.5)).foregroundStyle(Theme.dim).lineSpacing(3).textSelection(.enabled)
            .frame(maxWidth: .infinity, alignment: .leading).card(padding: EdgeInsets(top: 14, leading: 16, bottom: 14, trailing: 16))
            .padding(.bottom, 22)
    }

    private func label(_ text: String, bottom: CGFloat = 8) -> some View {
        Text(text).font(.label).foregroundStyle(Theme.faint).padding(.bottom, bottom)
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
