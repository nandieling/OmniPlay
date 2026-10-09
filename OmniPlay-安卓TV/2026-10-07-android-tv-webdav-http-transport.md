# 2026-10-07（第二批）WebDAV 传输层换成本地 Socket、SMB 崩溃可见

设备实测 v1.9（versionCode 20）反馈两问题：

1. `点击链接SMB后软件直接闪退。`
2. `点击登录webdav，显示： REM: Expected one of [OPTIONS, GET, HEAD, POST, PUT, DELETE, TRACE,`

结论：问题 2 有确定根因并已修复；问题 1 在没有 adb 的情况下无法拿到栈，改成「不再静默退出 + 把原因显示出来」，下次一点就能看到具体异常。

## WebDAV：`Expected one of [OPTIONS, GET, HEAD, POST, PUT, DELETE, TRACE, PATCH]`

这条消息来自 Android `java.net.HttpURLConnection.setRequestMethod`（libcore），它只允许那八个方法名。v1.9 的 `WebDavClient.list()` 用 `connection.setRequestMethod("PROPFIND")`，于是在真机上第一次 PROPFIND 就抛 `ProtocolException`，WebDAV 从来没通过（`LanDiscovery.exchange()` 同理，只是它 catch 掉异常，所以表现为「扫不到」而不是报错）。

修法不是换库，而是自己发 HTTP：新增 `local/Http.java`。

- 一条连接一个请求（`Connection: close`），不跟随重定向、不走系统代理。
- 方法名不做白名单，任意 token（`PROPFIND`）直接写进请求行。
- 请求行按 UTF-8 百分号编码，中文目录名可用；已编码字符不重复编码。
- 响应支持 `Content-Length`、分块传输与读到 EOF 三种长度语义，并跳过 1xx 中间响应。
- 请求体写出失败（服务器提前回 401 并关闭）不丢状态码：继续读响应，只有响应也读不出来才报错。
- TLS 用信任所有证书的 `SSLSocketFactory`（NAS 自签证书），IP 主机跳过 SNI，域名设置 `SNIHostName`。

`WebDavClient.list()` 与 `LanDiscovery.exchange()` 都改走它；播放地址仍由 mpv 自己抓取，`OfflineCacheManager` 只用 GET/HEAD，保持系统实现不动。

顺带修掉一个真实缺陷：`WebDavClient.displayNameOf()` 对以 `/` 结尾的容器 href 取最后一段得到空串，而目录浏览器会跳过空名字的行——也就是说没有 `displayname` 属性的普通文件夹在选择目录时根本不出现。现在先去掉结尾斜杠再取名，并对根路径回退到可读形式。

## SMB 闪退：改成崩溃可见

`MainActivity.runAsync` 原来只 `catch (Exception)`。jcifs-ng 一旦抛 `Error`（`NoClassDefFoundError`、`LinkageError`、`VirtualMachineError` 等），异常会从 io 线程池逃逸并终止进程：屏幕上刚点下去就回桌面，也就是「闪退」。设备没接 adb，拿不到栈，所以这一轮做的是让它必然留下可读信息：

- `runAsync` 捕获 `Throwable`，`Failure` 接口参数放宽到 `Throwable`；回调本身也在 `try/catch` 里，界面上的异常变成 Toast 而不是退出。
- 新增 `local/CrashLog.java`：安装全局 `UncaughtExceptionHandler`（链式调用原 handler，不改变系统重启行为），把线程名、异常类、消息与栈前几帧写进本机偏好和 `files/omniplay-tv-crash.txt`；登录页 `showSetup` 用 `CrashLog.take()` 读一次并清除，显示成「上次异常退出：…」。
- `SmbClient` 的每个 jcifs 入口（`testConnection` / `listShares` / `list` / `walk` / `randomAccessFile` / `open` / `context()`）把任何 `Throwable` 翻译成带原因的 `IOException`，因此即使真是类加载问题，显示的也是「SMB 组件无法启动：NoClassDefFoundError：…」，App 不退出。
- 登录页启动时用 `SmbClient.warmUp()` 在后台提前加载 `jcifs.smb.SmbFile` / `SmbRandomAccessFile` 并建一次 `BaseContext`：能起来就当预热，起不来立刻在状态行显示「SMB 组件不可用：…」，不用等用户去点 SMB。
- `LanDiscovery` 扫描线程整体包一层 `catch (Throwable)`：出问题时仍然回调 `onDone`，登录页不会一直停在「正在扫描」。
- `friendlyLoginError` 对 `LinkageError` / `VirtualMachineError` 直接用 `CrashLog.summarize()`，避免把类名当成人话显示。

打包核对过：`classes.dex` 里有 475 个 `jcifs/*`、34 个 `org/slf4j/*`、4140 个 `org/bouncycastle/*` 类，所以不是「依赖没打进 APK」。桌面侧也确认 `BaseContext` 需要 BC 的 ASN.1 类、`SmbFile` 需要 slf4j（缺绑定只是 NOP 日志），这两样都在包里，因此崩溃更可能出在设备特有的类解析或凭据/共享枚举路径上——这需要设备回一条消息才能定死。

## 局域网判定规则同步收紧

PROPFIND 现在在桌面与设备上都真的能发出去，原来「PROPFIND 401/403 就算 WebDAV」会让带登录的普通 Web 管理页（80/443/8080）被误报。规则改为：`DAV` 头或真 207 一定算；401/403 只在专用端口（非 80/443/8080）算，OPTIONS 与 PROPFIND 任一命中即可。共享端口仍可手填地址连接。

## 验证

- `/tmp/lanprobe/run.sh`：30 条断言 ALL_OK，新增 PROPFIND 207（OPTIONS 不带 DAV 头）、专用端口 PROPFIND 401、共享端口 PROPFIND 401 不算 WebDAV、分块响应解码、`Depth` 头与中文路径透传。
- `/tmp/pitest/run-webdav.sh`（新增）：真 PROPFIND 往返，断言方法名、请求路径、`Depth`、`Authorization: Basic`、请求体、multistatus 解析（含无 `displayname` 的中文容器名、相对/绝对 href 归一化）、401 状态码透传，12 条全过。为脱离 Android 运行，脚本用 `/tmp/pitest/fake` 里的 `android.util.Base64` 等桩类与 `kxml2` 提供 `org.xmlpull`。
- `/tmp/pitest/run-saved.sh`（32 条）、`/tmp/pitest/run-phone.sh`（手机输入服务，原有断言全过）：ALL_OK（`SmbClient` 现在引用 `CrashLog`，两份脚本补了源文件与 `getFilesDir()` 桩）。
- `/tmp/pitest/run-smb.sh`（新增，4 条）：SMB_OK。带上 `slf4j-api` 与 `bcprov-jdk18on` 后确认 `SmbClient.warmUp()` 返回 `jcifs-ng`（即 jcifs 的类初始化在依赖齐全时没问题，桌面端 `BaseContext` 确实需要 BC 的 ASN.1 类），空主机与不可达主机都只得到一条 `IOException` 描述而不是 `Error`。
- `javac` 全量语法检查通过；`assembleRelease --offline` 通过，`aapt2 dump badging` 报 `versionCode='21' versionName='1.10'`，`dexdump` 确认 `com/omniplay/tv/local/Http`、`Http$Response`、`Http$Target`、`Http$BufferedStreams`、`CrashLog` 已在 `classes.dex`。
- 产物：`dist/OmniPlay-TV-v1.10-arm64.apk`（77.57 MB，arm64 / armeabi-v7a / x86_64）。

## 已知限制

- SMB 崩溃的确切原因仍要等设备上的那行提示；本轮保证「一定能看到原因」，没有盲猜修好某一种具体 linkage 错误。
- `OfflineCacheManager` 的下载走系统 `HttpURLConnection`：方法都是标准的，但自签 HTTPS 证书在那里仍会失败（缓存 WebDAV 时如果证书不可信会失败，播放本身不受影响）。
- `Http` 不做连接复用、不处理代理与压缩；这些对局域网 WebDAV 没必要，PROPFIND 响应体也都在内存里。
