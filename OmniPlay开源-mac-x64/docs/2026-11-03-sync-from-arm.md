# 从 ARM 版同步：`.sup` 蓝光字幕挂载修复

日期：2026-11-03 ｜ 版本：1.10

以 `OmniPlay开源-mac-ARM` 为准，逐字同步这三个文件：

- `OmniPlay/PGSSubtitleWrapper.swift`（新增：把 `.sup` 里的 PGS 段重新封装成只含字幕流的 MPEG-TS）
- `OmniPlay/MPVPlayerManager.swift`（同名外挂字幕扫描、`.sup` 后台封装后 `sub-add`、手动挂载走同一条链路）
- `OmniPlay/PlayerScreen.swift`（播放页「挂载字幕」面板放行 `.sup`/`.idx` 等格式）

根因与验证过程见 ARM 版 `docs/2026-11-03-sup-pgs-subtitle-mount.md`。
两边工程均已通过 `xcodebuild` 编译（arm64 / x86_64，Debug）。
