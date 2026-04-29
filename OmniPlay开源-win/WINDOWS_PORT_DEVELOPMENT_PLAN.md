# OmniPlay Windows 版开发方案

## 1. 文档目的

本文档用于定义 OmniPlay 的 Windows 版移植方案、工程结构、阶段计划、当前实现状态，以及手动编译运行方式。

当前目标技术栈：

- 语言：`C#`
- UI：`Avalonia 11`
- 播放核心：`libmpv`
- 数据库：`SQLite`
- 目标系统：`Windows 10/11 x64`

本次移植的目标不是把 Swift 代码逐行翻译成 C#，而是基于 Windows 平台重建一套可维护的桌面应用，同时尽量还原 macOS 版的：

- 信息架构
- UI 风格与交互路径
- 媒体库、刮削、播放的核心能力

参考源工程位于：

- [OmniPlay](C:\软件\OmniPlay开源-mac\OmniPlay)

Windows 版工程位于：

- [windows](C:\软件\OmniPlay开源-mac\windows)

## 2. 当前结论

当前已经确定的技术路线：

- `C# + Avalonia + libmpv`

选择原因：

- `Avalonia` 更适合还原当前 mac 版偏自定义的视觉和布局风格
- `C#` 适合承接原有 Swift 中的状态管理、异步任务、数据库、网络访问
- `libmpv` 继续保留，可以减少播放器层重写风险

不采用的路线：

- 不使用 SwiftUI for Windows
- 不做 Electron/Tauri 重写
- 不在第一阶段引入 Web 技术 UI
- 不在移植早期替换播放器内核

## 3. 现有 macOS 工程结构判断

当前 macOS 版不是单纯的 SwiftUI 小应用，而是由以下部分组合而成：

- `SwiftUI`：大部分界面
- `AppKit`：窗口控制、文件选择、原生视图嵌入
- `libmpv`：视频播放
- `GRDB`：SQLite 封装
- 自定义媒体扫描和文件名解析
- TMDB 刮削与海报/缩略图缓存

关键源文件：

- 应用入口
  - [OmniPlayApp.swift](C:\软件\OmniPlay开源-mac\OmniPlay\OmniPlayApp.swift)
  - [ContentView.swift](C:\软件\OmniPlay开源-mac\OmniPlay\ContentView.swift)
- 主页面和媒体库
  - [PosterWallView.swift](C:\软件\OmniPlay开源-mac\OmniPlay\PosterWallView.swift)
  - [MovieDetailView.swift](C:\软件\OmniPlay开源-mac\OmniPlay\MovieDetailView.swift)
  - [MovieCardView.swift](C:\软件\OmniPlay开源-mac\OmniPlay\MovieCardView.swift)
- 播放器
  - [PlayerScreen.swift](C:\软件\OmniPlay开源-mac\OmniPlay\PlayerScreen.swift)
  - [MPVPlayerManager.swift](C:\软件\OmniPlay开源-mac\OmniPlay\MPVPlayerManager.swift)
  - [MPVVideoView.swift](C:\软件\OmniPlay开源-mac\OmniPlay\MPVVideoView.swift)
- 多窗口播放
  - [DirectPlaybackWindowManager.swift](C:\软件\OmniPlay开源-mac\OmniPlay\DirectPlaybackWindowManager.swift)
  - [DirectFilePlaybackManager.swift](C:\软件\OmniPlay开源-mac\OmniPlay\DirectFilePlaybackManager.swift)
- 数据与刮削
  - [MediaLibraryManager.swift](C:\软件\OmniPlay开源-mac\OmniPlay\MediaLibraryManager.swift)
  - [TMDBService.swift](C:\软件\OmniPlay开源-mac\OmniPlay\TMDBService.swift)
  - [PosterManager.swift](C:\软件\OmniPlay开源-mac\OmniPlay\PosterManager.swift)
  - [ThumbnailManager.swift](C:\软件\OmniPlay开源-mac\OmniPlay\ThumbnailManager.swift)
- 数据模型
  - [AppDatabase.swift](C:\软件\OmniPlay开源-mac\OmniPlay\AppDatabase.swift)
  - [DatabaseModels.swift](C:\软件\OmniPlay开源-mac\OmniPlay\DatabaseModels.swift)

## 4. Windows 版推荐工程结构

Windows 版按职责拆成多个项目：

```text
windows/
  src/
    OmniPlay.Desktop/
    OmniPlay.UI/
    OmniPlay.Core/
    OmniPlay.Infrastructure/
    OmniPlay.Player.Mpv/
  tests/
    OmniPlay.Tests/
```

当前解决方案文件：

- [OmniPlay.Windows.slnx](C:\软件\OmniPlay开源-mac\windows\OmniPlay.Windows.slnx)

### 4.1 `OmniPlay.Desktop`

职责：

- Avalonia 应用入口
- 启动初始化
- 依赖注入
- 顶层窗口创建
- 平台级桌面行为

当前关键文件：

- [Program.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Desktop\Program.cs)
- [App.axaml](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Desktop\App.axaml)
- [App.axaml.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Desktop\App.axaml.cs)
- [ServiceRegistration.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Desktop\Bootstrap\ServiceRegistration.cs)

### 4.2 `OmniPlay.UI`

职责：

- Avalonia 视图
- 用户控件
- 样式和主题
- 主界面布局

当前关键文件：

- [MainWindow.axaml](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.UI\Views\Shell\MainWindow.axaml)
- [PosterWallView.axaml](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.UI\Views\Library\PosterWallView.axaml)

### 4.3 `OmniPlay.Core`

职责：

- 领域模型
- ViewModel
- 服务接口
- 设置与业务契约

当前关键文件：

- [ShellViewModel.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Core\ViewModels\ShellViewModel.cs)
- [PosterWallViewModel.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Core\ViewModels\Library\PosterWallViewModel.cs)
- [IMediaSourceRepository.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Core\Interfaces\IMediaSourceRepository.cs)
- [IMovieRepository.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Core\Interfaces\IMovieRepository.cs)
- [ILibraryScanner.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Core\Interfaces\ILibraryScanner.cs)

### 4.4 `OmniPlay.Infrastructure`

职责：

- SQLite 访问
- 文件系统
- 本地媒体扫描
- 后续承接 TMDB、WebDAV、海报、缓存等外部能力

当前关键文件：

- [SqliteDatabase.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Infrastructure\Data\SqliteDatabase.cs)
- [MediaSourceRepository.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Infrastructure\Data\MediaSourceRepository.cs)
- [MovieRepository.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Infrastructure\Data\MovieRepository.cs)
- [StoragePaths.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Infrastructure\FileSystem\StoragePaths.cs)
- [JsonSettingsService.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Infrastructure\FileSystem\JsonSettingsService.cs)
- [MediaNameParser.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Infrastructure\Library\MediaNameParser.cs)
- [LibraryScanner.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Infrastructure\Library\LibraryScanner.cs)

### 4.5 `OmniPlay.Player.Mpv`

职责：

- `libmpv` 互操作
- 播放器生命周期
- 视频宿主桥接

当前关键文件：

- [MpvPlayer.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Player.Mpv\MpvPlayer.cs)
- [MpvNative.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Player.Mpv\Interop\MpvNative.cs)
- [MpvVideoHost.cs](C:\软件\OmniPlay开源-mac\windows\src\OmniPlay.Player.Mpv\Hosting\MpvVideoHost.cs)

## 5. 依赖方向约束

必须保持以下依赖方向：

- `OmniPlay.Desktop` -> `UI`、`Core`、`Infrastructure`、`Player.Mpv`
- `OmniPlay.UI` -> `Core`
- `OmniPlay.Infrastructure` -> `Core`
- `OmniPlay.Player.Mpv` -> `Core`

禁止：

- `Core` 引用 `Avalonia`
- `Core` 直接访问数据库或 `libmpv`
- `UI` 直接写 SQL 或网络请求
- `UI` 直接操作播放器底层原生接口

## 6. 包与框架

当前工程集中版本文件：

- [Directory.Packages.props](C:\软件\OmniPlay开源-mac\windows\Directory.Packages.props)
- [Directory.Build.props](C:\软件\OmniPlay开源-mac\windows\Directory.Build.props)

当前主要依赖：

- `Avalonia`
- `Avalonia.Desktop`
- `Avalonia.Themes.Fluent`
- `CommunityToolkit.Mvvm`
- `Dapper`
- `Microsoft.Data.Sqlite`
- `Microsoft.Extensions.DependencyInjection`
- `Microsoft.Extensions.Logging`
- `Serilog`
- `xUnit`

说明：

- 现在本机环境里只有 `.NET 10 SDK`
- 因此当前工程暂时以 `net10.0` 编译
- 如果后续统一为 `.NET 8`，需要安装对应 SDK 后再切换 `TargetFramework`

## 7. Swift 文件到 Windows 模块的映射

### 7.1 应用入口

- [OmniPlayApp.swift](C:\软件\OmniPlay开源-mac\OmniPlay\OmniPlayApp.swift)
  - `OmniPlay.Desktop/App.axaml`
  - `OmniPlay.Desktop/App.axaml.cs`

- [ContentView.swift](C:\软件\OmniPlay开源-mac\OmniPlay\ContentView.swift)
  - `OmniPlay.UI/Views/Shell/MainWindow.axaml`
  - `OmniPlay.Core/ViewModels/ShellViewModel.cs`

### 7.2 媒体库

- [PosterWallView.swift](C:\软件\OmniPlay开源-mac\OmniPlay\PosterWallView.swift)
  - `OmniPlay.UI/Views/Library/PosterWallView.axaml`
  - `OmniPlay.Core/ViewModels/Library/PosterWallViewModel.cs`

- [MovieCardView.swift](C:\软件\OmniPlay开源-mac\OmniPlay\MovieCardView.swift)
  - 后续映射到 `OmniPlay.UI/Controls/Library/MovieCard.axaml`

- [MovieDetailView.swift](C:\软件\OmniPlay开源-mac\OmniPlay\MovieDetailView.swift)
  - 后续映射到 `OmniPlay.UI/Views/Library/MovieDetailView.axaml`
  - 后续映射到 `OmniPlay.Core/ViewModels/Library/MovieDetailViewModel.cs`

### 7.3 播放器

- [PlayerScreen.swift](C:\软件\OmniPlay开源-mac\OmniPlay\PlayerScreen.swift)
  - 后续映射到 `OmniPlay.UI/Views/Player/PlayerView.axaml`
  - 后续映射到 `OmniPlay.Core/ViewModels/Player/PlayerViewModel.cs`

- [MPVPlayerManager.swift](C:\软件\OmniPlay开源-mac\OmniPlay\MPVPlayerManager.swift)
  - `OmniPlay.Player.Mpv/MpvPlayer.cs`
  - `OmniPlay.Player.Mpv/Interop/MpvNative.cs`

- [MPVVideoView.swift](C:\软件\OmniPlay开源-mac\OmniPlay\MPVVideoView.swift)
  - `OmniPlay.Player.Mpv/Hosting/MpvVideoHost.cs`
  - 后续会有 Avalonia 控件宿主

### 7.4 数据与刮削

- [AppDatabase.swift](C:\软件\OmniPlay开源-mac\OmniPlay\AppDatabase.swift)
  - `OmniPlay.Infrastructure/Data/SqliteDatabase.cs`

- [DatabaseModels.swift](C:\软件\OmniPlay开源-mac\OmniPlay\DatabaseModels.swift)
  - `OmniPlay.Core/Models/Entities/*.cs`

- [MediaLibraryManager.swift](C:\软件\OmniPlay开源-mac\OmniPlay\MediaLibraryManager.swift)
  - `OmniPlay.Infrastructure/Library/MediaNameParser.cs`
  - `OmniPlay.Infrastructure/Library/LibraryScanner.cs`

- [TMDBService.swift](C:\软件\OmniPlay开源-mac\OmniPlay\TMDBService.swift)
  - 后续映射到 `OmniPlay.Infrastructure/Tmdb/TmdbService.cs`

## 8. 当前已完成内容

截至目前，Windows 版已经具备以下能力：

### 8.1 工程可编译

已确认可以执行：

- `dotnet restore`
- `dotnet build`

当前方案已经能成功 Debug 编译。

### 8.2 启动初始化

- 程序启动时会初始化依赖注入
- 启动时会创建本地数据目录
- 启动时会初始化 SQLite 数据库和基础表结构

### 8.3 数据目录结构

默认数据目录：

```text
%LocalAppData%\OmniPlay\
```

当前代码中已约定的子目录：

```text
%LocalAppData%\OmniPlay\
  data\
  cache\
  settings\
  posters\
  thumbnails\
  logs\
```

数据库文件位置：

```text
%LocalAppData%\OmniPlay\data\omniplay.sqlite
```

### 8.4 核心模型

当前已落地模型：

- `MediaSource`
- `Movie`
- `TvShow`
- `VideoFile`
- `LibraryScanSummary`

### 8.5 媒体名解析

当前已迁移基础能力：

- 标题清洗
- 年份提取
- `SxxExx` 剧集识别
- BDMV 场景标题识别
- 本地路径规范化

### 8.6 最小媒体库闭环

当前 UI 已经有最小闭环：

- `Refresh`
- `Scan`
- `Add Videos Folder`
- 媒体源列表
- 扫描摘要
- 最近扫描诊断信息
- 电影/剧集海报墙
- 详情页文件列表
- 剧集季切换
- 继续观看区域
- 详情页覆盖层播放器
- 覆盖层与独立窗口专用视频宿主控件

当前扫描器能力：

- 仅支持本地目录
- 识别常见视频扩展名
- 首次扫描时写入 `movie` / `tvShow` / `videoFile`
- 同源重复路径会在扫描时收敛，只保留一条记录并保留较完整的播放状态
- 已不存在的旧文件记录会在扫描时清理
- `BDMV/STREAM` 会按主片段大小筛选，避免把大量小片段都当成独立视频
- 目前使用合成 `movie id`
- 已支持基础 TMDB 刮削、海报下载、详情页手动重匹配与锁定
- 已支持 WebDAV 媒体源、连通性测试、递归扫描和带认证的远程播放地址生成
- 缩略图链路已接入基础能力，仍需继续打磨
- 已有播放入口、进度回写和专用宿主控件接入，仍需真机播放验证

## 9. 手动编译与运行

本节用于手动验证当前 Windows 版工程效果。

### 9.1 前提

需要满足：

- 已安装 `.NET SDK`
- 当前仓库路径为 `C:\软件\OmniPlay开源-mac`
- 当前解决方案文件存在：
  - [OmniPlay.Windows.slnx](C:\软件\OmniPlay开源-mac\windows\OmniPlay.Windows.slnx)

### 9.2 在 `cmd.exe` 中编译和运行

如果你当前用的是 `cmd`，执行：

```cmd
cd /d C:\软件\OmniPlay开源-mac
set DOTNET_CLI_HOME=C:\软件\OmniPlay开源-mac\.dotnet
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_NOLOGO=1
dotnet restore .\windows\OmniPlay.Windows.slnx
dotnet build .\windows\OmniPlay.Windows.slnx -c Debug
dotnet run --project .\windows\src\OmniPlay.Desktop\OmniPlay.Desktop.csproj
```

注意：不要在 `cmd.exe` 中使用 `$env:DOTNET_CLI_HOME=...` 这类 PowerShell 语法，`cmd` 里应使用 `set`。

### 9.3 在 PowerShell 中编译和运行

如果你当前用的是 PowerShell，执行：

```powershell
cd C:\软件\OmniPlay开源-mac
$env:DOTNET_CLI_HOME='C:\软件\OmniPlay开源-mac\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'
dotnet restore .\windows\OmniPlay.Windows.slnx
dotnet build .\windows\OmniPlay.Windows.slnx -c Debug
dotnet run --project .\windows\src\OmniPlay.Desktop\OmniPlay.Desktop.csproj
```

### 9.4 直接运行已编译程序

如果已经编译完成，也可以直接运行：

```cmd
.\windows\src\OmniPlay.Desktop\bin\Debug\net10.0\OmniPlay.Desktop.exe
```

### 9.5 查看运行日志

应用运行日志会写入：

```text
%LOCALAPPDATA%\OmniPlay\logs\app.log
```

直接打开：

```cmd
notepad %LOCALAPPDATA%\OmniPlay\logs\app.log
```

PowerShell 实时查看：

```powershell
Get-Content "$env:LOCALAPPDATA\OmniPlay\logs\app.log" -Wait
```

### 9.6 查看系统崩溃日志

如果应用直接崩溃退出，也可以通过 Windows 事件日志排查：

```powershell
Get-WinEvent -LogName Application -MaxEvents 50 |
  Where-Object { $_.ProviderName -match 'Application Error|.NET Runtime|Windows Error Reporting' } |
  Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, Message
```

如果只筛选 `OmniPlay.Desktop`：

```powershell
Get-WinEvent -LogName Application -MaxEvents 100 |
  Where-Object { $_.Message -match 'OmniPlay.Desktop' } |
  Select-Object TimeCreated, ProviderName, Id, Message
```

也可以直接打开“事件查看器”：

- `Windows 日志`
- `应用程序`
- 查看 `.NET Runtime`、`Application Error`、`Windows Error Reporting`

### 9.7 当前运行后可见效果

当前运行后，预期可以看到：

- 一个 Avalonia 主窗口
- 顶部操作按钮：`Refresh`、`Scan`、`Add Videos Folder`
- 媒体源区域
- 最近扫描摘要区域
- 电影列表区域

`Add Videos Folder` 的当前行为：

- 会尝试把当前用户的 `Videos` 目录加入为本地媒体源

`Scan` 的当前行为：

- 扫描已加入的本地目录
- 识别常见视频文件
- 将扫描结果写入 SQLite
- 再次加载页面数据

## 10. 当前已知限制

当前不是最终产品状态，限制如下：

- WebDAV 当前以基础连通性、递归扫描和 Basic Auth 播放为主，更复杂认证与缓存策略未完成
- 只实现最小扫描逻辑，不做复杂归并
- 剧集与电影还没有完整分流策略
- TMDB 已有基础刮削、海报、手动修正、语言设置和基础自动控制链路，更细的控制仍待补齐
- 缩略图链路仍在继续补齐和验证
- 播放器仍以详情页覆盖层为主，独立页面细节还未完善
- `libmpv` 已接入专门的视频宿主控件，但还缺少真机验证和稳定性打磨

## 11. 当前编译状态与警告说明

当前已能成功 `build`，但仍有一些警告：

### 11.1 Avalonia 版本警告

`Directory.Packages.props` 已统一更新为 `11.2.0`：

- `Avalonia`
- `Avalonia.Controls.DataGrid`
- `Avalonia.Desktop`
- `Avalonia.Themes.Fluent`

此前的 `NU1603` 版本解析警告已按这个方式处理。

### 11.2 依赖漏洞警告

编译时会出现：

- `Tmds.DBus.Protocol 0.20.0` 的漏洞提示

这是 Avalonia 依赖链带出的告警，当前不会阻止本地开发，但后续发布前需要重新评估依赖版本。

## 12. 测试策略

当前测试项目：

- [OmniPlay.Tests.csproj](C:\软件\OmniPlay开源-mac\windows\tests\OmniPlay.Tests\OmniPlay.Tests.csproj)

当前已加入的测试主要覆盖：

- 本地路径规范化
- 年份提取
- 剧集识别
- BDMV 标题识别
- 季识别优先级
- 播放进度与总时长持久化

当前测试文件：

- [PlaceholderTests.cs](C:\软件\OmniPlay开源-mac\windows\tests\OmniPlay.Tests\PlaceholderTests.cs)
- [AppRuntimePathsTests.cs](C:\软件\OmniPlay开源-mac\windows\tests\OmniPlay.Tests\AppRuntimePathsTests.cs)
- [LibraryScannerTests.cs](C:\软件\OmniPlay开源-mac\windows\tests\OmniPlay.Tests\LibraryScannerTests.cs)
- [VideoFileRepositoryTests.cs](C:\软件\OmniPlay开源-mac\windows\tests\OmniPlay.Tests\VideoFileRepositoryTests.cs)

后续应优先补充：

- 更复杂的媒体扫描入库测试
- 数据库迁移测试
- 覆盖层播放器与宿主集成测试

## 13. 后续开发阶段

### 阶段 0：工程地基

目标：

- 解决方案可 restore / build / run
- DI、设置、SQLite、基础模型到位

状态：

- 已完成

### 阶段 1：本地媒体库 MVP

目标：

- 本地目录接入
- 最小扫描
- 列表回显

状态：

- 进行中

下一步重点：

- 用真实媒体样本继续验证扫描结果和播放结果
- 搜索与排序
- 扫描边界样本与文案打磨

### 阶段 2：播放器 MVP

目标：

- `libmpv` 真正嵌入 Avalonia
- 播放器页面
- 播放进度保存
- 音轨字幕基础切换

状态：

- 进行中

当前已完成：

- 详情页覆盖层播放器入口
- 播放进度与总时长回写数据库
- 覆盖层与独立窗口专用视频宿主控件接入
- 覆盖层与独立窗口音量控制
- 覆盖层与独立窗口音轨/字幕基础切换
- 覆盖层与独立窗口本地外挂字幕加载
- 命令行播放诊断入口（`--play-file` / `--overlay-play-file` / `--close-after`）
- `OMNIPLAY_APP_ROOT` 运行目录覆盖，便于开发和沙箱环境验证

当前未完成：

- 真机播放验证与稳定性打磨
- 独立播放器页面完善

### 阶段 3：刮削与海报

目标：

- TMDB 查询
- 海报缓存
- 手动修正
- 缩略图

状态：

- 进行中

### 阶段 4：高级功能

目标：

- WebDAV
- 局域网发现
- 独立播放窗口
- 外挂字幕
- 下一集逻辑

状态：

- 进行中

## 14. 接下来最合理的开发顺序

建议继续按这个顺序推进：

1. 增强 `LibraryScanner`
2. 真机验证 `libmpv` 覆盖层与独立窗口播放
3. 补独立播放器页面和播放控制细节
4. 再补 TMDB、海报
5. 接 WebDAV
6. 再做多窗口与外挂字幕

## 15. 工程原则

- UI 不直接写数据库逻辑
- `libmpv` 必须被隔离在独立边界内
- 先保证行为正确，再考虑美化和优化
- 先做本地媒体库，再做 WebDAV
- 先完成播放器宿主验证，再大规模铺功能

## 16. 文档更新原则

后续每次 Windows 版有明显阶段推进时，应同步更新本文档，至少更新：

- 当前可编译状态
- 当前可运行效果
- 当前已完成模块
- 当前已知限制
- 下一阶段目标
