# 短剧刮削方案（2026-10-26）

面向竖屏/微短剧（单集 1–5 分钟、一部 30–120 集、多为纯中文片名）的入库与刮削设计。
以 mac ARM 版为基准实现，Windows / 安卓 TV 后续对齐。

## 1. 结论先行

1. 短剧不新建主表。元数据仍落 `movie`（沿用 TMDB id 作主键的做法），只在 `videoFile` 上加一列
   `mediaKind`（`movie` / `tv` / `shortDrama` / `unmatched`），另建一张与 `doubanMetadata` 同形态的
   旁表 `shortDramaMetadata` 存集数、平台、来源标识等短剧专属字段。
2. 主来源仍是 TMDB，豆瓣降级为「一剧一次搜索 + 严格频控的补充源」，另加一个可选的
   「短剧专源」（用户填 base URL 的 JSON 接口，默认关闭）。豆瓣不能当主力的理由见 §5.2。
3. 刮削单位从「文件」改成「剧」：一个目录 = 一次搜索 = 一次 credits，N 个分集共享同一条结果。
   现有 `tvAutoReuseCache` 只在这一步之外再做一次保险。
4. 全部来源都没命中时不再留 `unmatched` 占位卡，而是以目录名为标题、用首帧当封面本地入库，
   状态标 `local-only`，用户可事后手动匹配。短剧在线库覆盖本就残缺，「先能看能搜」比「标红待补」有用。

## 2. 现状缺口（为什么必须改）

| 位置 | 现状 | 对短剧的后果 |
| --- | --- | --- |
| `MediaLibraryManager.swift:462` `isLikelyTVEpisodePath` | 只认 `SxxExx` / `Ep\d` / `第N集` / `第N季` | `我在八零年代当后妈/001.mp4` 这类纯数字分集不被判为剧集 |
| `MediaLibraryManager.swift:3955` 分组键 | 非剧集走 `sourceId#movie#relativePath` | 一部 100 集 = 100 个 group = 100 张占位卡 + 100 次搜索 |
| `MediaLibraryManager.swift:1715` `tvAutoReuseKey` | 同样以 `isLikelyTVEpisodePath` 为前提 | 纯数字分集拿不到复用，配额按集数线性烧 |
| `MediaLibraryManager.swift:3972` 显示名兜底 | 仅对「≤2 位纯数字」回退父目录名 | `001.mp4` 三位数字，卡片标题直接显示 `001` |
| `MediaLibraryManager.swift:1596` `saveScrapingResult` | 命中后一律写 `mediaType = "movie"` | 库里根本区分不出短剧，无法做筛图和分集文案 |
| `MediaLibraryManager.swift:390` 分集标题 | 固定「第 X 季 第 Y 集」 | 短剧只有一季，「第 1 季 第 87 集」很怪 |
| `DoubanService.swift:93` | 只有 `fetchSubject(id)`，无关键词搜索 | 豆瓣的国产短剧条目进不来，只能用户手贴链接 |
| `DoubanService.swift:36` | 请求闸门 12s + 3~8s 抖动 | 见 §5.2，豆瓣只能做低频补充 |
| `videoFile.duration` | v2 迁移起就有，扫描期不参与判断 | 短剧最强的识别信号被浪费 |

## 3. 识别：短剧判定放在入库前

在 `buildPendingMediaGroups` 之前插一道目录级预聚合 `ShortDramaClassifier`，输入是同一父目录下的
候选文件（含 `duration`、`fileSize`、文件名），输出 `isShortDrama + 置信度`。全部用已入库字段，
不引入 ffprobe 二次探测。

打分（≥ 2 分判为短剧，标准档）：

- 同目录媒体文件数 ≥ 30：+2；20–29：+1
- 该目录平均时长 45–420s：+2；421–600s：+1
- 文件名形如 `^\d{1,4}` 且集号能连成 1…N（N ≥ 20）：+2
- 路径含 `短剧` / `竖屏` / `合集` / `全集` / `共?\d{2,3}集`：+1
- 目录名命中豆瓣/TMDB 返回的 `type=short` 或国产剧 + 集数 ≥ 20：+2（刮到结果后回补，用于纠偏）

三档灵敏度进设置页：保守（≥3）/ 标准（≥2）/ 激进（≥1 + 仅要求同目录 ≥ 20 文件）。
判定结果写进 `videoFile.mediaKind`，扫描与重新刮削都读它，避免每次重算。

误判代价可控：判成短剧但实为长剧，只影响分集文案与筛选；判不成短剧，走原剧集分支照常刮，
只是配额花得多。所以阈值宁可从紧，先保长剧不受影响。

## 4. 数据模型

新增迁移 `v14`，同时把新表/新列补进 `AppDatabase.expectedTables` / `expectedColumns`
（`AppDatabase.swift:287`）——x64 老库的教训写在原注释里，只加迁移不加自愈等于没加。

```
ALTER TABLE videoFile ADD COLUMN mediaKind TEXT;      -- movie / tv / shortDrama / NULL 沿用 mediaType
CREATE TABLE shortDramaMetadata (
  movieId INTEGER PRIMARY KEY REFERENCES movie(id) ON DELETE CASCADE,
  source TEXT NOT NULL,          -- tmdb / douban / duanju / local
  externalId TEXT,               -- 来源侧条目号
  platform TEXT,                 -- 红果 / 河马 / 抖音 / 快手 …
  episodeCount INTEGER,
  leads TEXT,                    -- JSON 数组，短剧主角常不在 TMDB credits 里
  tags TEXT,                     -- 逆袭 / 重生 / 穿越 / 甜宠
  posterOrientation TEXT,        -- portrait / landscape
  fetchedAt DOUBLE NOT NULL,
  nextRefreshAt DOUBLE NOT NULL,
  lastError TEXT
);
CREATE INDEX idx_videoFile_mediaKind ON videoFile(mediaKind);
```

`mediaType` 保持不动，新语义优先读 `mediaKind`，为空则回落 `mediaType`——这样 Windows、安卓 TV
的库文件被旧版本读取时不会因为缺列而崩，跨端同步也留了台阶。

## 5. 来源链

按顺序尝试，任一级命中即止；每级都写 `source` 与 `externalId`，供详情页标注出处和事后重新匹配。

### 5.1 L0 本地 sidecar（已有）

`LocalMetadataSidecarStore` 已支持 `tvshow.nfo` / `movie.nfo` / 单集 nfo 与海报、剧照图。这里只补两点：
`episodeCount`、`platform` 从 nfo 自定义标签读；识别为短剧的目录优先读 `tvshow.nfo`。
走本地来源时零网络请求，是离线环境和 NAS 上批量迁移的第一选择。

### 5.2 L1 TMDB（主力，配额可控）

复用 `TMDBService`：`search/multi`（现 `TMDBService.swift:580`）改为带 `search_type` 收窄到 tv，
并用 `first_air_date_year`；命中后用 `tv/{id}/season/1` 的实际集数与本地文件数比对，偏差 > 30% 降权。
欧美 short 走 `type=short`。请求仍走现有限流（`TMDBService.swift:173`，3.2 rps）、429 退避与
代理网关改写（`proxyAPIBaseURL` / `resolvedAPIURL`），不改。

一剧一次搜索 + 一次 season 校验 + 一次 credits ≈ 3 次请求，100 部短剧约 300 次，TMDB 免费配额够用。

### 5.3 L2 豆瓣（补充，一剧一次）

豆瓣对国产短剧覆盖最好，但 `DoubanRequestGate` 的 `minimumInterval = 12` 加 3–8s 抖动意味着
逐条请求不可扩展：50 部就是 10 分钟起，还会把 `blocked` 冷却（`pause(hours:)`）触发出来。
所以豆瓣只做三件事：

1. TMDB 未命中或集数对不上时，发一次搜索请求（`search.douban.com/movie/subject_search`，
   关键词清洗后的目录名，`tag=短剧` 优先）拿候选列表；
2. 候选过判据后直接采用搜索项字段，复用现成的 `parseSubject` 仅在简介缺失时补一次详情；
3. 结果落已有的 `doubanMetadata` 表，`nextRefreshAt` 沿用现有刷新策略。

批量场景给豆瓣单独设上限：单次扫描最多 20 次豆瓣请求，超出的条目留到下次扫描继续，
在扫描状态里显示「豆瓣补充排队中」，别让用户以为卡住。

### 5.4 L3 短剧专源（可选，默认关）

> 落地状态（第 6 期）：最终没有走「设置里填 base URL 的通用聚合源」这条路，
> 而是把红果短剧（`hongguoduanju.com`）接成了内置补充源，见 §18。
> 下面这段是当时的设计草案，接口约定留作以后再挂第三方源时的参考。

国产竖屏短剧在 TMDB / 豆瓣都有系统性缺口，唯一稳的解法是接一个可配置的聚合源：
设置里填 base URL，接口约定一个端点：

```
GET {base}/search?q=<清洗片名>&limit=5
→ [{ "title","year","episodes","poster","summary","platform","id" }]
```

实现上照抄 TMDB 那套骨架：Codable 模型、独立 `URLSession`、限流、连通性探测、代理改写。
默认关闭、连通性检测失败就整级跳过，避免第三方站点的 ToS 与失效风险变成 App 的故障。
后续可自建一个薄代理（`OmniPlay-docker` 里加一个路由），把它升成公共源。

### 5.5 L4 本地兜底

全链未命中：标题取目录名（回退清洗后的文件名），集数取实际文件数，封面用该集首帧，
`mediaKind='shortDrama'`、`source='local'`。首页不再显示「正在排队等待刮削」，
详情页给一个「重新匹配」入口接现有的手动匹配弹窗。

## 6. 刮削编排

`processUnmatchedFiles` 里为 `mediaKind='shortDrama'` 开一条分支：

1. 按 `sourceId + 父目录` 聚成 `ShortDramaGroup`，每组只跑一次来源链；
2. 集号解析扩三种形态：`^0*(\d{1,4})\.`、`第(\d{1,3})集`、`(?:EP|E)(\d{1,3})`，season 恒为 1，
   展示名改成「第 N 集」；`全70集.mp4`、`第1-30集.mp4` 这类合集文件当单条处理，不拆集；
3. 命中结果一次写库，整组文件共享同一条 `movie`，`shortDramaMetadata.episodeCount` 用来源集数，
   与本地实际数不符时以本地为准并记 `lastError`；
4. 失败按 §5.5 落 `local`，并保留一次「手动重新匹配」的入口；
5. 顺序上仍走 `representativeScrapeSortKey` 那套排序，让同一部剧的分集相邻处理，
   现有 `tvAutoReuseCache` 继续兜住跨目录同名剧。

配额量级：短剧每部 1–3 次网络请求（专源/豆瓣按 §5.3 上限节流），与集数无关。

## 7. 界面

- 设置页在「刮削服务 (TMDB)」下方加「短剧刮削」区：总开关、识别灵敏度三档、来源顺序、
  专源地址与连通性检测、单次扫描豆瓣请求上限。
- 首页筛选加「短剧」，与电影/剧集并列；短剧卡片优先用竖屏海报，来源只给横图时回落剧照。
- 详情页分集列表：短剧不显示季切换，标题「第 N 集」；集数 > 30 时按 20 集分段 + 懒加载，
  避免一次性把 100 张剧照塞进 `ThumbnailManager`（`maxConcurrentFetches = 3`）把队列堵死。
- 手动匹配弹窗增加来源标签（TMDB / 豆瓣 / 专源），命中后把 `source`、`externalId` 一起写回。

## 8. 分期

1. 第 1 期：`v14` 迁移 + 自愈清单、`ShortDramaClassifier`、目录级分组、集号解析与「第 N 集」文案、
   本地兜底。这一期不接任何新网络来源，短剧已经能整部入库、能连播。
2. 第 2 期：TMDB 分支收窄 + 集数校验 + 一剧一次刮；设置页短剧区；首页筛选。
3. 第 3 期：豆瓣关键词搜索 + 频控排队 + `doubanMetadata` 落库。
4. 第 4 期：短剧专源（含连通性检测）、竖屏海报与分集懒加载、手动匹配弹窗来源切换。
5. 第 5 期：Windows / 安卓 TV 对齐（`mediaKind` 读旧回落、分集文案、筛选入口）。

## 9. 测试

`OmniPlayTests` 新增 `ShortDramaScrapeTests`，全部是纯逻辑、不打网络：

- 识别：`001.mp4…100.mp4` + 平均时长 90s → 短剧；`movie.mkv` 单文件 → 非短剧；
  长剧 24 集 × 45min → 非短剧；20 集 × 5min 边界 → 按档位分别断言。
- 集号：`001` / `1080p.012.mp4` / `第37集.mp4` / `EP003.mp4` / `全70集.mp4` / `第1-30集.mp4`。
- 分组：同一目录 100 个文件只产出一个 group 与一次搜索调用（用可注入的假来源计数）。
- 迁移：从 v13 库升级、以及人为删掉 `shortDramaMetadata` 后 `healSchemaGaps` 能补回。

样本目录固定几组放在测试资源里，覆盖纯数字、中文集号、中英混合、合集文件四种形态。

## 10. 风险

- 豆瓣反爬升级：已用「一剧一次 + 单次扫描上限 + 现成冷却机制」压到最低，命中失败即降级，不阻塞入库。
- 第三方专源失效：默认关闭、独立开关、失败只记日志，不影响其他来源。
- 误判把长剧当短剧：分集文案与筛选受影响，元数据不受影响；提供单条目「按剧集处理」的改判入口。
- 100 集缩略图把首帧生成队列堵死：短剧剧照走独立低优先级队列并分段加载。

## 11. 刮削源实测（2026-10-26 联网验证）

| 候选 | 实测 | 用法 |
| --- | --- | --- |
| 豆瓣 `search.douban.com/movie/subject_search?search_text=` | 200，页面内 `window.__DATA__` 是 JSON（`count` + `items[]`，字段含 `id` / `title` / `labels` / `abstract` / `cover_url` / `rating` / `url`） | **主力**。一次搜索就拿到候选、评分、竖版海报、别名、单集时长，不必再进详情页 |
| 豆瓣 `m.douban.com/movie/j/subject_suggest` | 404 | 已废弃，别接 |
| TMDB `api.themoviedb.org/3` | 401（可达，缺 key） | 辅助：海外短剧、credits、海报兜底；公共反代 `tmdb.aaaagi.com` 实测不通 |
| B 站 `api.bilibili.com/x/web-interface/view?bvid=` | 200，无需 key | 合集型短剧：`pages[]` 分 P 即分集，标题、简介、封面、分集名齐全 |
| 红果短剧 `hongguoduanju.com` | 200，页面暴露 `/api/search/result/`、`/api/search/suggestion/`，但受字节 `bdms` 签名保护 | 覆盖天花板，需自建薄代理签名，单独验证后再决定是否接 |
| TVDB v4 | 200 | 中文短剧基本无覆盖，pass |
| GitHub 搜「短剧」「刮削」 | 短剧仓库全是 AI 视频生成工具，刮削仓库全是 JAV / 番剧 / 网盘向 | 没有现成的国产短剧元数据服务可依赖，L3 只能自建或回落到豆瓣 |

短剧命中率抽样（豆瓣，均为竖屏短剧）：

| 检索词 | 命中 | 关键字段 |
| --- | --- | --- |
| 我在八零年代当后妈 | 36777988 (2024) | 剧集 / 6.4 分 / 别名「我在80年代当后妈 / Hi, Mom」/ 单集 2分钟 |
| 长公主在上 | 35765541 (2022) | 剧集 / 爱情 / 短片 / 单集 2分钟 |
| 授她以柄 | 36441335 (2024) | 剧集 / 爱情 / 短片 / 古装 / 可播放 / 单集 13分钟 |
| 闪婚老公是千亿首富 | 三条同类条目，需按年份与别名择一 | 改名条目各建一条，别名互不相同 |

据此修正 §5.3 两处：

1. 「搜索命中后再 `fetchSubject` 取详情」对短剧是多余一次请求。搜索项已带齐短剧所需字段，
   改成「一剧一次搜索请求」，只在简介缺失时补 `fetchSubject`。在 12 秒频控下这就是两倍吞吐。
2. `abstract` 里的「短片」类型与「N分钟」单集时长是免费的短剧判据，可直接进 §3 打分，
   并作为集数校验之外的第二道确认，比再猜一次文件名可靠。

短剧改名与同片改名很常见（上表末行三个候选别名各不相同），别名段必须参与匹配打分，
否则容易命中错误年份的条目。

## 12. 兜底补充源

- B 站合集：下载源本就是 B 站分 P 时最省事，`pages[]` 直接给出集数与分 P 标题，连集号都不用解析。
- LLM 兜底：目录名 + 集数 + 首帧截图交给模型，生成简介、标签、主角，落 `source='llm'` 并在详情页
  标「自动生成」。短剧目录名信息量通常足够，这条比再挂一个爬虫站划算。

## 13. 落地记录（第 1 期 + 豆瓣源，2026-10-26）

已实现（mac ARM 版）：

- `AppDatabase.swift` 新增迁移 `v14`：`videoFile.mediaKind` 列 + `shortDramaMetadata` 表 + 索引，
  同时进 `expectedTables` / `expectedColumns` 自愈清单。
- `ShortDramaSupport.swift`：`ShortDramaSensitivity` 三档、`ShortDramaClassifier` 目录级打分、
  集号解析、合集文件识别、搜索词清洗、豆瓣主键号段，以及 `ShortDramaMetadata` 旁表模型。
- `DoubanSearch.swift`：豆瓣搜索页 `__DATA__` 解析（`DoubanSearchParser`）、候选结构
  `DoubanSearchHit`（含别名、单集时长、短剧特征判定）、候选排序。
- `DoubanService.searchSubjects(query:)`：走现有 `DoubanRequestGate` 频控与风控冷却，一次请求拿候选。
- `MediaLibraryManager`：入库前按目录判定短剧并整部成组（分组键 `sourceId#short-drama#目录`）、
  `insertPendingMediaGroup` 写 `mediaKind` 与旁表、`processUnmatchedFiles` 走短剧分支
  （豆瓣 → TMDB → 本地兜底），命中后把旁表行从占位 id 搬到真 id。
- `MovieDetailView`：短剧分集标题用「第 N 集」，集号优先取文件名里的数字。
- `SettingsView`：「短剧刮削」区（总开关、灵敏度、单次扫描豆瓣查询上限）。
- `OmniPlayTests/ShortDramaScrapeTests.swift`：识别、集号、搜索词、豆瓣解析、旁表建表。

对 §3 的一处实现期修正：**扫描期拿不到时长**（`videoFile.duration` 只有播过才有），
所以识别主力换成「中位文件大小」——短剧单集几十 MB、长剧一集几百 MB 起，这个量级差入库时就有。
时长降级成辅助信号（播过的目录才有）。这也解决了 §3 回避的问题：76 集的长篇国产剧
和 100 集短剧在文件数上分不开，靠文件大小分开了。

对 §5.3 的两处修正见 §11：豆瓣一剧一次搜索、不额外取详情；abstract 里的「短片」与分钟数进判据。
代价是豆瓣命中的条目没有简介，详情页显示「短剧 · 共 N 集 · 豆瓣 X 分」这样一行，
用户想要完整简介可以用已有的人工绑定豆瓣入口补。

本地兜底的落库形态：`mediaKind='shortDrama'`、`mediaType='movie'`、条目 `isLocked`，
简介写明「已按本地目录入库（N 集），未匹配到在线条目，可手动重新匹配」，
不进未匹配列表，也不会每轮扫描重复花豆瓣配额。

验证：`xcodebuild build` 通过；`OmniPlayTests` 编译通过。
短剧识别与豆瓣解析这批纯逻辑另用独立 harness 跑过全部断言
（`swiftc` 直编 `ShortDramaSupport.swift` + `DoubanSearch.swift` + 替身），因为
`xcodebuild test` 要拉起 App 宿主进程才肯连测试运行器，命令行环境下会挂在
"The test runner hung before establishing connection"。在 Xcode 里 ⌘U 跑 OmniPlayTests 即可。

未做，按 §8 分期继续：短剧筛选入口、竖屏海报优先与分集懒加载（第 2–4 期）、
手动匹配弹窗的来源切换、可选短剧专源、Windows / 安卓 TV 对齐。

## 14. 落地记录（第 2 期，2026-10-26）

- **首页筛选**：海报墙胶囊里加了「全部影视 / 只看短剧」，认的是 `videoFile.mediaKind`
  （`Movie.fetchShortDramaIDs`），TMDB 与豆瓣两种来源都能被筛出来。筛选在 `rebuildWallCaches`
  里做，沿用原来的排序缓存，不新增一次全库重排。
- **老库重分类**：`reclassifyExistingShortDramaEntries(sourceID:)`，在 `processUnmatchedFiles`
  开头跑，合并短剧功能之前入库的「一集一条」记录。判据是纯函数
  `ShortDramaClassifier.shouldMergeExistingDirectory`：目录本身像短剧，且这批影片身上
  没有值得保留的东西（海报、评分、豆瓣绑定、NeoDB 标记、`isLocked` 任一即算「有主」）。
  合并方式是把整个目录的 `videoFile.movieId` 改指到最小那条并改名，剩下空掉的影片行删掉，
  播放进度与字幕覆盖都在 `videoFile` / UserDefaults 上，不受影响。单次扫描最多处理 200 个目录。
- **TMDB 集数校验**：`TMDBService.tvSeasonEpisodeCount(tvId:season:)` 读 `episodes.count` 并缓存；
  短剧分支命中后要满足 `远端集数 ≥ max(5, 本地集数 / 3)` 才认，
  本地一百集对上十二条目的错片会被拒掉并按本地入库。豆瓣来源不给集数，不做这一步校验。
- 验证：`xcodebuild build` 通过；纯逻辑 harness 扩到 16 组断言（新增重分类判据三条）全过。

未做：短剧条目的竖屏海报优先与分集懒加载、手动匹配弹窗的来源切换、可选短剧专源、
把 `mediaKind` 也给普通剧集写上值（首页「电影 / 剧集」两档筛选要等它）、Windows / 安卓 TV 对齐。

## 15. 真机反馈修正（2026-10-26，第 3 期）

第一个真实样本：`/Volumes/1T/电影/持械入宋 S07（100集） - SKIT`（100 个 `持械入宋 - S07Exx.mp4`，
单集约 14MB，目录内另有 `poster.jpg` / `tvshow.nfo`）。截图看刮得不错——干净片名、竖版海报、
真简介、分集标到「第7季第1集」。但拆开看走的是**旧的剧集分组路径**：`S07` 命中
`isLikelyTVEpisodePath` 里的 `\bs\d{1,2}\b`，100 个文件本来就归成一条，正常走 5 轮 TMDB，
所以短剧分支根本没参与，`mediaKind` 是空的，首页「只看短剧」筛不出它。
§14 的重分类也放过它——那些行有海报，`shouldMergeExistingDirectory` 判定「有主」直接跳过。

按这个样本补了四处：

- **打标与合并分家**：新增 `ShortDramaClassifier.shouldTagExistingDirectory`（与 metadata 无关），
  重分类对「整个目录已经归成一条记录」的短剧只写 `videoFile.mediaKind`，不合并、不改标题、
  不动海报简介，旁表缺记录时补一条 `source = local`。原来那套「有主就不动」只用来管合并，
  因为只有合并会覆盖用户的东西。`reclassifyExistingShortDramaEntries` 的返回值换成
  `ShortDramaReclassification`，把合并数与打标数分开报，日志一行看得到两种。
- **体量闸门**：文件数与集号在长剧上完全同形（一部 76 集国产剧也是几十上百个文件），
  150–400MB 的单集大小又落在判据的空白区。现在要求「中位文件 <150MB」或「已知平均时长
  45–420s」至少要成立一条，否则不判短剧，`ShortDramaVerdict.contentConfirmed` 记录这条证据；
  激进档按它的定义（文件多即认）越过闸门。S07 这个样本单集 14MB，闸门照过。
- **季号**：`seasonNumber(inPath:)` 认 `S07` / `Season 7` / `第7季`。详情页对带季号的短剧
  保留「第7季第N集」，没有季号才用干净的「第N集」；`searchKeyword` 也把季号从搜索词里剥掉，
  S07 目录现在给出「持械入宋 SKIT」。
- **豆瓣打分补一条子串规则**：目录名常带制作方、季号这类尾巴，豆瓣的干净标题会被检索词包住。
  `matchScore` 加了「标题（或别名）是检索词的子串」= 85 / 70，并要求被包含的一方不少于 4 个字符，
  免得「家」这种单字标题靠子串乱认。上面那条搜索词对上豆瓣同名条目即拿 85，过 §6 的接受线。
- **首页按钮合并**：类型筛选原来是独立的一颗下拉，跟「分类」并排像两个重复按钮，
  现在合成一个 `Menu`：`Picker("类型")` 内联在分类菜单顶部，按钮图标随筛选变，
  选中非默认档时胶囊描边高亮并显示「短剧」小标签，tooltip 汇总当前筛选（分类 + 短剧部数）。
- **旁表会被级联吃掉**（读线上库发现的，`~/Library/Application Support/OmniPlay/omniplay.sqlite`）：
  真机这条目录 100 个文件都已标 `mediaKind = shortDrama`、归成一条 336651，
  但 `shortDramaMetadata` 整张表是空的。原因在 `saveScrapingResult`：刮削把负数占位影片行
  换成真实 id 时，用的是「先 `deleteOne` 再 `save`」，而占位行随后就被删——
  `shortDramaMetadata` 的外键是 `ON DELETE CASCADE`，行在两步之间被级联带走（`try?` 又把写失败吞了）。
  现在两处 rekey（`saveScrapingResult` 与 `rekeyShortDramaMetadata`）都换成一条
  `UPDATE shortDramaMetadata SET movieId = ? WHERE movieId = ?`（`ShortDramaMetadata.rekey`），
  并加了一道补齐：重分类末尾扫「文件已标短剧但旁表没有行」的条目，按现有证据补来源
  （绑过豆瓣 = douban，已刮到在线条目 = tmdb，其余 local）与集数，日志报「补齐旁表 N 条」。
  老库里像这次一样丢过行的，下次同步自动补回来。

验证：`xcodebuild build` 与 `build-for-testing` 均通过，短剧相关文件已无并发隔离告警
（把 `kindValue`、`threshold`、`doubanIDOffset` 与那几个正则 helper 标了 `nonisolated`）；
纯逻辑 harness 扩到 21 组断言，含按真实目录还原的 S07 用例（判定、季号、搜索词、打标判据、
豆瓣子串规则、模糊区间与激进档）。`OmniPlayTests/ShortDramaScrapeTests.swift` 同步加了
「模糊区间」「带季号短剧」「只补标签」「标题被包住」四个用例，另加一条 `DatabaseQueue` 上的
旁表用例（先验证换主键不被级联吃掉，再验证缺行补齐后来源是 tmdb、集数 100，且第二次跑不再补），
SQL 本身也单独用 `sqlite3` 在临时库上验过级联损失与补齐幂等。命令行跑不了 ⌘U。

## 16. 落地记录（第 4 期：形态落库与筛选扩档，2026-10-26）

§14 留的「把 `mediaKind` 也给普通影视写上值」这期做掉，顺带把详情页一百格分集的
主线程解码问题一起处理了。

- **形态取值**：`MediaKindResolver` 判 `movie` / `tv`，只认集号（`SxxExx`、`第N集/话`、`EPn`）
  和片名里的「第N季」。刻意不看文件数：两碟电影（CD1/CD2）有两个文件但不是剧集。
  `videoFile.mediaKind` 的注释本来就写着取值是 movie / tv / shortDrama，这期把缺的两档补上。
- **写入时机**：`saveScrapingResult` 按来源给的 `media_type` 落库（短剧条目保持 `shortDrama`，
  不被剧集结果降级）；扫描入库时分集判不出来的先留 `nil`。
- **旧库补齐**：重分类末尾加 `backfillLegacyMediaKind`，把 `mediaKind` 还空着的条目按
  文件名与片名补成 movie / tv（单次最多 4000 个文件，失败不带崩整轮同步）。
  体量闸门拦下的短剧候选目录跳过 —— 用 `ShortDramaVerdict.blockedByContentGate` 标出来，
  只在该目录形状确实像短剧（文件数或集号 ≥30）时才置位，二十来集的普通剧集目录照常贴标签。
  不跳过的话形态就被贴死，之后调灵敏度也救不回来。
- **首页筛选扩到四档**：`LibraryKindFilter` 加 `movie` / `tv`，`kindValue` 直接映射
  `rawValue`，`symbolName` 交给档位自己给（`film` / `play.tv` / `play.rectangle.on.rectangle`）。
  查询从 `fetchShortDramaIDs` 换成 `fetchMovieIDsByKind`（一条 GROUP BY 出全部档位），
  切档不再查库。旧库要等第一次同步把形态补齐，「只看电影 / 只看剧集」才有内容。
- **分集缩略图后台解码**：`EpisodeThumbnailView.loadThumbnail` 原来在 `onAppear` 里
  直接 `NSImage(contentsOf:)`，短剧一百格滚动就是每格一次主线程磁盘解码。改成先 `fileExists`
  再交给 `Task.detached` 解码、回主线赋值。分集格子本来就在 `LazyVGrid` 里，
  行是懒的，所以要做的是解码而不是分页。
- **没做**：竖屏海报优先。现在豆瓣和 TMDB 给的都是竖版海报，卡片 `160x240` 已经吃得住，
  真正的取舍要等红果这类专源同时给竖版封面和横版 backdrop 才有意义（§5.4）。

验证：`xcodebuild build` 与 `build-for-testing` 通过；纯逻辑 harness 扩到 26 组断言
（新增形态判定四条 + 档位映射 + 闸门标记）；`ShortDramaScrapeTests` 新增形态判定、
档位映射、闸门标记与「二十来集目录不占短剧候选」用例。

## 17. 落地记录（第 5 期：豆瓣搜索进手动匹配，2026-10-26）

首页四档筛选在真机验证过（电影 / 剧集 / 短剧都能筛出来）之后，接着补 §8 里的
「手动匹配弹窗的来源切换」。做下来发现不用做成分段控件：弹窗里本来就嵌着
`DoubanBindingPanel`，缺的不是「切来源」而是「豆瓣只能粘链接」。所以直接给这块加搜索，
`MovieSearchModalView` 与独立的 `DoubanBindModalView` 两个入口一起受益。

- **复用现成的搜索链**：`DoubanService.searchSubjects(query:)` + `rankedShortDramaHits`
  一条请求出候选，不新写解析。候选行给竖版小封面、片名、年份、豆瓣评分、
  单集分钟数和「短剧」标记（判据是 `DoubanSearchHit.looksLikeShortDrama`），
  点「绑定」走原来 `bindSubject()` 那条抓取入库的路，不改绑定语义。
- **排序与容错**：对得上片名的排前面，但零分候选也留着排后面（手动匹配本来就要能挑冷门条目），
  最多 6 条；风控或确实没同名时给一句人话错误提示。按钮在请求飞行中禁用，豆瓣闸门
  （12 秒 + 抖动）会在 `acquire()` 里排队，所以文案里说明可能要等十几秒。
- **留多条是实测决定的**：`search_text=持械入宋` 返回 14 条，`38589478 持械入宋 (2026)`
  之外全是分季条目（`持械入宋：第一季`、`第五季`、`第三季`…，abstract 都是「短片 / 2分钟」）。
  分季标题对不上检索词按 §15 的规则拿 0 分、排后面，`S07` 目录要的「第七季」就可能被截掉，
  所以放宽到 6 条，并在按钮 tooltip 里说明可以在输入框补季号再搜一次
  （输入框装着已绑定的链接时，搜索自动退回片名）。
- **布局**：弹层不再写死 `height: 300`，改 `.fixedSize(horizontal: false, vertical: true)`
  跟内容走；候选列表用普通 `VStack` 而不是 `ScrollView`——自适应高度的弹层里套滚动容器，
  `ScrollView` 的理想高度会把布局撑崩。
- **顺手改掉一句过期文案**：原来写「豆瓣不会自动搜索豆瓣，也不会参与批量扫描」，
  短剧扫描已经在搜了，改成说明单轮上限。

验证：`xcodebuild build` 通过、无新增告警。候选行的渲染依赖 App 主题与 `CachedPosterView`，
纯逻辑 harness 覆盖不到，需要打开手动匹配弹窗点一次「搜索豆瓣」看真实候选。

## 18. 落地记录（第 6 期：红果短剧补充源，2026-10-26）

先更正 §11 的结论：红果的搜索接口**不需要 `bdms` 签名**，也不需要自建代理。实测

```
GET https://hongguoduanju.com/api/search/result/?query=<关键词>&page_index=1&sort_type=0
```

带桌面 UA 与 `Referer: https://hongguoduanju.com/` 就返回纯 JSON：顶层 `totalCount /
pageNum / totalPages / hasMore`，候选在 `searchList[].video_data`，字段有 `series_id`
（19 位）、`series_title`、`series_intro`、`series_cover`、`episode_cnt`、
`episode_right_text`、`create_time`、`hot_score_data.text`、`category_list[].name`、
`celebrities`。用「持械入宋」试，第一季那条 `episode_cnt` = 100，跟 `/Volumes/1T/电影/
持械入宋 S07（100集） - SKIT` 目录里的文件数一致——而「权威集数」恰好是 TMDB 和豆瓣
对国产短剧最容易缺的一项，`series_intro` 也是短剧里少见的完整简介。

**只做补充源，不决定条目身份。** 红果是单一平台库，认不上是常态（下架、改名、独播差异），
如果让它拥有身份，就得把 19 位 `series_id` 塞进 `movie` 主键的命名空间，跟豆瓣的
`+1e10` 偏移一起再叠一层规则。所以链路仍是豆瓣 → TMDB → 本地兜底决定「这部是谁」，
红果在三个落点都过一遍，只往最终那条 `shortDramaMetadata` 上补字段：

- `ShortDramaMetadata.enrich(...)` 只填值为 `NULL` 的列，已有值一律不覆盖；只有新建行或
  `source='local'` 的行才把 `source` 写成 `hongguo`，豆瓣/TMDB 行保持原有出处；
- `overview` 只在为空、或是我们自己写的占位（`已识别为短剧` / `已按本地目录入库` /
  `正在排队` / `短剧 · 共`）时才替换，`posterPath` 只在为空时写，不抢已经刮好的海报；
- 补的是 `episodeCount`、`tags`（`category_list` 分类名）、`platform='红果'`、
  `posterOrientation='portrait'`、`externalId=series_id`。

**季号必须参与比对。** 红果和豆瓣都把国产短剧按季拆条目（`持械入宋：第一季`、`第七季`…），
`S07` 目录错绑到第一季会塞进来一段看着像真的但说错内容的简介。新增 `ShortDramaTitleMatch`：
`splitSeason` 认尾部 `第N季`（含「七」「十」「二十一」这类中文数字）与 `S07`，
`seasonAdjusted` 给同名同季 `+10`、明确错季 `−70`、候选没标季号 `−15`；
`hongguoHit(forQuery:season:)` 只接受最终得分 ≥ 20 的候选。豆瓣侧同步换成
`DoubanSearchHit.matchScore(query:season:)`，§17 那 14 条分季候选因此不会再把「第七季」挤掉。
错季扣分远大于对季加分是有意的：宁可不补，也别补一段错内容。

**礼貌与失败处理。** `HongguoRequestGate` 最小间隔 2 秒 + 0.5–1.5 秒抖动（一轮扫描要刮
几十部剧，豆瓣那套 12 秒风控红果没有，但也不能贴着打）；403 / 429 停 6 小时，冷却时间写
`hongguoRequestCooldownUntil`，重启仍生效。无同名与请求失败都只记一行日志后继续原来的落库路径，
红果挂了不会让短剧刮削变差。

**开关。** 设置页新增「查询红果短剧，补充集数与简介」（`enableHongguoShortDramaSource`，默认开），
短剧总开关关掉时置灰。

验证：纯逻辑 harness 新增三组断言（`hongguoSearchPageParses` 用真实响应裁剪的 fixture、
`seasonAwareMatching`、`enrichmentKeepsExistingProvenance`）全绿，`xcodebuild build` 与
`build-for-testing` 通过。真机还需确认一次：同步短剧源后
`select source, platform, episodeCount, posterOrientation from shortDramaMetadata`
应出现 `hongguo / 红果 / 集数 / portrait`。

遗留两条已在 §20 结掉：红果封面实测就是竖版，横竖优先这条不成立；
手动匹配已经能搜红果并手选绑定。

## 19. 真机反馈修正（第 7 期：WebDAV 短剧刮完不见了，2026-10-26）

现象：WebDAV 源的 `持械入宋 S01（100集） - SKIT` 扫描成功、100 个文件都入了库，
首页却没有这部。查库看到的是：`mediaKind='shortDrama'`（判定没问题，形态标签也写进去了），
但 100 行的 `movieId` 全是 NULL——全库唯一一批 NULL 就是它，`shortDramaMetadata`
和 `doubanMetadata` 里也都没有这部。

原因是 `applyDoubanShortDramaHit` 把「删占位行」放在了「分集改指」前面。
`videoFile.movieId` 的外键是 `ON DELETE SET NULL`，删占位行那一刻 100 行就集体解绑，
紧接着的 `UPDATE ... WHERE movieId = 占位id` 匹配 0 行；扫描收尾那句
`DELETE FROM movie WHERE id NOT IN (SELECT DISTINCT movieId FROM videoFile ...)`
见刚插进来的豆瓣条目没人引用，顺手删掉，`shortDramaMetadata` 再跟着级联走。
TMDB 那条路径（`saveScrapingResult`）顺序本来是对的——先改指再删——所以本地那个
`S07` 目录一直没事，豆瓣落库这条路是第一次被真实数据走到才暴露。

修复：`applyDoubanShortDramaHit` 的落库顺序改成 **建正条目 → 分集改指 → 挪
`doubanMetadata` → 写 `shortDramaMetadata` → 最后删占位行**，与 TMDB 路径一致。
回归用例 `doubanBindingKeepsEpisodeFilesAttached` 用真 schema 的外键建表，
既断言新顺序下 100 集都挂在豆瓣条目上，也断言旧顺序会留下 100 个孤儿行。

已经躺在库里的这批孤儿行也要能自己爬回来，否则增量扫描会一直跳过它们（路径已经存在）：

- 「算已入库」的判据加了条件，`movieId` 为空或指向不存在条目的行不再算已入库；
- `insertPendingMediaGroup` 遇到同路径的旧行改成**原地更新**（补 movieId、形态、大小），
  不再插第二条，这样播放进度和按 file id 缓存的分集缩略图都不丢。

下次同步该源即可重新收编，并重新走一遍来源链（会再花一次豆瓣配额）。

顺带清掉 6 条 MainActor 隔离告警：`HongguoHit.coreTitle` / `season`、红果那个文件内
`firstMatch`、`sanitizedFallbackTitle`、`shortDramaAwarePlaceholderOverview` 补 `nonisolated`；
`backfillLegacyMediaKind` 里被并发闭包捕获的 `byEntry` 先定成常量。

验证：用 `sqlite3` 按真 schema 复刻两种顺序做对照，旧顺序是
`orphans=6 / live_entries=0 / sdm=0`，新顺序是 `orphans=0 / live_entries=1 / bound=6 /
sdm=douban/9`，且原地更新保住已有播放进度；`build-for-testing` 通过，剩下的唯一告警
`BusinessLogicIntegrationTests.swift` 里的 `formattingError` 弃用与本次无关。

## 20. 落地记录（第 8 期：手动匹配支持红果，2026-10-26）

先结掉 §18 留的那条「竖版海报优先」：拿 `持械入宋：第一季` 的 `series_cover` 实测，
`640×853`，是竖版；豆瓣与 TMDB 的 `poster_path` 同样都是竖版，`movie` 表里也没有
backdrop 这类横版字段。横竖冲突并不存在，这条不做，`posterOrientation` 如实记 `portrait` 就够。

接着做 §18 的另一条：手动匹配里能搜红果并直接绑定。入口在 `DoubanBindingPanel`
（`MovieSearchModalView` 顶部嵌的就是它，独立弹层 `DoubanBindModalView` 也一起受益），
「搜索豆瓣」旁边加「搜索红果」，受 `enableShortDramaScrape` 与
`enableHongguoShortDramaSource` 两个开关控制，都关掉时整块不出现。

- **绑定入口 `HongguoBinding.bind(_:movieId:)`**：条目身份沿用现有主键，红果那 19 位
  `series_id` 只写进 `shortDramaMetadata.externalId`，不往 `movie` 主键里塞新命名空间——
  这与 §18 定的「红果只补字段、不决定身份」一致，手动绑定也不例外。写入的是标题、年份、
  简介（`series_intro` + `· 共 N 集`）、竖版封面和分类标签，同时把该条目下的文件标成
  `mediaType='movie'` + `mediaKind='shortDrama'`，免得下次扫描重刮。条目 `isLocked`
  时只补出处、不动用户手填的内容。
- **为什么这里改标题而豆瓣绑定不改**：豆瓣有自己的 `doubanMetadata` 表给详情页读，
  绑定只需落那张表；红果没有旁表可读，写回条目本身才是这件事的目的。扫描链上的
  `applyDoubanShortDramaHit` 也是直接改条目，两边口径一致。
- **候选列表**：复用 `HongguoService.rankedHits`，季号校正与自动刮削同一套；
  对不上片名的候选也留着排后面（手动匹配要能挑冷门条目），最多 6 条；候选行给竖版封面、
  片名、年份、`共 N 集`、热度和分类。状态行收成一处 `statusLine`，
  「正在请求豆瓣 / 正在查询红果 / 已绑定红果条目」三种提示共用。

验证：`build-for-testing` 通过，只剩 `ChineseNameTranslationTests` 与
`BusinessLogicIntegrationTests` 里两条与本次无关的既有告警；纯逻辑 harness 仍全绿
（`HongguoBinding` 只依赖 `AppDatabase` 与 `PosterManager`，harness 按接线代码跳过）。
弹层里的候选渲染要真机点一次才算数：搜红果 → 挑某一季 → 绑定 → 首页该条目应换成
红果的竖版封面与集数，`shortDramaMetadata` 应出现 `source='hongguo'`、
`platform='红果'`、`externalId=<series_id>`。

**真机第一次点击的修正**：绑定后封面简介还是原来那套。查库发现
`shortDramaMetadata` 已经落成 `hongguo / 红果 / 7671966197985856536 / 100 集`，
但 `movie.isLocked = 1`（这条先前手动匹配过 TMDB），而我给 `HongguoBinding.bind`
加了「锁定的条目不动内容」——于是只落了出处，标题简介封面一个没写。
判断错了：锁定要挡的是**自动刮削**覆盖手填内容，用户亲手点绑定本身就是决定，
所以改成照样写入，只留一条例外——红果只给到年份，已有更精确的 `releaseDate` 不降级。
反过来，自动那条 `applyHongguoEnrichment` 补上了 `!movie.isLocked` 的判断（此前会替
锁定条目补占位简介），并改成只在真改了字段时才 `update`。

另外确认了红果 CDN 不挡盗链：`p3-novel.byteimg.com` 那张封面在不带 Referer、
甚至空 UA 下都是 `200 image/jpeg`，所以 `PosterManager.downloadPoster` 直接就能拿到，
落盘名走既有的 `ext_<percent 编码 URL>`，`CachedPosterView` 收 `PosterUpdated_*`
广播自愈，不需要为红果单开一条取图路径。

**弹层被候选撑穿**：手动匹配那张是固定 `640×650`，而候选列表按内容长，豆瓣加红果
最多 12 行直接把结果区挤没、内容上下互相压着，又没有滚动条。改成：候选区合并成一个
滚动容器，高度自己按行数算（每行约 62pt + 6pt 间距，封顶 320pt，超了出滚动条）；
删掉那段三行的说明文字；输入框收成半宽（`maxWidth: 280`），「在浏览器搜索豆瓣」
（带 safari 图标的文字按钮，另留 tooltip）与「绑定并抓取」并到输入框后面同一行，
底部只剩「解除绑定 / 取消」，两处都空时整行不渲染；弹层高度放到 `640×760`。


## 迁移补记（10-28）

v14 给 `videoFile` 加 `mediaKind` 之前先查一次列是否存在。老库走的是「先结构自愈、再走迁移阶梯」这条路，
v13 里的 `healSchemaGaps` 已经把 `mediaKind` 补上了，v14 再无条件 `ALTER TABLE ADD COLUMN` 就是
`duplicate column name: mediaKind`，整条阶梯停在 v14，库直接报「媒体库升级未完成」，扫描写库跟着变成
`FOREIGN KEY constraint failed`。x64 镜像同步记录见 [2026-10-28-sync-with-arm-1.10.md](../../OmniPlay开源-mac-x64/2026-10-28-sync-with-arm-1.10.md)。
