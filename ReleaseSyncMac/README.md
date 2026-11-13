# ReleaseSync for Mac

A native SwiftUI app that looks and feels like [ReleaseSync on Windows](../README.md): the same sidebar (Library, Updates, Discover, Try later, Scan, Import & Export, Activity, Settings), the same cards, colours, grid and list views, groups, filters and details drawer, in a real Mac window with the system's sidebar, toolbar, Liquid Glass materials, SF Symbols and Mac shortcuts. The colours, sizes and wording come from the Windows app's theme files.

**What works today.**
- **Your library.** Shows your apps (groups, automatic groups by type, favorites, hidden apps, notes, tags), your Try later list and the devices that sync, from the private GitHub Gist you sync with. Looks up each project's latest release (version, date, notes, downloads, which file suits this Mac, or "No Mac build").
- **Install, update, uninstall.** Downloads over https, checks the SHA-256 against what the project published, reads what `codesign` and Gatekeeper say, and asks before anything unsigned (a checksum mismatch or a broken signature always stops). Installs from a disk image or zip (a package is opened in the macOS installer), keeps the version it replaces so it can be put back, never replaces an app that is open without asking, marks the app as downloaded so macOS asks once on first launch, and moves uninstalled apps to the Trash. You can also reinstall, or install any version, which is how you roll back.
- **Updates.** The Updates page lists what has a newer version, and Update all does them. Checks run on a schedule while the app is open (Settings), tell you once about each new version, and update the apps you set to update themselves.
- **Add apps.** The top bar takes a link, owner/repo or search words; Discover and Try later have Add buttons. Import & Export reads ReleaseSync backups from Windows, Obtainium exports, links and owner/repo lines, and writes a backup or link list.
- **Scan Mac.** Finds the apps you already have in your Applications folders, matches them to your library (by name, or by the update address the app itself uses), and keeps them up to date.
- **Changing your library** (favorites, hidden, groups, notes, options, adding and removing) is saved to your Gist, but only after you allow it once. It is off until then, and the app asks the first time you try.
- **Find my devices on this network** is designed (see [docs/LAN-PRESENCE.md](../docs/LAN-PRESENCE.md)); the screens exist but nothing listens or announces yet.

Not done yet: VirusTotal lookup, backups of your library, the Releases picker's notes view, and the Mac app updating itself.

**GitHub requests.** Everything is built to use as few as it can: checks are conditional (an unchanged project answers "not modified" and costs nothing), run a few at a time, pause before the hourly allowance is gone, and a counter in the sidebar and on Discover shows what is left. Discover has a Light mode that asks for about a third as much; in Full mode it quietly prepares the other tabs when a token is saved and plenty of allowance is left. What the app learns about your apps' releases is kept in a file encrypted with a key held in your Keychain, because it would otherwise show which projects you follow even if your library is locked.

It uses [ReleaseSyncKit](../ReleaseSyncKit) for the sync file (the same rules as the Windows app) and needs macOS 14 or later. It looks best on macOS 26 and later, where the system's Liquid Glass look is used.

## Build and run

The Xcode project is generated from `project.yml` with [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`):

```
cd ReleaseSyncMac
xcodegen generate
open ReleaseSync.xcodeproj
```

Run the `ReleaseSync` scheme, or from a terminal: `xcodebuild -scheme ReleaseSync -destination 'platform=macOS' test`. One test downloads and installs a real open-source app into a temporary folder; it only runs when asked: `TEST_RUNNER_RELEASESYNC_LIVE_TEST=1 xcodebuild … test -only-testing:ReleaseSyncTests/LiveInstallTests`.

To look around without an account, run a Debug build with the argument `--demo`: it shows a made-up library and fetches nothing and never touches the Keychain. Other Debug-only arguments: `--page library|tryLater|settings|updates|…`, `--status needsToken|needsPassphrase|wrongPassphrase|noLibrary|failed`, `--drawer` (open the first app), and `--snapshot <folder> --name <file> --appearance dark|light`, which saves a picture of the app's own window and quits (this is how the layout is checked without screen-recording access). None of this is in a Release build.

## Signing

By default the app is signed "ad hoc", for your own Mac only, so you don't need an Apple account to build it. Your own team, signing identity and bundle identifier go in `Config/Local.xcconfig` (copy `Config/Local.xcconfig.example`); that file is not committed.

## Making a real app, and releasing it

**For your own Mac.** Build the Release configuration and copy it to Applications, so you can launch it like any other app without Xcode:

```
xcodebuild -project ReleaseSync.xcodeproj -scheme ReleaseSync -configuration Release -destination 'generic/platform=macOS' build
ditto "$(xcodebuild -project ReleaseSync.xcodeproj -scheme ReleaseSync -configuration Release -showBuildSettings | awk -F' = ' '/ BUILT_PRODUCTS_DIR/{print $2}')/ReleaseSync.app" /Applications/ReleaseSync.app
```

**For everyone else.** `scripts/release.sh` builds it, signs it with a Developer ID, puts it in a disk image, has Apple notarize it and staples the ticket, so it opens on any Mac with no warning. It needs, once:

1. A **Developer ID Application** certificate in your Keychain (Xcode, Settings, Accounts, your team, Manage Certificates, +, Developer ID Application; only the account holder can make it).
2. A notarization login stored in your Keychain (it asks for an app-specific password from appleid.apple.com, and keeps it there; nothing goes in this repository): `xcrun notarytool store-credentials "releasesync-notary" --apple-id "you@example.com" --team-id "YOURTEAMID"`

Then: `DEVELOPER_ID="Developer ID Application: Your Name (YOURTEAMID)" NOTARY_PROFILE="releasesync-notary" scripts/release.sh`. The result is `dist/ReleaseSync-<version>.dmg` and its SHA-256, ready to attach to a GitHub release. Add `SKIP_NOTARIZE=1` (and use any signing identity) to try the steps without sending anything to Apple. The version number comes from `MARKETING_VERSION` in `project.yml`.

## Your token and passphrase

Add your GitHub token (with the "gist" permission) and, if you locked your library, your sync passphrase in **Settings** (⌘,). They are stored in your Mac's login Keychain under the same names the Windows app uses (`ReleaseSync/GitHubToken`, `ReleaseSync/SyncPassphrase`, `ReleaseSync/VirusTotalKey`) and nowhere else: not in files, logs, backups or preferences. The token is sent only to `api.github.com`. The app collects nothing and has no accounts or analytics.

A copy signed ad hoc is a different "app" to the Keychain each time it is rebuilt, so macOS may ask once whether to allow access. A properly signed copy doesn't.

## Entitlements and hardened runtime

- **Hardened runtime: on**, with no exceptions.
- **App Sandbox: off.** Installing apps means writing to `/Applications` and running macOS's own tools (`hdiutil`, `ditto`, `codesign`, `spctl`), which the sandbox doesn't allow. Those tools are started with an exact list of arguments, never through a shell. The app is meant to be shared as a notarized download from GitHub, not through the App Store.
- **Entitlements: none** in a Release build. (Debug builds carry Xcode's `get-task-allow` so they can be debugged.)
- Keychain access never happens on the main thread, so the app stays responsive while macOS asks for permission. Preferences such as grid or list, sorting and light or dark are ordinary app preferences; no secrets and no library data are kept there. The library is not cached on disk: it is fetched when the app opens or you press ⌘R. A cache would keep an unlocked copy of a library that you may have locked with a passphrase.
