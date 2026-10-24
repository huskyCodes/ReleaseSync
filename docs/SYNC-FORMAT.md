# ReleaseSync sync format

This describes the file that keeps a ReleaseSync library the same on all of a person's devices, and the rules for combining two copies of it. It is written from the C# reference implementation in `ReleaseSync.Core/Sync/` (`SyncDoc.cs`, `SyncMerger.cs`, `SyncService.cs`, `SyncCrypto.cs`, `SyncTransport.cs`) and from `Store.Options` in `ReleaseSync.Core/Services.cs`. Where this document and the code disagree, the code is right and this document has a bug.

Every device must keep reading and writing this format. **Do not change it without a plan for every platform.** See [Compatibility](#9-compatibility-and-things-that-can-bite) before adding anything.

Contents: [1 Overview](#1-overview) · [2 JSON conventions](#2-json-conventions) · [3 Document](#3-the-document) · [4 Keys and tombstones](#4-keys-and-tombstones) · [5 Timestamps](#5-timestamps) · [6 Merge](#6-merge) · [7 A sync round](#7-a-sync-round) · [8 Encryption](#8-encryption) · [9 Compatibility](#9-compatibility-and-things-that-can-bite) · [10 Transports](#10-transports) · [11 Test data](#11-test-data)

---

## 1. Overview

One file holds the whole library: tracked apps, the Try later list, custom groups, deletion records ("tombstones") and the list of devices. The file lives in a private GitHub Gist, or in a shared folder that something else (OneDrive, Dropbox, Syncthing…) copies between devices. It can be locked with a passphrase.

A device syncs by: **read** the file, **merge** it with its own library, **write** it back if anything changed. There is no server logic. Two devices can sync in any order, any number of times, and end up agreeing, because the merge is symmetric and repeatable (see [6](#6-merge)).

The file never contains the GitHub token, the passphrase, the VirusTotal key, or any installed files.

## 2. JSON conventions

The document is serialized with `Store.Options`:

| Setting | Effect on the file |
|---|---|
| no naming policy | Property names are **PascalCase**, exactly as in the tables below (`UpdatedAt`, not `updatedAt`). |
| `PropertyNameCaseInsensitive = true` | When **reading**, property names match in any case. Always **write** PascalCase. |
| `WriteIndented = true` | Pretty-printed, 2 spaces per level. The line ending is the writer's OS default (`\r\n` from Windows, `\n` from Mac/Linux). Readers must accept both. |
| `DefaultIgnoreCondition = WhenWritingNull` | A property whose value is `null` is **left out**, not written as `null`. `false`, `0` and empty lists/objects are written. |
| `JsonStringEnumConverter` | Enums are written as their **name** (`"Portable"`). When reading, a name (any case) or a number is accepted. |
| default string encoder | Non-ASCII characters and `< > & ' +` (and a few others) are written as `\uXXXX` escapes with **uppercase** hex. Any valid JSON string escape is accepted when reading. |
| unknown properties | **Ignored on read, and therefore lost when this implementation rewrites the file** (see [9](#9-compatibility-and-things-that-can-bite)). |

Example of the escaping: the text `café <b>` is written `"café <b>"`.

## 3. The document

`SyncDoc` (top level object):

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Version` | integer | `3` | Format version. A reader that sees `Version` greater than `3` must stop and tell the user to update. Versions `1` and `2` files are still read. (What changed in each version is in [9](#9-compatibility-and-things-that-can-bite).) |
| `UpdatedAt` | timestamp | now | When the file was last written (see [5](#5-timestamps)). |
| `WrittenBy` | string | machine name | Display name of the device that last wrote it. Informational. |
| `Apps` | array of `SyncApp` | `[]` | Tracked apps. |
| `TryLater` | array of `TryLaterItem` | `[]` | The Try later list. |
| `CustomGroups` | array of string | `[]` | Names of groups the user made. |
| `GroupStamps` | object: group name → timestamp | `{}` | When each custom group was created or last re-created. |
| `Tombstones` | object: key → timestamp | `{}` | What was deleted and when (see [4](#4-keys-and-tombstones)). |
| `Devices` | object: device id → `SyncDevice` | `{}` | Devices that take part. |

Properties are written in the order shown.

### `SyncApp` (one entry of `Apps`)

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Key` | string | `""` | Identity: `source:owner/repo`, **lower-case** (see [4](#4-keys-and-tombstones)). Entries with an empty `Key` are dropped by the merge. |
| `Source` | string | `"github"` | `"github"`, `"gitlab"`, `"codeberg"`, or `"forgejo:host"`. |
| `Owner` | string | `""` | Repository owner, original capitalization. |
| `Repo` | string | `""` | Repository name, original capitalization. |
| `Group` | string, optional | omitted | Name of the group the app is in. Omitted means none/automatic. |
| `Pinned` | boolean | `false` | Favorite. |
| `Hidden` | boolean | `false` | Hidden from lists. |
| `IncludePrereleases` | boolean | `false` | |
| `IgnoreUpdates` | boolean | `false` | |
| `AutoUpdate` | boolean | `false` | |
| `Mode` | enum `InstallMode` | `"Auto"` | `"Auto"`, `"Installer"` or `"Portable"`. |
| `CustomArgs` | string, optional | omitted | Extra installer arguments. |
| `DisplayName` | string, optional | omitted | |
| `Note` | string, optional | omitted | |
| `Tags` | string, optional | omitted | Comma or semicolon separated. |
| `SkippedVersion` | string, optional | omitted | |
| `AssetPattern` | string, optional | omitted | Which release file to prefer. |
| `TrustUnsigned` | boolean | `false` | |
| `AddedAt` | timestamp | — | When the app was added. |
| `ModifiedAt` | timestamp | — | When any of the above last changed. The merge uses this. |

Only these fields are synced. Everything else the Windows app knows about an app (installed version, release lists, install paths…) stays on that device. In particular **there is no `Platform` on `SyncApp` today** (the Windows app's `TrackedApp` has one, but it is not written to the file).

### `TryLaterItem` (one entry of `TryLater`)

The whole item is written as-is, so the file carries a small snapshot of the repository.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Owner` | string | `""` | |
| `Repo` | string | `""` | |
| `Source` | string | `"github"` | As in `SyncApp`. |
| `Platform` | string | `"Windows"` | Which platform the item is for. |
| `Description` | string, optional | omitted | |
| `Language` | string, optional | omitted | |
| `Stars` | integer | `0` | |
| `AvatarUrl` | string, optional | omitted | |
| `Topics` | array of string, optional | omitted | |
| `SavedAt` | timestamp | now | When it was saved. |
| `ModifiedAt` | timestamp, optional | omitted | When the note last changed. |
| `Note` | string, optional | omitted | |

The item's identity key is **not stored**. It is computed: `source:owner/repo`, lower-cased (`ToLowerInvariant`). `SavedAt`/`ModifiedAt` give the item's *stamp*: the later of the two, or `SavedAt` if `ModifiedAt` is missing or not later.

### `SyncDevice` (value in `Devices`, keyed by `Id`)

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Id` | string | `""` | Stable id for the device (the Windows app uses a GUID in 32 lower-case hex digits). Entries with an empty `Id` are dropped. |
| `Name` | string | `""` | Display name. |
| `Platform` | string | `"Windows"` | `"Windows"`, `"Mac"`, `"Android"`, `"iPhone"`… free text, shown to the user. |
| `AppVersion` | string | `""` | |
| `LastSeen` | timestamp | — | When that device last synced. |
| `Online` | boolean, optional | omitted | Whether the device says it is running ReleaseSync right now. Omitted by devices that predate version 3. See [Presence](#presence-and-installed-programs). |
| `Installed` | array of `SyncInstall`, optional | omitted | What the device has installed, as it knows it (see below). Omitted by devices that predate version 3 or that don't report it. |

### `SyncInstall` (entry of a device's `Installed`)

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Key` | string | `""` | The identity key of the tracked app, as in [4](#4-keys-and-tombstones) (`github:owner/repo`). Entries with an empty `Key` are ignored. |
| `Version` | string | `""` | The version installed on that device. |
| `InstalledAt` | timestamp, optional | omitted | When it was installed there. |
| `Latest` | string, optional | omitted | The newest version that device has seen for the app. Together with `Version` this says whether an update is waiting there. |

This is a report by the device about itself, so only that device writes its own entry. Other devices keep it as it is and show it; they never edit it. It carries no paths, file names, user names or anything else about the machine.

### Presence and installed programs

There is no direct link between devices: they meet in the shared file. So "connected" can only mean "said recently that it is running":

- A device writes `Online = true` when ReleaseSync starts and keeps `LastSeen` fresh while it runs (the [heartbeat](#7-a-sync-round), every 2 hours when nothing else changed). On a normal exit it writes `Online = false`.
- A reader shows a device as **online** when `Online` is `true`, **and** `LastSeen` is no more than **150 minutes** before the reader's current time, **and** no more than **10 minutes** after it (clocks differ a little). Anything else is shown as offline. So a device that stopped without saying so (a crash, a closed laptop lid) turns grey by itself within 150 minutes.
- A reader uses a device's `Installed` list like this: ignore entries with an empty `Key`; if a `Key` appears more than once the last entry wins; order by `Key` (ordinal); use at most the first **1000** entries.
- An entry has an **update waiting** when `Latest` and `Version` are both non-empty and `Latest` is a newer version than `Version` (the version comparison used for release data).

The fixtures in `presence.json` cover these rules.

### Small example (fake data)

```json
{
  "Version": 3,
  "UpdatedAt": "2026-01-01T12:00:00Z",
  "WrittenBy": "Example Mac",
  "Apps": [
    {
      "Key": "github:example/sample-app",
      "Source": "github",
      "Owner": "example",
      "Repo": "sample-app",
      "Pinned": true,
      "Hidden": false,
      "IncludePrereleases": false,
      "IgnoreUpdates": false,
      "AutoUpdate": false,
      "Mode": "Auto",
      "TrustUnsigned": false,
      "AddedAt": "2026-01-01T10:00:00Z",
      "ModifiedAt": "2026-01-01T11:00:00Z"
    }
  ],
  "TryLater": [],
  "CustomGroups": [],
  "GroupStamps": {},
  "Tombstones": {},
  "Devices": {
    "0123456789abcdef0123456789abcdef": {
      "Id": "0123456789abcdef0123456789abcdef",
      "Name": "Example Mac",
      "Platform": "Mac",
      "AppVersion": "0.1",
      "LastSeen": "2026-01-01T12:00:00Z",
      "Online": true,
      "Installed": [
        {
          "Key": "github:example/sample-app",
          "Version": "1.0",
          "InstalledAt": "2026-01-01T10:30:00Z",
          "Latest": "1.1"
        }
      ]
    }
  }
}
```

## 4. Keys and tombstones

A **tombstone** records a deletion so that a device that still has the item does not bring it back. `Tombstones` maps a key to the time of the deletion. Keys are exact strings (compared case-sensitively), built like this:

| What was deleted | Tombstone key | Item identity it refers to |
|---|---|---|
| a tracked app | the app's `Key`, e.g. `github:example/sample-app` | `SyncApp.Key` |
| a Try later item | `later:` + the item's key, e.g. `later:github:example/sample-app` | `source:owner/repo`, lower-case |
| a custom group | `group:` + the name **trimmed and lower-cased** (`ToLowerInvariant`), e.g. `group:games` | the group name, compared ignoring case |

An app and a Try later item with the same repository are separate things with separate tombstones.

The Windows app also records an app tombstone when a repository entry is replaced by a different one (a renamed or moved repo), so the old key does not return.

A device that creates or re-creates a group sets `GroupStamps[name]` to now and removes the `group:` tombstone. A device that deletes a group removes its `GroupStamps` entry, adds the `group:` tombstone, and clears `Group` on the apps that used it (the test devices also bump those apps' `ModifiedAt`, which is what lets the change win the merge).

Tombstones are forgotten after **90 days** (see [6](#6-merge)).

## 5. Timestamps

- Written as ISO 8601 in UTC with a trailing `Z`: `2026-01-01T12:00:00Z`. Fractional seconds appear only when needed, up to **7 digits** (100 nanosecond "ticks"), with trailing zeros removed: `…12:00:00.1234567Z`, `…12:00:00.0000005Z`.
- On read, a value with a `Z` is UTC, a value with an offset (`+02:00`) is converted to that instant, and a value with **no** suffix is taken as-is (treated like UTC for comparisons). New writers should always write UTC with `Z`.
- **Comparisons are exact to the tick.** The merge decides ties by testing equality, so an implementation must not round: a timestamp from the file, parsed and compared, must equal the same timestamp parsed again. A floating-point seconds value (Swift `Date`) cannot hold 100 ns exactly at today's dates, so the Swift side should keep timestamps as an integer tick count (or seconds plus ticks) and convert to `Date` only for display. It must also **write back the same text** it read for items it did not change, so that unchanged items stay byte-identical across devices.

## 6. Merge

`Merge(a, b, now)` combines two documents into one. It is **symmetric** (`Merge(a,b)` and `Merge(b,a)` agree on every field that matters, see the two exceptions below) and **repeatable** (merging the result with either input again changes nothing). `now` is the UTC time of the sync. In what follows `Newer(x, y, stamp)` means: the one with the later stamp; if the stamps are exactly equal, the one whose JSON text is **ordinally greater** (see [9](#9-compatibility-and-things-that-can-bite) for what "JSON text" means).

The result starts as: `Version = max(a.Version, b.Version)`, `UpdatedAt = now`, `WrittenBy` = the writer of whichever of `a`/`b` has the later `UpdatedAt` (`a` if equal; the sync service overwrites it anyway, see [7](#7-a-sync-round)).

**Step 1: tombstones.** Union of both; for a key in both, the later time wins. (Expiry is applied at the end.)

**Step 2: apps.**
1. Collect all apps from `a` then `b`, skipping empty keys. For the same `Key` keep `Newer(x, y, ModifiedAt)`.
2. For each remaining app that has a tombstone `del` under its `Key`:
   - if `del >= ModifiedAt` **and** `del >= AddedAt`: delete the app. The tombstone stays.
   - otherwise (the app was edited or re-added after the deletion): keep the app and **drop the tombstone**.

**Step 3: Try later.**
1. Collect items from `a` then `b`. For the same item key keep `Newer(x, y, stamp)` where stamp is the item's stamp (see [3](#3-the-document)).
2. For each remaining item with a tombstone `del` under `later:<key>`: if `del >= stamp` delete the item (tombstone stays); otherwise keep the item and drop the tombstone.

**Step 4: groups.**
1. `GroupStamps`: union, names compared **ignoring case**; for a name in both, the later time wins.
2. `CustomGroups`: union, names compared ignoring case, ignoring empty or whitespace-only names. When both sides spell a name differently (`Games` / `games`), the **ordinally greater** spelling is kept.
3. For each group with a tombstone `del` under `group:<name trimmed, lower-cased>`: if the group has no stamp, or `del >= stamp`: delete the group and its stamp (tombstone stays); otherwise keep the group and drop the tombstone.

Note that deleting a group here does not touch apps; the deleting device already cleared `Group` on its apps, and those edits arrive as newer app versions.

**Step 5: devices.** Union by `Id`, skipping empty ids; for the same `Id` keep `Newer(x, y, LastSeen)`. The whole entry of the winner is kept as it is, including `Online` and `Installed`: nothing is combined inside a device entry. (At an equal `LastSeen` the tie-break compares the entry's JSON text, so these fields take part.) Devices are never expired or deleted.

**Step 6: finish.**
- Keep only tombstones with `time >= now - 90 days` (a tombstone exactly 90 days old is kept).
- `Apps` sorted by `Key`, ordinal (UTF-16 code unit order).
- `TryLater` sorted by item key, ordinal.
- `CustomGroups` sorted by name, ordinal ignoring case.
- `GroupStamps`, `Tombstones` and `Devices` are objects; their order in the file carries no meaning.

**Repeatable, with one limit.** A tombstone is forgotten after 90 days. A device that was offline longer and still holds a deleted item brings it back when it next syncs, because nothing remembers the deletion any more.

**Two known asymmetries** (neither changes what the user sees; both are in the C# reference):
1. `GroupStamps` keys keep the spelling from whichever input was merged first. `Merge(a,b)` may write `"Games"` where `Merge(b,a)` writes `"games"`. The values are the same. `Signature` (below) lower-cases these keys, so devices do not treat it as a change. A symmetry test should compare `GroupStamps` with lower-cased keys.
2. `WrittenBy` is taken from `a` when both `UpdatedAt` are equal. The sync service overwrites it afterwards, so it never reaches the file from a merge.

### Signature

`Signature(doc)` is an in-memory fingerprint used only to decide "did anything change?". It is **never stored or sent**, so another implementation only has to behave the same way: two documents have the same signature exactly when they are equal after this normalization:

- ignore `UpdatedAt`, `WrittenBy`, and each device's `LastSeen`;
- `Apps` and `TryLater` as sets (order ignored), compared field by field;
- `CustomGroups` as a set, ignoring case;
- `GroupStamps` with names lower-cased, times in UTC;
- `Tombstones` as given, times in UTC;
- `Devices`: for each id, only `Id`, `Name`, `Platform`, `AppVersion`, `Online`, and `Installed` with its entries ordered by `Key` (ordinal). So a change to what a device reports counts as a change and is written, but a plain refresh of `LastSeen` does not.

### Known defect: notes on Try later items

When the C# engine reads a `TryLaterItem` that has a `Note`, the `Note` setter also sets `ModifiedAt` to the current time, replacing the value in the file (the properties are read in file order, `ModifiedAt` first, then `Note`). In the C# engine an item with a note therefore always looks freshly edited after it is read, wins over other devices' copies in the merge, and writes the new time back. The format as described above (stamp = later of `SavedAt` and `ModifiedAt`) is what other implementations should follow; the golden fixtures avoid notes on Try later items so they do not depend on this. It does not affect `SyncApp.Note`.

## 7. A sync round

`SyncService.RunAsync(transport, local, passphrase, self, now)`:

1. **Read** the file text from the transport. `null` or blank means "nothing stored yet".
2. If there is text: if it [looks encrypted](#8-encryption), decrypt it with the passphrase (a missing or wrong passphrase stops the round with a "locked" message). Parse the JSON as a `SyncDoc`. If it cannot be parsed, stop with "The sync file isn't readable, so nothing was changed." (nothing is written). If `Version > 3`, stop with "made by a newer version".
3. Set `self.LastSeen = now` and put `self` into `local.Devices[self.Id]`.
4. `merged = Merge(local, remote ?? empty, now)`, then `merged.WrittenBy = self.Name`.
5. `changed` = a remote file existed **and** `Signature(merged) != Signature(local)`. When true the app applies `merged` to its library: it adds, updates and removes things so the library matches. It only removes items that were part of what it sent, so edits made during the sync survive.
6. `push` (write the file) when any of:
   - there was no remote file;
   - `Signature(merged) != Signature(remote)`;
   - the remote file's encryption state differs from what this device wants (locked vs not locked);
   - this device is not in `remote.Devices`, or its `LastSeen` there is more than **2 hours** old (the "heartbeat"; it keeps the device's [online light](#presence-and-installed-programs) green).
7. If pushing, serialize `merged` and write it, encrypted if a passphrase is set (an empty string counts as none; whitespace is a passphrase, it is not trimmed).

Reading and writing are not atomic. If two devices sync at the same moment, one write can overwrite the other. Nothing is lost for good: the device that lost still has its own changes, and its next sync merges and writes them again.

## 8. Encryption

Optional. The file is replaced by an **envelope**: a JSON object, not the document.

```json
{
  "releasesync-enc": 1,
  "Kdf": "pbkdf2-sha256",
  "Iter": 200000,
  "Salt": "<base64, 16 bytes>",
  "Nonce": "<base64, 12 bytes>",
  "Tag": "<base64, 16 bytes>",
  "Data": "<base64 ciphertext>"
}
```

- `releasesync-enc` is the envelope version (`1`). The property name is also the marker: a file is treated as encrypted when, ignoring leading whitespace, it starts with `{` and contains the text `"releasesync-enc"`.
- **Key:** PBKDF2 with HMAC-SHA-256, password = the passphrase as **UTF-8 bytes** (no Unicode normalization, no trimming), salt = `Salt`, iterations = `Iter`, output 32 bytes. New files use `Iter = 200000`.
- **Cipher:** AES-256-GCM, 12-byte nonce, 16-byte tag, **associated data = the ASCII bytes of `releasesync-enc`**.
- **Plaintext:** the UTF-8 bytes of the JSON document text exactly as in [2](#2-json-conventions).
- `Salt`, `Nonce`, `Tag` and `Data` are standard Base64 (with padding). The tag is stored **separately**, not appended to `Data`.
- A fresh random salt and nonce are made every time the file is written.
- On read: the property names of the envelope are matched **case-sensitively** (unlike the document). `releasesync-enc` must be `1` and `Iter` must be between `1000` and `10,000,000`, otherwise the reader reports "made by a newer version". `Kdf` is not checked. An authentication failure means "That passphrase doesn't open the sync file." Bad Base64 or JSON means "The sync file is damaged."
- The envelope is written with the default JSON writer (indented, default escaping, so `+` in Base64 appears as `+`). Readers must accept both forms.

Sizes and wording of the user messages are in `SyncCrypto.cs`; reuse them so the devices say the same things.

## 9. Compatibility and things that can bite

**Adding fields.** Reading ignores unknown properties, so a newer writer's extra fields do not break an older reader. But an older **C# device drops fields it doesn't know when it merges and rewrites the file**, so an added field survives only while no device that doesn't know it syncs. Therefore:
- Do not rely on a new field for correctness until every platform's engine keeps it. Teach the Windows engine the field first (add it to `SyncApp` / `TryLaterItem` / `SyncDevice` and let the merge carry it), then the others.
- Keep new fields optional with a default that means "same as before".
- Do not rename fields, change types, change the key rules, or change the timestamp format. A change that older readers cannot handle needs `Version` raised, which makes older devices refuse to sync (that is deliberate and should be a rare decision).

**Versions.**
- `1`: the first format.
- `2`: current until version 3 (the same fields as 3 without `Online` and `Installed`; readers of 2 refuse a file marked 3).
- `3`: devices report `Online` and `Installed` ([Presence](#presence-and-installed-programs)); the heartbeat went from 6 hours to 2. Older apps refuse a version 3 file, so **update every device before any device writes version 3**; the shared file moves to version 3 the first time an updated device writes it, and older apps then stop with "made by a newer version". `Merge` keeps the higher `Version`, so the file does not go back. Reading a version 2 file is fine, and the result is version 3.

**Platform-aware library.** `SyncApp` has no `Platform`; `TryLaterItem` has one (default `"Windows"`). The library itself is the same on every device: one entry per repository. What differs per device (which build is installed, which version, whether an update is waiting) is not stored on the app but in each device's `Installed` list, so the same repository can be installed on a PC and a Mac at different versions without a `Platform` on `SyncApp`.

**Exactness needed for the tie-break.** When two versions of an item have exactly equal stamps and different content, the winner is the one whose JSON text compares ordinally greater. "JSON text" is that single item (an app, Try later item or device) serialized with the rules in [2](#2-json-conventions): PascalCase names in the order of the tables above, nulls omitted, enums as names, `\uXXXX` escapes with uppercase hex, 7-digit-trimmed timestamps, indented with two spaces. "Ordinal" compares UTF-16 code units. Another implementation must reproduce this text exactly, or two devices could pick different winners for a tie. (The line ending does not change the outcome.) Equal stamps with different content are rare but possible, for example after a bulk import.

**Case.** Keys and tombstone keys are lower-cased with the invariant culture. For the ASCII names that repositories use, that equals a plain lower-case; other implementations must not use locale-dependent lower-casing.

**Time.** All devices compare stamps from their own clocks, so a device with a wrong clock can win edits. The format does not fix that.

## 10. Transports

A transport stores and returns one block of text. `Read` returns `null` when nothing is stored yet.

### Private GitHub Gist

- File name inside the gist: **`releasesync-library.json`**. Gist description: **`ReleaseSync library (synced between your devices)`**. The gist is created **secret** (`"public": false`).
- Base address `https://api.github.com/`. Headers: `User-Agent: ReleaseSync/1`, `Accept: application/vnd.github+json`, `Authorization: Bearer <token>`. Timeout 30 seconds. The token needs the `gist` permission (a classic token with the gist box ticked).
- **Find** (when the gist id is not known yet): `GET gists?per_page=100&page=N` for `N = 1…5`. Stop at the first gist whose `files` contain the file name; stop with "not found" on an empty page or a page with fewer than 100 entries. The description is not checked.
- **Read:** `GET gists/{id}`. If the file object is missing → nothing stored. If `truncated` is `true` and a `raw_url` is given, the full text is fetched from `raw_url`; otherwise the text is the file's `content`. (The C# client sends its `Authorization` header on the `raw_url` request too, because it reuses the same HTTP client. A new implementation should not: the token belongs only on `api.github.com`, and a secret gist's raw address works without it.)
- **Write:** if there is no id, `POST gists` with `{ "description": …, "public": false, "files": { "<name>": { "content": <text> } } }` and remember the returned `id`; otherwise `PATCH gists/{id}` with `{ "files": { "<name>": { "content": <text> } } }`. No version check is sent (last write wins, see [7](#7-a-sync-round)).
- **Remember the gist id** after the first sync so later syncs skip the search.
- **Errors:** network failure → "Couldn't reach GitHub. Check your internet connection."; timeout → "GitHub took too long to answer."; HTTP 401 → "GitHub didn't accept your token. Add a fresh one in Settings."; HTTP 403 with `X-RateLimit-Remaining: 0` → "GitHub's hourly request limit is used up. Sync will work again when it resets."; other 403 or 404 → "GitHub wouldn't open the sync gist. Your token needs the “gist” permission (a classic token with the gist box ticked), or the gist was deleted."; anything else → "GitHub answered {code}."

### Shared folder

- The file is `<chosen folder>/ReleaseSync/library.json`.
- Read: the file's text (UTF-8), or nothing if the file does not exist.
- Write: create the folder if needed, write `library.json.tmp` next to it, then replace `library.json` with it. UTF-8, no byte-order mark.

## 11. Test data

The reference implementation's tests are in `ReleaseSync.Core.Tests` (`SyncMergerPropertyTests.cs` for symmetry and repeatability, `SyncScenarioTests.cs` for several simulated devices, `GistAndCryptoTests.cs` for the Gist transport and encryption). Other implementations should be tested against fixture files produced by the C# engine, with obviously fake data and a fake passphrase.
