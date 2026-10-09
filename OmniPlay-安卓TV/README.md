# OmniPlay 安卓 TV 版

安卓 TV / 手机客户端，技术栈为 Java + libmpv（NDK 编译 JNI 桥接层）。
当前版本为独立版（versionName 1.17）：媒体库完全在本机工作，不依赖 OmniPlay Docker 服务端。
功能对齐 ARM mac 版（`OmniPlay开源-mac-ARM`），交互按遥控器 D-pad 逻辑重做，不照搬 macOS 布局。

## 功能范围

- 启动器双入口：TV 界面（LEANBACK_LAUNCHER，遥控器 D-pad 焦点导航）与手机界面（触摸海报网格、横屏播放器），由 `LauncherActivity` 自动分发。
- 独立媒体引擎 `LocalEngine`：本地文件夹、WebDAV、SMB 媒体源，本机 SQLite 库扫描，支持按季/集解析（中英文件名、SxxExx、第 N 集等）。
- 登录页（TV 全屏，沉浸式）：媒体源类型按 WebDAV、SMB、本地目录排列（默认选中 WebDAV）；进入 App 后自动扫描局域网 `LanDiscovery`（子网枚举 + 每个「主机 × 端口」一条探测任务的 TCP 探测 + OPTIONS `DAV` 头 / PROPFIND 验证 + NetBIOS 主机名查询），边扫边列出 WebDAV / SMB 设备，行高足够显示第二行的地址或已挂载目录；点选设备或手动填写地址后弹出登录框（地址 / 用户名 / 密码），登录成功再弹出“选择媒体目录”浏览器（WebDAV 目录树 / SMB 共享与子目录 / 本地文件夹管理器），选定目录后保存媒体源并自动扫描进入首页。TMDB API Key 与代理网关在首页设置中配置，不在登录页。
- 已保存的连接（`SavedConnections`）：WebDAV / SMB 只要登录成功一次，账号密码与该次挂载的目录就按「协议 + 主机 + 端口」记进本机偏好（最多 12 台服务器、每台 4 个目录，最新在前）。登录页顶部的“已保存的连接”直接点选即可重连并跳过登录框与目录选择；扫描到的设备若已有记录，同样一键连接，行内第二行显示上次挂载的目录。只有换服务器或换目录时才需要再走登录与选目录；升级前已有的媒体源在首次进入登录页时自动补录。
- 目录浏览器（WebDAV / SMB / 本地）以 `BrowseEntry`（名称 + 完整 URL + 是否目录）承载每一行，直接进入服务器返回的 URL，不再靠拼接目录名；WebDAV 自身条目按 URL（忽略结尾斜杠与百分号编码）过滤，SMB 走 jcifs 的容器 URL 并隐藏 `.` 开头与 `IPC$` / `ADMIN$` / `PRINT$` 等管理共享，路径行按编码还原显示中文目录名。
- WebDAV / 局域网探测的传输层是自己写的 `Http`（裸 Socket 的 HTTP/1.1，见 `local/Http.java`）：Android 的 `HttpURLConnection` 只允许 OPTIONS/GET/HEAD/POST/PUT/DELETE/TRACE/PATCH，`setRequestMethod("PROPFIND")` 会直接抛 `ProtocolException: Expected one of [...]`，所以 WebDAV 不能走它；同时 NAS 的自签证书也一并绕过（信任所有证书 + SNI），支持 Content-Length / 分块响应与 1xx 跳过，请求行按百分号编码所以中文目录名可用。播放地址仍交给 mpv 自己抓取，`OfflineCacheManager` 的下载只用到 GET/HEAD，继续走系统实现。
- 异常可见（`CrashLog`）：`runAsync` 与局域网扫描线程捕获 `Throwable`（不只是 `Exception`），未捕获异常由全局 `UncaughtExceptionHandler` 记进本机偏好，下次进入登录页会在顶部显示「上次异常退出：…」一行后清除；`SmbClient` 把 jcifs 抛出的任何错误（含 linkage error）翻译成带原因的 `IOException`，登录框直接显示原因而不是整个 App 静默退出；登录页还会用 `SmbClient.warmUp()` 预热一次 SMB 库，起不来就在状态行写「SMB 组件不可用：…」。
- SMB 媒体源（jcifs-ng 纯 Java 实现）：扫描共享目录并用 libmpv 直接播放原片；播放通过设备本地起的 HTTP Range 代理（`SmbStreamServer`，模式与 `RemoteIsoStreamServer` 一致）向 mpv 提供字节流，支持断点续播。SMB 播放依赖局域网连通性与 NTLM 认证（Synology/Samba 可用，host 作为 workgroup）。
- TV 遥控器焦点导航（`ui/FocusGrid`）：每个界面把自己控件声明成「行」，FocusGrid 负责写 nextFocus 链接、按网格移动焦点、把焦点滚进可视区；覆盖层用 `push`/`pop` 把网格压栈，关闭后必定回到打开它的那个控件，覆盖层套覆盖层也不会互相踩。首页用框架的 focusSearch，避免整面海报墙被网格化。
- 返回键只有一个入口（`closeTopmostLayer`）：播放器菜单 → 最上层覆盖层 → 播放器 → 首页搜索 / 排序展开行 → 详情页 → 登录页，`onKeyDown` 与 `onBackPressed` 共用它，不会出现两条链走歪或同一个弹层被关两次。
- 覆盖层只有一份实现（`ui/Overlays`）：遮罩、面板、标题、副标题、状态行、底部按钮行、焦点栈、关闭还原都由它负责；设置、媒体源、登录、目录浏览器、手机输入、文本输入六个弹层都走 `overlays.open(...)`，所以它们的外观、层级、返回行为必然一致。
- TMDB 自动刮削（`TmdbClient`，移植自 mac 版）：多关键词搜索评分、zh-CN 语言合并、限速；设置中填写自己的 API Key / v4 Read Access Token，无法直连 TMDB 时支持代理网关（带路径前缀的 Worker 反代地址，见根 README）。
- 刮削语言可在设置中切换（简体中文 zh-CN / English en-US），TMDB 接口与图片语言一并跟随。
- 首页顶部工具栏（D-pad 版）：搜索、排序（标题/添加时间/年份/评分）、升降序、媒体源、设置，一行五颗按钮（1.17 起去掉了「筛选」，类型与观看状态不再参与过滤）。工具栏下方是状态横幅，实时显示扫描与刮削进度与待刮削数量；「继续播放」是横向卡片行，与海报网格共用同一套焦点链。
- 媒体源管理覆盖层（`openSourcesOverlay`）：逐个媒体源启用/停用、重命名、移除，另提供添加媒体源、重新扫描并刮削、清空媒体库索引；面板为单列焦点链，返回键关闭并恢复首页焦点。
- 类型 / 导演 / 主演：随刮削结果一次请求取回（`append_to_response=credits`），存进 `movie`/`tvShow` 的 `genres`/`directors`/`casts` 列，详情页展示。
- 分集信息与分集剧照：详情页按季拉取 TMDB 分集详情（标题 / 简介 / 首播日期 / 剧照），剧照季映射沿用 mac 版回退规则（本季节 → 第 1 季 → 集数最多的季），仍无剧照时改查 `/images` 按投票加权择优；单集副标题支持手动覆盖，覆盖值优先于刮削结果。
- 详情页「编辑」可修改影片 / 剧集标题（保存后锁定名称并重新排队刮削），单集可写自定义分集副标题。
- 启动自动扫描 + 扫描后自动刮削（均可在设置中关闭），行为与 mac 版一致。
- 服务端接入模式（`OmniPlayApi`）：输入 OmniPlay Docker 地址 + 账号密码登录，海报墙走 `/api/library/items`，播放直连 `/api/playback/files/{id}/stream`（HTTP Range + 会话 Cookie），与 ARM mac / TV 端播放进度同步。
- libmpv 直接播放原片文件，带续播；外挂字幕通过 OmniPlay 服务端发现（sidecar），内嵌字幕由 mpv 自动选择。
- 播放按键：中键/回车切换暂停，左右快退/快进 10 秒，返回键退出播放；手机播放器提供触摸暂停与 ±10 秒按钮。
- 播放偏好（`TvSettings`）：默认音轨语言、默认字幕语言（自动 / 中文 / 英文 / 日文 / 关闭）、画质模式（流畅 / 均衡 / 画质优先，映射为 mpv 的 `demuxer-max-bytes`、`demuxer-max-back-bytes`、`demuxer-readahead-secs`）、详情页显示分集简介与进度、显示真实源文件路径。
- 手机辅助输入（`PhoneInputServer`）：遥控器打长串文本太慢时，TV 显示二维码与 `http://<TV-IP>:<随机端口>/p/<token>`，手机浏览器打开表单填写后 POST 回电视，直接写进对应输入框。详见下方“手机辅助输入”。
- 离线缓存（`OfflineCacheManager`）：1.15 起 TV 界面不再提供这个功能（电视本来就接在 NAS 上，盒子内没有可看的大容量空间），详情页与设置里的缓存入口、状态条里的缓存进度一并撤下。下载与容量预检的实现仍留在 `local/` 里，播放时也会继续优先用已缓存的副本，历史数据不会突然换成网络流。
- 4K DV/HDR 双播放模式：设置中可切换“字幕兼容播放模式”（色彩管理与字幕兼容性更好、负载更高）与“高性能直出播放模式”（仅针对 4K 或 DV/HDR 生效，其它格式仍走兼容模式；直出模式需要服务端字幕预缓存，否则可能无字幕）。4K 播放卡顿建议切高性能直出。
- 蓝光 ISO/BDMV：`RemoteIsoStreamServer` 在设备本地起 HTTP 分块流服务，配合 mpv 直接播放 ISO。
- 蓝光主片挑选（`BluRayStreams`，移植自 mac 版 `MediaNameParser.selectedBluRayStreamIndices`）：三种媒体源的 walker 都会走进 `BDMV/STREAM`，但一盘里的分卷不会整盘入库——每个「BDMV 上一级目录」为一组，按体积挑主片：最大的一条不小于第二名的 1.55 倍就只留它，否则按 0.72 的门槛把分卷合辑整组留下；体积未知时退回分卷号最小的那条。`BDMV` 下不在 `STREAM` 的文件（PLAYLIST / CLIPINF）直接丢弃，扫描报告用「蓝光非正片分卷跳过 N」说明少掉了多少条。
- PGS 图形字幕解析（`PgsSubtitleParser` / `PgsSubtitleView`）：解析 PGS segment 流并在播放器上叠加渲染。
- 服务端 HLS 兼容播放：Docker 模式下 MKV/蓝光等浏览器不支持的格式经服务端转 HLS 播放（TV 端解码与 HLS 兼容模式自动选择）。
- Lucky STUN 穿透客户端（`LuckyStunClient`）：登录 Lucky 管理端、拉取穿透规则、更新外网地址，用于通过 Lucky 穿透访问 OmniPlay 服务端。
- APK 自更新：`ApkUpdateProvider` 暴露本地 content:// APK 供“软件更新”入口安装。
- 播放进度规则：0.95 视为已播完，其余精确续播。

## libmpv 依赖

默认从 Gradle 依赖 `dev.jdtech.mpv:libmpv:0.5.1` 拉取 Android `libmpv.so` 及其 FFmpeg 运行时，覆盖 `arm64-v8a`、`armeabi-v7a`、`x86_64`（`arm64-v8a` 是绝大多数安卓 TV 盒子目标）。
JNI 桥接层 `libmpvbridge.so`（`app/src/main/cpp/mpv_bridge.cpp` + CMake）在构建时由 NDK 编译，动态加载 `libmpv.so`，向 mpv 传入 Surface、Cookie 头、字幕 URL、续播位置与直连流地址。
如需手动替换 libmpv 构建，按 `app/src/main/jniLibs/README.md` 的布局放入各 ABI 目录即可覆盖 AAR 中的版本。

## SMB 依赖

SMB 媒体源使用 `eu.agno3.jcifs:jcifs-ng:2.1.10`（纯 Java CIFS/SMB 客户端，LGPL），首次构建会从 Maven Central 拉取，并一并引入其传递依赖 `org.bouncycastle:bcprov-jdk18on`（NTLM 认证所需，Apache 2.0）。两者都打进 `classes.dex`，无需 NDK。若目标设备 SMB 服务器仅支持 Kerberos 或较新的 AES-128-CT 加密且需要额外 BC 算法，可在 `app/build.gradle` 中按需提供对应 BC 模块。

## 手机辅助输入

遥控器上的系统输入法打 NAS 密码、TMDB v4 Token 这类长串文本很不现实，App 内置一个只监听局域网的迷你 HTTP 服务（`local/PhoneInputServer.java`，与 `SmbStreamServer` 同一套进程内 ServerSocket 写法）代填文本：

- 入口：登录弹窗的“手机输入”、添加媒体源页的“手机输入”、设置页的“用手机输入 TMDB 配置”与“用手机输入缓存目录”、通用文本弹窗（重命名媒体源、改片名、分集副标题）里的“手机输入”。
- 交互：覆盖层显示二维码（`local/QrCode.java`，zxing 编码）与可手输的纯文本地址，底部只有“关闭”一个焦点项；手机提交后电视状态行显示“已填入 N 项（来自 <手机 IP>）”，覆盖层不关闭，可连续提交。
- 页面字段由覆盖层注册，只列出当前场景需要的输入；留空的字段不会被改写，避免误清空已填内容。单字段场景额外提供“补充内容”文本域，长串密钥可直接整段粘贴。
- 访问控制：路径带装机时生成、之后保持不变的 token；服务只接受 site-local（10/8、172.16/12、192.168/16）与回环地址来的请求；端口每次启动随机分配（`ServerSocket(0)`）；覆盖层关闭即清空会话，旧页面再提交只会得到“电视端已经离开这个输入页”。
- 手机与电视必须在同一局域网，且路由器不能开 AP 隔离；页面为明文 HTTP，密码在内网明文传输，与 WebDAV/SMB 本机直连的既有假设一致。

## 界面层（1.11 重构）

TV 界面以前是一个 8000 多行的 Activity 里手搓六套弹层、三套 D-pad 逻辑，弹层套弹层时焦点会丢、返回键会跳级、改个排序就整页重建（海报重新加载、滚动位置和焦点都要手工还原）。1.11 把这些收进 `ui/`：

- `ui/Theme.java`：唯一的配色、字号缩放、面板形状与焦点样式来源（对应 mac 版的 `ThemeManager`）。按钮 / 行 / 输入框 / 海报卡 / 播放器玻璃控件各有一种焦点样式，界面代码不再自己 `new GradientDrawable()`。
- `ui/Widgets.java`：唯一的控件工厂（对应 mac 版的 `MovieCardView`、按钮、分区标题等 SwiftUI 视图）：`button` / `toolbarButton` / `listRow` / `input` / `metaPill` / `badge` / `progressBar`，加上设置表单用的 `settingsCard` / `sectionCaption` / `addCardRow` / `cardButtonRow` / `switchRow` / `segment`，以及一套固定字号梯度（`TEXT_DISPLAY`…`TEXT_MICRO`）与控件高度（`HEIGHT_BUTTON` 等）。
- `ui/FocusGrid.java`：D-pad 导航的唯一所有者。网格按栈管理：底层界面的网格永远在栈底（界面在弹层打开期间重建网格也不会丢），弹层 `push`/`pop`，空行、重建后的控件、跑到网格外的焦点都有兜底。
- `ui/Overlays.java`：覆盖层的唯一实现，`open(Options)` 返回一个可以直接 `addView` 的面板，`closeTop()` 供返回键调用；面板自带状态行（写入才显示、清空就隐藏），底部按钮行用 `footer()`。`Options.scrollable=false` 用于自带滚动列表的固定高度面板（目录浏览器），避免两层 ScrollView 抢焦点。
- 设置面板改开关是就地改样式（`SwitchRow.setOn` / `Segment.setSelectedIndex`），面板不重建，滚动位置与焦点原地不动。
- 首页改为增量渲染：`buildHomeShell()` 只建一次工具栏 / 状态行 / 滚动容器，排序、筛选、升降序只重建 `homeControls`（选项行）或 `homeBody`（继续播放 + 海报墙），输入搜索词只重跑查询。滚动位置、焦点、已加载的海报都不会因为一次切换而丢掉。

## 主题与详情页（1.13）

`ui/Theme.java` 从「一套写死的浅色」改成 mac 版 `ThemeManager` 那种多变体调色板：

| id | 名称 | 强调色 | 底色 | 明暗 |
| --- | --- | --- | --- | --- |
| `appleLight` | 原生经典 | `#007AFF` | `#F2F2F7` | 浅 |
| `crystal` | 晶透白（默认） | `#007AFF` | `#FFFFFF` | 浅 |
| `linen` | 亚麻灰 | `#0D9488` | `#F9F9F9` | 浅 |
| `mint` | 薄荷清 | `#10B981` | `#F1FAF7` | 浅 |
| `rose` | 北欧粉 | `#D946EF` | `#FFF7FA` | 浅 |
| `midnight` | 午夜蓝 | `#4F8CFF` | `#0B1220` | 深 |
| `graphite` | 石墨黑 | `#2DD4BF` | `#16171A` | 深 |
| `oled` | 纯黑 | `#0A84FF` | `#000000` | 深 |

- 前五套的五个颜色值直接取自 mac 版，深色三套是为关灯观影另画的：面板色从底色抬起以免与背景糊在一起，强调色、评分色与危险色各提亮一档以保住对比。
- 边框、弱化文字、焦点色、焦点底色、危险色、评分色、玻璃控件、遮罩全部由所选变体推导（`Theme.apply()`），代码里没有第二处颜色字面量。
- 焦点样式统一成一条规则：**3dp 强调色描边 + 抬升阴影 + 1.045 倍微放大**，按钮 / 行 / 输入框 / 海报卡 / 播放器控件只是圆角和底色不同。3 米外看不清焦点是之前最大的问题。
- 选择入口在`设置` → `外观主题`，每项前有一个「面板色 + 强调色」小色块，当前项前加 `✓`；选完立即生效，写入 `TvSettings.themeId()`，`onCreate` 里 `Theme.apply()` 恢复。换肤走 `applyThemeChange()` 整屏重建，只刷列表会留下半新一半旧的界面。

详情页对齐 mac 版 `MovieDetailView`：海报底图在 Android 12+ 用 `RenderEffect` 实时虚化（更早的系统退化为同色柔和底）；右栏依次是片名、元信息行（年份、`TMDB ★`、`豆瓣 ★`、类型 / 制片地区）、`导演` / `主演` 标签行、简介（超过 96 字给展开 / 收起，无简介显示「暂无简介」）、播放按钮与 `已播 / 总时长` 进度条；下方是季切换、分集卡与电影的 `版本 / 文件（N）` 列表（文件名、相对路径、`已播时间 / 总时长 · 未播放 / 继续播放 / 已播放完`）。

同版本清理的界面问题：设置与媒体源面板改成真正贴边的通高侧滑栏；筛选行拆成「类型」「状态」两组并加分隔线（之前两个「全部」看不出不是同一个开关）；`重新扫描并刮削`、`清空媒体库索引` 改成整行按钮，标签不再换行；手机输入二维码放大到 320dp 并补了同网段提示；未刮削条目的展示名把 `The.Last.of.Us.2023` 这类场景命名整理成可读标题；`○` 改成「○ 未看 / ✓ 已看」；「继续播放」没有内容时整节不再占位；`CrashLog.summarize()` 附带抛出位置（详情页面那条 `NullPointerException` 就是靠它定位到 `ConcurrentHashMap.put` 收到 null 字幕状态）。

## 遥控器滚动、设置表单与刮削可见性（1.14）

三件「用起来不对劲」的事追到底是同一类问题：滚动位置的坐标算错、以及面板的行序与视觉顺序不一致。

- `FocusGrid.scrollToVisible()` 之前按 `view.getTop()` 判断焦点在不在可视区，而 `getTop()` 是相对**直接父容器**的：面板里凡是埋在行容器里的按钮（主题胶囊、分段控件、卡片里的动作行），坐标都是几十而不是几百，于是被判定成「已经在视口上方」并往回滚——这就是按方向键「有概率回弹、滑不下去」的直接原因。现在改成逐层累加 `getTop()` 得到相对滚动容器的位置。
- ScrollView 的子 View 只在**下一次布局**之后才把自己的 `getTop()` 改成含滚动量的值，所以单纯累加在「滚动后未重新布局」和「滚动后重新布局」两种时刻会差一个滚动量，表现为第二次按键滚了两倍距离。`viewportTop()` 用内容视图的布局位移加当前 `scrollY` 做归一化，两种时刻都正确。
- 连续按键时不再用 `smoothScrollTo`：动画会被下一次按键打断，观感就是页面在来回弹。遥控器场景要的是即时定位。
- 面板的 D-pad 行序不再手写。`FocusGrid.rowsFrom(panel)` 从面板自己的视图树按真实几何分组（同行 = 垂直区间以行首中线为锚重叠，锚点不随合并长大），布局完成后再声明一次；之前 `renderSettingsOverlay()` 末尾那份手工 `rows` 列表与视觉顺序差了好几行，按 DOWN 会在面板里上跳下跳。设置、媒体源两个面板都改用它。
- 弹层被返回键关闭时不会再留下失效字段：`Overlay.onClose` 回调让 `settingsOverlay` / `sourcesOverlay` / `loginOverlay` / `browserOverlay` / `phoneInputOverlay` / `textInputOverlay` 一起清空。之前用 BACK 关掉设置后，`openSettingsOverlay()` 因为字段非空直接返回，「设置」按钮看起来就点不动了。
- 焦点丢失时不再一律跳回第一行：`FocusGrid` 记住上次所在的行列，控件重建后从原位置继续。海报墙重建时用卡片上记的条目 id 找回焦点（`restorePosterFocus`），刷新页面不会把用户甩回顶部。

设置面板重排成 mac 那种分组表单：`媒体源` / `TMDB 刮削` / `外观主题` / `刮削与播放偏好` / `基础设置` / `离线缓存` / `4K DV/HDR 播放模式` / `关于与更新` 八个卡片，卡片上方是小标题，说明文字在卡片下方。布尔项换成真开关（`Widgets.SwitchRow`，右侧画一个可切换的滑钮），互斥项换成分段控件（`Widgets.Segment`：刮削语言、音频轨道、字幕轨道、网络缓冲、4K 播放模式），点一下只改样式和偏好，不重建设置面板。卡片里独立成行的按钮固定 48dp 高（之前用 `wrap_content`，被压成 27px，遥控器上几乎按不中）；版本号与开源许可证从面板顶部挪到最后的「关于与更新」里。

刮削过程中海报边刮边出：`startStartupSync()` 与设置里的「刮削海报」进度回调都会调 `scheduleScrapePosterRefresh()`（1.2 秒限流），条目集合没变就只把新数据写回已有卡片（`bindPosterCard`：海报、片名、年份、评分角标、观看状态、进度条），条目增减了才整面重建并补回滚动位置与焦点。之前只在刮削全部结束后刷一次，所以状态条写着「正在刮削 3/12」而海报要等最后才一起出现。

## 设置精简与蓝光主片扫描（1.15）

- 设置里的「媒体源」卡片删掉了。工具条上本来就有「媒体源」按钮，那个弹层里有启用 / 重命名 / 移除 / 添加 / 重新扫描并刮削 / 清空索引，设置里再放一份「添加媒体源」「重新扫描」「刮削海报」「管理媒体源」只是多一层入口，还让人怀疑两个「媒体源」是不是两回事。扫描入口现在只在媒体源管理里，「重新扫描并刮削」的进度回调也接上了 `scheduleScrapePosterRefresh()`，海报仍然是边刮边上墙。
- TMDB 两块输入改成标题在上、输入框在下（`labelledRow()`），输入框文字左对齐。遥控器调出来的软键盘里，居中的长 URL 首尾都会被吃掉一截，看不出打成什么样了。
- 开关现在把状态写在滑块里：关是凹下去的灰底 + 滑块靠左 + 「关」，开是主色底 + 滑块靠右 + 「开」。之前只靠一个浅色轨道的深浅区分，用户在电视前根本看不出这一项到底勾没勾。
- 离线缓存整块从 TV 界面撤下（设置卡片、详情页的缓存本片 / 本季 / 本集、状态条的缓存进度），原因见「功能范围」。

蓝光盘被刮出一堆条目，根因不在解析器，在扫描阶段：

- mac 版走完整棵树，把 `…/BDMV/STREAM/…` 按盘分组，只在组内挑主片；BDMV 里不在 STREAM 的文件丢弃，非 BDMV 文件照常收录。
- TV 版此前三种媒体源的 walker 各自把 `BDMV` / `STREAM` / `disc N` 目录剪掉了。本地和 WebDAV 是「一盘都不收」，SMB 是「全盘收」：jcifs 返回的目录名自带结尾 `/`，`lower.equals("bdmv")` 匹配不上 `BDMV/`，于是直接走进 STREAM，把每条 m2ts 都登记成文件；花絮、宣传片、分卷全都进库，首页就爆了。
- 现在剪目录的规则收到只剩系统垃圾（`lost+found`、`@eadir`、`.nomedia`、`.` / `_` 开头），SMB 的条目名统一去掉结尾 `/`，三个 sink 都带上文件体积（本地 `file.length()`、SMB `entry.length()`、WebDAV 解析 `getcontentlength`），入库前由 `ScanCollector` 分流：普通文件直接登记，光盘分卷先按盘攒着，走完这个源再挑主片。被跳过的分卷不进 `currentFileIds`，所以上一版留下的「每条 m2ts 一个条目」会在同一次扫描的失效清理里自动消失。

规则本身放在 `local/BluRayStreams.java`（不依赖任何 Android 类），配套加了 `/tmp/pitest/run-bluray.sh`（`BLURAY_OK`）：分组键、BDMV 与 STREAM 判定、1.55 独苗、0.72 分卷合辑、体积未知退回分卷号，都在桌面上跑。真机侧在模拟器里造了两张盘（一张 4G 正片 + 400M + 200M 花絮，一张 20G + 19G 分卷），扫完报告是 `文件 8，蓝光非正片分卷跳过 2`：五条 m2ts 只生成两个条目，两张盘各自一条 / 一组。

## TMDB 反代路径、凭证与 WebDAV 目录名（1.16）

反代网关和 API Key 都填对了却报「TMDB 无法访问」，是三个问题叠在一起：

- 连接检测发的是相对地址。`get("/configuration")` 在直连模式下会拼上默认域名，走反代时 `apiBase()` 返回的是空串，`new URL("/configuration")` 直接抛 MalformedURLException，异常被吞掉后统一显示成「无法访问」，跟钥匙对不对其实没关系。
- 反代地址没补 TMDB 的版本段。`https://…/tmdb0512` 只是网关根，TMDB 的接口在 `/3/…` 底下，中间少一段。用 curl 各打一次就能分开：`…/tmdb0512/configuration` 返回 nginx 的 404（网关压根没匹配上路由），`…/tmdb0512/3/configuration` 才是 TMDB 自己回的 `401 status_code:7`。现在 `TmdbClient.normalizeProxyBase()` 会把地址补到 `/3`，路径里已经带 `/3` 就不重复补，六处拼 URL 的地方统一走它。
- 走反代时凭证整个不发。原先六处都是 `isProxy() ? "" : "&api_key=…"`，等于假定「网关会替你加钥匙」。这台网关只做转发，请求到 TMDB 时是裸的；v4 Read Access Token 要的 `Authorization: Bearer …` 也从没发出去过。现在密钥一律跟着走：长度超过 50 位判定为 v4 Token，改用 Bearer 头；其余当 v3 Key 拼 `api_key=`，两种 TMDB API 都能直接粘贴使用。

连接检测也不再是一句话兜底：`checkConnectionDetailed()` 按 空 Key / 2xx / 401、403 / 404 / 连不上 / 其它状态码 分别给结论，设置面板显示的就是这句原文。模拟器上填这台反代加一把故意写错的 32 位 Key，状态行是「TMDB 拒绝了这把钥匙（HTTP 401）。反代只负责转发，API Key 仍要自己填；v4 Token 会以 Bearer 头发出。」路径已经打通，剩下的是钥匙本身。

反向代理输入框左边那个网络图标去掉了（`inputWithLeadingIcon` 一并删除），`Widgets.input()` 的文本统一左对齐，长 URL 不再被图标挤掉开头。

WebDAV 地址里显示成 `%E4%B8%AD%E6%96%87` 这种一串符号的目录名，是 URI 百分号编码没还原：服务器 PROPFIND 回来的 `<href>` 和保存下来的挂载路径都是编码过的。新增 `local/UrlLabels.displayPath()`，只对 `?` 之前的路径部分做按字节的 UTF-8 百分号解码（`+` 保持不动，非法的 `%zz` 原样留着，没有 `%` 就直接返回），登录框、已保存连接、局域网设备行、媒体源标题都改走它。它是个不依赖 Android 的纯函数类，因为 `SavedConnections` 不能去引用 `WebDavClient`——后者的编译单子里没有 kxml。

新增 `/tmp/pitest/run-tmdb.sh`（`TMDB_OK`）：起一个本地假 TMDB，断言 v3 Key 发出的请求路径是 `/tmdb0512/3/configuration?api_key=…`、v4 Token 带 `Authorization: Bearer` 且不带 `api_key`，再核对 404 / 401 / 空 Key 三种提示；`run-webdav.sh` 补了四条 `displayPath` 断言。第 3 条目前只有桌面级证据：模拟器上没挂中文目录名的 WebDAV，暂时截不到图。

## 遥控器手感、移除媒体源与筛选退场（1.17）

遥控器移动卡顿的实测结论：48 张海报的墙上走一圈，`dumpsys gfxinfo com.omniplay.tv` 是 402 帧 / 70 帧超标（17.41%）、68 次 `Slow issue draw commands`。三处成因——每次焦点变化新建一张 `GradientDrawable`（拿焦点、丢焦点各建一次，连带重算轮廓与阴影）、海报卡片叠了两层阴影（卡片一层、海报框一层）、缩放动画期间海报位图与两行标题每帧重新光栅化。`Theme` 现在把静止态与焦点态两张 drawable 一次画好缓存起来，`swapOnFocus()` 只换引用；海报卡只留卡片一层阴影（`elevation` 0dp→8dp，海报框恒为 0）；缩放动画期间挂 `LAYER_TYPE_HARDWARE`、结束摘掉；分集缩略图不再自带一套焦点样式，改与海报卡共用同一份缓存。同一套走位重测两遍是 412 帧 / 38 帧超标（9.22%）与 410 / 39（9.51%），`Slow UI thread` 归零，`Slow issue draw commands` 68→39。剩下的 9.5% 有地板：海报墙是不复用的 `GridLayout`（`Max resource usage: 99.53MB (x48)`），要再降得换 RecyclerView。

移除媒体源现在会连首页卡片一起清。`LocalEngine.removeSource()` 原先只改写媒体源列表，实体、文件、刮削队列都还留在 SQLite 里，首页照旧渲染那批海报。新增 `LocalMediaLibrary.purgeSource(sourceId)`：按 `local-<sourceId>:` 前缀挑出该源的文件（`LocalEngine.fileId` 的构造规则），删 `videoFile` / `offlineCache` 行与内存里的播放进度覆盖，再走新抽的 `pruneOrphanEntities()`——删掉没有任何文件的 `movie` / `tvShow`、这些剧的 `tvEpisode`、`sourceFileId` 指向已消失文件的 `scrapeQueue` 行。同一套剪枝也接到 `removeMissingFiles()` 后面，磁盘上删了影片重新扫描，卡片与空壳条目一起消失，与 mac 版一致。模拟器实测：本地源索引出 48 部时点「移除」，面板与首页同时变成「0 部」/「媒体库为空」，`待刮削 48 个条目` 一并清零。

筛选整块退场：右上角「筛选」按钮、类型与观看状态两组选项、`filteredItems()` 全删，首页名单只剩「按搜索词过滤 + 按当前排序排」。工具栏焦点行是 搜索 / 排序 / 降序 / 媒体源 / 设置，返回键少一个分支，计数文案用整个媒体库的条目数。

弹层滚动跟的是中线而不是边缘。原先 `scrollToVisible()` 判断「控件是否离开视口」，系统自己的 `requestChildRectangleOnScreen` 又把新获焦的行往视口边缘贴，两套逻辑打架：往上走时高亮在走、页面不动（约 8 次方向键的死区），而系统只会把第一个可聚焦控件对齐上边缘，标题与状态行永远回不来。`followVertically()` 现在只按布局坐标算落点：控件高于视口就露出顶部，否则中线对中线；第一行直接归零、最后一行到底（`scrollToEdge`）；行序仍由 `rowsFrom()` 按布局推导。设置面板的初始焦点从「主题第一颗色卡」换成 TMDB 卡片底部的「保存配置」——面板从顶部展开、焦点框一开始就可见，也不会顺手弹起软键盘。

「选择媒体目录」浏览器的操作行（选择此目录 / 返回上级 / 取消）从焦点顺序末尾提到第一行：影片目录动辄几十个子目录，原先要按几十次 DOWN 才够得着确认键；现在打开就在「选择此目录」上，往下进目录，往回一下回到按钮，进入子目录后焦点也重新落回它（SMB 共享根目录时它是禁用的，退回第一个目录行）。

## 模拟器与真机验证（1.12）

Apple Silicon 上用官方 Android TV 镜像即可，arm64 镜像原生跑，不用转译：

```sh
SDK="$PWD/.android-sdk"; export JAVA_HOME=/tmp/jdk17b/Contents/Home
"$SDK/cmdline-tools/latest/bin/sdkmanager" --sdk_root="$SDK" \
  "emulator" "platform-tools" "system-images;android-34;android-tv;arm64-v8a"
printf 'no\n' | "$SDK/cmdline-tools/latest/bin/avdmanager" create avd \
  -n omni_tv1080 -k "system-images;android-34;android-tv;arm64-v8a" -d tv_1080p --force
ANDROID_SDK_ROOT="$SDK" ANDROID_AVD_HOME="$PWD/.avd" \
  "$SDK/emulator/emulator" -avd omni_tv1080 -gpu host -no-snapshot -no-audio -no-boot-anim
```

- `tv_1080p` 皮肤默认 320dpi，真机 1080p 是 213dpi，界面会大一号；`adb shell wm density 213` 拉回真机比例再看。
- `ANDROID_AVD_HOME` 指到项目内的 `.avd`，镜像和虚拟盘都不落到家目录。
- 模拟器只能验证遥控器操作与本机媒体库：NAT 里看不到真实 NAS（宿主是 `10.0.2.2`），4K / HDR / DV 的硬解也必须回真机试。
- 方向键用 `adb shell input keyevent 19/20/21/22/4/66` 模拟，界面看 `uiautomator dump`，截图用 `adb exec-out screencap -p`。

1.12 是在模拟器上跑出来的第一轮，修的都是「没设备就看不见」的问题：

| 症状 | 成因 |
| --- | --- |
| 本地目录扫完报 `Syntax error in regexp pattern`，整库扫挂 | `LocalMediaNameParser` 里 `(?!…)\b` 给断言加了量词，Android 的正则是 ICU 实现，不接受；OpenJDK 能编译，所以 JVM 用例查不出来 |
| `待刮削 N 个条目` 每扫一次翻一倍 | `enqueueScrape` 是裸 INSERT，同一实体重复入队，TMDB 也重复查 |
| 设置 / 媒体源弹层刚打开时没有焦点框 | ScrollView 被当成控件收进了焦点网格，系统兜底把焦点给了它，而容器不画焦点圈 |
| 本机影片点播放报 `播放失败：loading failed (-13)` | 交给 mpv 的是相对路径，mpv 拿它去 `/` 下面找文件 |
| 反代和 Key 都填对了，检测却说「TMDB 无法访问」 | 1.16 前有三处：检测发的是相对地址、反代没补 `/3`、走反代时凭证不发。都已修，现在检测会说是 401 还是 404 还是连不上 |

配套加了 `/tmp/pitest/run-regex.sh`（`REGEX_OK`），静态查「量词紧跟在断言后面」这类只有 Android 会炸的写法。

JVM 侧一共八个用例：`run-webdav.sh`（`WEBDAV_OK`）、`run-smb.sh`（`SMB_OK`）、`run-saved.sh`、`run-phone.sh`、`run-regex.sh`（`REGEX_OK`）、`run-bluray.sh`（`BLURAY_OK`）、`run-tmdb.sh`（`TMDB_OK`）与 `/tmp/lanprobe/run.sh`（`ALL_OK`）。它们都只编 `local/` + `data/`，所以 `local/` 不能反向依赖 `ui/`。

## 目录结构

```text
OmniPlay-安卓TV/
  app/src/main/java/com/omniplay/tv/
    LauncherActivity.java        TV / 手机入口分发
    MainActivity.java            TV 主界面（海报墙、详情、设置、播放器）
    ui/                          界面层：Theme（配色/字号/焦点样式）、Widgets（按钮/标签/输入框）、
                                 FocusGrid（D-pad 网格与网格栈）、Overlays（覆盖层栈）、ImageLoader（海报加载）
    PhoneMainActivity.java       手机主界面（触摸海报网格、横屏播放器）
    ApkUpdateProvider.java       APK 自更新 ContentProvider
    player/                      MpvBridge / MpvPlayer / MpvVideoView
    data/                        OmniPlayApi（服务端）/ LocalPlayApi（独立版）/ LuckyStunClient
    local/                       独立版媒体引擎：扫描、WebDAV、SMB（jcifs-ng）、TMDB、文件名解析、局域网发现
                                 Http（裸 Socket HTTP/1.1，PROPFIND 用）、CrashLog（异常记录与回显）
                                 BrowseEntry（目录浏览器行）、SavedConnections（免密重连）
                                 BluRayStreams（蓝光分卷挑主片，纯逻辑可在桌面 JVM 上测）
                                 UrlLabels（URL 路径百分号解码，列表与登录框显示用）
                                 TvSettings（偏好设置）、LocalMediaLibrary（SQLite，v2）、OfflineCacheManager（实现保留，TV 界面已撤下）
                                 PhoneInputServer / QrCode（手机辅助输入）
    iso/                         本地 ISO 分块流服务
    subtitle/                    PGS 图形字幕解析与渲染
  app/src/main/cpp/              mpv_bridge.cpp + CMakeLists.txt
  keystore/                      release 签名文件（gitignore，本地保留）
  local.properties               sdk.dir 与 release 签名配置（gitignore）
  gradlew / gradle/              Gradle 8.14 wrapper
```

## 在 ARM Mac 上打包

### 环境要求

- JDK 17–23（AGP 8.7.3 + Gradle 8.14 要求）；Homebrew `openjdk@17` 或 Android Studio 自带 JBR 均可，通过 `JAVA_HOME` 指定。JDK 24+ 未验证，JDK 26 会构建失败（见常见问题）。
- Android SDK（可用 Android Studio 安装，或使用 Command-line Tools 自行安装）：
  - Platform `android-35`（`compileSdk 35`）
  - Build-Tools（仓库内为 35.0.0，34.0.0 亦可）
  - NDK `27.3.13750724`
  - CMake `3.22.1`
  - platform-tools（仅调试安装时需要）
- 可联网环境：首次构建需从 google() / mavenCentral() 下载 AGP、Gradle 8.14 发行版与 `dev.jdtech.mpv:libmpv:0.5.1` AAR。
- 二维码依赖 `com.google.zxing:core:3.5.3`（Apache 2.0，纯 Java，无 NDK）；`minifyEnabled false`，整体打进 APK，约增加 220 KB。

仓库已包含一套可直接使用的本地工具链（未提交 Git）：

```text
.android-sdk/          # 本地 Android SDK（platforms/android-35、ndk/27.3.13750724、build-tools、cmake）
.gradle/               # 本地 Gradle 用户目录（缓存与 wrapper 发行版）
```

`local.properties`（未提交 Git）已指向该 SDK：

```properties
sdk.dir=<仓库目录>/OmniPlay-安卓TV/.android-sdk
release.storeFile=keystore/omniplay-tv-release.jks
release.storePassword=omniplaytv16
release.keyAlias=omniplay-tv
release.keyPassword=omniplaytv16
```

如果你没有 `.android-sdk/`，用 SDK Command-line Tools 装齐上面组件后，把 `sdk.dir` 指到你自己的 SDK 路径即可；签名文件缺失时先创建 keystore 并同步修改 `local.properties`。

### 构建命令

ARM Mac 与 x86 Mac 上命令一致，NDK 与 Gradle 均在本机原生运行，产物同时包含 `arm64-v8a` / `armeabi-v7a` / `x86_64` 三个 ABI。

```sh
cd OmniPlay-安卓TV

# release 签名 APK（keystore 与密码来自 local.properties）
JAVA_HOME="$(/usr/libexec/java_home -v 17)" \
GRADLE_USER_HOME="$PWD/.gradle" \
./gradlew assembleRelease
```

产物：

```text
app/build/outputs/apk/release/app-release.apk
```

调试版（无需签名）：

```sh
JAVA_HOME="$(/usr/libexec/java_home -v 17)" \
GRADLE_USER_HOME="$PWD/.gradle" \
./gradlew assembleDebug
# app/build/outputs/apk/debug/app-debug.apk
```

发布到 Releases / 设备前的建议命名：

```sh
cp app/build/outputs/apk/release/app-release.apk dist/OmniPlay-TV-v1.17-arm64.apk
```

### 安装到设备

```sh
# TV / 手机（USB 或 ADB over Wi-Fi 均可）
ANDROID_SDK_ROOT="$PWD/.android-sdk" "$ANDROID_SDK_ROOT/platform-tools/adb" install -r app/build/outputs/apk/release/app-release.apk
```

### 常见问题

- `JAVA_HOME` 不满足 JDK 17–23 时 AGP 会直接报 JDK 版本错误；`/usr/libexec/java_home -V` 可列出本机可用 JDK。
- 首次构建卡在 Gradle 发行版或依赖下载，属于网络问题，确认可访问 `services.gradle.org`、`dl.google.com` 与 `repo1.maven.org` 后重试。
- 修改 `versionCode` 后记得同步 `app/build.gradle`；TV 端“软件更新”入口按 versionCode 判断是否有新版本。
- 构建别再带 `--offline`：`com.google.zxing:core` 不在本机 Gradle 缓存里时，离线模式会在 `checkReleaseAarMetadata` 直接失败，联网跑一次补上缓存即可。
- 若 Gradle 报 `FileLockContentionHandler ... Operation not permitted`，通常是沙箱/防火墙限制了本地回环端口，换普通 shell 或在允许网络的环境下重试。
- 手机打不开“手机输入”页面：先确认手机与电视连的是同一个 SSID / 同一网段；路由器开了 AP 隔离（客户隔离）时客户端互相不可见，需要在路由器上关闭；电视处于多网卡或访客网络时，`pageUrl()` 取到的可能不是手机所在网段的地址，可按页面提示改用手输地址。
- 用 JDK 24+ 构建时 Gradle 8.14 会在 Groovy 脚本编译阶段报 `Unsupported class file major version 70`（JDK 26），或在 `core-for-system-modules.jar` 的 `JdkImageTransform` / `jlink` 步骤失败；换 JDK 17–23 即可，本机可用独立解压的 Temurin 17 目录，macOS 上 `JAVA_HOME` 要指到 `.../Contents/Home`。
- 从 versionCode 18 及更早版本升级时，SQLite 会跑 v1→v2 迁移：给 `movie`/`tvShow` 补 `tmdb_id`/`genres`/`directors`/`casts`，给 `videoFile` 补 `episode_subtitle`，新建 `tvEpisode` 与 `offlineCache` 表；迁移只加列建表，不动已有数据。
- 局域网扫不到 WebDAV：判定规则是 OPTIONS 返回 `DAV` 头、或 PROPFIND 返回 207、或专用端口（5005 / 5006 / 8443 / 8010 / 8011 / 8000 / 5244 等）上吃到 401 / 403（OPTIONS 与 PROPFIND 任一即可）；80 / 443 / 8080 这类共享端口只认 `DAV` 头或真 207，避免把带登录的普通 Web 管理页当成 WebDAV。服务挂在其它端口时（例如 CloudDrive 的 19798），按页面提示手动填写完整地址后登录即可；扫描总时长上限 45 秒，NAS 唤醒或响应慢时可能来不及，重扫一次通常就有。
- WebDAV 登录报 `Expected one of [OPTIONS, GET, HEAD, POST, PUT, DELETE, TRACE, PATCH]`：那是 Android `HttpURLConnection.setRequestMethod` 抛的 `ProtocolException`，说明该设备不支持非标准方法；1.10 起 WebDAV 走自带的 `Http` 客户端，不应再出现，如果仍然出现请确认安装的是 v1.10 或更新（设置页第一行有版本号，也可看 APK 文件名）。
- 点 SMB 后 App 直接退出：v1.10 起不会再静默退出，登录框或登录页会给出原因（例如 `NoClassDefFoundError：jcifs.xxx` 或 `SmbAuthException：Login failure`）。若显示的是「上次异常退出：…」，那行文字就是崩溃原因，按它排查即可。
- WebDAV 目录名显示成 `%E4%B8%AD%E6%96%87…`：1.16 起列表、登录框、已保存连接都会把路径部分解回中文名。若仍是编码串，说明那串不是百分号编码（服务器返回的就是字面量），把地址原文发回来即可判断。
- 移除媒体源后首页还剩一堆卡片：1.17 起 `removeSource` 会连带清该源的文件、实体、剧集、刮削队列与缓存记录。若装机版本更旧，用媒体源面板的「清空媒体库索引」也能清干净。注意别在首次扫描进行中移除媒体源——那一轮扫描仍持有该源，可能把文件重新登记回去，等扫描结束再移除即可。
- 同一台服务器换过密码后连不上：登录框会回填保存过的账号密码，改完密码点“登录”即会覆盖旧记录，已挂载目录保留。

### 遥控器操作上遇到的问题先看这里

- 焦点跑到看不见的位置 / 方向键像没反应：1.11 起每个弹层都会把焦点收进自己内部（`rescueFocusIntoTopPanel`），再出现这种情况请记录「在哪个界面、按了哪个键、焦点从哪到哪」，网格声明缺控件是唯一的成因。
- 方向键往下滑却回弹：1.14 起面板行序由 `FocusGrid.rowsFrom()` 按布局推导、滚动跟随用布局位移归一化，两者都不再依赖手工行序；如果还能复现，请记录「在哪个面板、焦点在哪个控件、按了几次 DOWN」，那说明该面板有控件没被 `rowsFrom` 收到（只有 `isFocusable=false` 或 `View.GONE` 的控件会被跳过）。
- 原盘 / 蓝光目录扫出来一堆条目，或者反过来一个条目都没有：先看媒体源面板的扫描报告里有没有「蓝光非正片分卷跳过 N」。没有这一句说明 walker 没走进 `BDMV/STREAM`（多半是路径里又出现了被当成垃圾剪掉的目录名）；有这一句但数字不对，就是体积没读到（服务器不给 `getcontentlength`、SMB 拒绝 `entry.length()`），此时按分卷号最小的那条兜底。
- 弹层往下走再往上走滑不回顶部：1.17 起滚动跟随只按布局坐标计算中线落点，首行归零、末行到底。若还能复现，请记录「在哪个面板、焦点在哪个控件、按了几次 UP」，那说明该面板有控件没被 `rowsFrom()` 收到。
- 遥控器走动时画面明显掉帧：先说清「在哪个界面、往哪个方向、连续按还是单按」，并附上海报数量（首页计数文案里有）。1.17 已把每次焦点变化新建背景改成换引用，剩下的地板主要来自不复用的海报网格。
- BACK 关掉弹层后原按钮点不动：每个 `Overlay` 都带 `onClose` 回调清宿主字段，若再出现请确认那个弹层是否漏了 `onClose`，或宿主字段被别处提前赋值过。
- 设置页改完一个开关后焦点跑掉：应停在被改的那一项上（按标签找回），若仍跳回顶部说明该行文字随状态变了，报上来补匹配规则。
- 弹层套弹层（登录框里点“手机输入”，或设置里点“用手机输入 TMDB 配置”）关闭后应回到打开它的那个按钮；回到顶部或回到屏幕外都是回归。
- 刚打开某个界面却看不到焦点框：那是焦点落到了 ScrollView 上。布局用的滚动容器一律 `setFocusable(false)`，控件一律由 `FocusGrid.grab` 上焦（首次 `requestFocus()` 会被还没布局的视图拒掉，`grab` 会在布局后补一次）。
- 弹层打开后应停在第一个能操作的控件上（`媒体源` 停在「停用」，`设置` 停在「添加媒体源」），停在标题文字或分区名上都是回归。
- 换主题后如果标题、工具栏或海报占位还是旧配色：换肤必须走 `applyThemeChange()` 整屏重建，只重画列表不算换肤。
- 详情页出现 `NullPointerException（putVal:…）` 之类的浮动提示：那是被捕获的主线程回调异常，`CrashLog.summarize()` 已带上抛出位置，完整栈在 `/data/data/com.omniplay.tv/files/omniplay-tv-crash.txt`。
