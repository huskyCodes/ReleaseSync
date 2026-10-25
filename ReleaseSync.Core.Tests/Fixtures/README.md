# Sync fixtures

Files written by the C# sync engine (`ReleaseSync.Core/Sync`). Other implementations are tested against them: they must read each file and produce the same results. The format is described in `docs/SYNC-FORMAT.md`.

Everything here is made up: fake owners and repositories, fake devices, a fake passphrase. Never put real tokens, passphrases, e-mail addresses, paths or library data in these files.

## Contents

| Path | What it is |
|---|---|
| `merge/<case>/a.json`, `b.json` | Two library documents. |
| `merge/<case>/expected.json` | `Merge(a, b, Now)`: the result. |
| `merge/<case>/case.json` | What the case shows, and the sync time `Now` to pass to the merge. |
| `sync-round.json` | Whole sync rounds: this device's library, the stored file, the passphrase and time, and what the round reports and writes. |
| `crypto/<case>/` | An encrypted `envelope.json`, the `plaintext.json` inside it, and `case.json` with the passphrase that opens it and passphrases that must not. |
| `crypto/errors.json` | Envelopes that must fail to open, with the kind of error and the message. |
| `crypto/is-encrypted.json` | Texts and whether they count as an encrypted file. |
| `timestamps.json` | How times are written, and how the different spellings of a time are read. |
| `escapes.json` | How strings are escaped in JSON text. The tie-break in the merge depends on this being exact. |
| `releases/` | Release data: version comparison, release-notes cleanup, counts and sizes, project links, and how GitHub's JSON for a project and its releases is read. |
| `discover/` | Discover: how the engine orders projects and shows their movement since an earlier ranking. |
| `categorize.json` | The kind of project (the automatic group) the engine puts each name, description, topics and language in. |

## Rules for comparing

- Compare documents as data, not as text: key order inside objects and line endings do not matter.
- `WrittenBy` in a merge result comes from `a` when both `UpdatedAt` are equal, so a symmetry check must ignore it.
- `GroupStamps` keys keep the spelling of whichever side was merged first (`Games` vs `games`). When checking that `Merge(a,b)` equals `Merge(b,a)`, compare those keys lower-cased. `expected.json` is `Merge(a, b)`.
- Times are exact to 100 ns. Compare them as instants.
- Merging `expected.json` with `a` or `b` again must change nothing, except in `12-tombstone-expiry`: a tombstone older than 90 days is forgotten, so an old copy that still has the item brings it back.

## Known defect in the C# engine (left in place on purpose)

When the C# engine reads a Try later item that has a `Note`, setting the note also sets `ModifiedAt` to the current time, replacing the value in the file. So after a read, an item with a note always looks newly edited, its note wins over other devices' copies in the merge, and the new time is written back. The fixtures leave notes off Try later items so they stay stable, and so this does not decide what the other implementations are expected to do. Apps can have notes; they are not affected.

## Changing or regenerating

The test `Fixture_files_match_what_the_engine_produces` fails when a fixture no longer matches what the engine produces, so a change to the C# engine cannot go unnoticed. If the change is meant, rewrite the files and review the diff:

```
RELEASESYNC_WRITE_FIXTURES=1 dotnet test ReleaseSync.Core.Tests --filter Fixture_files_match
```

A diff in the fixtures is a change to the sync format or its rules. Every other implementation must be updated and tested against the new files, and the devices must keep reading each other's files.