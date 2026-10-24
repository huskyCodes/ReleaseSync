import AppKit
import SwiftUI

/// The look of ReleaseSync, taken from the Windows app's theme files (UI/Theme.Dark.xaml, UI/Theme.Light.xaml, App.xaml) so the two
/// feel like one product. Colours follow the Mac's light or dark appearance (or the choice in Settings).
enum Theme {
    // accent: #6C8CFF, with the same derived shades the Windows ThemeManager makes
    static let accent = Color(hex: 0x6C8CFF)
    static let accentHover = Color(hex: 0x6C8CFF).blended(with: .white, 0.15)
    static let accentSoft = Color(light: Color(hex: 0x6C8CFF, alpha: 0x1F / 255), dark: Color(hex: 0x6C8CFF, alpha: 0x2E / 255))
    static let accentSoftStrong = Color(hex: 0x6C8CFF, alpha: 0x55 / 255)
    static let accentText = Color(light: Color(hex: 0x6C8CFF).blended(with: .black, 0.25), dark: Color(hex: 0x6C8CFF).blended(with: .white, 0.12))

    static let bg = Color(light: Color(hex: 0xF3F5F9), dark: Color(hex: 0x0E1116))
    static let surface = Color(light: Color(hex: 0xFFFFFF), dark: Color(hex: 0x141820))
    static let card = Color(light: Color(hex: 0xFFFFFF), dark: Color(hex: 0x1A1F28))
    static let cardHover = Color(light: Color(hex: 0xF6F8FC), dark: Color(hex: 0x212733))
    static let line = Color(light: Color(hex: 0xE1E6EE), dark: Color(hex: 0x272E3A))
    static let text = Color(light: Color(hex: 0x18202E), dark: Color(hex: 0xE9EDF3))
    static let dim = Color(light: Color(hex: 0x566174), dark: Color(hex: 0x9BA5B4))
    static let faint = Color(light: Color(hex: 0x8993A4), dark: Color(hex: 0x6A7485))
    static let input = Color(light: Color(hex: 0xF7F9FC), dark: Color(hex: 0x10141A))
    static let soft = Color(light: Color(hex: 0x000000, alpha: 0x0F / 255), dark: Color(hex: 0xFFFFFF, alpha: 0x15 / 255))
    static let softHover = Color(light: Color(hex: 0x000000, alpha: 0x1A / 255), dark: Color(hex: 0xFFFFFF, alpha: 0x24 / 255))
    static let scrim = Color(light: Color(hex: 0x101828, alpha: 0x80 / 255), dark: Color(hex: 0x000000, alpha: 0xA6 / 255))

    static let success = Color(light: Color(hex: 0x16A765), dark: Color(hex: 0x3DD68C))
    static let successSoft = Color(light: Color(hex: 0x16A765, alpha: 0x1F / 255), dark: Color(hex: 0x3DD68C, alpha: 0x24 / 255))
    static let warn = Color(light: Color(hex: 0xB7791F), dark: Color(hex: 0xF5B94A))
    static let warnSoft = Color(light: Color(hex: 0xD69E2E, alpha: 0x26 / 255), dark: Color(hex: 0xF5B94A, alpha: 0x26 / 255))
    static let danger = Color(light: Color(hex: 0xD64545), dark: Color(hex: 0xF57878))
    static let dangerSoft = Color(light: Color(hex: 0xD64545, alpha: 0x1F / 255), dark: Color(hex: 0xF57878, alpha: 0x26 / 255))

    /// The brand tile: the same blue-to-violet gradient as the Windows app.
    static let brand = LinearGradient(colors: [Color(hex: 0x6C8CFF), Color(hex: 0x8A5CF6)], startPoint: .topLeading, endPoint: .bottomTrailing)

    // measurements, from App.xaml and Styles.xaml
    static let cardRadius: CGFloat = 14
    static let rowRadius: CGFloat = 12
    static let pagePadding: CGFloat = 32
    static let panelGap: CGFloat = 14
    static let avatar: CGFloat = 46
    static let rowAvatar: CGFloat = 40
    static let gridMin: CGFloat = 300
    static let wideMin: CGFloat = 380
    static let buttonHeight: CGFloat = 34
}

extension Color {
    init(hex: UInt32, alpha: Double = 1) {
        self.init(.sRGB, red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255, blue: Double(hex & 0xFF) / 255, opacity: alpha)
    }

    /// One colour for the light appearance and another for the dark one.
    init(light: Color, dark: Color) {
        self.init(nsColor: NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? NSColor(dark) : NSColor(light)
        })
    }

    func blended(with other: Color, _ amount: Double) -> Color {
        let a = NSColor(self).usingColorSpace(.sRGB) ?? .gray, b = NSColor(other).usingColorSpace(.sRGB) ?? .gray
        return Color(.sRGB, red: a.redComponent + (b.redComponent - a.redComponent) * amount, green: a.greenComponent + (b.greenComponent - a.greenComponent) * amount,
                     blue: a.blueComponent + (b.blueComponent - a.blueComponent) * amount, opacity: a.alphaComponent)
    }
}

// MARK: text styles (Styles.xaml: H1 26, H2 18, H3 14, Dim 12.5, Faint 11.5, Label 11)

extension Font {
    static let h1 = Font.system(size: 26, weight: .semibold)
    static let h2 = Font.system(size: 18, weight: .semibold)
    static let h3 = Font.system(size: 14, weight: .semibold)
    static let cardTitle = Font.system(size: 15, weight: .semibold)
    static let dim = Font.system(size: 12.5)
    static let faint = Font.system(size: 11.5)
    static let label = Font.system(size: 11, weight: .semibold)
}
