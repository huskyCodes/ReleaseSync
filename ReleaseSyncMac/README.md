# ReleaseSync for Mac

A native SwiftUI app that looks and feels like [ReleaseSync on Windows](../README.md): the same sidebar (Library, Updates, Discover, Try later, Scan, Import & Export, Activity, Settings), the same cards, colours, grid and list views, groups, filters and details drawer, in a real Mac window with the system's sidebar, toolbar, Liquid Glass materials, SF Symbols and Mac shortcuts. The colours, sizes and wording come from the Windows app's theme files.

**What works today.** This first version only **reads** your library. It shows your apps (with your groups, automatic groups by type, favorites, hidden apps, notes and tags), your Try later list and the devices that sync, from the private GitHub Gist you sync with. It never changes the sync file and doesn't install anything. Updates, Discover, Scan, Import & Export and Activity are in the sidebar marked "Soon"; they need release lookups, writing to your library and installing, which come in later steps.

It uses [ReleaseSyncKit](../ReleaseSyncKit) for the sync file (the same rules as the Windows app) and needs macOS 14 or later. It looks best on macOS 26 and later, where the system's Liquid Glass look is used.

## Build and run

The Xcode project is generated from `project.yml` with [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`):

```
cd ReleaseSyncMac
xcodegen generate
open ReleaseSync.xcodeproj
```

Run the `ReleaseSync` scheme, or from a terminal: `xcodebuild -scheme ReleaseSync -destination 'platform=macOS' test`.

To look around without an account, run a Debug build with the argument `--demo`: it shows a made-up library and fetches nothing and never touches the Keychain. Other Debug-only arguments: `--page library|tryLater|settings|updates|…`, `--status needsToken|needsPassphrase|wrongPassphrase|noLibrary|failed`, `--drawer` (open the first app), and `--snapshot <folder> --name <file> --appearance dark|light`, which saves a picture of the app's own window and quits (this is how the layout is checked without screen-recording access). None of this is in a Release build.

## Signing

By default the app is signed "ad hoc", for your own Mac only, so you don't need an Apple account to build it. Your own team, signing identity and bundle identifier go in `Config/Local.xcconfig` (copy `Config/Local.xcconfig.example`); that file is not committed.

## Your token and passphrase

Add your GitHub token (with the "gist" permission) and, if you locked your library, your sync passphrase in **Settings** (⌘,). They are stored in your Mac's login Keychain under the same names the Windows app uses (`ReleaseSync/GitHubToken`, `ReleaseSync/SyncPassphrase`, `ReleaseSync/VirusTotalKey`) and nowhere else: not in files, logs, backups or preferences. The token is sent only to `api.github.com`. The app collects nothing and has no accounts or analytics.

A copy signed ad hoc is a different "app" to the Keychain each time it is rebuilt, so macOS may ask once whether to allow access. A properly signed copy doesn't.

## Entitlements and hardened runtime

- **Hardened runtime: on**, with no exceptions.
- **App Sandbox: off.** Installing apps (a later step) means writing to `/Applications` and running installers, which the sandbox doesn't allow. The app is meant to be shared as a notarized download from GitHub, not through the App Store.
- **Entitlements: none** in a Release build. (Debug builds carry Xcode's `get-task-allow` so they can be debugged.)
- Keychain access never happens on the main thread, so the app stays responsive while macOS asks for permission. Preferences such as grid or list, sorting and light or dark are ordinary app preferences; no secrets and no library data are kept there. The library is not cached on disk: it is fetched when the app opens or you press ⌘R. A cache would keep an unlocked copy of a library that you may have locked with a passphrase.
