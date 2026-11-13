import XCTest
@testable import ReleaseSyncKit

final class InstallLogicTests: XCTestCase {
    let hashA = String(repeating: "ab", count: 32)
    let other = String(repeating: "cd", count: 32)

    func asset(_ name: String, digest: String? = nil) -> AssetInfo { AssetInfo(name: name, url: "https://e.test/" + name, digest: digest) }

    // MARK: checksums
    func testGitHubsOwnDigestIsUsedWhenThereIsOne() {
        XCTAssertEqual(Checksums.published(by: asset("a.dmg", digest: "sha256:" + hashA)), hashA)
        XCTAssertNil(Checksums.published(by: asset("a.dmg", digest: "md5:abc")))
        XCTAssertNil(Checksums.published(by: asset("a.dmg")))
    }

    func testChecksumFilesAreFoundBestFirst() {
        let list = ["App.dmg", "App.dmg.sha256", "SHA256SUMS", "checksums.txt", "App.dmg.sig", "SHA256SUMS.asc", "App-win.exe", "notes.txt"].map { asset($0) }
        XCTAssertEqual(Checksums.candidateFiles(for: list[0], among: list).map(\.name), ["App.dmg.sha256", "SHA256SUMS", "checksums.txt"])
    }

    func testTheHashForADownloadIsReadFromTheCommonFormats() {
        XCTAssertEqual(Checksums.hash(in: "\(hashA)  App.dmg\n\(other)  Other.dmg\n", for: "App.dmg", checksumFileName: "SHA256SUMS"), hashA)
        XCTAssertEqual(Checksums.hash(in: "\(other) *dist/Other.dmg\r\n\(hashA) *dist/App.dmg\r\n", for: "App.dmg", checksumFileName: "SHA256SUMS"), hashA)
        XCTAssertEqual(Checksums.hash(in: "SHA256 (App.dmg) = \(hashA)\n", for: "App.dmg", checksumFileName: "checksums.txt"), hashA)
        XCTAssertEqual(Checksums.hash(in: "\(hashA)\n", for: "App.dmg", checksumFileName: "App.dmg.sha256"), hashA, "a lone hashA in the download's own checksum file")
        XCTAssertNil(Checksums.hash(in: "\(hashA)\n", for: "App.dmg", checksumFileName: "SHA256SUMS"), "a lone hashA in a shared file says nothing about this download")
        XCTAssertNil(Checksums.hash(in: "\(hashA)  Other.dmg\n", for: "App.dmg", checksumFileName: "SHA256SUMS"))
        XCTAssertNil(Checksums.hash(in: "not a checksum file", for: "App.dmg", checksumFileName: "SHA256SUMS"))
    }

    // MARK: assessments
    func testReadingWhatCodesignAndSpctlSay() {
        XCTAssertEqual(Assessment.signature(exitStatus: 0, message: ""), .signed)
        XCTAssertEqual(Assessment.signature(exitStatus: 1, message: "App.app: code object is not signed at all"), .unsigned)
        XCTAssertEqual(Assessment.signature(exitStatus: 1, message: "a sealed resource is missing or invalid"), .invalid)
        let ok = Assessment.gatekeeper(exitStatus: 0, output: "App.app: accepted\nsource=Notarized Developer ID\norigin=Developer ID Application: Example Ltd (ABCDE12345)\n")
        XCTAssertTrue(ok.accepts); XCTAssertTrue(ok.notarized); XCTAssertEqual(ok.signer, "Developer ID Application: Example Ltd (ABCDE12345)")
        let store = Assessment.gatekeeper(exitStatus: 0, output: "App.app: accepted\nsource=Mac App Store\n")
        XCTAssertTrue(store.accepts); XCTAssertFalse(store.notarized)
        let no = Assessment.gatekeeper(exitStatus: 3, output: "App.app: rejected\nsource=no usable signature\n")
        XCTAssertFalse(no.accepts); XCTAssertFalse(no.notarized)
    }

    func testTheSignerIsReadFromCodesign() {
        let out = "Executable=/x/Rectangle.app/Contents/MacOS/Rectangle\nIdentifier=com.knollsoft.Rectangle\nAuthority=Developer ID Application: Example Ltd (ABCDE12345)\nAuthority=Developer ID Certification Authority\nAuthority=Apple Root CA\nTeamIdentifier=ABCDE12345\n"
        XCTAssertEqual(Assessment.signer(fromCodesignDisplay: out), "Developer ID Application: Example Ltd (ABCDE12345)")
        XCTAssertEqual(Assessment.signer(fromCodesignDisplay: "Authority=Software Signing\nTeamIdentifier=not set\n"), "Software Signing")
        XCTAssertEqual(Assessment.signer(fromCodesignDisplay: "Authority=Some Developer\nTeamIdentifier=ZZZ9999999\n"), "Some Developer (ZZZ9999999)")
        XCTAssertNil(Assessment.signer(fromCodesignDisplay: "Identifier=x\nSignature=adhoc\n"))
    }

    func testWhatHappensForEachAssessment() {
        func a(_ s: Assessment.Signature, gk: Bool, sum: Bool?) -> Assessment { Assessment(signature: s, gatekeeperAccepts: gk, notarized: gk, checksumMatched: sum) }
        XCTAssertEqual(a(.signed, gk: true, sum: nil).verdict(trustUnsigned: false), .ok)
        XCTAssertEqual(a(.signed, gk: true, sum: true).verdict(trustUnsigned: false), .ok)
        if case .blocked = a(.signed, gk: true, sum: false).verdict(trustUnsigned: true) {} else { XCTFail("a mismatched checksum always stops") }
        if case .blocked = a(.invalid, gk: false, sum: nil).verdict(trustUnsigned: true) {} else { XCTFail("a broken signature always stops") }
        if case .confirm(let why) = a(.unsigned, gk: false, sum: nil).verdict(trustUnsigned: false) { XCTAssertTrue(why.joined().contains("isn't signed")); XCTAssertTrue(why.joined().contains("no checksum")) } else { XCTFail() }
        if case .confirm = a(.signed, gk: false, sum: true).verdict(trustUnsigned: false) {} else { XCTFail("signed but not notarized asks") }
        XCTAssertEqual(a(.unsigned, gk: false, sum: nil).verdict(trustUnsigned: true), .ok, "the person said not to ask again about this app")
        XCTAssertTrue(a(.signed, gk: true, sum: true).summary.contains("checksum verified"))
        XCTAssertTrue(a(.invalid, gk: false, sum: false).summary.contains("MISMATCH"))
    }

    // MARK: choosing and naming
    func testTheAppToInstallIsChosenByName() {
        XCTAssertEqual(InstallPlanner.chooseApp(from: ["Only.app", "README.txt", "Applications"], repo: "x"), "Only.app")
        XCTAssertNil(InstallPlanner.chooseApp(from: ["README.txt", "Applications"], repo: "x"))
        XCTAssertEqual(InstallPlanner.chooseApp(from: ["Helper.app", "Pixel Player.app", "Uninstall.app"], repo: "pixel-player"), "Pixel Player.app")
        XCTAssertEqual(InstallPlanner.chooseApp(from: ["Helper.app", "Pixel Player Pro.app"], repo: "pixel-player"), "Pixel Player Pro.app")
        XCTAssertEqual(InstallPlanner.chooseApp(from: ["B.app", "A.app"], repo: "zzz"), "A.app")
        XCTAssertEqual(InstallPlanner.chooseApp(from: [".hidden.app", "Real.app"], repo: "real"), "Real.app")
    }

    func testFileNamesCannotClimbOutOfAFolder() {
        for bad in ["../../etc/passwd", "a/b", "..", "...hidden", "x\ny", ""] {
            let s = InstallPlanner.safeName(bad)
            XCTAssertFalse(s.contains("/") || s.contains("\\") || s.hasPrefix(".") || s.contains("\n") || s.isEmpty, bad)
        }
        XCTAssertEqual(InstallPlanner.safeName("App-1.2.dmg"), "App-1.2.dmg")
    }

    // MARK: records
    func testRecordsSurviveRestartsAndADamagedFileIsKept() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("RS-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent("installs.json")
        let store = InstallStore(url: url)
        let r = InstallRecord(key: "github:fake/app", version: "v1.0", bundlePath: "/Applications/App.app", installedAt: Timestamp.now(), assetName: "App.dmg")
        try await store.set(r)
        let again = await InstallStore(url: url).record(for: "github:fake/app")
        XCTAssertEqual(again, r)
        try await store.remove("github:fake/app")
        let gone = await InstallStore(url: url).all()
        XCTAssertTrue(gone.isEmpty)
        try Data("not json".utf8).write(to: url)
        let fresh = await InstallStore(url: url).all()
        XCTAssertTrue(fresh.isEmpty)
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.appendingPathExtension("damaged").path), "the damaged file is set aside, not deleted")
    }
}

final class AppMatcherTests: XCTestCase {
    let library: [(key: String, repo: String, displayName: String?)] = [
        ("github:rxhanson/rectangle", "Rectangle", nil), ("github:example/pixel-player", "pixel-player", "Pixel Player"), ("github:fake/tool", "tool", nil)]

    func testUpdateAddressesNameTheProject() {
        XCTAssertEqual(AppMatcher.repo(inFeed: "https://github.com/owner/repo/releases.atom").map { "\($0.owner)/\($0.repo)" }, "owner/repo")
        XCTAssertEqual(AppMatcher.repo(inFeed: "https://raw.githubusercontent.com/owner/repo/main/appcast.xml").map { "\($0.owner)/\($0.repo)" }, "owner/repo")
        XCTAssertEqual(AppMatcher.repo(inFeed: "https://owner.github.io/repo/appcast.xml").map { "\($0.owner)/\($0.repo)" }, "owner/repo")
        XCTAssertNil(AppMatcher.repo(inFeed: "http://github.com/owner/repo/releases.atom"), "only https")
        XCTAssertNil(AppMatcher.repo(inFeed: "https://example.test/appcast.xml"))
        XCTAssertNil(AppMatcher.repo(inFeed: "https://github.com/owner"))
        XCTAssertNil(AppMatcher.repo(inFeed: nil))
    }

    func testAnAppIsMatchedToTheLibraryByNameOrByItsFeed() {
        XCTAssertEqual(AppMatcher.match(ScannedApp(name: "Rectangle.app", path: "/Applications/Rectangle.app"), library: library), .library(key: "github:rxhanson/rectangle"))
        XCTAssertEqual(AppMatcher.match(ScannedApp(name: "Pixel Player", path: "/x"), library: library), .library(key: "github:example/pixel-player"))
        XCTAssertEqual(AppMatcher.match(ScannedApp(name: "Whatever", path: "/x", updateFeed: "https://github.com/fake/tool/releases.atom"), library: library), .library(key: "github:fake/tool"))
        if case .feed(let ref)? = AppMatcher.match(ScannedApp(name: "Whatever", path: "/x", updateFeed: "https://github.com/someone/else/releases.atom"), library: library) { XCTAssertEqual("\(ref.owner)/\(ref.repo)", "someone/else") } else { XCTFail() }
        XCTAssertNil(AppMatcher.match(ScannedApp(name: "Unknown App", path: "/x"), library: library))
        XCTAssertNil(AppMatcher.match(ScannedApp(name: "", path: "/x"), library: library))
    }
}
