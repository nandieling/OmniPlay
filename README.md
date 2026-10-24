# 觅影 OmniPlay

觅影 OmniPlay 是一款面向本地硬盘、外接硬盘、NAS 和家庭媒体服务器场景的媒体库播放器：扫描媒体文件、自动刮削 TMDB / 豆瓣元数据、生成海报墙与分集剧照，并用 mpv / FFmpeg 播放栈完成本地与远程播放。

本仓库是 OmniPlay 的多端合集，包含 4 个客户端工程。功能以 `OmniPlay开源-mac-ARM` 为基准，其他端持续向它对齐，但交互按平台习惯各自重做，不照搬同一套布局。

早期从套件版拆出的 `OmniPlay-docker` 服务端已停止开发，本文件不再收录；目录保留备查，部署说明见该目录内的 README。

## 软件截图

1. mac版

![](https://img3.pixhost.to/images/6317/781746795_5.jpg)
![](https://img3.pixhost.to/images/6317/781746868_6.jpg)
![](https://img3.pixhost.to/images/6317/781746928_7.jpg)
![](https://img3.pixhost.to/images/6317/781746979_8.jpg)

2. win版

![](https://img3.pixhost.to/images/6317/781748132_1-2.jpg)

![](https://img3.pixhost.to/images/6317/781748188_2-2.jpg)

![](https://img3.pixhost.to/images/6317/781748225_3.jpg)

![](https://img3.pixhost.to/images/6317/781748277_4.jpg)
## 版本一览

| 目录 | 平台 | 技术栈 | 版本 | 说明 |
| --- | --- | --- | --- | --- |
| `OmniPlay开源-mac-ARM` | macOS 14+（Apple Silicon） | Swift / SwiftUI + MPVKit-GPL / libmpv / FFmpeg | 1.9 | 功能基准版，工程 `OmniPlay.xcodeproj`，App 名「觅影」，Bundle ID `nan.OmniPlay` |
| `OmniPlay开源-mac-x64` | macOS 14+（Intel） | 同上 | 1.9 | 同一套工程的 x86_64 版本，已同步 ARM 版 1.9 |
| `OmniPlay-win` | Windows 10 / 11 x64 | Avalonia + .NET 10 + SQLite + libmpv | — | 独立重写，App 名「览影」，方案入口 `OmniPlay.Windows.slnx` |
| `OmniPlay-安卓TV` | Android TV / 手机（minSdk 26） | Java + libmpv（NDK / JNI） | 1.17 | 双入口启动器：TV 遥控器 D-pad 导航 + 手机触摸海报网格 |

各端详细说明分别在自己的 README 里，本文件只讲共性和入口：

- [macOS ARM 版 README](OmniPlay开源-mac-ARM/README.md)：完整功能清单、使用教程、Xcode 编译归档与打包、常见问题。
- [macOS x64 版同步记录](OmniPlay开源-mac-x64/2026-10-07-sync-with-arm-1.8.md)：与 ARM 版的差异收敛过程。
- [macOS x64 版 1.9 同步记录](OmniPlay开源-mac-x64/2026-10-24-sync-with-arm-1.9.md)：本轮从 ARM 版移植的功能与保留的 Intel 专属适配。
- [Windows 版 README](OmniPlay-win/README.md)：构建、打包、日志与播放诊断、TMDB 配置、离线环境说明。
- [安卓 TV 版 README](OmniPlay-安卓TV/README.md)：遥控器焦点导航、自研 WebDAV 传输层、主题体系、逐版本演进记录。

## 共同能力

各端共享同一套产品概念与数据模型，具体覆盖范围见下面的支持矩阵。

### 媒体库

- 首页海报墙展示全部已入库影视，支持搜索、按名称/评分/年份排序、升降序切换。
- 电影与剧集自动识别，支持多季、特别篇、分集排序与连播。
- 继续播放区、播放进度记录，以及已播 / 未看 / 未看完三态标记（三态图标以 mac 版为准，Windows 与安卓 TV 已对齐）。
- 导演、主演分类文件夹：按人物聚合成文件夹卡片，点进详情页看该人物的作品简介；封面可用该分类下的刮削海报、人物照片或本地图片，导演/主演名称支持手动改名。

### 媒体源

- 本地文件夹、外接硬盘、已挂载的 NAS 目录。
- WebDAV：局域网预扫描 + 手动填地址，浏览共享目录后点星标挂载，账号密码保存到系统钥匙串（macOS）或本机偏好。
- 家庭媒体服务器：Plex、Emby、Jellyfin，支持登录或访问令牌，选择要挂载的媒体库；已停止开发的 OmniPlay Docker 服务端仍可接入，并同步媒体库与播放进度。
- Lucky STUN 穿透媒体源：每个源独立保存 Lucky 管理端地址与穿透规则，可自动刷新穿透地址。
- SMB 网络共享（Windows / 安卓 TV）：直连播放，依赖设备本地 Range 代理。
- 媒体源支持启用/停用、重命名、移除并清理索引。

### 扫描与刮削

- 启动自动扫描 + 手动同步，扫描后自动把未匹配文件排进刮削队列。
- TMDB 提供标题、简介、评分、海报、分集剧照；支持公共源，也支持自己的 API Key / v4 Token。
- 豆瓣补充评分与中文条目；导演、主演名与作品名自动补全中文译名并持久化缓存，确认无译名的结果同样缓存。
- 无法直连 TMDB 时可填写反向代理网关（见下文 TMDB 一节）。
- 界面与刮削语言可在简体中文 / English 之间切换，TMDB 接口与图片语言一并跟随。

### 播放

- mpv / FFmpeg 播放栈，本地文件与远程文件统一走 libmpv。
- 蓝光 ISO / BDMV 通过 `bd://`、`dvd://` 代理流播放。
- 详情页覆盖层播放与独立播放器窗口（Windows），TV 端为全屏播放器 + 遥控器快捷键。
- 续播精确定位到上次保存的进度；默认音轨、默认字幕、画质模式（流畅优先 / 平衡 / 画质优先）均可配置。
- 播放页标题使用刮削后的影视名与季/集信息，不拼接原始文件名。

### 直接打开视频文件（mac 版）

- 访达「打开方式」选中觅影、设为默认后直接双击、文件菜单「打开视频文件…」（⌘O）、或把文件拖到海报墙窗口上，都能不经媒体库直接起播。
- 覆盖 mp4、mkv、mov、avi、rmvb、flv、webm、m2ts、ts、m4v、wmv 与蓝光 ISO；设置里可一键把觅影设为这些类型的默认打开应用。
- 详见 [macOS 直开文档](OmniPlay开源-mac-ARM/docs/2026-10-20-direct-file-playback.md)。

### 液态玻璃主题（mac 版）

- 主题分成「风格 × 配色」两维：5 套配色，风格有经典与液态玻璃两种。液态玻璃在所选配色的浅色底上画四色光斑场，再叠系统玻璃：右上角按钮簇玻璃胶囊、页眉玻璃胶囊、海报玻璃垫、筛选胶囊、详情页文件行与 NeoDB 按钮都有真折射与高光，标题栏与系统控件一并转浅色。
- macOS 26 起走系统 `glassEffect`，macOS 14 / 15 退回毛玻璃材质 + 染色；改动集中在主题的语义化取值上，经典风格与 5 套配色的观感不变。
- 实现与验证见 [macOS 液态玻璃主题文档](OmniPlay开源-mac-ARM/docs/2026-10-21-liquid-glass-theme.md)，Windows 与安卓 TV 已有各自深色主题，未跟进此样式。

### NeoDB 同步（mac 版）

- 详情页影视名称右边的 NeoDB 按钮弹窗里给影片打想看 / 在看 / 已看 / 搁置标记、五星评分和评论，设置页一键推送到 NeoDB，也能从 NeoDB 导回本机。
- 登录方式三种：账户密码、实例 `/developer/` 创建的访问令牌、浏览器授权码；令牌存系统钥匙串。
- 「已看」可由播放进度自动补齐，手工标记优先；走官方 https://neodb.social，不需要填实例地址。
- 方案与接口对照见 [macOS NeoDB 同步文档](OmniPlay开源-mac-ARM/docs/2026-10-19-neodb-sync.md)，Windows 与安卓 TV 待移植。

### 离线缓存

- mac 版与 Windows 版支持在首页或详情页按整部、整季、单集缓存到本地目录。
- 多集任务按剧集顺序串行执行，写入同目录 `.part` 文件后改名，避免进度误判。
- 任务前预检文件大小，并在所需空间外额外保留 1 GiB 可用空间；点击进度环可取消并二次确认。

## 能力支持矩阵

| 能力 | mac ARM / x64 | Windows | 安卓 TV |
| --- | --- | --- | --- |
| 本地文件夹媒体源 | 是 | 是 | 是 |
| WebDAV | 是 | 是 | 是 |
| SMB | 仅局域网发现 | 是 | 是 |
| Plex / Emby / Jellyfin | 是 | 是 | 否 |
| OmniPlay Docker 接入与进度同步 | 是 | 否 | 是 |
| Lucky STUN 穿透 | 是 | 是 | 否 |
| TMDB 刮削与中文译名 | 是 | 是 | 是 |
| 豆瓣补充信息 | 是 | 是 | 是 |
| 分集剧照与自定义副标题 | 是 | 是 | 是 |
| 导演 / 主演分类文件夹 | 是 | 是 | 否 |
| 蓝光 ISO / BDMV | 是 | 是 | 是 |
| 离线缓存 | 是 | 是 | 否（1.15 起从 TV 界面撤下） |
| NeoDB 标记 / 评分 / 评论同步 | 是 | 否 | 否 |
| 直接打开视频文件 / 设为默认打开方式 | 是 | 否 | 否 |
| 主题 | 5 套浅色配色 × 经典 / 液态玻璃两种风格 | 5 套（含深色） | 5 浅 + 3 深 |
| 播放器 | MPVKit-GPL / libmpv | libmpv（x64） | libmpv（NDK） |

## 快速上手

### macOS（ARM / Intel）

```bash
cd OmniPlay开源-mac-ARM
xcodebuild \
  -project OmniPlay.xcodeproj \
  -scheme OmniPlay \
  -configuration Release \
  -sdk macosx \
  -derivedDataPath /tmp/omniplay-release-build \
  build
```

产物在 `/tmp/omniplay-release-build/Build/Products/Release/觅影.app`。Archive、导出、DMG 与公证步骤见 [macOS ARM 版 README](OmniPlay开源-mac-ARM/README.md)。首次编译需要能访问 GitHub 以解析 MPVKit 等 Swift Package。

刚换目录打开工程就报 `Missing package product 'GRDB'` / `'MPVKit-GPL'`，多半是包没解析成功（`MPVKit-GPL` 要从 GitHub Releases 拉 1.6GB 二进制），排查步骤见 [Missing package product 排查](OmniPlay开源-mac-ARM/2026-10-11-missing-package-product-grdb-mpvkit.md)。

### Windows

```powershell
cd OmniPlay-win
dotnet restore .\OmniPlay.Windows.slnx
dotnet build .\OmniPlay.Windows.slnx -c Release -m:1
dotnet test .\OmniPlay.Windows.slnx -c Release -m:1
```

`.slnx` 方案在多节点调度下需要显式 `-m:1`，仓库内 `dev-build.ps1` / `dev-run.ps1` 已封装好。在 Apple Silicon Mac 上可以不装 Windows 直接交叉打包：

```bash
cd OmniPlay-win
./package-setup.sh
```

输出 `dist/览影-OmniPlay-x64-setup.exe` 与 `dist/OmniPlay-win-x64-portable.zip`。内置的 `libmpv-2.dll` 是 x64，因此暂不产出 `win-arm64` 包。

### Android TV / 手机

```bash
cd OmniPlay-安卓TV
./gradlew assembleDebug
```

TV 与手机共用一个 APK，双启动入口由 `LauncherActivity` 分发。NDK 与模拟器数据留在本机（见 `.gitignore`），首次构建需要本机 Android SDK。

## TMDB 与反向代理网关

第一步：
1. 登录 [Cloudflare 控制台](https://dash.cloudflare.com/)，进入 Compute (Workers & Pages) -> Workers。
2. 点击 Create application -> Create Worker，命名为 `tmdb-proxy` 并点击 Deploy。

第二步：配置反代代码

1. 点击 Edit code，将默认代码全部清空，替换为以下脚本：
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
点击右上角 **Deploy** 保存。

第三步：绑定自定义域名（关键）

- 由于 `.workers.dev` 默认子域名在国内通常无法直连，进入该 Worker 的 Settings -> Domains & Routes。
- 点击 Add -> Custom Domain，绑定你托管在 Cloudflare 上的二级域名（例如 `tmdb.yourdomain.com`）。

刮削软件中的填法：
在 MoviePilot / TMM / NASTool 中，将 TMDB API 地址填为：
https://tmdb.yourdomain.com/mySecret888

## 数据与日志位置

| 端 | 数据库 | 缓存 / 日志 |
| --- | --- | --- |
| macOS | `~/Library/Application Support/OmniPlay/omniplay.sqlite` | 海报、剧照由应用内部管理；离线缓存在设置中指定的目录 |
| Windows | `%LOCALAPPDATA%\OmniPlay\data\omniplay.sqlite` | 海报 `posters\`、缩略图 `thumbnails\`、日志 `logs\app.log`；根目录可用 `OMNIPLAY_APP_ROOT` 覆盖 |
| 安卓 TV | 应用私有目录内的 SQLite | 海报与剧照走本机缓存目录 |

## 开发约定

- 功能与交互以 `OmniPlay开源-mac-ARM` 为准，其他端向它对齐；移植时在对应版本目录留一份 `YYYY-MM-DD-<主题>.md` 记录改动与验证结果。
- 每个版本都是独立工程，依赖、构建脚本和测试互不共享：macOS 用 Xcode / `xcodebuild`，Windows 用 `.slnx` + `dotnet`，安卓 TV 用 Gradle。
- 构建产物、IDE 状态、Android SDK 与模拟器镜像、NuGet 缓存、TMDB 凭据和本地媒体样本一律不入库，规则见 [.gitignore](.gitignore)。安装包等发布物请挂到 GitHub Releases。

## 许可证与三方组件

本项目包含或依赖多个开源组件与媒体框架，分发前请确认 GRDB 的 MIT 要求、mpv / FFmpeg / MPVKit 的许可证与 LGPL/GPL 组件的再分发义务。macOS 版随附 `OmniPlay/OpenSourceLicenses/` 许可证文本并在应用内提供「开源组件详情」页面，Windows 版见 [THIRD_PARTY_NOTICES.md](OmniPlay-win/THIRD_PARTY_NOTICES.md)。如果你重新打包、修改播放器栈或替换二进制框架，请重新审查对应的许可证与源码提供义务。

## 免责声明

觅影 OmniPlay 仅用于管理和播放用户有权访问的本地或 NAS 媒体文件。TMDB 与豆瓣的元数据、海报、剧照版权归各自权利方所有，请遵守所在地区法律法规以及相关服务条款。
