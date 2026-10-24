# 安卓 TV 1.33：兼容模式播 4K 的负载砍一轮

问题是同一个死结的两头：兼容字幕播放能画内封字幕，但 4K 蓝光会卡；高性能直出不卡，又不画字幕。
这一版先把兼容模式的负载砍一轮，让它尽量够得着 4K；直出 + 自绘字幕那条路要动原生层，留作下一步。

## 一、开销在哪里

兼容模式是 `vo=gpu` + `hwdec=mediacodec-copy`。链路是：硬解 → 帧拷回内存 → 上传纹理 → GPU 上色管
与 HDR→SDR 色调映射 → 合成字幕与 OSD。4K 10-bit 一帧 12MB 出头，按 24~60fps 算，光是上传就要吃掉
几百 MB/s。以前在这条链上还有三处额外开销：

1. `hdr-compute-peak=yes`：逐帧降采样回读算峰值亮度。mpv 文档自己写着这一项「可能对性能有较大影
   响」，而 HDR 文件绝大多数带 MaxCLL / mastering display 元数据，本来就能直接用。
2. FBO 走默认的高位宽浮点（`rgba16-f`）+ 按 10-bit 抖动：带宽和带宽。
3. 画面挂在 `TextureView` 上：每帧在应用的 HWUI 场景里多一次全屏带纹理绘制，外加一次同步等待；
   直出模式一直挂在 `SurfaceView`，所以「切直出不卡」里也有一部分是这个原因，不只是解码路径。

## 二、改了什么

`app/src/main/cpp/mpv_bridge.cpp` 兼容分支：

```cpp
set_option(bridge, "hdr-compute-peak", "auto");   // 原来是 yes：逐帧回读
set_option(bridge, "fbo-format", "rgb10_a2");
set_option(bridge, "dither-depth", "8");
set_option(bridge, "blend-subtitles", "no");
set_option(bridge, "sub-gauss", "0");
```

`MpvVideoView` 增加第三个参数 `useSurfaceView`，`MainActivity.useSurfaceViewPlayback(file)` 决定载体：

```java
return shouldUseDirectPlayback(file) || isLikely4K(file) || needsManagedColorPlayback(file);
```

命中直出、4K、DV/HDR 的文件在两种模式下都画 SurfaceView；其余（1080p web-dl 这类日常播放）保持
TextureView 不变，避免整个库跟着换渲染路径。`replacePlayerViewForPlaybackMode()` 因此多接一个
`Models.VideoFile`，播放中途切模式时按同一规则重建视图。

## 三、直出 + 自绘字幕这条路还差什么

覆盖层早就有：`SubtitleCue` 文本层与 `PgsSubtitleView` 位图层，由 `subtitleOverlayUpdater` 跟着播放
进度刷新。但喂它的只有服务端字幕接口（`api.subtitleUrl(track, ticket)`，`selectExternalSubtitle()`
里 `usesAndroidSubtitleOverlay()` 那一支）；独立版没有这条接口，内封字幕直接 `playerView.
setSubtitleTrack(mpvId)` 交给 mpv，而 `vo=mediacodec_embed` 不参与 OSD 合成，所以画不出任何东西。

要补的就是「本机抽取」这一步，两种做法：

1. 原生侧用随 APK 打包的 libavformat 加一小段抽取器：把选中字幕轨 demux 成文本 cue
   （SRT / ASS / VTT）或 PGS 包流，喂现成覆盖层。优点是容器通吃（MKV / MP4 / TS）、蓝光原生的 PGS
   位图字幕也能显示、不额外占一路解码；代价是新增 JNI 与全轨预读的内存/耗时，ASS 的样式要自己
   落一层子集。
2. 叠第二个 mpv 实例只渲染字幕（透明层盖在视频上）：libass 的样式、PGS、字体回落全白拿；代价是同
   一个文件多一路 demux（高码率原盘上会多占带宽与读请求），以及两个实例的时钟纠偏。

两条都能让「不卡 + 有字幕」同时成立，第 1 条更省资源、也更可控，倾向它。

## 四、验证

模拟器 + 假 WebDAV（`2160p ... HDR ... mkv` 那条命中新载体）：SurfaceView 的 `surfaceCreated` 正常
触发、播放器照常挂载，日志走到 `end-file reason=error error=loading failed (-13)`（假服务回的是占位
字节，起播必然失败）；`mpv_initialize` 通过，四个新选项没有触发任何选项错误，说明这份 libmpv 全部
认。真实帧率收益得上真机对着 4K 原盘看：模拟器是 SwiftShader 软渲染，测不出带宽与合成开销的差别。
