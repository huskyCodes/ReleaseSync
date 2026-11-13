import ReleaseSyncKit
import SwiftUI

/// The panel that slides in from the right with an app's details (the Windows `DetailDrawer`). Organizing (favorite, hidden, group, notes, removing)
/// can be changed here once the person has let this Mac change their library; the rest is read-only for now.
struct DetailDrawer: View {
    @Environment(LibraryModel.self) private var model
    @Environment(Toasts.self) private var toasts
    @Environment(InstallModel.self) private var install
    @State private var askUninstall = false
    let opened: SyncApp
    let close: () -> Void
    @State private var noteText = ""
    @State private var noteLoadedFor = ""
    @State private var newGroup = ""
    @State private var askNewGroup = false
    @State private var askRemove = false

    init(app: SyncApp, close: @escaping () -> Void) { self.opened = app; self.close = close }

    /// The app as it is now (the panel keeps showing it after a change is saved).
    private var app: SyncApp { model.apps.first { $0.key == opened.key } ?? opened }

    var body: some View {
        let facts = install.facts(app, model.releases[app.key])
        VStack(spacing: 0) {
            header(facts).padding(.top, 22).padding(.leading, 24).padding(.trailing, 16).padding(.bottom, 16)
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    if let d = facts.description { Text(d).font(.system(size: 13)).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true).padding(.bottom, 18) }
                    if let error = facts.error, !facts.hasRelease { problem(error) }
                    label("VERSION")
                    card {
                        row("Installed", facts.isInstalled ? facts.installed! + (facts.versionUnrecognized ? "  (not a recent release)" : "") : "Not installed on this Mac")
                        row("Latest release", facts.hasRelease ? facts.latestTag + (facts.info?.latestPrerelease == true ? "  (pre-release)" : "") : (facts.checked ? "None found" : "Not checked yet"))
                        if let r = facts.info?.latestPublished { row("Released", r.friendlyAgo) }
                        row("Last checked", facts.info?.lastChecked?.friendlyAgo ?? "Not yet", last: true)
                    }
                    onDevices
                    actions(facts)
                    if facts.hasRelease { notes(facts) }
                    if let releases = facts.info?.releases, !releases.isEmpty { versions(releases, latest: facts.latestTag) }
                    if facts.hasRelease { download(facts) }
                    if model.canEdit { notesEditor } else if !isBlankText(app.note) { label("YOUR NOTES"); notesCard }
                    label("ORGANIZE")
                    if model.canEdit { organizeEditable } else { organizeReadOnly }
                    label("OPTIONS")
                    if model.canEdit { optionsEditable } else {
                        card {
                            row("Install", app.mode.friendlyName)
                            row("Pre-releases", app.includePrereleases ? "Included" : "Not included")
                            row("Updates", app.ignoreUpdates ? "Ignored" : "Watched")
                            if let v = app.skippedVersion, !v.isEmpty { row("Skipped version", v) }
                            if let p = app.assetPattern, !p.isEmpty { row("Preferred file", p) }
                            row("Unsigned apps", app.trustUnsigned ? "Trusted" : "Not trusted", last: true)
                        }
                    }
                    Text(model.canEdit ? "Changes here are saved to your library and shared with your other devices."
                                       : "To change any of this, use ReleaseSync on your other device for now. The Mac app only reads your library (you can allow changes in Settings).")
                        .font(.faint).foregroundStyle(Theme.faint).fixedSize(horizontal: false, vertical: true)
                }
                .padding(.horizontal, 24).padding(.bottom, 28)
            }
        }
    }

    // MARK: installing

    @ViewBuilder private func actions(_ facts: ReleaseFacts) -> some View {
        let working = install.progress(for: app.key) != nil
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                InstallButton(app: app, facts: facts)
                if facts.hasUpdate && !working && model.canEdit {
                    let tag = facts.latestTag
                    Button("Skip \(tag)") { change { LibraryEdits.setSkippedVersion(app.key, tag, in: &$0, now: $1) } }.buttonStyle(OutlineButtonStyle(height: 28))
                        .help("Don't offer this version again. A newer one will still be offered.")
                }
                if facts.isInstalled && !working {
                    Button("Open") { install.open(app) }.buttonStyle(OutlineButtonStyle(height: 28))
                    Button("Show in Finder") { install.reveal(app) }.buttonStyle(OutlineButtonStyle(height: 28))
                    if facts.hasRelease, facts.macAsset != nil, !facts.hasUpdate {
                        Button("Reinstall") { Task { await install.install(app, info: facts.info, tag: facts.installed) } }.buttonStyle(OutlineButtonStyle(height: 28))
                            .help("Download this version again and put it back in place")
                    }
                    Button("Uninstall") { askUninstall = true }.buttonStyle(OutlineButtonStyle(height: 28))
                }
            }
            if let why = facts.noInstallableFileText { Text(why).font(.faint).foregroundStyle(Theme.warn).fixedSize(horizontal: false, vertical: true) }
            if let record = install.records[app.key], facts.isInstalled {
                Text("Installed from \(record.assetName)" + (record.notarized == true ? ", signed and notarized" : ""))
                    .font(.faint).foregroundStyle(Theme.faint)
            }
        }
        .padding(.bottom, 22)
        .confirmationDialog("Uninstall \(app.title)?", isPresented: $askUninstall, titleVisibility: .visible) {
            Button("Move to Trash", role: .destructive) { Task { await install.uninstall(app) } }
            Button("Cancel", role: .cancel) {}
        } message: { Text("The app is moved to the Trash. Its settings and data are left alone.") }
    }

    // MARK: changing the library

    private func change(_ what: @escaping (inout SyncDoc, Timestamp) -> Bool) {
        Task { if let problem = await model.edit(what) { toasts.show(problem, .error) } }
    }

    private var organizeReadOnly: some View {
        card {
            row("Group", isBlankText(app.group) ? "Automatic" : app.group!)
            row("Favorite", app.pinned ? "Yes" : "No")
            row("Hidden", app.hidden ? "Yes" : "No", last: app.tagList.isEmpty)
            tags
        }
    }

    @ViewBuilder private var tags: some View {
        if !app.tagList.isEmpty {
            HStack(alignment: .top) {
                Text("Tags").font(.dim).foregroundStyle(Theme.dim)
                Spacer(minLength: 12)
                ChipFlow { ForEach(app.tagList, id: \.self) { Pill(text: $0) } }.frame(maxWidth: 240, alignment: .trailing)
            }.padding(.vertical, 9)
        }
    }

    private var organizeEditable: some View {
        let key = app.key
        return VStack(alignment: .leading, spacing: 0) {
            card {
                HStack {
                    Text("Group").font(.dim).foregroundStyle(Theme.dim)
                    Spacer(minLength: 12)
                    Menu {
                        Button("Automatic") { change { LibraryEdits.setGroup(key, nil, in: &$0, now: $1) } }
                        Divider()
                        ForEach(model.customGroups, id: \.self) { g in Button(g) { change { LibraryEdits.setGroup(key, g, in: &$0, now: $1) } } }
                        Divider()
                        Button("New group…") { newGroup = ""; askNewGroup = true }
                    } label: { Text(isBlankText(app.group) ? "Automatic" : app.group!).font(.system(size: 13, weight: .semibold)) }
                    .menuStyle(.button).buttonStyle(.plain).fixedSize()
                }.padding(.vertical, 9)
                Divider().overlay(Theme.line.opacity(0.6))
                Toggle("Favorite", isOn: Binding(get: { app.pinned }, set: { on in change { LibraryEdits.setFavorite(key, on, in: &$0, now: $1) } }))
                    .toggleStyle(.switch).font(.dim).padding(.vertical, 9)
                Divider().overlay(Theme.line.opacity(0.6))
                Toggle("Hidden", isOn: Binding(get: { app.hidden }, set: { on in change { LibraryEdits.setHidden(key, on, in: &$0, now: $1) } }))
                    .toggleStyle(.switch).font(.dim).padding(.vertical, 9)
                tags
            }
            Button(role: .destructive) { askRemove = true } label: { Label("Remove from my library", systemImage: "trash") }
                .buttonStyle(OutlineButtonStyle()).padding(.bottom, 22)
        }
        .alert("New group", isPresented: $askNewGroup) {
            TextField("Group name", text: $newGroup)
            Button("Create") { let name = newGroup; change { LibraryEdits.setGroup(key, name, in: &$0, now: $1) } }
            Button("Cancel", role: .cancel) {}
        }
        .confirmationDialog("Remove \(app.title) from your library?", isPresented: $askRemove, titleVisibility: .visible) {
            Button("Remove", role: .destructive) { change { LibraryEdits.removeApp(key, in: &$0, now: $1) } }
            Button("Cancel", role: .cancel) {}
        } message: { Text("It is removed on all your devices. You can add it again later.") }
    }

    private var optionsEditable: some View {
        let key = app.key
        return card {
            Toggle("Include pre-releases", isOn: Binding(get: { app.includePrereleases }, set: { on in change { LibraryEdits.setIncludePrereleases(key, on, in: &$0, now: $1) } }))
                .toggleStyle(.switch).font(.dim).padding(.vertical, 9)
            Divider().overlay(Theme.line.opacity(0.6))
            Toggle("Ignore updates", isOn: Binding(get: { app.ignoreUpdates }, set: { on in change { LibraryEdits.setIgnoreUpdates(key, on, in: &$0, now: $1) } }))
                .toggleStyle(.switch).font(.dim).padding(.vertical, 9)
            Divider().overlay(Theme.line.opacity(0.6))
            Toggle("Don't ask before installing it unsigned", isOn: Binding(get: { app.trustUnsigned }, set: { on in change { LibraryEdits.setTrustUnsigned(key, on, in: &$0, now: $1) } }))
                .toggleStyle(.switch).font(.dim).padding(.vertical, 9)
            if let v = app.skippedVersion, !v.isEmpty {
                Divider().overlay(Theme.line.opacity(0.6))
                HStack {
                    Text("Skipped version").font(.dim).foregroundStyle(Theme.dim); Spacer()
                    Text(v).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text)
                    Button("Stop skipping") { change { LibraryEdits.setSkippedVersion(key, nil, in: &$0, now: $1) } }.buttonStyle(OutlineButtonStyle(height: 24))
                }.padding(.vertical, 9)
            }
            if let p = app.assetPattern, !p.isEmpty {
                Divider().overlay(Theme.line.opacity(0.6))
                HStack {
                    Text("Preferred file").font(.dim).foregroundStyle(Theme.dim); Spacer(minLength: 12)
                    Text(p).font(.system(size: 11.5, design: .monospaced)).foregroundStyle(Theme.faint).lineLimit(1).truncationMode(.middle)
                    Button("Reset") { change { LibraryEdits.setAssetPattern(key, nil, in: &$0, now: $1) } }.buttonStyle(OutlineButtonStyle(height: 24))
                }.padding(.vertical, 9)
            }
        }
    }

    private var notesEditor: some View {
        let key = app.key
        return VStack(alignment: .leading, spacing: 0) {
            label("YOUR NOTES")
            TextEditor(text: $noteText).font(.system(size: 12.5)).scrollContentBackground(.hidden).frame(minHeight: 70, maxHeight: 140)
                .padding(10).background(Theme.input, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
            HStack {
                Spacer()
                Button("Save note") { let text = noteText; change { LibraryEdits.setNote(key, text, in: &$0, now: $1) } }
                    .buttonStyle(OutlineButtonStyle(height: 30)).disabled(noteText.trimmingCharacters(in: .whitespacesAndNewlines) == (app.note ?? ""))
            }.padding(.top, 8).padding(.bottom, 22)
        }
        .onAppear { loadNote() }
        .onChange(of: opened.key) { _, _ in loadNote() }
    }

    private func loadNote() { noteText = app.note ?? ""; noteLoadedFor = app.key }

    // MARK: pieces

    /// Where this app is installed on the person's other devices, as those devices report it.
    @ViewBuilder private var onDevices: some View {
        let installs = DeviceInventory.installs(of: app.key, on: model.devices, peers: model.peers)
        if !installs.isEmpty {
            label("ON YOUR DEVICES")
            VStack(spacing: 0) {
                ForEach(Array(installs.enumerated()), id: \.element.id) { i, item in
                    VStack(spacing: 0) {
                        HStack(spacing: 10) {
                            StatusLight(on: item.online)
                            Text(item.device.name.isEmpty ? "Unnamed device" : item.device.name).font(.dim).foregroundStyle(Theme.dim)
                            Spacer(minLength: 12)
                            if item.install.hasUpdate, let latest = item.install.latest { Pill(text: "Update to \(latest)", kind: .warn) }
                            Text(item.install.version.isEmpty ? "Installed" : item.install.version).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text)
                        }
                        .padding(.vertical, 9)
                        if i < installs.count - 1 { Divider().overlay(Theme.line.opacity(0.6)) }
                    }
                }
            }
            .card(padding: EdgeInsets(top: 6, leading: 16, bottom: 6, trailing: 16)).padding(.bottom, 22)
        }
    }

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
                        versionButton(r)
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

    /// Installs that exact version: a newer one, the same one again, or an older one to roll back to.
    @ViewBuilder private func versionButton(_ r: ReleaseSummary) -> some View {
        let facts = install.facts(app, model.releases[app.key])
        if install.progress(for: app.key) == nil, MacAssets.pick(r.assets, pattern: app.assetPattern) != nil, r.tag != facts.installed || facts.isInstalled {
            let title: String = {
                guard let have = facts.installed else { return "Install" }
                if r.tag == have { return "Reinstall" }
                return facts.isNewer(r.tag, than: have) ? "Update to this" : "Roll back"
            }()
            Button(title) { Task { await install.install(app, info: facts.info, tag: r.tag) } }.buttonStyle(OutlineButtonStyle(height: 24))
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
