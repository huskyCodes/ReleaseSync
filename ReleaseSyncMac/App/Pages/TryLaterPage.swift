import ReleaseSyncKit
import SwiftUI

struct TryLaterPage: View {
    @Environment(LibraryModel.self) private var model
    @Environment(ViewPrefs.self) private var prefs
    @State private var query = ""
    @State private var type = ""
    @State private var folded = Set<String>()

    private var result: LibraryLayout.TryResult {
        LibraryLayout.tryLater(model.tryLater, type: type, query: query, sort: prefs.trySort, group: prefs.tryGroup)
    }

    var body: some View {
        let r = result
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 0) {
                PageHeader(title: "Try later", subtitle: "Projects you want to look at — saved without adding or installing anything.")
                if !model.tryLater.isEmpty { toolbar(r).padding(.top, 16) }
            }
            .padding(.horizontal, Theme.pagePadding).padding(.top, 14).padding(.bottom, 12)

            if model.tryLater.isEmpty {
                EmptyMessage(symbol: "bookmark", title: "Nothing saved yet", message: "Projects you save to try later on your other devices show up here.").padding(.bottom, 60)
            } else if r.sections.isEmpty {
                EmptyMessage(symbol: "magnifyingglass", title: "Nothing matches", message: "Try a different search or type.") {
                    Button("Clear filters") { query = ""; type = "" }.buttonStyle(OutlineButtonStyle())
                }.padding(.bottom, 60)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) { ForEach(r.sections, id: \.title) { sectionView($0.title, $0.items) } }
                        .padding(.horizontal, Theme.pagePadding).padding(.bottom, 32)
                }
            }
        }
        .onChange(of: model.tryLater.count) { _, _ in if !type.isEmpty && !model.tryLater.contains(where: { LibraryLayout.category($0) == type }) { type = "" } }
    }

    private func toolbar(_ r: LibraryLayout.TryResult) -> some View {
        @Bindable var prefs = prefs
        return VStack(alignment: .leading, spacing: 12) {
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 10) { controls(r); Spacer(minLength: 8); shown(r) }
                VStack(alignment: .leading, spacing: 10) { HStack(spacing: 10) { controls(r) }; shown(r) }
            }
            ChipStrip {
                ForEach(r.types) { t in Chip(title: t.name.isEmpty ? "All" : t.name, count: t.count, selected: type == t.name) { type = t.name } }
            }
        }
    }

    private func controls(_ r: LibraryLayout.TryResult) -> some View {
        @Bindable var prefs = prefs
        return Group {
            SearchBox(prompt: "Search your saved projects…", text: $query, width: 300)
            SortMenu(choices: ViewPrefs.trySorts.map { ($0.name, $0.key) }, selection: $prefs.trySort, help: "Sort by", width: 160)
            IconButton(symbol: "rectangle.3.group", help: "Group into sections by type", size: 36, selected: prefs.tryGroup) { prefs.tryGroup.toggle() }
            if prefs.tryGroup {
                HStack(spacing: 0) {
                    IconButton(symbol: "chevron.up.2", help: "Fold all sections") { folded = Set(r.sections.map(\.title)) }
                    IconButton(symbol: "chevron.down.2", help: "Open all sections") { folded = [] }
                }
                .padding(2).background(Theme.soft, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            }
            IconSwitch(options: [.init(value: .grid, symbol: "square.grid.2x2", help: "Grid view"), .init(value: .list, symbol: "list.bullet", help: "List view")], selection: $prefs.tryViewMode)
        }
    }

    private func shown(_ r: LibraryLayout.TryResult) -> some View {
        Text(r.shown == r.total ? "\(r.total) saved" : "Showing \(r.shown) of \(r.total)").font(.faint).foregroundStyle(Theme.faint)
    }

    @ViewBuilder private func sectionView(_ title: String, _ items: [TryLaterItem]) -> some View {
        let open = Binding(get: { !folded.contains(title) }, set: { if $0 { folded.remove(title) } else { folded.insert(title) } })
        VStack(alignment: .leading, spacing: 10) {
            if prefs.tryGroup && !title.isEmpty { SectionHeader(title: title, count: items.count, open: open) }
            if open.wrappedValue || !prefs.tryGroup {
                if prefs.tryViewMode == .grid {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: Theme.wideMin, maximum: .infinity), spacing: Theme.panelGap, alignment: .top)], spacing: Theme.panelGap) {
                        ForEach(items, id: \.key) { TryCard(item: $0) }
                    }
                } else {
                    LazyVStack(spacing: Theme.panelGap) { ForEach(items, id: \.key) { TryRow(item: $0) } }
                }
            }
        }
        .padding(.bottom, 24)
    }
}

private struct OpenLinkButton: View {
    let item: TryLaterItem
    var body: some View {
        if let url = item.url {
            Link(destination: url) {
                Image(systemName: "arrow.up.right.square").font(.system(size: 14)).foregroundStyle(Theme.dim).frame(width: 30, height: 30)
            }
            .help("Open project page")
        }
    }
}

private struct TryActions: View {
    @Environment(AddModel.self) private var add
    let item: TryLaterItem
    var body: some View {
        HStack(spacing: 8) {
            Button { Task { await add.removeFromTryLater(item) } } label: { Image(systemName: "trash") }.buttonStyle(OutlineButtonStyle(height: 28)).help("Remove from Try later")
            Button("Add") { Task { await add.open(item) } }.buttonStyle(PrimaryButtonStyle(height: 28)).help("Look at what it offers and add it to your library")
        }
    }
}

private struct StatsLine: View {
    let item: TryLaterItem
    var body: some View {
        HStack(spacing: 12) {
            if item.stars > 0 {
                HStack(spacing: 5) {
                    Image(systemName: "star.fill").font(.system(size: 11)).foregroundStyle(Theme.warn)
                    Text(item.stars.compact).font(.system(size: 12, weight: .semibold)).foregroundStyle(Theme.text)
                }
            }
            if let language = item.language, !language.isEmpty { Pill(text: language) }
            Pill(text: LibraryLayout.category(item), kind: .accent).help("Type of project")
            Text("Saved \(item.savedAt.friendlyAgo)").font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
        }
    }
}

struct TryCard: View {
    @Environment(AddModel.self) private var add
    let item: TryLaterItem

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 14) {
                Avatar(name: item.repo)
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.repo).font(.cardTitle).foregroundStyle(Theme.text).lineLimit(1)
                    Text("\(item.fullName) · \(RepoLinks.sourceName(item.source))").font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
                }
                Spacer(minLength: 0)
                OpenLinkButton(item: item)
            }
            Text(isBlankText(item.description) ? " " : item.description!).font(.dim).foregroundStyle(Theme.dim).lineLimit(2)
                .frame(maxWidth: .infinity, minHeight: 36, alignment: .topLeading).padding(.vertical, 12)
            StatsLine(item: item)
            HStack { Spacer(); TryActions(item: item) }.padding(.top, 10)
            if !isBlankText(item.note) {
                Label(item.note!, systemImage: "note.text").font(.system(size: 12.5)).foregroundStyle(Theme.text).lineLimit(3)
                    .padding(.horizontal, 12).padding(.vertical, 9).frame(maxWidth: .infinity, alignment: .leading)
                    .background(Theme.input, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
                    .padding(.top, 14)
            }
        }
        .card()
        .contextMenu {
            Button("Add to library…") { Task { await add.open(item) } }
            Button("Remove from Try later") { Task { await add.removeFromTryLater(item) } }
            if let url = item.url { Link("Open on \(RepoLinks.sourceName(item.source))", destination: url) }
        }
    }
}

struct TryRow: View {
    @Environment(AddModel.self) private var add
    let item: TryLaterItem

    var body: some View {
        HStack(spacing: 14) {
            Avatar(name: item.repo, size: Theme.rowAvatar)
            VStack(alignment: .leading, spacing: 2) {
                Text(item.fullName).font(.h3).foregroundStyle(Theme.text).lineLimit(1)
                Text(isBlankText(item.description) ? RepoLinks.sourceName(item.source) : item.description!).font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
            }
            Spacer(minLength: 10)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    if item.stars > 0 {
                        Image(systemName: "star.fill").font(.system(size: 11)).foregroundStyle(Theme.warn)
                        Text(item.stars.compact).font(.system(size: 12, weight: .semibold)).foregroundStyle(Theme.text)
                    }
                    Pill(text: LibraryLayout.category(item), kind: .accent)
                }
                Text("Saved \(item.savedAt.friendlyAgo)").font(.faint).foregroundStyle(Theme.faint)
            }
            .frame(minWidth: 170, alignment: .leading)
            if !isBlankText(item.note) {
                Text(item.note!).font(.system(size: 12.5)).foregroundStyle(Theme.text).lineLimit(2).frame(width: 200, alignment: .leading)
                    .padding(.horizontal, 10).padding(.vertical, 7)
                    .background(Theme.input, in: RoundedRectangle(cornerRadius: 9, style: .continuous))
            }
            TryActions(item: item)
            OpenLinkButton(item: item)
        }
        .card(radius: Theme.rowRadius, padding: EdgeInsets(top: 12, leading: 16, bottom: 12, trailing: 16))
        .contextMenu {
            Button("Add to library…") { Task { await add.open(item) } }
            Button("Remove from Try later") { Task { await add.removeFromTryLater(item) } }
            if let url = item.url { Link("Open on \(RepoLinks.sourceName(item.source))", destination: url) }
        }
    }
}
