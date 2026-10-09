# 2026-10-07 Windows 版对齐 ARM Mac 版（9 项）

对应 `2026-09-04-win-vs-arm-mac-feature-diff.md` 列出的差异清单，本次把 9 项功能全部移植进 Windows 版。
数据库结构升到 `user_version = 3`，全部改动都是增量式的：旧的扁平 `director` / `mainCast` 列继续写入并保留，
新的 JSON 名单列只作为更精确的来源。

## 1. 导演 / 主演全量名单

- `movie` / `tvShow` 新增 `directors`、`casts` 两列（JSON 数组）。首次升到 v3 时，
  `SqliteDatabase.BackfillPersonListColumns` 会把历史扁平字符串按顿号 / 逗号 / 分号 / 斜杠拆成数组。
- 新增 `OmniPlay.Core/Models/PersonNames.cs`：名单编解码、拆词、去重、`ContainsChinese`。
- `Movie` / `TvShow` / `LibraryPosterItem` 暴露 `DirectorList` / `CastList`，
  JSON 列缺失时自动回退到扁平列，所以既有展示逻辑与旧库都不用改。
- `LibraryMetadataEnricher`、`LibraryMetadataEditor` 落库时同时写名单列与扁平列；
  扁平列由名单拼接而来，两边不会再互相打架。

## 2. 首页分类与文件夹

- `ApplyHomeCategorization` 改为展开全量名单：一部影视会出现在每位导演 / 主演的文件夹里。
- 没有导演也没有主演的条目归入「未标注」文件夹（替换原来只取第一个人的「其他」）。
- 新增 `categoryFolder` 表与 `ICategoryFolderStore`，文件夹封面存进数据库；
  老版本存在 `AppSettings.HomeFolderPosters` 里的手选封面在第一次加载时自动搬迁。
- 封面优先级：数据库里的文件夹封面 > 旧设置里的封面 > 人物头像 > 文件夹内第一张海报。

## 3. 人物档案与文件夹详情

- 新增 `personProfile` 表：人物名、TMDB 人物 id、头像、作品列表缓存。
- `TmdbMetadataClient.FetchPersonFilmographyAsync` 走 `/person/{id}/combined_credits`，
  片名同样过一遍中文补全；结果缓存在档案里，7 天内不重复请求。
- 文件夹详情页顶部新增一行说明：`「某某」共执导 / 参演过 N 部影视`，
  第二行列出最近 3 部作品；作品列表在后台拉取，拿到后自动刷新。

## 4. 中文译名

- 新增 `chineseNameCache` 表与 `ChineseNameCacheRepository`，key 有三类：
  `person#<TMDB 人物 id>`、`work#<movie|tv>#<id>`、`alias#<director|cast>#<刮削名>`。
- 结果是三态：`Found` / `NotTranslated` / `RequestFailed`。
  只有 `NotTranslated` 才写负缓存（30 天过期）；请求失败绝不落库，
  否则一次断网就会把外文名永久钉死在库里。
- 用户手改的名字写成 `alias#` 条目且不依赖网络与凭证，因此后续刮削带回外文名时仍会换回手改的中文名。
- `RenamePersonAsync` 一次改动会同步影片名单、`categoryFolder` 与 `personProfile` 主键，
  重复人物档案按字段合并（只填空值，不覆盖已有头像 / 作品）。
- 扫描结束后在后台跑 `BackfillChineseNamesAsync` 补齐历史条目（片名查询有人数上限，避免整库刷爆 API）。
- 界面语言为英文时不做任何自动翻译，保留 TMDB 原文。

## 5. 分集剧照

`LibraryThumbnailEnricher` 重写内部实现，公开接口不变：

- 按剧集分组，同时处理 3 部剧集；单部剧集搜索失败只影响该剧集。
- 只有全部剧集都因网络失败才上报 `EncounteredNetworkError`，
  以前是第一个网络异常直接结束整轮任务。
- 「请求成功但这一集确实没有剧照」写入 `thumbnails/no-still.json` 负缓存，30 天内不再重复请求。

## 6. TMDB 请求层

- 新增 `TmdbRequestLimiter`：自建凭证 3.2 req/s、内置共享源 1.5 req/s，按来源分开计数。
- 所有 API 调用统一走带重试的发送方法：429 / 5xx / `HttpRequestException` 重试 2 次，
  退避优先读 `Retry-After`，否则 1.5 s、3 s。
- 连通性探测保持单次请求，不做重试。
- `credits` / `combined_credits` / 译名结果都带进程内缓存，一轮扫描里同一人物 / 同一作品只查一次。

## 7. 播放器

- 位置快照先判 `IsFinite` 且非负再写 UI，避免刚切换片源时进度文本变成乱码。
- 暂停走乐观状态：先按目标状态更新界面，并在 0.75 秒窗口内忽略轮询回来的旧状态。
- 续播门槛从 `> 5 s` 降到 `> 0 s`，和 Mac 版一致（有效进度就续播）。
- 新增 `IPowerManagementService` 与 `WindowsPowerManagementService`，
  播放期间用 `SetThreadExecutionState` 阻止息屏和睡眠，停止播放即释放。

## 8. 本地 NFO 与豆瓣

- `LocalMetadataSidecarService` 读写 `<director>` 与 `<actor><name>`，
  读 Kodi / Jellyfin 生成的 NFO 时能带上导演和主演，导出时也会写回。
- 豆瓣解析新增 JSON-LD 里的 `director` / `actor`，存进 `doubanMetadata.directors` / `.casts`。
- 绑定豆瓣时在库内还没有导演 / 主演的前提下补写人名（不覆盖 TMDB 已有名单），并顺带补年份。

## 9. 界面多语言

- XAML 里的 174 条中文界面文案换成 `{DynamicResource Lxxx}`，
  文案表在 `OmniPlay.UI/Themes/LocaleStrings.cs`，中文是源语言、英文是查表结果。
- 新增 `LocaleService`：把当前语言的文案挂到应用级资源上，切换即时无需重建控件；
  英文表缺条目时回退中文，漏译只会显示中文而不会空白。
- 设置页新增「界面语言」，写入 `AppSettings.AppLanguage`，启动时读取；
  播放器状态、人物简介等运行时文本仍为中文，需要时可按同一条表补齐。

## 验证

- `dotnet build OmniPlay.Windows.slnx -c Debug`：0 error。
- `dotnet test`：309 passed / 1 failed。唯一失败项是既有的 `TmpDbDumpTests.DumpLiveData`，
  它读取一个写死的 `/tmp` 库文件，属于环境依赖，本次改动前也一样失败。
- 新增 `PeopleChineseNameTests`（10 项）：名单编解码、扁平列迁移、中文译名三态与重试、
  英文界面不翻译、改名迁移与别名记忆、人物档案登记、限速间隔、NFO 人名往返。
- `PosterWallViewModelTests.CachePosterItemCommand_CancelConfirmation_RemovesPartialFileAndKeepsStatus`
  单独用 `--filter` 跑时偶尔会超时（它依赖 8 MB 文件复制仍在进行），整类或整库跑都稳定通过；
  本次改动没有触碰离线缓存链路，属于既有的计时敏感用例。
