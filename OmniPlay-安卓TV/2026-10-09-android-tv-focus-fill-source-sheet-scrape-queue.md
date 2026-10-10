# 1.18：选中态改深色填充、媒体源抽屉、刮削队列与片名提取

## 选中态：加深自己的底色，不再画框

原来「聚焦」是靠一圈加粗的强调色描边（`STROKE_FOCUS` = 3dp）喊出来的，在两米外的电视上等于给每个控件套一个方框，整屏都在抢焦点。现在整套焦点语言只剩两条：`Theme.darker(color, amount)` 把控件**自己的**底色往黑里压（`FOCUS_DARKEN = 0.42f`，播放器里的半透明底板用 1.6 倍力度），加上原有的 `FOCUS_SCALE` 轻微放大与一层阴影；边框粗细在静止态和聚焦态完全一样，只换颜色。`STROKE_FOCUS` 字段删掉，`styleButtonFocus` / `styleRowFocus` / `styleInputFocus` / `styleCardFocus` / `stylePosterFocus` / `styleBadgeFocus` / 播放器三件套全部重写，`Widgets.Segment` 的分段按钮也一样（选中那一格原本是 GLASS_STRONG，聚焦时压深一档）。首屏设置页的类型选择按钮和媒体源卡片是就地手画的，也一起改成同一套。

海报是图片，压底色根本看不出来，所以 `stylePosterFocus` 改为在海报框里预先塞一张画好的暗色遮罩（`Color.argb(140, 8, 12, 20)`，与海报同圆角），聚焦时 `bringToFront()` + `VISIBLE`，失焦 `INVISIBLE`。之所以要 `bringToFront()`，是因为卡片先建、海报与角标后塞，遮罩加早了会被压在下面。

对号全部撤掉：类型选择按钮、主题色卡、播放器菜单行、详情页「版本」行原本都靠 `"✓ "` 前缀表示选中，现在一律由配色负责；详情页版本行改用「（当前）」文字标记主片，选中语义不丢。

## 媒体源抽屉：添加、挂载、扫描全在弹窗里

`renderSourcesOverlay()` 照 mac 版 `PosterWallView.webDAVSourceSheet()` / 「添加局域网媒体源」重写。面板宽 470dp，从上到下是：已有媒体源卡片（停用 / 重命名 / 移除）、「局域网设备（自动扫描）」区块（标题行右侧一颗「重新扫描」）、并排的「添加 WebDAV 媒体源」「添加 SMB 媒体源」、底部的「清空媒体库索引」。原来的「添加媒体源」会 `showSetup(null)` 跳到全屏设置页，「重新扫描并刮削」也挤在面板里，两者都不在了。

登录框从 560dp 加宽到 620dp，字段顺序照 mac 版：显示名称（可选）→ 地址 → 用户名 → 密码，底部是 取消 / 手机输入 / 登录并选择目录。它是个居中卡片，直接开在抽屉之上，抽屉不关；登录成功后打开「选择媒体目录」，选定目录 → `mountMediaSource()` 检测到 `sourcesOverlay != null` 就只重绘抽屉、刷新背后的海报墙，并把扫描刮削跑在抽屉的状态行上，不再 `showHome()` 把弹窗顶掉。显示名称以前会丢：`runLogin()` 成功后先 `closeLoginDialog()`（那里把 `loginDialogLabel` 清空）才打开目录浏览器，`confirmMediaSource()` 读到的永远是空串。新增 `browserLabel` 字段，登录成功时把名字交给它，挂载时用它当媒体源名。

局域网扫描原先硬绑在设置页上（监听里判 `screen != Screen.SETUP`）。现在 `startLanScan()` 记录宿主（`LAN_SETUP` / `LAN_SOURCES`，看打开时 `sourcesOverlay` 在不在），`lanTargetAlive(target)` 统一判存活：抽屉宿主看还开着没、容器还挂在窗口上没。`onDone` 无论宿主死活都先清 `lanDiscovering`，否则一次扫描被丢弃后计数永远卡住、下一次抽屉里点不动。动态插进去的设备行用 `refreshLanFocusRows()` 重排方向键：抽屉宿主走 `overlay.redeclare(FocusGrid.rowsFrom(panel))`，设置页走原来的 `rebuildSetupDpadGrid()`。`attachLanListContainer()` 必须排在 `declarePanelRows()` 之后，否则 `redeclare()` 会在面板还没建完时就把焦点网格推上去。

「扫描刮削」挪到首页工具条，位置在「媒体源」左边，工具条焦点行同步为 搜索 / 排序 / 降序 / 扫描刮削 / 媒体源 / 设置。进度不再只写面板状态行：新增 `startScanAndScrape(TextView status)` 一个入口，回调里同时刷 `refreshHomeStatusText()`（首页顶部那行）与 `scheduleScrapePosterRefresh()`（刮出一张上墙一张），`scanRunning` 挡住并发。

## 待刮削数字乱跳、海报一张不出来

两件事叠在一起。

`WebDavClient.relativePath(href)` 只在 `href.startsWith(rootUrl)` 时截相对路径。挂载点带中文时，用户填的 `rootUrl` 是原文、服务器回的 `<href>` 是百分号编码过的，前缀永远匹配不上，相对路径退化成「最后一段编码过的文件名」。坏掉的是三处：刮削查询词变成一串 `%E4%B8%AD`，剧集分组名取不到目录名，播放地址被二次编码成 404。改成两边都先过 `UrlLabels.displayPath()` 再比前缀，存进库的相对路径统一是解码后的原文（`streamUrl()` 会按段重新编码，这才是该存的形态）。`/configuration` 能验通而海报墙一张海报没有，就是这个原因：TMDB 是好的，查询词是垃圾。

刮削队列本身也在自己跟自己打架。`enqueueScrape()` 是无脑 INSERT，同一部片被多轮扫描登记就多行；`nextScrapeBatches()` 取一批之后 `DELETE FROM scrapeQueue` 把整张表清空，超过一批的条目直接丢失；`scrapeQueueCount()` 数的是行数，取的时候按 `(mediaType, entityId)` 去重，于是首页那行数字在 274 与 275 之间反复跳。现在入队先查同 `(mediaType, entityId)` 是否已有行；取批用 `GROUP BY mediaType, entityId` 并且只删取走的那几个 id；计数也按去重后的口径来。`LocalEngine.scrapePending()` 加了 `scrapeLock` + `scraping` 的单飞保护（原来那个 `boolean` 没有任何同步，两条线程能同时进），跑完一轮再取下一批（每批 200 条），全部处理完才落「刮削完成，共处理 N 个条目」，一条都没匹配上时提示可以重新扫描重试。

老库里由旧版本登记的文件仍带着编码过的 fileId，下一次扫描会先把它们当缺失文件清掉再重新登记，属于自愈，不需要清库。

## 海报下的名字太长

片名提取照 mac 版 `MediaNameParser` 补齐（`local/LocalMediaNameParser`）：

- 先 `truncateBeforeReleaseMetadata()`，按空白切词后遇到第一个「发布信息词」就截断——`1080p`、`s01e01`、`x265`、`web-dl`、`amzn`、`disc2`、`vol-3` 这些（词表与 mac 版 `isReleaseMetadataToken` 一致）。长命名变短主要靠这一步。
- 年份也照 mac 版**截断**而不是全局删除：匹配到发布年就切到它前面，`Inglourious.Basterds.2009.2160p.x265.…` 得到 `Inglourious Basterds`。
- `extractChineseTitle()` 把连续中文词分组取最长一组，并切掉「特典 / 花絮 / 预告片」之后的补充说明；`extractForeignTitle()` 滤掉发布组、编码、地区代号，只在开头出现的序号留在片名里；两个函数都照抄 mac 的噪声词表。中文正则里的 `\p{Han}` 换成 `\p{IsHan}`，OpenJDK 与 Android 都认。
- 新增 `displayTitle(relativePath, fileName)` = 中文片名 → 父目录中文名 → 外文片名 → 清洗后的整串，每层都过 `usableDisplayTitle()`（`movie` / `BDMV` / 纯数字之类判不合格，但《2012》这种 3 位以上的数字片名放行）。`LocalEngine.registerFile()` 用它决定入库名：电影用整条相对路径，剧集**只用剧集目录名**——条目是按标题 upsert 的，逐集算会算出不同的名字。
- 卡片与详情页显示时再过一层 `displayTitle()`（`prettifyMediaTitle(UrlLabels.displayPath(raw))`），旧库里已经存下的编码标题也顺眼。

新增 `/tmp/pitest/TitleProbe.java`（`PASS`）：13 条断言覆盖中英文名、WEB-DL、蓝光 `BDMV/STREAM` 目录、`S02E01`、纯发布信息名（宁缺毋滥返回 null 交给调用方兜底），以及「同一部剧的每一集必须解析出同一个分组标题」。

观看状态图标本来写着「✓ 已看 / ○ 未看」，现在只剩图形：`○` 未看、`◐` 未看完（有进度且 < 95%）、`✓` 已看，颜色分别是 次要色 / 强调色 / 焦点色，`tooltipText` 保留文字说明。

## 验证

`/tmp/tvbuild/compile.sh` 全量 javac 通过（JDK 21 + `android-35/android.jar` + libmpv / jcifs-ng / zxing + 一份手写的 `R` 桩）；`./gradlew --offline assembleRelease` 出包，`aapt2 dump badging` 确认 `versionName='1.18' versionCode='29'`。

`omni_tv1080`（`wm density 213`）实测：首页工具条是 搜索 / 排序 / 降序 / 扫描刮削 / 媒体源 / 设置；点「媒体源」弹出的抽屉里有源卡片、「局域网设备（自动扫描）」与自动开始的扫描、两颗添加按钮；点「添加 WebDAV 媒体源」弹出的居中卡片是 显示名称 / 地址 / 用户名 / 密码 + 取消 / 手机输入 / 登录并选择目录，抽屉仍在下面；焦点行落在输入框上是底色变深、没有加粗框；海报墙走焦时卡片压暗并抬起、阴影在外层。两轮扫描刮削分别报「处理 91 个条目」「处理 46 个条目」后状态行归位，没有出现待刮削数字来回跳。这一轮 WebDAV 侧只有代码级证据——手边没有带中文目录名的 WebDAV 服务，`/tmp` 上一轮被清掉后本地夹具没重建。
