# 直开播放首秒音画/字幕/进度条同步（1.10）

日期：2026-11-02　范围：mac ARM / x64　影响文件：`MPVPlayerManager.swift`

## 现象

直接打开视频文件（双击、拖进窗口、右键用觅影打开）后，前两秒画面和声音都在走，但进度条钉在原地不动，
字幕也看不见；过几秒字幕才冒出来，进度条才跟着跳。要求是打开之后声音、画面、字幕、进度条四项一起起步。

## 排查

给 `updatePlaybackState()` 挂了一个只在 `OMNIPLAY_SYNC_PROBE=1` 下开火的探针，把引擎侧读到的
`time-pos / duration / pause / sid / vid` 和主线程已经发布的 `currentTime / position` 一起打出来，
再用 `open -a 觅影.app <mkv>` 复现（4K HDR + Dolby Vision 的蜘蛛侠，有内嵌字幕）：

```
16:04:50.029 open movie=蜘蛛侠… fileId=48F51E37…
16:04:50.5   setDrawable 延后：等全屏动画落定再绑图层        （连续 5 次）
16:04:50.6   起播计时：loadfile→拿到时长 83ms               （引擎侧时长早就拿到了）
16:04:51.881 全屏动画落定（帧稳定 1669ms），放行延后的图层绑定
16:04:51.881 setDrawable wid=… result=0                     （同一次起播里连写 6 遍同一个 wid）
…
[SyncProbe] +2063ms timePos=713.38 paused=0 vid=1 published=11:52 pos=0.082
[SyncProbe] +2498ms timePos=713.38 …   ← 引擎时间轴冻在 713.38 近 3.4 秒
[SyncProbe] +4498ms timePos=713.55 …   ← 而 UI 一直报 paused=0，进度条自然不动
```

两个关键点：

1. 播放窗是复用+强制全屏的，`windowDidEnterFullScreen` 在实测里根本不发来，帧稳定那条快路径又要求
   `styleMask` 已经带上全屏标记，两条都接不上，图层只能等 3 秒那条兜底定时器放行。这期间 mpv 是
   `vid=no` 打开的（容器探测和音频缓冲跟建窗口并行），**期间没有任何视频输出**，字幕自然也无处渲染。
2. 图层一绑上就把 `vid` 从 `no` 翻回 `auto`，mpv 这时已经在放音，建视频输出（建 swapchain、编
   tone-mapping 着色器）实测要 2 秒以上，而这 2 秒里 mpv 的播放核心不推进 `time-pos`。UI 每 0.25 秒
   轮询到的仍是「没暂停、位置没变」，于是进度条钉死、字幕缺席，等视频输出起来才一起补跳。
   探针里 control queue 没被堵住（没有「上一轮还没回来」的跳过行），冻的是 mpv 自己的播放时钟。
   另外 `MPVVideoView` 在 enter-fullscreen / backing-scale / live-resize 上各自会补一次
   `setDrawable(self, force: true)`，同一次起播里同一个 wid 被原样写了 6 遍，mpv 每吃一次就重建一次
   视频输出，把这段冻结又拉长了一截。

## 改动

- **重复绑定挡在控制队列上**：记下最后一次真正写进 mpv 的 `wid` 与图层像素尺寸
  （`boundDrawablePointer` / `boundDrawablePixelSize`），两者都没变就不再写 `wid`。`force` 的语义按需求
  收敛成「尺寸可能变了，去核一遍」，而不是「无条件再写一遍」。日志里 `setDrawable 跳过：wid=… 已按
  2940x1846 绑好` 就是被挡掉的那几次。`loadFiles` 换片时清掉这本账，之后第一次绑定一定真写。
- **补视频轨前先把引擎按住**：新增 `armVideoOutputStartupGate(mpv:)`，在 `setDrawable` 把 `vid` 翻回
  `auto` 之前调用。它复用现成的 `armGeometryRecovery` 机器，只是多要一个条件——必须看到 mpv 真的报出
  与图层相符的输出尺寸（`geometryRecoveryRequireOutput`）才放行，最短停 0.35 秒、最长兜底 2.5 秒，
  续播前照例做一次 `seek … absolute+exact` 把音视频两条管线冲干净。是否要等由
  `hasPlayableVideoTrack` 决定：问 `track-list`，不问 `has-video` / `current-tracks/video/*`，因为这一刻
  视频轨还摘着，那份目录本来就是空的（第一版就问错了，所有片子都被判成「没有视频」）。
- **进度条不再从 0 起步**：`loadFiles` 里把发布时间直接摆到续播位置（`currentTimePosition` /
  `currentTime` 用 `startPosition` 播种），不再先刷成 `00:00` 等第一轮轮询追回来。

## 验证

同一台 ARM、同一个文件（续播位置 712s）：

```
16:04:51.881 setDrawable wid=… result=0 目标尺寸=2940x1846     ← 只此一次真写
16:04:51.9   尺寸切换 补绑图层起视频轨：先暂停，等视频输出按新尺寸重建完成后续播
16:04:51.9   图层已绑定，补上视频轨（绑定前 vid=no）
16:04:51.9   setDrawable 跳过 ×4
16:04:54.3   尺寸切换续播：输出尺寸已跟上图层，暂停了 2388ms
```

放行之后连续几轮探针是 `timePos` 每 0.5 秒走 0.5 秒（1.0 倍速，不再冻），发布的
`currentTime` 与它逐轮对齐，`sid=1`、`subVis` 有值，字幕随首帧一起出。
起播的总时长没变（全屏动画 1.7s + 视频输出 2.4s），变的是这段时间里引擎是「停着等」而不是
「假装在放」，用户看到的第一帧、第一声、第一行字幕和进度条位置是同一条时间线。

x64 侧 `MPVPlayerManager.swift` 与 ARM 逐字节一致，x64 工程 `xcodebuild … arch=x86_64` 编译通过。

## 复发：有续播进度时「播—停—播」（同日实测）

上面这版按住的是「补绑图层之后建视频输出」那一段，实测没进度从头放的Case没问题，
但**有续播进度**的片子会先播一两秒、停一下、再继续，成了「播—停—播」。

原因就在按住的时间点：`loadfile` 到图层绑上中间有一两秒（实测 1.3~2.6 秒，取决于全屏动画多久落定），
这期间 mpv 是真在放的——续播 seek 一完成声音就起来了；等图层绑上，闸门为了建视频输出才第一次按暂停。
用户听到的正是这一段「先放的」声音。

改法是把按住从「绑图层时」提到「`loadfile` 之前」：图层没绑上这一趟，除了摘掉视频轨，
连播放一起按住，等图层与视频输出都就位再一次性放行，只起步一次。

- 按住走 mpv 的 `pause` 属性，不用 `loadfile` 的 per-entry 选项。实测在
  `loadfile … replace -1 start=712.420` 后面再追一个 `pause=yes`，mpv 这条命令就没解析出来，
  时长一直读不到（`起播计时` 不出现、`initial resume seek expired before media became ready`）；
  写 `pause` 属性则一切照常，放行时一起写回 `no`。
- `isAwaitingDeferredVideoStart` 记录这次按住，任何放行路径都要收口：闸门等到达标出画、
  容器里根本没有视频轨（纯音频）、图层 4 秒没绑上的看门狗、以及用户在按住期间自己按了播放/暂停
  （`playOrPause` / `setPaused` 里作废这次按住，不替他放行）。
- 闸门这次是「我们主动按的暂停」，引擎本来就 `pause=yes`，所以 `armGeometryRecovery` 的续播条件补成
  `wasPlaying || requireOutput`，否则按住的片子放行时反而停在原地不动。
- 起播期间发布的 `isPlaying` 也跟着走 `!holdsVideoStart`，不再先亮播放再被轮询打回。

实测（同一台 ARM、同一个文件，续播点 712.42s；`[PROBE]` 是临时探针，收尾已摘）：

```
[PROBE] t=712.42 paused=1 vid=no geo=0 hold=1   ← loadfile 起就没放
01:01:48.143 全屏动画落定，补绑图层
       尺寸切换 补绑图层起视频轨：先暂停，等视频输出按新尺寸重建完成后续播
       图层已绑定，补上视频轨（绑定前 vid=no）
[PROBE] t=712.42 paused=1 vid=1  geo=1 hold=1   ← 建视频输出期间仍然按住
       尺寸切换续播：输出尺寸已跟上图层，暂停了 2366ms
       playback activity started                 ← 全场只此一次
[PROBE] t=712.42 paused=0 vid=1 geo=0 hold=0
[PROBE] t=712.79 … t=713.30 … t=713.80          ← 每 0.5 秒走 0.5 秒，一路不回头
```

`paused=1` 的样本总共只有 4 个，全在主动按住那 2.4 秒里；从头放（进度为 0）的 case 同样只起步一次。

## 已知边界

- 纯音频文件（容器里没有视频轨）不走这个闸门，绑完图层直接放行。
- 视频输出真的起不来时，最长停 2.5 秒照常续播，不会永久卡住。
- 探针（`OMNIPLAY_SYNC_PROBE`）是排查用的临时码，收尾时已摘干净，仓库里不留残码。
