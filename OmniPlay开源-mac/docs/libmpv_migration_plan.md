# libmpv / FFmpeg 评估与迁移计划

## 1. 依赖与许可证盘点
| 组件 | 来源 / 版本 | 作用 | 许可证状态 | 风险说明 |
| --- | --- | --- | --- | --- |
| MPVKit-GPL (SwiftPM) | `https://github.com/mpvkit/MPVKit` @ 0.41.0（`OmniPlay.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved`） | 提供 libmpv 封装与 FFmpeg 预编译产物 | 产品名即带 GPL；传递依赖 libmpv & FFmpeg GPL 变体 | 只要直接链接就必须遵循 GPL，App Store 分发受限 |
| `Libmpv.xcframework` | 多平台切片（`Libmpv.xcframework/Info.plist`） | 播放核心 | `mpv` 客户端 API 为 ISC，但核心默认 GPLv2+，除非构建时 `-Dgpl=false`（`Libmpv.xcframework/ios-arm64/Libmpv.framework/Headers/mpv/client.h`） | 当前未知是否禁用 GPL 特性，须重新编译才能确认/转 LGPL |
| `Libav*`（codec/format/filter/util/swresample/swscale/device） | 定制 FFmpeg 构建（`Libavcodec.xcframework/ios-arm64/Libavcodec.framework/Headers/config.h` 等） | 解码、滤镜、网络 I/O | 配置中含 `--enable-gpl --enable-nonfree`，`FFMPEG_LICENSE` 标记为 “nonfree and unredistributable”；`CONFIG_GPL=1`、`CONFIG_NONFREE=1`、`CONFIG_LGPLV3=0` | 该产物无法合法随闭源 App 分发，且与 LGPL 不兼容 |
| GRDB.swift | `GRDB.swift-master` 子模块，MIT 许可 (`GRDB.swift-master/LICENSE`) | 本地数据库 | MIT | 可继续使用；注意保留版权声明 |

## 2. LGPL 化技术路径

### 2.1 FFmpeg 重新构建
1. Fork MPVKit 构建脚本，新增 “LGPL-only” preset：去掉 `--enable-gpl`、`--enable-nonfree`、所有 GPL-only 库（如 x264/x265/libuavs3d 若不提供 LGPL 许可）。
2. 仅保留 VideoToolbox、libdav1d、libplacebo（需确认其 MPL/GPL 交叉许可）等兼容组件，必要时改用苹果硬件解码器和开源 AAC/ALAC 实现。
3. 针对 iOS / iOS Simulator / tvOS / tvOS Simulator / macCatalyst / visionOS 各自产出静态库；编译时加入 `--enable-shared=0 --enable-static=1`，并记录 toolchain 版本，便于 App Store 提交时提供源代码和 build script。
4. 在产物中保留 `config.h`、`versions.txt` 以佐证禁用 GPL，CI 中添加自动检查（grep `CONFIG_GPL`、`CONFIG_NONFREE` 等）。

### 2.2 mpv/libmpv 重新构建
1. 使用官方 Meson 构建：`meson setup build-ios --cross-file ios-arm64.txt -Dlibmpv=true -Dgpl=false -Dcplayer=false -Dlua=disabled -Dgl=disabled -Dmetal=enabled`。
2. 链接上一步产出的 LGPL 版 FFmpeg + 其它依赖（libplacebo、lcms2 等需确认许可证兼容）；若某依赖无法满足 LGPL，要么移除功能，要么替换实现。
3. 生成新的 `Libmpv.xcframework`，确保包含 iOS、tvOS、macOS、Mac Catalyst、visionOS 切片；为每个切片保留 `Info.plist` 与 `LICENSE`，并在仓库根目录提供源代码下载入口。
4. 运行基础功能测试（解码 H.264/H.265、字幕、HDR、音轨切换），同时验证 `mpv_render_context_*` Metal 路径在 iOS/tvOS 可行。

### 2.3 合规动作
1. 产出《开源合规说明》，列出每个组件的许可证、源码获取方式（例如 GitHub 仓库 + 编译指令）。
2. 更新应用内 “关于 / 开源信息” 页面，至少列出 mpv、FFmpeg、GRDB 及其许可证。
3. 若仍保留任何 GPL 代码（哪怕独立进程），必须提供完整源代码下载，与 App 链接/通信方式要满足 GPL 条款。

## 3. iOS / tvOS 渲染原型与架构改造
1. **抽象播放器内核**：从 `MPVPlayerManager` 中剥离 AppKit 依赖，定义 `MetalDrawableProvider` 协议（macOS 提供 NSView/CAMetalLayer，iOS/tvOS 由 UIView/CAMetalLayer 实现）。参考 `OmniPlay/MPVPlayerManager.swift` 与 `OmniPlay/MPVVideoView.swift` 当前绑定逻辑。
2. **引入 Swift Package `OmniPlayerCore`**：承载 `MPVPlayerManager`、会话状态、UserDefaults 配置等，与 UI 模块解耦，方便多 target 复用。
3. **最小原型 App**：在新建的 iOS/tvOS target 中，创建 `UIViewRepresentable`（或 SwiftUI `View`) 包裹 `CAMetalLayer`，通过 mpv render API（`mpv_render_context_create`、`mpv_render_context_render`）驱动；优先验证播放、暂停、seek、音轨/字幕切换。
4. **平台特性适配**：
   - iOS：后台音频、Now Playing、PiP、遥控事件。
   - iPadOS：多窗口（UIScene）、Pointer、拖放。
   - tvOS：Focus Engine、遥控器手势、Top Shelf。
5. **测试矩阵**：为核心播放器写 Swift 单测（配置解析、命令拼接）+ UI 自动化（XCTest / UITest）验证渲染 Surface 生命周期；CI 链接 Step 2 的自定义二进制。

## 4. 初步排期（理想状态）
1. 第 1 周：完成依赖梳理、LGPL 方案评审、CI 环境准备。
2. 第 2–3 周：产出 LGPL 版 FFmpeg、libmpv，并通过基础播放验证。
3. 第 3–4 周：重构播放器核心、跑通 iOS/tvOS 原型、补充平台特性。
4. 第 5 周起：集成 DTS 屏蔽策略、完善测试与合规文档，准备 TestFlight。
