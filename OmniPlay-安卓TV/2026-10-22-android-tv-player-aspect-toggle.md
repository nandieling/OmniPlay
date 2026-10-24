# 1.40 播放页右下角的「保持比例 / 拉伸」

## 1. 拉伸是从哪儿来的

控制条那行（暂停 + 音轨 + 字幕 + 播放模式）本来就贴着屏幕右下，缺口就是少一颗比例按钮。
先问一句：画面为什么会被拉伸？

- 直出模式（`vo=mediacodec_embed`）里 mpv 不参与渲染，解码器的帧直接写进 SurfaceView，
  而 SurfaceView 默认把帧**铺满整个视图**。视图是全屏的，于是 2.35:1 的片子被上下拉开成
  16:9。这不是 mpv 的 bug，是这条链路的默认行为。
- 兼容模式（`vo=gpu`）由 mpv 自己渲染，它按影片比例留黑边，本来就不拉伸。

所以真正的修法是让那张视频视图自己变成画面大小，而不是让整块屏幕当画面。

## 2. 为什么不是 `SurfaceView.setAspectRatio`

第一版写的是 `surfaceView.setAspectRatio(ratio)`，编译直接「找不到符号」——这个方法在
SDK 里是 `@hide` 的（`javap android.view.SurfaceView` 里既没有 `setAspectRatio` 也没有
`setFixedSize`），媒体框架内部用，应用侧调不到。

于是换成量视图：`MpvVideoView`（一个全屏 `FrameLayout`）按影片比例把里面的 Surface /
Texture 视图居中摆一块，多出来的是容器这块黑底。这与 ExoPlayer 的
`AspectRatioFrameLayout` 是同一个路子，也顺带让兼容模式与直出模式共用一套几何。

比例来源是 mpv 的 `dwidth` / `dheight`（已含像素宽高比，`MpvPlayer.displayAspectRatio()`），
读不到时退回 `video-size` 的 `"1920x1080"` 字符串按编码尺寸算。重算的时机：`playback-restart`、
新增的 `video-reconfig` 事件、视图 `onSizeChanged`、以及用户点开关那一刻。

拉伸档下兼容模式还要改 mpv：`aspect` 写成视图自己的宽高比（真拉伸、会变形），`panscan`
钉在 0；切回保持比例时 `aspect` 交回 `auto`。

## 3. 字幕层得跟着画面走

黑边一出来，原来贴屏幕底边的字幕就掉进黑边里了，这比拉伸更扎眼。于是
`MpvVideoView.setOnAspectChanged(...)` 在每次摆完后叫一声，MainActivity 把两层字幕重摆：

- 文字层（SRT / VTT / ASS 覆盖层）把 `bottomMargin` 抬高一个黑边高度；
- PGS 层是整个视图按字幕平面比例自绘的，直接把它的视图缩到画面那一块，字才落在画上。

回调可能正好落在布局过程中，所以那一头 `main.post` 到下一轮再改布局；摆出来的那块高度记在
`frameHeightPx` 里当场就能问，不等布局跑完（问 `child.getHeight()` 会慢一步）。

## 4. 交互

右下角新增最后一颗按钮，写当前那一档（`保持比例` / `拉伸`），点开是两选一菜单，与「播放模式」
同一套 `showPlayerMenu`：标题会复述「已选 ××」，选中那行有勾，焦点链上挂在播放模式右边。
点中就地生效、不重开播放器，进度不动；偏好写进 `player_stretch_to_fill`（默认关），
下一条片子从这条偏好起。

## 5. 验证与代价

`compile.sh` / `harness.sh`（三项全绿）/ `release.sh` 通过，产物
`dist/OmniPlay-TV-v1.40-arm64.apk`（versionCode 51）。

真机要看的几点：直出播放 2.35:1 片子时是否上下有黑边；片头那两三帧是否还来不及改尺寸
（几何要到 `playback-restart` 才可知）；中途切档是否只重排不卡顿（换的是布局，不是解码）；
PGS 字幕在两种档位下都压在画面上而不是黑边里。已知代价：黑边是容器画的，屏本身漏光的
机器上黑边会显得发灰——那是硬件的事。
