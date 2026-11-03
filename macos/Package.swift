// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "DiscImageStudioMac",
    defaultLocalization: "zh-Hans",
    platforms: [
        .macOS(.v14),
    ],
    products: [
        .executable(name: "DiscImageStudioMac", targets: ["DiscImageStudioMac"]),
        .executable(name: "disc-burn-stream", targets: ["DiscBurnStream"]),
        .library(name: "DiscImageStudioKit", targets: ["DiscImageStudioKit"]),
    ],
    targets: [
        .target(name: "DiscImageStudioKit"),
        // Objective-C 写成的 DiscRecording 流式刻录助手（DRTrack producer + DRBurn）。
        .executableTarget(
            name: "DiscBurnStream",
            path: "Sources/DiscBurnStream",
            cSettings: [.unsafeFlags(["-fobjc-arc"])],
            linkerSettings: [
                .linkedFramework("Foundation"),
                .linkedFramework("DiscRecording"),
            ]
        ),
        .executableTarget(
            name: "DiscImageStudioMac",
            dependencies: ["DiscImageStudioKit"]
        ),
        .testTarget(
            name: "DiscImageStudioKitTests",
            dependencies: ["DiscImageStudioKit"],
            resources: [.copy("Fixtures")]
        ),
    ],
    swiftLanguageModes: [.v6]
)
