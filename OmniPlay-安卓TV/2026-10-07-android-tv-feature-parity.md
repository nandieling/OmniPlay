# 2026-10-07 安卓 TV 版功能对齐 ARM mac 版（versionName 1.8 / versionCode 19）

## 目标

TV 版此前只有独立媒体引擎 + 海报墙 + 播放器骨架，独立版设置、刮削深度信息、播放偏好、离线缓存都缺。本次以 `OmniPlay开源-mac-ARM` 为准移植功能，交互按遥控器 D-pad 重做：不复制 macOS 的侧栏 + 网格布局，全部改成「顶部工具栏 → 覆盖层面板 → 单列/网格焦点链」的 TV 形态。

## 新增能力

### 设置与偏好（`local/TvSettings.java`，新增）

SharedPreferences（`omniplay_tv_local`）承载 mac 版「偏好设置」等价项：

- `auto_scan_on_startup` / `auto_scrape_after_scan`：启动自动扫描、扫描后自动刮削，`MainActivity.onCreate` 里通过 `LocalEngine.scanAndScrape(...)` 触发，只跑一次（`startupSyncStarted`）。
- `show_episode_details` / `show_real_source_path`：详情页分集简介与进度、真实源文件路径。
- `default_audio_language` / `default_subtitle_language`：`applyDefaultPlaybackTracks` 在起播前选中音轨与字幕轨，字幕选「关闭」时 `loadDefaultSubtitle` 直接返回。
- `playback_quality_mode`：流畅 / 均衡 / 画质优先，`MpvPlayer.setQualityProfile` 映射为 `demuxer-max-bytes`、`demuxer-max-back-bytes`、`demuxer-readahead-secs`（64MiB/16MiB/10s、128MiB/32MiB/20s、512MiB/128MiB/60s）；`MpvVideoView` 缓存为 `pendingQualityProfile`，在 `applyPendingTracks()` 里统一应用，避免 mpv 实例未就绪时丢设置。
- `scrape_language`：zh-CN / en-US，`TmdbClient.language()` 与 `imageLanguage()` 共用，所有 TMDB 请求与图片语言一起跟随。
- `offline_cache_dir`：默认 `<外置存储>/OmniPlayCache`。

`toAppSettings()` 输出 `Models.AppSettings.withPlayback(...)`，与服务端模式的 `AppSettings` 共用一套结构。

### 数据库 v1 → v2（`local/LocalMediaLibrary.java`）

- `movie` / `tvShow` 增列 `tmdb_id`、`genres`、`directors`、`casts`；`videoFile` 增列 `episode_subtitle`。
- 新表 `tvEpisode`（主键 showId+season+episode，存标题/简介/首播日期/剧照）与 `offlineCache`（主键 fileId，存 localPath/sourceUrl/bytes）。
- 迁移用 `addColumnIfMissing`（读 `PRAGMA table_info`）+ 全量 `CREATE TABLE IF NOT EXISTS`，只加列建表，不动已有数据；`removeMissingFiles` 同步清理 `offlineCache` 行。
- 新增 `setEntityCredits`、`renameEntity`（改名同时置 `is_locked`，后续刮削不覆盖用户命名）、`requeueScrape`、`entity`、`upsertEpisodeMetadata`、`episodeMetadata`、`setEpisodeSubtitle`、`saveCacheRecord` / `allCacheRecords` / `removeCacheRecord`。

### 刮削（`local/TmdbClient.java`）

- 影片/剧集匹配成功后追加一次 `/{movie|tv}/{id}?append_to_response=credits`，取回类型、导演、主演写入库中（`applyTitleDetails`）。
- 分集详情 `episodeDetail(tvId, season, episode)`：返回 `tmdbAnswered`（TMDB 是否明确应答，用于区分「确实没有」和「网络失败」，后者下轮重试）、标题、简介、首播日期、剧照。
- 剧照季映射沿用 mac 版规则：本季节 → 第 1 季 → 集数最多的季；仍无剧照时改查 `/images?include_image_language=…`，按 `vote_average + vote_count * 0.1` 择优。
- `getOrNull(url, statusOut)` 返回 HTTP 状态码而不抛异常，404 与超时能分开处理。
- `LocalEngine.needsEpisodeMetadata` + `scrapeEpisodeMetadata` 做按需拉取，`episodeScrapeInFlight` 去重，详情页用 `episodeMetaAttempts` 限制重试次数。

### 媒体源管理

新增覆盖层 `openSourcesOverlay` / `renderSourcesOverlay`：每个媒体源一行，提供启用/停用、重命名、移除，底部是添加媒体源、重新扫描并刮削、清空媒体库索引。重命名用内联输入框（`openRenameSourceDialog` + `applyDpadRows`），保存走 `LocalEngine.renameSource`。面板注册为单列焦点链，返回键关闭并恢复首页焦点（`isSourcesOpen` 加入 back 与 `dispatchKeyEvent` 层级判断）。`LocalPlayApi.disconnect()` 不再顺手清空媒体库，清空改由显式的 `wipeLibrary()`。

### 首页与详情页 UI（TV 交互重做）

- 顶部工具栏：搜索、排序、升降序、筛选、媒体源、设置。搜索框移到工具栏下方、「继续播放」之前，与列表同级。
- 筛选行：全部/电影/剧集 + 全部/未观看/已观看，`filteredItems()` 先筛后搜再排序（`libraryComparator`）。
- 状态横幅 `homeStatusView`：扫描/刮削状态、待刮削数量、离线缓存进度（`refreshHomeStatusText` + `OfflineCacheManager.Listener`）。
- 「继续播放」改为横向卡片行（`HorizontalScrollView` + 复用 `posterCard`），避免占用整屏网格。
- 详情页：新增类型/导演/主演信息行；电影页「缓存本片」，季头「缓存本季 N 集」，分集卡片「缓存本集」与「副标题」；「编辑」走通用文本输入覆盖层 `openTextInputOverlay`，保存后锁定命名并重新刮削。
- 设置面板宽度 dp(360) → dp(430)，新增区块全部用 `extraRows` 注册进焦点链；面板内跳转（如改缓存目录后刷新状态）用 `restartSettingsOverlay()` 重渲染。

### 离线缓存（`local/OfflineCacheManager.java`，新增）

- 串行队列，`enqueue(files, groupLabel)` 返回错误文案或 null；已在缓存中、`file://` 本地源、不支持的协议直接跳过并在文案中说明。
- 下载写 `<原名>-<hash>.part`，成功后改名并 `saveCacheRecord`；失败删除 `.part`。
- 容量预检：HEAD 或 SMB 文件长度取期望大小，可用空间需 ≥ 期望大小 + 1 GiB（`RESERVED_FREE_BYTES`）。
- WebDAV 用 URL userinfo 重建 Basic 认证（`java.util.Base64`），SMB 走 `LocalEngine.smbFile(url)` → `SmbClient.open(absoluteUrl)`。
- `LocalEngine.toVideoFile` 优先返回 `file://<缓存副本>`，缓存对播放链路透明；设置页展示已缓存数量、占用与目录剩余空间，可清空。

## 与 mac 版的已知差距

- 未移植 Plex / Emby / Jellyfin 媒体服务器接入、Lucky STUN 界面、豆瓣源、人物（导演/主演）浏览页与分类文件夹。
- 未移植中文名补全（`ChineseNameStore`）与本地缩略图缓存目录体系；TV 版分集剧照直接用 TMDB 图片 URL 走 `ImageLoader` 的 URL 磁盘缓存。
- 手机界面（`PhoneMainActivity`）未同步本次 UI 改造，仍是原触摸布局。
- TV 版新增能力集中在独立版链路；Docker 服务端模式（`OmniPlayApi`）行为未变。

## 验证

- 快速编译：`javac` 全量编译 app 源码（`android-35/android.jar` + 缓存的 `jcifs-ng-2.1.10.jar` + 桩 `R.java`），`DONE status=0`。
- 完整构建：`JAVA_HOME=<Temurin 17> GRADLE_USER_HOME="$PWD/.gradle" ./gradlew assembleRelease --offline` → BUILD SUCCESSFUL；`aapt2 dump badging` 显示 `versionCode='19' versionName='1.8'`，产物 `app/build/outputs/apk/release/app-release.apk`。
- 迁移验证：用 `sqlite3` 构造 v1 库跑 `ALTER` + `CREATE TABLE IF NOT EXISTS` 序列，`MIGRATION_OK`。
- 未做设备端实机验证（无连接设备），遥控器焦点顺序与缓存下载需在真盒上复测。
