import ReleaseSyncKit
import SwiftUI

/// The Install / Update button of an app, or how far along it is while it works.
struct InstallButton: View {
    @Environment(InstallModel.self) private var install
    let app: SyncApp
    let facts: ReleaseFacts
    var compact = false

    var body: some View {
        if let p = install.progress(for: app.key) {
            HStack(spacing: 8) {
                if let f = p.fraction { ProgressView(value: f).frame(width: compact ? 60 : 90) } else { ProgressView().controlSize(.small) }
                Text(p.text).font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
            }
        } else if let title = facts.actionText {
            Button(title) { Task { await install.install(app, info: facts.info) } }
                .buttonStyle(PrimaryButtonStyle(height: 28))
                .help(facts.macAsset.map { "Downloads \($0.name)" } ?? "")
        }
    }
}

/// The question shown when an install needs the person's say-so (an app that isn't signed, or one that is open).
struct InstallQuestionAlert: ViewModifier {
    @Environment(InstallModel.self) private var install

    func body(content: Content) -> some View {
        let q = install.question
        content.alert(title(q), isPresented: Binding(get: { q != nil }, set: { if !$0 { install.answer(false) } }), presenting: q) { q in
            switch q.kind {
            case .unsigned: Button("Install anyway") { install.answer(true) }; Button("Cancel", role: .cancel) { install.answer(false) }
            case .quitApp: Button("Quit it and go on") { install.answer(true) }; Button("Cancel", role: .cancel) { install.answer(false) }
            }
        } message: { q in
            switch q.kind {
            case .unsigned(let findings):
                Text("ReleaseSync checked \(q.appTitle) and found that " + findings.joined(separator: ", and ") + ". macOS may ask you to confirm the first time you open it. Only go on if you trust where it came from.")
            case .quitApp(let name): Text("\(name) is open. ReleaseSync can quit it, which closes it without saving anything you haven't saved.")
            }
        }
    }

    private func title(_ q: InstallModel.Question?) -> String {
        guard let q else { return "" }
        switch q.kind {
        case .unsigned: return "Install \(q.appTitle)?"
        case .quitApp: return "Quit \(q.appTitle)?"
        }
    }
}

extension View { func installQuestions() -> some View { modifier(InstallQuestionAlert()) } }

// MARK: Updates

struct UpdatesPage: View {
    @Environment(LibraryModel.self) private var model
    @Environment(InstallModel.self) private var install
    @Environment(Toasts.self) private var toasts
    @Environment(Navigator.self) private var nav

    private var items: [(app: SyncApp, info: AppRelease?, facts: ReleaseFacts)] {
        model.apps.filter { !$0.hidden }.map { ($0, model.releases[$0.key], install.facts($0, model.releases[$0.key])) }
    }

    var body: some View {
        let all = items
        let waiting = all.filter { $0.facts.hasUpdate }.sorted { $0.app.title.localizedCaseInsensitiveCompare($1.app.title) == .orderedAscending }
        let installed = all.filter { $0.facts.isInstalled }
        let ignored = all.filter { $0.facts.updateIgnored }
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top) {
                PageHeader(title: "Updates", subtitle: subtitle(waiting.count, installed.count))
                Spacer()
                HStack(spacing: 10) {
                    Button { Task { await checkNow(model, toasts) } } label: { Label(model.isChecking ? "Checking…" : "Check now", systemImage: "arrow.clockwise") }
                        .buttonStyle(OutlineButtonStyle()).disabled(model.isChecking || model.isBusy)
                    Button { Task { await install.updateAll(waiting.map { ($0.app, $0.info) }) } } label: { Label("Update all (\(waiting.count))", systemImage: "arrow.down.circle") }
                        .buttonStyle(PrimaryButtonStyle()).disabled(waiting.isEmpty || waiting.contains { install.progress(for: $0.app.key) != nil })
                }
            }
            .padding(.horizontal, Theme.pagePadding).padding(.top, 14).padding(.bottom, 16)
            if installed.isEmpty {
                EmptyMessage(symbol: "arrow.triangle.2.circlepath", title: "Nothing installed yet",
                             message: "Apps you install with ReleaseSync on this Mac show up here, and ReleaseSync tells you when one has a new version. Pick an app in your Library and press Install.", brand: true) {
                    Button { nav.show(.library) } label: { Label("Open your Library", systemImage: "books.vertical") }.buttonStyle(PrimaryButtonStyle(height: 40))
                }
            } else if waiting.isEmpty {
                EmptyMessage(symbol: "checkmark.circle", title: "Everything is up to date",
                             message: "\(installed.count) app\(installed.count == 1 ? "" : "s") installed here" + (ignored.isEmpty ? "." : ", and \(ignored.count) with a newer version you chose to ignore."))
            } else {
                ScrollView {
                    LazyVStack(spacing: Theme.panelGap) {
                        ForEach(waiting, id: \.app.key) { UpdateRow(app: $0.app, facts: $0.facts) }
                    }
                    .padding(.horizontal, Theme.pagePadding).padding(.bottom, 32)
                }
            }
        }
    }

    private func subtitle(_ waiting: Int, _ installed: Int) -> String {
        if installed == 0 { return "Nothing installed yet" }
        if waiting == 0 { return "All \(installed) installed app\(installed == 1 ? " is" : "s are") up to date" }
        return "\(waiting) update\(waiting == 1 ? "" : "s") available  ·  \(installed) installed"
    }
}

private struct UpdateRow: View {
    @Environment(Navigator.self) private var nav
    @Environment(LibraryModel.self) private var model
    let app: SyncApp
    let facts: ReleaseFacts

    var body: some View {
        ClickableCard(radius: Theme.rowRadius, padding: EdgeInsets(top: 12, leading: 16, bottom: 12, trailing: 16), action: { nav.open(app) }) {
            HStack(spacing: 14) {
                Avatar(name: app.title, size: Theme.rowAvatar, url: facts.avatarURL(allowed: !model.isDemo))
                VStack(alignment: .leading, spacing: 2) {
                    Text(app.title).font(.h3).foregroundStyle(Theme.text).lineLimit(1)
                    Text(facts.versionText).font(.system(size: 12.5, weight: .semibold)).foregroundStyle(Theme.accentText).lineLimit(1)
                    if facts.versionUnrecognized { Text("This version isn't one of its recent releases.").font(.faint).foregroundStyle(Theme.faint) }
                }
                Spacer(minLength: 10)
                InstallButton(app: app, facts: facts)
            }
        }
    }
}

// MARK: Activity

struct ActivityPage: View {
    @Environment(ActivityLog.self) private var log

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top) {
                PageHeader(title: "Activity", subtitle: log.entries.isEmpty ? "Nothing yet" : "\(log.entries.count) event\(log.entries.count == 1 ? "" : "s") since ReleaseSync opened")
                Spacer()
                Button("Clear") { log.clear() }.buttonStyle(OutlineButtonStyle()).disabled(log.entries.isEmpty)
            }
            .padding(.horizontal, Theme.pagePadding).padding(.top, 14).padding(.bottom, 16)
            if log.entries.isEmpty {
                EmptyMessage(symbol: "clock.arrow.circlepath", title: "Nothing has happened yet", message: "Update checks, installs and removals are listed here, newest first.")
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(log.entries.reversed()) { e in
                            HStack(alignment: .firstTextBaseline, spacing: 12) {
                                Text(e.at.formatted(date: .omitted, time: .standard)).font(.system(size: 11.5, design: .monospaced)).foregroundStyle(Theme.faint)
                                Circle().fill(color(e.kind)).frame(width: 7, height: 7)
                                Text(e.text).font(.system(size: 13)).foregroundStyle(Theme.text).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                                Spacer(minLength: 0)
                            }
                            .padding(.vertical, 7)
                            Divider().overlay(Theme.line.opacity(0.5))
                        }
                    }
                    .padding(.horizontal, Theme.pagePadding).padding(.bottom, 32)
                }
            }
        }
    }

    private func color(_ k: ActivityLog.Entry.Kind) -> Color {
        switch k { case .info: return Theme.faint; case .success: return Theme.success; case .warn: return Theme.warn; case .error: return Theme.danger }
    }
}

// MARK: Scan Mac

struct ScanPage: View {
    @Environment(LibraryModel.self) private var model
    @Environment(InstallModel.self) private var install
    @Environment(ScanModel.self) private var scan
    @Environment(AddModel.self) private var add

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top) {
                PageHeader(title: "Scan Mac", subtitle: subtitle)
                Spacer()
                Button { Task { await scan.scan(library: model.apps) } } label: { Label(scan.isScanning ? "Scanning…" : (scan.lastScan == nil ? "Scan this Mac" : "Scan again"), systemImage: "magnifyingglass") }
                    .buttonStyle(PrimaryButtonStyle()).disabled(scan.isScanning)
            }
            .padding(.horizontal, Theme.pagePadding).padding(.top, 14).padding(.bottom, 16)
            if scan.lastScan == nil {
                EmptyMessage(symbol: "magnifyingglass", title: "Find the apps you already have",
                             message: "ReleaseSync looks through your Applications folders and shows which apps come from projects it can keep up to date. It only reads the apps' names and versions, and asks no website.", brand: true) {
                    Button { Task { await scan.scan(library: model.apps) } } label: { Label("Scan this Mac", systemImage: "magnifyingglass") }.buttonStyle(PrimaryButtonStyle(height: 40))
                }
            } else {
                ScrollView {
                    LazyVStack(spacing: Theme.panelGap) { ForEach(scan.rows) { ScanRow(row: $0) } }
                        .padding(.horizontal, Theme.pagePadding).padding(.bottom, 32)
                }
            }
        }
        .task { if scan.lastScan == nil, model.phase == .loaded { await scan.scan(library: model.apps) } }
    }

    private var subtitle: String {
        guard scan.lastScan != nil else { return "Not scanned yet" }
        let matches = scan.rows.filter { $0.match != nil }.count
        return "\(scan.rows.count) apps found  ·  \(matches) from projects ReleaseSync knows"
    }
}

private struct ScanRow: View {
    @Environment(LibraryModel.self) private var model
    @Environment(InstallModel.self) private var install
    @Environment(AddModel.self) private var add
    let row: ScanModel.Row

    var body: some View {
        HStack(spacing: 14) {
            Image(nsImage: NSWorkspace.shared.icon(forFile: row.app.path)).resizable().frame(width: 40, height: 40)
            VStack(alignment: .leading, spacing: 2) {
                Text(row.app.name).font(.h3).foregroundStyle(Theme.text).lineLimit(1)
                Text([row.app.version, (row.app.path as NSString).deletingLastPathComponent].compactMap { $0 }.joined(separator: "  ·  ")).font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
            }
            Spacer(minLength: 10)
            action
        }
        .card(radius: Theme.rowRadius, padding: EdgeInsets(top: 12, leading: 16, bottom: 12, trailing: 16))
    }

    @ViewBuilder private var action: some View {
        switch row.match {
        case .library(let key):
            if let app = model.apps.first(where: { $0.key == key }) {
                if install.records[key]?.bundlePath == row.app.path { Pill(text: "Kept up to date", kind: .success, symbol: "checkmark") }
                else { Button("Keep it up to date") { Task { await install.adopt(row.app, as: app, info: model.releases[key]) } }.buttonStyle(PrimaryButtonStyle(height: 28)) }
            }
        case .feed(let ref):
            VStack(alignment: .trailing, spacing: 4) {
                Text("\(ref.owner)/\(ref.repo)").font(.faint).foregroundStyle(Theme.faint)
                Button("Add to library") { Task { await add.open(ref.url) } }.buttonStyle(PrimaryButtonStyle(height: 28))
            }
        case nil:
            Button("Search GitHub") { Task { await add.open(row.app.name) } }.buttonStyle(OutlineButtonStyle(height: 28))
        }
    }
}
