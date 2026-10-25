import Foundation

/// The numbers of docs/LAN-PRESENCE.md (section 3 and section 11), in one place. Both implementations use the same ones.
/// Durations are in seconds.
public enum Lan {
    public static let protocolVersion = 1
    public static let serviceType = "_releasesync._tcp"
    public static let pairingServiceType = "_releasesync-pair._tcp"
    public static let noiseProtocolName = "Noise_NNpsk0_P256_AESGCM_SHA256"
    public static let noisePrologue = "releasesync-lan/1"

    // timing
    public static let pingInterval: TimeInterval = 2
    public static let deadAfter: TimeInterval = 5
    public static let handshakeTimeout: TimeInterval = 5
    public static let redialBackoff: [TimeInterval] = [1, 2, 4, 8, 16, 30]
    public static let stateMinInterval: TimeInterval = 2
    public static let pairingWindow: TimeInterval = 120
    public static let pairingStepTimeout: TimeInterval = 5
    public static let pairingConfirmWait: TimeInterval = 60
    public static let maxFailedPairingSessions = 3
    public static let replayMemory: TimeInterval = 300

    // blocking (section 12)
    public static let unknownFailureLimit = 10
    public static let unknownFailureWindow: TimeInterval = 60
    public static let unknownMaxConnectionsPerAddress = 4
    public static let unknownBlockFirst: TimeInterval = 5 * 60
    public static let pairedBlockFirst: TimeInterval = 10 * 60
    public static let blockCap: TimeInterval = 60 * 60
    public static let blockLevelReset: TimeInterval = 24 * 60 * 60
    public static let pairedStatesPerWindow = 10
    public static let pairedStateWindow: TimeInterval = 10

    // limits (section 11)
    public static let maxPaired = 16
    public static let maxConnections = 16
    public static let maxHandshakeMessage = 1_024
    public static let maxPairingMessage = 1_024
    public static let maxRecordPlaintext = 524_288
    public static let maxInstalled = 1_000
    public static let maxKeyBytes = 200
    public static let maxVersionBytes = 64
    public static let maxNameBytes = 64
    public static let maxPlatformBytes = 32
    public static let maxAppVersionBytes = 32
    public static let deviceIdLength = 32
}
