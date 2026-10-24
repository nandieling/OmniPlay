# 直开播放首轮修复同步到 x64 版

日期：2026-10-20　来源：`OmniPlay开源-mac-ARM`（基准版）

根因分析、改动清单与真机验证见基准版的 [docs/2026-10-20-direct-playback-first-round-fixes.md](../OmniPlay开源-mac-ARM/docs/2026-10-20-direct-playback-first-round-fixes.md)，本文件只记 x64 侧的落地。

## 改动

从 ARM 版原样拷入：

- 修改：`OmniPlay/MPVPlayerManager.swift`（`keep-open=yes` + `PlaybackEndTracker`）、`OmniPlay/DirectPlaybackWindowManager.swift`（全屏与关闭时序重写）、`OmniPlay/DirectFilePlaybackManager.swift`（去掉开播时的 `.libraryUpdated`）、`OmniPlay/PlayerScreen.swift`（独立窗口不再被 `ensureComfortableWindowSize()` 改尺寸）
- 修改：`OmniPlayTests/DirectPlaybackTests.swift`（新增 `PlaybackEndTrackerTests`）

## 验证

- `xcodebuild build`（`platform=macOS,arch=x86_64`）通过。
- 用例执行以 ARM 基准版为准（本机 x86_64 测试运行器启动会挂住，见前几篇记录）：`xcodebuild test -only-testing:OmniPlayTests` 全绿 66 项，含 `PlaybackEndTrackerTests` 5 项。
- 两侧 `OmniPlay/`、`OmniPlayTests/` 下的 Swift 与 plist 源码经 `cmp` 确认一致。
