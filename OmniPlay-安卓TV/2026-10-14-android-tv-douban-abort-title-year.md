# 安卓 TV 1.31：移除就停、豆瓣兜底、片名年份再收一轮

对应反馈十四条，归成四组：移除了媒体源左上角还在扫描；手动匹配要能贴豆瓣链接、豆瓣评分要
上卡片与详情页、TMDB 搜不到时用豆瓣兜底；海报选中别再加暗；剩下的十条全是片名与年份——
`空战群英.Masters.of.the.Air.S01.2024` 刮完还是英文、`Solo Leveling S01 2024` 成了
`THE LEVELING OF SOLO LEVELING`、两部 `劇場版「Fate／stay night [Heaven's Feel]」` 一个提取成
`00000` 一个只剩「劇場版」、`Signal.S01.2016` 与 `The.Long.Season.S01.2023` 都记成 2017、
`Paprika.2006...CMCT` 刮成 `Paprika Western`、`Vinland.Saga.S01.2019` 记成 2005、
`Millennium Actress 2001` 刮成 `The Making of Millennium Actress`。另外两条是交互：年份输入框
光标停在最前面删不掉，从详情页返回首页掉回海报墙顶上。

## 一、移除媒体源：这一轮得当场收手

`removeSource()` 只删源和该源的文件行，扫描线程不知道这件事，于是继续往下走：源循环剩下的源
照扫、`ScanCollector.register` 照登记、刮削照开跑，左上角那句「正在扫描」就一直挂着。现在
`LocalEngine` 立一块 `volatile boolean abortRequested`：

1. `removeSource()` 第一件事就是 `abortRequested = true`，随后才删源、清文件行；界面侧
   `cancelScanAndScrape()` → `removeSource()` → 重画抽屉 → 重读媒体库 → 状态行写「已停止扫描刮削。」。
2. 四个检查点：源循环开头（直接返回「已停止扫描刮削。」）、`ScanCollector.register`（抛
   `ScanAborted`，遍历当场停，一个文件都不再登记）、刮削批次循环（剩下的条目留在队列里，
   下一轮 `requeueUnscraped()` 补回）、扫描途中借刮削的入口。
3. 收尾那句也跟着改口：`summarize` 收尾时若已经立了中止牌，只报「已停止扫描刮削。」，不再报
   一句「刮削完成：……」盖在用户刚做的动作上。

## 二、豆瓣：手动绑定 + 兜底 + 评分展示

mac 版有 `DoubanService` / `DoubanMetadata` / `DoubanBindModalView`，TV 版此前完全没有豆瓣这一路。
这一版照它补齐，语义保持一致：**豆瓣只做手动绑定，不参与自动匹配**；TMDB 搜不到的条目，绑定后
由豆瓣把资料填上。

- `DoubanClient`（新，约 660 行）：`subjectIdFrom()` 认桌面 / 移动版条目链接、也认裸编号（≥5 位）；
  `fetch()` 两次抓取之间至少隔 12 秒并带抖动，抓到 403 / 429 / 验证页就把后续抓取按住 12 小时
  （`douban_cooldown_until` 落偏好，媒体源抽屉与手动匹配都会显示「豆瓣暂时休息中」的截止时间）。
  请求走移动端 UA + `Referer: https://m.douban.com/movie/`。
- 解析优先 JSON-LD（`@type: Movie`、`name`、`alternateName`、`description`、`contentRating`），
  拿不到再退回页面正则：`v:itemreviewed` 取片名（比 `og:title` 干净，不带年份）、`YEAR_IN_TITLE` /
  `YEAR_SPAN` 取年份、「又名」取别名、`<span class="pl">导演 / 主演 / 类型 / 制片国家 / 片长</span>`
  取资料块、评分与评价人数两套候选正则。标题尾巴上的 `(19xx|20xx)` 一律削掉。
- 海报可用性照 mac：只认 `doubanio.com/view/photo/{s_ratio_poster,raw,l}/public/`，`qnmob`、
  `/q/60/`、`/w/300/`、`/h/300/` 这些缩略图一律不当海报用。
- 库里加 `doubanMetadata` 表（数据库版本升到 4，`onUpgrade` 有 `oldVersion < 4` 分支），一条实体一行，
  `key` 为 `movie#<id>` / `tv#<id>`。`saveDouban()` 顺手补空缺的海报 / 导演 / 主演；当这条实体
  `scraped == 0 || tmdbId == 0` 时，直接写片名、上映年份、简介并置 `scraped = 1, isLocked = 1`——
  这就是「TMDB 匹配不到时豆瓣兜底」，同时它不会再被自动刮削覆盖。
- 入口在详情页手动匹配弹层里多出来的一行：说明文字 + 「豆瓣条目链接或编号」输入框 +（已绑定时）
  「解除豆瓣绑定」，按钮行是「关闭 / 手机输入 / 绑定豆瓣（已绑定则显示重新抓取豆瓣）/ 搜索」。
  手机推送的字段表里同时多了 `douban` 一项，长链接从手机贴比遥控器敲快得多。
- 展示：详情页元信息行现在是 `年份 · TMDB ★ 8.1 · 豆瓣 ★ 9.1 · 类型 / 地区`，首页卡片评分角标
  同样两颗并排（`LibraryItem.doubanRating` 来自 `doubanRatings()` 一次查询）。豆瓣资料还能补
  类型 / 制片地区 / 导演 / 主演 / 简介的空缺。

## 三、选中态与输入框：两处遥控器手感

`Theme.stylePosterFocus` 在选中时除了放大一档还额外盖了一层暗色遮罩，遥控器每走一格海报就暗
一下，看着像图片在闪。去掉遮罩，只留轻微放大 + 一层阴影。`styleInputFocus` 在拿到焦点时把光标
移到文本末尾：进输入框时光标默认停在最前面，想改年份得先把光标一路按右键挪出去，实际用起来就是
「删不掉」。

## 四、片名与年份：清洗规则补全，旧账重算

`劇場版「Fate／stay night [Heaven's Feel]」 Ⅲ.spring song 2020 ...` 提取成 `00000`、
`Ⅱ.lost butterfly 2019` 只剩「劇場版」，成因是同一个：清洗是按「词元」做的，全角 `／`「」` 与
罗马数字字符 `Ⅱ / Ⅲ` 既不算字母也不算数字，整段词被当垃圾丢掉，剩下的词元里只有「劇場版」这种
通用词，年份那里又只剩 `2020` / `2019` 拆散后的碎片。新增 `normalizeNameText()`：清洗之前先把
全角标点折成空格、`U+2160..U+2169` / `U+2170..U+2179` 折成 `I..X`、全角数字与字母折成 ASCII，
相对路径与父目录名一并折。`GENERIC_CHINESE_TOKENS` 补上「劇場版」（旧表只有简体的「剧场版」），
`cleanPatterns` 按 mac 那份重写。现在：

| 文件名 | 外文名 | 年份 |
| --- | --- | --- |
| `空战群英.Masters.of.the.Air.S01.2024.2160p...` | Masters of the Air | 2024（中文片名 空战群英） |
| `Solo Leveling S01 2024 1080p NF WEB-DL...` | Solo Leveling | 2024 |
| `劇場版「Fate／stay night…」 Ⅲ.spring song 2020 ...` | Fate stay night Heaven's Feel III spring song | 2020 |
| `劇場版「Fate／stay night…」 Ⅱ.lost butterfly 2019 ...` | Fate stay night Heaven's Feel II lost butterfly | 2019 |
| `Signal.S01.2016.1080p.BluRay.Remux...` | Signal | 2016 |
| `The.Long.Season.S01.2023.2160p.UHD...` | The Long Season | 2023 |
| `Paprika.2006.2160p.HKG.UHD...CMCT` | Paprika | 2006 |
| `Vinland.Saga.S01.2019.1080p.BluRay.Remux...` | Vinland Saga | 2019 |
| `Millennium Actress 2001 UHD BluRay 2160p...` | Millennium Actress | 2001 |
| `Bicycle Thieves 1948 CC Blu-ray 1080p...`（对照） | Bicycle Thieves | 1948 |
| `流人.Slow.Horses.2022.S01...`（对照） | Slow Horses | 2022（中文片名 流人） |

**为什么规则早就对了、首页还是旧名字。** 1.29 / 1.30 修的是「这一次刮的时候怎么算」，而已经刮到
资料的行是 `scraped = 1`（手动关联过的还带 `isLocked = 1`），根本不会再进队列。`Signal` 的 2017、
`Vinland` 的 2005、`Paprika Western`、`The Making of Millennium Actress` 全是这么留在库里的老账。
新增 `redecideStaleResults(listener)`：每轮扫描摘要之后跑一次，把每条实体与一个代表文件 join
出来，按**当前**规则现算片名与年份，年份对不上、或者库里片名没有汉字而提取出的关键词有对应外文名
的（忽略标点小写差异），一律强制重新入队；跑完把 `scrape_rule_epoch` 写成 31，只跑这一次，
不会每轮都重刮一遍整库。

**刮完还是英文。** TMDB 的 zh-CN 搜索结果对这类作品常常就是拉丁名，旧版把候选标题原样写库，
`空战群英` 那部就一直挂着 `Masters of the Air`。现在照 mac 的 `preferredTitle(chineseFallback:)`
走：`localizeTitle()` 拿 `/movie/{id}`（zh-CN）里的标题，含汉字就用；没有汉字就退到文件名或父目录
里的中文片名（`applyChineseTitleUnlessLocalized()`），手动关联走同一条路。全英文资源（`Solo Leveling`
这种文件名里一个汉字都没有）保留 TMDB 的标题，只是不会再莫名其妙变成另一部作品的别名。

## 五、从详情页返回：位置与选中的海报都要在

`showDetail()` 前记下 `homeScrollView.getScrollY()` 与当前聚焦海报的实体 id，返回时按这份记录还原。
原来有两处不生效：一是新 `ScrollView` 在第一次布局前会吞掉 `scrollTo()`，而落地后又有一次媒体库
重读会重建墙，位置就被冲掉——现在记录等 `post()` 里的 `scrollTo()` 真跑过之后再清账，中间插进来的
重绘继续沿用同一个位置；二是 `restorePosterFocus()` 旧写法是「只要屏幕上有任何东西带着焦点就不补」，
重建后系统会把默认焦点丢给第一个可聚焦控件，于是每次都它挡回来——现在改成「焦点不在这张卡上就补
过去」，从下往上走一遍父链比对。`homeScrollY` 也改为每次重建都同步一次，就地补丁那几条路径不再丢位置。

## 六、验证

JVM 用例（拿真源码编译，`android.jar` 上 classpath）：`build/harness/NameYearCheck.java` 覆盖年份
取法十组、`A.K.A.` 拆分、Jellyfin / Emby 端点路径，全部通过；`build/harness/DoubanParseCheck.java`
配一份 `douban.html` 真页面快照，验片名 / 年份 / 又名 / 评分 / 导演 / 主演 / 类型 / 国家 / 海报
与「只给编号、桌面链接、移动链接、混杂文字都能解析出 `1395091`、短号码不算条目号、缩略图不能当
海报」，全部通过；`build/harness/ParseProbe.java` 打印上面那张表。

模拟器（Android TV 镜像 + 假 WebDAV :8100 + 假 TMDB :8099）跑真链路：

- 数据库从 v3 升到 v4，`doubanMetadata` 建表成功，海报墙照旧 11 部带海报，冷启动不闪退。
- 手动匹配弹层里输入编号 `1395091` 点「绑定豆瓣」：Toast 一句「已绑定豆瓣《Perfect Blue》，豆瓣 9.1」，
  详情页元信息行变成 `1997 · TMDB ★ 8.1 · 豆瓣 ★ 9.1 · 剧情 动画 悬疑 日本`，首页那一张卡片角标
  同时是 `8.1` 与 `9.1`。
- 扫描途中移除媒体源：首页 `所有影视` 立刻归零，抽屉里那一行消失，状态行不再出现「正在扫描」。
- 选中第三张海报进详情再返回：墙还停在原偏移（`中文译名8001` 在 y=493），焦点回到原来那张卡
  （bounds 与进详情前一致）。
- 海报选中只剩轻微放大与阴影，没有再变暗。
