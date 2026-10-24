import XCTest
@testable import ReleaseSyncKit

final class MacAssetsTests: XCTestCase {
    func assets(_ names: String...) -> [AssetInfo] { names.map { AssetInfo(name: $0, url: "https://example.test/" + $0) } }

    func testAppleSiliconPrefersItsOwnBuildThenUniversal() {
        let list = assets("App-1.0-x64.dmg", "App-1.0-arm64.dmg", "App-1.0-universal.dmg", "App-1.0-setup.exe", "App-1.0.AppImage", "App-1.0.tar.gz")
        XCTAssertEqual(MacAssets.pick(list, arch: .arm64)?.name, "App-1.0-arm64.dmg")
        XCTAssertEqual(MacAssets.pick(list, arch: .x64)?.name, "App-1.0-x64.dmg")
        XCTAssertEqual(MacAssets.choices(list, arch: .arm64).map(\.name), ["App-1.0-arm64.dmg", "App-1.0-universal.dmg", "App-1.0-x64.dmg"])
    }

    func testUniversalBeatsAnUnlabelledFile() {
        XCTAssertEqual(MacAssets.pick(assets("App.dmg", "App-universal.dmg"), arch: .arm64)?.name, "App-universal.dmg")
    }

    func testAnArm64OnlyBuildIsNeverOfferedOnIntel() {
        XCTAssertNil(MacAssets.pick(assets("App-arm64.dmg"), arch: .x64))
        XCTAssertEqual(MacAssets.pick(assets("App-x64.dmg"), arch: .arm64)?.name, "App-x64.dmg", "Intel builds run on Apple silicon")
    }

    func testDiskImagesBeatInstallersBeatZips() {
        XCTAssertEqual(MacAssets.pick(assets("App-macos.zip", "App-macos.pkg", "App-macos.dmg"))?.name, "App-macos.dmg")
        XCTAssertEqual(MacAssets.pick(assets("App-macos.zip", "App-macos.pkg"))?.name, "App-macos.pkg")
    }

    func testOtherSystemsSourceAndChecksumsAreLeftOut() {
        let list = assets("App-linux-x64.zip", "App-windows-x64.zip", "App-win64.zip", "App-1.0-src.zip", "App-source.zip", "App.dmg.sha256", "App.dmg.sig", "App-debug-symbols.zip", "App.AppImage", "App.deb")
        XCTAssertTrue(MacAssets.choices(list).isEmpty)
        XCTAssertNil(MacAssets.pick(list), "nothing here installs on a Mac")
        XCTAssertFalse(MacAssets.looksLikeMac(list))
    }

    func testAPlainZipNeedsMacInItsNameToCount() {
        XCTAssertFalse(MacAssets.looksLikeMac(assets("App-1.0.zip")))
        XCTAssertTrue(MacAssets.looksLikeMac(assets("App-1.0-macos.zip")))
        XCTAssertTrue(MacAssets.looksLikeMac(assets("App-1.0-osx-arm64.zip"), arch: .arm64))
        XCTAssertTrue(MacAssets.looksLikeMac(assets("Setup.dmg")))
        XCTAssertTrue(MacAssets.looksLikeMac(assets("App.pkg")))
        XCTAssertFalse(MacAssets.looksLikeMac(assets("App-arm64.dmg"), arch: .x64))
        XCTAssertFalse(MacAssets.looksLikeMac(nil))
    }

    func testAFileStillUploadingIsSkipped() {
        var a = AssetInfo(name: "App.dmg", url: "x"); a.state = "uploading"
        XCTAssertNil(MacAssets.pick([a]))
    }

    func testASavedPatternWinsWhenItMatches() {
        let list = assets("App-1.0-arm64.dmg", "App-1.0-arm64.pkg", "App-1.0-universal.zip")
        XCTAssertEqual(MacAssets.pick(list, pattern: #"\.pkg$"#, arch: .arm64)?.name, "App-1.0-arm64.pkg")
        XCTAssertEqual(MacAssets.pick(list, pattern: "UNIVERSAL", arch: .arm64)?.name, "App-1.0-universal.zip", "ignores case")
        XCTAssertEqual(MacAssets.pick(list, pattern: "nothing matches this", arch: .arm64)?.name, "App-1.0-arm64.dmg", "falls back to the best one")
        XCTAssertEqual(MacAssets.pick(list, pattern: "([", arch: .arm64)?.name, "App-1.0-arm64.dmg", "a broken pattern is ignored")
    }

    func testKinds() {
        XCTAssertEqual(MacAssets.kind(of: "A.DMG"), .dmg); XCTAssertEqual(MacAssets.kind(of: "a.pkg"), .pkg); XCTAssertEqual(MacAssets.kind(of: "a.zip"), .zip); XCTAssertEqual(MacAssets.kind(of: "a.exe"), .unknown)
    }
}
