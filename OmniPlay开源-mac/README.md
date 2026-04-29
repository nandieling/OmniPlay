# 觅影 OmniPlay

觅影 OmniPlay 是一款使用 Swift / SwiftUI 编写的 macOS 视频媒体库播放器，重点面向本地硬盘、外接硬盘和 NAS WebDAV 片库场景。它可以扫描媒体文件、自动刮削 TMDB 元数据、生成海报墙和分集剧照，并使用 mpv/FFmpeg 播放器栈完成本地与 WebDAV 媒体播放。

本仓库是 macOS 开源版工程，项目入口为 `OmniPlay.xcodeproj`，主应用显示名称为“觅影”。当前播放器链路使用 MPVKit-GPL / libmpv / FFmpeg 相关组件，适合自编译、本机使用和开源再分发评估；如果要对外正式分发，请先完成签名、公证和开源许可证合规审查。

## 功能特色

### 海报墙媒体库

- 首页以海报墙展示所有已入库影视。
- 支持继续播放、播放进度记录、已播/未播状态。
- 支持按名称、评分、年份排序。
- 支持首页搜索。
- 支持电影与剧集自动识别。
- 支持多季剧集、特别篇、分集排序与下一集播放。

### 本地文件夹媒体源

- 支持添加本地文件夹、外接硬盘、已挂载的 NAS 目录。
- 支持启用/关闭媒体源。
- 支持重命名媒体源。
- 支持移除媒体源及其索引。
- 支持保留或清理本地缓存海报。

### WebDAV 媒体源

- 支持 `http` / `https` WebDAV。
- 支持局域网 WebDAV 服务自动预扫描。
- 打开首页右上角媒体源管理弹窗时会自动预扫描 WebDAV。
- 预扫描出的 WebDAV 可点击进入登录弹窗。
- 支持手动输入 WebDAV 地址，流程与预扫描入口一致。
- 登录后可浏览 WebDAV 共享文件夹。
- 在共享文件夹右侧点击星标即可挂载该文件夹。
- 标星挂载后立即扫描、入库并触发刮削。
- WebDAV 用户名和密码保存到 macOS Keychain。
- 删除 WebDAV 源时可在设置中选择是否同步删除保存的登录凭据。

### 自动扫描与刮削

- 支持启动时自动扫描并同步库。
- 支持手动点击同步按钮重新扫描。
- 扫描后自动将未匹配文件加入刮削队列。
- 使用 TMDB 获取电影/剧集标题、简介、评分、海报等数据。
- 支持公共 TMDB 源，也支持用户填写自己的 TMDB API Key / v4 Token。
- 支持自定义刮削语言，当前支持简体中文和英文界面/刮削语言。

### 分集剧照

- 支持从 TMDB 获取分集剧照。
- 支持为分集手动替换本地图片。
- 支持手动修改影片名、季号、集号。
- 分集自定义副标题默认留空，不从文件名自动提取。
- 只有用户手动填写并保存副标题后，副标题才会显示在分集剧照下方。

### 播放体验

- 使用 mpv / FFmpeg 相关播放栈。
- 支持本地文件播放。
- 支持 WebDAV 远程文件播放。
- 支持续播点保存与恢复。
- 支持默认音轨偏好。
- 支持默认字幕偏好。
- 支持播放画质模式：流畅优先、平衡、画质优先。
- 支持直接播放单个视频文件，并在窗口中播放。

### 离线缓存

- 支持设置本地离线缓存目录。
- 支持在详情页将支持缓存的媒体加入离线缓存。
- 支持整季缓存入口。
- 对不支持缓存的远程源会跳过并提示。

### 偏好设置

- 启动时自动扫描并同步库。
- 删除文件夹时保留本地缓存海报。
- 快速悬停提示。
- 媒体源显示真实路径。
- 删除 WebDAV 源时是否删除 Keychain 凭据。
- 离线缓存目录。
- 主题配色。
- 软件与刮削语言。
- 默认音轨、默认字幕。
- 播放画质模式。
- TMDB 公共源和自定义 API。
- 开源组件信息与许可证线索。

## 使用教程

### 1. 首次启动

1. 打开“觅影”。
2. 首次启动时，应用会在用户目录下创建数据库：

   ```text
   ~/Library/Application Support/OmniPlay/omniplay.sqlite
   ```

3. 首页为空时，右上角点击“文件夹 +”图标进入媒体源管理。

### 2. 添加本地文件夹

1. 点击首页右上角“文件夹 +”按钮。
2. 在“新增媒体源”中点击“添加本地文件夹”。
3. 在系统文件选择器中选择包含视频的文件夹。
4. 添加完成后，应用会自动触发扫描和刮削。

建议目录结构：

```text
Movies/
  霸王别姬 (1993)/
    Farewell.My.Concubine.1993.1080p.mkv
TV Shows/
  绝命毒师/
    Season 01/
      Breaking.Bad.S01E01.mkv
      Breaking.Bad.S01E02.mkv
```

文件名越规范，自动识别和刮削结果越稳定。

### 3. 通过预扫描添加 WebDAV

1. 点击首页右上角“文件夹 +”按钮。
2. 媒体源管理弹窗打开后会自动预扫描局域网 WebDAV。
3. 扫描到的 WebDAV 会在弹窗底部按列表显示。
4. 点击目标 WebDAV 服务。
5. 在登录弹窗中输入用户名和密码。
6. 点击“保存”。
7. 进入 WebDAV 共享文件夹浏览界面。
8. 找到要作为媒体库的文件夹。
9. 点击该文件夹右侧星标。
10. 应用会将该文件夹加入已挂载媒体源，并立即扫描、入库和刮削。

常见 WebDAV 地址示例：

```text
http://192.168.0.100:5005/电影
https://nas.example.com:5006/dav/Movies
```

注意：

- 端口 `5005` 通常是 HTTP WebDAV。
- 端口 `5006` 通常是 HTTPS WebDAV。
- 具体端口以 NAS WebDAV 服务配置为准。
- 如果服务根目录下还有多个共享目录，建议先填服务根地址，再进入文件夹浏览界面选择具体媒体目录。

### 4. 手动添加 WebDAV

1. 点击首页右上角“文件夹 +”按钮。
2. 在“新增媒体源”中点击“添加 WebDAV 媒体源”。
3. 在登录弹窗中手动输入 WebDAV 地址。
4. 输入用户名和密码。
5. 点击“保存”。
6. 后续流程与预扫描 WebDAV 完全一致：进入文件夹浏览，点击星标挂载目录。

手动添加适合以下情况：

- 预扫描没有发现设备。
- WebDAV 服务不在同一局域网。
- 使用域名、反向代理或公网地址访问 NAS。
- WebDAV 路径不是默认服务根。

### 5. 管理媒体源

打开首页右上角“文件夹 +”按钮后，可对已挂载源执行：

- 关闭：暂时隐藏该源内容，索引保留一段时间。
- 开启：重新启用该源，并立即扫描。
- 重命名：只修改应用内显示名称，不改动真实路径。
- 移除：删除媒体源及其影片索引。

如果移除的是 WebDAV 源，是否同时删除 Keychain 中保存的账号密码，取决于偏好设置里的“移除 WebDAV 源时同时删除保存的登录凭据”。

### 6. 扫描与同步

首页右上角“循环箭头”按钮用于手动同步：

1. 扫描所有启用的媒体源。
2. 添加新发现的视频文件。
3. 移除源内已不存在的视频索引。
4. 对未匹配文件执行 TMDB 刮削。
5. 补齐分集剧照。

如果开启“启动时自动扫描并同步库”，应用启动后会自动执行一次同步。

### 7. 播放与续播

1. 在首页点击影片海报进入详情页。
2. 点击“开始播放”或“继续播放”。
3. 播放进度会自动保存。
4. 剧集播放接近结尾时可切换下一集。
5. 详情页中可手动切换已播/未播。

### 8. 分集剧照与副标题

在剧集详情页：

1. 将鼠标悬停到分集剧照。
2. 点击右上角编辑按钮。
3. 可修改影视名称、季号、集号。
4. “自定义副标题”默认是空的。
5. 手动输入副标题并保存后，副标题会显示在分集剧照下方。
6. 可选择本地图片替换该集剧照。

示例：

```text
第 1 季 第 3 集 · 导师版
```

其中“导师版”只有在手动填写并保存后才会显示。

### 9. TMDB 配置

进入偏好设置的“刮削服务 (TMDB)”：

- 默认可使用公共 TMDB 源。
- 长期使用建议填写自己的 TMDB API Key 或 v4 Token。
- 填写后点击“验证”检查密钥可用性。
- “软件与刮削语言”会影响 TMDB 元数据语言。

如果没有可用 TMDB 凭据，刮削、海报和剧照获取会受影响。

### 10. 离线缓存

1. 打开偏好设置。
2. 在“离线缓存”中设置保存位置。
3. 进入影视详情页。
4. 打开离线缓存模式。
5. 对支持缓存的剧集或整季执行缓存。

远程源是否支持缓存由当前实现策略决定；不支持时应用会跳过并提示。

## 自行编译教程

### 环境要求

建议环境：

- macOS，版本需满足项目当前 `MACOSX_DEPLOYMENT_TARGET`。
- Xcode，需包含项目当前使用的 macOS SDK。
- 可联网环境，用于首次解析 Swift Package 与下载二进制产物。
- Apple Developer 账号或本地签名环境，用于导出可分发 App。

当前工程配置中：

```text
PRODUCT_BUNDLE_IDENTIFIER = nan.OmniPlay
PRODUCT_NAME = 觅影
MARKETING_VERSION = 1.0
CURRENT_PROJECT_VERSION = 1
MACOSX_DEPLOYMENT_TARGET = 26.2
DEVELOPMENT_TEAM = DT95MB3RG4
ENABLE_HARDENED_RUNTIME = YES
```

如果你的系统或 Xcode SDK 低于工程配置，请在 Xcode 的 Build Settings 中调整 Deployment Target 后再编译。如果不是项目原签名团队成员，请在 Xcode 中修改 Team 和 Bundle Identifier，或改成本机调试签名后再归档导出。

### 依赖说明

主要依赖包括：

- SwiftUI / AppKit：界面与 macOS 集成。
- GRDB：SQLite 数据库访问，仓库内以本地 Swift Package 形式存在于 `GRDB.swift-master/`。
- MPVKit-GPL：mpv 播放器相关 Swift Package。
- FFmpeg / libmpv 相关二进制框架：用于媒体播放与解码链路。
- TMDB API：用于媒体信息、海报和剧照刮削。

MPVKit 来源锁定在 `OmniPlay.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved`，当前为 `https://github.com/mpvkit/MPVKit` 的固定 revision。首次打开工程或首次命令行编译时，Xcode 会解析 Swift Package。若网络不可用，可能会停在依赖解析阶段。

### 使用 Xcode 编译运行

1. 打开项目：

   ```bash
   open OmniPlay.xcodeproj
   ```

2. 在 Xcode 顶部 Scheme 选择 `OmniPlay`。
3. 运行目标选择 `My Mac`。
4. 如果需要自己的签名，进入项目设置：

   ```text
   OmniPlay Target -> Signing & Capabilities
   ```

5. 修改 Team 和 Bundle Identifier。
6. 点击 Run 运行。

### 使用命令行 Debug 编译

在仓库根目录执行：

```bash
xcodebuild \
  -project OmniPlay.xcodeproj \
  -scheme OmniPlay \
  -configuration Debug \
  -sdk macosx \
  -derivedDataPath /tmp/omniplay-build \
  build
```

编译成功后，App 通常位于：

```text
/tmp/omniplay-build/Build/Products/Debug/觅影.app
```

### 使用命令行 Release 编译

```bash
xcodebuild \
  -project OmniPlay.xcodeproj \
  -scheme OmniPlay \
  -configuration Release \
  -sdk macosx \
  -derivedDataPath /tmp/omniplay-release-build \
  build
```

Release 产物通常位于：

```text
/tmp/omniplay-release-build/Build/Products/Release/觅影.app
```

### 归档 Archive

用于后续导出 `.app`、签名或分发：

```bash
xcodebuild \
  -project OmniPlay.xcodeproj \
  -scheme OmniPlay \
  -configuration Release \
  -sdk macosx \
  -archivePath /tmp/OmniPlay.xcarchive \
  archive
```

归档产物：

```text
/tmp/OmniPlay.xcarchive
```

### 导出 App

创建导出配置文件，例如 `/tmp/OmniPlayExportOptions.plist`：

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>method</key>
  <string>developer-id</string>
  <key>signingStyle</key>
  <string>automatic</string>
  <key>teamID</key>
  <string>你的 Team ID</string>
</dict>
</plist>
```

导出：

```bash
xcodebuild \
  -exportArchive \
  -archivePath /tmp/OmniPlay.xcarchive \
  -exportPath /tmp/OmniPlayExport \
  -exportOptionsPlist /tmp/OmniPlayExportOptions.plist
```

导出结果通常位于：

```text
/tmp/OmniPlayExport/觅影.app
```

如果只是本机调试，可以直接使用 Debug/Release build 产物；如果要分发给其他用户，建议使用 Developer ID 签名并完成公证。

### 打包为 DMG

导出 `.app` 后，可以先创建一个 DMG 根目录，把 App 和 `/Applications` 快捷方式放进去：

```bash
mkdir -p /tmp/OmniPlayDmgRoot
ditto "/tmp/OmniPlayExport/觅影.app" "/tmp/OmniPlayDmgRoot/觅影.app"
ln -sfn /Applications /tmp/OmniPlayDmgRoot/Applications
```

再用系统工具创建 DMG：

```bash
hdiutil create \
  -volname "觅影" \
  -srcfolder /tmp/OmniPlayDmgRoot \
  -ov \
  -format UDZO \
  /tmp/OmniPlay.dmg
```

如果要做正式分发，建议：

1. 使用 Developer ID 签名。
2. 对 `.app` 或 `.dmg` 执行 notarization。
3. 使用 `stapler` 附加公证票据。

示例：

```bash
xcrun notarytool submit /tmp/OmniPlay.dmg \
  --apple-id "你的 Apple ID" \
  --team-id "你的 Team ID" \
  --password "App 专用密码" \
  --wait

xcrun stapler staple /tmp/OmniPlay.dmg
```

## 常见问题

### 1. 首次编译卡在 Resolve Package Graph

通常是 Swift Package 或二进制产物下载较慢。请确认网络可访问 GitHub，并重试：

```bash
xcodebuild -resolvePackageDependencies -project OmniPlay.xcodeproj
```

### 2. WebDAV 预扫描找不到设备

可直接使用“添加 WebDAV 媒体源”手动输入地址。预扫描依赖局域网服务发现，以下情况可能无法发现：

- NAS 和电脑不在同一局域网。
- 路由器阻止 Bonjour/mDNS。
- WebDAV 服务未广播 `_webdav._tcp` 或 `_webdavs._tcp`。
- 使用公网域名或反向代理。

### 3. WebDAV 可以登录但扫描不到文件

请检查：

- 星标挂载的是具体媒体文件夹，而不是空目录。
- 目录下存在支持的视频格式。
- WebDAV 用户对该目录有读取权限。
- NAS WebDAV 服务允许 `PROPFIND`。

支持的视频扩展名包括但不限于：

```text
mp4, mkv, mov, avi, rmvb, flv, webm, m2ts, ts, iso, m4v, wmv
```

### 4. 刮削结果不准确

建议：

- 文件名包含片名和年份。
- 剧集使用 `S01E01`、`S01E02` 等规范命名。
- 在设置中配置自己的 TMDB API。
- 手动搜索或编辑元数据。

### 5. 分集副标题为什么不自动显示文件名片段

当前行为是有意设计：分集自定义副标题默认留空，不再从文件名自动提取。只有用户在编辑弹窗中手动输入并保存后，副标题才会显示在分集剧照下面。

### 6. 播放 WebDAV 文件失败

请检查：

- WebDAV 地址是否仍可访问。
- 用户名和密码是否正确。
- NAS WebDAV 服务是否启用。
- 文件是否被移动或删除。
- 应用是否有网络访问权限。

### 7. 数据库和缓存在哪里

数据库默认位置：

```text
~/Library/Application Support/OmniPlay/omniplay.sqlite
```

海报、剧照、离线缓存等数据由应用内部管理，离线缓存目录可在偏好设置中更改。

## 开发说明

### 代码结构

```text
OmniPlay/
  OmniPlayApp.swift                 App 入口与数据库初始化
  ContentView.swift                 主视图入口
  PosterWallView.swift              首页海报墙、媒体源管理、WebDAV 入口
  MovieDetailView.swift             详情页、分集卡片、播放入口
  PlayerScreen.swift                播放窗口 UI
  MPVPlayerManager.swift            mpv 播放管理
  MediaLibraryManager.swift         扫描、刮削、WebDAV 扫描
  DatabaseModels.swift              数据模型与 Keychain 凭据
  ThumbnailManager.swift            分集剧照获取与缓存
  PosterManager.swift               海报缓存
  OfflineCacheManager.swift         离线缓存
  SettingsView.swift                偏好设置
```

### 测试

仓库包含业务集成测试和 WebDAV Mock 测试：

```text
OmniPlayTests/
OmniPlayUITests/
```

可在 Xcode 中运行测试，或使用命令行：

```bash
xcodebuild \
  -project OmniPlay.xcodeproj \
  -scheme OmniPlay \
  -configuration Debug \
  -sdk macosx \
  -derivedDataPath /tmp/omniplay-test-build \
  test
```

## 许可证与三方组件

本项目包含或依赖多个开源组件和媒体框架。请在分发前确认：

- GRDB 的 MIT 许可证要求。
- mpv / FFmpeg / MPVKit 相关许可证要求。
- LGPL/GPL 组件的再分发义务。
- App 内“开源组件详情与许可证线索”页面展示的信息。
- `OmniPlay/OpenSourceLicenses/` 中随附的许可证文本。

如果你重新打包、修改播放器栈或替换二进制框架，请重新审查对应许可证和源码提供义务。

## 免责声明

觅影 OmniPlay 仅用于管理和播放用户有权访问的本地或 NAS 媒体文件。TMDB 元数据、海报和剧照版权归各自权利方所有。请遵守所在地区法律法规以及相关服务条款。
