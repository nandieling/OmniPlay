// swift-tools-version:5.10
import PackageDescription

// GRDB on Windows 的定点探针。这是整座移植计划里唯一「必须先知道答案才能排期」的未知项：
// 它靠 SQLite C + WAL + 自定义 Collator，而这些在 Windows 上的 Swift 支持没人替我们保证。
// 单独一个包，是为了让它的失败不波及核心库的构建结论。
let package = Package(
    name: "OmniPlayDbSpike",
    platforms: [.macOS(.v13)],
    dependencies: [
        .package(url: "https://github.com/groue/GRDB.swift.git", from: "7.11.0"),
    ],
    targets: [
        .executableTarget(
            name: "db-spike",
            dependencies: [.product(name: "GRDB", package: "GRDB.swift")],
            path: "Sources/DbSpike"
        ),
    ]
)
