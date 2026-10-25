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

**2026-10-25 Windows 实测：不通过。** 不是运行时不行，是编译就起不来——GRDB 的
`GRDBSQLite` C 目标在 Windows 上找不到 `sqlite3.h`（它只在 Apple 平台上用系统 SQLite）。
退路按优先级：① 自带 SQLite 合并源码（vendor `sqlite3.c`，走 GRDB 的 custom SQLite build
用法，改动只在依赖声明上）；② 核心里放一层极薄的 SQL 封装；③ 宿主持库，核心退成规则层。

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

## 2d. 1.9 装到用户机器上直接闪退

第一版包只在构建机上验过，而 `abi-probe` 是控制台程序——WPF 从没真起过，所以「双击就没了」
没有现成的原因可查。补上 `--smoke` 界面冒烟后一次就抓到了：

```
界面搭建失败：InvalidOperationException：创建播放子窗口失败：1407
   at OmniPlay.WpfSpike.VideoHost.BuildWindowCore(HandleRef hwndParent)
```

1407 是「找不到窗口类」。`VideoHost` 用 `private static readonly ushort ClassAtom = Register();`
注册窗口类，但类里没有静态构造，类型就是 `beforefieldinit`：CLR 只保证「第一次读静态字段之前」
完成初始化，而这段代码从头到尾没人读 `ClassAtom`，于是 `Register()` 根本没跑过，
`CreateWindowExW` 拿着一个没注册过的类名失败。改成显式 `static VideoHost()` 后好了。
依赖不是原因——冒烟日志里 `核心 omniplay-core.dll 装载成功`、`本机自检：Swift 运行时
13 个 DLL 依赖齐` 都在。

顺带留下的东西：

- 宿主开机先自检：按全路径装载一次 `omniplay-core.dll`，失败就把 Win32 错误码翻译成人话，
  并顺着导入表指出缺哪个 DLL；全程写进 `%LOCALAPPDATA%\OmniPlay\startup.log`，
  界面任何一处炸掉都会弹窗并给出这个路径，不再无声退出。
- MSVC 运行库（`vcruntime`/`msvcp`/`concrt`）从应用目录挪进 `redist\`。摆在应用目录会盖住
  本机已装的那份，而 WPF 的原生组件同样吃这套；现在只有核心第一次装载报 126 时，
  才把 `redist\` 补进搜索路径重试一次。
- 打包多了两道闸：`Test-ShippedImports` 把包里每个原生 DLL 的导入过一遍，包内和 System32
  都没有就别想出货（`libmpv-2.dll` 的可选图形依赖只警告）；`OmniPlaySpike.exe --smoke` 在
  PATH 只剩系统目录的情况下真起一次界面，带 20 秒看门狗，装载类问题直接拦下出包。
- `libmpv-2.dll` 额外要 `vulkan-1.dll`（随显卡驱动来），Server 镜像上没有，这也是探针里
  126 的来源——装载失败会显示在界面上，不影响启动。

## 2e. 接上片库后又闪退（2026-10-25 第二轮）

现象：装机后点开没影。构建机的冒烟是绿的，差异只能在用户机器上——所以先把「崩了也
得留下字」做实，再谈原因：

- 原生崩溃不走 .NET 异常管道（访问违例、栈溢出这类），进程直接没了。现在挂了
  `SetUnhandledExceptionFilter`，把异常码、出错地址、那块地址属于哪个 DLL 写进启动日志。
- 启动日志同时写 `%LOCALAPPDATA%\OmniPlay\startup.log` 与 `%TEMP%\OmniPlay-startup.log`。
  AppData 被同步盘接管、目录只读、杀软拦写入这类机器上，只剩 TEMP 那份能读。
- 建版分阶段留标记：`顶栏完成` / `海报墙与导航完成` / `界面搭建完成` / `片库：…`。
  日志停在哪一行，问题就在下一步。
- 库这一层不再能拖垮窗口：先搭壳，`Background` 优先级再读库，失败只记一笔。
  WAL 在接管盘上开不起来就退回默认日志模式；AppData 打不开就在临时目录另建一份，
  原因作为 `library.location` 事件推给界面念出来。
- `--no-library` 只起壳不碰库，用来二分：能开 = 问题在数据层，连这个都开不了 = 装载或建版。
- CI 冒烟两处日志都认，并把「片库读取失败」「原生崩溃」当失败——绿包不再等于蒙过去。


### 用户机器上的日志说停在哪儿

实机日志（`C:\Users\nan\...`，纯 ASCII 路径）走到 `界面搭建完成` 就断，后面既没有
`原生崩溃` 也没有 `退出（0）`：进程是被硬终止的，不走 .NET 异常管道（`__fastfail` 这类
连 `SetUnhandledExceptionFilter` 都不经过）。断点在首帧渲染与读库之间，两者都还没被标记分开。

于是补一条分隔线和一个自愈：

- `ContentRendered` 打一行 `界面：第一帧已画出`。日志停在这行之前 = 死在渲染（多半是显卡驱动
  撂挑子），停在之后 = 死在片库那一层。`片库：开始读取` 再切一刀。
- 下次启动先读上一份日志：上次没画到首帧 → 本次自动 `RenderOptions.ProcessRenderMode =
  RenderMode.SoftwareOnly`；上次画到首帧但没读到库 → 本次自动跳过片库。手动开关是
  `--soft-render`（或环境变量 `OMNIPLAY_SOFT_RENDER=1`）与 `--no-library`。
- 采样区尺寸为 0 时不再挂高斯模糊：那是块非法渲染目标。

### 第三刀：死在核心调用里，而且是无痕迹硬死

第二轮的实机日志（软件渲染生效后）走到 `片库：开始读取` 就断：首帧画出来了，
死在 `omniplay_invoke("library.sections")` 里面，仍然没有 `原生崩溃`，也没有
`片库读取失败`（托管 catch 没接住）——说明是 `__fastfail` / 栈溢出这类连 unhandled
filter 都不经过的死法。两个动作：

- 核心自己报阶段：`开库 → 建表与建索引 → 查库统计 → 铺演示数据 → 读分组与排序`，
  宿主在事件回调里**同步**落盘。日志最后一条阶段就是断点，不用再猜。
- 默认不再开 WAL。单机单进程，WAL 换来的并发收益很小，却要引入 `-shm` 这个内存映射
  文件——杀软、透明加密、同步盘这类文件系统过滤驱动最爱在映射文件上把进程直接打没。
  改成回滚日志（`journal_mode=DELETE`），要 WAL 拿 `OMNIPLAY_WAL=1` 打开。
- 崩溃兜底再加一层向量异常处理器（第一手）：只认非法指令（Swift fatalError 走这条）、
  `__fastfail`、栈溢出、边界检查失败这几种码，报一次，带模块名。

还抓不到的话，Windows 事件日志里那条 Application Error 直接给答案（异常码 + 出错模块）：

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'} -MaxEvents 40 |
  Where-Object { $_.Message -match 'OmniPlaySpike' } |
  Select-Object -First 3 TimeCreated, Message | Format-List
```


## 2f. 构建机自己复现了：玻璃板问错了人要坐标（2026-10-25 第三轮）

加了阶段事件那一版跑 CI，冒烟自己炸了，而且炸得明明白白：

```
Fatal error. Internal CLR error. (0x80131506)
   at System.Windows.Media.Visual.TransformToVisual(System.Windows.Media.Visual)
   at OmniPlay.WpfSpike.GlassPlate.RefreshSample()
   at OmniPlay.WpfSpike.GlassPlate..ctor(...)
   at SpikeWindow.EnsurePlates(ThemeSpec)  ←  OnLoaded 里第一次应用主题时
```

`GlassPlate` 构造函数末尾就调 `RefreshSample()`，可那会儿板子还没 `Children.Add` 进窗口：
一个没有组合目标的视觉去问另一个视觉要坐标，milcore 返回 `E_UNEXPECTED`，CLR 把它
升格成 **Internal CLR error**——这不是异常，`catch (Exception)` 拦不住，进程当场消失，
日志就停在最后一行。用户端「点开没影、什么提示都没有」和构建机「日志断在
`界面搭建完成`」是同一件事：那一步正是 `OnLoaded → ApplyTheme → EnsurePlates`。

- 采样改到 `Loaded` 之后做，并且在问变换之前先确认两边都还挂在窗口上
  （`VisualTreeHelper.GetParent` + `PresentationSource.FromVisual`）。拿不到就退回纯染色玻璃。
- 冒烟门禁补两条：stderr 里有 `Fatal error|Internal CLR error|Unhandled exception` 拦下；
  日志写了 `界面搭建完成` 却没写收尾的 `退出（`（= 中途被硬带走）也拦下。之前退出码
  非零只告警，这一类炸法就是这么漏过去的。
- 顺带：`0x80131506` 之外，访问违例现在也记进启动日志，并且写**模块+偏移**
  （`omniplay-core.dll+0xF8770`）。链接器加了 `/MAP`，符号表随产物一起上传
  （`windows-<版本>-symbols`），偏移一查就是函数名，不用等现场复现。
- 再加一条外来模块清单：进程里既不在包里也不在 `C:\Windows` 下的 DLL 全部列出来。
  `Foundation.dll`、`icu*.dll` 这类通用名字最容易被输入法、杀软、网盘的同名模块抢走，
  「构建机上好好的」多半就是栽在这儿；核心装载前先按全路径把包内那 13 个抢下来。
- 片库这条路的自愈改成有账本：`crash-state.txt` 记连续死了几趟，两趟都断在读库里
  就不再碰它（`OMNIPLAY_LIBRARY=1` 强行再试），宁可海报是空的。


## 2g. 不闪退了，改成"关窗后挂住"（2026-10-25 第四轮）

玻璃那处修好后 CI 冒烟不再秒死，但包还是被拦：进程 90 秒不退出。日志这回走完了全程：

```
片库：阶段 开库 … → 建表与建索引 → 查库统计 → 铺演示数据 → 读分组与排序
片库：片库 12 部 · 文件 12 · 已看 3 · 可续播 5
片库：读取完成 → 冒烟：界面活着 → 窗口已关闭，核心已释放      ← 然后没有然后
```

数据层、渲染、外来模块（零）全部清白，`wermgr` 也没在跑，所以不是「已停止工作」弹窗
在吊着。断点在 `OnClosed` 之后、`OnExit` 之前——正是 WPF 渲染线程退出、AppDomain 卸载、
Swift 运行时 DLL 的 `DllMain` 收尾那一段，而 §4 早就记过这组 DLL 的收尾顺序会 fastfail：
以前靠"退出前没人调用 FreeLibrary， unload 根本没发生"绕开，这一轮给核心的 13 个运行时
DLL 做了按全路径预装载，它们从"核心的依赖"变成"进程自己装载的模块"，卸载时真的会走
`DLL_PROCESS_CLOSE`，收尾问题就摆到台面上了。三个动作：

- 冒烟收尾改成关窗而不是 `Application.Shutdown()`：`Shutdown` 一定走到 AppDomain 卸载。
- `OnClosed` 里武装一个 3 秒兜底线程：窗口一关，无论 WPF 收尾走到哪一步，3 秒后
  `TerminateProcess`。库和日志此时都已落盘，等它没有意义（用户端等价于"点关闭后
  进程还挂着"）。
- `SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX)` + `WerAddExcludedApplication`：
  出事时不再摆「已停止工作」对话框。那个框会把进程吊在那儿等点击——无人值守的机器上
  就是干等，用户那边也是点了才没影。原因我们本来就已经写进启动日志。

顺带一条与本次卡顿无关的降级：玻璃采样只在有显卡加速时做（`RenderCapability.Tier > 0`）。
VisualBrush 要把被采样的子树重画一遍再高斯模糊，虚拟机、基础显示驱动、远程桌面这些
纯软件渲染下这笔开销不划算，退回纯染色玻璃——构建机报的是显卡加速，所以本轮卡顿不是
它造成的，别把两件事混成一个结论。


## 2h. 装了新包点开没反应（2026-10-25 第五轮）

实机日志停在一行新的地方，比之前都短：

```
主窗口已创建        ← 到此为止
```

`界面：顶栏完成` 那批标记都在 `OnLoaded` 里，也就是说**窗口构造函数之后、Loaded 之前**
就没动静了：既没有 `原生崩溃`，也没有 `退出（0）`——进程不是死了，是被吊住了。嫌疑就在这
一轮自己加的调试设施上：向量异常处理器开始对**访问违例**动手，而 CLR、WPF、显卡驱动都拿
访问违例当正常控制流用；在第一手处理器里写文件，等于在别人持锁的中间插一段磁盘 IO，装了
杀软/输入法钩子的机器上正好能把线程钉住。构建机没有这些过滤驱动，所以那边一路绿灯。

三件事：

- 诊断拆成两道：**未处理过滤器**里全报（进程确定要死，怎么折腾都行），**向量处理器**只对
  `__fastfail`、栈溢出、非法指令、边界检查失败这种「一定是死」的码动手。访问违例退出第一手。
  WER 排除也从 `WerAddExcludedApplication` 改成直接写 HKCU，少一次说不清的内部调用。
- 「点开没反应」必须在日志里能分身份：窗口构造一进来就写 `界面：窗口开始构造`，另有 8 秒
  看门狗（普通线程，不信界面线程）在没等到首帧时写「进程还活着，卡在构造或首帧渲染里」。
  有这行 = 卡住，没这行 = 死了，不用再靠任务管理器猜。
- 判定条件放宽成「有 `主窗口已创建` 但没 `第一帧`」，命中就自动软件渲染 **并** 关掉玻璃采样与
  模糊；手工档是 `--no-glass` / `OMNIPLAY_NO_GLASS=1`（玻璃板的位置、圆角、染色、描边都留着，
  只是不采样不模糊不投影）。CI 现在跑两趟冒烟，第二趟专走 `--no-glass --no-library`，
  给用户留的排障档得先自己能用。


## 2i. 软件渲染 + 无玻璃还是不起：把断点交给日志（2026-10-25 第六轮）

第五轮的包带了自愈，日志证明自愈生效了，也证明玻璃不是原因：

```
上次没画到首帧：本次软件渲染 + 关掉玻璃采样与模糊
主窗口已创建 -> 界面：窗口开始构造
界面：8 秒没画出一帧——进程还活着，卡在构造或首帧渲染里      x2
```

两条看门狗行相隔 7 秒 = 用户点了两次图标，两个进程都活着、都没有窗口。断点在
`窗口开始构造` 之后、`Loaded` 之前，也就是 XAML 解析到首帧渲染这一段。XAML 本身很干净
（没 Effect、没图片、没外部资源），剩下的嫌疑都是系统调用：加载器锁、DirectWrite 字体
枚举、DWM/主题。而前几轮里唯一动过加载器时序的，就是"按全路径预装载 13 个 Swift 运行时
DLL"那道保险。这一轮：

- 撤掉预装载，只装载核心一个，运行时由加载器按依赖自己拉；改成**事后核对来源**
  （`GetModuleHandleEx` + `GetModuleFileNameW`）。同名劫持照样看得见，不再为了防它去动时序。
- 阶段标记统一成 `StartupLog.Mark`，看门狗直接报「停在哪个阶段」。窗口这条路上有：
  窗口开始构造 / 字体回源 / XAML 解析完 / 窗口句柄已建好 / 顶栏完成 / 海报墙完成 /
  界面搭建完成 / 第一帧已画出。下一份日志自带断点。
- 字体这道坎单独标：构造里先用 `FormattedText` 走一次中文标题的字形回源。中文机器上一款
  坏字体能让 DirectWrite 转很久，这是 WPF 首帧前最经典的一种「没反应」。
- 新增 `--probe`：只跑核心（装载、theme.get、开库、分组），一个窗口都不建，最后一行写
  「核心这一层全通，卡点在 WPF 建窗」。窗口不再由 `StartupUri` 建，探针才绕得开 WPF。
  CI 冒烟因此跑三趟，第三趟就是探针。
- 启动时报告同名实例：界面上没窗，用户会再点一次，日志就被两趟写在一起。现在有
  「同一个程序还有 N 个实例在跑（PID …）」这行，看日志的人不用猜。

## 2j. 卡在 Show() 里：两个探针把它切开（2026-10-25 第七轮）

实机日志第一次给出精确位置：

```
阶段：XAML 解析完，等源就绪        ← 构造函数走完了
界面：8 秒没画出一帧——进程还活着，停在「XAML 解析完，等源就绪」
```

`window.Show()` 没返回，`SourceInitialized` 也没来：卡点在 WPF 建窗口源这一步。另外这趟
**没触发自愈**——上一版的判定读的是日志里的旧标记名，我把标记改名之后判定静悄悄失效了。

- 阶段单独记到 `%LOCALAPPDATA%\OmniPlay\phase.txt`，自愈只读它，不看日志文案；收尾时写
  `已退出`，正常结束过的趟次一律不降级。
- 看门狗发现卡住就顺手 `MiniDumpWriteDump` 存一份现场（`hang-*.dmp`），dbghelp 提前装载。
  连 dmp 都写不出来也是一种答案：说明卡点正抱着加载器锁或陷在内核调用里。
- 加 `--min-wpf`：裸 `Window` + 一个 `TextBlock`，没有 XAML、没有玻璃、没有海报墙。它出得来
  而主窗口起不来，问题在我们自己的内容；它也卡住，就是 WPF 初始化本身。
- 加 `--win32-probe`：完全不碰 WPF，`RegisterClassEx` + `CreateWindowEx` + `ShowWindow` +
  泵 8 秒消息，最后弹一个 `MessageBoxW` 问用户看没看见。它把「这台机器让不让这个进程建窗」
  和「WPF 自己有问题」彻底切开——这是这一步唯一能一步到位的分诊。
- CI 冒烟变成五趟：完整功能、关玻璃、核心探针、最小 WPF 窗口、Win32 探针，每趟都要求看到
  自己那句结论行；日志里出现 `现场没存下来` 也算失败。


## 2k. 最小窗口那趟没过：兜底退出搬去 App（2026-10-25 第八轮）

CI 头一回替我们把用户机器上的处境演了一遍——`最小 WPF 窗口` 那趟 90 秒不退出：

```
阶段：建最小窗口 → 最小窗口：句柄已建好 → 最小窗口：Show 已返回
阶段：最小窗口：第一帧已画出 → 冒烟：界面活着
（然后就没了）
```

裸窗口建窗、显示、画首帧全都正常，卡的是关窗之后那段收尾。兜底退出当时挂在
`SpikeWindow.OnClosing` 里，裸窗口没有 `OnClosing`，于是没人兜底，WPF 那套 AppDomain
卸载在构建机上实测能挂一分多钟，冒烟只能干等超时。

- 兜底退出搬到 `App.ArmExitBackstopOn(window)`，两条建窗路径都挂上。订阅发生在窗体自己的
  `Closing` 之后，进度落盘与释放核心仍旧先跑完。
- 删掉探针收尾行——上一版补丁应用了两次，`退出（探针）` 被写了两遍。
- dmp 文件名带上阶段（`hang-220244-XAML 解析完，等源就绪.dmp`），扫一眼目录就知道上一趟
  卡在哪；`MiniDumpWriteDump` 之前先写一行「正在存现场」，它自己也卡住时日志不至于一片空白
  （这步要冻结所有线程，卡点正抱着加载器锁时它本身就会挂）。
- 加 `--no-activate`：`Show()` 时不抢前台，显示内容一样，少走激活那串 DWM 前后交接。
  上一趟停在「建窗」且没拿到过窗口句柄时自动生效，日志会写明。CI 多跑第六趟验这条路。


## 3. 排序一致性

`Tests/OmniPlayCoreTests/HomeSortTests.swift` 里的拼音序断言在两个平台都必须绿。
Windows 上若只有这条红，说明 ICU 排序与 mac 的 `localizedStandardCompare` 不一致，
解决办法是在核心里注册自定义 collator（GRDB 支持），或者把排序权重表算成纯 Swift 数据，
不再依赖平台 collation。

**2026-10-25 实测**：Windows（Swift 6.2.1 + ICU）35 项全绿，拼音序与 mac 本机一致；
但 `macos-14` CI 镜像上同样这三条红了。所以不守恒的变量不是「mac 还是 Windows」，
而是 macOS/Xcode 版本。正式实现里排序权重要在核心内部定死，别交给平台 collation。

## 3c. 首页接上真片库（2026-10-25）

- 库这一层落成 `Sources/OmniPlayCore/Database/`：SQLite 官方合并源 + 一层薄封装 + 表结构。
  GRDB 在 Windows 编不过（§1），而两边必须跑同一个 SQLite，所以自己包；表名列名照 mac 版抄，
  以后互导库不用翻译。
- 桥上新增 `library.*` 七个方法。核心在第一次收到 `library.*` 时才开库：`version`、
  `player.*` 这些调用不该顺手在磁盘上建文件，ABI 探针与契约测试也因此完全不碰数据库。
  空库首次读会铺 12 部演示片（`DemoSeed`），界面刚装完就有海报可画。
- 观看进度写 `mediaMark` 用 upsert：光记进度不能把用户标好的架子冲掉，反过来标记
  也不该把已存下的百分比清空。
- WPF 侧 `HomeBoard` 只画不判：分组、片名排序、已看未看全来自 `library.sections`。
  海报占位色 = 核心按片名算的色相与主题色混合，换配色整面墙跟着变。

- 左侧导航条（首页 / 继续播放 / 已看 / 未看 / 导演 / 主演）切的都是核心认得的视图 id：
  前四个走 `library.sections` 的分组，后两个走 `library.categories`。分类页只做「谁 + 几部」，
  点进去的片子列表还没接。

已知缺口：真实海报图、媒体源扫描、详情页与播放页跳转、分类页下钻都还没接。


## 4. 已知风险

- `libmpv-2.dll`（shinchiro mpv-dev）在 CI 的 Windows Server 2022 镜像上 `LoadLibraryW`
  报 126——它自己的依赖在 Server SKU 上缺（多半是媒体框架那类组件），探针因此 skip 播放。
  安装包照样打了这份 115 MB 的库；普通 Win10/11 桌面能不能装载要装完才知道，装不上时界面
  会带 Win32 错误码说明原因，也可以直接往安装目录放一份自己的 `libmpv-2.dll`。
- 安装包向导目前是英文（Inno 的 Default.isl）：简体中文 isl 不属于官方安装包，
  想要中文界面要么随包带上 ChineseSimplified.isl，要么把 [Setup] 换成 Inno Setup
  Wishlist 分支。应用本体是中文的，这里先不折腾。

- `abi-probe` 在 Windows 上把每项检查都跑绿、打印完 `abi-probe: OK` 之后，进程仍可能带
  `0xC0000409` 退出——Swift 运行时那几个 DLL 的 `DllMain` 收尾顺序问题，不是功能失败。
  `package.ps1` 因此以探针打的结论行为准，退出码异常只告警。演示窗这边在 `OnExit` 里
  先写完日志再 `TerminateProcess`：界面和数据都已收尾完，跳过系统那套收尾顺序，
  用户端就不会再看到「已停止工作」（mac 那边 ⌘Q 不落盘是同一类问题：收尾时机）。
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
