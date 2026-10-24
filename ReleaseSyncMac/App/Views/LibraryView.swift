import ReleaseSyncKit
import SwiftUI

struct LibraryView: View {
    @Environment(LibraryModel.self) private var model
    @State private var query = ""
    @State private var showHidden = false
    @State private var selection: String?
    @State private var showDetails = false

    private var sections: [LibrarySection] {
        LibraryLayout.sections(apps: model.apps, customGroups: model.customGroups, query: query, showHidden: showHidden)
    }

    private var selected: SyncApp? { model.apps.first { $0.key == selection } }

    var body: some View {
        let sections = sections
        Group {
            if model.apps.isEmpty {
                ContentUnavailableView("Your library is empty", systemImage: "square.grid.2x2",
                                       description: Text("Apps you add in ReleaseSync on your other devices show up here."))
            } else if sections.isEmpty {
                ContentUnavailableView.search(text: query)
            } else {
                List(selection: $selection) {
                    ForEach(sections) { section in
                        Section(section.title) {
                            ForEach(section.apps, id: \.key) { app in
                                AppRow(app: app, showGroup: section.id == LibraryLayout.favoritesID).tag(app.key)
                            }
                        }
                    }
                }
                .safeAreaInset(edge: .bottom) {
                    Text(summary(sections)).font(.callout).foregroundStyle(.secondary).glassPill().padding(.bottom, 10)
                }
            }
        }
        .searchable(text: $query, placement: .toolbar, prompt: "Search your apps")
        .toolbar {
            ToolbarItem {
                Toggle(isOn: $showHidden) { Label("Show Hidden Apps", systemImage: showHidden ? "eye" : "eye.slash") }
                    .help(showHidden ? "Hide the apps you've hidden" : "Show the apps you've hidden")
            }
            ToolbarItem {
                Toggle(isOn: $showDetails) { Label("Details", systemImage: "sidebar.trailing") }
                    .help("Show or hide details")
            }
        }
        .inspector(isPresented: $showDetails) {
            Group {
                if let app = selected { AppDetail(app: app) }
                else { ContentUnavailableView("No app selected", systemImage: "square.grid.2x2", description: Text("Pick an app to see its details.")) }
            }
            .inspectorColumnWidth(min: 280, ideal: 330, max: 440)
        }
        .onChange(of: selection) { _, new in if new != nil { showDetails = true } }
    }

    private func summary(_ sections: [LibrarySection]) -> String {
        let n = sections.reduce(0) { $0 + $1.apps.count }
        return query.isEmpty ? "\(n) \(n == 1 ? "app" : "apps")" : "\(n) found"
    }
}

private struct AppRow: View {
    let app: SyncApp
    var showGroup = false

    var body: some View {
        HStack(spacing: 10) {
            InitialTile(name: app.title)
            VStack(alignment: .leading, spacing: 1) {
                Text(app.title).lineLimit(1)
                Text(subtitle).font(.callout).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 8)
            if app.hidden { Image(systemName: "eye.slash").foregroundStyle(.tertiary).help("Hidden") }
            if !isBlankText(app.note) { Image(systemName: "note.text").foregroundStyle(.tertiary).help("Has a note") }
            if app.pinned { Image(systemName: "star.fill").foregroundStyle(.yellow).help("Favorite") }
        }
        .padding(.vertical, 2)
        .contextMenu {
            if let url = app.url { Link("Open on \(RepoLinks.sourceName(app.source))", destination: url) }
        }
    }

    private var subtitle: String {
        var s = "\(app.fullName) · \(RepoLinks.sourceName(app.source))"
        if showGroup, !isBlankText(app.group) { s += " · \(app.group!)" }
        return s
    }
}

private struct AppDetail: View {
    let app: SyncApp

    var body: some View {
        Form {
            Section {
                HStack(spacing: 12) {
                    InitialTile(name: app.title, size: 48)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(app.title).font(.title3.weight(.semibold))
                        Text(app.fullName).foregroundStyle(.secondary)
                    }
                }
                if let url = app.url { Link("Open on \(RepoLinks.sourceName(app.source))", destination: url) }
            }
            Section("Library") {
                LabeledContent("Group", value: isBlankText(app.group) ? "None" : app.group!)
                LabeledContent("Favorite", value: app.pinned ? "Yes" : "No")
                LabeledContent("Hidden", value: app.hidden ? "Yes" : "No")
                LabeledContent("Added", value: app.addedAt.friendlyDate)
                LabeledContent("Last changed", value: app.modifiedAt.friendlyDate)
            }
            if !app.tagList.isEmpty {
                Section("Tags") { ChipFlow { ForEach(app.tagList, id: \.self) { Chip(text: $0) } } }
            }
            if !isBlankText(app.note) {
                Section("Note") { Text(app.note!).textSelection(.enabled) }
            }
            Section("Updates") {
                LabeledContent("Install", value: app.mode.friendlyName)
                LabeledContent("Pre-releases", value: app.includePrereleases ? "Included" : "Not included")
                LabeledContent("Update automatically", value: app.autoUpdate ? "Yes" : "No")
                if app.ignoreUpdates { LabeledContent("Updates", value: "Ignored") }
                if let v = app.skippedVersion, !v.isEmpty { LabeledContent("Skipped version", value: v) }
                if let p = app.assetPattern, !p.isEmpty { LabeledContent("Preferred file", value: p) }
            }
        }
        .formStyle(.grouped)
    }
}
