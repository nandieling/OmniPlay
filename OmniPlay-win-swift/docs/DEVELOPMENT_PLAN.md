# Windows 版开发方案（Swift 核心 + WPF）

## 1. 这份文档管什么

`../OmniPlay-win/WINDOWS_PORT_DEVELOPMENT_PLAN.md` 写的是 C#/Avalonia 那一版，本文只管
`OmniPlay-win-swift` 这条新线：现在到哪了、下一步做什么、怎么验收。两边并存，功能对齐后再决定留哪个。

## 2. 已定的两条路线

- 玻璃观感走**自绘**：`VisualBrush` 采本窗口内容 + 高斯模糊。Mica/Acrylic 采不到窗口内容，
  做不出海报墙滚动时玻璃跟着变色。
- 与 C#/Avalonia 版**并存**，新版功能对齐后再决定是否替换。

## 3. 当前状态（2026-10-25）

跑起来的证据：CI 每次出包都跑核心单测、ABI 探针、随包依赖自检、干净 PATH 下的界面冒烟。
最近一次绿的运行里，安装包装机（101 MB），界面从装载到「界面活着」再到退出 0。

### 3.1 边界与打包

- C ABI 六函数 + JSON 契约，响应缓冲由核心分配、宿主归还。
- Swift 运行时 13 个 DLL 随包，MSVC 运行库 3 个进 `redist\`，PE 导入表逐个核过。
- libmpv 随包（115 MB）；拿不到只影响播放，界面照装。
- Inno 安装包 + 便携 zip + sha256；`win-v*` 标签顺手发 Release。

### 3.2 核心包（`Sources/`）

- 主题配方 `theme.*`：五套配色 × 经典/液态玻璃，两个平台同一份算法。
- 片库 `library.*`：自带 SQLite 合并源 + 薄封装，表名照 mac 版；首次用到才开库，
  空库铺 12 部演示片。库文件在 `<数据目录>/library.db`（Windows = `%APPDATA%\OmniPlay\library.db`）。
- 排序与分桶 `home.sort` / `category.groups`：中文按拼音，外文排后面。
- 播放器状态机 `player.*`：`--wid` 传子 HWND，headless 也能跑，CI 里验整条链路；
  `player.seek` 走 seek 命令而不是写 time-pos，续播才不会被某些容器吞掉。
- 测试 47 项。

### 3.3 界面（`windows/OmniPlay.Wpf/`）

- 自绘玻璃：渐变底 + 主题光斑 + 采样本窗口内容的模糊玻璃板。
- 左侧导航（首页/继续播放/已看/未看/导演/主演）切的都是核心认得的视图 id。
- 海报墙 `HomeBoard` 只画不判：分组、片名排序、已看未看全来自 `library.sections`；
  海报占位色 = 核心按片名算的色相与主题色混合。
- 播放子 HWND + Popup 控制条（airspace：子窗口盖在 WPF 内容上，控制条必须另起顶层窗口）。
- 详情页 `DetailView`：叠在海报墙右侧一栏，片名 / 年份 / 时长 / 进度 / 演职员 / 类型全部来自
  `library.sections` 那条记录，界面不自己算。渐变方向与 mac 弹窗一致（左浅右深、上浅下深）。
- 进度回写：播放中每 5 秒写一次 `library.progress`，停机与关窗前强制写一次；
  回到首页就能在「继续播放」里看到它。详情页的「继续播放」会 seek 回上次的秒数。

## 4. 边界契约

| 方法 | 用途 |
| --- | --- |
| `version` `echo` `error.demo` | 边界自检 |
| `theme.list` `theme.get` | 配色清单 / 一份主题配方 |
| `library.sections` `library.section` `library.stats` | 首页分组、单组、库统计 |
| `library.categories` | 导演 / 主演分桶 |
| `library.mark` `library.progress` | 已看未看、观看进度（写 `mediaMark`） |
| `library.seed` | 显式铺演示数据 |
| `home.sort` `category.groups` | 纯函数：给记录返回顺序 / 分桶 |
| `player.*` | 状态、起播、加载、跳转（`player.seek`）、暂停、停机 |

## 5. 分步计划

按这个顺序推，每步都要能装出来看一眼再进下一步。

| 步 | 做什么 | 完成判据 | 状态 |
| --- | --- | --- | --- |
| 0 | 骨架、契约、打包、闪退排查 | 安装包能在用户机器上打开 | 已完成 |
| 0.5 | 片库层 + 首页海报墙 + 导航 | 首页有真数据，能换架子、能标记 | 已完成 |
| 1 | 详情页与播放入口 | 点海报出详情，能起播/续播，退出回写进度并进继续播放 | 已完成 |
| 2 | 真实海报与缩略图 | 占位色换成真图，带磁盘缓存与降采样 | 待做 |
| 3 | 媒体源与扫描 | 本地 / WebDAV / SMB 挂进来能扫能入库，替掉演示数据 | 待做 |
| 4 | 刮削 | TMDB 查询 + 中文名映射 + 手动改片 | 待做 |
| 5 | 播放页 | 进度条、音轨字幕、快捷键、全屏切换不花屏 | 待做 |
| 6 | NeoDB 记录 | 复用 `mediaMark` 做标记与评分同步 | 待做 |
| 7 | 设置与多窗口 | 主题/语言/代理/凭据，播放独立窗口 | 待做 |

## 6. 怎么验证

- 本机（mac）：`swift build`、`swift test`、`python3 scripts/host_smoke.py`、
  `dotnet build windows/OmniPlay.Wpf`（交叉编译，只能验语法，跑不了 WPF）。
- CI（`windows-package.yml`）：真跑界面。冒烟日志在 `%LOCALAPPDATA%\OmniPlay\startup.log`，
  装了之后打不开就看这个文件。

## 7. 已知限制

- 演示片库写死 12 条，媒体源扫描（第 3 步）之前不会有真内容。
- 详情页只覆盖在播放区之上，不是独立窗口；演示片库的路径是假的，播的是随包示例片。
- 界面仍是单窗口单页，没有系统托盘、任务栏进度之类系统集成。

## 8. 文档更新原则

每完成一步就更新本文第 3 节与第 5 节的状态；改了契约要同步第 4 节的表，
界面对不上契约时优先怀疑界面，别在界面上再算一遍规则。
