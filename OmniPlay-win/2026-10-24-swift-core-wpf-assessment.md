# 「Swift 核心 + WPF」移植觅影：还原度与工作量评估（2026-10-24）

对照项目：`/Users/nan/Downloads/swift/box-send`（Windows 版方案见其 `PORTING-WINDOWS.md`）。

## 0. 结论

1. **方案成立，而且是目前评估过的几条里风险最低的一条。** WPF 是成熟框架，不存在「SwiftUI on Windows 能不能用」这种赌局；桥接形态（动态库 + C ABI + JSON 契约 + 平台钩子注入）box-send 已经完整跑通并量产。
2. **还原度判定：除了两处，其余可以 1:1。** 两处是：播放页「视频画面上叠控制条」受 WPF airspace 限制必须换实现方式（可以做出同样观感，路径不同）；macOS 26 那档真折射玻璃在 Windows 上只能做近似。
3. 真正的差距不在语言也不在桥，而在四件 box-send 从没碰过的事：**SQLite 持久化、libmpv 视频宿主、海报墙虚拟化与图像管线、跨设备同步**。这四项决定了排期与风险，其余都是可估的界面工作量。
4. 建议先花 **5 天做两个 spike**（GRDB on Windows、mpv 宿主 + 覆盖层），两个都过就正式定案开工；任一不通，退路是「宿主持库」或「宿主继续持播放器」，核心照样共享。

## 1. box-send 里可以直接搬过来的东西

- **桥接形态**：`boxsend_create / invoke / free / version / destroy / last_error`，请求 `{method, params}`、响应 `{ok, result|error}`；觅影照搬，只换方法名与载荷。
- **Windows 链接坑已经踩过**：Swift 的 `@_cdecl` 只保证符号名不保证进 COFF 导出表，得用 `-Xlinker /EXPORT:` 显式列名单，打包前解析导出表核对；核心库文件名要带 `-core` 后缀避免和托管程序集同名；Winsock 要显式链 `ws2_32`。觅影的 `omniplay-core` 直接沿用这套写法与核对步骤。
- **平台钩子注入**（`jpegReencode` / `ocr` / `unzip` / `ssh exec` 由宿主注入）：觅影正好用同一个模式承接 Keychain→凭据管理器、图像解码、SMB 共享枚举、mpv 窗口句柄与消息泵。
- **主题共享**：`ThemeCatalog` 放在核心里，两套界面共用色值。觅影的 `ThemeManager` 里那套色板、渐变（左浅右深/上浅下深）、玻璃染色与描边定义要照此抽成共享目录，这是还原度的地基。
- **C# 侧 `abi-probe`**：用一个控制台程序加载真库跑一遍开机路径（version → snapshot → 建组 → 加站 → 事件流 → 错误传递）。觅影对应做一份：启动 → 建源 → 扫描 → 库查询 → 详情 → 播放器状态 → 错误传递，作为 CI 冒烟。
- **构建与打包**：Windows 上原生 `swift build`（导入 VS 开发者环境），不做交叉编译；Inno Setup 安装器；`DynamicResource` 全量主题化的控件模板写法。

## 2. 觅影与 box-send 的结构性差异

| 维度 | box-send | 觅影 | 影响 |
| --- | --- | --- | --- |
| 核心依赖 | 只用 Foundation，配置写 JSON | 全库依赖 GRDB + SQLite | **新增最大风险**，见 §5 |
| 界面规模 | 6 个页签，XAML 8 + C# 21 文件共 5,172 行 | 首页海报墙、详情页、播放页、设置、8 类弹窗，估 9~14k 行 | 工作量主体，但风险低 |
| 媒体播放 | 无 | libmpv + 蓝光 `bd://` + ISO 代理 + WebDAV/Plex/Emby/Jellyfin + SMB | 视频表面、airspace、全屏/DPI |
| 图像 | 一张背景图 | 325+ 张海报与剧照的解码、缓存、自愈 | 虚拟化与降采样（WPF 有现成能力） |
| 状态流 | 一份全局快照 + 轮询 + `Refreshed` 事件 | 扫描/刮削/缓存/缩略图进度 + 播放 4Hz 位置 | 契约必须改，见 §3 |
| 跨设备同步 | 无 | mac 走 iCloud 容器目录 | Windows 无对应，改走 OmniPlay-docker 服务端 |
| 测试资产 | 445 个 Swift 用例 + abi-probe | mac 80 个用例；win C# 12,090 行测试 | 领域用例可直接翻成 Swift 用例 |

## 3. 契约设计：快照轮询不能照抄

觅影的库列表比 box-send 的站点列表大两个数量级，且进度事件密集。三点必须改：

1. **列表查询化**：`library.query(filter, sort, offset, limit)`、`category.groups(mode)`、`movie.detail(id)`、`continueWatching.list()`，而不是一份含全部影片的 `snapshot`。325 部片子全量 JSON 约 200KB，每 250ms 轮一次纯烧 CPU。
2. **进度走推送回调**：新增 `omniplay_set_event_callback`，核心在扫描阶段、刮削计数、缓存进度、缩略图队列、播放器状态（约 4Hz）上主动推；C# 侧 `Dispatcher` 收。`events(since:)` 保留做补偿与断线重放。
3. **二进制不进 JSON**：海报/剧照传文件路径 + 版本号（或内容哈希），界面自己解码；播放进度、音轨字幕列表等结构化数据才走 JSON。

方法族规模（初版估 90~120 个，box-send 约 50）：媒体源 CRUD/检测/启用、扫描与刮削控制、库查询与搜索、分类与人物档案、详情与多版本、记录/评分/评论/已看未看、NeoDB 登录与同步、TMDB 与代理网关、播放器控制与状态、离线缓存、缩略图、设置读写、诊断日志。

## 4. UI 还原度逐项判定

| 界面元素 | mac 做法 | WPF 做法 | 还原度 |
| --- | --- | --- | --- |
| 首页网格、海报卡、年份行、已看/未看胶囊 | `LazyVGrid` + 固定 160×240 | `ListBox` + 虚拟化 `WrapPanel`（自研或社区 `VirtualizingWrapPanel`） | 1:1 |
| 继续播放横向滚动 | 横向 `ScrollView` | `ScrollViewer` 横向 + `ItemsControl` | 1:1 |
| 玻璃胶囊、玻璃条（主题染色 + sheen + 描边） | `glassEffect` + overlay 描边 | 半透明 `Border` + `LinearGradientBrush` 高光 + 双层描边（照抄 `ThemeSurfaceModifier` 的三层结构） | 观感近似，形状/染色/描边 1:1，无折射 |
| 页面渐变与光斑（左浅右深、上浅下深） | `LinearGradientBrush` 等价物 | `LinearGradientBrush` + `RadialGradientBrush`，色值取自共享 ThemeCatalog | 1:1 |
| 详情页整页背景随主题 | 渐变背景层 | 同 | 1:1 |
| 窗口材质 | 标题栏染色 + 玻璃铺到内容上沿 | `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` 设 Mica/Acrylic | 现代观感一致，材质不同档 |
| 播放页控制条 + 自动隐藏 | 视频上是 SwiftUI 层 | **airspace 限制**：`HwndHost` 的子 HWND 永远压在 WPF 内容之上。控制条放 `Popup`（独立顶层 HWND，能浮在上面）或独立分层窗口跟随 | 观感 1:1，实现路径不同 |
| 字幕 | mpv 渲染 | 同 | 1:1（天然一致） |
| 悬停浮层、拖把手排序、右键菜单、Toast、快捷键、PerMonitorV2 DPI、托盘、单实例 | AppKit/SwiftUI | WPF 原生能力，box-send 已全部趟过 | 1:1 |
| 各类弹窗（记录、编辑、豆瓣绑定、批量选择、分集剧照） | `.sheet`/自绘浮层 | `Window`/`Popup` + 主题模板 | 1:1 |
| macOS 26 真折射玻璃 | `Glass.regular` | 不做 | 明确放弃，mac 侧继续 `#available` 走真玻璃 |

播放页那一栏要特别说明：现有 Avalonia 版已被迫在视频表面加一层 `#01000000` 的 `TapSurface` 兜点击，说明「视频表面之上还要不要有一层能吃事件的 UI」这个问题换任何壳都存在，不是 WPF 独有的坑。选定 Popup 方案后，覆盖层控制条、音轨/字幕菜单、拖拽调整窗口边的 grip 都能照 mac 的形状做。

## 5. 持久化怎么落（本项目最大的技术决策）

| 方案 | 做法 | 代价 | 判断 |
| --- | --- | --- | --- |
| A. GRDB 直接上 Windows | 核心照原样用 GRDB，Windows 靠社区已合入的适配（PR #1661 #1662 #1849 #1850） | 官方不在 Windows 上跑 CI，问题要自己兜 | **首选**，spike 3 天定生死 |
| B. 核心自带薄 SQLite C 层 | 核心里写 1~2k 行 SQL 仓储，mac 也切过来 | 放弃 GRDB 的迁移/观察能力，两套持久化变一套但重写完 | 兜底，可控 |
| C. 宿主持库，核心只吃行 | C# 读写 SQLite，把记录喂给 Swift | 最省事但领域逻辑被劈成两半，本 app 的逻辑核心就是查询与排序 | 不推荐，除非 A/B 都塌 |

推荐：Phase 0 抽核心时就把所有 DB 访问收进 `LibraryStore` 协议，A 不通退 B，界面与契约一行不改。

## 6. 播放宿主怎么落

- WPF `HwndHost` 造一个 `WS_CHILD` 子窗口，句柄经宿主钩子交给核心，核心 `mpv_create` + `wid` + `--gpu-api=d3d11`；这与 mac 的 `setDrawable` 时机模型一致，起播 `vid=no` 先行、拿到表面再补视频轨那套逻辑可直接共用。
- 全屏用窗口自身全屏（`WindowStyle=None` + 覆盖整个 monitor），DPI 变化与尺寸切换后重建 VO、暂停→恢复→原地精确 seek 的经验照搬 mac（这套在 mac 上刚修完，别再踩）。
- 能直接从现有 C# 版带走：`MpvNative.cs`（libmpv P/Invoke 绑定）、`RemoteIsoStreamProxy.cs`（ISO/蓝光代理流）、libmpv/ffmpeg DLL 的打包清单、安装器与托盘/单实例/PerMonitorV2 配置。
- 需要新写：Swift 侧 libmpv 绑定（约 300 行）、事件到状态快照的转换（mac 的 `MPVPlayerManager` 状态机翻译过来，是共享的重点）。

## 7. 分阶段与工作量

| 阶段 | 内容 | 估时 | 验收 |
| --- | --- | --- | --- |
| 0 | mac 侧抽 `OmniPlayCore`（协议切断 Keychain/ImageIO/NetFS/iCloud，DB 收进 `LibraryStore`） | 2~3 周 | mac 80 用例全绿、行为不变 |
| 1 | 双 spike + 契约草案（GRDB on Windows、mpv + Popup 覆盖层、URLSession 自签证书、导出表核对） | 1 周 | Go/No-Go 结论 |
| 2 | 桥层（90~120 方法）+ JSON DTO + 事件推送 + `abi-probe` | 2 周 | 探针程序跑完开机到播放全路径 |
| 3 | WPF 壳与首页、详情页、记录弹窗、设置首屏（含图像管线与虚拟化） | 7~10 周 | 与 mac 同库对照，列表/排序/分类结果一致 |
| 4 | 播放链路（覆盖层、音轨字幕、蓝光、WebDAV/媒体服务器、退出同步落库） | 3~4 周 | 真机播放与 mac 行为对齐 |
| 5 | NeoDB 全量、服务端同步、安装包 + 签名、CI 打包、测试重建 | 3~4 周 | 出可安装包，CI 双端产物 |

合计 18~24 周（全职单人，业余乘 2~3）。与上一版「UI 路线未定」的估算总量接近，但不确定性从「框架能不能用」降到「界面工作量有多少」，排期可信度完全不同。

## 8. 现有 C# / Avalonia 版的得失

带走：`MpvNative`、`RemoteIsoStreamProxy`、libmpv 打包清单、安装器与签名脚本、托盘/单实例/DPI 配置、领域用例（扫描分流、命名解析、蓝光主片段、TMDB 匹配保护、STUN 工具）翻成 Swift 测试。
作废：Avalonia 界面（`OmniPlay.UI` 与 `Desktop` 里约 6k 行）与其 axaml 主题——这部分正是被 WPF 替换的对象。
并行策略：迁移期两套 Windows 版并存，Swift 版连续两个版本功能对齐后再停 Avalonia 版。

## 9. 先做的两个 spike（5 天）

1. **GRDB on Windows**：Windows runner 上编译带 GRDB 的核心包，跑建库、版本迁移、并发读、WAL、中文路径与 UNC 长路径；不通即评估方案 B 的改造面。
2. **mpv 宿主 + 覆盖层**：WPF 空窗口里 `HwndHost` 播一个本地文件 + 一个 WebDAV 地址（自签证书），上面用 `Popup` 放一条半透明控制条，验证点击命中、拖动、自动隐藏、全屏切换、PerMonitorV2 DPI 缩放 125%/150% 下的画面比例。

两个都绿 → 定案按 §7 排期；GRDB 不通 → 先做方案 B；覆盖层不通 → 控制条退回「独立无边框跟随窗口」，观感不变但要额外处理多屏与 DPI。

## 10. 与今天早先那份评估的关系

本方案即 `2026-10-24-swift-rewrite-assessment-and-plan.md` §3 里新增的**方案 F（Swift 核心 + WPF）**，取代 A（SwiftCrossUI）/ B（OpenSwiftUI）/ C（WinUI 3 投影）的候选地位：A/B 的不确定性与 C 的投影链复杂度，都不如「成熟的 WPF + 已验证的桥接模式」划算。Phase 0 抽核心包的结论不变，那份文档里的 Apple 依赖替代清单（§2）同样适用。

## 11. 决策落定（2026-10-25）

1. **玻璃观感：自绘。** 不用 Mica/Acrylic。理由是它们只能采窗口背后的桌面，采不到本窗口内容，
   海报墙滚动时玻璃里不会跟着变色，而 mac 版正是靠这个才有纵深。落地方式是 VisualBrush 把
   玻璃板下方那块画面原样映射进来再高斯模糊，染色、高光、描边、光斑全部吃核心算好的配方，
   界面里不出现第二套调色。代价已知：每帧重绘、与 mpv 抢 GPU、玻璃板盖不住播放子窗口。
2. **与现有 C#/Avalonia 版并存。** 新骨架写在 `../OmniPlay-win-swift`，不动 `OmniPlay-win`；
   Swift 版功能对齐两个版本后再决定是否停掉 Avalonia 版。
3. 持久化仍是唯一未验证的硬未知项，由 `spike-db` 在 Windows runner 上给结论。

骨架、探针与打包流程见 `../OmniPlay-win-swift/README.md` 与 `../OmniPlay-win-swift/docs/SPIKE.md`。

