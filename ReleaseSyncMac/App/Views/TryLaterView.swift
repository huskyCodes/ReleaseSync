import ReleaseSyncKit
import SwiftUI

struct TryLaterView: View {
    @Environment(LibraryModel.self) private var model
    @State private var query = ""

    private var items: [TryLaterItem] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return model.tryLater.filter { q.isEmpty || LibraryLayout.matches($0, q) }.sorted { $0.savedAt > $1.savedAt }
    }

    var body: some View {
        let items = items
        Group {
            if model.tryLater.isEmpty {
                ContentUnavailableView("Nothing to try yet", systemImage: "bookmark",
                                       description: Text("Projects you save for later on your other devices show up here."))
            } else if items.isEmpty {
                ContentUnavailableView.search(text: query)
            } else {
                List(items, id: \.key) { TryLaterRow(item: $0) }
                    .safeAreaInset(edge: .bottom) {
                        Text("\(items.count) \(items.count == 1 ? "project" : "projects") saved for later").font(.callout).foregroundStyle(.secondary).glassPill().padding(.bottom, 10)
                    }
            }
        }
        .searchable(text: $query, placement: .toolbar, prompt: "Search your list")
    }
}

private struct TryLaterRow: View {
    let item: TryLaterItem

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            InitialTile(name: item.repo)
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline) {
                    Text(item.repo).fontWeight(.medium)
                    Text("\(item.owner) · \(RepoLinks.sourceName(item.source))").font(.callout).foregroundStyle(.secondary)
                }
                if !isBlankText(item.description) { Text(item.description!).font(.callout).foregroundStyle(.secondary).lineLimit(2) }
                if !isBlankText(item.note) {
                    Label(item.note!, systemImage: "note.text").font(.callout).lineLimit(2)
                }
                HStack(spacing: 6) {
                    if item.stars > 0 { Chip(text: item.stars.compact, symbol: "star") }
                    if let language = item.language, !language.isEmpty { Chip(text: language) }
                    ForEach((item.topics ?? []).prefix(3), id: \.self) { Chip(text: $0) }
                    Spacer(minLength: 0)
                    Text("Saved \(item.savedAt.friendlyAgo)").font(.caption).foregroundStyle(.tertiary)
                }
            }
        }
        .padding(.vertical, 4)
        .contextMenu {
            if let url = item.url { Link("Open on \(RepoLinks.sourceName(item.source))", destination: url) }
        }
    }
}
