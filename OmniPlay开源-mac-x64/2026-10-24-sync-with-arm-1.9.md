# 1.9 与 ARM 同步（x64 镜像记录）

日期：2026-10-24　范围：mac x64　基准：[ARM 记录](../OmniPlay开源-mac-ARM/docs/2026-10-24-playback-resilience-and-neodb-record.md)

把 10-21 之后落在 ARM 上的改动一次性补齐，本目录 `OmniPlay/`、`OmniPlayTests/` 与 ARM 逐文件 `cmp` 一致，版本号同步升到 1.9。内容涉及：主题落点与玻璃对齐、NeoDB「记录」与脏标记语义和条目页跳转、设置页文案与输入框对齐、SMB 直连媒体源、蓝光包合并成一个版本、`DatabasePool` 与退出落盘、全屏/尺寸切换的视频输出重建与音画对齐、播放页菜单自动隐藏、图层未绑定时的先行加载与起播计时日志。

镜像文件（29 个）：

- 界面与主题：`ThemeManager.swift`、`PosterWallView.swift`、`MovieDetailView.swift`、`MovieCardView.swift`、`PlayerScreen.swift`、`SettingsView.swift`、`NeodbSettingsView.swift`、`NeodbMarkEditorView.swift`、`OpenSourceInfoView.swift`、`PlainSingleLineField.swift`（新增）
- 播放链路：`MPVPlayerManager.swift`、`MPVVideoView.swift`、`DirectFilePlaybackManager.swift`
- 数据与同步：`AppDatabase.swift`、`DatabaseModels.swift`、`MediaMarkStore.swift`、`NeodbModels.swift`、`NeodbService.swift`、`NeodbSyncService.swift`、`OmniPlayICloudSync.swift`、`MediaLibraryManager.swift`、`ChineseNameStore.swift`、`ThumbnailManager.swift`
- 媒体源与本地化：`SMBSourceManager.swift`（新增）、`Localizable.xcstrings`
- 测试：`OmniPlayTests/BusinessLogicIntegrationTests.swift`、`DirectPlaybackTests.swift`、`NeodbSyncTests.swift`
- 其他：`docs/smb_poc_check.sh`

两处文件本轮由 ARM 反向补齐到 x64 工程配置：共享 scheme `OmniPlay.xcodeproj/xcshareddata/xcschemes/OmniPlay.xcscheme`（原先只有 `xcuserdata`，命令行 `-scheme` 与 CI 需要共享 scheme）、`INFOPLIST_KEY_NSLocalNetworkUsageDescription`（SMB/WebDAV 局域网访问权限说明）。新增的两个源文件靠 `PBXFileSystemSynchronizedRootGroup` 自动进 target，不需要改 `pbxproj`。

保留的 Intel 专属适配（**不要**被 ARM 覆盖）：

- `OmniPlay.entitlements` / `OmniPlay.iCloud.entitlements` 多出 `allow-jit`、`allow-unsigned-executable-memory`、`disable-library-validation` 三项。
- `GRDB.swift-master`：GRDB target 锁 `swiftLanguageMode(.v5)`、包声明 `swiftLanguageModes: [.v5, .v6]`，`EncodableRecord.swift` / `FetchableRecord.swift` 的 `userInfo` 不做 `unsafeBitCast`。
- `project.pbxproj`：`AppIntents` 走 `OTHER_LDFLAGS` 并带 `-Wl,-no_fixup_chains`、`-Wl,-unaligned_pointers,suppress`；`DEVELOPMENT_TEAM = DT95MB3RG4`；不设 `EXCLUDED_ARCHS`。

验证：`xcodebuild -configuration Release -destination 'platform=macOS,arch=x86_64'` 编译通过，产物 `lipo -info` 为 thin x86_64、ad-hoc 签名带 hardened runtime、Info.plist 含局域网权限说明；单测在 ARM 上跑（同一份源码，测试不依赖指令集）。自动打包见仓库根 `.github/workflows/macos-release.yml`，两份工程各出一个 zip 与 dmg。

补记：修掉 CoreAudio 设备热插拔导致的意外退出后（`MPVPlayerManager.swift` 里把 mpv 音频输出改成 `ao=avfoundation,coreaudio`，原因见 ARM 工程 `docs/2026-10-24-playback-resilience-and-neodb-record.md` 第 8 节），两份工程重新各编译一次，均 `BUILD SUCCEEDED`。

再补记：修完 x64 首页滚动卡顿与回弹（`PosterWallView.swift`、`MovieCardView.swift`、`ThemeManager.swift`，原因见 ARM 工程 `docs/2026-10-24-playback-resilience-and-neodb-record.md` 第 9 节）与播放条玻璃条去染色、再上移（`PlayerScreen.swift`）后，两份工程又各编译一次，均 `BUILD SUCCEEDED`；ARM 单测 80 个用例全绿。
