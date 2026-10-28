# 关闭播放页偶发闪退排查（2026-10-26）

## 现象与证据

`~/Library/Logs/DiagnosticReports/` 里当天 8 份觅影崩溃报告签名完全一致：

```
EXC_BAD_ACCESS / SIGSEGV，线程 com.Metal.CompletionQueueDispatch
觅影  invocation function for block in MVKCommandEncoder::finishQueries()
Metal MTLDispatchListApply
Metal -[_MTLCommandBuffer didCompleteWithStartTime:endTime:error:]
```

坏地址每次都不同（`0xf27f…`、`0x901f…` 这类明显是脏内存），最早一份在 15:43，
比今天的短剧改动都早，且都来自 `/Applications/觅影.app`——与短剧功能无关，
是播放器关闭路径上原有的问题。

## 原因

- mpv 认播放图层用的是裸指针：`setDrawable` 里
  `Unmanaged.passUnretained(layer).toOpaque()` 写进 `wid`。图层是播放页
  `MPVContainerView` 的 backing layer，播放页一拆，AppKit 就把它释放了。
- 全项目没有任何一处调用 `mpv_terminate_destroy`（`MPVPlayerManager.deinit` 只
  invalidate 了 timer），也就是说每关一次播放器就漏一个 mpv 核心，而它的视频输出
  还指着那块已经不存在的图层。
- `stop()` 还在主线程立刻把 `retainedDrawableLayer` 放掉——那是我们手里最后一个强引用。
  于是「AppKit 释放图层」和「mpv 还在往图层画」之间出现空档，在途的命令缓冲完成时
  回调读已释放的 MoltenVK 状态，就落在 `finishQueries()` 里。
- 配置里写的是 `vo=gpu-next` + `gpu-api=metal`，但崩在 MoltenVK，说明这套 libmpv
  实际走的是 vulkan→MoltenVK 那条路，图层生命周期必须严格晚于视频输出。

这解释了为什么「没先扫描、直接点开文件播一下再关掉」最容易复现：那一刻在途的
首帧工作最多，而关闭最快。

## 修复

按「先让 mpv 停手，再放手图层」的顺序收：

1. `stop()`：`stop` 命令之后马上 `vo=null` 摘掉视频输出，摘完再回主线程放掉
   图层相关引用（`releaseDrawableReferences()`）。实测 `vo` 可以运行时改，
   返回 `result=0`。
2. `deinit`：`stop` → `vo=null` → `mpv_terminate_destroy(mpv)`，真正把核心销毁。
   崩溃类消失，顺带补上不销毁核心的泄漏（原来每关一次播放页漏一整个 mpv，
   含解码器线程与音频输出）。
3. `loadFiles`：把 `vo` 写回 `gpu-next`。同一个实例被复用再播时，`stop()` 摘掉的
   视频输出要装回来；值没变时 mpv 内部自己比掉，不多重建一次。

## 验证

用当天新构建直接打开一个短剧分集文件，日志顺序是
`stop begin` → `stop on control queue: stop command` →
`stop on control queue: unload vo result=0` → `stop main-thread cleanup done`，
进程正常退出，没有产生新的崩溃报告。同一时段 `/Applications` 那份旧构建又崩了
三次、签名不变，说明没装上修复之前问题照旧。

崩溃本身是竞态，一次干净跑不能算最终证明：需要把新构建装成 `/Applications/觅影.app`
反复开关播放页确认。若仍复现，下一步依次是：`stop()` 里补一次 `wid` 解绑；
给 `MPVContainerView` 加 `viewWillMove(toWindow: nil)` 的摘出钩子（要小心全屏切换
会临时离窗，不能一律摘）。
