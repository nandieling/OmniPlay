# 1.26：抽屉瘦身、详情页收进长按、海报改走反代、首页计数不再冻结

十三条反馈，归结成五件事：媒体源抽屉与首页信息重复、选目录要点进才能挂、重命名弹全屏；详情页按钮太多；已看状态与进度条占地方；手动匹配/自定义编辑看不到海报；以及最要紧的一条——扫描期间「所有影视 N 部」不涨、刮削看着不自动进行。

## 一、媒体源抽屉

抽屉里那行扫描进度与首页左上角是同一句话，删（`mountMediaSource` 之后 `startScanAndScrape(null)`，进度只写首页）；`options.subtitle = "停用后该目录不再扫描入库…"` 删。

重命名从「再开一层弹层」改成内联：新 `openRenameSourceEditor(source, row, status)` 在动作行下面 `row.addView(editor)`，输入框（`Widgets.HEIGHT_INPUT`）+ 紧凑「保存」，`setId(0x7f0e0001)` 方便重复点击时先摘掉旧编辑器；保存后重挂这一行的名字，空名字写「名称不能为空。」到抽屉状态行。焦点顺序在 `FocusGrid.rowsFrom(opened.body)` 里重声明过，方向键不会跳到已经消失的按钮上。

选目录每行拆成两个控件：左边名字（`browserNavigateInto`，进下一级），右边 `widgets.iconButton(Icons.of(mounted ? STAR_FILL : STAR, ...))`（`mountBrowserEntry`，直接挂这一级）。焦点行用 `browserFocusRows()` 把成对的两个控件塞进同一行，`browserStarButtons` 收着这些 `ImageView`（`iconButton` 返回的是 `ImageView`，不是 `Button`）。`isBrowserEntryMounted()` 用 `trimTrailingSlash(entry.url)` 与同类源的 `source.path` 比对，已挂载的显示实心星。

## 二、详情页第一行

```
[开始播放]   (已看图标)   ├──── 进度条 ────┤  --:--
```

- 已看状态是 `widgets.iconButton(Icons.of(watchStateOf(file)...))`，三个符号照 mac：`WATCH_DONE` / `WATCH_PART`（`fileProgressPercent > 0`）/ `WATCH_NONE`，状态名只进 contentDescription。
- 进度条 `timeline(detail, mainFile)` 用 `weight=1` 排在图标右边。
- 原来那排「手动匹配 / 自定义编辑 / 已看」文字按钮撤下。

长按：`attachDetailLongPress(view, action)`。要点是遥控器 OK 键走 `KeyEvent`，`View.setOnLongClickListener` 只在触摸里触发，光设 `setLongClickable(true)` 在电视上永远不响。所以自己计时：`ACTION_DOWN` 时 `postDelayed(fire, ViewConfiguration.getLongPressTimeout())`，`ACTION_UP` 时若已触发（或 `FLAG_LONG_PRESS`、`getRepeatCount() > 0`、按住时长超阈值）就 `cancelPendingInputEvents()` 并吃掉这次 UP，避免弹完面板又顺手播了一次。弹层是 `openDetailActionsOverlay()`：「刮削与资料」，两行 `listRow`，宽度 420dp、不滚动、关闭后焦点还给原控件（`options.restoreFocus = getCurrentFocus()`）。

## 三、能挑海报了

`posterRow(title, detail, posterUrl, onClick)`：可聚焦可点击的行，左边 46×68 的 `ImageView` 走 `loadPoster(...)`，右边片名加一行说明，聚焦样式与卡片一致（`Theme.withAlpha(COLOR_FOCUS, 46)` 描边 + `animateFocusScale`）。手动匹配候选、`从 TMDB 选海报` 的候选都用它。

自定义编辑底部新增海报块：当前缩略图（64×96）+ `手机推送海报`（`openPhoneInputOverlay(...)` 挂一个 `PhoneInputServer.Field("poster", "海报图片地址", detail.posterAssetId, false, sink)`）+ `从 TMDB 选海报`（`api.engine().searchCandidates()` 前六条）+ `恢复自动海报`。落库是新加的 `LocalMediaLibrary.setPosterOverride(mediaType, entityId, posterUrl)`：只 `UPDATE movie|tvShow SET posterUrl = ?, isLocked = ?`，空串等于恢复自动（`isLocked = 0`）。`Models.LibraryDetail.posterAssetId` 是 final，所以改完是重读缩略图 + `refreshDetail()`，不是就地赋值。

## 四、三处按 mac 收

- `renderMovieVersions()`：`if (files.size() <= 1) return;`，对齐 mac `PosterWallView`/`MovieDetailView` 的 `videoFiles.count > 1`。
- 首页卡片标题 `name.setGravity(Gravity.CENTER_HORIZONTAL)`，mac 是 `multilineTextAlignment(.center)`。
- 分集剧照里的「改副标题」整块删除。

## 五、N 部不涨：三个原因叠在一起

1. **只认最后一次读取**在扫描期是反效果。`ScanCollector` 每 400ms 重播进度，每次进度都触发一轮 `refreshLibraryInPlace()`，只要有一条读取耗时超过这个间隔，所有结果都在落地前被后来的号覆盖，界面永远停在旧数字上——而「已发现 N」是引擎内存里的计数，照样涨，看上去就是「文件在涨、库里不涨、刮削不跑」。
2. `libraryItems()` 是 N+1：每个实体单独 `filesForMovie/filesForShow` 查一遍，两百个实体就是两百多条查询。
3. SQLite 还是默认回滚日志，扫描写事务期间读会被挡。

改法：

- 进度行上的「N 部」改成直接问库：`LocalMediaLibrary.entityCount()`（两条 `SELECT COUNT(*) ... WHERE id IN (SELECT movieId|showId FROM videoFile ...)`），`LocalEngine.libraryItemCount()` 透传，`updateLibraryCountLabel()` 每次刷新进度顺手改文案。数字来自数据库当前状态，不受在飞的读取结果影响。
- `LocalMediaLibrary.allFilesGrouped(isTv)` 一条 `SELECT ... WHERE movieId|showId IS NOT NULL ORDER BY ..., season, episode, fileName` 读全库再按实体分组（`LinkedHashMap`），`readFile(Cursor)` 与 `queryFiles` 共用；`libraryItems()` 改从这两张表里取桶。
- 首页刷新收敛成 `requestLibraryFetch(inPlace, showErrorScreen)`：单飞，正在读就记一次「读完再补一轮」（尾随重读会合并，不会叠三条），回调里统一更新计数、按 `inPlace` 决定就地补丁还是整墙重建；`applyLibraryPatch` 遇到空库且墙上有卡片时强制重建（对应抽屉里移走最后一个源）。
- `OpenHelper.onConfigure()` 里 `db.enableWriteAheadLogging()`。

## 六、海报为什么半天不上墙

给 `ImageLoader` 加了两条 `Log.d` 才看清：每张海报先请求 `https://image.tmdb.org/t/p/w500/poster-x.jpg`，配反代时这一趟基本是 `FileNotFoundException`，回头才试反代 `/t/p/`。一屏四十几张卡、4 个线程排队，一轮几十秒，用户看到的就还是占位块。

`TmdbClient` 现在有三个分工明确的方法：`proxyImageForm()`（CDN → 反代）、`cdnImageForm()`（反代 → CDN）、`posterImageUrl()`（主地址：配了反代就用反代）、`posterFallbackUrl()`（备胎：两个方向互转）。`LocalPlayApi.posterUrl()/thumbnailUrl()` 走 `posterImageUrl()`，库里存的仍是官方 CDN 地址，改天不配反代不需要迁移数据。

## 七、验证

模拟器（android-34 TV，假 TMDB 反代 `http://10.0.2.2:8099/tmdb`）：

- 60 个新文件重启：`刮削元数据和海报 (26/60)：NewTitle26` → `(39/60)`，`所有影视 238 部` 一路上涨，满屏彩色海报（`v26-m-home`、`v26-o-22/45`），`OmniPlayPoster` 里 CDN 404 归零。
- 抽屉：无进度行、无说明行、每行一颗星（`v26-c-picker`、`v26-b-src`），重命名内联在动作行正下方（`v26-d-rename`）。
- 详情页：第一行只有播放 + 图标 + 进度条（`v26-i-sheet`），长按弹出「刮削与资料」（`v26-k-sheet`），手动匹配候选带海报（`v26-n-match`），自定义编辑海报块三按钮（`v26-n-edit`），`版本 / 文件（2）` 只在真有两个文件的条目上出现。
- 首页标题居中（所有截图）。

`adb shell input keyevent --longpress 23` 可以用来在模拟器上验长按；`input swipe x y x y 900` 不产生长按事件，别用它试。
