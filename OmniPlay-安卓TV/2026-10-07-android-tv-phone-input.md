# 2026-10-07 手机辅助输入（安卓 TV 版）

## 问题

TV 端所有文本输入（媒体源地址、WebDAV/SMB 账号密码、TMDB API Key / v4 Token、代理网关、缓存目录、重命名、分集副标题）都是普通 `EditText` 直接拉系统输入法。国内安卓盒子普遍没有像样的软键盘，用遥控器逐字符点几十位密码或 200 字符的 v4 Token 基本不可用；Android 官方遥控器 App 的手机键盘依赖 GMS 与配套 App，覆盖率不可控，不能作为主路径。

## 方案

TV 端起一个只监听局域网的迷你 HTTP 服务，把输入表单交给手机浏览器：手机与电视在同一网段，扫码或照着地址输一次，之后所有长文本都在手机上打字，提交后直接写回电视上对应的输入框。

选它而不是"TV 内置 D-pad 屏幕键盘"的原因：屏幕键盘解决不了长串文本与密码的可读性（还得自己处理中英切换、光标移动、粘贴），而网页表单天生有系统输入法、密码管理器和剪贴板；实现上仓库已经有两个进程内 HTTP 服务（`SmbStreamServer`、`RemoteIsoStreamServer`），复用同一套写法即可，不引入 Web 框架。

## 实现

- `local/PhoneInputServer.java`（新增）：进程内 `ServerSocket(0)`（随机端口）+ 守护线程 + 缓存线程池，请求解析、`application/x-www-form-urlencoded` 解码（`URLDecoder`，UTF-8）、页面渲染全部手写，不依赖 Web 框架。页面样式与 TV 端配色一致（浅灰底、青绿主色），移动端输入框 16px 字号避免 iOS 聚焦时自动放大。
- `local/QrCode.java`（新增）：zxing `QRCodeWriter` + `BitMatrix` 直接逐像素填 `int[]` 生成 `Bitmap`，不经过 `MatrixToImageWriter`（那是 javase 模块）。
- `TvSettings.phoneInputToken()`：装机时生成 10 位 token 并持久化，端口每次启动重新分配，token 不变，收藏过的页面下次还能打开。
- 访问控制：请求方地址必须是 site-local 或回环；路径必须匹配 token；覆盖层关闭即 `clearSession()`，此时页面 GET 提示"电视端已经离开这个输入页"，POST 不会写入任何东西。
- `MainActivity`：`openPhoneInputOverlay(title, subtitle, fields, allowNote)` 统一入口，二维码下方同步显示可手输的纯文本地址；已打开时再次调用只换注册的字段，因此任何覆盖层都能提供同一个入口。字段用 `boundField(key, label, Supplier<EditText>, secret)` 按引用绑定，设置面板原地重渲染后仍能写进新的输入框；密码框用 `type=password`，留空的字段不改写，避免半填的表单把已填内容清掉。
- 焦点链：覆盖层只注册"关闭"一个焦点项；打开前记下 `dpadRows` 与当前焦点，关闭时整体还原，取不到原焦点就回落到还原后网格的第一个可聚焦控件，遥控器不会掉焦点。返回键层级上，手机输入面板位于登录弹窗 / 设置 / 媒体源之上（`onKeyDown`、`onBackPressed`、`dispatchKeyEvent` 三处同步）。
- 入口：登录弹窗、添加媒体源页、设置页（TMDB 配置、缓存目录）、通用文本弹窗（重命名媒体源、改片名、分集副标题）。
- 生命周期：首次打开覆盖层才 `start()`，`onDestroy` 里 `stop()`；线程全部 daemon，`stop()` 会 `shutdownNow()` 线程池，不拖住进程退出。

## 验证

- `local/PhoneInputServer` 在桌面 JVM 上跑了端到端测试（用最小 android 桩类 `Context`/`SharedPreferences`/`Handler`/`Looper`/`TextUtils`/`Environment` 替换 android.jar 的 stub）：真实启动服务、GET 表单、POST 提交，24 项断言全部通过，覆盖 token 校验、错误 token 拒绝、会话关闭后的提示、UTF-8 中文解码、`a+b c` 这类含加号与空格的密码不被误解、留空字段不改写、单字段场景下"补充内容"回落、空提交报 0 项、favicon 静默。测试进程能自行退出，说明线程都是守护线程。
- 二维码：同一套 hints（UTF-8、纠错 M、margin 2）在桌面生成 PNG，用 zxing reader 回环解码，168px 与 400px 都能解回原 URL。macOS 侧的 CIDetector / Vision 在这台机器上不可用（`mrcdetector.espresso.net` ANE 模型加载失败），因此实机扫码识别未验证。
- `assembleRelease`（JDK 17）BUILD SUCCESSFUL，zxing 已进 dex，APK 从 77.34 MB 增至 77.55 MB；zxing 已进本地 Gradle 缓存，后续 `--offline` 构建正常。
- 未做设备端验证：二维码在电视上的实际可扫性（缩放、抗锯齿）、手机浏览器键盘行为、以及 AP 隔离网络下的失败提示需真机复测。
