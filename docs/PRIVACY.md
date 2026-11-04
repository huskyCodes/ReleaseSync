# Privacy policy

ReleaseSync has no accounts, no telemetry, no analytics and no servers of its own. The project collects nothing about you.

## What the app connects to

| Connects to | When | What is sent | How to turn it off |
|---|---|---|---|
| The sites hosting projects you track or browse (GitHub, GitLab, Codeberg, Forgejo/Gitea sites) | When you browse, search, or check for updates | Which projects it's asking about, and your GitHub token if you added one (to GitHub only) | Turn off update checks in Settings → Updates, and don't use Discover |
| This project's GitHub page | At start-up and on the update schedule, to see if a newer ReleaseSync exists | An ordinary request for the release list | Clear the address in Settings → Updates |
| VirusTotal | Only if you turn it on and add your own key | The fingerprint (hash) of a file you're installing | Off by default; Settings → Security |
| A private Gist in your own GitHub account, or a folder you choose | Only if you turn on sync | Your list of apps, groups, notes, Try later list and device names. Never your token, passphrase or installed files | Off by default; Settings → Data |

These sites see your IP address when you connect to them, and their own privacy policies apply.

## What stays on your PC

Your library and settings are in `%APPDATA%\ReleaseSync`. Your GitHub token, VirusTotal key and sync passphrase are in Windows Credential Manager, never in ReleaseSync's files, backups or exports.

## Removing it

Uninstall ReleaseSync from Windows' Installed apps or from Settings → Data. You can choose to delete your library and settings at the same time. Apps you installed with ReleaseSync stay installed.
