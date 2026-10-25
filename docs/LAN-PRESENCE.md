# ReleaseSync device presence on the local network

**Status: DRAFT for review. Nothing here is implemented yet.** Both the Windows side (C#, `ReleaseSync.Core`) and the Mac side (Swift, `ReleaseSyncKit`) follow this one document. Later: Android, then iPhone.

This replaces the file-based presence that was added in sync format 3 (`Online`, `Installed`, `SyncPresence`). See [Open questions](#14-open-questions) for what that removal means for each side.

Contents: [1 Goal](#1-goal-and-non-goals) · [2 Threats](#2-who-we-defend-against) · [3 Constants](#3-constants) · [4 Discovery](#4-discovery) · [5 Pairing](#5-pairing) · [6 Link handshake](#6-link-handshake) · [7 Records](#7-records) · [8 Messages](#8-messages) · [9 Liveness](#9-liveness-and-reconnecting) · [10 Forgetting](#10-forgetting-a-device) · [11 Limits](#11-limits) · [12 Consent and UI](#12-consent-network-behaviour-and-wording) · [13 Fixtures and structure](#13-fixtures-and-code-structure) · [14 Open questions](#14-open-questions)

---

## 1. Goal and non-goals

**Goal.** A device shows another of the user's devices as **online (green)** while a direct, authenticated, encrypted connection between them is alive, and grey within about 5 seconds of that connection dying. Over the connection a device says who it is (name, platform), that ReleaseSync is open, and what it has installed (key, version, install time, newest version it knows of).

**What it is not.**
- Not the library sync. The library still syncs through the private Gist or the shared folder, unchanged (`docs/SYNC-FORMAT.md`).
- Not internet presence. Away from the shared network, lights are grey. That is accepted.
- No accounts, no tokens, no GitHub requests, no telemetry, no servers of ours.

**Out of scope, but not made impossible.** Connecting across the internet or through VPN/overlay networks. "Connect to a known host and port" is a separate, replaceable step after discovery (section 13), so a later version can add a manually entered address.

**Only paired devices recognise each other.** A stranger on the same Wi-Fi learns nothing useful (no device names, no app lists, no stable identifier) and cannot pretend to be one of the user's devices.

**Off by default.** Nothing listens, announces or browses until the user turns on "Find my devices on this network".

---

## 2. Who we defend against

| Attacker | What they can do | What we promise |
|---|---|---|
| Someone on the same Wi-Fi, passive | See traffic | They see that something called ReleaseSync is on the network, an IP address, a port, and the size and timing of encrypted records. They do not see names, platforms, app lists or any stable device identifier. |
| Someone on the same Wi-Fi, active | Connect to our port, replay captured bytes, sit between two devices | They cannot get a reply that identifies anyone, cannot make a device accept them as paired, and cannot read or change what two paired devices say. Pairing is the one moment they could interfere, which section 5 is built around. |
| A device the user paired and then forgot | Keep trying | Forgetting deletes the shared secret. Without it the device is a stranger. |
| Malware on a paired device | Anything that device can do | Out of scope. |

**Known leaks we accept.** The operating system publishes its own network host name (often containing the person's name); ReleaseSync cannot hide that. Record sizes hint at how many apps a device has installed (no padding in version 1).

---

## 3. Constants

| Name | Value |
|---|---|
| Protocol version (`Proto`) | `1` |
| Service type (normal) | `_releasesync._tcp` |
| Service type (pairing) | `_releasesync-pair._tcp` |
| Handshake protocol name | `Noise_NNpsk0_P256_AESGCM_SHA256` |
| Handshake prologue | ASCII `releasesync-lan/1` |
| Ping interval | 2 s of silence in sending |
| Dead after | 5 s of nothing received |
| Handshake timeout | 5 s |
| Redial backoff | 1, 2, 4, 8, 16, 30 s (stays at 30), reset after a success |
| State message rate | at most one per 2 s (changes are merged) |
| Pairing window | 120 s |
| Wrong code attempts | 3, then pairing mode ends |
| Replay memory | 5 min |
| Failed-handshake limit | more than 10 from one address in 60 s: ignore that address for 5 min |
| Max paired devices | 16 |
| Max simultaneous connections | 16 |

---

## 4. Discovery

DNS-SD (Bonjour) on the local network. Windows uses the system DNS-SD API, the Mac uses the Network framework (`NWListener` / `NWBrowser`).

**Normal service** `_releasesync._tcp`:
- Instance name: `rs-` + 8 random lower-case hex digits, chosen fresh each time the feature starts. It identifies nothing.
- Port: whatever the system gives (ephemeral).
- TXT records: exactly one, `v=1`. Nothing else. No names, no ids, no hints about who is paired.

**Pairing service** `_releasesync-pair._tcp`, advertised only while the user has an "Add a device" window open (at most 120 s, one session at a time):
- Instance name: random, as above.
- TXT: `v=1` and `n=<this device's display name>`. The user started this on purpose and needs to pick the right device, so the name is shown here, and only here.

**Who dials whom.** A device browses for `_releasesync._tcp` and dials every instance it has not already identified (not itself, not an address it already has a live link with). It does not know which pair an instance belongs to, so it tries its paired secrets one at a time (the most recently successful first). When a try fails, it remembers "not mine" for that instance for 10 minutes. Both sides may dial; see the duplicate rule in section 9.

**Interfaces.** Wi-Fi and wired Ethernet only. Not cellular, not peer-to-peer/AirDrop-style links.

**What a device does not do.** It never announces or browses while the feature is off. It never sends anything to an address that did not come from DNS-SD (or, later, from a manually entered address).

---

## 5. Pairing

Pairing happens once per pair of devices. It creates a 32-byte shared secret (`psk`) that both store in their platform secret store. There are two roles: the **Host** (the device the user chose "Add a device" on; it shows a code) and the **Joiner** (the device that finds the Host and takes the code in).

### 5.1 What it is, and why it is not a PAKE

The code is a **6-digit short authentication string (SAS)**, the same idea as Bluetooth "numeric comparison" and ZRTP's SAS. Both devices do an ephemeral P-256 key exchange, derive the same 6 digits from everything they exchanged, and the user types the Host's 6 digits into the Joiner. If someone sat in the middle, the two devices would have different digits, so pairing fails.

I (the Mac side) propose this over a PAKE such as SPAKE2/CPace because **neither platform ships a PAKE**: not CryptoKit, not .NET. SPAKE2 needs elliptic-curve point addition and CPace needs hash-to-curve; the platform ECDH APIs expose neither (ECDH returns only the x-coordinate). Implementing curve arithmetic ourselves in two languages is exactly the "home-made" cryptography we want to avoid, and adding a third-party library is a supply-chain risk for a public repo. The SAS approach uses only primitives both platforms have (P-256 ECDH, SHA-256, HMAC, HKDF) and is a well-understood pattern. The price: the code is checked against a value derived from the session rather than typed into a PAKE. **See open question 5**; if you would rather take a vetted PAKE library, the rest of this document does not change.

### 5.2 Messages

Pairing runs over a TCP connection from the Joiner to the Host's pairing service. Each message is a 2-byte big-endian length followed by that many bytes of compact UTF-8 JSON (conventions in [section 8.1](#81-json-conventions)); binary values are standard Base64. Maximum message size 1,024 bytes.

```
Joiner                                                    Host
  generate ephemeral P-256 key (PI), random NI (16 bytes)
  P1 {Type:"pair1", Proto:1, Name, Platform,
      Commit: SHA256("rs-pair-commit/1" || PI || NI)}  ------>
                                                              generate (PD), random ND (16 bytes)
                       <------  P2 {Type:"pair2", Name, Platform, Key: PD, Nonce: ND}
  P3 {Type:"pair3", Key: PI, Nonce: NI}              ------>
                                                              check Commit matches PI and NI
                                                              both compute Z, T, SAS (below)
                                                              Host SHOWS the SAS
  User types the SAS into the Joiner.
  Joiner compares it with its own SAS. If different: stop, send nothing more.
  P4 {Type:"pair4", Proof: HMAC(confirmKey, "rs-confirm-J" || SAS)}  ------>
                                                              check; if wrong: count a failed attempt, stop
                       <------  P5 {Type:"pair5", Proof: HMAC(confirmKey, "rs-confirm-H" || SAS)}
  both store psk, the peer's name and platform, and their role; close.
```

`PI`, `PD` are 65-byte uncompressed X9.63 points (`04 || X || Y`). A receiver rejects a point that is not on the curve.

### 5.3 Derived values

```
Z       = x-coordinate (32 bytes) of ECDH(own ephemeral private, peer ephemeral public)
field(b)= uint16 big-endian length of b, then b
T       = SHA256( "rs-pair-transcript/1"
                  || field(PI) || field(NI) || field(PD) || field(ND)
                  || field(UTF8(Joiner Name)) || field(UTF8(Joiner Platform))
                  || field(UTF8(Host Name))   || field(UTF8(Host Platform)) )
PRK     = HKDF-Extract(salt = T, ikm = Z)            (HMAC-SHA256)
sasRaw  = HKDF-Expand(PRK, info = "rs-sas",     L = 4)
confirmKey = HKDF-Expand(PRK, info = "rs-confirm", L = 32)
psk     = HKDF-Expand(PRK, info = "rs-psk",     L = 32)
SAS     = (uint32 big-endian of sasRaw) mod 1,000,000, written as 6 digits with leading zeros
```

The names and platforms are in `T`, so the SAS also proves they were not changed on the way.

### 5.4 Why this holds

- **No grinding.** The Joiner commits to `PI, NI` before it sees `PD, ND`; the Host fixes `PD, ND` before it sees `PI, NI`. Neither side can pick keys to steer the six digits. An attacker in the middle runs two separate sessions and the digits match by luck only: 1 in 1,000,000 per try.
- **The code is never sent in a form that can be guessed offline.** The Joiner checks the typed digits against its own SAS **before** it sends anything derived from them. A wrong code (or an attacker in the middle) leaks nothing about the Host's real code. Only when the Joiner's own SAS equals what was typed does it send `Proof`.
- **Online guessing is capped.** One attempt per session; after 3 failed sessions, or 120 s, pairing mode ends and the user must start it again. An attacker's chance per pairing window is at most 3 in 1,000,000.
- **Who is joining is shown.** The Host's screen says "Pairing with <Joiner name>" so the user can cancel if the name is not the device they are holding.
- **One session at a time.** A second connection while one is in progress is refused ("busy").

### 5.5 What gets stored

Per pair: `pairId` (16 random bytes, local only, never sent), the peer's name and platform, our role (Host or Joiner), the date. These are not secrets and go in ordinary app preferences. The `psk` goes **only** in the platform secret store (Mac: Keychain item `ReleaseSync/LanPair/<pairId>`, this-device-only, never iCloud Keychain). It is never written to a file, a log, a backup or a test.

---

## 6. Link handshake

After pairing, two devices connect to each other whenever they see each other. The handshake is `Noise_NNpsk0` ([Noise Protocol Framework, revision 34](https://noiseprotocol.org/noise.html)) with the paired `psk`, AES-256-GCM and SHA-256, with P-256 as the Diffie-Hellman function.

### 6.1 Why this and not TLS

- A TLS server presents its certificate to **anyone** who connects. A stable certificate would let a stranger on any network recognise and track the device. (Per-pair certificates fix that, but a Network.framework listener cannot choose a certificate per connection, and .NET has no TLS-PSK.)
- Windows 10 does not offer TLS 1.3.
- Noise is small, formally analysed, and built from primitives both platforms already have: ECDH, SHA-256, HMAC, HKDF, AES-GCM.
- The responder recognises a paired device by **trying to decrypt** the first message with each paired `psk`. No tag or identifier appears on the wire, so strangers cannot tell a paired device from a random one, and nothing identifying is published in DNS-SD.

### 6.2 Deviations from stock Noise

Noise does not register P-256, so we define it as follows (everything else is Noise rev 34):

- DH public keys are 65-byte uncompressed X9.63 points. The DH output is the 32-byte x-coordinate. (Noise assumes both are `DHLEN` long; nothing in this pattern depends on that.)
- Cipher: AES-256-GCM. Nonce = 4 zero bytes followed by the 64-bit counter, big-endian. Associated data as Noise specifies.
- Hash: SHA-256. `HKDF` is Noise's, which is RFC 5869 HKDF-SHA-256 with an empty `info` and the chaining key as salt.
- Prologue: the ASCII bytes of `releasesync-lan/1`.
- Protocol name `Noise_NNpsk0_P256_AESGCM_SHA256` is 31 bytes, so `h` starts as the name padded with zeros to 32 bytes.

### 6.3 The messages

```
-> psk, e        (initiator = the device that dialled)
<- e, ee         (responder)
```

Each handshake message on the wire is a 2-byte big-endian length followed by the bytes of the Noise message (maximum 1,024). The payload of message 1 is the dialer's `hello`, the payload of message 2 is the responder's `hello` (section 8.2), both encrypted by Noise.

The responder tries each of its paired `psk`s on message 1. If none decrypts it, the responder **closes the connection without sending a single byte**. After the second message both sides call `Split()`; the initiator sends with the first key and receives with the second. From then on [records](#7-records) carry the traffic.

The responder keeps a 5-minute memory of the ephemeral public keys it has seen in message 1 and drops repeats, so a captured message 1 cannot be replayed to make the device do work. (A replay would not give the attacker anything anyway: they cannot compute the session keys without the ephemeral private key.)

Cost to an unauthenticated peer: one SHA-256/HKDF/AES-GCM try per paired secret. No elliptic-curve work happens until message 1 has authenticated.

---

## 7. Records

After the handshake, each direction is a stream of records:

```
uint32 big-endian length N     (N = size of what follows; at most 524,304)
N bytes: AES-256-GCM ciphertext followed by its 16-byte tag
```

- Key: the one `Split()` gave for that direction. Nonce: 4 zero bytes then a 64-bit big-endian counter that starts at 0 and goes up by 1 for each record. No associated data.
- A receiver closes the connection on a bad tag, a length over the limit, or a record it cannot decode. It never skips a record.
- After 2^32 records a device closes the connection and reconnects (new keys).
- Plaintext inside a record is one message ([section 8](#8-messages)); at most 524,288 bytes (512 KiB).

---

## 8. Messages

### 8.1 JSON conventions

The same as the sync file ([SYNC-FORMAT.md section 2](SYNC-FORMAT.md)): UTF-8, PascalCase names, properties in the order given here, properties with no value left out, `\uXXXX` escaping exactly as that document describes, timestamps as UTC with a trailing `Z` and no more than 7 fractional digits. **Compact**: no spaces and no line breaks. Readers ignore properties they do not know and ignore messages with a `Type` they do not know.

### 8.2 Message types

| `Type` | Properties (in this order) | When |
|---|---|---|
| `hello` | `Type`, `Proto`, `Name`, `Platform`, `AppVersion` | Payload of the two handshake messages. If the two `Proto` values differ, both use the lower if they support it, else close. |
| `state` | `Type`, `Seq`, `Open`, `Installed` | Right after the handshake, and whenever what a device has installed changes (merged, at most one per 2 s). `Seq` counts up from 1 on each connection. `Open` is `true` while ReleaseSync is running. `Installed` is a list of the entries below; the whole list is sent every time. |
| `ping` | `Type`, `N` | Sent after 2 s with nothing sent. |
| `pong` | `Type`, `N` | The answer to `ping`, with the same `N`. |
| `goodbye` | `Type`, `Reason` (optional) | Sent on a normal quit, so the peer goes grey at once. |
| `forget` | `Type` | Sent just before a device deletes the pairing ([section 10](#10-forgetting-a-device)). |

An `Installed` entry: `Key` (the tracked app's key, `source:owner/repo`, lower case), `Version`, `InstalledAt` (optional), `Latest` (optional; the newest version that device knows of).

Anything else a device might want to send (paths, file names, user names, tokens, passphrases, library contents) is **not** part of the protocol and is never sent.

### 8.3 What a receiver does with `state`

It replaces what it knew about that device with the new list (applying the limits in section 11), and keeps it in memory for as long as the connection lives. A platform may also keep the last list on disk (encrypted) so "installed on that device" still works away from home; that is a product choice per platform, and the light must then be grey and the list marked "as of <time>".

---

## 9. Liveness and reconnecting

- **Green** means: the handshake is complete and something (any record) has arrived within the last 5 seconds.
- A device sends `ping` whenever it has sent nothing for 2 seconds (a `state` or `pong` counts as sending). The peer answers `pong`. So records flow both ways at least every ~2 s while healthy.
- **Grey** when nothing arrives for 5 seconds, the socket closes or errors, or a `goodbye` arrives. The connection is closed and the redial rules apply. TCP keep-alive is also turned on as a backstop; it is not what the 5 seconds rely on.
- **Redial.** While the other device's service is still visible in DNS-SD, the dialer retries with the backoff in section 3. When DNS-SD says the service is gone, it stops. On a network change, wake from sleep, or the feature being turned on, it browses again and dials at once.
- **Duplicate links.** Both devices may dial each other at once. When a second connection for the same pair finishes its handshake, the device keeps the one **the Joiner dialled** and closes the other, without a `goodbye`.
- **Handshake time.** A connection that has not finished the handshake after 5 seconds is closed.
- **Backgrounded phones** (later) may use longer intervals; they would need a protocol change and are not designed here.

---

## 10. Forgetting a device

"Forget this device" on either side:
1. If a link is alive, send `forget`, then close it.
2. Delete the `psk` from the secret store and remove the pair's record.
3. The peer, on receiving `forget`, does the same and shows "<Name> removed this pairing."

If the other device was not reachable, it finds out later: its dials are closed without a reply (the forgotten side has no matching secret). After 3 failures in a row while that device's service is visible, it shows "<Name> no longer recognises this device. Pair again or remove it." and stops dialling it until the user acts.

---

## 11. Limits

| What | Limit | On exceeding |
|---|---|---|
| Handshake message | 1,024 bytes | close |
| Pairing message | 1,024 bytes | close |
| Record plaintext | 524,288 bytes | close |
| `Installed` entries per device | 1,000 | ignore the rest |
| `Key` | 200 bytes (UTF-8) | ignore the entry |
| `Version`, `Latest` | 64 bytes | ignore the entry |
| `Name` | 64 bytes | shorten |
| `Platform` | 32 bytes | shorten |
| `AppVersion` | 32 bytes | shorten |
| Empty or duplicate `Key` | | ignore (last duplicate wins, as `SyncPresence.Inventory` did) |
| Paired devices | 16 | refuse a 17th |
| Simultaneous connections | 16 | refuse more |

---

## 12. Consent, network behaviour and wording

**Off by default.** Setting: **Find my devices on this network**. Off means: no listener, no browser, no advertisement, no open port.

**On.** The device listens on an ephemeral TCP port, advertises `_releasesync._tcp`, browses for the same, and dials as in section 4. Turning it off stops all of that at once and closes every link.

**Network types.** The Windows side advertises only on networks Windows has marked Private (it will add the Firewall rule details). The Mac has no such label, so the Mac asks nothing extra beyond the toggle; a later version may let the user pick which networks to use.

**Per-device state shown to the user** (plain words):
- *Online* (green): "Online now"
- *Grey, feature on, network up, device not found*: "Not found on this network"
- *Grey, feature off*: "Turn on Find my devices to see this"
- *Grey after repeated failures*: "No longer recognises this device. Pair again."

**Client isolation.** Many networks (guest Wi-Fi, some routers, hotels) stop devices seeing each other. When the feature is on, at least one device is paired, and none is found 20 seconds after the network came up, show: *"No other devices found. Some networks, such as guest Wi-Fi, stop devices from seeing each other. Try your home network."* Never fail silently.

**macOS Local Network permission.** The first time the Mac browses or listens, macOS asks "ReleaseSync would like to find and connect to devices on your local network". Before turning the feature on, the Mac app explains, in plain words and with the setting off until the user agrees: *"ReleaseSync looks for your own devices on this Wi-Fi so it can show which ones are on. macOS will ask permission first. Nothing goes over the internet, and only devices you pair with can see anything."* Required `Info.plist` entries:
- `NSLocalNetworkUsageDescription`: *ReleaseSync looks for your own devices on this network so it can show which ones are online. Nothing leaves your network.*
- `NSBonjourServices`: `_releasesync._tcp` and `_releasesync-pair._tcp`.

If the user declines the macOS prompt, the Mac shows "macOS is blocking local network access for ReleaseSync" with a button to open Privacy settings.

**Windows.** An inbound Windows Firewall rule for the app on Private networks is needed because the port is chosen by the system. (To be filled in from the PC side.)

---

## 13. Fixtures and code structure

### 13.1 Structure

The shared logic lives in the platform-neutral cores (`ReleaseSync.Core` in C#, `ReleaseSyncKit` in Swift) with **no networking in them**. The core is "sans-IO": a link object takes bytes that arrived and the current time, and returns what to send and what happened.

```
LanLink  (one per connection; role = dialer or listener)
   init(self info, paired secrets, clock/random source)
   feed(bytes)  -> [action]     // bytes the socket received
   tick(now)    -> [action]     // time passing (pings, dead-connection checks)
   setState(installed, open) -> [action]
   close(reason) -> [action]
   action = send(bytes) | connected(peerInfo, pairId) | state(peer, installed) | closed(reason)

LanPairing (host and joiner)  same shape: feed / tick / actions, plus sas
```

A platform provides: listening, DNS-SD advertise/browse, sockets or `NWConnection`, a secret store, a clock, and a random source. Tests drive two links against each other in memory, with a fake clock, to simulate devices, lost connections and attackers.

"Connect to a known host and port" is its own small step in each platform (`dial(host, port)`), kept separate from discovery.

### 13.2 Fixtures (generated by the C# core, checked by the Swift package, like the sync fixtures)

`ReleaseSync.Core.Tests/Fixtures/lan/`:

| File | Contents |
|---|---|
| `messages.json` | Every message type: the object and its exact compact text. Escaping cases. Over-limit and unknown cases (ignored). |
| `pairing.json` | Obviously fake fixed private scalars, nonces, names, so the public keys, the commitment, `Z`, `T`, the SAS, `confirmKey`, `psk`, both proofs and all five wire messages are fixed. Cases for a wrong commitment, a wrong code and an off-curve point. |
| `handshake.json` | A fake `psk`, fixed ephemeral keys and payloads: both handshake messages byte for byte, the running `h` and `ck`, the two transport keys, and the first records. Cases for a wrong `psk` (no reply), a damaged message and a replayed message. |
| `records.json` | Record framing and nonce counters, size limits, a bad tag, a skipped or repeated counter. |
| `liveness.json` | Scenarios for the sans-IO link with a fake clock: ping after 2 s of silence, grey at 5 s, goodbye, duplicate links, backoff times. |
| `limits.json` | The limits in section 11, applied to `state` messages. |

The usual rules apply: nothing from a real machine, no real names, paths, keys or secrets. All keys and secrets in fixtures are fixed, made-up values.

---

## 14. Open questions

**Answers to step 1 (from the Mac side).**

1. **Do I agree with removing the file-based presence and returning to format 2? Yes**, for these reasons and with these notes:
   - **It also removes the rollout blocker.** The Mac's write support (favorites, hide, groups, notes, remove; behind a switch, off by default) currently warns that older Windows apps stop syncing because the first write makes the file format 3. With the file back at format 2, a Mac write is readable by every existing Windows app, so the switch no longer depends on updating the PC first. I will then drop that warning.
   - **What the Mac app uses today** (all of it moves to the LAN link as an in-memory source):
     `ReleaseSyncKit`: `SyncInstall`, `SyncDevice.online`/`installed`, `SyncPresence`, the signature including them, `supportedVersion = 3`, the 2-hour heartbeat, `PresenceFixtureTests`.
     Mac app: `DeviceInventory`, `StatusLight` and the lights in Settings, the Library device chips and the drawer's "On your devices", the Library "Updates" filter, `LibraryModel`'s `online = true` when the Mac registers itself, the demo data, and `DeviceInventoryTests`, `LibraryEditingTests`.
   - **Away from home**, "what is installed on my other devices" and the Updates filter would show nothing. I propose to keep the last list per paired device in the Mac's encrypted cache, shown grey as "as of <time>" (section 8.3). Tell me if you object.
   - **The Mac still registers itself in the file's `Devices` list** (Id, Name, Platform, AppVersion, LastSeen), so the device registry stays as it is.
2. **Constants.** In the repository today the heartbeat is **2 hours** and the online window **150 minutes** (`SyncService.Heartbeat`, `SyncPresence.OnlineWindow`), not 4 and 11 minutes. If you have a different version locally, tell me. On removal the heartbeat should go back to **6 hours**, its format 2 value.

**Questions for you (PC side) and decisions I want you to confirm.**

3. **Not TLS.** Section 6.1 gives the reasons for a Noise handshake instead (no stable certificate for strangers to track, no per-connection identity choice in `NWListener`, no TLS-PSK in .NET, no TLS 1.3 on Windows 10). It is a standard pattern with a small, spelled-out deviation (P-256). I would like an independent cryptography review of sections 5 to 7 before this ships; I do not want to be the only reviewer of my own design.
4. **Dialling every instance.** Because nothing identifying is published, a device cannot tell which instance is whose, so it tries each paired secret on each new instance (cached for 10 minutes). With the 16-device cap this is cheap, but it does mean strangers' devices receive one closed connection per secret. Acceptable?
5. **SAS instead of a PAKE** (section 5.1). The UX is the same ("one device shows 6 digits, the other takes them"), the security is a 1-in-a-million guess per session with at most 3 sessions per pairing window, and there is no cryptography of our own beyond ECDH/HKDF/HMAC. If you prefer a vetted PAKE library (CPace, SPAKE2+), say so; the rest of the protocol does not change, only section 5.2 to 5.4.
6. **QR pairing** (phones, later): a QR code could carry a random 128-bit secret instead of the SAS flow, skipping the typed code. Not designed here.
7. **Names in pairing mode.** The pairing service publishes the device name (`n=`) for at most 120 s so the user can pick the right device. Alternative: show only "ReleaseSync on <platform>". I chose the name for usability; tell me if you want it hidden.
8. **No padding of records** in version 1, so sizes hint at app counts. Say so if you want padding to a multiple of 1 KiB.
9. **Windows Firewall.** The system-assigned port needs a program-based inbound rule on Private networks. Please add what the Windows installer/first-run does (section 12).
10. **Process.** After you say "spec agreed": you change the engine and fixtures (format back to 2, 6-hour heartbeat, presence removed) and I pull; I port the LAN logic to Swift as you port it to C#, each side then builds its networking and UI.
