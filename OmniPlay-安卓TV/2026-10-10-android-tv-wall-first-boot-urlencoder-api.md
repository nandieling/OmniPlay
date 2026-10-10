# 1.21：海报墙才是首屏、扫描按钮要有回音、播放地址别再调 API 33 的方法

## 首屏为什么会被换成全屏媒体源页

`onCreate` 结尾原来是：

```java
if (api.hasServerUrl()) { showHome(); } else { showSetup(null); }
```

这句在服务端模式下说得通（没填服务端地址，当然得先去登录页），但独立版里 `LocalPlayApi.hasServerUrl()` 的实现就是 `!engine.sources().isEmpty()`——它回答的是「媒体库空不空」，不是「服务端配没配好」。于是清库之后、或者第一次装好还没挂源时，冷启动落在整屏设置页上，用户看到的就是一整屏「媒体源」。

现在 `onCreate` 无条件 `showHome()`，崩溃摘要照旧一条 Toast。空库由 `refreshHomeBody()` 处理：没有源就放 `emptySourceCallToAction()`（一句说明 + 居中 220×56 的「添加媒体源」，点了直接开媒体源抽屉），有源但没结果仍是「媒体库为空 / 没有匹配结果」。`showSetup()` 留在原地，只被 `refreshHomeLibrary()` 的错误回调用到，属于错误页而不是首屏。

`disconnectFromServer()` 同样以 `showHome()` 收尾。

## 挂完源别再拿全屏 loading 挡着

`mountMediaSource()` 在抽屉外挂载时走的是：

```java
showLoading("正在扫描媒体源");
api.engine().scanAll(null);   // 同步，跑完才回调
...
```

`scanAll()` 是同步的整库遍历，几百个文件的 WebDAV 要 PROPFIND 好几页，那一屏能停几分钟，而且 `showLoading` 是盖满窗口的，期间既看不到海报也点不动任何东西。抽屉内挂载那条分支反而是对的（`renderSourcesOverlay()` + `startScanAndScrape(statusLine)`），两条路径行为不一致。

统一成抽屉那条：保存源 → 记录已保存连接 → `showHome()` → `startScanAndScrape(null)`。立刻回墙，扫描刮削在后台线程跑，进度走 `throttledProgress()`，`refreshHomeLibrary(false)` 与整墙重建防抖让海报一张张往上加；出错只 `showToast("扫描失败：…")`。顺带 `local` 源不存在时也不再甩全屏错误页，改 Toast 报是哪个目录读不了。

抽屉里加了第三颗「添加本地目录」（`openLocalFolderPicker()`）——全屏设置页退场后，那是本地目录唯一的入口；高度写死 `Widgets.HEIGHT_BUTTON`，与上面两颗「添加 WebDAV / SMB 媒体源」对齐，不让它被内容压成薄薄一条。

顺带把 `rememberExistingSources()`（把升级前挂好的源补录进 `SavedConnections`，登录框靠它回填账号密码）从 `renderSavedConnections()` 挪了一份到 `renderSourcesOverlay()` 开头——那句话原先只有全屏设置页渲染时才会跑，设置页退场后老用户的已存账号就再也补不进去了。

## 「点扫描刮削没反应」

两处都在 `startScanAndScrape()`：

1. `scanRunning` 为真时直接 `return`，用户按下去什么也没发生。启动自动扫描那轮可能还在跑（几百个文件 + 刮削几分钟），按钮看起来就是坏的。
2. 横幅有个「这一句已经收起过就不再显示」的机制：`homeStatusHiddenMessage` 记下上一条自动收起的文案，`refreshHomeStatusText()` 遇到同样的文字就保持 `GONE`。手动再点一次时开场文案仍是 `正在扫描媒体源...`，与上一轮收尾时那条撞车，横幅于是继续藏着。

改法：进入时先 Toast「扫描刮削正在进行中，海报会陆续出现在首页。」并清掉 `homeStatusHiddenMessage` 后重刷横幅；真的开跑时清 latch、用新的 `showHomeStatusLine("正在扫描媒体源...")` 直接写横幅并置 `VISIBLE`、`setScanButtonRunning(true)` 把工具条那颗字改成「扫描中」，成功/失败两个回调各自复位。工具条按钮初次创建也改用 `scanButtonLabel()`，状态与文案同源。

`startStartupSync()` 里原来自己 `runAsync(scanAll…)` 一遍，现在只调 `startScanAndScrape(null)`：同一时间只可能有一轮扫描，横幅与按钮状态也不会分叉。

## `No static method encode(String, Charset)`

```
java.lang.NoSuchMethodError: No static method encode(Ljava/lang/String;Ljava/nio/charset/Charset;)Ljava/lang/String;
    in class Ljava/net/URLEncoder; ... core-oj.jar
```

`URLEncoder.encode(String, Charset)` 是 Android 13（API 33）加的重载。编译期用的是 android-35 的 `android.jar`，方法就在里面，javac 不报错；`minSdk 26` 的设备运行时 `core-oj.jar` 里没这个方法，一点海报（要算 WebDAV 播放地址）就抛。这是二进制兼容问题，ProGuard 与 multidex 都掩盖不了。

`WebDavClient.streamUrl()` 是全库唯一一处，改成本地 helper：

```java
private static String urlEncode(String value) {
    try {
        return URLEncoder.encode(value, "UTF-8");
    } catch (java.io.UnsupportedEncodingException impossible) {
        return value;
    }
}
```

顺手把 userinfo 也过一遍编码：账号密码里带空格、`@`、`/` 时，原来会把 URL 结构写坏；旧的编码用 `+` 表示空格，但 `+` 只在 query 里有这个含义，路径与 userinfo 里必须写 `%20`，所以 `streamUrl()` 统一 `.replace("+", "%20")`。

同类坑用同一把尺子扫过：`readAllBytes()`、`transferTo()`、`Path.of()`、`Files.readString/writeString`、`PrintStream(Charset)`、`List.of()`、`String.isBlank()`、`URLDecoder.decode(String, Charset)`，`local/` 与 `ui/` 里都没有第二处；`InputStreamReader(in, StandardCharsets.UTF_8)` 是 API 1 就有的，安全。

## 验证

`URLEncoder` 那条走 JVM 探针，不必装机：`/tmp/pitest/StreamProbe.java` 只编译 `local/` + `data/`（顺带卡住分层规则），断言中文目录、带空格账号、含 `@` 与 `/` 的密码，输出

```
http://nas%20user:p%40ss%2F1%2B2@192.168.0.150:5005/%E7%94%B5%E5%BD%B1/%E7%89%87%E5%90%8D.mkv
```

即 `STREAM_OK`。

模拟器（`android-tv;android-34;arm64-v8a`，`-gpu host`，density 213）：

- 清数据冷启动、零媒体源 → 首屏是海报墙，左上角「0 部」，中间「还没有媒体源…」加一颗「添加媒体源」，没有整屏设置页。
- 点那颗按钮 → 媒体源抽屉打开，三颗添加按钮齐全（WebDAV / SMB / 本地目录）。
- 选 `/storage/emulated/0` 挂载 → 抽屉不关，背后的墙已经出现 46 部，工具条那颗变成「扫描中」，横幅写「刮削元数据和海报 (3/46)：Film」。
- 扫描进行中再点「扫描中」→ Toast「扫描刮削正在进行中，海报会陆续出现在首页。」，不卡不闪退。
- 点海报 → 详情页正常打开（这条在 v1.20 的包上必崩）。

真机上还需要确认的两件事：WebDAV 源的播放（模拟器里没有 WebDAV 服务器，`streamUrl()` 只由探针证明），以及 1.20 遗留的「扫描进行中移除媒体源」。

版本 1.21（versionCode 32）。
