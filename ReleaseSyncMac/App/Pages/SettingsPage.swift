import ReleaseSyncKit
import SwiftUI

/// Settings, in the same single page of cards as the Windows app. Secrets go to the Keychain and are never shown again.
struct SettingsPage: View {
    @Environment(LibraryModel.self) private var model
    @Environment(ViewPrefs.self) private var prefs
    @Environment(Toasts.self) private var toasts
    @State private var confirmChanges = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                PageHeader(title: "Settings", subtitle: "Everything is saved automatically.")

                SettingsCard {
                    SecretField(title: "GitHub access token", target: SecretTarget.gitHubToken, prompt: "Paste your token",
                                saved: "A token is saved in your Keychain.", empty: "No token saved yet.",
                                help: "Use the token you sync with on your other devices. It needs the “gist” permission. It is kept in your Mac's Keychain and sent only to GitHub.",
                                link: ("Create a token on GitHub", URL(string: "https://github.com/settings/tokens/new?scopes=gist&description=ReleaseSync")!))
                }

                SettingsCard {
                    Text("Your devices and sharing").font(.h2).foregroundStyle(Theme.text)
                    Text("Your library is kept the same on every device through a private GitHub Gist in your own account.").font(.dim).foregroundStyle(Theme.dim)
                    SecretField(title: "Sync passphrase", target: SecretTarget.syncPassphrase, prompt: "Type your passphrase",
                                saved: "A passphrase is saved in your Keychain.", empty: "No passphrase saved.",
                                help: "Only needed if you locked your library with a passphrase on another device. Type it exactly as you set it.", heading: false)
                    HStack {
                        Button { Task { await syncNow(model, toasts) } } label: {
                            HStack(spacing: 8) { Image(systemName: "arrow.clockwise"); Text(model.isBusy ? "Checking…" : "Sync now") }
                        }
                        .buttonStyle(OutlineButtonStyle()).disabled(model.isBusy)
                        if let last = model.lastFetched, model.phase == .loaded {
                            Text("Checked \(RelativeDateTimeFormatter.short.localizedString(for: last, relativeTo: Date()))").font(.faint).foregroundStyle(Theme.faint)
                        }
                    }
                    devices
                    changesSwitch
                }

                SettingsCard {
                    Text("Updates").font(.h2).foregroundStyle(Theme.text)
                    Text("ReleaseSync checks for new versions of your apps while it is open. Checks that find nothing new cost nothing against GitHub's limit.").font(.dim).foregroundStyle(Theme.dim)
                    HStack {
                        Text("Check for new versions").font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text)
                        Spacer()
                        Picker("", selection: Binding(get: { prefs.checkEveryHours }, set: { prefs.checkEveryHours = $0 })) {
                            Text("Never").tag(0); Text("Every hour").tag(1); Text("Every 3 hours").tag(3); Text("Every 6 hours").tag(6); Text("Every 12 hours").tag(12); Text("Once a day").tag(24)
                        }.labelsHidden().frame(width: 160)
                    }
                    Toggle("Tell me about new versions", isOn: Binding(get: { prefs.notifyOfUpdates }, set: { prefs.notifyOfUpdates = $0 })).toggleStyle(.switch)
                    Text("You get one notification for each new version. Apps you set to update themselves are updated without asking.").font(.faint).foregroundStyle(Theme.faint)
                }

                SettingsCard { FindDevicesSection() }

                SettingsCard {
                    Text("Appearance").font(.h2).foregroundStyle(Theme.text)
                    Text("Follow your Mac, or always use light or dark.").font(.dim).foregroundStyle(Theme.dim)
                    HStack(spacing: 8) {
                        ForEach(ViewPrefs.Appearance.allCases, id: \.self) { a in
                            Chip(title: a.rawValue, selected: prefs.appearance == a) { prefs.appearance = a }
                        }
                    }
                }

                SettingsCard {
                    HStack(spacing: 14) {
                        BrandTile(size: 44)
                        VStack(alignment: .leading, spacing: 2) {
                            Text("ReleaseSync for Mac  \(Self.version)").font(.h2).foregroundStyle(Theme.text)
                            Text("Track apps from GitHub, GitLab and Codeberg and keep your library in sync across your devices.").font(.dim).foregroundStyle(Theme.dim)
                        }
                    }
                    Text("ReleaseSync isn't affiliated with GitHub, GitLab, Codeberg or Apple. It collects nothing about you, has no accounts, and talks only to the sites your apps come from and to GitHub for your library.")
                        .font(.faint).foregroundStyle(Theme.faint).fixedSize(horizontal: false, vertical: true)
                }
            }
            .frame(maxWidth: 820, alignment: .leading)
            .padding(.horizontal, Theme.pagePadding).padding(.top, 14).padding(.bottom, 32)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private static var version: String { (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String) ?? "" }

    /// Off by default. Turning it on is the moment this Mac starts writing to the sync file, so it asks first.
    @ViewBuilder private var changesSwitch: some View {
        Divider().overlay(Theme.line.opacity(0.6))
        Toggle(isOn: Binding(get: { prefs.allowLibraryChanges }, set: { on in if on { confirmChanges = true } else { prefs.allowLibraryChanges = false } })) {
            Text("Let this Mac change my library").font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text)
        }
        .toggleStyle(.switch)
        Text(prefs.allowLibraryChanges ? "On: favorites, groups, notes and removals you make here are saved to your library, and this Mac is listed as a device."
                                       : "Off: this Mac only reads your library.")
            .font(.faint).foregroundStyle(Theme.faint).fixedSize(horizontal: false, vertical: true)
        .alert("Let this Mac change your library?", isPresented: $confirmChanges) {
            Button("Turn on") { prefs.allowLibraryChanges = true; Task { await model.refresh() } }
            Button("Not now", role: .cancel) {}
        } message: {
            Text("This Mac will save your changes to your private Gist and show up in your list of devices. Your other devices keep working as they do now.")
        }
    }

    @ViewBuilder private var devices: some View {
        if model.phase == .loaded, !model.devices.isEmpty {
            VStack(spacing: 0) {
                ForEach(Array(model.devices.enumerated()), id: \.element.id) { i, d in
                    let online = DeviceInventory.isOnline(d, peers: model.peers)
                    HStack(spacing: 14) {
                        Image(systemName: d.symbolName).font(.system(size: 17)).foregroundStyle(Theme.dim).frame(width: 28)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(d.name.isEmpty ? "Unnamed device" : d.name).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text)
                            Text(d.appVersion.isEmpty ? d.platform : "\(d.platform)  ·  ReleaseSync \(d.appVersion)").font(.faint).foregroundStyle(Theme.faint)
                            if let summary = DeviceInventory.summary(of: model.peers[d.id]) { Text(summary).font(.faint).foregroundStyle(Theme.faint) }
                        }
                        Spacer()
                        HStack(spacing: 7) {
                            StatusLight(on: online)
                            Text(online ? "Online" : "Seen \(d.lastSeen.friendlyAgo)").font(.faint).foregroundStyle(online ? Theme.success : Theme.faint)
                        }
                    }
                    .padding(.vertical, 10)
                    if i < model.devices.count - 1 { Divider().overlay(Theme.line.opacity(0.6)) }
                }
            }
            .padding(.horizontal, 14)
            .background(Theme.input, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
            Text("The list comes from your library. A green light will show a device that is on this network right now (coming soon)." + (prefs.allowLibraryChanges ? "" : " This Mac isn't in the list yet: it only reads your library and never changes it.")).font(.faint).foregroundStyle(Theme.faint)
        }
    }
}

private struct SettingsCard<Content: View>: View {
    @ViewBuilder let content: () -> Content
    var body: some View {
        VStack(alignment: .leading, spacing: 12, content: content)
            .frame(maxWidth: .infinity, alignment: .leading)
            .card(padding: EdgeInsets(top: 20, leading: 22, bottom: 20, trailing: 22))
    }
}

/// A secret kept in the Keychain. What is typed here is cleared as soon as it is saved, and a saved secret is never shown again.
private struct SecretField: View {
    @Environment(LibraryModel.self) private var model
    let title: String, target: String, prompt: String, saved: String, empty: String, help: String
    var link: (title: String, url: URL)?
    var heading = true
    @State private var text = ""
    @State private var isSaved = false
    @State private var problem: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if heading { Text(title).font(.h2).foregroundStyle(Theme.text) } else { Text(title).font(.h3).foregroundStyle(Theme.text).padding(.top, 6) }
            Text(help).font(.dim).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 10) {
                SecureField(title, text: $text, prompt: Text(prompt)).textFieldStyle(.plain).font(.system(size: 13)).labelsHidden()
                    .padding(.horizontal, 12).frame(height: 38)
                    .background(Theme.input, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
                    .onSubmit(save)
                Button("Save", action: save).buttonStyle(PrimaryButtonStyle(height: 38)).disabled(text.isEmpty)
            }
            HStack {
                Label(isSaved ? saved : empty, systemImage: isSaved ? "checkmark.circle.fill" : "circle.dashed")
                    .font(.system(size: 12.5)).foregroundStyle(isSaved ? Theme.success : Theme.faint)
                Spacer()
                if let link { Link(link.title, destination: link.url).font(.system(size: 12.5, weight: .semibold)).foregroundStyle(Theme.accentText) }
                if isSaved { Button("Remove", role: .destructive, action: remove).buttonStyle(.plain).font(.system(size: 12.5, weight: .semibold)).foregroundStyle(Theme.danger) }
            }
            if let problem { Text(problem).font(.system(size: 12.5)).foregroundStyle(Theme.danger) }
        }
        .task { isSaved = await model.hasSecret(target) }
    }

    private func save() {
        guard !text.isEmpty else { return }
        let value = text
        text = ""
        Task {
            do {
                try await model.save(value, as: target)
                problem = nil; isSaved = true
                await model.refresh()
            } catch { problem = "Couldn't save it. " + error.localizedDescription }
        }
    }

    private func remove() {
        Task {
            do {
                try await model.remove(target)
                isSaved = false; problem = nil
                await model.refresh()
            } catch { problem = "Couldn't remove it. " + error.localizedDescription }
        }
    }
}
