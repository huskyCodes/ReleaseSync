import ReleaseSyncKit
import SwiftUI

struct LibraryPage: View {
    @Environment(LibraryModel.self) private var model
    @Environment(ViewPrefs.self) private var prefs
    @Environment(Navigator.self) private var nav
    @State private var status = LibraryLayout.Status.all
    @State private var groupFilter: String?
    /// The id of the device whose apps are shown (nil: the whole library).
    @State private var deviceFilter: String?
    @State private var query = ""
    @State private var folded = Set<String>()

    /// Devices that report what they have installed. A device that has since left the file can no longer be chosen.
    private var reporting: [SyncDevice] { DeviceInventory.reporting(model.devices, peers: model.peers) }
    private var chosenDevice: String? { reporting.contains { $0.id == deviceFilter } ? deviceFilter : nil }

    private var result: LibraryLayout.Result {
        let devices = reporting
        return LibraryLayout.layout(apps: model.apps, customGroups: model.customGroups, releases: model.releases, status: status, groupFilter: groupFilter, query: query,
                                    sort: prefs.sort, groupItems: prefs.groupItems, autoGroups: prefs.autoGroups,
                                    installedOnDevice: chosenDevice == nil ? nil : DeviceInventory.keys(on: devices, peers: model.peers, only: chosenDevice),
                                    updatesWaiting: DeviceInventory.updatesWaiting(on: devices, peers: model.peers, only: chosenDevice))
    }

    var body: some View {
        let r = result
        VStack(alignment: .leading, spacing: 0) {
            PageHeader(title: "Library", subtitle: subtitle(r)).padding(.horizontal, Theme.pagePadding).padding(.top, 14)
            if model.apps.isEmpty {
                EmptyMessage(symbol: "books.vertical", title: "Your library is empty",
                             message: "Apps you add in ReleaseSync on your other devices show up here, kept in sync through your private GitHub Gist.", brand: true)
            } else {
                toolbar(r).padding(.horizontal, Theme.pagePadding).padding(.vertical, 16)
                if r.sections.isEmpty && r.emptyGroups.isEmpty {
                    EmptyMessage(symbol: "magnifyingglass", title: "Nothing matches", message: "Try a different filter or clear the search box.")
                        .padding(.bottom, 80)
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(r.sections) { section in sectionView(section) }
                            ForEach(r.emptyGroups, id: \.self) { name in emptyGroup(name) }
                        }
                        .padding(.horizontal, Theme.pagePadding).padding(.bottom, 32)
                    }
                }
            }
        }
        .onChange(of: status) { _, _ in groupFilter = nil }
    }

    private func subtitle(_ r: LibraryLayout.Result) -> String {
        if model.apps.isEmpty { return "Nothing tracked yet" }
        if status == .hidden { return "\(r.hidden) hidden \(r.hidden == 1 ? "app" : "apps")  ·  not shown in your library" }
        var s = "\(r.total) \(r.total == 1 ? "app" : "apps")"
        if r.hidden > 0 { s += "  ·  \(r.hidden) hidden" }
        if model.isChecking, let p = model.checkProgress { s += "  ·  checking \(p.done) of \(p.total)…" }
        else if let last = model.lastCheck { s += "  ·  checked " + RelativeDateTimeFormatter.short.localizedString(for: last, relativeTo: Date()) }
        else if let last = model.lastFetched { s += "  ·  synced " + RelativeDateTimeFormatter.short.localizedString(for: last, relativeTo: Date()) }
        return s
    }

    // MARK: toolbar

    @ViewBuilder private func toolbar(_ r: LibraryLayout.Result) -> some View {
        @Bindable var prefs = prefs
        VStack(alignment: .leading, spacing: 12) {
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 10) { chips(r); Spacer(minLength: 12); controls(r) }
                VStack(alignment: .leading, spacing: 10) { chips(r); controls(r) }
            }
            if !reporting.isEmpty {
                HStack(spacing: 8) {
                    Text("On").font(.label).foregroundStyle(Theme.faint)
                    ChipStrip {
                        Chip(title: "Any device", selected: chosenDevice == nil) { deviceFilter = nil }
                        ForEach(reporting, id: \.id) { d in
                            Chip(title: d.name.isEmpty ? "Unnamed device" : d.name, light: DeviceInventory.isOnline(d, peers: model.peers), selected: chosenDevice == d.id) { deviceFilter = (chosenDevice == d.id ? nil : d.id) }
                        }
                    }
                }
            }
            HStack(spacing: 8) {
                ChipStrip {
                    Chip(title: "All groups", selected: groupFilter == nil) { groupFilter = nil }
                    ForEach(r.chips) { g in Chip(title: g.name, count: g.count, selected: groupFilter == g.name) { groupFilter = (groupFilter == g.name ? nil : g.name) } }
                }
                Button { prefs.autoGroups.toggle() } label: { Text("Auto").font(.system(size: 12, weight: .semibold)) }
                    .buttonStyle(AutoToggleStyle(on: prefs.autoGroups))
                    .help(prefs.autoGroups ? "Apps are sorted into groups by type automatically. Click to turn that off." : "Automatic groups are off. Click to sort apps into groups by type.")
            }
        }
    }

    private func chips(_ r: LibraryLayout.Result) -> some View {
        HStack(spacing: 8) {
            Chip(title: "All", count: r.total, selected: status == .all) { status = .all }
            Chip(title: "★ Favorites", count: r.favorites, selected: status == .favorites) { status = .favorites }
            if r.updates > 0 { Chip(title: "Updates", count: r.updates, selected: status == .updates) { status = .updates } }
            if r.noMacBuild > 0 { Chip(title: "No Mac build", count: r.noMacBuild, selected: status == .noMacBuild) { status = .noMacBuild } }
            if r.hidden > 0 { Chip(title: "Hidden", count: r.hidden, selected: status == .hidden) { status = .hidden } }
        }
    }

    private func controls(_ r: LibraryLayout.Result) -> some View {
        @Bindable var prefs = prefs
        return HStack(spacing: 10) {
            SearchBox(prompt: "Search your library…", text: $query)
            SortMenu(choices: ViewPrefs.librarySorts.map { ($0, $0) }, selection: $prefs.sort, help: "Sort by", width: 150)
            IconButton(symbol: "rectangle.3.group", help: "Group apps into sections", size: 36, selected: prefs.groupItems) { prefs.groupItems.toggle() }
            if prefs.groupItems {
                HStack(spacing: 0) {
                    IconButton(symbol: "chevron.up.2", help: "Fold all sections") { folded = Set(r.sections.map(\.title)) }
                    IconButton(symbol: "chevron.down.2", help: "Open all sections") { folded = [] }
                }
                .padding(2).background(Theme.soft, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            }
            IconSwitch(options: [.init(value: .grid, symbol: "square.grid.2x2", help: "Grid view"), .init(value: .list, symbol: "list.bullet", help: "List view")], selection: $prefs.viewMode)
        }
    }

    // MARK: sections and cards

    @ViewBuilder private func sectionView(_ section: LibraryLayout.Section) -> some View {
        let open = Binding(get: { !folded.contains(section.title) }, set: { if $0 { folded.remove(section.title) } else { folded.insert(section.title) } })
        VStack(alignment: .leading, spacing: 10) {
            if prefs.groupItems && !section.title.isEmpty { SectionHeader(title: section.title, count: section.apps.count, open: open) }
            if open.wrappedValue || !prefs.groupItems {
                if prefs.viewMode == .grid {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: Theme.gridMin, maximum: .infinity), spacing: Theme.panelGap, alignment: .top)], spacing: Theme.panelGap) {
                        ForEach(section.apps, id: \.key) { AppCard(app: $0) }
                    }
                } else {
                    LazyVStack(spacing: Theme.panelGap) { ForEach(section.apps, id: \.key) { AppRow(app: $0) } }
                }
            }
        }
        .padding(.bottom, 24)
    }

    private func emptyGroup(_ name: String) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                Image(systemName: "chevron.down").font(.system(size: 10, weight: .semibold)).foregroundStyle(Theme.faint)
                Text(name).font(.system(size: 14.5, weight: .semibold)).foregroundStyle(Theme.text)
                Text("0").font(.system(size: 11, weight: .semibold)).foregroundStyle(Theme.dim).padding(.horizontal, 8).background(Theme.soft, in: Capsule())
            }
            Text("Nothing here yet.").font(.dim).foregroundStyle(Theme.dim).frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 18).padding(.vertical, 16)
                .background(Theme.soft, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
        }
        .padding(.bottom, 24)
    }
}

extension RelativeDateTimeFormatter {
    static let short: RelativeDateTimeFormatter = { let f = RelativeDateTimeFormatter(); f.unitsStyle = .full; f.dateTimeStyle = .named; return f }()
}

private struct AutoToggleStyle: ButtonStyle {
    let on: Bool
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(on ? Theme.accentText : Theme.dim)
            .padding(.horizontal, 12).frame(height: 30)
            .background(on ? Theme.accentSoft : (configuration.isPressed ? Theme.softHover : .clear), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
    }
}

/// A pop-up list that looks like the Windows combo box.
struct SortMenu: View {
    let choices: [(name: String, key: String)]
    @Binding var selection: String
    let help: String
    var width: CGFloat = 150

    var body: some View {
        Menu {
            Picker(help, selection: $selection) { ForEach(choices, id: \.key) { Text($0.name).tag($0.key) } }.pickerStyle(.inline).labelsHidden()
        } label: {
            HStack {
                Text(choices.first { $0.key == selection }?.name ?? selection).font(.system(size: 13)).foregroundStyle(Theme.text).lineLimit(1)
                Spacer(minLength: 4)
                Image(systemName: "chevron.up.chevron.down").font(.system(size: 9, weight: .semibold)).foregroundStyle(Theme.faint)
            }
            .padding(.horizontal, 12).frame(width: width, height: 36)
            .background(Theme.input, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden)
        .help(help)
    }
}
