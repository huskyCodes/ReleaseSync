# Local-network protocol fixtures

Files written by the C# core (`ReleaseSync.Core/Lan`) for the protocol in `docs/LAN-PRESENCE.md`. Other implementations are tested against them. Everything is made up: fake names, fake keys, fake secrets. Never put anything real in these files.

| File | Section | What it is |
|---|---|---|
| `messages.json` | 8, 11 | Every message as it is written, and how odd, oversized and unknown input is read. |

Bytes are written as lower-case hex unless a file says Base64. JSON values in the files are pretty-printed for reading; the texts that are compared byte for byte are inside strings.

## Reading rules these fixtures settle

The spec says what to ignore; these cases also fix what it leaves open:

- A property that is missing reads as `0`, `false`, an empty list or an empty text.
- A `Type` is matched exactly (case-sensitive). Property names match in any case.
- An object with no `Type`, JSON that does not parse, anything after the object, a value of the wrong kind, or a number that does not fit in 32 bits is an error. A `Type` this version does not know is ignored.
- The installed list: skip entries whose key is empty or over 200 bytes or whose `Version` or `Latest` is over 64 bytes; if a key appears more than once the last entry wins; sort by key (ordinal); keep the first 1,000.
- Free text is cut to its limit in UTF-8 bytes, between characters, with nothing added.