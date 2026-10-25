# 觅影 Windows 版（Swift 核心 + WPF）

这是 Windows 移植的新骨架，与 `../OmniPlay-win`（C# + Avalonia 那一版）并存，互不覆盖。
路线来自两份评估：`../OmniPlay-win/2026-10-24-swift-rewrite-assessment-and-plan.md`
与 `../OmniPlay-win/2026-10-24-swift-core-wpf-assessment.md`。

思路是把 mac 版的规则层（排序、分组、主题配方、播放器状态机）搬进一个零依赖的 Swift 包，
用 C ABI + JSON 契约暴露出去；Windows 侧只做界面与系统集成。mac 与 Windows 共用同一套
判断，两份界面就不会各自漂一套规则。

两条已定的方案：玻璃观感走**自绘**（VisualBrush 采本窗口内容 + 高斯模糊，Mica/Acrylic 采不到
窗口内容，做不出海报墙滚动时玻璃跟着变色的效果）；与现有 C#/Avalonia 版**并存**，新版功能对齐
两个版本后再决定是否替换。

## 目录

```
Package.swift                零依赖核心包（只用 Foundation）
Sources/OmniPlayCore/        规则层：主题配方、首页排序与分组、JSON、播放器状态机
Sources/OmniPlayCore/Platform/   宿主注入的钩子（密钥、数据目录等）
Sources/OmniPlayBridge/      @_cdecl 导出，六个 C 函数
Tests/OmniPlayCoreTests/     契约与规则锁，35 项
spike-db/                    GRDB on Windows 的独立探针（它红了不牵连核心包）
scripts/host_smoke.py        用 ctypes 假扮 WPF，走一遍跨语言边界
windows/OmniPlay.Wpf/        WPF 演示窗：主题、播放子 HWND、Popup 控制条
windows/abi-probe/           控制台探针，CI 拿它的退出码当结论
windows/build.ps1            一条命令：核心 -> 探针 -> 演示 -> 跑探针
docs/SPIKE.md                验收清单与已知风险
```

## 本机（macOS）

```bash
swift build                      # 核心库
swift test                       # 35 项契约与规则测试
python3 scripts/host_smoke.py    # 跨语言边界冒烟
cd spike-db && swift run db-spike
```

## 出安装包

```powershell
pwsh windows/package.ps1 -Version 0.1.0     # 自包含发布 + Inno 安装包 + 便携 zip + sha256
```

CI 里对应的是一 [Windows 打包](../.github/workflows/windows-package.yml)：手动触发即可拿产物，
打 `win-v*` 标签会顺手发 Release。产物在 Actions 页面的 `windows-<版本号>` 制品里：
`OmniPlay-win-swift-<版本>-x64-setup.exe`（安装包）与 `-portable.zip`（解压即用）。
安装包不依赖已装的 .NET，libmpv 会一起打进去；拿不到 libmpv 时只影响播放，界面照装。

## Windows

前置：Swift 6 工具链、.NET 8 SDK、Visual C++ 生成工具（WPF 编译要）。libmpv 可选，
没有也能起界面，只是播放器显示「不可用」。

```powershell
pwsh windows/build.ps1                       # 会自动取 libmpv（-LibMpvUrl 或 windows/native/）
pwsh windows/build.ps1 -Release
```

跑完先确认 `abi-probe: OK`，再双击它打印出来的 `OmniPlaySpike.exe`，按
`docs/SPIKE.md` 的清单逐条看。

## 边界规则

- 只传 JSON，不传 Swift 对象；请求 `{method, params}`，响应 `{ok, result|error}`。
- 错误信息是给人看的一句话，界面直接显示，不做本地化映射。
- 响应缓冲由核心分配，宿主必须用 `omniplay_free_bytes` 归还。
- 事件由核心推送（`omniplay_set_event_callback`），界面不轮询；耗时动作先回「已受理」。
- 颜色一律 `#AARRGGBB`，由核心算完；界面不参与色彩混合。
- 库名带 `-core`：Windows 文件名不分大小写，避免和界面程序集撞名。
