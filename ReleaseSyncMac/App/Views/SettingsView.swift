import SwiftUI

struct SettingsView: View {
    @Environment(LibraryModel.self) private var model

    var body: some View {
        Form {
            SecretField(title: "GitHub token", target: SecretTarget.gitHubToken, prompt: "Paste your token",
                        saved: "A token is saved in your Keychain.", empty: "No token saved yet.",
                        help: "Use the token you sync with on your other devices. It needs the “gist” permission. It is sent only to api.github.com.")
            SecretField(title: "Sync passphrase", target: SecretTarget.syncPassphrase, prompt: "Type your passphrase",
                        saved: "A passphrase is saved in your Keychain.", empty: "No passphrase saved.",
                        help: "Only needed if you locked your library with a passphrase on another device. Type it exactly as you set it.")
            Section {
                Text("ReleaseSync isn't affiliated with GitHub, GitLab, Codeberg or Apple. It doesn't collect any data about you, and it talks only to the sites your apps come from and to GitHub for your library.")
                    .font(.callout).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 480)
        .fixedSize(horizontal: false, vertical: true)
    }
}

/// A secret kept in the Keychain. What is typed here is cleared as soon as it is saved, and a saved secret is never shown again.
private struct SecretField: View {
    @Environment(LibraryModel.self) private var model
    let title: String, target: String, prompt: String, saved: String, empty: String, help: String
    @State private var text = ""
    @State private var isSaved = false
    @State private var problem: String?

    var body: some View {
        Section(title) {
            SecureField(title, text: $text, prompt: Text(prompt)).labelsHidden()
                .onSubmit(save)
            HStack {
                Label(isSaved ? saved : empty, systemImage: isSaved ? "checkmark.circle.fill" : "circle.dashed")
                    .foregroundStyle(isSaved ? .green : .secondary).font(.callout)
                Spacer()
                if isSaved { Button("Remove", role: .destructive, action: remove) }
                Button("Save", action: save).disabled(text.isEmpty).keyboardShortcut(.defaultAction)
            }
            if let problem { Text(problem).font(.callout).foregroundStyle(.red) }
            Text(help).font(.callout).foregroundStyle(.secondary)
        }
        .onAppear { isSaved = model.hasSecret(target) }
    }

    private func save() {
        guard !text.isEmpty else { return }
        do {
            try model.save(text, as: target)
            text = ""; problem = nil; isSaved = true
            Task { await model.refresh() }
        } catch { problem = "Couldn't save it. " + error.localizedDescription }
    }

    private func remove() {
        do {
            try model.remove(target)
            isSaved = false; problem = nil
            Task { await model.refresh() }
        } catch { problem = "Couldn't remove it. " + error.localizedDescription }
    }
}
