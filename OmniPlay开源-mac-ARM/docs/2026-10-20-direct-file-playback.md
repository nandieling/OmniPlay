# 直接打开视频文件播放

日期：2026-10-20　适用端：macOS（ARM 基准版，x64 同步）　状态：已实现

## 1. 目标与现状

不经媒体库，双击或选择单个视频文件就能播。同时要能把觅影设成视频文件的默认打开应用。

代码里早就有 `DirectFilePlaybackManager` 和 `DirectPlaybackWindowManager`：前者把文件登记成 `direct` 条目并交给播放器，后者负责独立播放窗口（含全屏、退出后回主窗口、光标恢复）。缺的是入口和收尾：

| 缺口 | 后果 |
| --- | --- |
| `Info.plist` 没有 `CFBundleDocumentTypes` | 访达的「打开方式」里根本没有觅影，`application(_:open:)` 永远不触发 |
| `handleOpen` 只把请求塞进 `pendingRequest`，没有人消费 | 就算文件递进来了也不会开播 |
| 没有「打开视频文件…」菜单 | 应用内没有入口 |
| `setAsDefaultVideoPlayer()` 用已废弃的 `LSSetDefaultRoleHandlerForContentType`，且没有 UI 调用 | 用户没法一键设为默认，也看不到当前状态 |

## 2. 实现

| 文件 | 改动 |
| --- | --- |
| `OmniPlay/Info.plist` | 新增 `CFBundleDocumentTypes`：`public.movie`、`public.audiovisual-content`、`public.iso-image` 加 mkv / rmvb / flv / webm / m2ts / ts / wmv 的具体 UTI，`CFBundleTypeRole = Viewer`、`LSHandlerRank = Alternate` |
| `OmniPlay/DirectFilePlaybackManager.swift` | 扩展名列表改成 `static`，配套 `supportedContentTypes`；入库成功后直接调 `DirectPlaybackWindowManager.shared.open(_:)`；新增 `presentOpenPanel()`、`clearOpenError()`、`reportDroppedNothing()`；默认打开方式改用 `NSWorkspace.setDefaultApplication(at:toOpen:)`，逐类型上报成功与失败，另给 `extensionsNotOwnedYet()` 查当前状态 |
| `OmniPlay/OmniPlayApp.swift` | 文件菜单加「打开视频文件…」（⌘O），替换掉没有意义的「新建窗口」 |
| `OmniPlay/PosterWallView.swift` | 海报墙支持把文件从访达拖进来直开；直开失败弹提示（格式不支持 / 无权限 / 解析失败） |
| `OmniPlay/SettingsView.swift` | 「基础设置」末尾加一行「视频文件默认打开方式」：显示是否已是默认、一键设为默认、说明文案 |
| `OmniPlayTests/DirectPlaybackTests.swift` | 新增：扩展名能解析成 UTI、都归在 `public.movie` / `public.audiovisual-content` / `public.iso-image` 之下、打包后的 Info.plist 确实声明了这些类型 |

`direct` 条目本来就被 `Movie.fetchVisibleLibrary` 用 `videoFile.mediaType != 'direct'` 排除，所以直开的文件不会污染海报墙；「访达直开」这个虚拟媒体源的 `protocolKind` 是 `.direct`，扫描器会直接拒绝，不会被当成文件夹去扫。

## 3. 三个入口

1. 访达：右键「打开方式」选觅影，或在设置里设为默认后直接双击；ISO 蓝光镜像也在声明范围内。
2. 应用内：文件 > 打开视频文件…（⌘O），支持一次多选，取第一个能播的。
3. 拖拽：把文件拖到海报墙窗口上。

开播后是独立全屏窗口，退出播放回到主窗口；已经在放的时候再开一个文件会换片而不是再开一个窗口。

## 4. 验证

- `xcodebuild build` 通过。
- `xcodebuild test -only-testing:OmniPlayTests` 全绿 62 项，含新增 `DirectFilePlaybackTests` 3 项；其中 `bundleDeclaresMovieTypes` 读的是打包后的 `Bundle.main.infoDictionary`，能证明 `GENERATE_INFOPLIST_FILE` 合并后 `CFBundleDocumentTypes` 真的进了 app bundle。
- 设为默认的实际效果依赖本机 LaunchServices 状态，需要用户在真机上点一次按钮确认；未接管的类型会在界面上按扩展名列出来。
