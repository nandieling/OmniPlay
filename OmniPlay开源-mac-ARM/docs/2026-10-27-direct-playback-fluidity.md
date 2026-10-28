# 直开播放的卡顿与「点两下才关」（2026-10-27）

## 现象

1. 直接打开视频文件不够流畅：点开之后有一段什么都没发生的空档。
2. 点关闭会卡顿，并且先变成一屏黑色的播放页，再点一下才真正关掉。

测试素材：`/Volumes/1T/偶像大师.The.Idolm@ster.S01.2011.1080p.BluRay.Remux.AVC.LPCM 2.0-LuckAni`
（26 集，单集约 6.4 GB，1080p AVC remux + LPCM 2.0）。

## 根因

### 关闭路径把重活排在了用户和首页之间

旧顺序是：先广播关闭通知（同步抓帧做缩略图 + 同步落库）→ 停机 → 退出全屏动画 → 才把窗口藏起来。
全屏动画约 1.5 s，期间窗口还挂在屏幕上，显示的就是停止出画后的黑底播放页——这就是「先变黑屏、
再点一下才关」。另外 `MPVPlayerManager.deinit` 会在主线程上跑 `mpv_terminate_destroy`，
又多压 50 ms 上下。

关闭按钮在过渡期还能重复点：`windowShouldClose` 每次拦截都重新排一遍退出全屏，于是出现「点了两下」。

### 打开路径把播放器扣在全屏动画之后

旧实现等全屏动画落定才装 `PlayerScreen`，探测、缓冲全都排在动画后面，冷开要 4 s 以上；
而动画本身（AppKit 建全屏 Space）实测就要 2.5~3.0 s，这段空档屏幕是纯黑的。

装上之后又踩到第二个坑：`MPVContainerView` 收到 `willStartLiveResize` 会为「尺寸要变了」自动按暂停，
等视频输出按新尺寸重建完再续播。可这时候图层还没绑给 mpv，屏幕上根本没有画面，
白等 1.3 s（实测 `尺寸切换 live-resize：先暂停 … 暂停了 1277ms`）。

## 改动

`DirectPlaybackWindowManager`

- `closeCurrentWindow()` 重排：`settlePendingHandoff()` → `endFullScreenTransition()` → **先 `hide()`**
  （`orderOut` + 还原主窗口）→ 再广播 `.standalonePlayerShouldClose` → 最后对已经看不见的窗口
  `toggleFullScreen`。用户点一下就立刻没影，收尾全在隐藏窗口上做。
- `isClosingPlaybackWindow` 一次性闸门：关闭流程走到底之前，重复点击直接忽略并记一行日志。
- `open()` 立即装播放器（`apply(request, to:handoff:)`），探测与缓冲跟全屏动画并行；
  图层交给 `DirectPlayerHandoff` 延后绑，mpv 的视频输出只按最终尺寸建一次。
- 落定信号加了兜底：`windowDidEnterFullScreen` 到了立刻 `settlePendingHandoff()`；通知没送到时，
  `pollFullScreenSettle` 每 0.1 s 看一次窗口 frame，连续 0.2 s 不动且已进全屏就认动画落定；
  3 s 超时仍是最后出口（否则过渡标记会永久卡住后续关闭）。
- `recoverCursorVisibility()` 从 8 轮 × 11 次 `NSCursor` 调用砍到 4 轮（0/120/400/1000 ms）× 3 次
  ——每轮都是一串 WindowServer 往返，全压在关闭后的那一帧上。

`MPVPlayerManager`

- `stop()` 把 `stop` → `vo=null` → `mpv_terminate_destroy` 整体挪到 `nan.omniplay.mpv.control` 队列上，
  主线程只留一次 `takeMPVHandle()` 交换指针；`deinit` 只作兜底。
- 新增 `isRenderingVideoOutput`（图层是否已绑）。

`MPVVideoView`

- `beginLiveResize()` 只在 `playerManager?.isRenderingVideoOutput == true` 时才为尺寸变化按暂停。

`PlayerScreen` / `MPVVideoView`：新增 `videoOutputHandoff` / `handoff` 参数，把交接对象传到
`makeNSView` 里的 `attach`。

## 实测时间线

对照基准（上一个构建，未锁屏，播放器仍等全屏动画落定才装）：

```
open movie=…                      30.206
windowWillEnterFullScreen         30.586   +0.38s
windowDidEnterFullScreen          33.163   +2.96s（AppKit 建全屏 Space）
loadfile → 拿到时长                          1186ms
playback activity started         34.348   +4.14s
```

最终构建只能在锁屏下测（AppKit 不推进全屏动画，截图全黑，只能读日志）：

```
E01  open movie=…                 19.235
     loadfile → 拿到时长            +75ms（不再排在动画后面）
     windowWillEnterFullScreen     +0.44s
     全屏过渡超时无通知 → 补绑图层   +3.93s（通知没来，3s 兜底放行）
E02  open movie=…                 31.177
     loadfile → 拿到时长             +8ms
     帧稳定 4703ms → 补绑图层       +5.09s
     windowDidEnterFullScreen      +5.10s（真信号 8ms 后到了，兜底判据与之对得上）
```

锁屏把动画拖到 4.7 s，所以这里的绝对值不代表真机观感；能确认的是 `loadfile` 已经跟动画并行
（探测 75 ms / 8 ms，不再是被动画堵住之后的 1186 ms），以及两条放行路径都能正确接到 `补绑图层`。
未锁屏时的历史数据：同一窗口已在全屏 Space 上再开一条片子，`open` → `windowDidEnterFullScreen`
只用 59 ms。全屏动画本身是 AppKit 建 Space 的固有成本，播放器侧已经不再往里加东西。

关闭（E01 播到中途点关闭）：

```
windowShouldClose intercepted       53.633
closeCurrentWindow requested        53.633   0ms
hide window orderOut                53.633   同帧藏窗
restoreHiddenWindows 觅影           53.742   +109ms 首页回来了
stop begin                          53.743
藏窗之后再退出全屏                   53.744
windowWillExitFullScreen            53.768
stop+vo=null 17ms / 销毁核心 54ms    53.816   （控制队列，不占主线程）
windowDidExitFullScreen             53.820   +187ms
installBlankContent 播放页已拆掉     54.006   +373ms
```

主线程上关闭一帧的活从「抓帧 + 落库 + 停机 + 销毁核心 + 1.5 s 动画」缩到藏窗与还原窗口这 100 ms 上下。

自动暂停：改动前冷开必经 `尺寸切换 live-resize：先暂停 … 暂停了 1277ms`；改动后同一条素材的开场日志里
不再出现 `尺寸切换`。

## 解锁后补测：图层延后引出的黑屏回归

解锁重跑，冷开第一次播放出现「有声音、字幕在走、进度在涨，画面整场纯黑」。日志给得很直白：

```
36.218 补绑图层 → setDrawable wid=… result=0
36.9   重建视频输出 enter-fullscreen 跳过：mpv 暂无输出尺寸
```

图层绑上了，mpv 却始终拿不到输出尺寸——视频轨还摘着。

### 根因

`loadFiles` 在图层没绑时先按「无视频轨」打开（`vid=no`），把容器探测与音频缓冲挪去跟建窗并行，
同时记下 `pendingVideoTrackAfterDrawable = "auto"`，等 `setDrawable` 成功再放回轨道。
而 `clearPendingInitialSeek()` 把这本账一起清了——它本该只管 seek 的事。带续播进度的直开一定会做
那次起播 seek（除了 `loadfile … start=270.5`，还有一次 absolute seek 兜底），它比图层绑定先到，
账本被抹干净，轨道就再也没放回来。

延后绑图层之前撞不上：那时图层在 `loadfile` 之前就绑好了，压根不摘轨道。
不带续播进度的那条也撞不上——上一轮里 E02 就正常打出了 `图层已绑定，补上视频轨`。

### 修法

- `clearPendingInitialSeek()` 不再动 `pendingVideoTrackAfterDrawable`，这本账改由 `stop()` 统一清。
- `setDrawable` 成功后不只认账本，也认引擎实际状态：`vid` 还是 `no` 就补 `auto`
  （正在重建视频输出时除外，那里要求「摘轨」与「恢复」之间隔开一段真实时间）。
- `reinitVideoOutput` 里「mpv 暂无输出尺寸」那条分支顺手兜底：图层已绑、轨道还摘着，就地补回。

### 补测数据（同一条带 270 s 续播进度的 E01）

第一次打开（要新建全屏 Space，动画 2523 ms）：

```
open movie=…                          06.273
start playback drawable=false         +0.34s（拿到时长只花 +82ms，探测不再排在动画后）
windowWillEnterFullScreen             +0.44s
playback activity started             +0.45s（先出声）
帧稳定 2523ms → 补绑图层                +2.97s
图层已绑定，补上视频轨（绑定前 vid=no）   +2.97s
重建视频输出 跳过：mpv 输出已是 2940x1846
windowDidEnterFullScreen              +2.97s（比帧稳定判据晚 2ms）
```

关闭：`hide window orderOut` 同帧 → `restoreHiddenWindows` +99 ms → `windowDidExitFullScreen` +151 ms
→ `installBlankContent` +373 ms。

第二次打开（全屏 Space 还在，64 ms 就进全屏）：图层在 `loadfile` 之前就绑上了，根本不走摘轨道那条路，
画面 +0.55 s 内出。

截图核对：第一次打开的画面铺满整屏、上下黑边对称；第二次打开先看到的是首页而不是上一条片子的最后一帧；
关闭之后 `AXWINDOWS` 只有一张 `觅影`。

## 已知行为变化与遗留

- 关闭时首页是从背后翻上来的，全屏 Space 退场那一下仍会露一小段黑色过场，这是 macOS 离开全屏 Space
  的固有动画，去不掉；顺序改成先藏窗之后，它不再挡在「点关闭」和「看到首页」之间。
- 锁屏/熄屏状态下 AppKit 可能整条不发 `didEnterFullScreen`（实测锁屏后动画停在半途 4.7 s），
  这正是加帧稳定兜底的原因；此时截图全是黑的，只能靠日志验证。解锁后已复测，见上一节。
- 每个播放周期 `hideOtherWindows hidden=2→3`：应用侧似乎多出一张同名主窗口候选，
  首页叠两张的症状已由「只还原记下的一批」压住，但滚雪球本身还没查。

## 复现验证

```zsh
APP="build/derived/Build/Products/Debug/觅影.app"
open -n "$APP" --stdout /tmp/dptest/run.log --stderr /tmp/dptest/run.err
open -a "$APP" "/Volumes/1T/偶像大师…/偶像大师.The.Idolm@ster.S01E01…mkv"
```

看 `/tmp/dptest/run.log` 里 `open movie=` → `loadfile` → `windowWillEnterFullScreen` →
`全屏动画落定（帧稳定 …）` / `windowDidEnterFullScreen` → `补绑图层` → `playback activity started`
的顺序，关闭看 `hide window orderOut` → `restoreHiddenWindows` → `stop 控制队列收尾`。
`[DirectPlaybackWindowManager]` / `[MPVLifecycle]` / `[PlayerScreenClose]` 三个前缀都带毫秒时间戳并即时 flush。
