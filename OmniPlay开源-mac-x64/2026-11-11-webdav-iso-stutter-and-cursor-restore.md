# WebDAV ISO 播放不再一卡一卡，播放页移动鼠标就恢复光标

日期：2026-11-11 ｜ 版本：1.10 ｜ 平台：mac x64（与 ARM 同步）

## 现象

1. WebDAV 源播蓝光 ISO 原盘（The Odyssey 2026 2160p UHD HDR10 HEVC）放到中间一卡一卡，
   关掉播放页再进去播还是卡。
2. 播放页把鼠标移上去，系统光标不出现，得点一下才冒出来。

## 根因

**远程 ISO 的直读代理是串行的。** WebDAV 上的 ISO 由 `RemoteISOStreamProxy`（本机 127.0.0.1
的 HTTP 服务）喂给 mpv：先按 Range 解析 UDF/ISO9660 找出 `BDMV/STREAM/*.m2ts`，再把这些
m2ts 当 http 地址交给 mpv。mpv 会一次要 `bytes=偏移-` 这种长段，然后顺着这条连接读到底，
于是代理的取数节奏就是整条链路的天花板。旧实现「取一块 512KB、发完再取下一块」，
每块都要空等一次 WebDAV 的首字节往返，还要等这块写完本地 socket 才发起下一次取数。
拿一个 150ms 首字节延迟的服务器实测：吞吐钉在 **3.1MB/s**（约 25Mbps），而 2160p 原盘
动辄要 6~10MB/s。缓冲入不敷出，mpv 就把已经缓存的一段放完、停下来等、再放，
看起来正是「顺一会儿卡一下」；换成别的时间点重播，同样的节奏缺陷当然照旧。

**播放页收不到鼠标移动。** `NSCursor.hide()` 之后，把光标放回来的只有
`.onChange(of: showControls)` 那条路。而播放页的 `onContinuousHover` 挂在包着 mpv 图层的
SwiftUI 视图上，mpv 的 `MPVContainerView`（layer-backed、`isOpaque`）盖在上层并且吃 hit-test：
指针只在画面上移动时，事件根本到不了 SwiftUI 的 tracking area，控制条和 `NSCursor.unhide()`
都不会被触发。点击能生效是因为 `mouseDown` 走的是响应链，不是 tracking area。
用一个照搬播放页视图层级的探针复现：光标隐藏后往窗口里灌 12 个 mouseMoved，
SwiftUI 的 `onContinuousHover` 只收到 1 次，而 mpv 视图自己挂的 tracking area 收到 13 次。

## 改动

`PlayerScreen.swift`
- `RemoteISOStreamProxy.sendBody`：改成滑动窗口预取。单次 Range 从 512KB 提到 2MB，
  同时在途 4 块（`RemoteISOReadTuning`），`inFlight` 严格按偏移排队，发送当前块的时候
  后面几块已经在下载；顺序发送保住 mpv 的字节序。连接被播放端半路挂断（拖进度、换集）
  时取消在途任务，别再为死连接预取。
- 单块取数失败重试 3 轮（公网抖动、服务器偶尔 5xx），原来一次失败就整条流断掉。
- 每条流结束时打一行 `[RemoteISOProxy]` 吞吐日志，下次卡不卡有数据可看。
- `RemoteISOHTTPByteReader.read(file:)` 的分块上限跟着提到 2MB，否则多 extent 的文件
  会在内部又被切回 512KB 串行取。
- 新增 `startObservingPointerActivity` / `stopObservingPointerActivity`（与菜单跟踪同一套路，
  在 `onAppear` / `onDisappear` 挂卸）接住指针活动，`handlePointerActivity` 里放回控制条和
  系统光标。播放页的 `body` 已经大到不能再加修饰符（加一个 `.onReceive` 就
  「compiler is unable to type-check this expression in reasonable time」），所以没往 body 上加东西。

`MPVVideoView.swift`
- `MPVContainerView` 自己挂 `NSTrackingArea`（`.mouseMoved/.mouseEnteredAndExited/.activeAlways`），
  在 `mouseMoved`、进入、拖拽、滚轮时经 `MPVPlayerManager.reportPointerActivity()` 发通知。
  每个事件都继续交给 `super`，不抢原来能到 SwiftUI 的手势。视图层 0.1 秒节流一次。

`MPVPlayerManager.swift`
- 起播前放宽网络源缓存：`cache=yes`、`demuxer-max-bytes=256MiB`、`stream-buffer-size=2MiB`。
  mpv 默认只留 100MiB 前置缓存，公网抖动几次就抽干；挂载盘（NetFS WebDAV 播 ISO）
  本来就完全靠它做预读。
- 卡顿体检：0.25 秒轮询顺手记 `time-pos`，连续 2 秒不往前走就打一行现场——缓存提前量、
  `hwdec-current`、编码、分辨率。之前只能靠「看着卡」猜是网络喂不进还是解码追不上。
- 解码补试：1080p 以上却只落在软件解码时，补写一次 `hwdec=videotoolbox-copy`。走 `wid`
  嵌图层这条路上零拷贝的 videotoolbox 有几率建不起来，mpv 会静默退回软解，
  2160p HEVC 10-bit 一旦软解，Intel 机器基本追不上实时。仍然软解时补一条提示
  （切「播放画质模式 → 流畅优先」）。

## 验证

同一份代理代码、同一个每台请求 120ms 延迟的 Range 服务器，取中段 8MB：

| 取数节奏 | 结果 |
| --- | --- |
| 512KB × 1（改前） | 3.57MB/s |
| 2MB × 4 预取（改后） | 51MB/s，8,000,000 字节逐字节比对 0 处不一致 |

指针事件探针：改前 `onContinuousHover` 1 次 / tracking area 0 次；改后 tracking area 13 次。
两份工程 Release 全量编译通过（x64 工程 `platform=macOS,arch=x86_64`、ARM 工程 `arch=arm64`）。

## 遗留

- 真机上还需要按 `[RemoteISOProxy]` 和「播放停滞」「解码路径」三行日志确认一次：
  如果 WebDAV 侧带宽本身就低于码率，代理这边再怎么并发也补不出来，得看服务器限速。
- 光标恢复依赖 mpv 那块 NSView 收到 mouseMoved。真机上如果播放画面上还压着别的
  AppKit 视图，指针活动可能要从窗口层再兜一道。
