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
swift build                        Build complete
swift test                         Executed 35 tests, with 0 failures
python3 scripts/host_smoke.py      host-smoke: OK
./.build/debug/db-spike            db-spike: OK platform=macos sqlite=3.54.0 pool=wal fts5=ok rows=3
nm -gU libomniplay-core.dylib      六个 _omniplay_* 平铺导出齐全
dotnet abi-probe（挂真 dylib）      abi-probe: OK（播放一段本机没装 libmpv，跳过）
```

C# 探针在 mac 上也能干跑：`DllImport("omniplay-core")` 会去找 `libomniplay-core.dylib`，
所以除了「平台自报」和「播放」两条，其余检查与 Windows 上完全同一份代码。

## 2b. 打包（`.github/workflows/windows-package.yml`）

手动触发即可。产物：`*setup.exe`（Inno 安装包，自包含，不依赖已装的 .NET）、`*.zip`（便携）、
`.sha256`。流程在 `windows/package.ps1`：构建后先跑一遍 `abi-probe`，探针不过就不出包；
libmpv 取不到只降级、不阻断。装完按第 2 节那九条看。

Swift 6.4 的新构建引擎（swiftbuild）在 GRDB 的 C 目标上会报「Multiple input files」，
本地跑探针要加 `--build-system native`；CI 那步已经写了这条退路。

## 3. 排序一致性

`Tests/OmniPlayCoreTests/HomeSortTests.swift` 里的拼音序断言在两个平台都必须绿。
Windows 上若只有这条红，说明 ICU 排序与 mac 的 `localizedStandardCompare` 不一致，
解决办法是在核心里注册自定义 collator（GRDB 支持），或者把排序权重表算成纯 Swift 数据，
不再依赖平台 collation。

## 4. 已知风险

- `compnerd/gha-setup-swift` 装的发行版与稳定版有差异，正式打包前换成官方安装包。
- WPF 的 `HwndHost` 在窗口最小化/最大化时可能重建子窗口，届时 `wid` 要重新交给 mpv。
- 核心的 `PlayerCore` 目前是单实例状态机，与 mac 版一致；多路播放（预览小窗）要另想。
- 自绘玻璃用 VisualBrush 采本窗口内容，源子树每帧会被渲染两遍：正式实现里要按板块大小限制
  采样分辨率（先降采样再模糊），并让玻璃板在播放页避开视频区，否则 4K 播放会看到帧率掉。
- 采不到内容时要留降级：Popup 里的板与窗口不同视觉树，拿不到变换就退回纯染色玻璃，
  演示里已走过这条路，正式实现别删。
