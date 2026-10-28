# 自动刮削失败账本（海报 / 剧照 / 元数据）

日期：2026-10-27

## 现象

缺海报、缺剧照的条目每轮扫描都被反复刮削：整库里凡是海报下载失败或 TMDB 认不出片名的条目，
每次开机自动扫描、每次界面刷新都会重新打一遍请求，网络抖动时能一直空转。

## 根因

三条链路各自都缺「失败次数」这个概念：

1. `PosterManager.downloadPoster(posterPath:)`（PosterManager.swift:115）只在本地文件已存在时跳过，
   失败只是 `print` 一行。而 `CachedPosterView.loadImage()` 每次卡片出现都会再调一次，
   源站 404 的海报等于「每刷一次首页重下一次」。
2. `ThumbnailManager` 早就区分了 `confirmedNoStillFileIDs`（TMDB 确认无剧照，长期跳过）和
   `failedFetchFileIDs`（抓取失败，下次重试），但全库补抓 `enqueueMissingEpisodeThumbnailsAcrossLibrary`
   固定以 `forceRetry: true` 调用 `startBatchWebFetch`（ThumbnailManager.swift:616），
   每轮开机扫描都把两个集一起清空重来，连「确认无剧照」的永久跳过都被抹掉了。
3. `MediaLibraryManager.processUnmatchedFiles`（MediaLibraryManager.swift:1152）对每个仍未匹配的
   代表文件跑完 5 种查询（精准 / 父目录 / 外文 / 清洗名 / 强降级），失败不记账，
   下轮扫描原样再来一遍。

## 方案

新增 `ScrapeFailureTracker`（OmniPlay/ScrapeFailureTracker.swift）：UserDefaults 里存
`[目标键: 失败次数]`（键 `scrapeAutoFailureCounts.v1`），`maxAutoAttempts = 3`，`NSLock` 保护，
可从后台队列直接调用。三类目标键互不影响：

- `poster:<海报缓存文件名>`
- `still:<videoFile.id>`
- `scrape:<代表文件 videoFile.id>`

接入点：

| 位置 | 行为 |
| --- | --- |
| `PosterManager.downloadPoster(posterPath:force:)` | 非 `force` 先查账本，满 3 次直接返回；成功销账，失败记一次 |
| `ThumbnailManager.startBatchWebFetch(tasks:forceRetry:)` | `forceRetry` 销账后放行，否则查账本，满 3 次跳过并汇总一行日志 |
| `ThumbnailManager.applyFetchOutcome` | 成功与「确认无剧照」销账，抓取失败记一次 |
| `resetNoStillMarks` / `removeAssets` | 文件相关记录删除时顺带销账 |
| `enqueueMissingEpisodeThumbnailsAcrossLibrary(orderedMovieIDs:forceRetry:)` | 新增 `forceRetry`，不再写死 `true` |
| `processUnmatchedFiles` | 循环开头查账本并计数跳过，走完 5 轮仍失败记一次 |
| `handleSuccessfulScrape` | 认上条目即销账 |
| `PosterManager.clearCache` | 清缓存等于从头开始，整本清零 |

手动动作绕过账本：

- `PosterWallView.triggerScanAndScrape(...,manual:)` 新增 `manual`。为 true 时先
  `ScrapeFailureTracker.shared.resetAll()`，并把本轮标记存进 `lastScanRunWasManual`，
  链接续扫描（`finishScanRun` 里的 `needsRescanAfterCurrentRun`）沿用同一标记。
  工具栏「扫描刮削」按钮、TMDB 断连弹窗的「重试扫描」、`TriggerScanAndScrape` 通知、
  新增本地文件夹 / 标星 SMB 共享 / 标星 WebDAV 源 / 新增 WebDAV 源 / 重新启用媒体源
  这 8 处全部传 `manual: true`；只有开机 `autoScanOnStartup` 走 `manual: false`。
- `enqueueEpisodeStillBackfill(runID:movieIDs:forceRetry:)` 把该标记透传给剧照补抓，
  自动轮次不再 `forceRetry`，因此也不会再抹掉「TMDB 确认无剧照」。
- 用户主动的海报下载走 `force: true`：豆瓣手动绑定（DoubanBindModalView 两处）、
  红果手动绑定（`HongguoBinding.bind`）、手动重新匹配（MovieSearchModalView）、
  分类文件夹封面（PosterWallView 的 `saveFolderPoster`）。MovieSearchModalView 的剧照
  补抓也改成 `forceRetry: true`。

短剧条目不需要这个闸门：`finalizeShortDramaLocal` 会把整目录标成 `mediaKind = shortDrama`
并置 `isLocked = true`，认不上的短剧本来就不会再进自动队列（`reclassifyExistingShortDramaEntries`
只处理 `mediaKind IS NULL` 的老行，不会解锁重试）。

## 已知取舍

- 计的是「自动轮次里的连续失败」，网络恢复后不会自动补，需要点一次「扫描刮削」。
- 账本按 `videoFile.id` 记，代表文件换了（同目录首集被删）会重新获得 3 次机会。
- 手动扫描是整本清零，不是只清本次涉及的源；代价是多打一轮请求，换来的是「点了就该试」。

## 现网数据佐证

改动的这台机器上 `~/Library/Preferences/nan.OmniPlay.plist` 记录了：

- `ThumbnailTMDBConfirmedNoStillFileIDs` 1075 条 —— 以前每轮开机自动扫描都被 `forceRetry: true` 清空，
  等于 1075 个分集每次重打一遍 TMDB 季度接口。
- `ThumbnailTMDBFailedFetchFileIDs` 58 条，失败原因全是 `TMDB search hit no result`
  —— 认不出片名的稳定失败，以前无限重试，现在自动 3 次后搁置。

## 验证

- `xcodebuild -scheme OmniPlay build` 与 `build-for-testing` 均通过，只剩两条既有测试告警
  （`BusinessLogicIntegrationTests.swift:740` 的 `formattingError` 弃用、
  `ChineseNameTranslationTests` 的 `tmdbId` Sendable）。
- 新增 `OmniPlayTests/ScrapeFailureTrackerTests.swift`（Swift Testing，5 个用例：3 次上限、
  成功销账、超额收敛、三类目标独立、手动清零）。`xcodebuild test` 无头环境跑不起来，
  已用同一份源码在 `/tmp/scrapefail` 编译同样的断言跑通（`✅ 全部断言通过`），
  ⌘U 可复跑。
- 真机验证看日志：放弃时打印 `[OmniPlay][刮削] <key> 自动刮削连续失败 3 次…`；
  剧照补抓行会带上「已放弃 N 个（自动扫描 / 手动扫描，全部重新计数）」；
  元数据行打印「N 个条目已自动失败 3 次，本轮跳过」。
