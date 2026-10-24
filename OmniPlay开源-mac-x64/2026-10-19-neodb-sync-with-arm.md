# NeoDB 同步同步到 x64 版

日期：2026-10-19　来源：`OmniPlay开源-mac-ARM`（基准版）

方案、接口对照与同步语义见基准版的 [docs/2026-10-19-neodb-sync.md](../OmniPlay开源-mac-ARM/docs/2026-10-19-neodb-sync.md)，本文件只记 x64 侧的落地与验证。

## 改动

从 ARM 版原样拷入，两侧这几个文件保持一致：

- 新增：`OmniPlay/NeodbModels.swift`、`OmniPlay/NeodbService.swift`、`OmniPlay/MediaMarkStore.swift`、`OmniPlay/NeodbSyncService.swift`、`OmniPlay/NeodbSettingsView.swift`、`OmniPlay/NeodbMarkEditorView.swift`、`OmniPlayTests/NeodbSyncTests.swift`
- 修改：`OmniPlay/AppDatabase.swift`（v12 建 `mediaMark` 表，并把该表纳入结构自愈清单）、`OmniPlay/SettingsView.swift`（设置页新增「NeoDB 同步」区块）、`OmniPlay/MovieDetailView.swift`（影视名称右边新增 NeoDB 按钮，点开弹窗编辑标记 / 评分 / 评论）、`OmniPlay/ContentView.swift`（启动后按需同步一次）

工程是 Xcode 16 的同步文件夹分组，新文件不需要改 `project.pbxproj`。

老库升级：v12 建表 + `healSchemaGaps` 兜底，x64 那边 1.7 时代迁移编号错位的老库也会补出 `mediaMark`，不会让详情页写入报外键失败。

## 验证

- `xcodebuild build`（`platform=macOS,arch=x86_64`）通过。
- `xcodebuild test -destination 'platform=macOS,arch=x86_64'` 已把 app 与 `OmniPlayTests.xctest` 全部编译、链接成 x86_64（`-target x86_64-apple-macos14.0`）；但本机 x86_64 测试运行器在启动阶段挂住（Rosetta 环境，历史上 UI target 在本机也起不来），用例执行以 ARM 基准版为准：`OmniPlay开源-mac-ARM` 上 `xcodebuild test -only-testing:OmniPlayTests` 全绿 59 项，含新增的 `NeodbSyncLogicTests`（评分互转、进度推导标记、豆瓣 / TMDB 外链解析、`MarkInSchema` 编码、书架响应解析、实例地址归一化、标题校验）与 `MediaMarkStoreTests`（待同步队列、手工标记不被覆盖、导入落库与进度回填、占位条目不入队）。两侧 `OmniPlay/`、`OmniPlayTests/` 下的 Swift 源码经 `diff -rq` 确认逐字节一致。
- 真机联调需要能访问实例的网络：本机所在网络访问 `neodb.social` / `api.neodb.ai` 不通，未跑线上冒烟。
