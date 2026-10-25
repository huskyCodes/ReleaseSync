import Foundation

/// A topic in Discover: the GitHub topics that feed it and a hand-picked list of well-known projects (`owner/repo`).
public struct DiscoverCategory: Equatable, Sendable, Identifiable {
    public let key: String
    public let title: String
    public let topics: [String]
    public let seeds: [String]
    public var id: String { key }
}

/// What Discover offers on the Mac: the same shape as the Windows catalog, with GitHub topics and favourites that run on macOS.
/// The picks are suggestions, not endorsements; a project that no longer exists is simply left out.
public enum DiscoverCatalog {
    public static let categories: [DiscoverCategory] = [
        DiscoverCategory(key: "all", title: "Everything",
                         topics: ["topic:macos topic:app", "topic:macos topic:utility", "topic:menubar-app", "topic:swiftui topic:macos", "topic:developer-tools topic:macos"], seeds: []),
        DiscoverCategory(key: "games", title: "Games & ports",
                         topics: ["topic:decompilation topic:macos", "topic:source-port topic:macos", "topic:recompilation", "topic:game topic:macos"],
                         seeds: ["HarbourMasters/Shipwright", "HarbourMasters/2ship2harkinian", "HarbourMasters/Starship", "sm64-port/sm64-port", "OpenRCT2/OpenRCT2", "OpenTTD/OpenTTD", "diasurgical/devilutionX", "RSDKModding/RSDKv5-Decompilation", "Eukaryot/sonic3air", "ScummVM/scummvm", "OpenMW/openmw", "OpenRA/OpenRA", "ZDoom/gzdoom", "dhewm/dhewm3", "yquake2/yquake2", "OpenXcom/OpenXcom"]),
        DiscoverCategory(key: "emulators", title: "Emulators",
                         topics: ["topic:emulator topic:macos", "topic:emulation topic:macos", "topic:nintendo-switch topic:emulator", "topic:playstation topic:emulator"],
                         seeds: ["dolphin-emu/dolphin", "PCSX2/pcsx2", "RPCS3/rpcs3", "stenzek/duckstation", "hrydgard/ppsspp", "flyinghead/flycast", "shadps4-emu/shadPS4", "azahar-emu/azahar", "xemu-project/xemu", "melonDS-emu/melonDS", "mgba-emu/mgba", "SourMesen/Mesen2", "libretro/RetroArch", "Vita3K/Vita3K", "ares-emulator/ares", "joncampbell123/dosbox-x", "snes9x/snes9x"]),
        DiscoverCategory(key: "utilities", title: "Utilities",
                         topics: ["topic:macos topic:utility", "topic:menubar-app", "topic:macos topic:productivity", "topic:window-manager topic:macos"],
                         seeds: ["rxhanson/Rectangle", "exelban/stats", "lwouis/alt-tab-macos", "MonitorControl/MonitorControl", "Hammerspoon/hammerspoon", "pqrs-org/Karabiner-Elements", "nikitabobko/AeroSpace", "jordanbaird/Ice", "ianyh/Amethyst", "aonez/Keka", "transmission/transmission"]),
        DiscoverCategory(key: "media", title: "Media",
                         topics: ["topic:media-player topic:macos", "topic:video topic:macos", "topic:music-player topic:macos", "topic:audio topic:macos"],
                         seeds: ["iina/iina", "obsproject/obs-studio", "HandBrake/HandBrake", "audacity/audacity", "mifi/lossless-cut", "jellyfin/jellyfin-desktop"]),
        DiscoverCategory(key: "dev", title: "Developer tools",
                         topics: ["topic:developer-tools topic:macos", "topic:cli topic:macos", "topic:terminal topic:macos", "topic:code-editor topic:macos"],
                         seeds: ["VSCodium/vscodium", "zed-industries/zed", "wez/wezterm", "alacritty/alacritty", "kovidgoyal/kitty", "neovide/neovide", "drawio-app/drawio-desktop"]),
        DiscoverCategory(key: "privacy", title: "Privacy & security",
                         topics: ["topic:privacy topic:macos", "topic:security topic:macos", "topic:password-manager", "topic:encryption topic:macos"],
                         seeds: ["bitwarden/clients", "keepassxreboot/keepassxc", "Cryptomator/cryptomator", "veracrypt/VeraCrypt", "mullvad/mullvadvpn-app", "objective-see/LuLu", "objective-see/BlockBlock", "mullvad/mullvad-browser"]),
        DiscoverCategory(key: "productivity", title: "Productivity",
                         topics: ["topic:productivity topic:macos", "topic:note-taking topic:macos", "topic:knowledge-base", "topic:todo topic:macos"],
                         seeds: ["logseq/logseq", "laurent22/joplin", "AppFlowy-IO/AppFlowy", "siyuan-note/siyuan", "toeverything/AFFiNE", "TriliumNext/Trilium", "Zettlr/Zettlr", "obsidianmd/obsidian-releases", "marktext/marktext"]),
        DiscoverCategory(key: "customize", title: "Customization",
                         topics: ["topic:customization topic:macos", "topic:menubar-app topic:macos", "topic:theme topic:macos", "topic:wallpaper topic:macos"],
                         seeds: ["pqrs-org/Karabiner-Elements", "jordanbaird/Ice", "nikitabobko/AeroSpace", "rxhanson/Rectangle", "ianyh/Amethyst", "exelban/stats"]),
        DiscoverCategory(key: "graphics", title: "Graphics & design",
                         topics: ["topic:graphics topic:macos", "topic:image-editor", "topic:3d topic:macos", "topic:game-engine topic:macos"],
                         seeds: ["godotengine/godot", "upscayl/upscayl", "drawio-app/drawio-desktop", "KDE/krita"]),
        DiscoverCategory(key: "communication", title: "Communication",
                         topics: ["topic:chat topic:macos", "topic:messenger topic:macos", "topic:voip", "topic:discord-client"],
                         seeds: ["Vencord/Vesktop", "mumble-voip/mumble", "simplex-chat/simplex-chat", "element-hq/element-desktop"]),
        DiscoverCategory(key: "ai", title: "AI & data",
                         topics: ["topic:llm topic:macos", "topic:stable-diffusion topic:macos", "topic:ai topic:desktop-app", "topic:machine-learning topic:macos"],
                         seeds: ["ollama/ollama", "janhq/jan", "nomic-ai/gpt4all", "invoke-ai/InvokeAI", "SillyTavern/SillyTavern"]),
        DiscoverCategory(key: "internet", title: "Browsers & internet",
                         topics: ["topic:browser topic:macos", "topic:download-manager", "topic:vpn topic:macos", "topic:rss-reader topic:macos"],
                         seeds: ["zen-browser/desktop", "brave/brave-browser", "Floorp-Projects/Floorp", "mullvad/mullvad-browser", "agalwood/Motrix", "transmission/transmission"]),
    ]

    public static func find(_ key: String?) -> DiscoverCategory { categories.first { $0.key == key } ?? categories[0] }
}
