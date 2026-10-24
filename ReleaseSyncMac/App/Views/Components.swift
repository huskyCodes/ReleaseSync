import SwiftUI

/// A round-cornered tile with the first letter of a name, in a colour that stays the same for the same name. (No logos or avatars.)
struct InitialTile: View {
    let name: String
    var size: CGFloat = 32

    private var color: Color {
        var hash: UInt32 = 5381
        for b in name.lowercased().utf8 { hash = (hash &* 33) &+ UInt32(b) }
        return Color(hue: Double(hash % 360) / 360, saturation: 0.45, brightness: 0.78)
    }

    var body: some View {
        Text(String(name.first.map { Character($0.uppercased()) } ?? "?"))
            .font(.system(size: size * 0.5, weight: .semibold, design: .rounded))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(color.gradient, in: RoundedRectangle(cornerRadius: size * 0.26, style: .continuous))
            .accessibilityHidden(true)
    }
}

/// A small rounded label.
struct Chip: View {
    let text: String
    var symbol: String?

    var body: some View {
        HStack(spacing: 3) {
            if let symbol { Image(systemName: symbol).imageScale(.small) }
            Text(text)
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 7).padding(.vertical, 2)
        .background(.quaternary.opacity(0.6), in: Capsule())
    }
}

/// Chips that wrap onto more lines when they don't fit.
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

extension View {
    /// Liquid Glass on macOS 26 and later; a soft material on older systems.
    @ViewBuilder func glassPill() -> some View {
        if #available(macOS 26, *) {
            self.padding(.horizontal, 14).padding(.vertical, 7).glassEffect(.regular, in: .capsule)
        } else {
            self.padding(.horizontal, 14).padding(.vertical, 7).background(.regularMaterial, in: Capsule())
        }
    }
}
