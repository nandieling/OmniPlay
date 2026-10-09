# 2026-10-07 模拟器接入与第一轮真机化验证（v1.12 / versionCode 23）

## 为什么做这件事

1.11 的重构是在没有设备的前提下做的：所有验证都停在 javac、`assembleRelease`、`aapt2`、`dexdump` 和五个 JVM 用例上，遥控器操作到底顺不顺、扫描和播放能不能真跑，全是推断。这次补上设备：Apple Silicon 装官方 Android TV 镜像（arm64-v8a 原生），跑 `dist` 里的 release 包，用 `adb shell input keyevent` 当遥控器，`uiautomator dump` 当眼睛。

设备参数：Android 14 TV 镜像（`sdk_google_atv64_arm64`）、1920x1080、`wm density 213`（对齐真机 1080p，模拟器 `tv_1080p` 皮肤默认 320dpi 会让界面大一号）。媒体库是临时造的 `/sdcard/Video`：三部电影 + 一部两集的剧（ffmpeg `testsrc2` 生成，4 秒）。

## 装了什么

```sh
SDK="$PWD/.android-sdk"
sdkmanager --sdk_root="$SDK" "emulator" "platform-tools" \
  "system-images;android-34;android-tv;arm64-v8a"
avdmanager create avd -n omni_tv1080 -k "system-images;android-34;android-tv;arm64-v8a" -d tv_1080p
avdmanager create avd -n omni_tv4k   -k "system-images;android-34;android-tv;arm64-v8a" -d tv_4k
```

- AVD 落在项目内 `.avd`（`ANDROID_AVD_HOME`），镜像 8.1G 在 `.android-sdk/system-images`，都不污染家目录。
- 两个 AVD 分别对应 `uiScale` 的 1.0 与 1.18 分支（1920 / 3840 宽），4K 那台还没跑。
- 模拟器与 `adb` 都要在非沙箱环境跑：`adb` 绑不上回环 5037 端口，模拟器进程会随启动它的命令一起被收掉，得当常驻会话起。

## 第一轮跑出来的四个问题

### 1. 本地目录整库扫挂（只有真机能撞上）

界面上是 `媒体源已保存，但扫描失败：扫描失败：Syntax error in regexp pattern near index 48`。指向 `LocalMediaNameParser` 的清洗规则：

```java
"(?i)\\b(vol|volume)\\s*[-_ ]?\\d{1,2}(?!\\u2013?\\d{1,2})?\\b"
```

断言后面跟了 `?`。Android 的 `java.util.regex` 是 ICU 实现，不接受给断言加量词（下标 48 正好落在紧跟其后的 `\b` 上），OpenJDK 却能编译，所以 JVM 用例全绿、真机整库扫挂。那个 `−`（en dash）本来也该是普通连字符。改成：

```java
"(?i)\\b(vol|volume)\\s*[-_ .]?\\d{1,2}\\b"
```

`\\d{1,2}\\b` 已经能挡住 `vol 123`（回溯后仍不匹配），断言是多余的。顺手加了 `/tmp/pitest/run-regex.sh`（输出 `REGEX_OK`），静态查「量词紧跟断言」这一类只有 Android 会炸的写法，六个用例现在都在跑。

### 2. `待刮削 N 个条目` 每扫一次翻一倍

`LocalMediaLibrary.enqueueScrape` 是裸 INSERT，而 `upsertUnscrapedShow/Movie` 每次扫描回的是同一个 entityId，于是同一实体反复入队：4 个条目扫两次变 10（含首启动那次的残留）。改成先按 `(mediaType, entityId)` 删再插，一个实体一条待办，TMDB 也不再重复查。已存在的重复行会被下次扫描自然清掉，不需要迁移。真机复测：4 → 4。

### 3. 弹层打开时看不见焦点框

`设置`、`媒体源` 刚打开时焦点落在 `ScrollView` 上，容器不画焦点圈，用户看到的是「打开就没焦点」。三处叠加：

- `collectFocusables` 把 `isFocusable()` 的 ViewGroup 一律收进网格，滚动容器也进去了（海报卡是 LinearLayout，不能简单排除 ViewGroup，所以按类型排除滚动容器）；
- `Overlays.Overlay.focus()` 用 `scroll.fullScroll(FOCUS_UP)` 回到顶部，而 `fullScroll` 在上方无可聚焦控件时会顺手 `requestFocus()` 把焦点抢走；
- 新建的控件在首次布局前会拒掉 `requestFocus()`，系统兜底就把焦点给了「仍然可聚焦」的容器。

三处一起补：布局用的滚动容器 `setFocusable(false)`（项目里本来对两处是这么写的，这次补全七处）；`focus()` 改 `scrollTo(0, 0)`；新增 `FocusGrid.grab(view)`，请求被拒就在布局后补一次，首页首次上焦和弹层初始上焦都走它。

### 4. 本机影片放不了

`播放失败：loading failed (-13)`，logcat 里是 `Cannot open file 'The.Last.of.Us.2023/Season 01/The Last of Us S01E01 1080p.mp4': No such file or directory`。`LocalEngine.playbackUrlFor` 只处理 WebDAV / SMB 的绝对化，本机源落到兜底 `return "file://" + raw`，把相对路径直接丢给了 mpv，mpv 就去 `/` 下面找。现在先按本机源根目录拼，并用 `isFile()` 兜底，多本地源时也能挑对那一个。修完 `mpv event file-loaded`，视频轨 `h264 640x360`、音频轨 `aac` 都识别出来，硬解走 `h264_mediacodec`。

## 复测矩阵（模拟器，遥控器语义）

| 检查 | 结果 |
| --- | --- |
| 冷启动进媒体库，焦点落在真实控件（`搜索`） | PASS |
| `设置` 侧栏打开停在「添加媒体源」，BACK 回到 `设置` | PASS |
| `媒体源` 面板六个操作全可达（停用 / 重命名 / 移除 / 添加媒体源 / 重新扫描并刮削 / 清空媒体库索引） | PASS |
| 重新扫描不重复入队（待办 4 → 4） | PASS |
| BACK 回到打开它的那个按钮（`用手机输入 TMDB 配置`、`媒体源` 两处都验过） | PASS |
| 详情页：季 / 集列表、未播状态、缓存本季、字幕入口 | PASS |
| 播放器：`file-loaded`，无 FATAL | PASS |
| 五个 JVM 用例 + 新正则守卫 | `ALL_OK` / `WEBDAV_OK` / `SMB_OK` / `ALL_OK` / `ALL_OK` / `REGEX_OK` |

## 还没验证 / 已知限制

- 4K AVD（`omni_tv4k`，`uiScale` 1.18 分支）没跑；4K / HDR / DV 硬解只能真机验。
- 模拟器在 NAT 后（`10.0.2.15`，宿主 `10.0.2.2`），局域网扫描看不到真实 NAS，WebDAV / SMB 的挂载与文件夹选择器、以及手机扫码输密码，都得回真机或宿主起假服务再验。
- TMDB 没配 API Key，条目停在待刮削，海报是占位图；刮削链路与海报回显没在设备上验过。
- 播放器里 `h264_mediacodec: Both surface and native_window are NULL` 是模拟器 GPU 路径的抱怨，真机需要再看一眼。
- `--offline` 构建现在会挂：`com.google.zxing:core` 不在本机 Gradle 缓存里，得联网跑一次。

## 产物

`dist/OmniPlay-TV-v1.12-arm64.apk`，versionCode 23 / versionName 1.12，`aapt2 dump badging` 核对过。
