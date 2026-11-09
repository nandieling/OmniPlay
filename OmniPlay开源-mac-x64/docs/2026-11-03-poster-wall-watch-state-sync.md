# 从 ARM 版同步：首页海报三态观看图标

日期：2026-11-03 ｜ 版本：1.10

以 `OmniPlay开源-mac-ARM` 为准，逐字同步这三个文件：

- `OmniPlay/DatabaseModels.swift`（新增 `PlaybackWatchState.aggregate(files:)`：多文件合成已播/未播完/未播）
- `OmniPlay/MovieCardView.swift`（去掉 `isFullyWatched` 两态缓存，海报下方圆点改三态图标 + 状态文字 tooltip）
- `OmniPlayTests/PosterWallWatchStateTests.swift`（新增 7 例聚合口径单测）

同步前已比对：这两个源文件在 x64 侧没有 ARM 侧没有的独立改动，`diff` 只有本次 ARM 新增内容，
可直接覆盖；`OmniPlayTests` 走 `PBXFileSystemSynchronizedRootGroup`，新测试文件自动进 target。

根因与验证过程见 ARM 版 `docs/2026-11-03-poster-wall-watch-state-icon.md`。
`MARKETING_VERSION` 两边均为 1.10；ARM 按 `arch=arm64`、本工程按 `arch=x86_64` 均 `** BUILD SUCCEEDED **`。
