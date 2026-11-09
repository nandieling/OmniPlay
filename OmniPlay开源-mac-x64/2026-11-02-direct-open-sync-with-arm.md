# 与 ARM 同步：直开播放首秒音画/字幕/进度条（1.10）

日期：2026-11-02　范围：mac x64　基准：ARM 工程 `OmniPlay开源-mac-ARM`

ARM 侧修掉了「直接打开视频文件后头几秒声音画面在走、进度条钉住、字幕缺席」的问题，
根因与验证记在 `OmniPlay开源-mac-ARM/docs/2026-11-02-direct-open-av-subtitle-progress-sync.md`。

本轮把改动带到 x64：`OmniPlay/MPVPlayerManager.swift` 单文件从 ARM 整份覆盖，覆盖前后先 `diff` 确认
两边该文件在改动前逐字节一致，因此除这三处外没有别的差异——

- `setDrawable` 在控制队列上比掉「同一个 wid + 同一份图层像素尺寸」的重复写入，`force` 不再等于无条件重写；
  `loadFiles` 换片时清掉这本账。
- 补绑图层把 `vid` 翻回 `auto` 之前先按暂停，等 mpv 报出与图层相符的输出尺寸（最长 2.5 秒兜底）再放行，
  续播前做一次 `seek … absolute+exact` 冲掉旧管线。
- 有续播进度时把发布时间播种到续播位置，进度条不再从 `00:00` 起步。

实测反馈「有续播进度会先播一两秒、停一下、再继续」，ARM 侧同日又补一刀：图层没绑上这一趟在
`loadfile` 之前就用 `pause` 属性把播放一起按住（不用 `loadfile` 的 per-entry `pause=yes`，实测那样
mpv 解析不出这条命令，时长都读不到），等图层与视频输出都就位才放行，只起步一次；纯音频、图层超时没绑上、
用户在按住期间自己按播放，这三条路径各自收口。x64 侧同样整份覆盖并重新编译通过。

验证：`xcodebuild build -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug
-destination "platform=macOS,arch=x86_64" -skipPackageUpdates` → BUILD SUCCEEDED。
运行时行为在 ARM 机器上实测（同一份代码），x64 侧无额外分支逻辑。

版本号保持 1.10 / build 1，本轮没有再动。
