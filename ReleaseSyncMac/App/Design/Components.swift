import AppKit
import SwiftUI

// MARK: surfaces

extension View {
    /// A card: the Windows app's `Card` style (rounded, 1 pt line, card colour).
    func card(radius: CGFloat = Theme.cardRadius, padding: EdgeInsets = EdgeInsets(top: 16, leading: 18, bottom: 16, trailing: 18), hovering: Bool = false) -> some View {
        self.padding(padding)
            .background(hovering ? Theme.cardHover : Theme.card, in: RoundedRectangle(cornerRadius: radius, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous).strokeBorder(hovering ? Theme.accentSoftStrong : Theme.line, lineWidth: 1))
    }
}

/// A card that lights up when the pointer is over it, and can be clicked.
struct ClickableCard<Content: View>: View {
    var radius: CGFloat = Theme.cardRadius
    var padding = EdgeInsets(top: 16, leading: 18, bottom: 16, trailing: 18)
    var dimmed = false
    let action: () -> Void
    @ViewBuilder let content: () -> Content
    @State private var hovering = false

    var body: some View {
        content()
            .card(radius: radius, padding: padding, hovering: hovering)
            .opacity(dimmed ? 0.6 : 1)
            .contentShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
            .onHover { hovering = $0 }
            .onTapGesture(perform: action)
    }
}

/// Small pictures for projects, fetched on demand. They are kept in memory only (nothing is written to disk, so no list of the projects you
/// follow is left behind), a few at a time, and only from sites that ReleaseSync already talks to.
@MainActor
final class AvatarCache {
    static let shared = AvatarCache()
    private let cache = NSCache<NSURL, NSImage>()
    private var inflight: [URL: Task<NSImage?, Never>] = [:]
    private let session: URLSession = {
        let c = URLSessionConfiguration.ephemeral
        c.httpMaximumConnectionsPerHost = 4; c.timeoutIntervalForRequest = 15; c.waitsForConnectivity = false
        return URLSession(configuration: c)
    }()

    static func isAllowed(_ url: URL) -> Bool {
        guard url.scheme == "https", let host = url.host?.lowercased() else { return false }
        return ["github.com", "githubusercontent.com", "gitlab.com", "codeberg.org", "gravatar.com", "gitlab.io"].contains { host == $0 || host.hasSuffix("." + $0) }
    }

    func image(for url: URL) async -> NSImage? {
        guard Self.isAllowed(url) else { return nil }
        if let hit = cache.object(forKey: url as NSURL) { return hit }
        if let running = inflight[url] { return await running.value }
        let task = Task { [session] () -> NSImage? in
            guard let (data, response) = try? await session.data(from: url), (response as? HTTPURLResponse)?.statusCode == 200, data.count < 2_000_000 else { return nil }
            return NSImage(data: data)
        }
        inflight[url] = task
        let image = await task.value
        inflight[url] = nil
        if let image { cache.setObject(image, forKey: url as NSURL) }
        return image
    }
}

/// The project's picture on a soft tile, or the first letter of its name until (or unless) there is one.
struct Avatar: View {
    let name: String
    var size: CGFloat = Theme.avatar
    var url: URL?
    @State private var picture: NSImage?

    var body: some View {
        ZStack {
            Text(String(name.first.map { Character($0.uppercased()) } ?? "?"))
                .font(.system(size: size * 0.43, weight: .bold))
                .foregroundStyle(Theme.accentText)
            if let picture { Image(nsImage: picture).resizable().scaledToFill().transition(.opacity) }
        }
        .frame(width: size, height: size)
        .background(Theme.accentSoft)
        .clipShape(RoundedRectangle(cornerRadius: 11, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 11, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
        .task(id: url) {
            guard let url else { picture = nil; return }
            let image = await AvatarCache.shared.image(for: url)
            withAnimation(.easeOut(duration: 0.15)) { picture = image }
        }
        .accessibilityHidden(true)
    }
}

/// The brand tile at the top of the sidebar and on the empty screens.
struct BrandTile: View {
    var size: CGFloat = 34

    var body: some View {
        Image(systemName: "arrow.triangle.2.circlepath")
            .font(.system(size: size * 0.5, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(Theme.brand, in: RoundedRectangle(cornerRadius: size * 0.3, style: .continuous))
            .accessibilityHidden(true)
    }
}

// MARK: pills and chips

/// A small status or label pill.
struct Pill: View {
    enum Kind { case neutral, accent, success, warn, danger }
    let text: String
    var kind = Kind.neutral
    var dot = false
    var symbol: String?

    private var foreground: Color {
        switch kind {
        case .neutral: Theme.dim
        case .accent: Theme.accentText
        case .success: Theme.success
        case .warn: Theme.warn
        case .danger: Theme.danger
        }
    }
    private var background: Color {
        switch kind {
        case .neutral: Theme.soft
        case .accent: Theme.accentSoft
        case .success: Theme.successSoft
        case .warn: Theme.warnSoft
        case .danger: Theme.dangerSoft
        }
    }

    var body: some View {
        HStack(spacing: 6) {
            if dot { Circle().fill(kind == .neutral ? Theme.faint : foreground).frame(width: 6, height: 6) }
            if let symbol { Image(systemName: symbol).font(.system(size: 10, weight: .semibold)) }
            Text(text).lineLimit(1)
        }
        .font(.system(size: 11, weight: .semibold))
        .foregroundStyle(foreground)
        .padding(.horizontal, 9).padding(.vertical, 3)
        .background(background, in: Capsule())
    }
}

/// A filter chip (the Windows `Chip` style): rounded, soft, and tinted when chosen.
struct Chip: View {
    let title: String
    var count: Int?
    /// A small status light before the title (green when online).
    var light: Bool?
    let selected: Bool
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                if let light { StatusLight(on: light) }
                Text(title).fontWeight(.semibold)
                if let count { Text("\(count)").fontWeight(.regular) }
            }
            .font(.system(size: 12.5))
            .foregroundStyle(selected ? Theme.accentText : Theme.dim)
            .padding(.horizontal, 14).padding(.vertical, 7)
            .background(selected ? Theme.accentSoft : (hovering ? Theme.softHover : Theme.soft), in: Capsule())
            .overlay(Capsule().strokeBorder(selected ? Theme.accentSoftStrong : .clear, lineWidth: 1))
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

/// A row of chips that scrolls sideways when there are too many.
struct ChipStrip<Content: View>: View {
    @ViewBuilder let content: () -> Content
    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) { HStack(spacing: 8) { content() } }
    }
}

// MARK: buttons

struct PrimaryButtonStyle: ButtonStyle {
    var height: CGFloat = Theme.buttonHeight
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(.white)
            .padding(.horizontal, 14)
            .frame(minHeight: height)
            .background(configuration.isPressed ? Theme.accentHover : Theme.accent, in: RoundedRectangle(cornerRadius: 9, style: .continuous))
            .opacity(enabled ? 1 : 0.45)
    }
}

struct OutlineButtonStyle: ButtonStyle {
    var height: CGFloat = Theme.buttonHeight
    @Environment(\.isEnabled) private var enabled
    @State private var hovering = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(Theme.text)
            .padding(.horizontal, 14)
            .frame(minHeight: height)
            .background(hovering || configuration.isPressed ? Theme.softHover : Color.clear, in: RoundedRectangle(cornerRadius: 9, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
            .opacity(enabled ? 1 : 0.5)
            .onHover { hovering = $0 }
    }
}

/// The plain grey button (Windows `BaseButton`).
struct SoftButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    @State private var hovering = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(Theme.text)
            .padding(.horizontal, 14)
            .frame(minHeight: Theme.buttonHeight)
            .background(hovering || configuration.isPressed ? Theme.softHover : Theme.soft, in: RoundedRectangle(cornerRadius: 9, style: .continuous))
            .opacity(enabled ? 1 : 0.5)
            .onHover { hovering = $0 }
    }
}

struct IconButton: View {
    let symbol: String
    let help: String
    var size: CGFloat = 34
    var selected = false
    var tint: Color?
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 14, weight: .medium))
                .foregroundStyle(tint ?? (selected ? Theme.accentText : Theme.dim))
                .frame(width: size, height: size)
                .background(selected ? Theme.accentSoft : (hovering ? Theme.soft : .clear), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        }
        .buttonStyle(.plain)
        .help(help)
        .accessibilityLabel(help)
        .onHover { hovering = $0 }
    }
}

/// Two or three icon buttons in a soft tray (the grid/list switch).
struct IconSwitch<Value: Hashable>: View {
    struct Option { let value: Value; let symbol: String; let help: String }
    let options: [Option]
    @Binding var selection: Value

    var body: some View {
        HStack(spacing: 0) {
            ForEach(options.indices, id: \.self) { i in
                IconButton(symbol: options[i].symbol, help: options[i].help, selected: selection == options[i].value) { selection = options[i].value }
            }
        }
        .padding(2)
        .background(Theme.soft, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}

/// The search box used on the pages (Windows `SearchBox`).
struct SearchBox: View {
    let prompt: String
    @Binding var text: String
    var width: CGFloat = 220

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").font(.system(size: 12)).foregroundStyle(Theme.faint)
            TextField(prompt, text: $text).textFieldStyle(.plain).font(.system(size: 13))
            if !text.isEmpty {
                Button { text = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(Theme.faint) }.buttonStyle(.plain).help("Clear")
            }
        }
        .padding(.horizontal, 12)
        .frame(width: width, height: 36)
        .background(Theme.input, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
    }
}

// MARK: headings

struct PageHeader: View {
    let title: String
    var subtitle: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.h1).foregroundStyle(Theme.text)
            if let subtitle { Text(subtitle).font(.dim).foregroundStyle(Theme.dim) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// The folding header of a group of cards: chevron, name and a count.
struct SectionHeader: View {
    let title: String
    let count: Int
    @Binding var open: Bool

    var body: some View {
        Button { withAnimation(.easeOut(duration: 0.15)) { open.toggle() } } label: {
            HStack(spacing: 10) {
                Image(systemName: "chevron.down").font(.system(size: 10, weight: .semibold)).foregroundStyle(Theme.faint)
                    .rotationEffect(.degrees(open ? 0 : -90)).frame(width: 12)
                Text(title).font(.system(size: 14.5, weight: .semibold)).foregroundStyle(Theme.text)
                Text("\(count)").font(.system(size: 11, weight: .semibold)).foregroundStyle(Theme.dim)
                    .padding(.horizontal, 8).padding(.vertical, 1).background(Theme.soft, in: Capsule())
            }
            .padding(.vertical, 4).padding(.trailing, 12)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("\(title), \(count)")
        .accessibilityHint(open ? "Folds this section" : "Opens this section")
    }
}

/// A calm message with an icon, used when there is nothing to show.
struct EmptyMessage<Actions: View>: View {
    let symbol: String
    let title: String
    let message: String
    var brand = false
    @ViewBuilder var actions: () -> Actions

    var body: some View {
        VStack(spacing: 0) {
            if brand { BrandTile(size: 84) }
            else { Image(systemName: symbol).font(.system(size: 34)).foregroundStyle(Theme.faint) }
            Text(title).font(brand ? .h1 : .h2).foregroundStyle(Theme.text).padding(.top, brand ? 24 : 14).padding(.bottom, brand ? 8 : 4)
            Text(message).font(.system(size: brand ? 14 : 12.5)).foregroundStyle(Theme.dim).multilineTextAlignment(.center).fixedSize(horizontal: false, vertical: true)
            actions().padding(.top, 22)
        }
        // a minimum width, so that when SwiftUI asks "how small can this be?" the text can't wrap a letter per line and ask for a huge window
        .frame(minWidth: 300, maxWidth: 520)
        .padding(.horizontal, 24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

extension EmptyMessage where Actions == EmptyView {
    init(symbol: String, title: String, message: String, brand: Bool = false) {
        self.init(symbol: symbol, title: title, message: message, brand: brand) { EmptyView() }
    }
}

/// Pills that wrap onto more lines when they don't fit.
struct ChipFlow: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        arrange(width: proposal.width ?? .infinity, subviews).size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for (view, frame) in zip(subviews, arrange(width: bounds.width, subviews).frames) {
            view.place(at: CGPoint(x: bounds.minX + frame.minX, y: bounds.minY + frame.minY), proposal: ProposedViewSize(frame.size))
        }
    }

    private func arrange(width: CGFloat, _ subviews: Subviews) -> (frames: [CGRect], size: CGSize) {
        var frames: [CGRect] = [], x: CGFloat = 0, y: CGFloat = 0, rowHeight: CGFloat = 0, maxX: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > 0, x + size.width > width { x = 0; y += rowHeight + spacing; rowHeight = 0 }
            frames.append(CGRect(origin: CGPoint(x: x, y: y), size: size))
            x += size.width + spacing; rowHeight = max(rowHeight, size.height); maxX = max(maxX, x - spacing)
        }
        return (frames, CGSize(width: maxX, height: y + rowHeight))
    }
}

/// The little light beside a device: green when it says it is running ReleaseSync, grey otherwise.
struct StatusLight: View {
    let on: Bool
    var body: some View {
        Circle().fill(on ? Theme.success : Theme.faint.opacity(0.55)).frame(width: 8, height: 8)
            .overlay(Circle().strokeBorder(on ? Theme.success.opacity(0.35) : .clear, lineWidth: 3))
            .accessibilityLabel(on ? "Online" : "Offline")
    }
}
