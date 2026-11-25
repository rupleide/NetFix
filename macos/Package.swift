// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "NetFixMac",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "NetFixMac", targets: ["NetFixMac"])
    ],
    targets: [
        .target(name: "NetFixCore"),
        .executableTarget(name: "NetFixMac", dependencies: ["NetFixCore"]),
        .executableTarget(name: "NetFixChecks", dependencies: ["NetFixCore"])
    ]
)
