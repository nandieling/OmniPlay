# NeoDB 同步：已看记录 / 评分 / 评论

日期：2026-10-19　适用端：macOS（ARM 基准版，x64 同步）　状态：已实现，Windows / 安卓 TV 待移植

## 1. 目标

把本地媒体库里用户对一部影视的三件事同步到 NeoDB：

1. 已看记录（想看 / 在看 / 已看 / 搁置，即 NeoDB 的 mark + shelf_type）
2. 评分（NeoDB 的 `rating_grade`，1…10，界面按五星半格输入）
3. 评论（NeoDB mark 的 `comment_text`）

站点固定为官方 `https://neodb.social`（不提供自建实例地址输入），下面出现的接口路径都相对站点根地址。登录方式两种，任选其一：

- 账户密码登录：`POST /v0/password_login`，换回 access token
- API 令牌：用户在站点 `/developer/` 里建好的 token 直接粘贴

另附 OAuth 授权码（OOB）登录作为标准兜底：`/api/v1/apps` 注册应用 → 浏览器授权 → 用户回填授权码 → 换 token。

## 2. NeoDB 接口事实（按 neodb-social/neodb 主干源码核对）

| 用途 | 请求 |
| --- | --- |
| 账户密码换令牌 | `POST {instance}/v0/password_login`，JSON `{identifier, password}`，返回 `{token}` |
| 校验令牌 / 取账户 | `GET /api/me` → `username` / `display_name` / `url` |
| OAuth 注册应用 | `POST /api/v1/apps`（`redirect_uris=urn:ietf:wg:oauth:2.0:oob`，`scopes=read write`） |
| OAuth 换令牌 | `POST /oauth/token`，`grant_type=authorization_code` |
| 按外链定位条目 | `GET /api/catalog/fetch?url=…`：命中 302→条目 JSON；正在抓取 202；未收录 404 |
| 读某条目当前标记 | `GET /api/me/shelf/item/{uuid}`，未标记返回 404 |
| 批量读标记 | `GET /api/me/shelf/items/{uuid1,uuid2,…}`（≤20） |
| 写标记 | `POST /api/me/shelf/item/{uuid}`，body `MarkInSchema` |
| 删除标记 | `DELETE /api/me/shelf/item/{uuid}` |
| 写进度 | `POST /api/me/shelf/item/{uuid}/progress`，body `{type, value}` |
| 拉某书架 | `GET /api/me/shelf/{type}?category={movie|tv}&page=N` |

`MarkInSchema`：`shelf_type`(必填) + `visibility`(必填, 0 公开 / 1 关注可见 / 2 仅自己) + `comment_text` + `rating_grade`(0…10) + `tags` + `created_time` + `post_to_fediverse`。`shelf_type` 取值 `wishlist` / `progress` / `complete` / `dropped`。注意：POST 时缺省 `rating_grade`、`comment_text`、`tags` 字段会把它们清空，所以每次写标记都全量带上。

条目的站外 id 从 `external_resources[].url` 里解析（豆瓣条目页 / TMDB movie、tv 页），推送前用它去重、导入时用它匹配本地影片。

## 3. 本地数据

海报墙上电影和剧集都是 `movie` 表的一行（`movie.id` 存的是 TMDB 的 movie id 或 tv id，`tvShow` 表实际未使用），所以本地标记的主键只带本地 id、不带类型；类型只决定往 NeoDB 发哪条站外链，由文件名和目录结构推导，和刮削时判断剧集用的是同一套规则。

新增表 `mediaMark`，一行对应一部本地影视：

| 列 | 含义 |
| --- | --- |
| `key` | `item:{localId}`，主键 |
| `kind` / `localId` | `movie`\|`tv`（推导出来的）与本地 id；`localId < 0` 是未刮削占位条目，不参与同步 |
| `shelfType` | 本地标记，空串表示无标记（同步时执行删除标记） |
| `ratingGrade` | 0…10，0 为未评分 |
| `comment` | 本地评论 |
| `progressPercent` | 0…100，由播放进度推导，写进 NeoDB 的 progress |
| `isManual` | 是否用户手动编辑过；播放进度推导出的标记不会覆盖它 |
| `updatedAt` / `syncedAt` / `lastError` | 脏标记靠 `syncedAt < updatedAt` 判断；失败原因留在 `lastError` |

## 4. 代码落点

| 文件 | 职责 |
| --- | --- |
| `OmniPlay/NeodbModels.swift` | 标记模型、书架/评分映射、外链 URL 与 id 解析、请求体与响应 DTO、错误文案（纯逻辑，可单测） |
| `OmniPlay/MediaMarkStore.swift` | `mediaMark` 建表与增删改查、待同步队列、由播放进度推导标记、导入时落库 |
| `OmniPlay/NeodbService.swift` | HTTP 客户端：登录、`/api/me`、catalog fetch（含 202 重试）、读写标记、进度、拉书架 |
| `OmniPlay/NeodbSyncService.swift` | 编排：登录态、待同步计数、推送（限速 1.2s/条）、导入、状态回写与 UI 发布 |
| `OmniPlay/NeodbSettingsView.swift` | 设置页「NeoDB 同步」区块：三种登录（逐行输入框、左对齐）、可见性与同步开关、同步与导入按钮；站点固定为官方实例，不提供自建实例地址输入 |
| `OmniPlay/NeodbMarkEditorView.swift` | 弹窗里的标记 / 评分 / 评论编辑区，以及片名右边显示当前标记的 `NeodbMarkStatusButton` 与弹窗容器 `NeodbMarkSheet` |
| `OmniPlay/AppDatabase.swift` | v12 迁移建表，并把 `mediaMark` 纳入结构自愈清单 |
| `OmniPlay/SettingsView.swift`、`OmniPlay/MovieDetailView.swift` | 设置页接入区块；详情页片名右边放按钮，点开弹窗编辑 |
| `OmniPlayTests/NeodbSyncTests.swift` | 映射、URL/id 解析、请求体编码、响应解析、脏队列与导入落库 |

交互：详情页只在片名右边放一个按钮，按钮文字就是当前标记（未标记时显示「NeoDB」，有未同步改动时右侧亮小橙点），点开才给标记 / 评分 / 评论的编辑区；设置页固定走官方站点 https://neodb.social，不需要填实例地址。

## 5. 同步语义

- 推送：只处理 `syncedAt < updatedAt` 的行。先用站外链解析条目 uuid：有绑定的豆瓣条目就直接信它，只有 TMDB 编号时拿两条链接（本片类型优先、另一类型兜底）去试并校验标题，避免 `movie/123` 和 `tv/123` 同号不同片把评分写歪；中英文名对不上时不硬拦，退回首选那条候选。再读远端当前标记，只有书架 / 评分 / 评论确实不同才 `POST`（字段全量带上，免得被清空），避免重复刷时间线；本地是未标记而远端有标记则 `DELETE`；`progress` 书架额外写一次播放进度百分比。
- 导入：翻 `complete` / `progress` 两个书架（`category=movie`、`tv`），按豆瓣 / TMDB id 匹配本地影片；只在远端时间更新、或本地缺该项时写入，不会覆盖本地更新的编辑。
- 限速与容错：串行、每条 1.2s；202 等 2.5s 重试最多 3 次；401 直接停止整轮并提示重新登录；单条失败记录原因后继续下一条，结果汇总成几行状态文本给用户看。
- 凭据：token 存系统钥匙串（`nan.omniplay.neodb.token`，仍按主机分键），可见性、是否发到联邦宇宙等非敏感项走 `UserDefaults`；早期版本存过的自建实例地址会在启动时清掉。

## 6. 验证

- ARM 基准版：`xcodebuild build` 通过；`xcodebuild test -only-testing:OmniPlayTests` 全绿（59 项，其中本次新增 18 项）。用例覆盖评分与五星互转、播放进度推导标记、豆瓣 / TMDB 外链解析、`MarkInSchema` 编码（缺省字段会被清空所以全量带）、`/api/me` 与书架列表（裸数组与 `data` 包装两种）、`parent_uuid`、实例地址归一化、跨语言标题校验、脏队列筛选、手工标记不被播放历史覆盖、导入落库与进度回填、占位条目不入队。
- x64 版：`xcodebuild test -destination 'platform=macOS,arch=x86_64'` 把 app 和 `OmniPlayTests.xctest` 都编译链接成 x86_64 通过；本机 x86_64 测试运行器在启动阶段挂住（Rosetta + 本机环境），用例执行以 ARM 端为准，两侧源码逐字节一致。
- `OmniPlayUITests-Runner` 在本机仍连不上测试会话（历史结论相同），与本次改动无关。
- 真机联调需要能访问实例的网络：本仓库所在网络访问 `neodb.social` / `api.neodb.ai` 不通，未能跑线上冒烟。

## 7. 后续端

- Windows（Avalonia + .NET）：`NeodbClient.cs` + `MediaMarkRepository`，设置页同名区块，复用同一套接口；凭据存 Windows 凭据管理器。
- 安卓 TV（Java + libmpv）：遥控器输入账户密码很痛苦，优先做令牌 / OAuth OOB 粘贴（配合手机），TV 端只做推送不做导入。
- 两端都以本文第 2 节的接口表为准，字段含义不再重复解释。
