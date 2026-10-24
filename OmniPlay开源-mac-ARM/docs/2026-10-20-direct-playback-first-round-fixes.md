# 直开播放首轮真机反馈修复

日期：2026-10-20　范围：mac（ARM 基准 + x64 镜像）　关联：[2026-10-20-direct-file-playback.md](2026-10-20-direct-file-playback.md)

## 1. 反馈与根因

| 现象 | 根因 |
| --- | --- |
| 播完剩余时间显示 0:00，时间轴却停在半路 | mpv 播完回到 idle，`time-pos` / `duration` 都读成 0，状态轮询里 `if validDuration > 0` 整段被跳过，`position` 停在最后一次轮询值。顺带：退出时 `playbackEngineSnapshot` 读到 `timePos = 0`，进度会写成 0 而不是「已看完」 |
| 打开和播放都不够顺 | 入库成功后广播了一次 `.libraryUpdated`，海报墙在 mpv 冷启动的同时整体重排刷新；全屏动画期间还往窗口里装播放器，mpv 绑定的是动画中间尺寸的 drawable，动画结束又要重建 swapchain |
| 第一次播放画面偏左上、没全屏，退出重播又正常 | `PlayerScreen.ensureComfortableWindowSize()` 拿 `isKeyWindow` 改窗口尺寸，和 `DirectPlaybackWindowManager` 刚发出的 `toggleFullScreen` 撞在一起；更要紧的是 `isInFullScreenTransition` 只在 didEnter / didExit 复位，通知一旦被吞就永久停在 true，之后每次全屏切换都被这个标记挡掉 |
| 播完点关闭有概率没反应 | 同一处：处于全屏或「过渡中」时 `closeCurrentWindow()` 只把 `pendingHideAfterExitFullScreen` 置位就 return，既不落库也不藏窗，全靠 `windowDidExitFullScreen` 接力；标记卡住后就再也关不掉 |

## 2. 改动

| 文件 | 改动 |
| --- | --- |
| `OmniPlay/MPVPlayerManager.swift` | `loadFiles` 里按次设置 `options/keep-open`：单文件播放设 `yes`，播完停在结尾而不是清空当前文件，`time-pos` / `duration` 与最后一帧都还在；多文件播放表和蓝光（`bd://` 自带标题播放表）保持 `no`，照常自动切下一个。抽出 `nonisolated struct PlaybackEndTracker` 负责「是否已播完」判定：`eof-reason == "eof"`，或引擎回到 idle 且最后看到的位置已在片尾 5 s 内（bd:// 等不认 keep-open 的源走这条路）；从结尾重新起播会自动作废。UI 在 `duration` 读不到时用记录下来的时长钉在 100%、剩余 `-00:00`；`playbackEngineSnapshot` 同源返回结尾位置，退出时按「已看」落库，NeoDB 收割观看历史也吃这个结论 |
| `OmniPlay/DirectPlaybackWindowManager.swift` | 重写全屏与关闭时序：窗口先按整屏可见区域建好并上屏，0.05 s 后再切全屏；播放器（`PlayerScreen`）等全屏动画落定或 2.5 s 超时后才装进窗口，mpv 不再在动画中途绑尺寸。过渡标记改成本类维护并配 3 s 超时清扫，`windowWillEnter/ExitFullScreen` 会重新续期，通知丢了也能自愈。`closeCurrentWindow()` 先无条件发 `.standalonePlayerShouldClose` 落库停机，再处理窗口：需要退出全屏时挂 2.5 s 看门狗，最多再试两次退出全屏，最后强制 `orderOut`，不会再出现关不掉的窗口 |
| `OmniPlay/DirectFilePlaybackManager.swift` | 去掉开播时那次 `.libraryUpdated` 广播（`direct` 条目本来就不上墙），少一次 MainActor 跳转，也让海报墙在 mpv 冷启动期间安静待着 |
| `OmniPlay/PlayerScreen.swift` | `ensureComfortableWindowSize()` 对独立播放窗口直接返回，窗口尺寸归 `DirectPlaybackWindowManager` 管 |
| `OmniPlayTests/DirectPlaybackTests.swift` | 新增 `PlaybackEndTrackerTests` 5 项：keep-open 停在结尾、idle 兜底认片尾、半路 idle 不算播完、从结尾重播后状态作废、刚换片没时长不算播完 |

定时器都排在全屏动画之后：动画在本机实测约 1.5 s（退出）到 3.4 s（进入），所以内容安装 2.5 s、过渡清扫 3 s、关闭看门狗 2.5 s，正常动画不会被兜底抢活。

## 3. 验证

- 单测：`xcodebuild test -only-testing:OmniPlayTests` 全绿 66 项（含新增 5 项）。
- `options/keep-open` 在运行期可写：真机日志里没有失败行，控制队列照常走到 `loadfile`（写失败会打印 `设置 options/keep-open=... 失败：...`）。
- 真机走了一遍 4 秒测试片段（`open -a 觅影.app /tmp/omniplay-short.mp4`）：
  - 首帧就是全屏，画面居中无偏移，控制条与进度条正常；
  - 播完界面为 `00:03` / `-00:00`、进度条走满、按钮回到播放态；
  - 关闭时 `snapshot ... progress=3.96 duration=4.0`，按 `>= 0.95` 记为已看，`windowDidExitFullScreen -> continue hide` 正常回到主窗口，看门狗不需要插手。
- 两平台编译：ARM（`arch=arm64`）与 x64（`arch=x86_64`）`xcodebuild build` 均通过，两侧源码 `cmp` 一致。
- 未覆盖：4 K HDR/DV 长片的完整播放流畅度只能靠使用者实机感受；远程源与 `bd://` 走的是 idle 兜底路径（不设 keep-open），尚未逐个源实测。
- 补测说明：第三轮跑测时显示器已进入睡眠，截图全黑，只留下「运行期 keep-open 写入未报错、控制队列正常推进到 loadfile」这条日志证据；界面结论以前两轮（15:03 那次）的截图与日志为准。
