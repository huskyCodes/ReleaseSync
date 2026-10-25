# ReleaseSync device presence on the local network

**Status: DRAFT, revised after the PC review. Nothing here is implemented yet.** The protocol (sections 5 to 7) was designed and reviewed by AI assistants only; **it needs a review by a human with cryptography experience before any public release.** Both the Windows side (C#, `ReleaseSync.Core`) and the Mac side (Swift, `ReleaseSyncKit`) follow this one document. Later: Android, then iPhone.

This replaces the file-based presence that was added in sync format 3 (`Online`, `Installed`, `SyncPresence`). See [Open questions](#15-open-questions) for what that removal means for each side.

Contents: [1 Goal](#1-goal-and-non-goals) · [2 Threats](#2-who-we-defend-against) · [3 Constants](#3-constants) · [4 Discovery](#4-discovery) · [5 Pairing](#5-pairing) · [6 Link handshake](#6-link-handshake) · [7 Records](#7-records) · [8 Messages](#8-messages) · [9 Liveness](#9-liveness-and-reconnecting) · [10 Forgetting](#10-forgetting-a-device) · [11 Limits](#11-limits) · [12 Blocking and safety](#12-blocking-and-safety) · [13 Consent and UI](#13-consent-network-behaviour-and-wording) · [14 Fixtures and structure](#14-fixtures-and-code-structure) · [14 Open questions](#15-open-questions)

---

## 1. Goal and non-goals

**Goal.** A device shows another of the user's devices as **online (green)** while a direct, authenticated, encrypted connection between them is alive, and grey within about 5 seconds of that connection dying. Over the connection a device says who it is (name, platform), that ReleaseSync is open, and what it has installed (key, version, install time, newest version it knows of).

**What it is not.**
- Not the library sync. The library still syncs through the private Gist or the shared folder, unchanged (`docs/SYNC-FORMAT.md`).
- Not internet presence. Away from the shared network, lights are grey. That is accepted.
- No accounts, no tokens, no GitHub requests, no telemetry, no servers of ours.

**Out of scope, but not made impossible.** Connecting across the internet or through VPN/overlay networks. "Connect to a known host and port" is a separate, replaceable step after discovery (section 14), so a later version can add a manually entered address.

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
| Pairing network step timeout | 5 s |
| Pairing confirmation wait (people compare digits) | 60 s |
| Declined or failed pairing sessions | 3, then pairing mode ends |
| Replay memory | 5 min |
| Failed-handshake limit | more than 10 from one address in 60 s: blocked, see [section 12](#12-blocking-and-safety) |
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

Pairing happens once per pair of devices. It creates a 32-byte shared secret (`psk`) that both store in their platform secret store. There are two roles: the **Host** (the device the user chose "Add a device" on) and the **Joiner** (the device that finds the Host and asks to pair). **Both devices show the same 6 digits, and the user confirms on both that they match.**

### 5.1 What it is, and why it is not a PAKE

The 6 digits are a **short authentication string (SAS)**, the same idea as Bluetooth "numeric comparison" and ZRTP's SAS. Both devices do an ephemeral P-256 key exchange and derive the same 6 digits from everything they exchanged. The user looks at both screens. If someone sat in the middle, the two devices would have different digits.

**Nothing is typed.** An earlier draft had the user type the Host's digits into the Joiner. That protected only the Joiner: the Host never learned what was typed, and anyone who had completed the key exchange with the Host could compute the same proof. An attacker who connected to the Host's pairing window first could therefore have been paired without guessing anything. The fix, from the PC review, is that **each device only goes on after its own user has said the digits match**, and the Host stores the secret only after its own user said so.

We use this over a PAKE such as SPAKE2/CPace because **neither platform ships a PAKE** (not CryptoKit, not .NET). SPAKE2 needs elliptic-curve point addition and CPace needs hash-to-curve; the platform ECDH APIs expose neither (ECDH returns only the x-coordinate). Writing curve arithmetic ourselves in two languages is the "home-made" cryptography we want to avoid, and a third-party library is a supply-chain risk for a public repo. The SAS approach uses only primitives both platforms have (P-256 ECDH, SHA-256, HMAC, HKDF).

### 5.2 Messages

Pairing runs over a TCP connection from the Joiner to the Host's pairing service. Each message is a 2-byte big-endian length followed by that many bytes of compact UTF-8 JSON ([8.1](#81-json-conventions)); binary values are standard Base64. Maximum message size 1,024 bytes. The Host serves one session at a time. **No identifier from the sync file (such as `DeviceId`) is sent in these cleartext messages.**

```
Joiner                                                    Host
  generate ephemeral P-256 key (PI), random NI (16 bytes)
  P1 {Type:"pair1", Proto:1, Name, Platform,
      Commit: SHA256("rs-pair-commit/1" || PI || NI)}  ------>
                                                              generate (PD), random ND (16 bytes)
                       <------  P2 {Type:"pair2", Name, Platform, Key: PD, Nonce: ND}
  P3 {Type:"pair3", Key: PI, Nonce: NI}              ------>
                                                              check Commit matches PI and NI
  both compute Z, T, SAS (5.3)
  BOTH SHOW the SAS:
     Joiner: "Does <Host name> show 482 913?"           Host: "Pairing with <Joiner name>. Does it show 482 913?"
  user taps "Yes, they match" on the Joiner          user taps "Yes, they match" on the Host
  (or "No": stop, send nothing more)                 (or "No": stop, send nothing more)
  P4 {Type:"pair4", Proof: HMAC(confirmKey, "rs-confirm-J" || SAS)}  ------>   (sent only after the Joiner's own tap)
                                                              Host checks P4 AND needs its own tap
                       <------  P5 {Type:"pair5", Proof: HMAC(confirmKey, "rs-confirm-H" || SAS)}
                                (sent only after the Host's own tap AND a valid P4)
  Joiner stores psk after a valid P5.                Host stores psk after valid P4 AND its own tap.
```

`PI`, `PD` are 65-byte uncompressed X9.63 points (`04 || X || Y`). A receiver rejects a point that is not on the curve.

The names and platforms in P1/P2 are chosen by whoever sent them and prove nothing; **the digits are the proof.** The screens say so ("Only tap Yes if both screens show the same digits").

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

The names and platforms are in `T`, so the digits also show that they were not changed on the way.

### 5.4 Why this holds

- **The tap is what stops an attacker who connects first.** The proofs `pair4`/`pair5` can be computed by anyone who completed the key exchange, so they are not what protects pairing. What protects it is that **the Host stores nothing until its own user taps "Yes, they match"**, and the user taps only when the Joiner they are holding shows the same digits. An attacker who connected to the Host's window first has no screen the user is looking at: the user's own Joiner is refused ("busy") or shows different digits, the user taps "No" (or nothing), and nothing is stored. The same holds the other way round for a fake Host facing the Joiner.
- **No grinding.** The Joiner commits to `PI, NI` before it sees `PD, ND`; the Host fixes `PD, ND` before it sees `PI, NI`. Neither side can pick keys to steer the digits.
- **An attacker between the two real devices** runs two separate sessions with independent digits. The two screens show different digits except by luck: **1 in 1,000,000 per session**, and the user must look at both screens and tap Yes on both for it to matter. Each failed session is visible to the user ("No").
- **Limits on retries.** After 3 declined or failed sessions, or 120 s, pairing mode ends and the user must start it again.
- **Nobody can park on the window.** Each network step has a 5-second timeout (P1 to P3 must arrive in time). After P3 the Host waits at most 60 seconds for the people to compare. When a timeout fires, or the user taps No or "Not this device", the session is dropped and the one-session slot is free again. A "Not this device" tap also refuses that address for the rest of the window.
- **A user who taps Yes without looking** defeats any numeric comparison. The screens ask for a real comparison and show the device name and platform next to the digits, but the digits are what counts.

### 5.5 What gets stored

Per pair: `pairId` (16 random bytes, local only, never sent), the peer's name and platform, our role (Host or Joiner), the date, and the peer's sync-file `DeviceId` once its `hello` has told us. These are not secrets and go in ordinary app preferences. The `psk` goes **only** in the platform secret store (Mac: Keychain item `ReleaseSync/LanPair/<pairId>`, this-device-only, never iCloud Keychain). It is never written to a file, a log, a backup or a test.

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

Each handshake message on the wire is a 2-byte big-endian length followed by the bytes of the Noise message (maximum 1,024). **Both handshake payloads are empty** (each message is the ephemeral key plus a 16-byte tag, 81 bytes). The first message is protected only by the `psk`, so nothing about the device goes in it; each side sends its `hello` ([8.2](#82-message-types)) as the **first record** after `Split()`, when the keys already include the ephemeral exchange and so have forward secrecy.

The responder tries each of its paired `psk`s on message 1 (the empty payload's tag is what tells it the `psk` is right). If none works, the responder **closes the connection without sending a single byte**. After the second message both sides call `Split()`; the initiator sends with the first key and receives with the second. From then on [records](#7-records) carry the traffic.

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
| `hello` | `Type`, `Proto`, `DeviceId`, `Name`, `Platform`, `AppVersion` | The first record each side sends after the handshake. `DeviceId` is the device's id in the sync file's `Devices` list (32 lower-case hex digits), so what arrives over the link can be matched to that registry entry; it travels only inside encrypted records, never in discovery or the pairing messages. If the two `Proto` values differ, both use the lower if they support it, else close. A receiver stores the `DeviceId` with the pair record (and updates it if it changes after a reinstall). |
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
- **Redial.** (A device that is paused, or under a delay after misbehaving, is not dialled at all: [section 12](#12-blocking-and-safety).) While the other device's service is still visible in DNS-SD, the dialer retries with the backoff in section 3. When DNS-SD says the service is gone, it stops. On a network change, wake from sleep, or the feature being turned on, it browses again and dials at once.
- **Duplicate links.** Both devices may dial each other at once. When a second connection for the same pair finishes its handshake, the device keeps the one **the Joiner dialled** and closes the other, without a `goodbye`.
- **Handshake time.** A connection that has not finished the handshake and received the peer's `hello` after 5 seconds is closed.
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
| `DeviceId` | 32 bytes (hex digits) | ignore the value |
| `Name` | 64 bytes | shorten |
| `Platform` | 32 bytes | shorten |
| `AppVersion` | 32 bytes | shorten |
| Empty or duplicate `Key` | | ignore (last duplicate wins, as `SyncPresence.Inventory` did) |
| Paired devices | 16 | refuse a 17th |
| Simultaneous connections | 16 | refuse more |

---

## 12. Blocking and safety

Two kinds of peer are handled differently. Nothing here is written to disk except the user's own "Pause" choices.

### 12.1 Unknown peers (they do not authenticate)

An unknown peer is anyone whose first message does not work with any paired secret. There is no identity to block, only an address, so blocks are **by address, temporary, and kept in memory only**.

**Triggers** (any one):
- more than 10 failed handshakes from one address within 60 s;
- a malformed handshake message: wrong length, or a first message that is not a 65-byte point starting `04` followed by the tag (the point itself is only checked against the curve after the peer has authenticated, because no elliptic-curve work is done for strangers);
- a connection that sends nothing for the handshake timeout (5 s);
- more than 4 connections open at once from one address.

**Effect.** Close at once and send nothing. Ignore that address for **5 minutes**. Each time the same address earns another block, the time doubles: 5, 10, 20, 40, then 60 minutes, which is the cap. An address that has not triggered anything for 24 hours starts again at 5 minutes. A block ends by waiting; there is no permanent block list.

**Never blocked.** This device's own addresses and loopback.

### 12.2 Paired peers that misbehave (they authenticated, then broke a rule)

We cannot tell a bug from an attack, so the pairing is **never deleted automatically**.

**Triggers** (any one):
- a record with a bad authentication tag;
- a record or message over the size limits (section 11);
- a message that does not parse;
- `state` messages faster than one per second for 10 seconds (more than 10 in any 10-second window);
- an `Installed` list over the limit, more than once within 10 minutes.

**Effect.** Close the link at once. Do not redial that device, and close without a reply anything it opens, for **10 minutes**; each repeat doubles that (10, 20, 40, then 60 minutes, the cap); a device that has been clean for 24 hours starts again at 10 minutes. Whatever it sent in the record that broke the rule is discarded. The last good state it sent may stay shown, greyed.

The user sees, in plain words: *"<Name> sent data ReleaseSync could not trust, so the connection was closed."* with two buttons: **Remove this device** (section 10) and **Allow it again** (clears the delay and dials at once).

**One difference from the list above.** A device whose `hello` shows a `Proto` this version does not support is not misbehaving: it is probably newer. It is closed and not redialled for the same delay, but the message is *"<Name> uses a newer version of ReleaseSync. Update this device to connect."* and no "could not trust" wording is used.

### 12.3 What the user can do

On the "Find my devices" screen, in plain words:
- Per paired device: **Pause** (do not connect to it, and close anything it opens, until the user resumes; the pairing is kept) and **Remove** (section 10). Pause is the one choice that is saved, in ordinary app preferences.
- A counter such as *"3 unknown connection attempts were blocked in the last hour"* (every connection that closed without authenticating counts, including those turned away because the address was blocked), with a **Show** link that lists the time and address of each. The list is held in memory only and is cleared when the app quits.
- **Remove all paired devices.**
- The master switch **Find my devices on this network**. Off closes every link and stops listening, announcing and browsing at once.

### 12.4 Networks

- Never listen, announce or browse on a network the platform marks as public or untrusted. On Windows that is the Public network category (the PC side implements this and shows *"This network is set to Public in Windows. Change it to Private to find your devices."*). The Mac has no such label, so the Mac does not invent one. Whenever nothing is happening because of this, say why instead of failing silently.
- On a network change (a different network, wake from sleep, an interface going away), drop every link and every address block that belongs to the old network and start fresh. Pauses and delays for paired devices are kept, because they belong to the device, not the address.

---

## 13. Consent, network behaviour and wording

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

**Windows.** (From the PC review.)
- Windows itself asks the user when a program first listens, and only for the network types they tick. The app does not create firewall rules (that needs administrator rights). The rule belongs to the program's path, so a moved program asks again.
- The PC binds its listener only after the user has turned the feature on, and explains the Windows prompt before it appears.
- A network Windows has marked **Public** blocks inbound connections silently. The PC detects that (Network List Manager) and shows: *"This network is set to Public in Windows. Change it to Private to find your devices."*
- The PC advertises only on networks marked Private.
- Discovery on Windows needs a spike first: the Windows DNS-SD API against a small mDNS implementation over UDP multicast. The PC side will report the result; the wire protocol does not depend on it.

---

## 14. Fixtures and code structure

### 14.1 Structure

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

### 14.2 Fixtures (generated by the C# core, checked by the Swift package, like the sync fixtures)

`ReleaseSync.Core.Tests/Fixtures/lan/`:

| File | Contents |
|---|---|
| `messages.json` | Every message type: the object and its exact compact text. Escaping cases. Over-limit and unknown cases (ignored). |
| `pairing.json` | Obviously fake fixed private scalars, nonces, names, so the public keys, the commitment, `Z`, `T`, the SAS, `confirmKey`, `psk`, both proofs and all five wire messages are fixed. Cases for a wrong commitment, an off-curve point, a Joiner who taps No, a Host who taps No, a timeout at each step, and **an attacker as Joiner who sends a valid `pair4` but whose Host user never confirmed: the Host stores no `psk`, sends no `pair5`, and frees the session.** |
| `handshake.json` | A fake `psk` and fixed ephemeral keys: both (empty-payload) handshake messages byte for byte, the running `h` and `ck`, the two transport keys, and the first records (the two `hello` records). Cases for a wrong `psk` (no reply), a damaged message and a replayed message. |
| `records.json` | Record framing and nonce counters, size limits, a bad tag, a skipped or repeated counter. |
| `liveness.json` | Scenarios for the sans-IO link with a fake clock: ping after 2 s of silence, grey at 5 s, goodbye, duplicate links, backoff times. |
| `limits.json` | The limits in section 11, applied to `state` messages. |
| `blocking.json` | Scenarios on a fake clock for every trigger in section 12: the link closes and sends nothing, the stated delay applies, repeats double (5, 10, 20, 40, 60, 60 minutes for unknown addresses; 10, 20, 40, 60 for paired devices), and a block ends by waiting. Also: a paused device is never dialled and anything it opens is closed; this device's own addresses and loopback are never blocked; a `Proto` this version does not support gets the "newer version" outcome, not the "could not trust" one; a network change clears address blocks but not device delays or pauses. |

The usual rules apply: nothing from a real machine, no real names, paths, keys or secrets. All keys and secrets in fixtures are fixed, made-up values.

---

## 15. Open questions

**Settled (PC review, and the Mac side's answers).**

1. **Remove the file-based presence and return to format 2: agreed by both.** The heartbeat goes back to 6 hours. The PC's local 4- and 11-minute commits were never pushed and are being dropped (the repository's 2-hour and 150-minute values go with the removal).
   - **The Mac's write switch** (favorites, hide, groups, notes, remove; off by default) warned that older Windows apps stop syncing because a write made the file format 3. At format 2 that warning goes, and the switch no longer depends on updating the PC first.
   - **What the Mac used from the file, and moves to the LAN link:** `SyncInstall`, `SyncDevice.online`/`installed`, `SyncPresence`, the signature including them, `supportedVersion = 3`, the 2-hour heartbeat and `PresenceFixtureTests` in the Swift package; `DeviceInventory`, `StatusLight` and the lights in Settings, the Library device chips, the drawer's "On your devices", the Library "Updates" filter, `online = true` on registering, the demo data, `DeviceInventoryTests` and `LibraryEditingTests` in the Mac app. The Mac keeps listing itself in the file's `Devices` registry.
2. **A cached per-device list, shown grey as "as of <time>", is agreed** (section 8.3).
3. **Noise rather than TLS: agreed** (section 6.1).
4. **Trying each paired `psk` on each new instance: agreed.**
5. **A short authentication string rather than a PAKE: agreed only with two-sided confirmation**, which section 5 now has.
6. **Names shown in the pairing service for at most 120 s: agreed.**
7. **No padding of records in version 1: agreed.**
8. **Windows firewall behaviour:** settled in section 13.

**Still open.**

9. **A human cryptography review** of sections 5 to 7 before any public release. Both of us are AI assistants.
10. **QR pairing** (phones, later): a QR code could carry a random 128-bit secret instead of the SAS flow. Not designed here.
11. **Windows discovery spike** (Windows DNS-SD API or a small mDNS over UDP multicast): the PC side will report. The protocol does not depend on it.
12. **Process.** After "spec agreed": the PC changes the engine and fixtures (format back to 2, 6-hour heartbeat, file presence removed) and the Mac pulls; the Mac ports the LAN logic to Swift as the PC ports it to C#; each side then builds its networking and UI.
