# 2026-10-08 安卓 TV：遥控器滚动回弹、设置表单与刮削可见性（v1.14 / versionCode 25）

五个问题一起处理：设置里方向键有概率回弹、刮削时海报要等全部刮完才出现、「设置 / 媒体源」按钮有时点了没反应、设置弹窗按钮仍然不好看、以及上一轮 P0/P1/P2 清单的收尾。

## 根因

1. 方向键回弹。`FocusGrid.scrollToVisible()` 用 `view.getTop()` 判断焦点是否可见，而 `getTop()` 是相对**直接父容器**的坐标。设置面板里埋在行容器中的控件（主题胶囊、分段控件、卡片动作行）报告的是容器内偏移（几十像素），于是被判成「在视口上方」并往回滚。埋在面板直接子层里的控件坐标正确——所以现象是「有概率」。
2. 行序与视觉顺序不一致。`renderSettingsOverlay()` 在末尾手工维护一份 `rows`（视觉顺序改了十几次后没人同步），而 `FocusGrid.handleKey()` 完全按声明顺序移动焦点，于是按 DOWN 会在面板里上下跳，看起来也像回弹。媒体源面板同样手工维护。
3. ScrollView 的滚动量。子 View 的 `getTop()` 只在**下一次布局**之后才包含 `-scrollY`，所以「滚动后未重新布局」时累加值得到文档坐标、「滚动后重新布局」时得到视口坐标；用同一个公式处理就会在第二次按键时滚两倍距离。另外 `smoothScrollTo` 的动画会被下一次按键打断，观感正是来回弹。
4. 按钮点了没反应。`closeTopmostLayer()` 走 `overlays.closeTop()`，`Overlay.close()` 只移视图、弹网格，不会清 `MainActivity` 里的 `settingsOverlay` / `sourcesOverlay`；而 `openSettingsOverlay()` 开头是 `if (settingsOverlay != null) return;`。用 BACK 关过一次之后，那个按钮就再也打不开，直到换屏。
5. 海报最后才出现。`startStartupSync()` 的进度回调只刷新状态文字，`refreshHomeLibrary()` 在整轮扫描+刮削结束时才调一次。

## 改动

- `ui/FocusGrid.java`：新增 `viewportOffset()`（逐层累加 `getTop/getLeft`）与 `viewportTop()`（用内容视图布局位移与当前 `scrollY` 归一化，两种布局时刻都正确）；`scrollToVisible()` 改用即时 `scrollTo`；新增 `rowsFrom(ViewGroup)` 按真实几何推导面板行序（以行首控件中线为锚判断同行，锚点不随合并长大，避免级联吞行；未布局时退化为「横向容器算一行」的结构推导）与 `afterLayout()`；记住上次所在行列，焦点丢失时从原位置继续而不是一律回第一行。
- `ui/Overlays.java`：`Overlay` 增加 `onClose` 回调，`close()` 末尾触发；`redeclare()` 保持滚动位置（重新声明是重链，不是导航事件）；`StatusText` 对外开放，供面板内嵌「写入才显示」的状态行。
- `MainActivity`：六个弹层（设置、媒体源、登录、目录浏览器、手机输入、文本输入）都注册 `onClose` 清自己的宿主字段；设置与媒体源面板改用 `declarePanelRows()`（= `rowsFrom` + 布局后再声明一次）；重建设置面板时保留滚动与焦点。
- 设置面板重排成 mac 的分组表单：八个卡片（媒体源 / TMDB 刮削 / 外观主题 / 刮削与播放偏好 / 基础设置 / 离线缓存 / 4K DV/HDR 播放模式 / 关于与更新）。布尔项用 `Widgets.SwitchRow`（右侧画滑钮），互斥项用 `Widgets.Segment`（刮削语言 / 音频轨道 / 字幕轨道 / 网络缓冲 / 4K 播放模式），都是就地改样式，不重建设置面板；`restartSettingsOverlay()` 与其配套的按标签找回控件的辅助方法随之删除。卡片内独立成行的按钮固定 48dp（原先 `wrap_content` 压成 27px），版本与许可证移入最后的「关于与更新」。媒体源面板的动作按钮改用 `compactButton`，去掉标签前的 `✓` 缩进。
- 海报卡片抽出 `PosterViews` + `bindPosterCard()`，`scheduleScrapePosterRefresh()` 在刮削进度回调里 1.2 秒限流刷新：条目集合不变就逐卡就地换海报 / 片名 / 角标 / 观看状态，条目增减才重建海报墙，重建时用卡片 tag 里的条目 id 找回焦点（`restorePosterFocus`）。空海报不再是一块纯灰：显示片名首字（`artworkFallbackLetter`）。播放器菜单底色改为随主题取深色中性（`Theme.withAlpha`）。
- 版本号 1.14 / versionCode 25，`Http.USER_AGENT` 同步。

## 验证

Android 14 TV 1080p（`wm density 213`）+ `uiautomator` 驱动遥控器：

- 设置面板连按 26 次 DOWN：焦点始终在可视区内、命中顺序与视觉顺序逐行一致（探针日志给出各控件真实 y：145→228→414→510→606→689→861→944→1027→1110→…→3026，单调递增），到最后一行 `退出连接` 后停住；连按 10 次 UP 同样不丢焦点；整轮「焦点不在可视区」计数为 0。
- 设置 / 媒体源：打开 → BACK → 再点同一个按钮，两个面板都能重复打开（此前第二次失效）。
- 外观主题走到「午夜蓝」回车：整屏换肤且焦点仍在面板内；再往下走到「详情页显示分集简介与进度」回车：滑钮就地翻色，焦点与滚动位置一像素没动。
- 深色主题下首页 / 设置 / 卡片 / 空海报占位一体；`AndroidRuntime` 无 FATAL；`logcat -s OmniPlayLibrary` 看到 `刮削中刷新：就地更新 4 张海报`，证明增量路径在进度回调里真的跑到了（模拟器未配 TMDB Key，海报逐张出现的效果要靠真机验证）。
- 六个 JVM 探针全绿（`lanprobe ALL_OK`、`WEBDAV_OK`、`SMB_OK`、`run-saved ALL_OK`、`run-phone ALL_OK`、`REGEX_OK`），说明 `local/` 仍未反向依赖 `ui/`。

## 已知边界

- 4K 分支（`uiScale()` 在屏宽 ≥2500px 时放大布局）仍没在真 4K 表面验过：本机 `omni_tv4k` 的合成表面固定在 1920x1080。
- 刮削期间海报逐张出现依赖真实 TMDB 响应，模拟器只验到刷新链路；真机若仍表现为「最后一起出现」，先确认状态条是否在同一时刻只更新一次（那说明回调没打进来，而不是渲染问题）。
- 播放器覆盖层仍是深色玻璃板压在画面上（只有底色取主题），没有做主题化播放器皮肤。
