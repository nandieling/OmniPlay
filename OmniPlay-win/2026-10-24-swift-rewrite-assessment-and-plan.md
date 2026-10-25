# Windows 版 Swift 重构：评估与计划（2026-10-24）

目标：把 mac 版的功能与 UI 完整移植到 Windows，Windows 版改用 Swift 语言。

## 0. 先说结论

1. 「用 Swift 重写 Windows 版」真正卡住的只有 **UI**。Swift 6 官方支持 Windows，SwiftPM、Foundation、libmpv、SQLite 这些都不成问题；Windows 上没有生产可用的 SwiftUI，而 mac 版 31,088 行里有 **25,320 行贴着 SwiftUI/AppKit**。
2. 推荐**两步走**，而不是一次性全量重写：
   - **Phase 0/1/2**：把 mac 版的领域逻辑抽成共享 Swift 包 `OmniPlayCore`，并用一个 Windows spike 验证工具链、GRDB、libmpv、TLS。这一步无论 UI 走哪条路都不浪费。
   - **Phase 3/4/5**：UI 路线由 spike 结论决定。若 SwiftUI 类方案不达标，退路是「Swift 核心库（C ABI DLL）+ 现有 C#/Avalonia 壳」，功能照样全 Swift，只保留一层 Avalonia。
3. 如果要求「UI 也是 Swift 且像素级还原 mac 的液态玻璃」，等于自研一个小型 SwiftUI（Skia 自绘），成本最高，见 §3 方案 D。Windows 上更现实的目标是 **Mica + Acrylic 近似玻璃**：布局、信息密度、交互 1:1，材质观感近似。

## 1. 现状盘点

mac ARM（`OmniPlay开源-mac-ARM`）：42 个 Swift 文件、31,088 行。

| 分类 | 规模 | 说明 |
| --- | --- | --- |
| UI 与平台耦合层 | 28 文件 / 25,320 行 | 直接 import SwiftUI 或 AppKit |
| 纯逻辑 | 14 文件 / 5,810 行 | TMDBService、DatabaseModels、NeodbModels、SMBSourceManager、ChineseNameStore、LuckyStun、MediaMarkStore、AppDatabase 等 |
| 挂 SwiftUI 只为 `ObservableObject` 的逻辑 | 约 7,300 行 | MediaLibraryManager 4,256、OfflineCacheManager 834、ThumbnailManager 668、LANScanner 663、NeodbSyncService 466、DirectFilePlaybackManager 418 |

也就是说：两平台真正能共用的「核心」上限约 **1.3 万行**；首页、详情页、设置、播放器覆盖层、玻璃主题、mpv 宿主这 1.8 万行在任何方案下都要在 Windows 侧再写一遍。

Windows 现状（`OmniPlay-win`，Avalonia + .NET 10 + SQLite + libmpv）：

| 工程 | C# 行数 |
| --- | --- |
| OmniPlay.Core | 17,166 |
| OmniPlay.Infrastructure | 16,974 |
| OmniPlay.UI | 3,765 |
| OmniPlay.Player.Mpv | 3,077 |
| OmniPlay.Desktop | 2,094 |
| 测试 | 12,090 |

移植这件事已经付过一次学费：领域模型、扫描分流、刮削、WebDAV/Plex/Emby/Jellyfin、SMB、TMDB 代理网关、Lucky STUN、蓝光 `bd://` 都已有 C# 实现与测试（功能差异记录见 `2026-09-04-win-vs-arm-mac-feature-diff.md`、`2026-10-07`、`2026-10-10` 三份文档）。Swift 重构要回答的第一个问题不是「能不能」，而是「这份已经跑通的东西要不要作废」。

## 2. Apple 专属依赖清单与 Windows 替代

| mac 依赖 | 现有用途 | Windows 方案 | 风险 |
| --- | --- | --- | --- |
| SwiftUI（25k 行） | 全部界面 | 见 §3 四选一 | **高**，本项目的成败点 |
| AppKit | NSWorkspace、NSOpenPanel、NSWindow 染色、NSMenu、拖拽 | Win32/WinRT 文件对话框、窗口样式、菜单、拖放 | 中 |
| GRDB.swift | 全部持久化 | 社区已合入 Windows 适配（PR #1661 #1662 #1849 #1850），但官方只在 Apple 平台自动测试 | 中高，Phase 1 必测；兜底是 SQLite C API 自研薄层，两平台共用同一套仓储接口 |
| Security（Keychain） | WebDAV 账号、NeoDB 令牌 | Windows 凭据管理器 `CredWrite/CredRead` | 低 |
| LocalAuthentication | 隐私锁 | Windows Hello（`UserConsentVerifier`），或首版不做 | 低 |
| ImageIO + NSImage | 海报/剧照降采样与绘制 | Windows 上没有 NSImage：WIC（COM）或 libjpeg-turbo/libpng，或复用 ffmpeg | 中，海报是主视觉，必须早做 |
| NetFS + `mount_smbfs` | SMB 挂载 | Windows 原生 UNC 路径，反而可以省掉挂载这一步 | 低 |
| Network / NWBrowser | 局域网设备发现 | 自研 mDNS/SSDP 或 `dns_sd`；退路是只留手动填地址 | 中 |
| iCloud 容器 | 海报目录、库文件跨设备同步 | 无对应。改走 OmniPlay-docker 服务端，顺带补上 Windows 目前缺的服务端同步 | 中，属于功能取舍 |
| Combine | `ObservableObject`/`@Published` | 核心包内定义最小 `Store` 协议（推荐），或 OpenCombine | 低 |
| Observation（`@Observable`） | 少量视图 | Windows 工具链是否随包发布待实测；核心包不依赖它 | 低 |
| MPVKit（xcframework）+ CAMetalLayer | 播放与渲染 | 官方 `libmpv-2.dll` + render API，`--wid`=HWND、`--gpu-api=d3d11`；Swift 写 C 绑定即可（C# 版已验证这条路可行） | 中，尺寸切换重建 VO 的经验可直接复用 |
| UniformTypeIdentifiers | 文件类型 | 扩展名 + 注册表文件关联 | 低 |
| UserNotifications | 扫描完成提示 | Windows Toast | 低 |

## 3. UI 路线四选一（外加一个对照方案）

| 方案 | SwiftUI 代码复用 | 成熟度 | 视觉还原 | 主要成本 | 判断 |
| --- | --- | --- | --- | --- | --- |
| A SwiftCrossUI（moreSwift/swift-cross-ui，0.2.x，1.8k star，活跃） | 低：API 像 SwiftUI 但不是 SwiftUI，视图逐个改写；`ViewModifier`、`AnyShape`、`glassEffect` 这套要另写 | 早期，但工程化程度不错 | 原生控件风，玻璃/渐变/圆角自绘能力有限 | 中 | 适合工具型 App，对本项目首页这种重度自绘海报墙偏紧 |
| B OpenSwiftUI（2.5k star，提交到今天） | 最高：目标就是复刻 SwiftUI | README 自陈 early development、learning/research only；Windows 还需自接渲染器与事件循环 | 取决于接的渲染器 | 中高 | 押在生产 App 上是全项目最大不确定项 |
| C WinUI 3 / Windows App SDK + swift-winrt 投影 | 无（XAML 重写），Swift 负责逻辑与 ViewModel | 微软官方，长期可依赖 | Windows 原生最佳：Mica、Acrylic、DPI、输入法、触摸 | 中高：XAML 全量重写 + WinRT 投影链小众、调试门槛高 | 若要求「Swift 写 App 且长期稳定」，这是最稳的一条 |
| D 自绘（Skia/wgpu + 自研布局） | 可复用视图层结构，但要自己实现布局系统 | 全靠自研 | 唯一能像素级还原 mac 玻璃的路 | 最高 | 除非玻璃观感是硬需求，否则不建议 |
| **F Swift 核心 + WPF（box-send 同款，推荐）** | 无（XAML 重写），但桥接模式已在 box-send 量产验证 | 高：WPF 成熟，桥已跑通 | 高：主题化模板可做到布局/主题/交互 1:1，仅真折射玻璃近似 | 中高（界面工作量为主） | 见 `2026-10-24-swift-core-wpf-assessment.md` |
| E（对照）Swift 核心库 + 现有 Avalonia 壳 | 不适用（UI 不动） | 立即可用 | 现状 | 低 | 不是「全 Swift」，但最快拿到「一份逻辑两平台跑」，且是 A/B/C/D 失败时的回退点 |

## 4. 分阶段计划

工作量按单人全职估算，业余节奏请乘 2~3。

### Phase 0：mac 侧抽核心包（2~3 周，UI 一行不动）

- 新建 `Packages/OmniPlayCore`（SwiftPM）：`AppDatabase`、`DatabaseModels`、`MediaMarkStore`、`TMDBService`/`TMDBModels`、`NeodbModels`/`NeodbService`、`ChineseNameStore`、`LuckyStun`、`LocalMetadataSidecarStore`、`PersonProfileStore`、`EpisodeMetadataOverrideStore`，以及 `MediaNameParser`、蓝光包聚合、首页排序与分类分组等纯函数。
- 用协议切断平台依赖：`SecretStore`、`ImageDecoder`、`PathProvider`、`Clock`、`NotificationBus`、最小 `Store`（替 `ObservableObject`）。`MediaLibraryManager` 按「协议先行、逐函数搬迁」拆，不做一次性大迁移。
- 验收：mac 现有 80 个用例全绿 + 手工冒烟；`swift build` 在 arm64 macOS 通过；UI 层只通过协议访问核心。

### Phase 1：Windows spike（1~2 周，决定 UI 路线）

在 GitHub Actions `windows-latest` 上 `swift build` 一个最小可执行程序，实测：

1. GRDB 建库/迁移/读写（含 WAL 与并发读）。
2. URLSession 打 TMDB 与自签证书 NAS（`https://192.168.x.x:5006`），确认 TLS 行为与证书绕过写法。
3. `libmpv-2.dll` 起播（`--wid` 指向一个空 Win32 窗口），验证 `vid=no` 先行与尺寸切换重建 VO。
4. 工具链本身：SwiftPM 资源包、`Observation`/`Combine` 可用性、UTF-8/GBK 路径、UNC 与超长路径、Swift 运行时 DLL 的打包方式与体积、代码签名。
5. A/B/C 任选一个 UI hello world（含网格、图片缓存、暗色主题、键盘事件）。

产出：Go/No-Go 结论文档，锁定 UI 路线。

### Phase 2：核心包上 Windows（2~3 周）

- OmniPlayCore 在 Windows 编译通过并跑通全部核心用例；DB schema 与 mac 共用同一份版本迁移链。
- 同一份 sqlite 文件做双端对照：扫描、排序、分类分组、进度计算结果一致。
- 新增 OmniPlay-docker 服务端同步（补 Windows 现有缺口），iCloud 那条路改成服务端。

### Phase 3：Windows Swift 壳与首页（6~10 周，UI 路线定稿后）

- 窗口、托盘、单实例、文件关联、设置持久化、更新检查。
- 首页（继续播放 + 所有影视 + 导演/主演分类）、详情页、记录弹窗、设置页首屏。
- 海报/剧照解码与缓存（`ImageDecoder` 的 Windows 实现）、主题色板从 mac 的 `ThemeManager` 抽进核心包，两平台共用同一份色值与渐变定义。

### Phase 4：播放链路（4~6 周）

- libmpv Swift 绑定、HWND 宿主、D3D11/OpenGL 渲染、覆盖层控制、音轨/字幕、进度退出时同步落库（⌘Q 那条经验直接照搬）、蓝光 `bd://`、WebDAV/Plex/Emby/Jellyfin 播放、SMB UNC 直连。

### Phase 5：对齐与上线（3~5 周）

- 设置全量、NeoDB 记录与同步、离线缓存、缩略图/剧照、安装包（MSIX 或 Inno）+ 代码签名 + 自动更新、Windows CI 打包（与 mac 的 `dist/` 产物同构）、文档。
- C# 版继续并行，直到 Swift 版连续两个版本功能对齐再下线。

总计：Phase 0~2 约 5~8 周，Phase 3~5 约 13~21 周。

## 5. 风险与回退

- **UI 框架是唯一的高不确定项**，所以 Phase 0/1 完全不碰 UI；spike 不通就落回方案 E，已投入全部保留。
- Swift 从 mac 交叉编译到 `x86_64-unknown-windows-msvc` 目前仍麻烦（Windows SDK、ucrt、SDK 目录），建议 Windows 构建直接放 windows runner 原生跑，mac 侧只做代码检查与单测。
- 安装包体积会上去：Swift 运行时 DLL + libmpv + ffmpeg 粗估 60~120MB，Phase 1 一并量化。
- C# 侧 12,090 行测试需要等价重建，迁移期两套并存跑，别边写边删。
- 「UI 完整移植」要先定义清楚：布局与信息密度 1:1、交互 1:1 可承诺；折射玻璃只能近似（除非走方案 D）。

## 6. 建议目录结构

```text
Packages/OmniPlayCore/        # 共享核心（mac 与 Windows 同一份）
OmniPlay开源-mac-ARM/          # 壳：SwiftUI + AppKit
OmniPlay开源-mac-x64/          # 镜像
OmniPlay-win/                 # 现有 C# / Avalonia 版，迁移期并行
OmniPlay-win-swift/           # 新壳：Windows UI + 平台层 + OmniPlayCore
```

## 7. 需要拍板的三件事

1. UI 路线：A / B / C / E（建议：先按 E 推进 Phase 0~2，spike 之后再决定是否升到 C）。
2. 视觉还原目标：Windows 原生观感（Mica/Acrylic）还是像素级 mac 玻璃（后者意味着方案 D 与额外 8~12 周）。
3. C# 版定位：迁移期并行维护（建议），还是 Swift 版一可用就停更。
