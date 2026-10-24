import ReleaseSyncKit
import SwiftUI

enum Destination: Hashable, CaseIterable {
    case library, tryLater, devices

    var title: String {
        switch self { case .library: "Library"; case .tryLater: "Try later"; case .devices: "Devices" }
    }
    var symbol: String {
        switch self { case .library: "square.grid.2x2"; case .tryLater: "bookmark"; case .devices: "laptopcomputer.and.iphone" }
    }
}

struct RootView: View {
    @Environment(LibraryModel.self) private var model
    @State private var destination: Destination? = .library

    var body: some View {
        NavigationSplitView {
            List(Destination.allCases, id: \.self, selection: $destination) { item in
                Label(item.title, systemImage: item.symbol)
                    .badge(count(item))
            }
            .navigationSplitViewColumnWidth(min: 170, ideal: 200, max: 260)
        } detail: {
            Group {
                if model.phase == .loaded {
                    switch destination ?? .library {
                    case .library: LibraryView()
                    case .tryLater: TryLaterView()
                    case .devices: DevicesView()
                    }
                } else {
                    StatusView()
                }
            }
            .navigationTitle((destination ?? .library).title)
        }
        .toolbar {
            ToolbarItem(placement: .status) { UpdatedText() }
            ToolbarItem(placement: .primaryAction) {
                Button { Task { await model.refresh() } } label: { Label("Refresh", systemImage: "arrow.clockwise") }
                    .keyboardShortcut("r", modifiers: .command)
                    .help("Check the library for changes")
                    .disabled(model.isBusy)
            }
        }
        .task { await model.refresh() }
        .frame(minWidth: 760, minHeight: 460)
    }

    private func count(_ item: Destination) -> Int {
        switch item {
        case .library: model.apps.count
        case .tryLater: model.tryLater.count
        case .devices: model.devices.count
        }
    }
}

private struct UpdatedText: View {
    @Environment(LibraryModel.self) private var model

    var body: some View {
        if model.isBusy {
            HStack(spacing: 6) { ProgressView().controlSize(.small); Text("Checking…") }.foregroundStyle(.secondary)
        } else if let date = model.lastFetched, model.phase == .loaded {
            Text("Updated \(Text(date, style: .relative)) ago").foregroundStyle(.secondary)
        }
    }
}

/// What the main area says when there is no library to show yet.
struct StatusView: View {
    @Environment(LibraryModel.self) private var model

    var body: some View {
        switch model.phase {
        case .loading, .loaded:
            ProgressView("Checking your library…")
        case .needsToken:
            ContentUnavailableView {
                Label("Connect your library", systemImage: "key")
            } description: {
                Text("Add the GitHub token you use to sync ReleaseSync on your other devices. It stays in your Mac's Keychain.")
            } actions: {
                SettingsLink { Text("Add Token…") }.buttonStyle(.borderedProminent)
            }
        case .needsPassphrase(let wrong):
            ContentUnavailableView {
                Label(wrong ? "That passphrase didn't work" : "Your library is locked", systemImage: "lock")
            } description: {
                Text(wrong ? "Check it and try again. It has to be the passphrase you set up on your other device."
                           : "Enter the passphrase you set up on your other device.")
            } actions: {
                SettingsLink { Text("Enter Passphrase…") }.buttonStyle(.borderedProminent)
            }
        case .noLibrary:
            ContentUnavailableView {
                Label("No library found", systemImage: "tray")
            } description: {
                Text("Your GitHub account doesn't have a ReleaseSync library yet. Turn on sync in ReleaseSync on your other device first, then check again.")
            } actions: {
                Button("Check Again") { Task { await model.refresh() } }
            }
        case .failed(let message):
            ContentUnavailableView {
                Label("Couldn't open your library", systemImage: "exclamationmark.triangle")
            } description: {
                Text(message)
            } actions: {
                Button("Try Again") { Task { await model.refresh() } }
                SettingsLink { Text("Settings…") }
            }
        }
    }
}
