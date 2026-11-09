# 2026-10-30 安卓 TV：认得出 NAS 写回的字幕 + 手机端补齐（1.44）

## 一、播放页找不到 NAS 转档出来的字幕

### 现象

转档容器把 `片名.zh.srt` / `.ass` / `.sup` 写在视频同目录，电视端播放页字幕菜单里一条都不出现，只剩内封轨。

### 原因

两条各自独立的坑，凑在一起就是「全找不到」。

1. **拿错媒体源去探名。** `LocalEngine.sidecarAccessFor(relativePath)` 的写法是「遍历启用的源，先碰见 WebDAV 用 WebDAV，否则用 SMB」，与这条文件到底存在哪儿无关。SMB 上的片子于是被拿去 WebDAV 根目录探 `片名.zh.srt`，路径都不存在，HEAD 全 404。
2. **文件名猜不全。** 探名用的语言来自内封字幕轨的 tag（最多三条）加 `zh` / `und` / `en` 兜底。转档服务写回的名字是 `片名.<内封轨自带语言 tag>.<格式>`，压制组常写 `chs` / `cht` / `zh-CN` / `zhs`，这些都不在猜测表里；反过来 `zh` 猜到了也可能因为格式是 `.sup` 而落空。

### 改动

- `SubtitleSidecarService.Access` 加 `listFileNames(directoryRelativePath)`：本地 / WebDAV / SMB 三个实现都会列目录（WebDAV 走 PROPFIND，SMB 走 QUERY_DIRECTORY），只回文件不回头目录本身。返回 `null` 表示「这个源列不出来」，调用方退回老的逐个探名；空列表是「列出来了，目录里就这些」。
- `SubtitleSidecarService.sidecarNamesFor(videoRelativePath, siblingNames)`：纯函数，同目录文件名里挑出属于这条视频的 sidecar。要求前缀是 `片名.`（大小写无关），后面必须是 `<语言>.<后缀>`，后缀限 `srt|ass|sup`，语言段还得像个语言码（`[a-z][a-z0-9-]{0,7}` 且能被 `normalizeLanguage` 认下），于是 `片名.1080p.mkv`、隔壁那部片子的字幕都不会被误收。登记进库时按 `语言.格式` 去重，轨位从 900 起（≥900 即「不是转出来的，是媒体源里现成的」）。
- `LocalEngine.discoverSidecars()` 先按文件 id 里的 `local-<媒体源id>:` 解析出这条文件属于哪个源（`sourceForFileId`），用那个源的凭据列目录（`accessFor(source)`）；认不出来才退回 `sidecarAccessFor(relativePath)` 那个「谁先看得到用谁」的兜底。列不出来才 HEAD 探名，且把内封轨的原始 tag（`chs`、`zh-CN` 这种）与规范化后的语言码都试一遍。
- `recordExternalSidecar()` 负责登记，`trackNumber` 递增避免同一目录重复登记。
- `WebDavClient.forgetDirectory(url)`：转档结果刚写回时不能被旧目录清单挡住，`list()` 的缓存要先丢掉再列。
- `MatroskaSubtitleExtractor.normalizeLanguage()` 认得 `chs` / `cht` / `zhs`，菜单里统一显示「中文」。

`transcodeSidecar()` 与 `readSidecar()` 一并改用「按文件所在源解析」的两参版本，否则写进去找得着、读出来又找不着。

harness 里补了 8 条针对这段的检查（本地目录列举、`chs` 显示成中文、`zh-CN`、`und`、同名多格式只收 3 条、`1080p` 不当语言、隔壁片子的字幕不串台、根目录），字幕转档这一组 43 项全过。

## 二、直出模式的字幕弹窗收起内封轨

`vo=mediacodec_embed` 下 mpv 不参与渲染，内封字幕点中了画面上一片空白。电视端 `showSubtitleMenu()` 在 `activeDirectPlayback` 时不再列内封轨（含 runtime 那批），只列覆盖层读得懂的媒体源外挂字幕与 sidecar；内封轨的条数换成一行说明「直出不渲染内封字幕（已收起 N 条）· 改用兼容字幕播放」，点它就是 `switchPlaybackModeDuringPlayback(false)`。兼容模式照旧列全。

## 三、手机端功能同步

`PhoneMainActivity` 原来只有海报墙 + 详情 + 一个三键播放条，电视端的能力大部分没跟上；共享逻辑这次从 activity 里搬出来放进新包 `phone/`，UI 按触摸重画，功能与电视端对齐：

- `PhoneHost`：宿主接口，抽屉类只认这个接口，不从 activity 里捞私有成员。
- `PhoneUi`：底部抽屉（82% 高、遮罩、圆角顶）、区块 / 按钮 / 输入框 / 状态行，配色全取 `ui/Theme`。
- `PhoneSourceSheet`：已挂载（启用停用、就地重命名、移除）、局域网自动发现（`LanDiscovery`，关抽屉即取消探测）、上次登录过（`SavedConnections`）、添加 WebDAV / SMB / 本地目录；挑本地目录沿用「每行一个 ★，点 ★ 确认这一级，点行进入下级」。
- `PhoneLoginSheet`：地址 / 用户 / 密码 → 登录 → 列目录（SMB 先 `list` 再退 `listShares`，WebDAV 走 `streamUrl("")` + PROPFIND）→ 挑文件夹 → 挂载即保存 + `refreshLibrary()` + 后台扫描刮削。
- `PhoneSettingsSheet`：主题（`Theme.VARIANTS` 全 8 档）、TMDB Key / 反代网关 / 反代 Key 与检测、刮削语言、开机扫描、扫描后刮削、分集简介与进度、真实源路径、默认音轨与字幕语言、网络缓冲、清空媒体库（带确认）、关于。
- `PhoneMatchSheet`：手动匹配（TMDB 与豆瓣两条候选链路，候选带海报，不填 TMDB Key 也能只靠豆瓣；支持直接粘豆瓣链接绑定）与自定义编辑（标题 / 年份 / 评分 / 类型 / 导演 / 主演 + 海报预览与替换）。
- `PhoneSubtitleOverlay`：文本字幕（srt 与 ass 都收进同一套 cue，`<i>`、`{\pos}` 这类标记剥掉）走 TextView，PGS 位图轨走 `PgsSubtitleView`，按播放位置取当前那条，抬高到控制条与黑边之上。

首页与详情页补齐：页头三颗符号按钮（添加媒体源 / 扫描刮削 / 设置）与一行扫描刮削进度；海报标题居中、已看状态只留图标；详情页有 TMDB 与豆瓣评分、类型、导演主演、豆瓣绑定、不折叠的简介、已看进度与剩余时间、分集剧照（多版本才显示版本数），右上角两颗图标进手动匹配与自定义编辑。

播放页：控制条加了「字幕」「音轨」「模式」「比例」四颗按钮与横向滚动（八颗按钮在窄屏上会被裁掉）；长按 «10 / 10» 与遥控器左右方向键走同一套步长爬升（0.7 秒内 10×、1.8 秒 30×、3.5 秒 100×、再久 250× 实时速度，松手停）；`已看 x / 剩 y` 时间条每两秒刷；字幕菜单在直出档同样收起内封，外挂字幕与 sidecar 交给覆盖层，`sup` 用 `PgsSubtitleParser` 解；「再找一遍媒体源里的外挂字幕」「后台转档这条的内封字幕」两条手动入口照搬电视端；切换播放模式先记进度再原地重开，走 `resolvePlaybackTarget`，SMB 代理与 ISO Range 代理的地址不会在切换时丢掉；播放模式与画面比例分别写回偏好（直出那档与电视端共用 `omniplay_tv_ui` 里同一个键）。

## 四、验证

- `compile.sh`：绿。
- `harness.sh`：绿（含字幕转档 43 项）。
- `release.sh`：出 `dist/OmniPlay-TV-v1.44-arm64.apk`（`versionCode 55` / `versionName 1.44`）。
- Mac Android Subsystem：装的是 arm64 包，跑通手机界面首屏海报墙、详情页与播放页。

## 五、在 Mac Android Subsystem 上跑手机界面之后补的几处

用 MAS（`/Applications/Mac Android Subsystem.app`，LineageOS 24 / Android 17，arm64）真跑了一遍手机界面，看到的问题都补在同一版里：

- **「所有影视 N 部」虚高。** 墙上两张海报，标题却写「3 部」。`LocalEngine.libraryItemCount()` 直接数 `entity` 表，而 `libraryItems()` 会跳过没有文件的条目（媒体源停用、文件被删、只登记未入库），两边口径不一样。改成同口径：只数真能上墙的条目。
- **状态栏 / 导航栏压内容。** 手机上页头「媒体库」被状态栏吃掉半行，空库那颗「添加媒体源」整颗藏在导航栏后面。给根 `FrameLayout` 加了 `setOnApplyWindowInsetsListener`，按 system window insets 留内边距；播放页是全屏幕，系统栏让开时 insets 归零，不受影响。
- **空库提示贴屏幕底。** 以前 `emptyLibraryCallToAction()` 挂在权重 1 的滚动视图下面，按钮掉到屏幕外。现在空库时它自己占满剩余空间并居中，文案也居中。
- **添源入口走错门。** 空库那颗按钮原先开的是整屏设置页，与电视端不一致，改成 `PhoneSourceSheet.show(this)`；「刷新」改成 `scanInBackground()`。
- **本地目录读不到。** 安卓 11 起读 `/sdcard` 要「所有文件访问权限」，`listFiles()` 直接返回 `null`，抽屉里只写「这个目录里没有子目录」，看不出是没权限。加了 `addStoragePermissionHint()`：说清楚读不到哪个目录，并给「去开启存储权限」跳 `ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION`（没有这个设置页的系统退回应用详情页）。
- **播放页左上角还是 `S00E01`。** 照电视端补了 `playerHeadingFor()` / `playerSummaryFor()`：标题用详情页片名，季集号（含「特别篇第 N 集」）与封装摘要放第二行。
- **扫描把海报墙拖白屏。** `scanAndScrape()` 压在共享的 4 线程 io 池上，一跑几分钟（等 TMDB 时更久），`getLibraryItems()` 排在后面，整屏停在「正在加载媒体库」。扫描改走独立线程 `omniplay-phone-scan`；`showHome()` 在墙上已有内容时不再盖整屏「正在加载」，失败也只弹一条 toast。

MAS 上验到的：首屏海报墙（标题、`独立版 · 1 个媒体源 · 2 部`、扫描刮削进度行）、媒体源抽屉（已挂载 / 局域网 / 上次登录过 / 三个添加入口）、本地目录抽屉（`★ 挂载这个目录` + 每行 ★ / 进入）、挂载后自动扫描上墙、详情页（返回 / 手动匹配 / 自定义编辑 / 片名 / 已看图标 / `0:00 / 剩 0:00` / 开始播放 / 文件行 `S00E01`）、播放页（`«10 ↺10 暂停 10» 字幕 音轨 模式 比例` 八颗 + 右上模式角标 + 已看时间）、字幕弹窗（`✓ 媒体源外挂字幕 · 中文（SRT）` + 关闭字幕 + 再找一遍 + 后台转档 + 关闭）。推给虚拟机的两个 mp4 旁边放了 `Interstellar.2014.1080p.zh.srt`，字幕菜单里认出来了并且默认选中，说明按文件所在媒体源列目录这条链路在本地源上也成立。

覆盖层的逐帧显示没能在 MAS 上验：虚拟机里 mpv 的播放位置不推进（`已看` 一直 `0:00`），取不到当前位置就永远不弹字幕，这条留给真机。为它补了 JVM 用例 `build/harness/PhoneSubtitleCheck.java`（SRT 条数与时间轴、`<i>` 与 `{\pos}` 剥离、ASS 按 `Format:` 行取 Start / End / Text、坏块跳过），`harness.sh` 里与其余检查一起跑，手机字幕 13 项全过。

> 顺带记一句 MAS 的脾气：它是菜单栏应用，虚拟机「smart」模式闲两三分钟就关；`open -a "Mac Android Subsystem.app" <apk>` 会开机 + 装包，但应用不一定被带到前台，随后补一次 `adb -s 127.0.0.1:58526 shell am start -n com.omniplay.tv/.LauncherActivity` 才稳定。自动化脚本见 `/tmp/drive*.py`（本轮临时用，未入库）。

## 八、同日补的两处

- **播放页控制条收起来就叫不回来。** 电视端有遥控器，方向键一动 `showPlayerChromeTemporarily()` 就把 chrome 叫回来；手机没有遥控器，四秒自动收起之后只能盲点。`mountPlayerView()` 里给播放器视图本身加上点击（`playerView.setClickable(true)` + `setOnClickListener(v -> showChromeTemporarily())`），点一下画面就唤回控制条与状态条。这一段原先在 `playFile()` 与 `switchPlaybackModeDuringPlayback()` 各抄了一份，合并成这一个方法，两处调用。
- **刚挂完目录就被杀进程，媒体源清单整屏归零。** `persistSources()` 用的是 `preferences.edit().putString(...).apply()`——异步落盘，写没落地进程就没了，重启后「还没有媒体源」，可文件其实都在库里。媒体源清单是这台设备上最重要的几条配置，改成 `.commit()` 同步落盘。
