# 安卓 TV 版：主题、详情页与界面走查（1.13 / versionCode 24）

2026-10-07。上一轮（1.12）在模拟器上把每个界面都截了图，列了一份 P0/P1/P2 清单，这一轮按清单逐条改。设备：Android 14 TV 镜像、`omni_tv1080`、`adb shell wm density 213`（与真机 1080p 电视同比例）。

## 1. 主题：搬 mac 的五套浅色，再补三套深色

`ui/Theme.java` 原来是十来个写死的 `static final int`。现在改成 mac 版 `ThemeManager` 的形状：`Variant` 持有一个外观的五个颜色（强调色 / 底色 / 面板色 / 主文字 / 次文字）加一个 `dark` 标记，`VARIANTS` 列八套，`apply(id)` 把其余颜色全部推导出来。

- 浅色五套的十六进制值与 mac 版一致：`appleLight`、`crystal`、`linen`、`mint`、`rose`，默认仍是 `crystal`。
- 深色三套是为电视补的：`midnight`、`graphite`、`oled`。深色下面板色从底色抬起（否则面板与背景糊在一起），强调色提亮 14%（同一种蓝在暗背景上对比会掉），评分色与危险色也各提一档。
- 派生颜色：`SURFACE_ALT`、`BORDER`、`MUTED`、`FOCUS`、`FOCUS_SOFT`、`DANGER`、`RATING`、`RATING_DOUBAN`、`GLASS*`、`SCRIM`，以及首页背景 `appBackground()`（由底色与强调色生成，深色下浓度更高）。界面代码里没有第二处颜色字面量。
- 所有 palette 字段由 `static final` 改为 `static`，`MainActivity` 里 12 个 `COLOR_*` 别名同样去掉 `final`，新增 `syncThemeColors()` 在 `Theme.apply()` 之后重抄一遍，88 处旧引用不用动。
- 焦点样式统一成一条规则：**3dp 强调色描边 + 8~14dp 抬升 + 1.045 倍微放大**，按钮 / 行 / 输入框 / 海报卡 / 播放器控件只差圆角和底色。`STROKE`、`STROKE_FOCUS` 由 `Theme(Context)` 按密度换算，4K 面板上不会退化成一根发丝线。3 米外看不清焦点是走查里最重的一条。

选择入口在`设置` → `外观主题`，每项前一个「面板色 + 强调色」双层色块（`LayerDrawable`），当前项前加 `✓`，两列一行。落点是 `TvSettings.themeId()`（键 `theme_id`）。默认值常量 `DEFAULT_THEME_ID` 放在 `TvSettings` 而不是 `Theme`：`local/` 反向依赖 `ui/` 会让只编译 `local/` + `data/` 的 JVM 用例编不过，改完 `WEBDAV_OK` 等五个用例才重新跑通。`onCreate` 在 `CrashLog.install()` 之后 `Theme.apply(...)`，`theme = new Theme(this)` 之后 `syncThemeColors()`。

两个坑：

1. 换肤只调 `refreshHomeControls()` + `refreshHomeBody()`，标题、工具栏按钮、状态行仍是旧配色——它们是在 `buildHomeShell()` 里按当时的调色板画出来的。改成 `applyThemeChange()`：记住滚动位置 → 关掉当前面板 → `homeScrollView = null` 逼外壳重建 → 重开原来那个面板。
2. 海报占位图是 `ImageLoader.placeholder()` 里写死的浅灰渐变，深色主题下四个亮块很扎眼，改成从 `Theme.SURFACE_ALT` 派生。

## 2. 详情页按 mac 补齐

对齐 `MovieDetailView`：

- 底图：Android 12+ 用 `RenderEffect.createBlurEffect` 实时虚化海报（`addDetailBackdrop()`），上面压一层与主题同色的柔和底；12 以下不做实时虚化，只留柔和底。
- 右栏：片名 → 元信息行（`preferredReleaseYear()` 优先豆瓣年份；`TMDB ★ 8.4` 用 `Theme.RATING`、`豆瓣 ★ 9.1` 用 `Theme.RATING_DOUBAN`，mac 只靠颜色区分两种星，电视上离得远所以加上来源词；类型与制片地区优先 TMDB 的 `genres`，缺失时回落豆瓣）→ `导演` / `主演` 标签行（固定宽标签列 + 正文，`、` 分隔，主演取前 6，与 mac 同构）→ 简介（豆瓣 summary 兜底，空则「暂无简介」；超过 96 字给展开 / 收起）→ 播放按钮与 `已播 / 总时长` 进度条。
- 播放按钮：电影页不再带集号。`playButtonText()` 原来只看文件有没有 `seasonNumber`，而单文件电影目录会被解析成 S00E01，于是电影页显示「开始播放 特别篇第 1 集」。改成 `playButtonText(file, includeEpisodeLabel)`，详情页按 `itemKind` 传值。
- 观看状态三态文案对齐 mac：`未观看` / `观看中` / `已观看`，原来是不成词的「未播 / 未播完 / 已播」。
- `版本 / 文件（N）` 每行三行信息：`✓ 版本 N · 文件名`、相对路径 + 封装 / 编码摘要、`已播时间 / 总时长 · 未播放 | 继续播放 | 已播放完`；当前正片带 `✓`，与上方按钮对应。
- 分集卡：集号只在标题或信息行出现一次（`episodeFacts(episode, includeLabel)` 按标题里是否已含集号决定），`副标题` 按钮改名 `改副标题`，动作按钮 40→44dp，季切换 48→52dp。

## 3. 走查里其余的修改

- `Overlays` 的 `sideSheet` 变成真正贴右边缘的通高侧滑栏。之前是 `WRAP_CONTENT` + 上下留边，看着像从右上角掉下来的一块，`媒体源` 面板下半截全是空的。
- `Widgets.HEIGHT_BUTTON` 54→56、`HEIGHT_COMPACT` 44→48；`optionRow` 单行 + 省略号 + 16sp。
- 媒体源面板的 `添加媒体源` / `重新扫描并刮削` / `清空媒体库索引` 从三等分改成整行堆叠，标签不再折行；每行内的 `停用` / `重命名` / `移除` 保持三列，加高到 46dp。
- TMDB 反代输入框的提示缩到「反向代理地址（可选）」，长说明挪到下面一行小字；那一长串既像标签又像输入框。
- 筛选行拆成「类型」「状态」两组，中间一条竖分隔线。之前两个 `全部` 同时高亮，看着像两个互斥筛选器都选中了。
- 「继续播放」为空时整节隐藏（含标题），不再占 170px。
- 海报卡右下角的 `○` 改成 `○ 未看` / `✓ 已看`——它本来是能按的切换控件，之前被当成了装饰。
- 未刮削条目的展示名走 `prettifyMediaTitle()`：全串无空格且含点号 / 下划线才整理（`The.Last.of.Us.2023` → `The Last of Us 2023`），带空格的正式片名原样保留。
- 手机辅助输入的二维码 188dp → 320dp（位图 300dp），面板加宽到 470dp，说明补了「需同一局域网」。
- `CrashLog.summarize()` 附带抛出位置（方法名:行号）。详情页那条只写「NullPointerException」的提示因此定位到 `refreshSubtitleCacheStatus()`：本机目录没有字幕记录，接口返回 `null`，而 `ConcurrentHashMap.put` 不接受 null 值。顺带给 `detailCache.put` 加了 null 保护。
- 冷启动界面一个焦点都没有，遥控器第一下按键被「建立焦点」吃掉：`buildHomeShell()` 末尾 `root.post(this::requestFirstHomeFocus)`。

## 4. 验证

- `javac` 全量类型检查 + `./gradlew assembleRelease`（在线：`com.google.zxing:core` 不在 Gradle 缓存里，`--offline` 会挂在 `checkReleaseAarMetadata`）。产物 `dist/OmniPlay-TV-v1.13-arm64.apk`，`aapt2 dump badging` 确认 versionCode 24 / versionName 1.13，APK 不比源码旧。
- 模拟器 D-pad 回归：冷启动焦点落在第一张海报；`筛选` 打开后两组标签与分隔线正确；`媒体源` 面板 `停用` 上有焦点框、三个整行动作都能到；`设置` 面板停在「添加媒体源」；主题八项齐全，`✓` 随点击移动；深色下首页、设置侧栏、详情页、分集卡配色一致，切回浅色正常；`logcat -s AndroidRuntime:E` 无 FATAL。
- 详情页不再弹异常提示；手机输入面板二维码在 1080p 上占约 320px，扫得动。
- JVM 用例：`WEBDAV_OK`、`SMB_OK`、`ALL_OK`（已保存连接）、`ALL_OK`（手机输入服务）、`REGEX_OK`、局域网发现 `ALL_OK`。

## 5. 仍未覆盖

- `omni_tv4k`（`tv_4k`）这台 AVD 的宿主表面被模拟器降到 1920x1080（`wm size` 显示 override，`screencap` 也是 1080p），所以 `Theme.uiScale()` 里 ≥2500px 那一档在本机还是没法跑；4K 真机或宿主能开 4K 窗口时要补这一眼。
- 深色主题只验过无海报的未刮削库：真实海报的虚化底图与海报上文字的对比度，要等 TMDB 刮削完成后在电视前再看一次。
- 播放器这轮没动：`GLASS` 虽已随主题派生，但播放器顶栏的底衬仍是半透明黑，深色下可能偏亮。
