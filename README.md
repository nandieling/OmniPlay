# 觅影 OmniPlay

觅影 OmniPlay 是一款原生开发的海报墙播放器，支持mac、win双系统。mac采用swift开发，win采用C# + .net + Avalonia UI。底层播放器核心为 MPVKit-GPL / libmpv / FFmpeg 相关组件。 ios版正在开发中。

## 功能特色

### UI
- UI简洁且美观，海报墙没有做过多的分类功能，只有搜索、排序功能。

### 海报墙媒体库

- 支持海报墙和分集剧照
- 采用TMDB刮削，增加了更宽松的刮削规则和自定义编辑功能。避免重命名和硬链接。

### 媒体源管理

- 支持添加本地文件夹、WebDAV、SMB。mac版因为开发一直有bug，不支持SMB直连，请在访达中挂载SMB，再在软件中添加本地文件夹，间接连接SMB。可以将访达挂载的SMB添加到开机自启



### 自动扫描与刮削
- 支持公共 TMDB 源，也支持自定义 TMDB API Key / v4 Token。公共源API做了限制，建议注册TMDB后获取API。

### 离线缓存

- 支持一键将SMB、WebDAV下的影视离线至电脑，方便外出观影。

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
