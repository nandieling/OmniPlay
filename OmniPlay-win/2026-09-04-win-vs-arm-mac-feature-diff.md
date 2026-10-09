# 2026-09-04 Windows 版与 ARM mac 版功能差异（移植前扫描）

## 背景

- 以 `OmniPlay开源-mac-ARM`（v1.7，源码 2026-08-31）为基准，扫描 `OmniPlay-win`（v1.5，dist 2026-08-06）。
- Win 版为 C# .NET 10 + Avalonia 11 + libmpv 重写（四层：Core / Infrastructure / UI / Desktop / Player.Mpv），不是 Swift 翻译，移植需按功能落到 MVVM 对应层。
- Win 功能代际与"同步前的 x64 mac"同期（2026-05-06 批次），落后 ARM 约 5 周、2 个版本号。

## 一、Win 缺失的功能（ARM 有）

### 1. Lucky STUN 按源穿透媒体源（最大缺口）
- ARM：`LuckyStunSourceConfiguration`（按源：managementURL / 账号 / 密码 / ruleID / ruleName / displayName / autoUpdate / updateIntervalMinutes / pathSuffix）；"添加媒体源"菜单有独立"Lucky STUN 穿透媒体源"入口（`PosterWallView.luckyStunSourceSheet`），登录检测、选穿透规则、选源类型（WebDAV/Plex/Emby/Jellyfin/Docker）并填媒体源账号，保存即挂载新源；WebDAV 可走穿透地址进目录浏览器选子目录，保存 `pathSuffix` 做子路径挂载；源显示名可显示 `Lucky STUN · 规则名`。
- Win：仅全局 `LuckyStunSettings` + 外网地址 `UseLuckyStun/LuckyRuleId` 绑定；`ILuckyStunClient.FetchAsync(LuckyStunSettings)` 是全局签名。
- 缺：按源配置结构、"添加穿透媒体源"菜单/表单、`LuckyStunCoordinator` 等价物（按源定时更新）、pathSuffix 挂载、按源登录凭据存储。

### 2. 离线缓存取消
- ARM：`OfflineCacheManager.cancelDownloads` + 详情页/卡片"取消离线缓存？"确认（已缓存保留、未完成删除）+ 取消令牌。
- Win：`PosterWallViewModel.offlineCacheProgressByFileId` 有进度，但无取消命令/确认框。

### 3. TMDB 代理网关
- ARM：设置页"TMDB API 代理网关（可选）"（`tmdbProxyURL`），`TMDBService.proxyAPIBaseURL/resolvedAPIURL` 支持带路径前缀代理，连接测试走代理。
- Win：`TmdbSettings` 无 proxy 字段，直连。

### 4. 播放器精确续播
- ARM：任意 >0 秒位置续播；`loadfile ... replace -1 start=%.3f` 精确起播（`MPVPlayerManager` pendingInitialSeek + start 参数）。
- Win：`PlayerViewModel.NormalizeStartupSeekPosition` 为 >5 秒旧逻辑，加载后 `seek absolute`，无 `start=` 参数。

### 5. WebDAV 隧道/穿透健壮性
- ARM：穿透地址首次请求（超时/断网/无法连接/无网络）自动重试一次；首次请求期间刷新按钮可用；登录后打开浏览器走 onDismiss 流程。
- Win：`WebDavDiscoveryClient` 无重试；刷新按钮在下载中禁用。

### 6. 顶栏离线缓存全局进度
- ARM：海报墙顶栏缓存进度（标题 · 第 N/M 集 · %，圆形进度）。
- Win：顶栏无全局缓存进度，仅详情内进度。

### 7. TMDB 内置公共源（回归缺失）
- Win 文档 `2026-05-06-tmdb-built-in-public-source-fix.md` 声称已补内置公共受限 Key，但当前 `TmdbSourceKind` 只有 CustomApiKey/CustomAccessToken/Environment*/Unconfigured，`EnableBuiltInPublicSource` 与内置 Key 均不存在（源码与测试均无），修复疑似回退丢失。
- ARM 当前源码同样未见硬编码内置 Key（`TMDBCredentialSource` 仅 custom/bearer/none）——两版行为目前一致（都没有内置 Key），此项需先和产品确认是否仍需要。

## 二、需对齐的行为差异

| 项 | ARM mac | Win |
|---|---|---|
| 已播判定阈值 | 0.95 | `PlaybackProgressRules.CompletionRatio = 0.97` |
| 状态文案 | 未播 / 未播完 / 已播 | 未看 / 未看完 / 已看 |
| Docker 远端进度 | `isWatched` 且有时长 → 进度=时长 | 直接存 `PositionSeconds` |
| 续播起点 | >0 秒即续播 | >5 秒才续播 |

## 三、架构差异（影响移植方式）

### 刮削替换与详情页跟随
- ARM：占位（负 id）movie 被 TMDB 结果**替换**为新 movie 行（file.movieId 指向新 id，删旧占位），`libraryUpdated` 通知携带 `replacedMovieId/updatedMovieId/metadataScrapeCompleted`，详情页 `refreshMovieMetadata(followFileAssociation:)` 改绑新 id；海报墙收到 `metadataScrapeCompleted` 立即取消防抖并 `loadData()`。
- Win：movieId 稳定，**原地更新** + 按 tmdbId 规范化合并（`LibraryMetadataEnricher` canonical merge）；手动"重新刮削"走 `RefreshMovieAsync(movieId)` → `ReloadLibraryAsync()` → `RefreshOpenDetailIfNeededAsync()`（按 `currentDetailMovieId` 重查）。
- 待验证点：占位（负 id）→ 真实 movie 替换后，`currentDetailMovieId` 是否仍有效；若 id 改变，Win 详情页是否失跟。见本文档第四节验证记录。

### WebDAV 挂载交互
- ARM：交互式 WebDAV 目录浏览器（路径栈逐层浏览选目录 + 标星）。
- Win：`NetworkShareDiscoveryService` 预扫描扁平文件夹列表 + 星标 + 一键挂载（UX 不同，保留 Win 风格即可）。

## 四、占位 movie → 真实 movie 详情页跟随验证（2026-09-04）

### 结论：Win 的跟随逻辑等价且更稳，无需照搬 ARM 的"换 id 重键"。

### 机制对比
- **ARM**：占位（负 id）movie 被 TMDB 结果替换为**新正 id 行**（file.movieId 改指新 id、删旧占位、迁移豆瓣行），`libraryUpdated` 通知携带 `replacedMovieId/updatedMovieId/metadataScrapeCompleted`，详情页 `refreshMovieMetadata(followFileAssociation:)` 改绑新 id。
- **Win**：占位 id 为 `CreateSyntheticEntityId`（SHA256 取负，负 id 稳定）。刮削走 `LibraryMetadataEnricher.ApplyMovieMatchCoreAsync` → `UPDATE movie SET ... WHERE id = @Id`（`@Id = candidate.Id`，即占位 id 本身）。`videoFile.movieId` 在扫描时沿用 `existingFile?.MovieId`，不重新分配。因此**被刮削的 movie 主键始终不变**。
- 详情跟随：`RefreshOpenDetailIfNeededAsync` 用 `currentDetailMovieId` 在 `allMovies` 重查并按同一 id 重开。因为 id 稳定，天然不失效。

### 唯一失跟风险：跨源/跨分组同 tmdbId 合并
`MergeDuplicateMoviesByTmdbIdAsync(canonicalMovieId = candidate.Id, ...)` 以**本次被刮削的 movie 自身**为 canonical，只删除"其它同 tmdbId 且有文件"的行并把它们的 file 重指向 canonical。故"用户正打开的那部"不会被当成重复项删掉；只有"另一部同名同 tmdbId 的 movie"被并入。这是正确的去重语义，不是失跟。

### 运行证据（本机 macOS + 自带 .NET 10.0.203 SDK）
- 基线：`dotnet test --filter PlaceholderTests|LibraryMetadataEnricherTests|LibraryMetadataEditorTests` → **51 passed / 0 failed**。
- `EnrichMissingMetadataAsync_UpdatesMovieMetadataAndDownloadsPoster`：占位 movie `id=-101` 刮削后仍以 `id=-101` 查得（id 未变）。
- `EnrichMissingMetadataAsync_MergesMovieRowsWithSameTmdbId`：两行同 tmdbId 合并后 `COUNT(movie WHERE tmdbId=27205)=1`、文件仍 2 行、`DISTINCT movieId=1`，canonical 保留。

### 与 ARM 的残留差异（非失跟，属行为对齐项）
1. **Docker 远端进度归一化**：ARM 把远端 `isWatched && duration>0` 归一为"进度=时长"；Win `LibraryScanner` 直接存 `PositionSeconds`。需在刮削/同步对齐（见第七节第 7 项落地）。
2. **刮削完成即时刷新**：ARM 用 `metadataScrapeCompleted` 触发海报墙取消防抖立即 `loadData()`；Win 每条目 `ReloadLibraryAsync()+RefreshOpenDetailIfNeededAsync()`，功能等价（更细粒度），无需改。


## 五、Win 独有功能（同步时务必保留）

- SMB 协议 + SMB 网络共享发现/连接（`MediaSourceProtocol.Smb`、`SmbNetworkConnection`）。
- Direct 直连协议。
- 蓝光 ISO/BDMV 播放：`RemoteIsoStreamProxy`（1516 行）+ MpvPlayer `bd://`/`dvd://`/主片识别。
- 网络共享预扫描扁平列表 + 星标挂载。
- 窗口缩放卡顿/左上角拖拽/播放卡顿修复（见 2026-05-06 系列文档）。
- 豆瓣表结构：Win 无 `nextRefreshAt/lastError`（无缓存过期），ARM 有（豆瓣 30 天缓存过期重抓）。

## 移植记录（2026-09-04）

### 第 0 步：验证"负 id 占位 → 真实 movie"详情页跟随
- 结论：Win 用稳定负 id + 原地 UPDATE，跟随逻辑等价且更稳，**无需**移植 ARM 的换 id 重键。详见第四节。
- 测试：`PlaceholderTests`/`LibraryMetadataEnricherTests`/`LibraryMetadataEditorTests` 基线 51 通过。

### 第 7 项：Docker 远端进度归一化（已完成，刮削相关）
- 位置：`OmniPlay.Infrastructure/Library/LibraryScanner.cs` Docker 文件 upsert。
- 改动：`PlayProgress = IsWatched && DurationSeconds>0 ? DurationSeconds : PositionSeconds`；`duration = MAX(现有, 远端)`；本地 `playProgress>5` 且远端未播且远端位置≤0 时**保留本地进度**（对齐 ARM `existingProgressIsNewer` 保护）。
- 验证：`LibraryScannerTests`/`PlaceholderTests`/`LibraryMetadataEnricherTests`/`VideoFileRepositoryTests` 共 **76 通过 / 0 失败**。
- 说明：播放前远端刷新（`GetPlaybackProgressAsync` 分支）保持原样——ARM 该路径本就"已播则不续播"，Win 用 `IsWatched||位置≤5→不续播` 等价实现，无需改。

### 第 1 项：播放器精确续播（已完成）
- ARM：任意 >0 秒位置续播。Win 原为 `>5` 秒。
- 改两处阈值闸门：`PosterWallViewModel.GetPlaybackStartPosition`（`>5`→`>0`）与 `PlayerViewModel.NormalizeStartupSeekPosition`（`>5`→`>0`）。
- 说明：Win 续播用"加载后轮询 seek"（`ApplyStartupSeekAsync`，40 次重试）而非 `loadfile start=`，对蓝光/dvd/bd 多分支更稳，功能等价，保留该实现。

### 第 3 项：已播阈值 0.95 + 状态文案对齐（已完成）
- `PlaybackProgressRules.CompletionRatio` 0.97 → 0.95（与 ARM 一致）。
- 三态文案对齐 ARM：`已看/未看完/未看` → `已播/未播完/未播`（转义 Unicode 精确替换，未误伤"查看/观看"）。涉及 `PlaybackProgressRules`/`LibraryVideoItem`/`VideoFileRepository`/`PosterWallViewModel` 及对应测试断言。
- 测试边界值调整：`VideoFileRepositoryTests` 进行中样本 1930/2000(0.965) → 1890/2000(0.945)，保证 0.95 下仍判"未播完"；已播样本 1940/2000(0.97) 不变。
- 验证：播放进度/详情/播放器/扫描/占位/刮削相关共 **150 通过 / 0 失败**。

### 第 2 项：离线缓存取消（已完成，2026-09-05）
- ARM：缓存进行中再点"离线缓存"弹"取消离线缓存？"确认框（继续缓存/取消缓存），取消删 `.part`、保留已完成文件。
- Win 实现（`PosterWallViewModel`）：4 个缓存入口（海报/详情整部/整季/单集）改为开关式——进行中再点即 `RequestCancelOfflineCache()` 弹确认框；确认后 `CancellationTokenSource.Cancel()` 贯穿 `CopyOfflineCacheFileAsync`/`DownloadRemoteOfflineCacheFileAsync`（本地读循环 `ThrowIfCancellationRequested`，HTTP `SendAsync`/`ReadAsync` 带 token）；取消时删除整批未完成的 `.tmp`、保留已完成文件、清进度条目，状态栏"已取消离线缓存，已缓存的内容会保留。"
- 与 ARM 语义差异（架构决定）：ARM 并行队列可逐文件取消；Win 单批次顺序复制，取消作用于当前批次内未完成文件，结果等价。
- XAML：`PosterWallView.axaml` 新增确认弹窗（ZIndex 91，文案与 ARM 一致）。
- 测试：新增 2 个（本地 8MB 复制中途取消删 `.tmp`；`HttpListener` 慢速远程下载中途取消），全量 **246 通过 / 0 失败**。

### 第 4 项：TMDB 代理网关（已完成，2026-09-05）
- ARM：设置里可填 TMDB 反向代理地址（`tmdbProxyURL`），`proxyAPIBaseURL` 规范化（缺省补 `https://`、补 `/3` 尾段、拒绝 userinfo/query/fragment/非 http(s)），`resolvedAPIURL` 把官方 API 路径重写到代理基址；代理无效时连接检查报"TMDB 代理网关地址无效。"
- Win 实现：`OmniPlay.Core/Settings/TmdbSettings.cs` 新增 `ProxyUrl`；新文件 `OmniPlay.Infrastructure/Tmdb/TmdbProxyGateway.cs`（`ResolveApiBaseUrl`/`ResolveRequestUrl`/`HasInvalidInput`，规则与 ARM 一致）；`TmdbMetadataClient` 的 `CreateRequest` 统一走 `TmdbProxyGateway.ResolveRequestUrl`（无效抛 `InvalidTmdbProxyException`，`SearchMatchesAsync` 吞掉返回空），`TestConnectionAsync` 前置 `HasInvalidInput` 短路；`TmdbSettings.ProxyUrl` 在 `SettingsViewModel` 加载/构建间往返，设置面板"自定义 TMDB API"卡新增"TMDB API 代理网关（可选）"输入。
- 说明：Win 的 `DownloadPosterAsync`/剧照仍直连 `image.tmdb.org`（ARM 亦仅 API 走代理，图片 CDN 直连），行为一致。
- 测试：新增 `TmdbProxyGatewayTests`（12 例：规范化/无效输入/URL 重写）+ `TmdbConnectionTesterTests` 2 例（代理重写请求 URL、无效代理短路）。

### 第 5 项：WebDAV 隧道重试（已完成，2026-09-05）
- ARM：PROPFIND 最多 3 次，5xx 与网络类错误（超时/连接丢失/无法连接等）按 `attempt*300ms` 退避重试；401/403 与其他非 2xx 立即失败。
- Win 实现：`WebDavDiscoveryClient` 新增 `SendPropFindAsync`（`MaxPropFindAttempts=3`，`TaskCanceledException` 记为超时、`HttpRequestException` 可重试、5xx 可重试；2xx/207/401/403 直返；重试耗尽后按是否超时分派 `TaskCanceledException`/`HttpRequestException`，调用方既有 catch 分支不变）。`TestConnectionAsync` 与 `EnumerateDirectoryAsync` 共用该入口。
- 测试：`WebDavDiscoveryClientTests` 新增 3 例（5xx 重试后成功、网络错误重试后成功、401 不重试），共 5 通过。

### 第 6 项：顶栏缓存全局进度（已完成，2026-09-05）
- ARM：顶栏在缓存期间显示整体进度环 + "任务标题 / 当前文件 · 第 N/M 集 · 百分比"。
- Win 实现：`PosterWallViewModel` 增批次状态（`offlineCacheBatchTotalCount/CompletedCount/CurrentFileId/CurrentFileName/ActiveTitle`）+ 可观察属性 `ShowOfflineCacheTopBar/OfflineCacheTopBarTitle/OfflineCacheTopBarDetail/OfflineCacheTopBarProgress`；`StartOfflineCacheAsync` 开始/逐文件/成功/失败/取消各路径维护批次计数，`RefreshOfflineCachePresentation` 末尾刷新 `UpdateOfflineCacheTopBarPresentation`；`PosterWallView.axaml` 顶栏 Grid 加第 3 列，缓存时显示 140px 进度条 + 标题 + 详情（样式复用 `libraryProgress`）。
- 语义差异（架构决定）：ARM 并行多任务整体进度；Win 单批次顺序复制，整体进度=该批次已完成数+当前文件进度 / 批次总数，观感一致。
- 验证：`dotnet build` 桌面工程 0 错误；既有取消/进度测试覆盖该状态机，全量 273 通过。

### 第 8 项：Lucky STUN 按源穿透媒体源（已完成，2026-09-05）
- ARM：每个媒体源自带 `LuckyStunSourceConfiguration`（管理地址/账号/密码/规则/显示名/自动更新/周期/lastUpdatedAt/pathSuffix，JSON 存 `addressConfiguration.luckyStun`）；`LuckyStunCoordinator` 每 5 分钟检查到期源，按源凭据拉规则、`normalizeAddress`+`pathSuffix` 写回源 BaseUrl；源上可单独"添加 Lucky STUN 穿透媒体源"（登录后按管理地址+规则幂等保存）。
- Win 实现：
  - 模型：`MediaSourceAddressConfiguration` 新增 `LuckyStun`（`LuckyStunSourceConfiguration`，字段/JSON key 与 ARM 对齐），`Clone()` 深拷贝；`MediaSource.HasLuckyStunSourceConfig`/`LuckyStunDisplayName`。
  - 地址工具：`OmniPlay.Core/Models/Network/LuckyStunAddressHelper`（`NormalizeAddress` 与 `AppendPathSuffix`，逻辑照搬 ARM；`LuckyStunClient.NormalizeAddress` 改为委托该助手，保证两套入口一致）。
  - 加源 UI：`PosterWallView.axaml`"添加媒体服务器"弹窗新增"使用 Lucky STUN 穿透（按源配置）"折叠区——管理地址/账号/密码、穿透规则下拉 + "登录检测"、路径后缀、自动更新+周期、"登录并保存为穿透媒体源"（登录→取规则→穿透地址(+后缀)作为源地址→按管理地址+规则幂等 Add/Update）。`BuildPendingMediaServerSource` 会在普通保存时附带按源配置。
  - 按源自动更新：`SettingsViewModel` 轮询改为 5 分钟节奏，`RefreshDueLuckyStunSourcesAsync` 按各源 `AutoUpdate`/`UpdateIntervalMinutes`/`LastUpdatedAt` 到期判断并逐源刷新（复用 `ILuckyStunClient.FetchAsync`，成功写回 BaseUrl/ActiveAddress 并触发 `LuckyStunAddressUpdated`）；未配置全局凭据但有按源配置时惰性启动轮询；全局"定期自动更新"闸门收紧为 `LuckyStunAutoUpdate && 管理地址非空`。
  - 源列表：带按源配置的源显示"刷新穿透"按钮（手动刷新 + 重扫媒体库）。
- 语义差异（架构决定）：ARM 用独立 `LuckyStunCoordinator` 单例；Win 复用 `SettingsViewModel` 轮询 + `ILuckyStunClient`（DI 注入 `PosterWallViewModel`/`SettingsViewModel`），行为等价。
- 测试：新增 `LuckyStunAddressHelperTests`（归一化 5 例 + 路径后缀 4 例）与 `MediaSourceLuckyStunConfigTests`（JSON 往返/Clone/HasLuckyStunSourceConfig 3 例），全量 **285 通过 / 0 失败**。

### 第 9 项：移除 Docker 版接入接口（已完成，2026-09-05，跨四端）
- 背景：TMDB 可用反代免代理，Docker 服务端取消开发，四端全部改为独立版。
- Win：`MediaSourceProtocol` 去 `OmniPlayDocker`（45722 映射保留为旧数据惰性值）；`MediaSourceNormalizer`/`MediaSourcePathResolver`/`MediaSourceAuthConfigSerializer`（去 `OmniPlayDockerAuthConfig`）；`ServiceRegistration` DI；`NetworkShareDiscoveryService`（去 docker 探测）；`LibraryScanner`（去 `ScanOmniPlayDockerSourceAsync`/`ImportOmniPlayDockerDetailsAsync` 等约 500 行）；`PosterWallViewModel`（去 docker 播放路径解析/远端进度归一/豆瓣回写/自动进度同步）；删 `OmniPlayDockerClient.cs` + `IOmniPlayDockerClient.cs`。`SettingsViewModel` 的 Lucky 自动更新去掉 docker 源改写分支，外网地址（external-addresses）循环保留。
- ARM mac：删 `OmniPlayDockerClient.swift`；`PosterWallView` 去 docker 表单/自动同步/扫描分支（25+ 处）；协议枚举、连接性检查、设备发现全部收敛为 webdav/plex/emby/jellyfin；测试同步。
- x64 mac：与 ARM 的差异经 diff 确认全部为 Docker 相关，8 个 Swift 文件 + 2 个测试文件直接自 ARM 拷贝对齐；"外网地址编辑 UI"在 x64 一并废弃（与 ARM 一致，均不再提供）。
- 安卓 TV：`MainActivity`/`PhoneMainActivity` 全部切到 `LocalPlayApi`（独立本地引擎），删 setup 页的 Docker 登录 + Lucky STUN 表单、`startServerDiscovery` 局域网探测；`RemoteIsoStreamServer` 仅保留本地 `file://` ISO Range 代理用途。
- 构建/测试：ARM xcodebuild + OmniPlayTests 通过；x64 x86_64 build 通过；Win build + 283/283 通过（原 285 中 2 个 docker 专项测试移除）；安卓 `assembleDebug` 通过（TV + 手机双入口）。
- 版本号：安卓 TV `versionName` 1.6 → 1.7（设置页文案同步）。

### 安卓 TV 完全体改造记录（2026-09-05）
- 新增 `com.omniplay.tv.local` 包：`LocalMediaNameParser`（ARM MediaNameParser 移植：SxE/第N集/EP 解析、中文/外文候选、年份提取、剧集分组）、`TmdbClient`（ARM TMDBService 打分算法移植 + 反代网关 + 3.2rps 限速 + 429 重试）、`LocalMediaLibrary`（SQLite：movie/tvShow/videoFile/scrapeQueue，进度规则与 ARM 一致：>=0.95 已看、>5s 续播、单调推进保护）、`WebDavClient`（PROPFIND 递归 + 内嵌凭据流式 URL）、`LocalEngine`（源管理 + 扫描 + 刮削队列 + 详情页聚合，产出既有 `Models.*` 类型）。
- `LocalPlayApi` 适配器镜像 `OmniPlayApi` 签名，TV/手机两个 Activity 仅需换字段类型。
- 播放：本地文件 `file://`、WebDAV 内嵌凭据 http(s)；ISO 走本地 Range 代理 + `bd://longest`；字幕依赖 mpv `sub-auto=all` 内嵌字幕（外置字幕/ HLS 路径为空实现）。
- `LocalMediaLibrary.removeMissingFiles` 修复：原实现误删"当前存在"的文件 id，改为删除"库中有、本次扫描没有"的差集，删除数量回填扫描报告。

### 第 10 项：mac 双端 iCloud 媒体库/进度同步（已完成，2026-09-05，仅 ARM + x64 mac）
- 需求：ARM mac / x64 mac 用 iCloud 同步媒体库与播放进度；安卓 TV、Win 不参与。
- 实现（两版完全一致，文件互拷）：
  - 新增 `OmniPlay/OmniPlayICloudSync.swift`：
    - 数据库：本地 `~/Library/Application Support/OmniPlay/omniplay.sqlite` 作为唯一读写源；iCloud 容器 `icloud.nan.OmniPlay`（`url(forUbiquityContainerIdentifier:)` + Documents）内保留云端副本。启动时按修改时间拉取较新云端库（新设备本地无库时直接拉取），运行中每 60 秒全量推送、`willTerminate` 前再推一次；`NSFileCoordinator` reading/writing 协调；推送后对齐本地修改时间避免误判。
    - 海报：`PosterManager`/`MovieEditModalView` 的海报目录改为 `OmniPlayICloudSync.posterDirectory()`——容器可用时直接存容器内（iCloud 原生文件同步），否则回退本地 Application Support。
    - 容器不可用（未登录 iCloud / ad-hoc 签名）时 `containerURL()` 返回 nil，全链路自动回退纯本地，行为与旧版一致。
  - `AppDatabase` 增加 `databaseURL`/`icloudContainerURL` 属性；`OmniPlayApp.init` 改走 `OmniPlayICloudSync.openDatabase()` + `startBackgroundSync()`。
- 签名：iCloud 容器 entitlement 要求 Development 签名。本机 Xcode 已登录账号（团队 `DT95MB3RG4`，个人免费团队"海涛 程"）——**个人团队不支持 iCloud capability**，故默认 `OmniPlay.entitlements` 保持无 iCloud（ad-hoc 可构建、运行时回退本地）；新增 `OmniPlay.iCloud.entitlements`（容器 `icloud.nan.OmniPlay` + KVStore `$(TeamIdentifierPrefix)nan.OmniPlay`），付费开发者团队构建时在 Xcode 中把 target 的 entitlements 指到该文件并去掉 ad-hoc 身份即可启用同步。
- 验证：ARM build + 全量单测通过；x64（x86_64）build 通过。两版 bundle id 均为 `nan.OmniPlay`，同一容器互通。
- 已知限制：iCloud 传播有分钟级延迟；两台 mac 同时长时间开机时进度同步延迟 ≤1 分钟；SQLite 全量拷贝在库很大时（>1GB）推送耗时增加（当前量级无感）。

### 待做（后续批次）
- 无（一~十项全部完成；安卓 TV 完全体 + 四端 Docker 移除 + mac 双端 iCloud 同步已交付）。

## 六、建议移植顺序（按价值/独立性）

1. 播放器精确续播（小，影响大）
2. 离线缓存取消（中，独立）
3. 已播阈值 0.95 + 状态文案对齐（小）
4. TMDB 代理网关（中）
5. WebDAV 隧道重试（小）
6. 顶栏缓存全局进度（小）
7. 刮削替换/详情页跟随对齐（依赖第四节验证结论）
8. Lucky STUN 按源穿透媒体源（大：新结构体 + 菜单/表单 + 按源定时更新 + pathSuffix + DI）

## 七、工程信息

- Win 工程：`OmniPlay.Windows.slnx`，目标 net10.0 / win-x64，发布 `dev-build.ps1` / `package-setup.ps1`。
- 测试：`tests/OmniPlay.Tests`（22 个测试文件，含 `PlaceholderTests`、`LibraryMetadataEditorTests`、`LibraryMetadataEnricherTests`、`PosterWallViewModelTests`、`LuckyStunClientTests`）。
- 本地 SDK 在仓库内 `.dotnet-sdk`（.NET 10），NuGet 缓存 `.dotnet/.nuget`。
