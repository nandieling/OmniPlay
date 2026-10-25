// swift-tools-version:5.10
import PackageDescription

// C ABI 导出符号：与 Sources/OmniPlayBridge/Exports.swift 里的 @_cdecl 一一对应。
// Windows 的 DLL 靠 COFF 导出表露名字，而 COFF 只认 .drectve/.def；Swift 的 @_cdecl
// 只保证符号名，不保证进导出表，所以名单要显式交给链接器（抄 box-send 的教训）。
let cdeclExports = [
    "omniplay_create", "omniplay_invoke", "omniplay_free_bytes",
    "omniplay_set_event_callback", "omniplay_version", "omniplay_destroy",
]

let bridgeLinkerSettings: [LinkerSetting] = cdeclExports.map {
    .unsafeFlags(["-Xlinker", "/EXPORT:\($0)"], .when(platforms: [.windows]))
}

let package = Package(
    name: "OmniPlayCore",
    platforms: [.macOS(.v13)],
    // 核心包零依赖，只用 Foundation。GRDB 能不能在 Windows 上用，由 spike-db/ 独立验证，
    // 它红了不影响核心库的构建结论。
    products: [
        // 动态库：Windows 得 omniplay-core.dll，macOS 得 libomniplay-core.dylib。
        // 名字带 -core：Windows 文件名不分大小写，核心库若叫 omniplay.dll 会和界面程序集撞名。
        .library(name: "omniplay-core", type: .dynamic, targets: ["OmniPlayBridge"]),
    ],
    targets: [
        .target(
            name: "OmniPlayCore",
            path: "Sources/OmniPlayCore"
        ),
        .target(
            name: "OmniPlayBridge",
            dependencies: ["OmniPlayCore"],
            path: "Sources/OmniPlayBridge",
            linkerSettings: bridgeLinkerSettings
        ),
        .testTarget(
            name: "OmniPlayCoreTests",
            dependencies: ["OmniPlayCore"],
            path: "Tests/OmniPlayCoreTests"
        ),
    ]
)
