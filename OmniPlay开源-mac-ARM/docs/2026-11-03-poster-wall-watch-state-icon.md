# 首页海报「已播 / 未播完 / 未播」三态图标

日期：2026-11-03　影响版本：1.10

## 现象

首页海报墙年份右侧那颗圆点只认两档：整部放完显示实心勾，其余一律空心圈。
于是播到一半的片子（例如 2400 秒的片子放到 960 秒）在首页显示成「未播」，
和详情页同一部片报两种状态。详情页早就是三态（`MovieDetailView.swift:1045`），
`MediaMarkStore.markFilesWatched` 的注释里也写着「海报墙的三态图标才会跟上」，
说明海报墙本来就是按三态设计的，只是没实现。

## 根因

`MovieCardView` 用 `@State isFullyWatched` 缓存状态，`refreshCardState()` 里算的是

```swift
let allFilesWatched = !files.isEmpty && files.allSatisfy { file in
    file.duration > 0 && (file.playProgress / file.duration) >= 0.95
}
```

只有「全部放到片尾」为真、其余为假两种取值，图标二选一，`未播完` 这一档在数据层就被丢掉了。
更糟的是这个判定没有走 `PlaybackWatchState`：同一个 0.95 阈值、同样的脏值约定（duration 为 0、
NaN、负数）在卡片和详情页各写了一遍，两边可以随时跑偏。

海报墙主视图的卡片没有进度条兜底——`continueWatchingProgressRatio` 只在
`isContinueWatchingContext` 为真时才返回非 nil（`PosterWallView.swift:521` 主墙传的是默认值 false，
只有「继续观看」区 `PosterWallView.swift:1080/1088` 传 true），所以这颗圆点是首页唯一的观看状态信号，
丢一档就等于是丢了整个「在看」状态。

## 改动

1. `DatabaseModels.swift`：在 `PlaybackWatchState` 里补一条聚合口径，卡片、详情页、标记入口共用。

```swift
static func aggregate(files: [VideoFile]) -> PlaybackWatchState {
    guard files.isEmpty == false else { return .unplayed }
    let states = files.map { PlaybackWatchState(progress: $0.playProgress, duration: $0.duration) }
    if states.allSatisfy({ $0 == .watched }) { return .watched }
    if states.contains(where: { $0 != .unplayed }) { return .inProgress }
    return .unplayed
}
```

   语义：全部放到片尾才算已播，只要有一个文件动过就算未播完。多集剧留一集没动就是未播完。

2. `MovieCardView.swift`：删掉 `isFullyWatched`，改成从 `movieFiles` 直接派生 `cardWatchState`，
   渲染三态图标（`checkmark.circle.fill` / `circle.lefthalf.filled` / `circle`），
   未播用 `textSecondary.opacity(0.5)`，另两档用主题色，tooltip 出 `statusLabel`。
   `toggleWatched()` 的判定同样换成 `PlaybackWatchState.aggregate(files:) != .watched`，
   和显示口径一致，不会出现「显示已播、点一下又写成已播」。

   状态是计算属性而不是 `@State` 缓存，所以只要 `movieFiles` 刷新，图标必然跟着变；
   刷新时机没变：`onAppear`、`.libraryUpdated`、缓存管理器的 `cachedFileKeys`/`cachedFileNames`、
   共享的 15 秒 `PosterWallAvailabilityTicker`。播放收尾时 `PlayerScreen` 在写库之后
   广播 `.libraryUpdated`（`PlayerScreen.swift:2138` / `2205` / 连播的 `3857`），
   卡片在用户回到首页前就已经重算过了。

## 验证

`OmniPlayTests/PosterWallWatchStateTests.swift`（7 例，随 `PBXFileSystemSynchronizedRootGroup` 自动进 target）：

| 用例 | 输入 | 期望 |
| --- | --- | --- |
| `testNoFilesIsUnplayed` | 无文件 | 未播 |
| `testPartiallyPlayedFileReadsAsInProgress` | 960/2400 | 未播完（`statusLabel` == 未播完） |
| `testEveryFileFinishedReadsAsWatched` | 两个文件都到片尾 | 已播 |
| `testMixedEpisodesReadAsInProgress` | 一集放完 + 一集没动 | 未播完 |
| `testJustUnderThresholdIsNotWatched` | 900/1000 | 未播完 |
| `testUnknownDurationWithProgressIsInProgress` | 42/0 | 未播完 |
| `testDirtyValuesFallBackToUnplayed` | progress ∈ {-10,0,NaN} × duration ∈ {-1,0,NaN,∞} | 未播 |

ARM `xcodebuild test -only-testing:OmniPlayTests` 122 例全绿；x64 同步后两工程 `xcodebuild build`
分别按 `arch=arm64`、`arch=x86_64` 均 `** BUILD SUCCEEDED **`。

## 教训

同一套业务判定只要出现在两个文件里，就一定会漂移。观看状态此前有四份实现
（卡片、详情页、`MediaMarkStore`、播放收尾），这次把「多文件合成一个状态」这一层也收进
`PlaybackWatchState`，新增页面直接调 `aggregate(files:)`，别再就地写 0.95。
