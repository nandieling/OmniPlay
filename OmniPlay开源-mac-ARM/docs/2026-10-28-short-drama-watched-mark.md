# 短剧连播不漏标「已播」（1.10）

日期：2026-10-28　范围：mac ARM / x64　影响文件：`PlayerScreen.swift`、`MPVPlayerManager.swift`、
`DatabaseModels.swift`、`MovieCardView.swift`、`MovieDetailView.swift`、`MediaMarkStore.swift`

## 现象

短剧连播时，一集看完切到下一集，回头看详情页那一集还是「未播」，得手动点一次已看。整部 800 集的短剧里
只有零星几集被记成已播，越是刚看的越容易丢。

## 根因

库里判断已看与否只看 `videoFile` 的两个字段：`PlaybackWatchState(progress:duration:)` 要求
`duration > 0` 且 `playProgress / duration >= 0.95`，而 `duration` 与 `playProgress` 同时为 0 就是「没播过」。

`duration` 只有在关播放窗写进度时才会落到 `videoFile` 上，扫描入库不填。短剧按目录整部入库，实测线上库
800 个文件里 601 个 `duration = 0`。

自动连播的那条标记路径 `PlayerScreen.persistFinishedPlaylistFiles(_:)` 只带文件 id，不带引擎观察到的时长，
它调的 `applyFinishedPlaybackState` 当时是这么算的：

```swift
let resolvedDuration = max(file.duration, max(observedDuration, observedProgress))
file.duration = resolvedDuration      // 0
file.playProgress = resolvedDuration  // 0 —— 又写回「没播过」
```

于是「播完」在库里的形状和「没播过」一模一样，标记当场蒸发。关窗那一刻的那一集反而能标上，因为那条路径
带上了 mpv 的真实时长——这正好解释了「只有零星几集记上了」。手动标记、导入回填那两条路径早就各自写了
`duration > 0 ? duration : 100` 的兜底，只有播放这条漏了。

顺带还有一个同源问题：mpv 把整个播放列表放完后 `playlist-pos` 变成 -1，`MPVPlayerManager` 用
`max(0, ...)` 夹成 0，`PlayerScreen` 就把「当前条目」当成列表第一项——最后一集既不会被标已看，关窗时进度还会
写到列表第一部那集身上。

## 改动

- `PlaybackWatchState` 收下这套约定：`unknownDurationWatchedMark = 100` 与 `finishedMark(duration:observed:)`，
  结果恒为正。手动标记（海报卡片、详情页分集）、导入回填（`MediaMarkStore.markFilesWatched` 的 SQL）
  与播放自动标记四处共用同一份，不再各写各的字面量。
- `PlayerScreen.applyFinishedPlaybackState` 改走 `finishedMark`：拿到过真实时长就顶到片尾，没拿到记 100 秒。
- `MPVPlayerManager` 原样透出 `playlist-pos`（含 -1），`playbackEngineSnapshot.playlistIndex` 同理；
  `PlayerScreen` 的 `.onChange(of: currentPlaylistIndex)` 本来就用 `allPlaylistFiles.indices.contains(newIndex)`
  挡越界，-1 会直接跳过，`currentVideoFileId` 不再被认错，关窗写库也就写对了文件。

## 验证

单元用例：

- 新增 `OmniPlayTests/PlaybackFinishedMarkTests.swift`：时长为 0 的文件标完必须读回 `.watched`；
  引擎报过 118.96 秒时不许写兜底值；已知时长顶到片尾；脏值（负数、0、NaN、inf）都不能写成 0。
- `xcodebuild test -scheme OmniPlay -destination 'platform=macOS,arch=arm64' -only-testing:OmniPlayTests`：
  114 个用例全绿（10-30 复跑；GUI 宿主测试在屏幕锁定时挂不上运行器，得解锁后再跑）。

真机回归（1.10 Debug 包，短剧《持械入宋》本地源，逐条对照库里 `videoFile`）：

- 看一集 `duration/progress` 都是 0 的：放完自动切下一集后，该集写成 100/100，详情页转「已播」，
  正在播的下一集仍留 0/0——修复前这一集永远标不上。
- 中途关窗：该集记下引擎给的真实时长与断点（103.4/58.8 + `lastPlayedAt`），没被兜底值污染。
- 整个播放列表放完（最后两集 E99→E100）：E99 在切集时标成 100/100；E100 放完后关窗，写的是它自己的
  98.1/98.1，E99 没被改写——即 `playlist-pos = -1` 不再被夹成 0 认错文件。

## 已知边界

- 历史数据补不回来：这次修复之前被写成 0/0 的行，与从没播过的行在库里不可区分，不做猜测式回填。
- 时长未知时记的 100 秒只是「已看」的载体，界面不显示文件总时长；这一集重新播完一次就会被真实时长覆盖。
