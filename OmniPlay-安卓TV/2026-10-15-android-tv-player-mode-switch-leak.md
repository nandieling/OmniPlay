# 1.36 播放页换模式：旧播放器没人认领

真机反馈三件事，其实是同一件事：播放中在「兼容字幕播放」和「高性能直出」之间切，画面会
叠一层、声音发闷像两条同时在放、有时新片直接卡住；退出软件回到电视主页，声音还在那儿循环。

## 根因

`playFile()` 是打开和重开播放的唯一入口，可它的开头是：

```java
screen = Screen.PLAYER;
closeAllOverlays();
root.removeAllViews();
…
playerView = new MpvVideoView(this, activeDirectPlayback, useSurfaceViewPlayback(file));
```

`root.removeAllViews()` 只是把旧视图从界面上摘下来，谁都没叫它停。紧接着 `playerView`
这个字段就被新实例覆盖了——旧的那个 libmpv 从此没人认领：解码线程还在跑、硬解码器还占着、
`ao=audiotrack` 还在出声。而 `MainActivity.destroyPlayer()` 只认 `playerView` 这一个引用，
退出播放页、退出软件都只会拆新的那个，孤儿就一直放着。

三条症状全对上了：

- 两个实例同时抢同一路硬解，电视盒子通常只有一两个 4K 解码器，新实例拿不到就卡在开头；
- 两个渲染面同时往屏幕上送帧，SurfaceView 那层看着就是叠画；
- 退出软件（回主页，进程还活着）只拆了新的，孤儿继续放声音。

顺带还有两处同源的小漏：换模式时 `playerMenuPanel` 跟着 `removeAllViews()` 一起消失，
但字段还留着，于是 `closeTopmostLayer()` 以为菜单还开着，第一次返回键被吃掉；旧的 HLS
会话号被新的直接覆盖，服务端那路没人 stop。

## 改了什么

`MainActivity` 里把「当前这一条播放」的收尾抽成一处，换片和退出播放页共用：

```java
private void releasePlaybackState() {
    stopPlaybackLoops();          // 界面刷新、进度上报、字幕覆盖层、平滑 seek、chrome 计时
    closePlayerMenu(null);
    stopActiveHlsSession();
    flushPlaybackProgress();      // 位置先记牢，新模式重开时接回去
    RemoteIsoStreamServer.shared().clear();
    SmbStreamServer.shared().clear();
    releasePlayerView();          // 先从视图树摘掉（顺带毁渲染面），再 destroyPlayer()
}
```

`playFile()` 在建新视图前调它，`destroyPlayer()` 与 `transitionToPlaybackPart()` 也改走同一条，
`replacePlayerViewForPlaybackMode()` 里原来那份手写 remove + destroy 的重复代码删掉。

生命周期上按「离开播放页就该停」补了 `onPause()`：先收播放，`onStop()` 保留兜底。
`MpvVideoView.destroyPlayer()` 加了 `released` 幂等标记并清空子视图；`MpvPlayer` 存一份
进程内实例名册，`onDestroy()` 调 `MpvVideoView.releaseAllPlayers()` 一网打尽——以后哪条
路径再漏释放，最坏也就是漏一条片子，不会一直响到下次开机。

原生侧 `mpv_bridge.cpp` 动两处：`nativeDestroy` 先 `pause=yes` 再 `terminate_destroy`，
万一拆的过程慢，声音在那一刻就已经停了；同时去掉 `dlclose`——libmpv 卸载时要收自己的线程，
紧挨着 `terminate_destroy` 卸最容易出问题，而且下次换模式还要把整个库重新定位一遍。库跟着
进程活到结束，代价只是一次映射。

## 验证

`assembleRelease` 过（三个 ABI 的 `mpv_bridge.cpp.o` 都在改动后重编），产物
`dist/OmniPlay-TV-v1.36-arm64.apk`。真机上要看的是：播放中来回切两次模式，画面只有一层、
声音只有一条、进度接得上；切完退回海报墙再按 Home，声音立刻没。
