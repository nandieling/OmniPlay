# 2026-10-07 导演/主演与文件夹作品名中文译名（ARM mac 版）

## 问题

- 刮削得到的导演、主演名显示为 "Christopher Nolan" 这类外文；按导演/主演分类后，文件夹名跟着是外文。
- 分类文件夹详情页的作品简介里，部分片名也是外文（例如 "Inception"）。

## 原因

TMDB 的本地化在缺少中文条目时会直接回传原文，而不是报错：

1. `/movie/{id}/credits?language=zh-CN`、`/tv/{id}/credits?language=zh-CN` 的 `name` 没有中文译名时返回原文人名；
2. `/person/{id}/combined_credits?language=zh-CN` 的 `title` 没有中文译名时返回原文片名。

原实现只依赖这两处返回的本地化字段，也没做任何兜底与缓存，所以缺中文的条目永久停留在外文。

## 修复内容

- 新增 `ChineseNameStore`（中文译名补全 + 落地）：
  - 刮削拿到演职员名单后，把外文人名换成 TMDB 中文名再入库；`PersonProfileStore`、分类文件夹、`Movie.directors/casts` 同步使用中文名；
  - 人物作品列表用 TMDB 官方别名补中文片名，仍拿不到时保留原名；
  - 查询结果写入新表 `chineseNameCache`（v10 迁移），含"TMDB 确认无中文译名"的负缓存（30 天过期），请求失败不写负缓存，下一轮重试；
  - 首次解析出中文名时迁移已有数据：`movie.directors/casts`、`categoryFolder`（含用户手选封面）、`personProfile`（含头像与作品列表缓存），已存在同名中文档案时按补空合并，不覆盖用户手选内容。
- `TMDBService` 新增 `fetchChinesePersonName`（人物详情 zh-CN 名称 → 中文译名 → `also_known_as` 中文别名）与 `fetchChineseTitle`（`alternative_titles` 中按 CN/SG/TW/HK 取中文片名）；挑名逻辑拆成 `chinesePersonName`/`chineseAlternativeTitle`/`chineseName` 纯函数，并按现有规则统一转简体。
- 启动后 5 秒在后台补齐历史库（`backfillExistingChineseNames`）：先补人名，再补作品名，单个人物档案最多补 20 条片名，顺序请求限速；补齐有变更时发 `libraryUpdated` 让首页与详情页刷新。
- 英文界面（`appLanguage = en`）不启用自动补全，保留原文名。
- 分类文件夹卡片右上角的编辑弹窗（原"选择封面"）新增「导演/主演名称」输入框：TMDB 没有中文译名时可手动填中文名，保存后同步更新影片名单、文件夹封面与人物资料，并把该名字写进 `chineseNameCache`（按人物 id + 刮削到的名字各记一条），下一轮刮削继续沿用，不会退回外文名。「未标注」分组不显示输入框。

## 同批附带修改

- 设置页去掉「首页分类」区块（含说明文字与 `homeCategoryMode` 的 @AppStorage）。分类方式仍由首页「所有影视」右侧的下拉按钮切换，设置项本来就与它重复。
- 修掉 `PersonProfileStore` 的 3 个 Swift 6 警告：模块开了默认 MainActor 隔离，`Task.detached` 里直接读 MainActor 隔离的 `TMDBService.shared.currentCredentialSource()` 报"未标 await"；`NSLock` 的 `lock()/unlock()` 不能在 async 上下文里用。改为：状态读写全部收进 MainActor 上的同步小函数（`backfillStarted`、`filmographyFetching` 天然串行，不再需要锁），detached 任务里用 `await MainActor.run` 读凭据状态，作品列表拉取拆成 `runFilmographyFetch` 后显式清理并发标记。
- TMDB 请求移出主线程：新增 `nonisolated` 的 `TMDBService.probeConnection`（自带 8s/10s 超时的独立 URLSession）与 `performRequest`（扫描前探测与所有 TMDB 接口共用），扫描流程与设置页「验证」按钮都用 `Task.detached` 发起探测，原 `checkConnection` 包装方法随之删除。原因是本模块默认 MainActor 隔离，`Task { }` 里 await 的非隔离函数仍按调用方隔离执行，刮削前的连通性检测和 URLSession 准备工作都落在主线程，表现为检测期间指针掉帧；`nonisolated` 后请求体在全局执行器上执行。`credentialSource`/`proxyAPIBaseURL`/`resolvedAPIURL`/`makeRequest`/`authenticatedURL`/`retryDelayNanoseconds` 与两个静态常量随之改为 `nonisolated`，`TMDBCredentialSource.isAvailable` 改 switch 判断（避免依赖 MainActor 隔离的 Equatable 合成）。
- 播放器补回播放结束时的状态复位：改用 `pause` 判定播放/暂停后，播完回到 idle 时 `pause` 仍读到「未暂停」，`isPlaying` 不会转假，息屏断言不释放。现在按 `idle-active` 且当前无文件才判定播放结束（`playbackEnded`），x64 版同步同一实现。

- 说明：扫描/刮削主体 `MediaLibraryManager`（GRDB 读写、首页刷新）仍受模块默认 MainActor 隔离影响跑在主线程，这次只把 TMDB 网络与探测部分摘了出去；彻底解决需要给该类做去隔离改造。

## 验证

- `xcodebuild -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx build` 通过。
- `xcodebuild ... test -destination 'platform=macOS' -skip-testing:OmniPlayUITests` 全部通过（36 项；本轮改动的 4 个文件已无编译告警，含新增 `ChineseNameTranslationTests`：改名迁移覆盖影片名单/文件夹封面/人物档案合并，手动改名后再刮削不回退，以及 TMDB 人物详情与别名 JSON 的取名规则）。
- UI 测试 target 在本机因系统认证问题无法启动，与本次改动无关（同历史结论）。
