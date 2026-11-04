# ReleaseSync

Find free, open-source apps, install them, and keep them up to date, all from one tidy Windows app.

ReleaseSync works with projects on GitHub, GitLab, Codeberg and self-hosted Forgejo/Gitea sites. There's no account to create and nothing to pay.

## Get started

1. **Download** [`ReleaseSync.exe`](../../releases/latest/download/ReleaseSync.exe) from the [latest release](../../releases/latest). There's nothing else to install.
2. **Open it.** Windows may show a blue "Windows protected your PC" box, because this free app isn't paid-signed. Click **More info**, then **Run anyway**.
3. **Pick something.** Click **Browse apps**, choose a category, and press **Add** on anything you like. ReleaseSync finds the right download for your PC and can install it for you.

From then on it tells you when your apps have updates. When ReleaseSync itself has a new version, an **Update available** button appears at the bottom left: click it to see what changed and update in one step. You can keep `ReleaseSync.exe` anywhere, such as your Desktop or Documents folder.

It works straight away without a GitHub account. Browsing is limited to 60 requests an hour, though, which runs out quickly. If you browse a lot, a free GitHub token raises that to 5,000: see Settings → GitHub.

Needs Windows 10 (version 1903) or later, on a 64-bit PC.

## Project status

ReleaseSync is young and made by one person with an AI assistant. It's been used on one PC so far, so expect the odd rough edge, and please [report anything that goes wrong](../../issues).

**Working now (Windows)**

| Feature | State |
|---|---|
| Add, install and update apps | Working |
| Discover (trending, popular, new, hand-picked lists) | Working |
| Library, groups, favorites, notes, Try later | Working |
| Scan this PC for apps you already have | Working |
| Rollback and backups | Working |
| Uninstall, including apps in a folder you chose (removes only what it installed) | Working |

**Early, use with care**

| Feature | State |
|---|---|
| Sync between PCs through a private Gist or a shared folder | Built and tested with simulated devices. Not yet tried in everyday use between two real computers. |
| ReleaseSync updating itself | An **Update available** button appears at the bottom left, shows what's new, and updates and reopens the app for you. Tested, but not yet over a real download. If it ever fails, your current version keeps working and you can download the new one by hand. |
| Safety checks | Checksums and signatures are in use. The Windows Security scan and VirusTotal lookup haven't been tried on a real download yet. |

**In progress (not in the download yet)**

| Feature | State |
|---|---|
| Mac app | In development. Shows your library and Discover; installing isn't done. |
| See your other devices on your home network | Design agreed, code just started. Green lights for devices that are on, and what's installed on each. It only ever views: one device can't install or change anything on another. It needs a review by a cryptography expert before release. |

**Planned**

- Android handheld companion, then installing on it
- iPhone companion (view only)
- "Send to my other device": a suggestion the other device can accept or ignore

**Known limits**

- The app isn't code-signed, so Windows shows a warning the first time you open it (see above).
- Only tested on one Windows 11 PC so far.

## What it does

- **Add anything.** Paste a repository link (or `owner/repo`) into the top bar. ReleaseSync finds the latest release, picks the best Windows file for your PC, and can install it right away.
- **Updates.** Checks on a schedule, updates one app or all of them, and can run checks in the background and send Windows notifications.
- **Discover.** Trending, popular, new, updated and hand-picked lists for emulators, game ports and decompilations, utilities, media, developer tools and more, with an option to hide projects that have no Windows download.
- **Your GitHub stars.** Bring in everything you've starred and add the ones you want kept current.
- **Library.** Automatic groups by type plus your own, favorites, notes and tags, and a *Try later* list.
- **Scan this PC.** Finds apps you already have and links them to their projects so they can get updates.
- **Safety checks** (each can be switched off in Settings → Security): checksum and signature verification, a Windows Security scan, build provenance, and an optional VirusTotal lookup.
- **Rollback.** Pick any release, roll back, or skip a version. Apps are backed up before they're changed.
- **Backups.** Scheduled backups to a folder you choose, and automatic local safety copies.

## Privacy

- **No accounts, no telemetry, no analytics.** ReleaseSync talks only to the sites hosting the projects you track, to this project's GitHub page to check for a newer ReleaseSync (you can clear that in Settings), and, if you turn it on, VirusTotal.
- **A GitHub token is optional.** If you add one, it's stored in Windows Credential Manager, never in ReleaseSync's files, backups or exports.
- **Your data stays on your PC.** The library and settings live in `%APPDATA%\ReleaseSync`; downloaded installers go to `%LOCALAPPDATA%\ReleaseSync\Downloads` and are removed after installing; portable apps unpack to `%LOCALAPPDATA%\Programs\ReleaseSync Apps`. Nothing is uploaded unless you turn on sync yourself. Sync shares your list of apps, groups, notes and Try later list (to a private Gist in your own GitHub account, or a folder you choose), and never your token, passphrase or installed files.

## For developers

```
dotnet build ReleaseSync.sln
dotnet test ReleaseSync.Core.Tests
dotnet publish ReleaseSync -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

Requires the .NET 8 SDK on Windows. The code is split in two: `ReleaseSync.Core` is the platform-neutral part (library model, release sources, Discover, backups, the sync engine) and `ReleaseSync` is the Windows app. `ReleaseSync.Core.Tests` simulates several devices syncing through a fake GitHub and a shared folder. Pushing a `v*` tag builds and publishes a release.

Issues and pull requests are welcome. Please don't include tokens, personal paths or personal library data in bug reports. ReleaseSync's Settings → Data → "Open folder" shows what's stored locally, so you can check before sharing anything.

## Contributors

| Who | Role |
|---|---|
| [@huskyCodes](https://github.com/huskyCodes) | Creator and maintainer: the idea, the design and every decision. |
| [Claude](https://www.anthropic.com/claude), an AI assistant made by Anthropic | AI contributor: wrote much of the code, and is credited as a co-author on the commits it contributed to. |

## Disclaimer

- **Independent project.** ReleaseSync is not affiliated with, endorsed by or sponsored by GitHub, GitLab, Codeberg, VirusTotal or Microsoft. Those names are used only to say what it works with, and belong to their owners. No logos are used.
- **It doesn't host or redistribute software.** Every download comes straight from the publisher's own release page. What you install, and whether you have the right to use it, is between you and that software's license and your local law.
- **Safety checks reduce risk but can't remove it.** Checksums, signatures, Windows Security scans and the other checks can miss things. Install only what you trust.
- **Lists aren't endorsements.** Discover's Trending, Popular, New and Updated lists are automatic views of public projects, and the Featured list is a hand-picked set of popular ones. Being listed says nothing about legality, quality or safety.
- **Provided as is**, under the MIT license below, with no warranty.

## License

[MIT](LICENSE)
