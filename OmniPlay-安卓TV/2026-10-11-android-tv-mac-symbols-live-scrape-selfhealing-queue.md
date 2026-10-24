# 1.24：图标对齐 mac 的 SF Symbols，刮削改成实时可见 + 队列自愈

两条要求：图标继续照 mac 还原；左上角看不到扫描刮削日志、海报墙长时间不动。第二条查下去不是显示问题，是刮削真的会永久停下来。

## 图标：形状、底板、亮度三件事

mac 版 `PosterWallView.swift` 那一排是这么写的：

```swift
Image(systemName: "folder.badge.plus").font(.system(size: 21, weight: .medium))
Image(systemName: "arrow.triangle.2.circlepath")… .disabled(isProcessing)
Image(systemName: "gearshape")
// 搜索与排序在「所有影视」那一行：magnifyingglass、line.3.horizontal.decrease.circle、arrow.up/arrow.down
```

TV 上没有 SF Symbols，`ui/Icons.java` 继续在 24 单位网格上手画，这一轮把五个符号改准：

- 排序：原来的「上下两个 V」是 `chevron.up.chevron.down`（mac 用它表示分类目录的展开），排序菜单该用的是 `line.3.horizontal.decrease.circle`——圆里三条左对齐、逐条变短的横线。
- 升降序：`arrow.up` / `arrow.down` 是通高箭杆加 45° 张开的箭头，不是 V。
- 扫描刮削：`arrow.triangle.2.circlepath` 的两段弧各自带一个**沿切线方向**的箭头。前一版箭头钉在固定坐标上，弧的角度一调就散架；现在终角 `a1` 处的切线方向就是 `a1 + 90°`，箭头跟着弧走。
- 媒体源：`folder.badge.plus` 的加号是压在文件夹轮廓上的徽标，直接画会和文件夹底边糊在一起，先用 `PorterDuff.Mode.CLEAR` 在轮廓上挖个洞再画加号。
- 设置：`gearshape` 是齿圈 + 本体圆 + 中心孔，前一版八根从圆心发散的线段更像太阳。

底板：1.23 那版每颗按钮留了一块玻璃底，mac 是 `.buttonStyle(.plain)`，平时什么底都没有。TV 上留着底板会把工具栏挤成六个方块，反而更不像 mac，现在 rest 态透明，只有焦点给圆角底 + 强调色描边。

亮度：mac 用的是 `theme.textPrimary.opacity(0.78)`，而 TV 之前用弱化文字色（大约是主文字色的一半），三米外看电视基本是灰的。现在图标取主文字色 78%、进度文字取 86%，线宽也从 1.9dp 收到 1.8dp 对应 medium 字重。

## 左上角那一行

mac 的显示条件是 `isProcessing || !thumbManager.progressMessage.isEmpty`。TV 之前是「有文案就显示，收尾停 8 秒收起」，于是用户过一会儿回首页，只看得到一面不动的墙。现在条件照 mac 来：

```java
boolean alive = scanRunning || api.engine().isScraping() || queued > 0;
```

空转且队列非空时补一句「待刮削 N 个条目，点右上角「扫描刮削」开始。」；收尾文案仍旧停 8 秒，但队列没见底就一直留着——一轮里全是失败时，这句话是用户唯一能看到的线索。

## 队列会永久漏条目

真正让「海报墙长时间不更新」的是这个。TV 版的刮削队列是一张 `scrapeQueue` 表，取批次时按行 `DELETE`：

```java
db.execSQL("DELETE FROM scrapeQueue WHERE id IN (" + placeholders + ")", args.toArray());
```

请求失败（网络抖、反代 503、TMDB 限流）的条目就此离开队列；而 `enqueueScrape()` 只给「还没刮到元数据的新登记实体」入队，扫描时已存在的老条目不会二次入队。结果：这几张卡片此后再也没人替它发一次 TMDB 请求，海报永远停在占位字母上，界面上还看不出任何异常。

mac 版结构上就不会有这个问题——它没有队列，每轮从库里现算未刮削清单：

```swift
self.processingMessage = "刮削元数据和海报 (\(index + 1)/\(successfulSources.count))：\(source.name)"
try? await libraryManager.processUnmatchedFiles(sourceID: source.id)
```

TV 现在在每轮开始前把「`scraped = 0` 且 `isLocked = 0`」的实体用两条 `INSERT … SELECT` 补回队列（`LocalMediaLibrary.requeueUnscraped()`），查询词取实体自己的标题、年份取上映日期的前四位、季号取该条目文件里的最小季。补队在**取批次之前**，所以既不会重复刮，也不会让已经刮到的条目被拉回来重刮（`scraped = 1` 直接被 WHERE 条件挡掉）。

顺带两处：

- 刮削前先 `检测 TMDB 连通性...`（mac 同名文案）。探测走 `GET /3/configuration`，5 分钟内成功过就不重复探；探失败时**一行队列都没取**，条目整整齐齐留在表里，状态行写「TMDB 无法访问：<原因>」。以前是先闷头刮完再报告几百个失败。
- 海报加载多了个 `fallbackUrl`：官方 CDN 取不到时改试同一台反代的 `/t/p/…`，成功的按正地址入缓存。配了反代说明这个网络到 TMDB 本来就不顺，图片走同一条路是顺理成章的兜底；没配反代时参数是 null，老行为一个字没变。
- 就地补卡的节流 2 秒 → 700 毫秒。

## 验证

模拟器 `omni_tv1080` + 假 TMDB（每次搜索拖 0.6 秒、海报由假服务器自己出图、结果不带 `release_date` 以放过年份校验）：

1. 启动后左上角 `检测 TMDB 连通性...` → `刮削元数据和海报 (13/42)：Film` → `(37/42)` → `刮削完成，共处理 42 个条目，全部更新。`，之后队列见底、横幅收起。
2. 同一段时间里海报墙从六张占位字母逐张变成带海报带评分的卡片（截图 `v24-d` 到 `v24-g`，间隔 12s / 34s / 60s）——实时补，不是跑完一轮一起变。
3. 假服务器日志确认每个条目两次请求，海报请求路径是 `…/tmdb/t/p/w500/…`，即走的是反代兜底那条路（此前这些海报在模拟器上根本取不到，一直是占位字母）。
4. 42 个条目正是上一版从没进过队列的那批。它们能被重新刮到，本身就证明 `requeueUnscraped()` 生效。

## 还欠着的

- 真机上确认反代是否真的转发 `/t/p/` 图片路径。不转发的话这条兜底只是白试一次请求，行为不会变坏，但海报仍得靠 CDN 直连。
- 每轮重排未刮削条目意味着「注定搜不到」的条目每轮都会再发一次请求。mac 也是这个行为，量大了以后可能需要一个失败退避（连续 N 轮没搜到就歇几轮）。
- 海报墙 `RecyclerView` 化仍是帧率地板。
