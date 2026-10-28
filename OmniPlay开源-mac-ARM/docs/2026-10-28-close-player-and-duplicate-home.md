# 关闭播放页的黑屏与「多出来的觅影标签页」（2026-10-28）

## 现象（ARM mac 实测）

1. 播放本身流畅，但点关闭之后先是一屏黑，卡一两秒，还要再点一次才关掉；关掉也没回到刚才看的详情页。
2. 打开本地视频文件播放再关闭之后，首页出现两个「觅影」标签页。

## 排查用的三个探针

- `DirectPlaybackWindowManager.traceWindows(_:)`：在拉起播放、藏播放窗、还原首页、退出全屏、
  过渡收尾几个节点打整份窗口名册（类名、标题、可见、全屏、标签组、frame）。
- `[WindowRoster]`：每秒对一遍窗口名册，内容变了就记一行，用来回答「首页是什么时候没的」。
- `[Watchdog]`：主线程 0.2 s 心跳，被占住之后报出迟到了多久，用来把卡顿对到时间轴上。

## 根因

### 1 多出来的那张首页

`WindowGroup` 在「用文件打开」这类外部事件里会再建一张场景窗口。实测：启动完成后名册是 1 张，
`open -a 觅影 <file>` 之后变 2 张，第二张从第一张级联偏移（`{{314,81}}` → `{{343,52}}`）。
macOS 的自动窗口标签默认允许，两张同应用的带标题窗口会被合进一个标签组——就是用户看到的
「首页多了个觅影标签页」。

关掉播放页时要还原的是用户那张（上面还停着详情页），可新建的那张空首页一旦可见就会被顶到前面，
于是「关不掉 + 没返回详情页」两件事一起出现。

### 2 关闭之后的那块黑屏

`closeCurrentWindow()` 藏掉播放窗口之后马上 `toggleFullScreen`。播放窗口是那条全屏 Space 上唯一的
窗口，藏起来之后 Space 上什么也没有，而退出全屏的动画实测要 1.6 s（其中约 1 s 主线程被 AppKit 占着），
这段时间屏幕上就是这块空白。

把首页还原再往后押（等退出全屏落定）实测更糟：首页 2.1 s 才出现。所以「当场翻首页」是对的，
问题是翻上来的首页会被 AppKit 顺手拽进那条全屏 Space——实测还原后首页自己带上了全屏标记、
frame 变成整屏 `{{0,0},{1470,923}}`，海报墙凭空变成一个独立全屏 Space。

### 3 试过没用的两条路

- `NSAnimationContext` 把 `toggleFullScreen` 的时长压成 0：动画照走 1.6 s，AppKit 不认这个上下文。
- 过渡期间给首页 `.fullScreenAuxiliary`：想让首页铺在那条空白 Space 上，实测没生效，
  黑屏时长与不加一致。`.fullScreenNone` 才达到目的（不让首页被拽进去）。

## 改动

`OmniPlayAppDelegate`

- `applicationDidFinishLaunching` 里 `NSWindow.allowsAutomaticWindowTabbing = false`：
  整个 App 不再出现自动标签组，也就不会再有「两个觅影标签页」。
- 监听 `NSWindow.didBecomeKeyNotification`，任何一张首页级窗口拿到键盘就过一遍
  `enforceSingleHomeWindow(_:)`。

`DirectPlaybackWindowManager`

- `enforceSingleHomeWindow(_:)`：认下最早那张首页（`canonicalHomeWindow`），后建的重复窗口当场
  `close()`。挂在启动完成、拿到键盘、收到打开文件、拉起播放四处。还原时这张排最前
  （`orderedForRestore(_:)`），不会再被新建的空首页顶掉。
- `hide(_:)`：全屏播放关闭时先给首页临时摘掉全屏能力（去掉 `.fullScreenPrimary`、加 `.fullScreenNone`），
  当场翻首页；退出全屏落定 0.6 s 之后原样还回去（3 s 兜底），用户平时点绿色按钮全屏不受影响。
  播放窗口自己 `tabbingMode = .disallowed` + `isRestorable = false`。
- `closeCurrentWindow()`：退出全屏押后 1.2 s。首页已经翻上来了再拆那条 Space，黑屏不再挡在
  「点了关闭」和「看到首页」之间；这期间再点开一条片子就把这次退出作废，直接复用还热着的全屏 Space。
- 诊断三件套见上。

## 实测

本地文件两个播放周期（`提升图片清晰度.mp4`、`373713495.mp4`），加一趟从详情页起播的
`蜘蛛侠：崭新之日`：

| 指标 | 改前 | 改后 |
| --- | --- | --- |
| 点关闭 → 首页可见 | 1.7~2.1 s（等全屏 Space 拆完） | 0.3~0.5 s（中间一帧约 0.2 s 的黑） |
| 退出全屏 | 1.6 s，其中主线程被占约 1 s | 挪到 1.2 s 之后，17 ms 落定，无动画也不挡视线 |
| 关闭后首页窗口数 | 2 张（其中一张是新建的空首页，标签栏两张） | 1 张，普通窗口态（frame 与全屏标记都正常） |
| 要不要点第二次关闭 | 要 | 不要，第一次就 `orderOut`；收尾期间再点只是补一次 |
| 详情页 → 播放 → 关闭 | 停在空白首页 | 截图核对回到原详情页，进度 00:09 / 02:25:00 已落库 |

关键时间线（第二轮，本地文件）：

```
closeCurrentWindow requested        05:01.868
藏播放窗 + 当场还原首页             05:01.883   +15ms
[WindowRoster] 觅影[可见,键,主]     05:02       首页已经在前台
退出全屏押后 1200ms                 05:01.883
关闭收尾：现在退出全屏              05:03.448
windowDidExitFullScreen             05:03.4xx   17ms，画面无变化
归还 1 张首页的全屏能力             05:04.113
```

## 已知行为变化与遗留

- 应用不再允许出现第二张首页窗口（本来也没有「新建窗口」菜单）。以后要支持多窗口，
  `enforceSingleHomeWindow` 得换成按场景/窗口标识判断，而不是一律关掉。
- 关闭之后 1.2 s 内，那条全屏 Space 还挂在 Mission Control 上，随退出全屏消失。
- Space 切换期间主线程仍被 AppKit 占约 1 s（不是本 App 的活），这期间点首页会慢半拍。
- 直接执行二进制启动偶发「启动后没有窗口」，要等一次外部事件才建窗；走访达或 `open` 启动没有
  这个现象，怀疑与状态恢复保存的窗口清单有关，另案。

## 附带回归：设置弹窗来回闪、关不掉

上一节的 `enforceSingleHomeWindow` 上线后，点首页右上角齿轮，「偏好设置」弹窗反复闪现，
连带首页一起闪，弹窗既点不动也关不掉。

探针给出的证据很直白：`[WindowRoster]` 每 0.6 s 就换一次名册，日志里每隔不到一秒出现一行
`关掉重复的首页窗口 ""（新窗口拿到键盘）`，`[Watchdog]` 同时报 3.3~5.5 s 的主线程被占。
被关掉的那张窗口标题为空，类名是 `SheetPresentationWindow`，也就是 SwiftUI `.sheet` 的承载窗。

链条是这样：

1. 判据 `isMainContentWindow` 当时只看「带标题 + 不是 NSPanel + 没有父窗口」。
   `SheetPresentationWindow` 三条全过（它有 `.titled`，标题为空，`parent` 为 nil）。
2. sheet 一弹出就拿到键盘，`NSWindow.didBecomeKeyNotification` 触发清理，把它当成
   「后冒出来的第二张首页」`close()` 掉。
3. SwiftUI 侧 `showSettings` 还是 `true`，于是立刻重挂一张 sheet，再拿键盘，再被关掉。
   一轮一轮，弹窗和首页一起闪；用户点到的永远是刚重建、还没接上事件的那一张，看起来就是「关不掉」。

修法两处，都在判据上（`DirectPlaybackWindowManager.isMainContentWindow`）：

- 加 `candidate.canBecomeMain`。sheet、弹层的承载窗能拿键盘但永远成不了主窗口，这一条最要紧。
- 再加 `sheetParent == nil`、`level == .normal`，并按类名排除
  `Sheet`/`Popover`/`Overlay`/`Transition`/`Toolbar`/`Panel`/`Alert`；
  关重复窗口时还要求 `type(of:)` 与认下的那张首页一致。
- `didBecomeKeyNotification` 的观察者先看拿到键盘的是不是首页候选，不是就直接返回，
  连清理都不跑。

`SettingsView` 的关闭按钮补了 `accessibilityIdentifier("settings.close")`，方便自动化点它。
主线程看门狗与窗口名册轮询挪进 `#if DEBUG`，发行版不再挂这两个定时器。

实测（`build/derived` Debug 包，同一套自动化）：

| 指标 | 改前 | 改后 |
| --- | --- | --- |
| 一次点击后 `关掉重复的首页窗口 ""` | 每 0.6 s 一行，直到切走 | 0 行 |
| sheet 承载窗在名册里出现次数 | 反复增删 | 1 次出现、1 次消失 |
| 首页区域逐帧亮度 | 来回跳 | 弹窗期间 4 帧完全一致（逐像素差为 0） |
| Esc / 弹窗内关闭按钮 | 无效 | 各一次就收起，名册回到 `AppKitWindow"觅影"[可见,键,主]` |
| 本地文件播放与关闭 | — | 仍是一张首页窗口，`关掉重复的首页窗口 "觅影"（收到打开文件）` 正常收掉多出来的那张 |
