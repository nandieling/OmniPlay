# 剧集连播：按集序播、只标真正播完的那一集

日期：2026-11-09 ｜ 版本：1.10 ｜ 平台：mac x64（与 ARM 同步）

## 现象

1. 播完一集，没看过的下一集被自动标成「已播」。
2. 连播不按集序走，中间几集既不播也不标。
3. 分集墙上的集号和实际顺序对不上（手工改过季/集也一样）。

## 根因

**结尾值串给了下一集。** 多文件播放表关了 `options/keep-open`，mpv 播完自己切下一条
（`MPVPlayerManager.loadFiles`）。切换那一瞬 `playlist-pos` 与 `filename` 已经是下一集，
下一集还在缓冲读不到时长，而 `playbackEngineSnapshot` 只要读不到时长就把
`PlaybackEndTracker.endedDuration` 当成当前文件的进度发出去——发的是**上一集**的片尾值。
关闭播放窗时 `persistAndStopPlaybackIfNeeded` 按文件名认到下一集，比值正好 1.0，
于是 `applyFinishedPlaybackState` 把一秒没看的下一集盖成已播；下一集库里还没时长时，
连 `duration` 都被写成上一集的长度。整表放完是另一种情形：filename 被清空、
`playlist-pos` 落到 -1，那种情况仍然要按「看到结尾」算，否则最后一集会掉进度。

**区间连坐。** `finishedPlaylistFileIds` 把「上一个索引到当前索引」整段无条件写成已播，
且不看进度。mpv 自己跳掉打不开的条目、或 0.25 秒轮询漏掉一次切换，索引一跳就是好几格，
中间那些没放过的集全部中招。

**播放表和分集墙各排各的。** 播放表用 `playbackSortPrecedes`，季/集只看文件名：
目录里的 `Season 2` 不算数，分集弹窗里手工改过的季/集也不算数。于是两季同名分集被压成
同一季按集号交错（第 1 季第 1 集后面接第 2 季第 1 集）；字幕组命名（`[NC] 有翡 - 01 [1080p].mkv`、
`权力的游戏 1x01.mkv`、`01 - Title.mkv` 都解析不出集号）时，改了集号标签会跟着变、顺序不变。

## 改法

- `PlaybackEndTracker` 记住播完那一刻的条目是谁（`endedFile` / `endedPlaylistPos`），
  只把结尾值交给「还在播当初那个条目」的读取方；换条目时把上一集的时长、进度、结尾状态全部作废。
  `playbackEngineSnapshot` 改走 `endedMark(forCurrentFile:playlistPos:)`。
- `PlayerScreen.resolvedPlaybackFile(from:)` 除索引外再给出「这份快照是不是这个文件的」。
  认不出来的文件名（`bd://`、代理 URL）仍按原样采信，只有能对上播放表里**另一个**条目时才算已切集；
  不属于本文件时关闭不再写它的进度，也不再判已播。
- `shouldMarkPlaylistFileFinished(fileId:justPlayedFileId:storedWatchState:)`：区间里只认引擎刚离开
  的那一条，中间跳过的条目只有库里本来就记着已播才保留。连播切换与关闭两条写库路径共用。
- 集序统一成 `MediaNameParser.episodeAwarePlaybackSortPrecedes` / `sortedForEpisodePlayback`，
  季号优先级为手工修正 > 文件名写死的 `SxxExx` > 目录季号 > 默认第 1 季。播放表两处排序、
  详情页分集墙、分集标题、连播「下一集」全部走这一条，「下一集」用的就是同一份播放表顺序。

## 已知边界

- 同一部剧的文件分散在多个媒体源时，起播仍只把所选文件所在源的文件排进播放表
  （`MovieDetailView.attemptPlayback` 按 `sourceId` 过滤），另一源的集不参与连播。
  这条留着，是因为播放表目前整表共用一份地址与凭据，改成逐文件取源要连 ISO/蓝光/Emby 分支一起动。
- 播放表仍从所选那一集往后截断，符合「从这里连着播」的语义。

## 验证

`OmniPlayTests/EpisodePlaybackOrderTests.swift` 新增 8 例：两季同名分集顺序、文件名季号压过目录、
手工修正真的改变顺序、季号优先级、结尾值不漏给下一条、整表放完仍记已播、远程 URL 与百分号编码
文件名认同一个文件、跳条不连坐。

```
xcodebuild -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Release \
  -destination 'platform=macOS' -derivedDataPath build/derived test \
  -only-testing:OmniPlayTests ENABLE_TESTABILITY=YES
```

`OmniPlayTests` 全绿。`OmniPlayUITests` 里 `testWebDAVPreflightFailureShowsCopyDiagnosticsButton`
在这台机器上本来就过不去（卡在 WebDAV 弹窗的「测试连接」按钮存在性），与本次改动无关。

## 顺带清零的标黄

本工程开了 `SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor`，新增的非隔离排序入口一落地就冒出四处标黄，一并处理：

- `EpisodeMetadataOverrideStore`：`shared`、`override(for:)`、`loadOverrides` 显式留非隔离，
  类标 `@unchecked Sendable`（可变态只有 `UserDefaults`，没有 Swift 侧字段）。
  消掉 `EpisodeMetadataOverrideStore.swift:50` 与 `PlayerScreen.swift:2054` 两处。
- `PersonProfile`、`TMDBFilmographyItem` 改 `nonisolated struct`：GRDB 记录本来就要在
  `dbQueue.read` 的后台闭包里取字段，和 `DatabaseModels.swift` 里 `MediaSource` 等类型同一口径。
- `BusinessLogicIntegrationTests` 里 `CocoaError(.formattingError)` 换成未弃用的 `.formatting`。

改完 x64、ARM 两侧 `xcodebuild test` 均 `** TEST SUCCEEDED **`，编译输出里再没有 `warning:` 行，
`OmniPlayTests` 各 129 例通过。
