import Foundation

// The shapes of the local-network link (docs/LAN-PRESENCE.md, sections 13 and 14.1): "sans-IO", so the logic takes bytes and time and gives back
// what to send and what happened, and the platform does the sockets and Bonjour. These are PROVISIONAL: they are filled in and tested
// against the C# fixtures (Fixtures/lan) as each one lands, and may change until then.

/// What a paired device says about itself in its `hello` (section 8.2).
public struct LanPeer: Equatable, Sendable {
    /// The device's id in the sync file's `Devices` list.
    public var deviceId: String
    public var name: String
    public var platform: String
    public var appVersion: String
    public init(deviceId: String, name: String, platform: String, appVersion: String) {
        self.deviceId = deviceId; self.name = name; self.platform = platform; self.appVersion = appVersion
    }
}

/// One program a device says it has installed (section 8.2).
public struct LanInstall: Equatable, Sendable {
    public var key: String
    public var version: String
    public var installedAt: Timestamp?
    /// The newest version that device knows of.
    public var latest: String?
    public init(key: String, version: String, installedAt: Timestamp? = nil, latest: String? = nil) {
        self.key = key; self.version = version; self.installedAt = installedAt; self.latest = latest
    }

    /// An update is waiting on that device: it knows of a newer version than the one it has.
    public var hasUpdate: Bool {
        guard let latest, !latest.isEmpty, !version.isEmpty else { return false }
        return VersionUtil.isNewer(latest, than: version)
    }
}

/// Why a link or a pairing ended.
public enum LanCloseReason: Equatable, Sendable {
    case local                      // this side chose to close (feature turned off, app quitting)
    case goodbye(String?)           // the peer said goodbye
    case forgotten                  // the peer removed the pairing
    case deadLink                   // nothing arrived for `Lan.deadAfter`
    case duplicate                  // another link for the same pair is kept
    case handshakeFailed            // no paired secret fitted, or the message was wrong
    case handshakeTimeout
    case badRecord                  // bad authentication tag
    case tooLarge
    case malformed                  // did not parse
    case tooManyStates
    case protocolUnsupported        // the peer is probably newer
}

/// What a link tells the platform to do or shows it.
public enum LanAction: Equatable, Sendable {
    case send(Data)
    /// The handshake is done and the peer's `hello` has arrived.
    case connected(LanPeer, pairId: String)
    /// The peer's current state (section 8.3).
    case peerState(open: Bool, installed: [LanInstall])
    case closed(LanCloseReason)
}

public enum LanLinkRole: Equatable, Sendable { case dialer, listener }
public enum LanPairRole: String, Codable, Equatable, Sendable { case host = "Host", joiner = "Joiner" }

/// One connection between two paired devices.
public protocol LanLinkMachine {
    /// Bytes that arrived on the socket.
    mutating func feed(_ bytes: Data) -> [LanAction]
    /// Time has passed (pings, dead-link and handshake timeouts).
    mutating func tick(_ now: Timestamp) -> [LanAction]
    /// What this device has installed changed (merged to at most one message per `Lan.stateMinInterval`).
    mutating func setState(open: Bool, installed: [LanInstall], now: Timestamp) -> [LanAction]
    mutating func close(_ reason: LanCloseReason, now: Timestamp) -> [LanAction]
}

/// What a pairing session shows the user and waits for.
public enum LanPairingStep: Equatable, Sendable {
    case connecting
    /// Both devices show these six digits; the user confirms on both that they match.
    case confirm(sas: String, peerName: String, peerPlatform: String)
    case waitingForPeer
    case paired
    case failed(LanPairingFailure)
}

public enum LanPairingFailure: Equatable, Sendable {
    case busy, timedOut, declinedHere, declinedByPeer, notMatching, damaged
}

/// A pairing, either role (section 5).
public protocol LanPairingMachine {
    var step: LanPairingStep { get }
    mutating func feed(_ bytes: Data) -> [LanAction]
    mutating func tick(_ now: Timestamp) -> [LanAction]
    /// The user said the digits match on this device. The proof is sent only now.
    mutating func userConfirmed(now: Timestamp) -> [LanAction]
    mutating func userDeclined() -> [LanAction]
}

/// A paired device, as kept in ordinary app preferences. The shared secret is NOT here: it lives only in the platform secret store.
public struct LanPairRecord: Codable, Equatable, Sendable, Identifiable {
    /// 16 random bytes as 32 lower-case hex digits; local only, never sent.
    public var pairId: String
    public var peerName: String
    public var peerPlatform: String
    public var role: LanPairRole
    public var createdAt: Timestamp
    /// The peer's id in the sync file, known once its `hello` has arrived.
    public var peerDeviceId: String?
    /// The user chose Pause: do not connect, and close anything it opens.
    public var paused: Bool
    public var id: String { pairId }

    public init(pairId: String, peerName: String, peerPlatform: String, role: LanPairRole, createdAt: Timestamp, peerDeviceId: String? = nil, paused: Bool = false) {
        self.pairId = pairId; self.peerName = peerName; self.peerPlatform = peerPlatform; self.role = role; self.createdAt = createdAt
        self.peerDeviceId = peerDeviceId; self.paused = paused
    }

    /// Whether `text` could be a pair id (32 lower-case hex digits). Used before a pair id becomes part of a secret-store name.
    public static func isValidPairId(_ text: String) -> Bool {
        text.utf8.count == 32 && text.utf8.allSatisfy { ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x61 && $0 <= 0x66) }
    }

    /// A fresh pair id from a source of random bytes.
    public static func makePairId(random: (Int) -> Data) -> String {
        random(16).map { String(format: "%02x", $0) }.joined()
    }
}

/// The clock and the randomness a link or pairing uses, given by the platform (a fake in tests).
public protocol LanEnvironment: Sendable {
    func now() -> Timestamp
    func randomBytes(_ count: Int) -> Data
}
