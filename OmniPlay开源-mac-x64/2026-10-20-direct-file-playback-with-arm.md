# 直接打开视频文件播放同步到 x64 版

日期：2026-10-20　来源：`OmniPlay开源-mac-ARM`（基准版）

方案、缺口分析与验证清单见基准版的 [docs/2026-10-20-direct-file-playback.md](../OmniPlay开源-mac-ARM/docs/2026-10-20-direct-file-playback.md)，本文件只记 x64 侧的落地。

## 改动

从 ARM 版原样拷入：

- 新增：`OmniPlayTests/DirectPlaybackTests.swift`
- 修改：`OmniPlay/Info.plist`（`CFBundleDocumentTypes`，两侧这份文件现在完全一致）、`OmniPlay/DirectFilePlaybackManager.swift`、`OmniPlay/OmniPlayApp.swift`、`OmniPlay/PosterWallView.swift`、`OmniPlay/SettingsView.swift`

工程是同步文件夹分组，新测试文件不需要改 `project.pbxproj`。

## 验证

- `xcodebuild build`（`platform=macOS,arch=x86_64`）通过。
- 用例执行以 ARM 基准版为准（本机 x86_64 测试运行器启动会挂住，见前几篇记录）：`xcodebuild test -only-testing:OmniPlayTests` 全绿 62 项，含 `DirectFilePlaybackTests`。
- 两侧 `OmniPlay/`、`OmniPlayTests/` 下的 Swift 与 plist 源码经 `diff -rq` 确认一致。
