# 1.27：停用真的收起海报、扫描只扫新增、登录弹窗抢回焦点

五条反馈：停用媒体源首页海报还在；点局域网设备登录弹窗却切不进焦点；`已发现文件` 涨到一万多不停、`N 部` 不涨、刮削不开始；反代确认转发了图片路径；手动匹配/自定义编辑从长按改回看得见的按钮。

## 一、停用＝从首页收起来

mac 版每条媒体库查询都带 `COALESCE(mediaSource.isEnabled, 1) = 1`（`MediaLibraryManager.swift` 里十几处），停用是把源「收起来」而不是删数据，30 天后才真的清行。TV 版没有 mediaSource 表（源在 SharedPreferences），于是把启用集合作为参数传进查询：

```java
public int entityCount(Set<Integer> enabledSourceIds)
public Map<Long, List<StoredFile>> allFilesGrouped(boolean isTv, Set<Integer> enabledSourceIds)
```

条件由 `sourceClause()` 拼：`null` 不加条件，`{}` 加 `AND 0`，否则 `AND (id LIKE 'local-1:%' OR ...)`（文件行主键形如 `local-<sourceId>:<相对路径>`，`sourceIdOf()` 早就有了）。

踩到的坑：第一版把「空集合」也当成不过滤，于是**所有源都停用**时首页照旧满墙。空集合必须翻译成 `AND 0`。

连带两处：

- `removeMissingFiles(keep, scannedSourceIds)` 加了第二个参数。扫描只扫启用的源，收尾那句「这轮没见到的就删」若不加范围，会把停用源的文件当成「盘上已经没了」删光。
- 空墙文案。原来只有「一个源都没有」才给「添加媒体源」入口，全停用时是一句 `媒体库为空`，屏幕上没有任何可聚焦控件，遥控器开屏不知道往哪儿走。现在按 `enabledSourceIds().isEmpty()` 判断，两种空态共用那颗入口按钮，全停用时文案换成「媒体源都已停用，海报先收起来了。启用任意一个就会重新出现，不用重新扫描。」

## 二、扫描只扫新增

`ScanCollector.register()` 原来对每个扫到的文件都做全套：

```
bindEntityForFile → insertVideoFile → enqueueScrape（DELETE + SELECT 判重 + INSERT）→ fileIds.add
```

一万条记录 = 三万次写 + 一万次判重查询，扫描十几分钟；刮削在 `scanAndScrape()` 里排在扫描之后，于是「刮削一直没开始」。而 `report.scannedFiles++` 每轮扫描都从 0 重新数，用户看到的「已发现 N」自然一路涨到一万多。

现在每个 `ScanCollector` 构造时读一次 `library.knownRelativePaths(source.id)`：

```java
fileIds.add(fileId(source.id, relativePath));
report.scannedFiles++;
if (knownPaths.contains(relativePath)) { paintProgress(); return; }   // 只做存在性比对
knownPaths.add(relativePath);
report.newFiles++;
registerFile(...);
```

`newFiles++` 从 `registerFile()` 里挪到调用点，只有真新增才计数——「新增」和「扫过」是两个数，混在一个词里就是用户报的「涨到一万多还没停」。进度句同步改：`扫描中 (i/n)：源名 · 已扫描 N 个文件，新增 M`，收尾 `扫描完成：新增 X，移除 Y，共扫描 Z 个文件。`

没跟 mac 一样记 `fileSize`：TV 的 `videoFile` 没有这一列，加一列只为「同路径但大小变了」这种少见的场景不值当，代价是同一文件原地被替换时不会刷新条目（mac 在这种情况下也只是改大小，不会重刮）。

## 三、登录弹窗抢不到焦点

`Overlay.focus()` 里 `if (gridPushed) owner.grid.replaceTop(rows)` 是无条件的，而 `FocusGrid.replaceTop()` 直接 `active = cleaned`——它并不知道自己替换的是谁的活动网格。抽屉打开时会 `declarePanelRows(...)`，其中 `FocusGrid.afterLayout` 的回调以及局域网扫描的 `onDevice/onDone → refreshLanFocusRows()` 都会 `sourcesOverlay.redeclare(...)`。这两条都可能发生在登录框已经压上去之后：重声明把**登录框**的网格整个换成抽屉的，焦点自然掉回抽屉，上面那层再也走不进去。

修法是在 `Overlay.focus()` 里先看身份：

```java
if (owner.top() != this) { staleRows = true; return; }   // 被压着，记账不走
owner.grid.replaceTop(rows); staleRows = false;
```

`Overlay.close()` 弹出自己之后调 `promoted.relinkRows()`，被压着期间攒下的声明在这时补上。

## 四、详情页两颗工具

`Icons.Kind` 加 `MATCH`（`text.magnifyingglass`：两行文字 + 右下放大镜，两者不重叠，22dp 也分得清）和 `PENCIL`。`renderDetail` 在 `heroFrame` 的 `TOP|RIGHT` 放一个横排 `ImageView`，字幕缓存状态行下移到 `sdp(58)` 让位。上一版的 `attachDetailLongPress` / `openDetailActionsOverlay` 一起删掉——顺带说一句，长按方案本身有个坑：遥控器 OK 键送来的是 `KeyEvent`，`View.setOnLongClickListener` 只在触摸里触发，得自己起 `postDelayed` 计时器并吃掉松手那一下；既然现在改回明面上的按钮，这套就整个撤了。

## 五、验证

模拟器（android-34 TV，全程 `input keyevent`，不用触摸）：

- 单源停用 → 首页立刻 `所有影视 0 部` + 「媒体源都已停用」+ 可聚焦的「添加媒体源」（`w27-n-empty`、`w27-p-emptystate`）；点「启用」回到 60 部，期间没重扫（`w27-y-back`）。
- 抽屉里开「登录 SMB」，等局域网扫描收尾（`onDone` 必然重声明抽屉焦点行）后焦点仍在登录框内、方向键照常走（`w27-w-login2`、`w27-x-login3`）。
- 第二次点「扫描刮削」：`扫描完成：新增 0，移除 0，共扫描 62 个文件。（蓝光非正片分卷跳过 2 条）`，几秒返回（`w27-d-rescan`）；首次挂载那轮仍是 `刮削元数据和海报 (8/12)：Title8` 一路往上走（`w27-b-progress`）。
- 详情页右上角两颗图标分别打开手动匹配（候选行带海报，`w27-ab-match`）与自定义编辑（海报块三颗按钮，`w27-aa-edit`）。
