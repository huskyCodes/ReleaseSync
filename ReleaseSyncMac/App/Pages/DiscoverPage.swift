import ReleaseSyncKit
import SwiftUI

struct DiscoverPage: View {
    @Environment(DiscoverModel.self) private var discover
    @Environment(LibraryModel.self) private var model
    @Environment(ViewPrefs.self) private var prefs
    @Environment(Navigator.self) private var nav
    @State private var showHelp = false

    private var mode: DiscoverMode { prefs.discoverMode }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                header
                if !prefs.discoverIntroSeen || showHelp { intro.padding(.bottom, 16) }
                else if !hasToken { tokenNudge.padding(.bottom, 16) }
                if let warning = rateWarning { warningBar(warning).padding(.bottom, 16) }

                HStack(spacing: 14) {
                    SegTabs(options: DiscoverMode.allCases.map { ($0.rawValue, $0) }, selection: Binding(get: { mode }, set: { discover.setMode($0) }))
                    Spacer(minLength: 8)
                    if mode != .featured {
                        SegTabs(options: [("Since yesterday", false), ("Since last week", true)], selection: Binding(get: { prefs.discoverCompareWeek }, set: { discover.setCompare(week: $0) }), small: true)
                            .help("Compare with an earlier ranking to show movement")
                    }
                }
                .padding(.bottom, 14)

                ChipStrip {
                    ForEach(DiscoverCatalog.categories) { c in Chip(title: c.title, selected: prefs.discoverCategory == c.key) { discover.setCategory(c.key) } }
                }
                .padding(.bottom, 14)

                options.padding(.bottom, 14)
                results
            }
            .padding(.horizontal, Theme.pagePadding).padding(.top, 14).padding(.bottom, 32)
        }
        .task { await model.prepareClient(); discover.appeared() }
    }

    // MARK: header

    private var hasToken: Bool { model.meter.status.limit > 60 || model.phase == .loaded }

    private var header: some View {
        HStack(alignment: .top, spacing: 14) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Discover").font(.h1).foregroundStyle(Theme.text)
                Text(mode.blurb).font(.dim).foregroundStyle(Theme.dim)
            }
            Spacer(minLength: 12)
            RateBadge(status: model.meter.status)
            IconButton(symbol: "questionmark.circle", help: "How Discover uses your GitHub requests", size: 36) { showHelp.toggle() }
            Button { nav.show(.tryLater) } label: {
                HStack(spacing: 8) {
                    Image(systemName: "bookmark").font(.system(size: 13))
                    Text("Try later")
                    if !model.tryLater.isEmpty {
                        Text("\(model.tryLater.count)").font(.system(size: 11, weight: .bold)).foregroundStyle(.white).padding(.horizontal, 6).frame(minWidth: 20, minHeight: 20).background(Theme.accent, in: Capsule())
                    }
                }
            }
            .buttonStyle(OutlineButtonStyle(height: 36)).help("Projects you saved to look at later")
            IconButton(symbol: "arrow.clockwise", help: "Refresh these lists", size: 36) { discover.load(force: true) }.disabled(discover.isBusy)
        }
        .padding(.bottom, 16)
    }

    private var options: some View {
        HStack(spacing: 14) {
            Toggle(isOn: Binding(get: { prefs.discoverMacOnly }, set: { discover.setMacOnly($0) })) {
                Text("Only projects with Mac downloads").font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text)
            }
            .toggleStyle(.checkbox).disabled(mode == .featured)
            .help(mode == .featured ? "Hand-picked projects are always shown" : "Each project is checked once for a .dmg, .pkg or .zip made for macOS (about one request each, remembered for three days)")
            Spacer(minLength: 8)
            Text(discover.status).font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
            SegTabs(options: [("Full", false), ("Light", true)], selection: Binding(get: { prefs.discoverLight }, set: { discover.setLight($0) }), small: true)
                .help("Full: 36 per list, other tabs prepared ahead.  Light: 15 per list, fewest requests.")
        }
    }

    // MARK: notices

    private var rateWarning: String? {
        let r = model.meter.status
        guard r.remaining >= 0, r.limit > 0, r.remaining == 0 || Double(r.remaining) / Double(r.limit) < 0.05 else { return nil }
        let when = r.reset.map { $0.formatted(date: .omitted, time: .shortened) } ?? "the top of the hour"
        return r.remaining == 0 ? "You have used all your GitHub requests for this hour. Update checks and Discover resume at \(when)."
                                : "Only \(r.remaining.formatted()) requests left this hour (resets at \(when)). Discover has paused its extras so ReleaseSync can still check your apps for updates."
    }

    private func warningBar(_ text: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Theme.danger).padding(.top, 2)
            Text(text).font(.system(size: 13)).foregroundStyle(Theme.text).fixedSize(horizontal: false, vertical: true)
        }
        .padding(.horizontal, 14).padding(.vertical, 11).frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.dangerSoft, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    private var tokenNudge: some View {
        HStack(spacing: 12) {
            Image(systemName: "key.fill").foregroundStyle(Theme.warn)
            Text("Without a GitHub token, GitHub allows only 60 requests an hour. Discover could use them all.").font(.system(size: 13)).foregroundStyle(Theme.text)
            Spacer(minLength: 8)
            Button("Add a token") { nav.show(.settings) }.buttonStyle(OutlineButtonStyle(height: 30))
        }
        .padding(.horizontal, 14).padding(.vertical, 9).background(Theme.warnSoft, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    private var intro: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Image(systemName: "info.circle").foregroundStyle(Theme.accentText)
                Text("Before you browse: Discover uses your GitHub request allowance").font(.h3).foregroundStyle(Theme.text)
            }
            Text(hasToken ? "With your token you have 5,000 requests an hour." : "Without a token you only have 60 requests an hour, so Discover will work but run out quickly.").font(.dim).foregroundStyle(Theme.dim)
            ForEach(["First time in a category: about five searches, plus roughly one request for each project it checks for a Mac download (40 to 100). After that it is remembered: lists for hours, the Mac check for three days.",
                     "Light mode asks for about a third as much. Nothing is prepared in the background.",
                     "The counter at the top right shows what is left. It turns amber, then red, and below 150 the Mac check pauses so your update checks can still run."], id: \.self) {
                Text("•  " + $0).font(.dim).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Button("Got it") { prefs.discoverIntroSeen = true; showHelp = false }.buttonStyle(PrimaryButtonStyle())
                Button("Use light mode") { discover.setLight(true); prefs.discoverIntroSeen = true; showHelp = false }.buttonStyle(OutlineButtonStyle()).help("About a third of the requests")
            }.padding(.top, 6)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card(padding: EdgeInsets(top: 18, leading: 20, bottom: 18, trailing: 20))
    }

    // MARK: results

    @ViewBuilder private var results: some View {
        if let error = discover.error, discover.items.isEmpty {
            EmptyMessage(symbol: "magnifyingglass", title: "Nothing to show", message: error) {
                Button("Try again") { discover.load(force: true) }.buttonStyle(OutlineButtonStyle())
            }.frame(minHeight: 260)
        } else if discover.items.isEmpty && discover.isBusy {
            VStack(spacing: 14) { ProgressView(); Text(discover.status).font(.dim).foregroundStyle(Theme.dim) }.frame(maxWidth: .infinity, minHeight: 260)
        } else {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: Theme.gridMin, maximum: .infinity), spacing: Theme.panelGap, alignment: .top)], spacing: Theme.panelGap) {
                ForEach(discover.items) { DiscoverCard(item: $0, showRank: mode != .featured) }
            }
            if discover.canLoadMore || (discover.isBusy && !discover.items.isEmpty) {
                HStack { Spacer()
                    if discover.isBusy { ProgressView().controlSize(.small) } else { Button("Load more") { discover.loadMore() }.buttonStyle(OutlineButtonStyle(height: 36)) }
                    Spacer() }.padding(.top, 20)
            }
        }
    }
}

// MARK: pieces

/// A row of choices in a soft tray, one chosen (the Windows `SegTab` look).
struct SegTabs<Value: Hashable>: View {
    let options: [(String, Value)]
    @Binding var selection: Value
    var small = false

    var body: some View {
        HStack(spacing: 2) {
            ForEach(options.indices, id: \.self) { i in
                let on = options[i].1 == selection
                Button { selection = options[i].1 } label: {
                    Text(options[i].0).font(.system(size: small ? 12 : 13, weight: .semibold)).foregroundStyle(on ? Theme.accentText : Theme.dim)
                        .padding(.horizontal, small ? 11 : 16).padding(.vertical, small ? 5 : 7)
                        .background(on ? Theme.accentSoft : .clear, in: RoundedRectangle(cornerRadius: small ? 8 : 10, style: .continuous))
                }
                .buttonStyle(.plain)
            }
        }
        .padding(3).background(Theme.soft, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

/// How many requests GitHub will still answer this hour, big and plain; amber, then red, as it runs down.
struct RateBadge: View {
    let status: RateStatus

    var body: some View {
        if status.remaining >= 0 {
            let fraction = status.limit > 0 ? Double(status.remaining) / Double(status.limit) : 1
            let tone: Color = status.remaining == 0 || fraction < 0.05 ? Theme.danger : fraction < 0.25 ? Theme.warn : Theme.text
            VStack(alignment: .trailing, spacing: 0) {
                Text(status.remaining.formatted()).font(.system(size: 22, weight: .bold)).foregroundStyle(tone).monospacedDigit()
                Text("of \(status.limit.formatted()) requests this hour").font(.system(size: 10.5)).foregroundStyle(Theme.faint)
            }
            .padding(.horizontal, 12).padding(.vertical, 4).background(Theme.soft, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .help("GitHub requests left this hour" + (status.reset.map { " (resets at " + $0.formatted(date: .omitted, time: .shortened) + ")" } ?? ""))
        }
    }
}

struct DiscoverCard: View {
    @Environment(LibraryModel.self) private var model
    @Environment(AddModel.self) private var add
    let item: DiscoverItem
    let showRank: Bool

    private var repo: RepoInfo { item.repo }
    private var url: URL? { RepoLinks.url(source: repo.source, owner: repo.ownerLogin, repo: repo.name) }
    private var key: String { SyncApp.makeKey(source: repo.source, owner: repo.ownerLogin, repo: repo.name) }
    private var inLibrary: Bool { model.apps.contains { $0.key == key } }
    private var saved: Bool { model.tryLater.contains { $0.key == key } }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 12) {
                Avatar(name: repo.name, url: model.isDemo ? nil : repo.avatarUrl.flatMap(URL.init(string:)))
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 8) {
                        Text(repo.name).font(.cardTitle).foregroundStyle(Theme.text).lineLimit(1)
                        if showRank && item.rank > 0 { Text("#\(item.rank)").font(.system(size: 11, weight: .semibold)).foregroundStyle(Theme.faint) }
                    }
                    Text(repo.fullName).font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
                }
                Spacer(minLength: 0)
                if showRank { movement }
                if let url { Link(destination: url) { Image(systemName: "arrow.up.right.square").font(.system(size: 14)).foregroundStyle(Theme.dim).frame(width: 28, height: 28) }.help("Open project page") }
            }
            Text(isBlankText(repo.description) ? " " : repo.description!).font(.dim).foregroundStyle(Theme.dim).lineLimit(2)
                .frame(maxWidth: .infinity, minHeight: 36, alignment: .topLeading).padding(.vertical, 12)
            HStack(spacing: 10) {
                HStack(spacing: 5) {
                    Image(systemName: "star.fill").font(.system(size: 11)).foregroundStyle(Theme.warn)
                    Text(repo.stars.compact).font(.system(size: 12, weight: .semibold)).foregroundStyle(Theme.text)
                    if !item.gainShort.isEmpty { Text(item.gainShort).font(.system(size: 11.5)).foregroundStyle(Theme.success) }
                }
                if let l = repo.language, !l.isEmpty { Pill(text: l) }
                Spacer(minLength: 0)
            }
            HStack(spacing: 8) {
                if item.hasMacBuild == true { Pill(text: "Mac download", kind: .success, symbol: "checkmark") }
                else if item.hasMacBuild == false { Pill(text: "No Mac download", kind: .warn) }
                if inLibrary { Pill(text: "In your library", kind: .accent) } else if saved { Pill(text: "Saved for later", kind: .accent) }
                Spacer(minLength: 0)
                if !inLibrary {
                    if !saved {
                        Button { Task { await add.saveForLater(repo) } } label: { Image(systemName: "bookmark") }.buttonStyle(OutlineButtonStyle(height: 28)).help("Save to Try later")
                    }
                    Button("Add") { Task { await add.open(repo: repo) } }.buttonStyle(PrimaryButtonStyle(height: 28)).help("Look at what it offers and add it to your library")
                }
            }
            .frame(minHeight: 26).padding(.top, 10)
        }
        .card()
        .contextMenu { if let url { Link("Open project page", destination: url) } }
    }

    @ViewBuilder private var movement: some View {
        switch item.move {
        case .up: Text(item.moveText).font(.system(size: 11, weight: .bold)).foregroundStyle(Theme.success).padding(.top, 5)
        case .down: Text(item.moveText).font(.system(size: 11, weight: .bold)).foregroundStyle(Theme.danger).padding(.top, 5)
        case .new: Pill(text: "NEW", kind: .accent)
        case .same: Text(item.moveText).font(.system(size: 11)).foregroundStyle(Theme.faint).padding(.top, 5)
        case .none: EmptyView()
        }
    }
}
