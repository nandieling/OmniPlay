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
} + [
    // 出符号表（和 DLL 同名的 .map，列的是「偏移 符号 段」）。用户机器上只能拿到
    // 「omniplay-core.dll+0xF8770」这样一个偏移，有这张表就能直接查到是哪个函数。
    .unsafeFlags(["-Xlinker", "/MAP"], .when(platforms: [.windows])),
]

let package = Package(
    name: "OmniPlayCore",
    platforms: [.macOS(.v13)],
    // 核心包不拉外部 Swift 依赖：Foundation + 仓库自带的 SQLite。
    // GRDB 能不能在 Windows 上用由 spike-db/ 独立验证过（结论：编不过，见 docs/SPIKE.md §1），
    // 所以库这一层自己写，SQLite 用官方合并源。
    products: [
        // 动态库：Windows 得 omniplay-core.dll，macOS 得 libomniplay-core.dylib。
        // 名字带 -core：Windows 文件名不分大小写，核心库若叫 omniplay.dll 会和界面程序集撞名。
        .library(name: "omniplay-core", type: .dynamic, targets: ["OmniPlayBridge"]),
    ],
    targets: [
        // 自带的 SQLite：Windows 没有系统 sqlite3 可链，mac 也统一用这一份，
        // 两个平台跑的是同一个 SQLite 版本，库行为不会各走各的。
        .target(
            name: "CSQLite",
            path: "Sources/CSQLite",
            publicHeadersPath: "include",
            cSettings: [
                .define("SQLITE_THREADSAFE", to: "1"),
                .define("SQLITE_OMIT_LOAD_EXTENSION", to: "1"),
                .define("SQLITE_OMIT_DEPRECATED", to: "1"),
                .define("SQLITE_DEFAULT_MEMSTATUS", to: "0"),
                .define("SQLITE_OMIT_PROGRESS_CALLBACK", to: "1"),
                .define("SQLITE_OMIT_TCL_VARIABLE", to: "1"),
            ]
        ),
        .target(
            name: "OmniPlayCore",
            dependencies: ["CSQLite"],
            path: "Sources/OmniPlayCore",
            exclude: ["Database/README.md"],
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
