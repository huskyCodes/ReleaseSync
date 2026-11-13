import Foundation

/// What macOS says about a downloaded app's signature and whether Gatekeeper would let it open. The platform runs `codesign` and `spctl`;
/// this reads their answers, and decides what ReleaseSync does about them (the Mac counterpart of the Windows `Verifier` rules).
public struct Assessment: Equatable, Sendable {
    public enum Signature: Equatable, Sendable { case signed, unsigned, invalid }

    public var signature: Signature
    /// Gatekeeper would open it without a warning (a Developer ID signature that Apple has notarized, or an App Store app).
    public var gatekeeperAccepts: Bool
    public var notarized: Bool
    /// Who signed it, as macOS reports it ("Developer ID Application: Example Ltd (ABCDE12345)").
    public var signer: String?
    /// The download's SHA-256 matched what the project published (nil: the project published none).
    public var checksumMatched: Bool?

    public init(signature: Signature, gatekeeperAccepts: Bool, notarized: Bool, signer: String? = nil, checksumMatched: Bool? = nil) {
        self.signature = signature; self.gatekeeperAccepts = gatekeeperAccepts; self.notarized = notarized; self.signer = signer; self.checksumMatched = checksumMatched
    }

    /// `codesign --verify --deep --strict`: exit status 0 means the signature is intact.
    public static func signature(exitStatus: Int32, message: String) -> Signature {
        if exitStatus == 0 { return .signed }
        return message.lowercased().contains("not signed at all") ? .unsigned : .invalid
    }

    /// `spctl --assess --type execute --verbose=4`: "accepted" with "source=Notarized Developer ID" or "source=Mac App Store", plus an
    /// "origin=" line naming the signer; otherwise "rejected".
    public static func gatekeeper(exitStatus: Int32, output: String) -> (accepts: Bool, notarized: Bool, signer: String?) {
        let lines = output.split(whereSeparator: \.isNewline).map(String.init)
        func value(_ key: String) -> String? { lines.first { $0.hasPrefix(key + "=") }.map { String($0.dropFirst(key.count + 1)).trimmingCharacters(in: .whitespaces) } }
        let source = value("source") ?? ""
        let accepted = exitStatus == 0 && lines.contains { $0.contains(": accepted") }
        let notarized = accepted && source.lowercased().contains("notarized")
        return (accepted, notarized, value("origin"))
    }

    /// Outcomes, worst first.
    public enum Verdict: Equatable, Sendable {
        /// Stop and delete the download: the checksum doesn't match, or the signature is broken.
        case blocked(String)
        /// Ask the person first: not signed, or macOS wouldn't open it without a warning.
        case confirm([String])
        case ok
    }

    /// `trustUnsigned` is the person's "don't ask again for this app", which covers the unsigned case only, never a mismatch or a broken signature.
    public func verdict(trustUnsigned: Bool) -> Verdict {
        if checksumMatched == false { return .blocked("The download's checksum doesn't match the one the project published, so ReleaseSync deleted it. Try again; if it keeps happening, don't install it.") }
        if signature == .invalid { return .blocked("The app's signature is invalid: it was changed after it was signed. ReleaseSync stopped and deleted it.") }
        var findings: [String] = []
        if signature == .unsigned { findings.append("it isn't signed by a developer") }
        else if !gatekeeperAccepts { findings.append("macOS can't confirm it is safe (it isn't notarized by Apple)") }
        if findings.isEmpty || trustUnsigned { return .ok }
        if checksumMatched == nil { findings.append("the project publishes no checksum to compare against") }
        return .confirm(findings)
    }

    /// One line for the activity log and the app's details.
    public var summary: String {
        var parts: [String] = []
        switch signature {
        case .signed: parts.append(notarized ? "signed and notarized" + (signer.map { " (\($0))" } ?? "") : (gatekeeperAccepts ? "signed" : "signed, not notarized"))
        case .unsigned: parts.append("not signed")
        case .invalid: parts.append("INVALID SIGNATURE")
        }
        if let m = checksumMatched { parts.append(m ? "checksum verified" : "CHECKSUM MISMATCH") } else { parts.append("no checksum published") }
        return parts.joined(separator: ", ")
    }
}
