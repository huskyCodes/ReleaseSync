// swift-tools-version: 5.10
import PackageDescription

// The part of the Mac and iPhone apps that has nothing to do with the screen: the sync file and its rules.
// It follows docs/SYNC-FORMAT.md and is tested against the files in ReleaseSync.Core.Tests/Fixtures.
let package = Package(
    name: "ReleaseSyncKit",
    platforms: [.macOS(.v13), .iOS(.v16)],
    products: [
        .library(name: "ReleaseSyncKit", targets: ["ReleaseSyncKit"]),
    ],
    targets: [
        .target(name: "ReleaseSyncKit"),
        .testTarget(name: "ReleaseSyncKitTests", dependencies: ["ReleaseSyncKit"]),
    ]
)
