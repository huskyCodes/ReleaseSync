# Files written by the Swift package

These are written by `ReleaseSyncKit` (the Swift code), not by the C# engine, so the C# tests can read them and check that the two implementations understand each other's files.

- `merge/<case>.json`: the merge of `../merge/<case>/a.json` and `b.json` as Swift wrote it. The C# tests read each one and compare it with the engine's own result, and merge it with `a` and `b` again.
- `crypto/envelope.json`: a locked file made by Swift (random salt and nonce) with the fake passphrase from `../crypto/01-plain-ascii/case.json`. `crypto/plaintext.json` is what it holds. The C# tests open it.

Rewrite them only on purpose, from `ReleaseSyncKit`: `RELEASESYNC_WRITE_SWIFT_FIXTURES=1 swift test --filter SwiftWrittenFixtureTests`. The envelope changes every time, because a locked file always gets a fresh salt and nonce.

Everything here is made up.
