# ReleaseSync for Mac

A native SwiftUI app. This first version only **reads** your library: it shows your apps, your Try later list and your devices from the private GitHub Gist you sync with. It never changes the sync file, and it doesn't install anything yet.

It uses [ReleaseSyncKit](../ReleaseSyncKit) for the sync file (the same rules as the Windows app) and needs macOS 14 or later. It looks best on macOS 26 and later, where the system's Liquid Glass look is used.

## Build and run

The Xcode project is generated from `project.yml` with [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`):

```
cd ReleaseSyncMac
xcodegen generate
open ReleaseSync.xcodeproj
```

Run the `ReleaseSync` scheme, or from a terminal: `xcodebuild -scheme ReleaseSync -destination 'platform=macOS' test`.

To look around without an account, run a Debug build with the argument `--demo`: it shows a made-up library and fetches nothing.

## Signing

By default the app is signed "ad hoc", for your own Mac only, so you don't need an Apple account to build it. Your own team, signing identity and bundle identifier go in `Config/Local.xcconfig` (copy `Config/Local.xcconfig.example`); that file is not committed.

## Your token and passphrase

Add your GitHub token (with the "gist" permission) and, if you locked your library, your sync passphrase in **ReleaseSync → Settings**. They are stored in your Mac's login Keychain under the same names the Windows app uses (`ReleaseSync/GitHubToken`, `ReleaseSync/SyncPassphrase`, `ReleaseSync/VirusTotalKey`) and nowhere else: not in files, logs, backups or preferences. The token is sent only to `api.github.com`. The app collects nothing and has no accounts or analytics.

A copy signed ad hoc is a different "app" to the Keychain each time it is rebuilt, so macOS may ask once whether to allow access. A properly signed copy doesn't.

## Entitlements and hardened runtime

- **Hardened runtime: on**, with no exceptions.
- **App Sandbox: off.** Installing apps (a later step) means writing to `/Applications` and running installers, which the sandbox doesn't allow. The app is meant to be shared as a notarized download from GitHub, not through the App Store.
- **Entitlements: none** in a Release build. (Debug builds carry Xcode's `get-task-allow` so they can be debugged.)
- The library is not cached on disk: it is fetched when the app opens or you press ⌘R. A cache would keep an unlocked copy of a library that you may have locked with a passphrase.
