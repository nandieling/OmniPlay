# 直开播放窗口与「继续播放」梳理（2026-10-27）

## 现象

1. 直接播放一个视频文件，关闭后再播放另一个文件，会先看到上一个视频的画面，随后才切到当前视频。
2. 关闭直开播放之后，屏幕上出现两张软件首页。
3. 首页「继续播放」里混着一批没有海报、标题就是文件名的卡片。

## 根因

### 1 旧画面

播放窗口是复用的：`windowShouldClose` 拦截后只 `orderOut`，不真正关窗，于是 `NSHostingView(PlayerScreen)`
连同 mpv 绑定的那张 `CAMetalLayer` 一直挂在窗口上。再次打开时先 `makeKeyAndOrderFront`，而播放器要等全屏
动画落定才装（`scheduleContentInstall`，实测 0.2~2.5 s），这段空档窗口显示的就是留着的老内容——上一个视频
的最后一帧。

### 2 两个首页

关闭时靠「挑一个看着像的窗口」还原：

- `hidePrimaryWindowIfNeeded` 只记一个窗口，且只在它当时可见才记得到；
- `restorePrimaryWindow` 在没记录时拿 `NSApp.windows.first(where: { $0 !== 播放窗口 })` 顶上，面板、子窗口、
  系统窗口都可能被翻出来；
- `applicationShouldHandleReopen(hasVisibleWindows: false)` 在这条路没翻到窗口时返回 true，AppKit 于是另建
  一张 WindowGroup 窗口，而先前藏起来的那张之后也会被显示出来，屏幕上就凭空多出首页。

实测（清掉保存状态后连放两条片）每个播放周期候选窗口数都 +1（2 → 3），确实在滚雪球。

### 3 继续播放

`Movie.fetchVisibleContinueWatching` 没排除 `mediaType='direct'`，直开过的文件都算「看到一半」。当前库里带
进度的条目 15 条，其中 11 条来自直开，全是没海报的卡片。

## 改动

- `DirectPlaybackWindowManager`
  - `hiddenWindows` 取代 `primaryWindow`：打开播放时把所有普通顶层窗口（titled、非 `NSPanel`、无父窗口）
    一起藏掉并记下名单，还原时只还原这批，不再从窗口列表里猜；当时的主窗口排在名单最前优先还原。
  - 关闭时在 `orderOut` 之后延迟 0.35 s 把播放页整页换成黑底空视图（`installBlankContent`）。延迟是给
    `MPVPlayerManager.stop()` 留时间，让它在控制队列上先把视频输出摘掉——图层是 mpv 按裸指针引用的，不能
    被我们抢先抽走。`contentEpoch` 自增保证这期间若新播放页已经装上，不会被这次延迟误拆。
  - `restoreWindowForReopen` 先看自己藏起来的窗口，翻到了就返回 true，不再让 AppKit 另建一张首页。
  - `hideOtherWindows` 的 trace 列出被藏窗口的类名、标题与 frame，方便下次追查多出来的窗口是谁建的。
- `Movie.fetchVisibleContinueWatching(in:includeDirectPlayback:)` 增加开关参数，默认读
  `ContinueWatchingSettings.showsDirectPlayback`（键 `showDirectPlaybackInContinueWatching`，默认 false）。
- `MovieCardView.continueWatchingProgressRatio` 同步忽略 direct 文件的进度，卡片进度条与列表口径保持一致。
- 设置 > 基础设置 新增「继续播放区显示直接播放文件的进度」，切换后广播 `libraryUpdated`，首页 300 ms 内自动刷新。

## 已知行为变化

播放时会把当时可见的**所有**普通窗口一起藏起来，关闭后只还原主窗口那一张。手动开过多个窗口的话，其余窗口
仍在「窗口」菜单里可唤回；确实需要整组还原的话，把 `restoreHiddenWindows` 改成遍历还原即可。

## 验证

- `xcodebuild build` 通过，无新增警告。
- 清掉保存状态后的单实例实测（Debug 版，用「文件 > 关闭」触发关闭，System Events 读窗口数）：
  - 关闭后 1 张窗口，再放再关仍是 1 张，不再随播放次数增长；
  - 换片后 0.8 s 抓屏为纯黑，没有上一个视频的残留画面；
  - 首页「继续播放」从 15 条降到 4 条，全是库内条目。
- 用只读副本跑同一条 SQL 对照：开关关闭 4 条、打开 15 条。

## 遗留

- 播放周期里多出来的那张窗口还没定位到是谁建的（应用里没有别的 `NSWindow` 创建点，怀疑是 SwiftUI 在
  「全部窗口不可见」时的 reopen/恢复行为）。新加的窗口清单 trace 能直接看出它的类名与标题。
- 安卓 TV 与 Windows 侧的「继续播放」口径还没跟着改，那边本来就不显示直开条目，需要先确认要不要对齐。
