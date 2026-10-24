# 安卓 TV 1.32：遥控器改字、播放模式进播放页、特别季排最后

对应反馈十三条，归成五组：详情页与弹层的按钮位置和尺寸（手动匹配四个按钮挪到候选上面、手机
输入的「关闭」、解除豆瓣绑定、选中态的蓝框）；季集结构（`S00E01` 认成特别季并排最后、剧集不再
讲「版本」、同集多文件平铺进分集剧照）；遥控器改字（输入框左右键移动光标）；播放器（点手动匹配
弹回详情页、ISO 报 -16、左上角名称、播放模式从设置搬进播放页、直出没有内封字幕、进度条右侧剩余
时间）。

## 一、`S00E01` 是特别篇

`LocalMediaNameParser.parseEpisode()` 以前在末尾把 `season == 0` 一律抬成 1，理由是没有季号的
普通文件（`Movie.2010.1080p.mkv`）会算出 0，不抬就冒出一个孤零零的「第 0 季」。代价是明写出来的
`S00E01` 也被抬进第一季，跟 `S01E01` 并成同一集，详情页里两张剧照一样、文件名却不同。

改成看来源：`parseEpisode` 内部记一个 `explicitSeason`，只要 `SEASON_EP`（`S(\d+)E(\d+)` 一类）
命中的是文件名本身，或者目录名里带 `Season/季` 提示，就置真；收尾的钳位变成

```java
if (season == 0 && !explicitSeason) {
    season = 1;
}
```

第 0 季在界面上是一等公民：`LocalEngine` 分组不再 `Math.max(1, file.season)`，季序排法是
`Comparator.comparingInt(n -> n == 0 ? 1 : 0).thenComparingInt(n -> n)`，标题写「特别篇」，
分集标题写「特别篇第 N 集」，`defaultSeasonId()` 也优先选第一个非 0 季，进详情页默认停在正片季。

扫描是增量的，老库里的行不会重算，所以补了两处：

1. 数据库版本 4 → 5，`onUpgrade` 新增分支调 `reparseEpisodeColumns(db)`，把 `showId IS NOT NULL`
   的 `videoFile` 逐行按当前规则重算季集号写回去。
2. `insertVideoFile` 在 `INSERT OR IGNORE` 之后补一条按 id 的 UPDATE（只在值真的变了时写），
   避免旧行把新解析结果顶掉。

## 二、剧集没有「版本」

mac 版那一节的条件是 `detail.videoFiles.count > 1`，而且只针对电影。TV 版以前给每集都冠一句
「版本 N · 文件名」，同一集两份拷贝被说成两个版本，用户看到的就是「剧集不该有版本」。

- 电影：`renderMovieVersions()` 保持 `files.size() <= 1` 直接 return，标题「版本 / 文件（N）」，
  每条下面两行小字（路径、时长与大小）。
- 剧集：分集剧照那节标题改成「文件（N）」，按钮上就是 `versionFile.fileName`，不再冠「版本 N ·」。

## 三、弹层里的按钮位置与尺寸

手动匹配弹层原来是「输入区 → 候选列表 → 底部按钮行」，候选是从 TMDB 回来的长列表，想点「手机
输入」得先把它滚穿。现在 `manualMatchResults` 在 `overlays.footer(...)` 之后才创建，顺序是
源文件与关键词 / 年份 / 豆瓣输入区 → `关闭 · 手机输入 · 绑定豆瓣 · 搜索` → 候选列表，按钮行与
列表之间留 10dp 顶边距。

两处尺寸：

- 手机输入弹层的「关闭」以前走 `footer()` 的等分宽度（1920 宽下 300px 反而不合适——它跟说明文字
  挤在一起、又矮），现在固定 `sdp(216)` 宽、`Widgets.HEIGHT_BUTTON + 10` 高、水平居中。
- 「解除豆瓣绑定」由 `matchWidth()` 改成 `WRAP_CONTENT`，同样加高 10dp，红字危险操作不再是整条
  横幅。

## 四、遥控器在输入框里挪光标

`dispatchKeyEvent` 里左右键一律先进 `handleDpadChain()` 做焦点导航，`EditText` 拿到焦点后光标
钉在文本开头，用户的手感就是「删不掉前面的字」。现在在 `handleDpadChain()` 之前先问一次
`moveCaretInFocusedField(event)`：

- 只在 `ACTION_DOWN` + `DPAD_LEFT/RIGHT` + 当前焦点是 `EditText` 时接管；
- 有选区先收拢到选区靠方向的一端（与系统输入框同一手感）；
- 没选区就把光标挪一格；
- 已经在文本头 / 尾就返回 `false`，交回焦点导航，跨出输入框不受影响。

`FocusGrid.handleKey` 也只看 `ACTION_DOWN`，所以吞掉 DOWN 就足以让 UP 事件不再触发一次焦点跳转。
删除键因此能精确落在光标处，实测 `2022` 左移两格再按删除得到 `222`。

## 五、点手动匹配有概率弹回详情页

弹层没被别的东西关掉，是被自己人拆的：`renderDetail()` 开头就是

```java
closeAllOverlays();
root.removeAllViews();
```

而详情页有两处异步刷新（刮削回来的 `refreshDetail`、播放进度回来的
`refreshPlaybackSettingsForDetail`）都会调它。刷新恰好落在刚打开弹层那一下，弹层当场消失。

修法是给 `Overlays` 加一个 `public Runnable onStackChanged`，在 `open()` 压栈和 `Overlay.close()`
出栈时各回调一次；`MainActivity` 侧 `overlays.onStackChanged = this::afterOverlayStackChanged`，
两处刷新发现 `overlays.hasOpen()` 时只更新 `currentDetail` 并置 `pendingDetailRerender = true`，
等栈空了再补一次重绘；`renderDetail()` 自己接管时先把这个标志清掉，避免连绘两次。

## 六、播放器

1. **左上角名称。** 以前那一行是文件名。新增 `playerHeadingFor(file)`：刮到资料就用
   `currentDetail.title`（`itemKind` 对不上时不瞎猜），否则退 `file.label()`；新增
   `playerSummaryFor(file)` 把 `episodeDisplayLabel(...)`、分集标题、`file.mediaSummary()` 用
   ` · ` 连起来放第二行。
2. **播放模式挪进播放页。** 设置里「4K DV/HDR 播放模式」整节（分段控件 + 说明）删除；
   `playerControls()` 的工具条末尾挂一个 `playerModeButton`，文案取 `activePlaybackStatusText(file)`
   即当前模式，点开 `showPlaybackModeMenu()` 两项：`兼容字幕播放 · 字幕由播放器画`、
   `高性能直出 · 视频直接进解码器`。选择走 `switchPlaybackModeDuringPlayback()`：写偏好 →
   `postPlaybackProgress()` 记进度 → `playFile()` 原地重开。焦点链上字幕按钮的右边就是它。
   `shouldUseDirectPlayback()` 开头加 `if (isIsoFile(file)) return false;`。
3. **进度条右侧剩余时间。** 右标签由总时长改成 `"-" + formatPlaybackTime(duration - position)`，
   时长未知显示 `--:--`，无障碍描述带「已播 / 总时长」。
4. **选中态去蓝框。** `Theme.focusFill(base)`：焦点态背景 = 主体色往焦点色提亮
   （深色主题 20%、浅色 10%），边框透明。`styleButtonFocus` / `styleIconFocus` / `styleRowFocus` /
   `styleInputFocus` / `styleCardFocus` / `styleBadgeFocus` 与播放器三处控件统一改用它，只保留
   1.045 倍放大与阴影。

## 七、蓝光 ISO：`bd://` 从来没生效过

`播放失败：no audio or video data played（-16）` 不是解码失败，是根本没有码流进来。查随 APK 打包
的 `libmpv.so` / `libavformat.so`：`bd_open`、`bd_read` 一类符号都不在，协议表里也搜不到 `bd` /
`dvd`，说明这份 ffmpeg 没编 libbluray，`bd://longest` 这个地址以前就没人能打开，报 -16 是必然。

现在走本机代理，普通 TS 交给 mpv：

1. `RemoteIsoStreamServer.prepare(sourceUrl, cookieHeader)` 用 HTTP Range 读 ISO 的
   UDF / ISO9660 目录树，在 `BDMV/STREAM` 里挑最大的那条 m2ts；
2. 本机起服务，播放地址 `http://127.0.0.1:<port>/remoteiso/<route>/stream.ts`，mpv 侧就是普通
   网络 TS，兼容模式下字幕、seek、进度都正常；
3. 挑不到视频流时报错文案点名：「这个 ISO 里没有找到 BDMV/STREAM 视频流（目前只支持蓝光 ISO，
   DVD 的 VIDEO_TS 暂不识别）。」

`resolvePlayback()` 里 ISO 分支只剩 `resolveIsoPlaybackUrl(finalUrl, "")` 一条路，`bd://` 那条
退掉。这一条是结构性修复：模拟器没有真蓝光 ISO，真机验证需要 NAS 上挂一份。

## 八、直出模式的内封字幕

直出是 `vo=mediacodec_embed` + `hwdec=mediacodec` + `vd-lavc-dr=yes`：解码后的帧由 MediaCodec
直接送进 Surface 上屏，mpv 的字幕与 OSD 渲染器不参与合成，所以内封的文本字幕和 PGS 位图字幕都不
显示——不是没读到字幕轨，是画不上去。本版做法：字幕菜单里，当次是直出且这个文件确实有字幕轨时，
末尾多一项「直出不渲染内封字幕 · 改用兼容字幕播放」，点一下切过去并接回原进度。

其余可选路线（本版未做）：Android 侧盖一层字幕 View，自己排版，顺带能做字幕样式与弹幕；播放前把
内封字幕抽成临时 SRT 用 `--sub-file` 挂上；或者选中字幕轨时自动降级到兼容模式（现在按用户显式
选择处理，不偷偷降级）。

## 九、验证

JVM 用例 `NameYearCheck`、`DoubanParseCheck` 全绿（改动解析器后重新按全量源码编译再跑）。模拟器
（Android TV 1080p）+ 假 WebDAV + 假 TMDB 跑真链路，夹具补了一条
`Slow.Horses.Specials.2022.1080p.WEB-DL/Slow.Horses.2022.S00E01.BDSpecial.1080p.WEB-DL.mkv`
和同集第二份拷贝 `Slow.Horses.2022.S01E01.1080p.BluRay.x264-DIY.mkv`：

- 数据库 v4 → v5 迁移正常，冷启动海报墙照旧带海报渲染，`所有影视` 11 部；
- 详情页季按钮 `第 1 季 | 第 2 季 | 特别篇`，特别篇卡片标题「特别篇第 1 集」；
- 第 1 季第 1 集那节是「文件（2）」加两条原始文件名，没有「版本」字样；
- 手动匹配弹层顺序为输入区 → 按钮行（y 677-752）→ 候选列表（y 800 起）；手机输入弹层的「关闭」
  是居中的一块 300×92px；
- 焦点态截图：输入框、按钮、海报卡片都是提亮 + 轻微放大，没有描边；
- 年份输入框光标左移两格再删除：`2022` → `222`；
- 播放页左上角 `中文译名8001` + `第 1 季第 1 集`，模式按钮点开两项菜单，切「高性能直出」后原地
  重开；设置弹窗滚到底只有 `刮削与播放偏好` / `基础设置` / `关于与更新`，没有播放模式那一节；
- 版本号一行显示「版本 1.32」。

没能在这台模拟器上验的三处：夹具文件时长为 0，进度条右侧只能显示 `--:--`；假 WebDAV 回的是占位
字节，起播就 `loading failed (-13)`，内封字幕的可见性要真片；ISO 需要真蓝光 ISO。
