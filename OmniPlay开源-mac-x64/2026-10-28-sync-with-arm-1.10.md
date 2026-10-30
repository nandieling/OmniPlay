# 1.10 与 ARM 同步（x64 镜像记录）

日期：2026-10-28　范围：mac x64　基准：ARM 工程 `OmniPlay开源-mac-ARM`（工作区当前状态）

把 10-24 之后落在 ARM 上的改动一次性补齐：`OmniPlay/`、`OmniPlayTests/` 按目录整份覆盖（排除 `.DS_Store`
与两份 entitlements），完成后两份工程的源码逐文件 `cmp` 一致，只剩两份 entitlements 的 Intel 差异。
版本号同步升到 1.10，build 号仍为 1（六个 build configuration 全部改齐）。

本轮带过来的内容：

- 短剧刮削：`ShortDramaSupport.swift`（新增，目录识别与整部入库）、`HongguoService.swift`（新增，红果补齐集数/简介/竖版封面）、
  `MediaLibraryManager.swift`、`DatabaseModels.swift`、`AppDatabase.swift`、`MovieDetailView.swift`、`MovieCardView.swift`、
  `MovieSearchModalView.swift`（豆瓣与红果候选直接绑定）、`PosterWallView.swift`（首页类型筛选：全部影视 / 只看电影 / 只看剧集 / 只看短剧）
- 刮削与图片：`ScrapeFailureTracker.swift`（新增，按目标记失败账本，连续 3 次搁置）、`TMDBService.swift`、
  `PosterManager.swift`、`ThumbnailManager.swift`、`LocalMetadataSidecarStore.swift`、`DoubanService.swift`、
  `DoubanSearch.swift`（新增）、`DoubanBindModalView.swift`
- 直接打开播放收尾：`DirectPlaybackWindowManager.swift`、`DirectFilePlaybackManager.swift`、`OmniPlayApp.swift`、
  `PlayerScreen.swift`、`MPVPlayerManager.swift`、`MPVVideoView.swift`
- 使用教程：`UsageGuideView.swift`（新增，入口在偏好设置标题下方，含 TMDB 反代网关的推荐搭法与 Worker 脚本）、
  `SettingsView.swift`、`Localizable.xcstrings`
- 测试：`OmniPlayTests/ScrapeFailureTrackerTests.swift`、`ShortDramaScrapeTests.swift`（新增）与其余被改动的用例

新增源文件靠 `PBXFileSystemSynchronizedRootGroup` 自动进 target，不需要改 `pbxproj` 的文件清单。

保留的 Intel 专属适配（本轮同样没被 ARM 覆盖）：

- `OmniPlay.entitlements` / `OmniPlay.iCloud.entitlements` 多出 `allow-jit`、`allow-unsigned-executable-memory`、
  `disable-library-validation` 三项。
- `GRDB.swift-master`：GRDB target 锁 `swiftLanguageMode(.v5)`、包声明 `swiftLanguageModes: [.v5, .v6]`，
  `EncodableRecord.swift` / `FetchableRecord.swift` 的 `userInfo` 不做 `unsafeBitCast`。
- `project.pbxproj`：`AppIntents` 走 `OTHER_LDFLAGS` 并带 `-Wl,-no_fixup_chains`、`-Wl,-unaligned_pointers,suppress`；
  `DEVELOPMENT_TEAM = DT95MB3RG4`；不设 `EXCLUDED_ARCHS`。

验证：

- `xcodebuild -configuration Debug -destination 'platform=macOS,arch=x86_64'` 编译通过；产物
  `lipo -info` 为 thin x86_64，`CFBundleShortVersionString` 1.10、`CFBundleVersion` 1，签名带三项 Intel entitlements。
- 单测在 ARM 上跑同一份源码（测试不依赖指令集）：`OmniPlayTests` 111 个用例全绿。
- 随同步一起带过来的两处修复（起因是单测跑出来的 `媒体库升级未完成`）：
  `AppDatabase` 的 v14 迁移先查 `videoFile` 有没有 `mediaKind` 再决定加不加——v13 的结构自愈对老库会先补这一列，
  迁移阶梯不查就重复 `ALTER TABLE`，`duplicate column name: mediaKind` 会把整条阶梯卡在 v14；
  `BusinessLogicIntegrationTests.prepareTestSchema` 末尾改调 `AppDatabase.healSchemaGaps`，夹具与期望结构不再两处维护，
  `ShortDramaScrapeTests.shortDramaTableRoundTrip` 补上旁表外键要的父条目行。
- 自动打包见仓库根 `.github/workflows/macos-release.yml`，两份工程各出一个 zip 与 dmg，包名里的版本号取自产物 Info.plist。

## 10-28 晚间增量：短剧连播的「已播」标记

ARM 上又落了一处播放进度修复，x64 同步覆盖 6 个源文件与 1 份新用例：`PlayerScreen.swift`、
`MPVPlayerManager.swift`、`DatabaseModels.swift`、`MovieCardView.swift`、`MovieDetailView.swift`、
`MediaMarkStore.swift`、`OmniPlayTests/PlaybackFinishedMarkTests.swift`。原因、改法与真机回归见
`OmniPlay开源-mac-ARM/docs/2026-10-28-short-drama-watched-mark.md`。两份工程源码再次逐文件一致，
仍只有两份 entitlements 保留 Intel 差异。

两份工程 `xcodebuild build-for-testing` 均通过，新用例随测试包一起编过；`OmniPlayTests` 在 ARM 上
114 个用例全绿（同一份源码，测试不依赖指令集），真机回归见 ARM 那份记录。
