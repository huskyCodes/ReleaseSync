# ReleaseSyncKit

The part of ReleaseSync's Mac (and later iPhone) app that has nothing to do with the screen: reading and writing the sync file, merging it, locking it with a passphrase, and keeping it in a private GitHub Gist or a shared folder. No UI, no Keychain, no secrets stored: the app passes the token and passphrase in when it syncs.

It follows [docs/SYNC-FORMAT.md](../docs/SYNC-FORMAT.md). The C# engine in `ReleaseSync.Core/Sync` is the reference, and the tests read the files the C# engine wrote ([ReleaseSync.Core.Tests/Fixtures](../ReleaseSync.Core.Tests/Fixtures)), so the two cannot drift apart without a test failing.

```
swift test
```

| File | What it is |
|---|---|
| `Timestamp.swift` | A time exact to 100 ns (a `Date` can't hold that, and the merge compares times exactly). |
| `JSON.swift`, `Text.swift` | A small JSON reader and writer that produce the engine's exact text (property order, `\uXXXX` escapes), and character-code string ordering. The merge's tie-break compares that text. |
| `Models.swift` | `SyncDoc`, `SyncApp`, `TryLaterItem`, `SyncDevice`. |
| `SyncMerger.swift` | The merge. |
| `SyncCrypto.swift` | The passphrase lock (PBKDF2-SHA256 and AES-256-GCM). |
| `SyncService.swift` | One sync round. |
| `SyncTransport.swift`, `GistTransport.swift` | Memory, folder and Gist storage. The web requests go through an injectable `HTTPSend`, so tests never touch the network. |

## Differences from the C# engine

These are small and deliberate. None changes what the file looks like or what a person sees.

- The GitHub token is sent only to `api.github.com`. The C# client also sends it when it downloads a large file from the gist's raw address.
- Times without a zone, or with an offset, are read as the instant they mean and always written back as UTC with a `Z`.
- Names used as keys (apps, tombstones, groups) are compared the way Swift compares strings, where `é` typed as one character equals `e` plus an accent. The C# engine compares them character code by character code. For the plain ASCII names repositories have, there is no difference.
- Lower- and upper-casing maps one character to one character, like .NET's invariant culture; a character that would grow is left alone.
- Unknown properties in the file are dropped, like the C# engine does. Adding a field to the format means teaching both engines first (see docs/SYNC-FORMAT.md).
- The C# engine resets `ModifiedAt` of a Try later item to the clock when it reads one that has a note (see the format doc). Swift keeps the time the file says, which is what the format intends.
