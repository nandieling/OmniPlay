# 安卓 TV 1.30：开软件就是海报、一部剧只留一张、名称年份按 mac 现算

对应反馈四条：打开软件已刮削海报先变白板；同一部剧集的两季 / 两集挂在两张相同海报下；
`Bicycle Thieves 1948 CC Blu-ray ...` 被刮成 2026 年同名新作；
`流人.Slow.Horses.2022.S01.../Slow.Horses.2022.S01E01...mkv` 被提取成「接住流星的人 / 2025」。
查下去还挖出第五条：刮削跑过一轮之后再也不发请求，左上角那句「待刮削 N」也一起消失。

## 一、开软件那一下白板：冷启动缺的是同步铺图

`ImageLoader` 的内存缓存挂在 `ImageView`  tag 与一次 `AsyncTask` 上，冷启动整面墙第一次
`load()` 全部走「先占位字母、再异步解码」那条路，于是用户先看见一屏白卡片，海报一张张补
上来。两处改动：

1. 内存缓存换成进程级 `LruCache`（`MEMORY`），面板开合、Activity 重建都不再把它丢掉。
2. 已刮削条目的第一次加载走新的 `syncFromDisk` 分支：本线程直接从
   `<cacheDir>/image-loader/<sha256>.thumb.jpg` 解码缩略图并铺上，不经过占位图。缩略图在
   第一次成功取图时按 1.3 倍目标尺寸、JPEG 82 质量落盘（`writeThumb()`）；老缓存里只有整图
   的，命中整图时顺手补一张，越用越快。同步解码有一道 14MB / 滚动一秒的预算，超了退回异步，
   首帧不会因为解码把遥控器手感拖成卡顿。

## 二、两张相同海报：TV 版必须主动收拢

mac 版的作品表以 TMDB id 为主键，同一个作品结构上不可能有两行；TV 版的实体 id 是自增的，
同一部剧的 `流人.Slow.Horses.2022.S01.../` 和 `Slow.Horses.S02.2023.../` 各自建了一条
`tvShow`，刮到同一个 TMDB id 也就是并排放两张一样的海报，同季两集只是让它们更像。

新增 `LocalMediaLibrary.collapseDuplicateWorks(mediaType, preferredId, onlyTmdbId)`：按
`tmdbId GROUP BY HAVING COUNT(*) > 1` 找出重复作品，留下刚刮到资料的那条（不在场就留挂着
文件多的那条，播放进度不丢），把文件行的外键搬过去、剧集分集行 `INSERT OR IGNORE ... SELECT`
搬过去、队列行与败者实体删掉。挂在三个位置：每次自动刮削写完结果后（只查这一条 TMDB id）、
手动匹配关联之后、以及每轮收尾对整库扫一次，把以前建的重复一并收掉。

## 三、名称与年份：从「扫描时的旧快照」改成「刮前按代表文件现算」

四条投诉里三条是同一个根因。TV 版刮的是队列里存的那份快照——`query = 实体标题`、
`year = substr(releaseDate,1,4)`（未刮削的行本来就是空串）、`secondaryQuery = ''`，
而且只发一次 `search/multi`。年份加权因此完全失效，同名作品由热度决定生死，2026 那部同名
新作赢了 1948 原版；中文目录名解析出的片名一旦在 TMDB 只对上不相干的条目，也没有第二条
关键词可以降级，于是《流人》变成《接住流星的人》。

改成 mac 那套（`MediaNameParser` + `processUnmatchedFiles`）：

- 刮前先取该实体的代表文件（`representativeFile`，优先队列行记下的那个），用
  `combinedSearchMetadata(relativePath, fileName)` 现算片名与年份。`extractYear` 用
  `(?<![0-9])((19|20)\d{2})(?![0-9])` 取第一个不接数字的四位数，`chooseReleaseYear` 只在
  片名本身就是年份时看第二个候选，`2012.2009.1080p.mkv` 取 2009、
  `Blade.Runner.2049.2017.2160p.mkv` 取 2049。`流人.Slow.Horses.2022.S01...` 这一路拿到
  中文片名 `流人`、外文库名 `Slow Horses`、年份 2022。
- 五级关键词阶梯：中文片名 → 父目录中文 → 外文名（含 `A.K.A.` / `ＡＫＡ` 拆分）→ 完整清洗名
  → 去空格去数字；每级都带年份，逐条降级，上一条「没搜到」才走下一条。
- 每个关键词查两种语言、各取两页（UI 语言先查，没拿到相关结果或不足三条再补 `en-US`），
  按 `mediaType#id` 去重。`normalizeTitle` 与 mac 一致：丢掉所有非字母数字，
  `Slow.Horses` 与 `Slow Horses` 算同一个名字。
- 评分照 mac 的 `calculateScore`：标题主 / 副命中分别 12000/8000 起，年份 0 年差 +10000、
  1 年差 +5000、10 年以上 −20000；剧集按**那一季的首播年份**比（`/tv/{id}/season/{n}` 的
  `air_date`，带负缓存），不是全剧的 `first_air_date`。
- 新增相关度门槛 `RELATEDNESS_FLOOR = 1200`：最高分那条连标题相关性都不过线就当没搜到，
  交给下一级关键词。手动匹配弹层用的 `searchCandidates()` 不受这条限制——人选的东西不该替
  他筛掉。年份校验容差从 2 年收到 1 年，剧集直接豁免（剧集文件名里的年份常是资源年份）。
- 详情页手动匹配弹层里那个「待刮削名称 / 年份」也改成同一套现算（`MainActivity.scrapeHint`），
  不再显示 `Bicycle Thieves` 配 2026 这种组合。

## 四、刮削跑一轮就不动了：队列行数不能当「有没有活」的开关

`nextScrapeBatches()` 取批次时按行删掉队列行。扫描途中借的那 4 秒（`pumpScrapeDuringScan`）
预算用完就 `break`，这一批剩下没刮的条目行已经没了；而收尾那一次正式刮削和每一次借刮削都写
成 `if (library.scrapeQueueCount() > 0)`，行数归零就被判成「没活可干」。结果是刮到一半整台
机器安静下来：不再发一个 TMDB 请求，左上角那句「待刮削 N」也掉回空，重开软件、点
「扫描刮削」都一样。真机上「卡在待刮削、点海报才刮一张」的另一半原因就在这里。

- `scanAndScrape` 与 `pumpScrapeDuringScan` 都改成**先 `requeueUnscraped()` 补队列**，再问
  有没有活；预算用完 `break` 之前也补一次，队列在任何时刻都等于「库里还差资料的实体」。
- `scrapeQueueCount()` 的口径改成 `队列行 ∪ 未刮削且未锁定的实体`（`UNION` 自带去重），与
  mac 每轮现算未刮削清单同一件事。原来那句只看队列行，`requeueUnscraped` 的补行数改用新的
  `queueRowCount()`。首页那句「待刮削 N 个条目」从此说的是真话。

## 五、收尾那句改按「一次动作一本账」

借刮削那一小段和收尾那一轮各自记账时，同一批条目会被数两遍，出现过「共处理 6 个条目：3 个
没搜到，3 个年份对不上」这种一句话里没有一个更新、海报墙却刚刷了五张的总结。现在一次用户动
作一本账（`tallySeen` 按 `mediaType#entityId` 去重，`scanAndScrape` 开头清零），收尾说
「刮削完成：更新了 N 个条目；X 个没搜到，Y 个年份对不上，合并了 Z 个重复条目。」；一个都没
更新时说的是「这一轮没有匹配到新的资料（…）」。

## 六、验证

JVM 侧 `build/harness/NameYearCheck.java`（拿真源码 + `android.jar` 编译）覆盖两条用户给的
路径、十组年份用例、`A.K.A.` 拆分、Jellyfin / Emby 端点路径，全部通过：`Bicycle.Thieves.1948
.CC.Blu-ray...` 解析成外文库名 `Bicycle Thieves` / 1948；`流人.Slow.Horses.2022.S01E01...`
解析成 `流人` / `Slow Horses` / 2022。

模拟器（`omni_tv1080`）+ 假 WebDAV + 假 TMDB（`build/patches/serve30.py`，故意摆了两颗陷阱：
`bicycle thieves` 同时返回 1948 原版（热度 20）与 2026 同名新作（热度 500），`流人` 只返回
一部不相干的 2025 华语片）。冷启动走完整流程后假服务器收到：

```
SEARCH query='Bicycle Thieves' lang='zh-CN' → lang='en-US' → ALT id=1001   # 1948 原版赢下热度 500 的新作
SEARCH query='Toy Story' → SEARCH query='ToyStory'                          # 关键词降级到去空格那一档
SEARCH query='流人' lang='zh-CN' / 'en-US' → SEARCH query='Slow Horses'      # 中文没对上，降级到外文名
SEASON id=8001 n=1 → ALT id=8001                                            # 按第一季首播年份评分
```

`ALT id=7001`（那颗《接住流星的人》陷阱）一次都没出现；首页 `所有影视` 从 12 部收到 **11 部**，
左上角收尾写「合并了 1 个重复条目」，两张 Slow Horses 海报并成一张。重开软件第一张截图上，
已刮削的三张海报已经在屏上（不再有白板那一帧），左上角同时写着
「刮削元数据和海报 (5/7)：Big Buck Bunny」。再冷启动一次，收尾是
「刮削完成：这一轮没有匹配到新的资料（3 个没搜到，3 个年份对不上）」——那六个都是假服务器
故意回错年份的用例，真库上对应的是确实搜不到的条目。
