# ReleaseSync

*Formerly called GitSync.*

Track apps from GitHub, GitLab, Codeberg and self-hosted Forgejo/Gitea sites, install their Windows releases, and keep them up to date — from one tidy desktop app.

ReleaseSync is a native Windows (WPF, .NET 8) program. Everything it knows lives on your PC.

## What it does

- **Add anything.** Paste a repository link (or `owner/repo`) into the top bar. ReleaseSync finds the latest release, picks the best Windows file for your PC, and can install it right away.
- **Updates.** Checks on a schedule, updates one app or all of them, and can run checks in the background and send Windows notifications.
- **Discover.** Trending, popular, new, updated and hand-picked lists for emulators, game ports and decompilations, utilities, media, developer tools and more — with ▲/▼ movement since yesterday or last week, and an option to hide projects that have no Windows download.
- **Your GitHub stars.** Bring in everything you've starred and add the ones you want kept current.
- **Library.** Automatic groups by type plus your own, favorites, notes and tags, hide or stop tracking, and a *Try later* list.
- **Scan this PC.** Finds apps you already have and links them to their projects so they can get updates.
- **Safety checks** (each can be switched off in Settings → Security): checksum and signature verification, a Windows Security scan, build provenance, and an optional VirusTotal lookup.
- **Rollback.** Pick any release, roll back, or skip a version. Apps are backed up before they're changed.
- **Backups.** Scheduled backups to a folder you choose, automatic local safety copies, and optional library sync across your PCs through a shared folder.

## Install

Download `ReleaseSync.exe` from the [Releases](../../releases) page and run it. If you used it when it was called GitSync, your library, settings and saved token carry over automatically the first time you open it (the old folders are left alone). It needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

## Build from source

```
cd ReleaseSync
dotnet build
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ..\dist
```

Requires the .NET 8 SDK on Windows 10 (1903) or later.

## Privacy

- **No accounts, no telemetry, no analytics.** ReleaseSync talks only to the sites hosting the projects you track (and, if you turn it on, VirusTotal).
- **Your GitHub token is optional.** It raises GitHub's rate limit from 60 to 5,000 requests an hour. It is stored in Windows Credential Manager — never in ReleaseSync's files, backups or exports.
- **Your data stays local.** The library and settings live in `%APPDATA%\ReleaseSync`; downloaded installers go to `%LOCALAPPDATA%\ReleaseSync\Downloads`; portable apps unpack to `%LOCALAPPDATA%\Programs\ReleaseSync Apps`. Nothing is uploaded anywhere unless you point backups or sync at a cloud-synced folder yourself.

## Contributing

Issues and pull requests are welcome. Please don't include tokens, personal paths or personal library data in bug reports — ReleaseSync's Settings → Data → "Open folder" shows what's stored locally, so you can check before sharing anything.

## Disclaimer

- **Independent project.** ReleaseSync is not affiliated with, endorsed by or sponsored by GitHub, GitLab, Codeberg, VirusTotal or Microsoft. Those names are used only to say what it works with, and belong to their owners. No logos are used.
- **It doesn't host or redistribute software.** Every download comes straight from the publisher's own release page. What you install, and whether you have the right to use it, is between you and that software's license and your local law.
- **Safety checks reduce risk but can't remove it.** Checksums, signatures, Windows Security scans and the other checks can miss things. Install only what you trust.
- **Lists aren't endorsements.** Discover's Trending, Popular, New and Updated lists are automatic views of public projects, and the Featured list is a hand-picked set of popular ones. Being listed says nothing about legality, quality or safety.
- **Provided as is**, under the MIT license below, with no warranty.

## License

[MIT](LICENSE)
