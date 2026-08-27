# 觅影 OmniPlay

觅影 OmniPlay 是一款原生开发的海报墙播放器，支持 macOS、Windows、Docker 和安卓 TV。macOS 版采用 Swift / SwiftUI 开发，Windows 版采用 C# / .NET / Avalonia UI。底层播放器核心为 MPVKit-GPL / libmpv / FFmpeg 相关组件。


## 软件截图
![览影首页](https://img3.pixhost.cc/images/5255/763004648_1.jpg)
![览影详情页](https://img3.pixhost.cc/images/5255/763004667_2.jpg)
## 功能特色

### UI
- UI简洁且美观，海报墙没有做过多的分类功能，只有搜索、排序功能。

### 海报墙媒体库

- 支持海报墙和分集剧照
- 采用TMDB自动刮削，豆瓣手动辅助刮削，增加了更宽松的刮削规则和自定义编辑功能。避免重命名和硬链接。

### 媒体源管理

- 支持添加本地文件夹、OmniPlay Docker、WebDAV、SMB、Plex、Emby 和 Jellyfin。macOS 版不支持 SMB 直连，请先在访达中挂载 SMB，再在软件中将其作为本地文件夹添加。
- macOS ARM 版支持局域网预扫描和手动添加 OmniPlay Docker、WebDAV、Plex、Emby 与 Jellyfin。
- macOS ARM 版支持 Lucky STUN 穿透媒体源，可登录 Lucky 管理端、选择穿透规则并自动刷新穿透地址。
- 不需要将电影、剧集分不同文件夹进行挂载，软件自动识别。

### 自动扫描与刮削

- 软件不内置、也不提供公共 TMDB 源或公共 API 凭据。
- 使用 TMDB 自动刮削前，必须在 TMDB 官网注册并在设置中填写自己的 API Key 或 v4 Read Access Token。
- macOS ARM 版无法直连 TMDB API 时，可在设置中填写自建的反向代理网关；可参考下文 Cloudflare Workers 部署说明。

### 离线缓存

- 支持将本地目录、访达已挂载的 SMB 和 WebDAV 媒体缓存到选定目录。
- 电影、整季和单集均有缓存入口，多集任务按剧集顺序依次执行。
- 离开详情页后任务会继续，首页左上角会显示当前文件、任务顺序和总进度。
- 点击缓存进度环可在确认后取消；任务开始前会进行存储空间预检，并在所需空间外预留 1 GiB。
- 未完成内容以同盘 `.part` 文件直接写入目标磁盘，完成后再转为最终文件。

### 使用指南
####  docker版安装
1. 以群晖为例：先去右上角code下载源代码，将OmniPlay-docker文件夹复制到NAS。文件夹中有compose.yml文件，compose.hwaccel.yml为硬解版，compose.yml为非硬解版，按需求取用。由于镜像包较大，建议去Releases下载docker镜像包，解压成tar格式，导入NAS docker中。
2. 打开群晖container manager，点映像-操作-导入-选择tar镜像包。
3. 点项目-新增，项目名称，点路径，选择导入的OmniPlay-docker文件夹。
4. 修改compose.yml。 
      - ./data:/var/lib/omniplay  ./data为docker数据目录，修改为自己的目录
      - ./cache:/var/cache/omniplay 为缓存目录，有HLS缓存和字幕缓存，这两个缓存文件根据后续设置可能很大，建议放在剩余储存空间大的目录中。
      - ./media:/media:ro  为影视文件目录。
 5. 点下一步，等安装完成。
 #### docker版设置
1. 浏览器输入http://NAS IP:45722 打开docker容器页面，注册用户名和密码，注意后续mac和安卓TV端连接docker端也是这个用户密码。
2. 点右上角设置，输入自己的 TMDB API Key 或 v4 Read Access Token。无法直连 TMDB API 时，Docker 版可配置标准出站 HTTP/SOCKS 代理（例如 v2rayA），然后保存并检测连通性。
3. 也可以点运行自检检测下配置有没有问题，比如GPU硬解是否启用。
4. 后面要注意的设置是HLS缓存和字幕缓存上限，避免硬盘存储空间被塞满。
5. 右上角加号添加媒体目录，挂载后自动进行扫描、刮削、字幕缓存。

#### docker版说明
1. docker版直接播放因为是在浏览器中播放，浏览器不支持MKV、蓝光格式，需要先转换为HLS。效率天然比客户端低，所以不建议直接播放。
2. 由于不是每台电视的硬解能力都很强，特别是4K蓝光，实时烧录字幕可能出现画面卡顿或者没有字幕的情况。因此加入字幕预缓存为VTT文本格式功能。缺点是需要提前预缓存和占用一定空间，实测字幕缓存占用空间比较小，但是电影多的话缓存时间会很长，后续加一个优先缓存功能。
3. 目前ARM mac和安卓TV端都可以接入docker服务端，并且有播放进度同步功能。但是已知的bug是docker端播放会将进度归零，这个bug后续会尝试修复。
#### 安卓TV
1. 安装：下载apk安装
2. 点预扫描或输入docker版地址，输入docker版注册的用户名和密码登录。
#### 安卓TV说明
1. 考虑到电视安装代理不方便，扫描刮削也不方便。TV版没有接入webdav、smb、plex、emby、jellyfin功能。需要先安装docker版再接入。
2. 设置里有字幕兼容播放模式和高性能直出播放模式。前者的色彩管理和字幕兼容性更好，但是资源负载更高，如果芯片性能足够强，建议选这个模式。如果播放4K卡顿，建议切换高性能直出播放模式。不过高性能直出只针对4K或者DV/HDR，其它格式仍然是兼容模式。高性能直出需要提前进行字幕缓存，不然会出现没有字幕的情况。
#### mac、win  

1. 安装：下载安装包，安装。
2. 填入自己的 TMDB API Key 或 v4 Read Access Token 并检测连通性。macOS ARM 版无法直连时，可使用下文的 Cloudflare Workers 反向代理网关。
3. 添加媒体源，如果有ARM mac和安卓TV播放进度同步功能需求，建议接入omniplay docker版媒体源。x64 mac和win版尚未更新，暂无接入docker版功能。
4. ARM mac版加了豆瓣手动刮削功能。没有自动匹配是因为豆瓣没有公开的api，自动匹配会被阻断，体验不佳。所以如果没有代理，也可以用豆瓣手动一个个匹配。缺点是海报画质可能没有TMDB好，以及没有分集剧照。手动匹配做了限制，以避免阻断。其实docker版也加了这个功能，但经测试每次都被阻断。不过mac版手动匹配的豆瓣评分等信息，会自动同步到docker版，进而自动同步到安卓TV端。

## 使用 Cloudflare Workers 反向代理 TMDB API

当前网络无法直接访问 `https://api.themoviedb.org` 时，macOS ARM 版可部署一个 Cloudflare Worker 作为 API 反向代理。Worker 只负责转发请求；TMDB 凭据仍由用户在 OmniPlay 中填写，不要把 API Key 或 v4 Token 写入 Worker 源码。

> 这是 TMDB API 反向代理网关，不是通用 HTTP/SOCKS 出站代理。请不要将 Worker 地址填入 Docker 或 Windows 的普通代理服务器字段。

### 1. 创建 Worker

**第一步：创建 Worker**

1. 登录 [Cloudflare 控制台](https://dash.cloudflare.com/)，进入 **Compute (Workers & Pages)** -> **Workers**。
2. 点击 **Create application** -> **Create Worker**，命名为 `tmdb-proxy` 并点击 **Deploy**。

**第二步：配置反代代码**

1. 点击 **Edit code**，将默认代码全部清空，替换为以下脚本：

JavaScript

```
// 自定义你的访问密码/暗号（建议字母+数字）
const SECRET_TOKEN = "mySecret888"; 

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    // 1. 验证路径是否以你的密钥开头
    const secretPrefix = `/${SECRET_TOKEN}`;
    if (!url.pathname.startsWith(secretPrefix)) {
      return new Response("403 Forbidden: Invalid Secret Token", { status: 403 });
    }

    // 2. 剥离密钥前缀，还原真实请求路径
    let realPath = url.pathname.replace(secretPrefix, "") || "/";

    // 3. 分流转发到 TMDB API 或 图片服务器
    let targetHost = "api.themoviedb.org";
    if (realPath.startsWith("/image/")) {
      targetHost = "image.tmdb.org";
      realPath = realPath.replace(/^\/image/, "");
    } else if (realPath.startsWith("/t/p/")) {
      // 兼容直接请求 /t/p/ 的海报路径
      targetHost = "image.tmdb.org";
    }

    const targetUrl = `https://${targetHost}${realPath}${url.search}`;

    const modifiedRequest = new Request(targetUrl, {
      method: request.method,
      headers: request.headers,
      body: request.body,
      redirect: "follow",
    });

    // 伪装 Host
    modifiedRequest.headers.set("Host", targetHost);
    modifiedRequest.headers.set("Referer", `https://${targetHost}`);

    return fetch(modifiedRequest);
  },
};
```

1. 点击右上角 **Deploy** 保存。

**第三步：绑定自定义域名（关键）**

- 由于 `*.workers.dev` 默认子域名在国内通常无法直连，进入该 Worker 的 **Settings** -> **Domains & Routes**。
- 点击 **Add** -> **Custom Domain**，绑定你托管在 Cloudflare 上的二级域名（例如 `tmdb.yourdomain.com`）。
### 2. 在 OmniPlay 中配置

1. 前往 [TMDB 官网](https://www.themoviedb.org/) 注册并获取自己的 API Key 或 v4 Read Access Token。
2. 打开 OmniPlay 设置中的 TMDB 配置。
3. 在 `API Key / v4 令牌` 中填写自己的凭据。
4. 在 `TMDB API 代理网关` 中填写 Worker 根地址：

   ```text
   https://tmdb.yourdomain.com/mySecret888
   ```


5. 点击“验证”或“检测”。验证成功后，TMDB API 请求会经 Worker 转发。


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
MARKETING_VERSION = 1.7
CURRENT_PROJECT_VERSION = 1
MACOSX_DEPLOYMENT_TARGET = 14.0
DEVELOPMENT_TEAM =
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
