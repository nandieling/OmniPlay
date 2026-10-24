# 1.23：首页头部照 mac 重画（符号图标 + 左上角进度行），刮削队列补上漏掉的一批

这一轮的五条要求都指向同一个地方——首页那一行头部：去掉品牌字、文字按钮换符号、进度信息显示在左上角、选中态只留放大，外加一条「显示正在扫描但没看到新海报」的排查。

## 左上角那一格本来就不是品牌位

mac 版首页左上角是 `Scraping metadata and posters (n/m): …` 加一个小转圈，标题文字在 mac 上也没占过这个位置。TV 版以前把「觅影 OmniPlay」摆在那儿，进度横幅另挂在工具栏下方，于是同一件事有两处显示、还都排得不一样。

现在 `buildHomeShell()` 里工具栏的结构是：

```java
homeProgressRow  // weight 1：homeProgressView(17dp 转圈) + homeStatusView(单行)
homeSearchToggle / homeSortToggle / homeDirectionToggle / homeScanToggle / homeSourcesToggle / homeSettingsToggle
```

状态文字与转圈都由 `setHomeStatusVisible()` 一处管：横幅要显示才显示，转圈只在 `scanRunning` 为真时才转。收尾那句话还要在横幅上停 8 秒（`hideHomeStatusSoon()`），这期间已经没活可干，继续挂一个无限循环的属性动画等于告诉用户「还没完」，也白给静息的首页每帧重绘一次。

两个坑记一下：

- `ObjectAnimator.start()` 是 cancel + start，进度回调每 250ms 来一次就会把转圈按回零点，看着像卡住。改成 `if (!homeProgressSpin.isRunning()) start()`。
- 横幅收起时那一格用 `INVISIBLE` 不是 `GONE`。它带着 `weight=1`，收成 `GONE` 会让右边六颗按钮整体左移十几厘米，焦点还留在原地的那个坐标上，用户一按方向键就跑到别的按钮去了。

## 符号图标：`ui/Icons.java`

没有引矢量图标库，也没有放 PNG。TV 版整个 UI 都是代码画出来的（`KindIcons` 的地球与机柜、`Widgets` 的开关滑块都是 Canvas 画的），再加一个 drawable 文件目录不划算。`Icons` 沿用同一套做法：24 单位网格、`STROKE` 画笔、`of(Kind, sizePx, color, strokePx)` 返回一个 `Glyph extends Drawable`。

对应 mac 版那批 SF Symbols：`SEARCH` 圆 + 柄、`CHEVRONS` 上下两个折角、`ARROW_UP`/`ARROW_DOWN` 升降序、`SYNC` 两段 150° 弧加箭头、`FOLDER_PLUS` 文件夹加号、`GEAR` 八齿、`SPINNER` 285° 缺口弧；海报卡右下角的已看状态也从「已看/未看完/未看」三个词换成 `WATCH_NONE`（空圆）/`WATCH_PART`（半 filled）/`WATCH_DONE`（实心圆里用 `PorterDuff.Mode.CLEAR` 抠出一个对勾）。

按钮由 `Widgets.iconButton(icon, label, active)` 生成：`ImageView` + `FIT_CENTER` + 内边距，`contentDescription` 与 `tooltipText` 承担原来的文字（扫描进行中时 tooltip 会变成「扫描刮削（进行中）」），焦点态交给 `Theme.styleIconFocus()`——它继承按钮那套焦点语言，另外在聚焦或激活时给图标本身染上强调色。

## 选中态只剩放大

`Theme.FOCUS_DARKEN` 从 0.18 归零，注释写明它现在只是留着这个旋钮。所有 `style*Focus()` 的聚焦态改成「原填充色 + 强调色描边 + 放大 + 抬升」，播放器浮层那三条也一并从半透明白改成强调色。深色主题上「压暗一档」和「按下」在视觉上是同一件事，遥控器用户没法区分「当前选中」和「正在按住」。

## 「正在扫描中」但一张新海报都没有

这一条不是 UI 问题，而且是这一轮唯一真正的 bug。`LocalEngine.registerFile()` 里入队刮削长这样（两分支同构）：

```java
if (metadata.chineseTitle != null || metadata.foreignTitle != null) {
    library.enqueueScrape(...);
}
```

`LocalMediaNameParser` 是从文件名里抠结构化信息的（中英片名、SxxExx、年份、分辨率组），文件名规整的样本能解析出来，`Film 02`、`纪录片第3季`、或者一串发布组命名之外的路径就解析不出。解析不出就不入队——扫描器该登记的实体登记了、卡片也上墙了、进度也走完了，唯独刮削队列里没有它们。表现就是用户报的：按钮上明明白白写着「正在扫描中」，海报一张没多。手动匹配能成，是因为手动匹配根本不看队列，它直接拿用户给的关键词去搜。

现在收成一个 `queueScrape()`：先用解析出的中西文标题，解析不出退回展示标题，只要标题非空就入队。

顺带把收尾文案改成能自证的：

```
刮削完成，共处理 42 个条目：42 个请求失败，0 个没搜到，0 个年份对不上，跳过 1 个手动匹配的条目。
```

以前只报总数，「没跑」和「跑了但 TMDB 没搜到」看起来一模一样，而这两种情况用户该做的事完全相反（前者升级/重扫，后者改关键词）。

## 验证

模拟器 `omni_tv1080`（android-34 TV / arm64），假 TMDB 在宿主机 8099 端口，媒体源反代指向 `http://10.0.2.2:8099/tmdb`。这一轮之前那台机器的反代还指着真网关、钥匙是我早先填的假串，正好先验了一次失败路径：

1. 冷启动 → 左上角 `正在扫描媒体源...` → `刮削元数据和海报 (2/42)：Film…` → `(32/42)` → `刮削完成，共处理 42 个条目：42 个请求失败。`（假钥匙打真网关，401）。这 42 个就是上一版从没进过队列的那批：上一版同一台机器报的是「共处理 45 个条目」，42 张 `Film NN` 卡片原地不动。
2. 把反代改指假 TMDB 再点扫描 → 假服务器日志收到 89 条 `/search/multi`，进度走到 `(20/42)`，收尾 `刮削完成，共处理 42 个条目：42 个年份对不上。`——年份对不上是假服务器对所有查询都回同一条 2021 年的结果，属于夹具行为，不是代码。
3. 截图逐项核对：头部无品牌字、六颗纯符号按钮、聚焦的搜索键（放大 + 强调色描边 + 图标染强调色，填充没变暗）、横幅收起后按钮不左移、海报卡右下角只剩图标。

编辑输入框这次能用 `dumpsys activity top` 判定焦点（`uiautomator dump` 在有动画的界面上会 `could not get idle state`，只能退回截图）：视图行里 `.F......` 的那个 `EditText` 就是当前焦点，改完再按坐标点「保存配置」。

## 还欠着的

- 真机上验证这批以前没入队的条目会不会被搜到（模拟器夹具的标题就是 `Film`，注定搜不到东西）。
- 海报墙 `RecyclerView` 化仍是帧率地板，`Icons` 是代码画的，真要贴 mac 的符号细节（线帽、圆角）只能继续手调。
