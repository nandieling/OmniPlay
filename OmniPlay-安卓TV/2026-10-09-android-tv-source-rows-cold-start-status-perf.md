# 1.19：源卡片行高、冷启动直入海报墙、状态横幅与刮削队列

## 源卡片加高，WebDAV 名字还原成文件夹名

源卡片的高度以前全凭内容撑：卡片是「标题 + 路径 + 一排按钮」三层竖排，`LayoutParams` 是 `wrap_content`，配 `dp(10)` 内边距与硬写的 `dp(46)` 按钮。WebDAV 源名往往就一行，整块被压成薄薄一条，三颗按钮贴着底边，遥控器上「移除」经常点空。现在卡片设 `setMinimumHeight(sdp(Widgets.HEIGHT_ROW))`（96dp）、内边距提到 14dp、三颗按钮改用 `Widgets.HEIGHT_BUTTON`（56dp），「重新扫描」那颗从 42dp 跟到 `HEIGHT_COMPACT`（48dp），设置页同名按钮一起对齐。

列表行本身也加高：`Widgets.HEIGHT_ROW` 78dp → 96dp，局域网设备行、已保存连接行、`MainActivity.listItemParams()` 都读这一个常量，改一处全体生效。

名字还是 `%E7%94%B5%E5%BD%B1` 这种符号，因为解码只做了一半：1.18 让 `WebDavClient.relativePath()` 存解码后的相对路径，但 `LocalEngine.defaultSourceName()` 拿 URL 最后一段当默认媒体源名时仍存原文。现在那一段也过 `UrlLabels.displayPath()`（只对 webdav）。首页卡片另加 `displaySourceName()` 兜一层，升级前就写进库里的旧名字也能正常显示，不必清库。

## 冷启动不该先看错误日志

`onCreate` 以前无条件走 `checkSession()`：先 `showLoading("正在打开媒体库")`，再异步问一次 `authStatus`，答不上来就 `showSetup(...)`。独立版根本没有服务端要问，那次网络抖动纯属自找，代价是冷启动有概率停在全屏设置页，页脚还挂着上次会话的崩溃摘要。

现在只剩一个判断：有媒体源就 `showHome()`，媒体库为空才进设置页。崩溃摘要改成一条 Toast（`CrashLog.take()` 照旧读一次清一次），不再占据整个首屏。

## 左上角横幅：措辞与开关都照 mac 版

mac 版 `PosterWallView` 的进度文案是 `processingMessage`，空闲时置空、整行不渲染。TV 版对齐成同一条链：

```
正在扫描媒体源...
扫描中 (2/3)：NAS-WebDAV
刮削元数据和海报 (12/248)：沙丘
扫描完成：新增 12，移除 3，共发现 248 个文件。（蓝光非正片分卷跳过 N 条）
刮削完成，共处理 248 个条目。/ 刮削完成，处理 248 个条目，3 个没匹配上（可重新扫描重试）。
```

两条规则：队列非空但 TMDB 没配好，直接说「检测到有待刮削的影视，但 TMDB 暂时无法连接。请在设置里检查 API Key 或反代地址。」；收尾那条停 8 秒后自动收起（`hideHomeStatusSoon()`），空闲时横幅 `GONE`。原来常驻的「待刮削 N 个条目」取消——它正是「卡在 255」看起来像死机的直接原因。文本与上次相同就不再 `setText`，横幅不再逐帧重绘。

## 「卡在待刮削 255」与一半名字没更新

`insertVideoFile` 是 `INSERT OR IGNORE`：同一个文件重扫时 `videoFile` 行保持原样，`movieId` / `showId` 还指着第一次入库那个实体。而 `registerFile()` 每轮都用新算出的展示名去 upsert 一个实体，于是同一部片出现两个实体——老的那个还挂在文件行上，新的没有任何文件，下一句 `pruneOrphanEntities()` 就把它当空壳删了。两半症状都从这里来：

- 海报墙读的是文件行指向的老实体，所以名字永远停在第一次扫描那轮。同一批文件里，先入过库的还是整串文件名，登记失败新建的反而拿到了短名，看起来就是「一部分成功、一部分失败」。
- `scrapeQueue` 里那批 `entityId` 指向被删实体的行没人清，`scrapeQueueCount()` 一直数得到，横幅就一直是「待刮削 255」。

修法是新增 `LocalMediaLibrary.bindEntityForFile(mediaType, fileId, title)`：文件已有实体就复用，只有在 `scraped = 0` 时才把新算的名字写回去改名；实体查不到（被裁过）才新建并把文件行改绑过去。`pruneOrphanEntities()` 多删一条——实体已消失的 `scrapeQueue` 行。

`scrapePending()` 的排空循环同时改掉：原来是 `while (true) { … if (batch < 200) break; }`，扫描还在入队时第一轮只取到不满一批就退出，扫描中途新入队的整批留在队列里等下一次冷启动；现在是最多 100 轮的 `for`，只要还有就继续取，进度分母用入队时的总数与已完成数取大，`(12/248)` 不会因为边扫边刮而倒退。

顺带一条同源的浪费：`enqueueScrape()` 每轮扫描都给每个扫到的文件重排一次刮削，已刮到元数据的库等于每次冷启动全库重刮一遍。现在扫描入队先看实体的 `scraped` 标记，已刮的直接跳过；详情页的手动重新匹配走 `requeueScrape()`，它用 `force = true` 强制入队。mac 版同样只刮「需要刮削的新文件」。原来那段「先 DELETE 再 SELECT 判重」的死代码一并删掉——刚删完再查自己是否存在，永远查不到。

## 卡顿：这一版砍掉的开销

1. 扫描进度不再逐条回调。以前每登记一个文件就 `listener.onScrapeProgress("扫描 " + fileName)`，255 个文件 = 255 次 `main.post` + 255 次横幅 `setText` + 一次列表重绘请求，全压在 UI 线程上；现在按源报进度（`扫描中 (i/n)：源名`），一个源一条。
2. 进度回调统一走 `throttledProgress()`，250ms 内的重复消息直接丢弃，再往主线程 post。
3. 整面海报墙的重建改为防抖：`patchLibraryViews()` 不再就地 `refreshHomeBody()`，改成 `scheduleWallRebuild()`，2 秒内多次变化只重建一次；海报增量刷新节流从 1200ms 提到 2000ms。刮削期间看到的是「每 2 秒上一批海报」，而不是每刮中一条整墙重排。
4. `bindPosterCard()` 加渲染签名（海报 id + 标题 + 年份 + 已看状态 + 进度 + 两个评分），不变就直接返回，跳过 `setText` / `removeAllViews` / 重新取海报。整墙重建时绝大多数卡片走的是这条快路径。
5. 冷启动没有 loading 页与那次 `authStatus` 往返；已刮削的库重扫时不再产生 TMDB 请求。

模拟器（`-gpu host`，213dpi，48 条未匹配条目）走查 72 次方向键：135 帧、14 帧超期（10.37%）、`Slow UI thread` 0、`Slow bitmap uploads` 0，帧耗时全部落在 16–19ms，没有一帧越过 20ms；PSS 60MB、552 个 View。`High input latency` 那一项虚高，是 `adb shell input keyevent` 每次起一个进程送键造成的，与应用无关。

## 还没做的

- 海报墙仍是 `GridLayout` 全量建 View：48 部 552 个 View，条目上千后光布局就要几十毫秒，且滚出屏幕的卡片照样占内存。下一步换 `RecyclerView` + `GridLayoutManager`，`FocusGrid.rowsFrom()` 改成从 `LayoutManager` 取可见行（弹层与设置面板不受影响，它们本来就是 ScrollView）。
- 海报解码走 `ImageLoader`，已经按目标尺寸降采样 + 磁盘缓存 + 按字节数的 `LruCache`。还能再省一半的是 `Bitmap.Config.RGB_565`（海报是照片，色带风险可控），但要在真机上先看一眼渐变背景那批封面。
- 焦点动画目前用 `View.setScaleX/Y`，会连带父容器重绘；换成 `ViewPropertyAnimator` + `setLayerType(LAYER_TYPE_HARDWARE)` 收益有限，先等 `RecyclerView` 落地后重新测。
