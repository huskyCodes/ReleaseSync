import AppKit
import ReleaseSyncKit
import SwiftUI

struct RootView: View {
    @Environment(LibraryModel.self) private var model
    @Environment(ViewPrefs.self) private var prefs
    @Environment(Toasts.self) private var toasts
    @Environment(Navigator.self) private var nav

    var body: some View {
        NavigationSplitView {
            Sidebar()
                .navigationSplitViewColumnWidth(min: 220, ideal: 252, max: 300)
        } detail: {
            ZStack(alignment: .trailing) {
                VStack(spacing: 0) {
                    TopBar()
                    PageHost(page: nav.page)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                .background(Theme.bg)

                if let app = nav.drawerApp {
                    Theme.scrim.ignoresSafeArea().onTapGesture { nav.closeDrawer() }.transition(.opacity)
                    DetailDrawer(app: app, close: { nav.closeDrawer() })
                        .frame(width: 440)
                        .background(Theme.surface)
                        .overlay(alignment: .leading) { Rectangle().fill(Theme.line).frame(width: 1) }
                        .shadow(color: .black.opacity(0.35), radius: 24)
                        .transition(.move(edge: .trailing))
                }
                ToastStack()
            }
            .animation(.easeOut(duration: 0.2), value: nav.drawerApp?.key)
            .navigationTitle("ReleaseSync")
            .navigationSubtitle(nav.page.title)
        }
        .background(escapeToClose)
        .task { await model.refresh() }
        .onChange(of: model.doc?.apps.map(\.key)) { _, keys in if let d = nav.drawerApp, keys?.contains(d.key) != true { nav.closeDrawer() } }
        .frame(minWidth: 820, minHeight: 520)
    }

    /// Esc closes the details drawer.
    private var escapeToClose: some View {
        Button("") { nav.closeDrawer() }.keyboardShortcut(.escape, modifiers: []).opacity(0).frame(width: 0, height: 0).accessibilityHidden(true)
    }
}

struct PageHost: View {
    @Environment(LibraryModel.self) private var model
    let page: Page

    var body: some View {
        Group {
            switch page {
            case .library, .tryLater:
                if model.phase == .loaded {
                    if page == .library { LibraryPage() } else { TryLaterPage() }
                } else { StatusView() }
            case .settings: SettingsPage()
            default: ComingPage(page: page)
            }
        }
    }
}

// MARK: sidebar

struct Sidebar: View {
    @Environment(LibraryModel.self) private var model
    @Environment(Navigator.self) private var nav

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                BrandTile()
                VStack(alignment: .leading, spacing: 1) {
                    Text("ReleaseSync").font(.system(size: 17, weight: .semibold)).foregroundStyle(Theme.text)
                    Text("Apps from GitHub, kept current").font(.system(size: 10.5)).foregroundStyle(Theme.faint).lineLimit(1)
                }
            }
            .padding(.horizontal, 6).padding(.top, 8).padding(.bottom, 18)

            ForEach(Page.primary) { item in
                NavButton(page: item, selected: nav.page == item, badge: badge(item)) { nav.show(item) }
            }
            Spacer(minLength: 12)
            NavButton(page: .settings, selected: nav.page == .settings, badge: 0) { nav.show(.settings) }
        }
        .padding(.horizontal, 12).padding(.bottom, 12)
        .frame(maxHeight: .infinity, alignment: .top)
    }

    private func badge(_ item: Page) -> Int { item == .tryLater && model.phase == .loaded ? model.tryLater.count : 0 }
}

private struct NavButton: View {
    let page: Page
    let selected: Bool
    let badge: Int
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Image(systemName: page.symbol).font(.system(size: 14, weight: .medium)).frame(width: 24)
                    .foregroundStyle(selected ? Theme.accentText : Theme.dim)
                Text(page.title).font(.system(size: 13.5, weight: .semibold)).foregroundStyle(selected ? Theme.text : Theme.dim)
                Spacer(minLength: 4)
                if badge > 0 {
                    Text("\(badge)").font(.system(size: 11, weight: .semibold)).foregroundStyle(.white)
                        .padding(.horizontal, 6).frame(minWidth: 20, minHeight: 20).background(Theme.accent, in: Capsule())
                } else if !page.available {
                    Text("Soon").font(.system(size: 10.5, weight: .medium)).foregroundStyle(Theme.faint)
                }
            }
            .padding(.horizontal, 12).frame(height: 42)
            .background(selected ? Theme.accentSoft : (hovering ? Theme.soft : .clear), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(alignment: .leading) {
                if selected { Capsule().fill(Theme.accent).frame(width: 3, height: 18) }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .padding(.vertical, 2)
        .onHover { hovering = $0 }
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

// MARK: top bar

struct TopBar: View {
    @Environment(LibraryModel.self) private var model
    @Environment(Toasts.self) private var toasts

    var body: some View {
        HStack(spacing: 14) {
            // the search-and-add box: the Windows app finds projects on GitHub from here; that is coming to the Mac
            HStack(spacing: 0) {
                Image(systemName: "magnifyingglass").font(.system(size: 14)).foregroundStyle(Theme.faint).frame(width: 46)
                Text("Search GitHub, or paste a repository link to add it… (coming soon)").font(.system(size: 13)).foregroundStyle(Theme.faint).lineLimit(1)
                Spacer(minLength: 0)
            }
            .frame(height: 44)
            .background(Theme.input, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
            .frame(maxWidth: 760)
            .help("Searching GitHub and adding apps from the Mac is coming. It works in ReleaseSync on Windows.")

            Spacer(minLength: 0)

            Button { Task { await syncNow(model, toasts) } } label: {
                HStack(spacing: 8) {
                    if model.isBusy { ProgressView().controlSize(.small) } else { Image(systemName: "arrow.clockwise").font(.system(size: 13, weight: .medium)) }
                    Text(model.isBusy ? "Checking…" : "Sync now")
                }
            }
            .buttonStyle(OutlineButtonStyle(height: 44))
            .disabled(model.isBusy)
            .help("Check your library for changes (⌘R)")
        }
        .padding(.horizontal, 28).padding(.top, 16).padding(.bottom, 6)
    }
}

/// Checks the library for changes and says how it went.
@MainActor
func syncNow(_ model: LibraryModel, _ toasts: Toasts) async {
    let had = model.doc != nil
    await model.refresh()
    switch model.phase {
    case .loaded: if had { toasts.show("Your library is up to date", .success) }
    case .failed(let message): toasts.show(message, .error)
    default: break
    }
}

// MARK: toasts

struct ToastStack: View {
    @Environment(Toasts.self) private var toasts

    var body: some View {
        VStack(alignment: .trailing, spacing: 8) {
            ForEach(toasts.items) { toast in
                HStack(alignment: .top, spacing: 12) {
                    Image(systemName: symbol(toast.kind)).foregroundStyle(tint(toast.kind)).font(.system(size: 14))
                    Text(toast.message).font(.system(size: 13)).foregroundStyle(Theme.text).fixedSize(horizontal: false, vertical: true)
                    Button { toasts.dismiss(toast) } label: { Image(systemName: "xmark").font(.system(size: 10, weight: .semibold)).foregroundStyle(Theme.faint) }
                        .buttonStyle(.plain).help("Close")
                }
                .padding(.horizontal, 14).padding(.vertical, 12)
                .frame(minWidth: 260, maxWidth: 420, alignment: .leading)
                .background(Theme.card, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
                .shadow(color: .black.opacity(0.3), radius: 14, y: 4)
                .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomTrailing)
        .animation(.easeOut(duration: 0.2), value: toasts.items)
        .allowsHitTesting(!toasts.items.isEmpty)
    }

    private func symbol(_ k: Toasts.Toast.Kind) -> String {
        switch k { case .info: "info.circle.fill"; case .success: "checkmark.circle.fill"; case .warn: "exclamationmark.triangle.fill"; case .error: "xmark.octagon.fill" }
    }
    private func tint(_ k: Toasts.Toast.Kind) -> Color {
        switch k { case .info: Theme.accentText; case .success: Theme.success; case .warn: Theme.warn; case .error: Theme.danger }
    }
}

// MARK: pages that aren't ready, and the screens before there is a library

struct ComingPage: View {
    let page: Page

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            PageHeader(title: page.title).padding(.horizontal, Theme.pagePadding).padding(.top, 14)
            EmptyMessage(symbol: page.symbol, title: "Coming to the Mac", message: page.comingMessage)
        }
    }
}

struct StatusView: View {
    @Environment(LibraryModel.self) private var model
    @Environment(Navigator.self) private var nav

    var body: some View {
        switch model.phase {
        case .loading, .loaded:
            VStack(spacing: 14) { ProgressView(); Text("Checking your library…").font(.dim).foregroundStyle(Theme.dim) }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        case .needsToken:
            EmptyMessage(symbol: "key", title: "Connect your library", message: "Add the GitHub token you use to sync ReleaseSync on your other devices. It stays in your Mac's Keychain and is only sent to GitHub.", brand: true) {
                Button { nav.show(.settings) } label: { Label("Add your token", systemImage: "key") }
                    .buttonStyle(PrimaryButtonStyle(height: 40))
            }
        case .needsPassphrase(let wrong):
            EmptyMessage(symbol: "lock", title: wrong ? "That passphrase didn't work" : "Your library is locked",
                         message: wrong ? "Check it and try again. It has to be the passphrase you set up on your other device." : "Enter the passphrase you set up on your other device.", brand: true) {
                Button { nav.show(.settings) } label: { Label("Enter your passphrase", systemImage: "lock") }
                    .buttonStyle(PrimaryButtonStyle(height: 40))
            }
        case .noLibrary:
            EmptyMessage(symbol: "tray", title: "No library found", message: "Your GitHub account doesn't have a ReleaseSync library yet. Turn on sync in ReleaseSync on your other device first, then check again.", brand: true) {
                Button { Task { await model.refresh() } } label: { Label("Check again", systemImage: "arrow.clockwise") }
                    .buttonStyle(PrimaryButtonStyle(height: 40))
            }
        case .keychainBlocked(let message):
            EmptyMessage(symbol: "lock.trianglebadge.exclamationmark", title: "ReleaseSync can't use your saved details", message: message) {
                HStack {
                    Button("Try again") { Task { await model.refresh() } }.buttonStyle(PrimaryButtonStyle(height: 40))
                    Button("Open Keychain Access") { NSWorkspace.shared.open(URL(fileURLWithPath: "/System/Applications/Utilities/Keychain Access.app")) }.buttonStyle(OutlineButtonStyle(height: 40))
                }
            }
        case .failed(let message):
            EmptyMessage(symbol: "exclamationmark.triangle", title: "Couldn't open your library", message: message) {
                HStack {
                    Button("Try again") { Task { await model.refresh() } }.buttonStyle(PrimaryButtonStyle(height: 40))
                    Button("Settings") { nav.show(.settings) }.buttonStyle(OutlineButtonStyle(height: 40))
                }
            }
        }
    }
}

