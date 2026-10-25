# Spike 验收

Phase 1 之前必须先拿到两个结论，这两件事任何一个不过，排期就要改法。
CI（`.github/workflows/windows-swift-spike.yml`）已经把下面大部分变成机器判断，
剩下要人眼的只有第 2 节的观感项。

## 1. GRDB 能不能在 Windows 上用（`spike-db`）

`swift run db-spike` 一次验完五件事：`DatabasePool` 打开、两版迁移、中文目录名下的
读写、`PRAGMA journal_mode=wal`、FTS5 检索，外加一次后台写 + 前台读并发。

- 通过：`db-spike: OK platform=windows sqlite=… pool=wal fts5=ok rows=3`
- 不通过：`db-spike: FAIL <原因>`，退出码 1

不通过时的退路（按评估文档 §6）：宿主持库（C# 侧用 Microsoft.Data.Sqlite），核心只做
纯函数；或者核心里放一层极薄的 SQL 封装，不引 GRDB。两条路的代价都是把「查询」搬回宿主，
核心退化成规则层。

## 2. libmpv + WPF 的 airspace（`abi-probe` + 演示窗）

机器部分（`abi-probe`）：核心装载 `libmpv-2.dll`、headless 起 mpv、示例文件时长解析、
进度推进、暂停后时间轴停住、事件回调能到宿主。看到 `abi-probe: OK` 说明边界与播放器都通。

人眼部分，跑 `OmniPlaySpike.exe` 逐条确认：

1. 播放画面出现在灰色区块内，控制条（Popup）浮在画面之上并可点击。
   —— 这条是 airspace 的全部风险：子 HWND 一定盖住 WPF 内容，控制条只有走 Popup
   或独立分层窗口才看得见。
2. 缩放窗口：画面跟着变形不拉伸，控制条不脱离画面。
3. 125% / 150% / 200% 缩放、以及跨不同 DPI 的两台显示器之间拖窗口，画面不糊、不错位。
4. 全屏（F11 或 Win+Shift+F，本演示未实现，正式实现要验）与切回窗口化后画面完整。
   mac 版踩过「退出全屏只剩一角」的坑：mpv 拿到裸图层后不会自己重读尺寸，Windows 侧
   要确认 `gpu` 输出是否同样需要重建 VO。
5. 关掉播放窗再重开，不残留进程、不泄漏 HWND。
6. 中文路径下的视频能放（示例文件就放在 `testdata/`，正式实现要再加一条 SMB/UNC 路径）。
7. 五套配色 + 两种风格切换后，背景、卡面、控制条颜色与 mac 版逐屏比对；差异应只剩
   「真折射的边缘位移」这一项。
8. 自绘玻璃：中间那条玻璃条里的颜色要跟着底下海报的颜色走，切主题时一起变；
   切到「经典」后玻璃层消失、只剩实底卡面。
9. 播放示例后：海报与中间玻璃条被画面盖住（airspace，符合预期），控制条以 Popup 形式
   浮在画面之上且能点。

### 本机（Apple Silicon，Swift 6.4）已跑通

```
swift build                                        Build complete
swift test                                         Executed 35 tests, with 0 failures
python3 scripts/host_smoke.py                      host-smoke: OK
./.build/debug/db-spike                            db-spike: OK platform=macos sqlite=3.54.0 pool=wal fts5=ok rows=3
nm -gU libomniplay-core.dylib                      六个 _omniplay_* 平铺导出齐全
dotnet abi-probe（挂真 dylib）                      abi-probe: OK（播放一段本机没装 libmpv，跳过）
dotnet build windows/OmniPlay.Wpf                  已成功生成（见下）
```

WPF 宿主在 mac 上也能编译期把关：`OmniPlay.Wpf.csproj` 里开了 `EnableWindowsTargeting`，
`dotnet build windows/OmniPlay.Wpf/OmniPlay.Wpf.csproj` 会把 XAML 与 C# 一起过一遍。
这一步省掉两轮 CI——首轮跨目标编译就抓到 `Popup` 上没有的 `AllowsTopMost`、
`LinearGradientBrush` 的错构造，以及 `BrushMappingMode`/`TileMode` 被写成
`MappingMode`/`BrushTileMode`。注意它只验编译，不验观感：`VisualBrush` 采样、airspace
仍要 Windows 上人眼确认。

C# 探针在 mac 上也能干跑：`DllImport("omniplay-core")` 会去找 `libomniplay-core.dylib`，
所以除了「平台自报」和「播放」两条，其余检查与 Windows 上完全同一份代码。

## 2b. 打包（`.github/workflows/windows-package.yml`）

手动触发即可。产物：`*setup.exe`（Inno 安装包，自包含，不依赖已装的 .NET）、`*.zip`（便携）、
`.sha256`。流程在 `windows/package.ps1`：构建后先跑一遍 `abi-probe`，探针不过就不出包；
libmpv 取不到只降级、不阻断。装完按第 2 节那九条看。libmpv 从 shinchiro 的 GitHub Release 取
（SourceForge 那个老地址在 CI 里回回 403），`Ensure-LibMpv` 会先校验 7z 魔数再解压，
`LIBMPV_URL` 可以用分号写多个候选。

发布目录里除 `omniplay-core.dll` 外还会躺一串 Swift 运行时 DLL：核心在 Windows 上是动态链接
`swiftCore` 的，`Copy-SwiftRuntime` 顺着 `omniplay-core.dll` 的 PE 导入表把工具链 `usr\bin`
里需要的几个拷进来（顺带补 `VCRUNTIME140`/`msvcp140`，Swift 安装器只在构建机装过 redist）。
少了这一步，包在自己机器上能跑、换台机器就 `DllNotFoundException`。

Swift 6.4 的新构建引擎（swiftbuild）在 GRDB 的 C 目标上会报「Multiple input files」，
本地跑探针要加 `--build-system native`；CI 那步已经写了这条退路。

## 2c. Windows 上的 Swift 工具链

两个 Windows job 都不再直接写 `compnerd/gha-setup-swift`，改成 `uses: ./.github/actions/setup-swift`：
版本只在该 action 的两个 default 里，装完立刻做一次 `swiftc -typecheck`（内容只有 `import Foundation`），
不过就带 `update-sdk-modules: true` 重装再验一次。

为什么要有这一步：首轮 CI 用 6.0.3，`swift build` 在编译核心时炸在

```
error: cyclic dependency in module 'ucrt': ucrt -> _Builtin_intrinsics -> ucrt
error: could not build module '_Builtin_intrinsics'   (intrin.h -> x86intrin.h -> mm_malloc.h -> stdlib.h)
error: could not build C module 'SwiftOverlayShims'   (LibcOverlayShims.h -> errno.h)
```

看着像业务代码的错，其实是工具链自带 modulemap 里有环：ucrt 依赖 `_Builtin_intrinsics`，
而它的 `mm_malloc.h` 又 include 属于 ucrt 的 `stdlib.h`。`update-sdk-modules` 会拿 swiftlang
仓库里的 modulemap 覆盖安装包里那套（ucrt / vcruntime / WinSDK / clang 资源目录），环就断了；
升级到 6.1+ 同样绕开——6.1 起 SDK 用 `SwiftUCRT.h` 把这两块解耦。现在两道保险都上：
先试 6.2.1，环没断就换 modulemap 再来。

## 3. 排序一致性

`Tests/OmniPlayCoreTests/HomeSortTests.swift` 里的拼音序断言在两个平台都必须绿。
Windows 上若只有这条红，说明 ICU 排序与 mac 的 `localizedStandardCompare` 不一致，
解决办法是在核心里注册自定义 collator（GRDB 支持），或者把排序权重表算成纯 Swift 数据，
不再依赖平台 collation。

## 4. 已知风险

- 安装包向导目前是英文（Inno 的 Default.isl）：简体中文 isl 不属于官方安装包，
  想要中文界面要么随包带上 ChineseSimplified.isl，要么把 [Setup] 换成 Inno Setup
  Wishlist 分支。应用本体是中文的，这里先不折腾。

- `abi-probe` 在 Windows 上把每项检查都跑绿、打印完 `abi-probe: OK` 之后，进程仍可能带
  `0xC0000409` 退出——卸载 Swift 运行时的收尾顺序问题，不是功能失败。`package.ps1` 因此以
  探针打的结论行为准，退出码异常只告警。正式实现要在自己的退出路径上验一遍（mac 那边
  ⌘Q 不落盘是同一类问题：收尾时机）。
- Windows 上 Swift 运行时 DLL 不在编译器旁边的 `Toolchains\<版本>\usr\bin`，而在
  `SDKROOT\usr\bin`。`Find-SwiftRuntimeDir` 两处都找，再退回扫 PATH。

- 工具链由 `.github/actions/setup-swift` 从 swift.org 官方安装包静默安装（不是自建发行版），
  换版本改那里的 default；换 modulemap 的退路也在那里。
- WPF 的 `HwndHost` 在窗口最小化/最大化时可能重建子窗口，届时 `wid` 要重新交给 mpv。
- 核心的 `PlayerCore` 目前是单实例状态机，与 mac 版一致；多路播放（预览小窗）要另想。
- 自绘玻璃用 VisualBrush 采本窗口内容，源子树每帧会被渲染两遍：正式实现里要按板块大小限制
  采样分辨率（先降采样再模糊），并让玻璃板在播放页避开视频区，否则 4K 播放会看到帧率掉。
- 采不到内容时要留降级：Popup 里的板与窗口不同视觉树，拿不到变换就退回纯染色玻璃，
  演示里已走过这条路，正式实现别删。
