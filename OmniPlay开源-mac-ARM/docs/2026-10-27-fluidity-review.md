# 流畅性排查与优化建议（2026-10-27）

只看代码不改行为。已按「改动小、收益大」排序，括号里是判据所在。

## 已经做对的事

海报走 `CGImageSourceCreateThumbnailAtIndex` 降采样并在后台队列解码（`PosterManager.swift:38-78`）；滚动期整墙降级
（玻璃垫降平涂、关投影、忽略 hover，`MovieCardView.swift:47/101/105`、`PosterWallView.swift:4880-4906`）；
可用性检查全墙共用一条 15 s 定时器（`MovieCardView.swift:356-361`）；卡片不挂 `@ObservedObject`；网格用
`LazyVGrid`；缩略图解码在 off-main。下面这些是剩下的。

## 1 主题每次访问都重算（收益最大，改动最小）

`AppThemeResolver.theme(colorRaw:styleRaw:)` 没有缓存（`ThemeManager.swift:208-212`），而 12 个视图都写成
`var theme: AppTheme { AppThemeResolver.theme(...) }`——也就是每写一次 `theme.textPrimary` 就重建一整个
`AppTheme`：5 个 `Color(hex:)` 走 `Scanner`，玻璃风格还要再跑 3 次 `blended`（`Color → NSColor →` sRGB 分量
转换）并重建 `LinearGradient` 与光斑数组（`ThemeManager.swift:236-241/298-308`）。访问次数：
`PosterWallView` 81 次、`MovieDetailView` 42 次、`MovieCardView` 7 次——一次首页 body 求值就是上百次整套重建，
滚动起停、hover、进度刷新都会触发。

改法：在 `AppThemeResolver` 里按 `(colorRaw, styleRaw)` 缓存 `AppTheme`（值类型，直接 `static` 字典），
或者干脆把选中的主题塞进 `EnvironmentValues` 由根视图注入，视图改成 `@Environment(\.appTheme)`。

## 2 离线缓存进度：每个数据块都广播一次

远程下载每收到一块数据就往 `@Published var downloadProgress` 写一次（`OfflineCacheManager.swift:448-456`
配合 `:730-743`，本地拷贝那条路径倒是按 bufferSize 限流了）。`PosterWallView` 用
`@ObservedObject var cacheManager` 订阅了整个缓存器（`PosterWallView.swift:369`），于是缓存下载时首页整面 body
会被打到几十次每秒。

改法：进度写回加节流（≥0.2 s 或百分比取整后变化才写）；或把进度搬进一个小 `ObservableObject`，
只有进度徽标视图订阅。

## 3 PosterWallView 是 4961 行、133 个 `@State` 的单视图

任何一个 `@State` 变化都会重算整个 `body`（`PosterWallView.swift:1051` 起约千行），包括 14 个
sheet/popover 声明与 5 个 `onReceive`。

改法：按块拆子视图并把局部状态下沉——继续播放行、标题工具栏、海报网格、分类文件夹列表、管理媒体源、
WebDAV 浏览器各自独立。这一步做完，第 1、2 条的收益也会一起放大。

## 4 CachedPosterView 的通知名在 body 里现算

`onReceive(...Notification.Name("PosterUpdated_\(PosterManager.shared.cacheFileName(for: ...))"))`
（`PosterManager.swift:192`）每次求值都做一遍 `addingPercentEncoding`，并且换了一个 publisher 实例，
SwiftUI 会拆掉重订——每次重绘每张海报一轮摘装。

改法：在 `init` 里把名字算好存 `let`，或改成整墙共用一个 `PosterUpdated` 广播 + 按 key 过滤。

## 5 每张卡片一次数据库读，且 `videoFile` 缺索引

`MovieCardView.refreshCardState()` 在 `onAppear` 和每次 `libraryUpdated` 各读一次
（`MovieCardView.swift:265-290`）。`videoFile` 只有主键和 `idx_videoFile_mediaKind`
（`AppDatabase.swift:193`），`movieId`、`sourceId` 都没索引，`EXPLAIN QUERY PLAN` 是 `SCAN videoFile`。
现在 2761 行，实测 354 次查询共 28 ms（0.08 ms/次）不算痛，但文件数上万会线性变差，
而一次 `libraryUpdated` 就是几十条串行读 + 几十张卡片重绘。

改法：migrator 补 `CREATE INDEX idx_videoFile_movieId ON videoFile(movieId)`、`idx_videoFile_sourceId ON videoFile(sourceId)`；
再把卡片状态改成整墙一次查询、按 `movieId` 分组发下去。

## 6 主线程等 mpv 控制队列

`playbackEngineSnapshot` 用 `mpvControlQueue.sync`（`MPVPlayerManager.swift:215`）。控制队列正在跑
`loadfile`/`seek`（网络挂载尤其慢）时主线程会跟着停住，播放页刚打开那一段最容易感到顿。

改法：0.25 s 轮询把快照缓存成主线程上的普通值，UI 只读缓存；需要即时值的场合改成 async 读。

## 7 PlayerScreen 每 0.25 s 重绘整页

`updatePlaybackState` 写多个 `@Published`（`MPVPlayerManager.swift:1100+`），驱动 3868 行的
`PlayerScreen.body` 每秒重算 4 次。改法：进度条、时间、字幕/音轨菜单拆成子视图，主 body 少观察；
暂停时把轮询降到 1 Hz。

## 8 关闭播放 → 整墙重载

进度落库后广播 `libraryUpdated`（`PlayerScreen.swift:2180` 附近），首页走
`scheduleDebouncedLibraryReload()` → `loadData()` 重取全部条目并重建网格（`PosterWallView.swift:1342-1349`、
`4314-4345`）。`LibraryUpdateUserInfoKey.updatedMovieId` 已经存在，可以走局部刷新。
顺带：`loadData()` 里 4 个独立 `read` 事务可以并成一个。

## 9 热路径上的日志

`MediaLibraryManager` 41 处 `print`，WebDAV 扫描每个目录两条（实测一次扫描上万行 PROPFIND 日志）。
`print` 会走 stdout 锁并写统一日志，扫描期间跟主线程抢 CPU。

改法：加一个默认关闭的详细日志开关（按前缀采样也行），把扫描期的逐目录日志并成汇总。

## 怎么确认收益

同一台机器、同一份库，开液态玻璃 + 一个正在进行的离线缓存任务，用 Instruments 的 SwiftUI
（View Body / View Updates）与 Time Profiler 跑三个场景：首页滚动、首页滚动 + 缓存下载、播放页打开。
先看 `PosterWallView.body` 的求值次数，这个数字最能说明问题。
