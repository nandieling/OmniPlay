# OmniPlay Windows 开发说明

`windows/` 是 OmniPlay 的 Windows 移植版工程目录。当前技术栈为 `Avalonia + .NET 10 + SQLite + libmpv`。

## 当前状态

已完成：

- Windows 多项目解决方案骨架
- 主窗口、媒体库、详情页、继续观看闭环
- 覆盖层播放器与独立播放器窗口
- 覆盖层与独立窗口音量控制、全屏、音轨/字幕切换
- 覆盖层与独立窗口加载本地外挂字幕
- SQLite 初始化与本地运行目录管理
- 本地目录扫描、剧集分流、失效文件清理
- BDMV 主片段筛选与重复路径收敛
- 最近一次扫描摘要与媒体源诊断信息
- `.slnx` 方案级 restore/build/test
- 扫描后基础 TMDB 元数据补全
- TMDB 海报下载到本地 `posters/` 并回写 SQLite
- 详情页 TMDB 手动重匹配、锁定和候选结果应用
- WebDAV 媒体源表单、连通性测试、递归扫描与带认证播放链路
- 本地媒体源后续更换目录，方便盘符或目录迁移后修正路径

仍在进行中：

- `libmpv` 真机播放细节打磨
- 剧照/缩略图链路
- WebDAV 更复杂认证与远程媒体源稳定性
- 播放器交互细节继续补齐

## 目录结构

```text
windows/
  src/
    OmniPlay.Core/
    OmniPlay.Desktop/
    OmniPlay.Infrastructure/
    OmniPlay.Player.Mpv/
    OmniPlay.UI/
  tests/
    OmniPlay.Tests/
  dev-build.ps1
  dev-run.ps1
  OmniPlay.Windows.slnx
```

## 构建入口

Windows 版当前统一以 [OmniPlay.Windows.slnx](C:/软件/OmniPlay开源-mac/windows/OmniPlay.Windows.slnx) 作为方案入口。

这里的 “`.slnx` 构建链” 指的是：

1. `dotnet restore .\windows\OmniPlay.Windows.slnx`
2. `dotnet build .\windows\OmniPlay.Windows.slnx ...`
3. `dotnet test .\windows\OmniPlay.Windows.slnx ...`

当前 CLI 默认多节点调度下，裸跑 `dotnet build .\windows\OmniPlay.Windows.slnx -c Debug` 仍可能失败。稳定做法是显式加 `-m:1`，或者直接使用仓库内脚本。

## 推荐命令

### PowerShell

```powershell
cd C:\软件\OmniPlay开源-mac
$env:DOTNET_CLI_HOME='C:\软件\OmniPlay开源-mac\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'

dotnet restore .\windows\OmniPlay.Windows.slnx
dotnet build .\windows\OmniPlay.Windows.slnx -c Debug -m:1
dotnet test .\windows\OmniPlay.Windows.slnx -c Debug -m:1
dotnet run --project .\windows\src\OmniPlay.Desktop\OmniPlay.Desktop.csproj
```

### `cmd.exe`

```cmd
cd /d C:\软件\OmniPlay开源-mac
set DOTNET_CLI_HOME=C:\软件\OmniPlay开源-mac\.dotnet
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_NOLOGO=1

dotnet restore .\windows\OmniPlay.Windows.slnx
dotnet build .\windows\OmniPlay.Windows.slnx -c Debug -m:1
dotnet test .\windows\OmniPlay.Windows.slnx -c Debug -m:1
dotnet run --project .\windows\src\OmniPlay.Desktop\OmniPlay.Desktop.csproj
```

## 推荐脚本

本地 PowerShell 若受执行策略限制，请使用 `-ExecutionPolicy Bypass`。

### 构建

```powershell
C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe `
  -ExecutionPolicy Bypass `
  -File .\windows\dev-build.ps1
```

`dev-build.ps1` 当前会：

- 设置 `DOTNET_CLI_HOME`
- 对 `.slnx` 做 restore
- 用 `-m:1` 进行稳定 build
- 用 `-m:1` 运行测试

### 运行

```powershell
C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe `
  -ExecutionPolicy Bypass `
  -File .\windows\dev-run.ps1
```

`dev-run.ps1` 用于启动桌面程序，并保留常用开发环境变量。

## 日志与运行目录

应用根目录按以下顺序解析：

1. `OMNIPLAY_APP_ROOT`
2. `%LOCALAPPDATA%\OmniPlay`
3. `%TEMP%\OmniPlay`

默认日志文件：

```text
%LOCALAPPDATA%\OmniPlay\logs\app.log
```

如果 `%LOCALAPPDATA%` 不可写，程序会自动回退到：

```text
%TEMP%\OmniPlay\logs\app.log
```

建议开发/诊断时显式指定根目录：

```powershell
$env:OMNIPLAY_APP_ROOT='C:\软件\OmniPlay开源-mac\windows\tmp\runtime-dev'
```

### 实时看日志

```powershell
Get-Content "$env:LOCALAPPDATA\OmniPlay\logs\app.log" -Wait
```

如果当前进程走了 `%TEMP%` 回退目录，请改看：

```powershell
Get-Content "$env:TEMP\OmniPlay\logs\app.log" -Wait
```

## 播放诊断

### 独立播放器窗口

```powershell
$env:OMNIPLAY_APP_ROOT='C:\软件\OmniPlay开源-mac\windows\tmp\runtime-standalone-verify'
dotnet .\windows\src\OmniPlay.Desktop\bin\Debug\net10.0\OmniPlay.Desktop.dll `
  --play-file "C:\软件\OmniPlay开源-mac\windows\tmp\playback-smoke.wav" `
  --close-after 6
```

### 详情页覆盖层

```powershell
$env:OMNIPLAY_APP_ROOT='C:\软件\OmniPlay开源-mac\windows\tmp\runtime-overlay-verify'
dotnet .\windows\src\OmniPlay.Desktop\bin\Debug\net10.0\OmniPlay.Desktop.dll `
  --overlay-play-file "C:\软件\OmniPlay开源-mac\windows\tmp\playback-smoke.wav" `
  --close-after 6
```

这两条命令会把播放器阶段快照写入对应 `logs\app.log`，用于验证 `libmpv + 宿主控件 + 状态轮询` 链路。

## TMDB 配置

当前 Windows 端已经接入“扫描后基础刮削”：

- 电影：补标题、发布日期、简介、评分、本地海报
- 剧集：补标题、首播日期、简介、评分、本地海报

应用启动并加载已有媒体库后，也会自动尝试回填缺失的 TMDB 元数据与海报，不必先删库重扫。

默认直接使用仓库当前内置的 TMDB API key。也可以通过环境变量覆盖：

```powershell
$env:OMNIPLAY_TMDB_API_KEY='your-api-key'
$env:OMNIPLAY_TMDB_ACCESS_TOKEN='your-tmdb-read-access-token'
$env:OMNIPLAY_TMDB_LANGUAGE='zh-CN'
```

说明：

- 若同时设置 `OMNIPLAY_TMDB_ACCESS_TOKEN`，请求会优先走 Bearer Token。
- 设置页现在可以直接保存 TMDB 语言；保存后的设置优先于环境变量。
- 设置页现在也支持自动元数据补全、自动海报下载和自动剧照下载三个开关。
- 若未保存语言设置，则继续回退到 `OMNIPLAY_TMDB_LANGUAGE` / `TMDB_LANGUAGE`，最终默认 `zh-CN`。
- 下载后的海报会落到 `posters\` 目录，并把本地绝对路径写回数据库。
- 补全过程会在主界面状态栏显示结果，例如已补全多少电影、剧集以及下载了多少张海报。

## 数据目录

SQLite 数据库默认位置：

```text
%LOCALAPPDATA%\OmniPlay\data\omniplay.sqlite
```

海报缓存默认位置：

```text
%LOCALAPPDATA%\OmniPlay\posters\
```

缩略图缓存默认位置：

```text
%LOCALAPPDATA%\OmniPlay\thumbnails\
```

## 离线环境说明

当前环境如果无法访问 `https://api.nuget.org/v3/index.json`，构建时可能出现 `NU1900` 警告。这个警告在离线开发环境下是预期现象，不会阻塞当前方案构建。

## 已知事项

- 当前目标框架是 `net10.0`，因为现有开发机安装的是 `.NET 10 SDK`。
- `dotnet build .\windows\OmniPlay.Windows.slnx -c Debug` 默认不加 `-m:1` 仍不稳定。
- 稳定入口优先使用 [dev-build.ps1](C:/软件/OmniPlay开源-mac/windows/dev-build.ps1) 或显式 `-m:1`。
- 现阶段 TMDB 已支持自动补全、手动重匹配、锁定、语言设置和基础自动刮削控制；更细的规则仍待补齐。

## 相关文件

- 方案入口：[OmniPlay.Windows.slnx](C:/软件/OmniPlay开源-mac/windows/OmniPlay.Windows.slnx)
- 构建脚本：[dev-build.ps1](C:/软件/OmniPlay开源-mac/windows/dev-build.ps1)
- 运行脚本：[dev-run.ps1](C:/软件/OmniPlay开源-mac/windows/dev-run.ps1)
- 开发计划：[WINDOWS_PORT_DEVELOPMENT_PLAN.md](C:/软件/OmniPlay开源-mac/windows/WINDOWS_PORT_DEVELOPMENT_PLAN.md)
