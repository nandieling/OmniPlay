# 1.25：扫描进度真的在走、中途移除媒体源当场清场、屏幕上不再甩异常栈

四条反馈：左上角写着「正在扫描媒体源」但长时间没新海报；移除媒体源后首页还剩一张未刮削卡片且横幅还挂着「正在扫描媒体源」；右上角媒体源/设置两颗按钮不好看；软件会突然在屏幕中间冒出一大段运行异常日志、几秒后消失。

## 一、扫描阶段根本没有进度可显示

1.24 把进度行的显示条件改对了（`扫描中 || 引擎在刮削 || 队列非空`），但**扫描阶段能显示的内容**没跟着改：`LocalEngine` 里刮削进度会写 `scrapeStatus`（首页读这个字段），而扫描进度只调 `listener.onScrapeProgress(...)`，字段里留的还是上一句。所以扫描三分钟，首页就三分钟显示「正在扫描媒体源...」，用户完全无法区分“在走”和“卡死”。

改法是收口：`LocalEngine` 新增

```java
private void progress(ProgressListener listener, String message) {
    scrapeStatus = message;
    if (listener != null) listener.onScrapeProgress(message);
}
```

所有发射点（`scanAll`、`scanAndScrape`、`scrapePending`、分集剧照补齐）一律走它，字段和回调不会再分家。句子按 mac 版 `PosterWallView.swift` 那一套抄：

```
扫描目录中... → 扫描中 (i/n)：源名 → 完成：源名（新增X 移除Y） → 更新首页... → 扫描完成：新增 X，移除 Y，共发现 Z 个文件。
```

TV 上文件数可能上千，`ScanCollector.paintProgress()` 给「已发现 N 个文件」加了 400ms 的重播节流——每登记一个文件就刷一次 UI 是没必要的开销，也看不清数字。`scanLocalSource/scanSmbSource/scanWebDavSource` 因此多收 `sourceIndex/sourceTotal` 两个参数，进度里能报「第几个源 / 共几个源」。

## 二、扫描中途移除媒体源

旧行为：收尾的 `removeMissingFiles(...)` + `purgeUnknownSources(liveSourceIds)` 只在一整轮结束时跑一次。用户中途移除一个源，手上那个目录树还在往下走，照样把这个源的文件登记回库；等收尾清理干净，界面上又冒出一张未刮削卡片，横幅也停在扫描中。

现在三处配合：

1. `ScanCollector.register()` 每次登记前先查 `liveSourceIds`，源没了就 `throw new ScanAborted(source.name)`；`finish()` 在 `aborted` 时把异常再抛一次，免得被当成“正常扫完”。
2. `SmbClient.walk()` 在 `catch (Throwable)` 之前先透传 `LocalEngine.ScanAborted`——否则会被翻译成一个 SMB 错误，本轮就成了“扫描失败”而不是“源被移除”。
3. `scanAll` 接住 `ScanAborted`，报「已移除：源名，本轮跳过」，继续下一个源；收尾的 `purgeUnknownSources` 本来就按还活着的源清理，卡片就此留不住。

首页侧同时补了 `libraryFetchSeq`：`refreshHomeLibrary()` 和 `refreshLibraryInPlace()` 各自抓一个序号，回来时号不对（有更新的一次读取在飞）就丢弃结果。旧版用 `libraryPatchInFlight` 布尔量，慢的那条读取会把刚清空的库重新画满。

顺带修掉一个 self-inflicted 回归：`showHome()` 同时发两条读取，补丁那条号大所以总赢，而它只在“已有卡片”时干活，**空库时什么都不做**，首页就永远停在中间那行「正在加载媒体库」。现在墙上有卡片才补丁，否则走整页读取，空库正常渲染成「还没有媒体源」空状态。

## 三、两颗按钮

`folder.badge.plus`：加号徽标原来在右下角，正好压在文件夹底边上，三米外看像个对勾。改成右上角实心圆 + `PorterDuff.CLEAR` 先挖洞再画加号，外加一圈 r=4.4 单位的清空区，文件夹轮廓在徽标处干净断开。

`gearshape`：齿圈线宽 ×1.5（r=5.6 单位），八颗齿改成从 5.2 单位伸到 8.6 单位的胶囊（线宽 ×2.1），中心孔收到 ×0.9。之前是“齿圈细、齿粗、孔大”，糊成一团；现在轮廓和齿的比例接近 mac 那枚。

## 四、屏幕上那段异常日志

用户看到的是 Toast：worker 线程回调里抛出的异常会 `showToast(CrashLog.summarize(error))`，而 `summarize()` 会带上抛出位置；更早就更难看——启动时把整份崩溃报告（十几行 `at ...`）原样弹出。收口成三条规则：

- worker 回调异常只 `CrashLog.record` + `Log.w`，不弹（首页还在，弹一句用户也来不及读，反而挡视线）。
- 上次崩溃只说「上次运行异常退出（已记入日志）」。
- `showToast()` 统一过 `briefMessage()`：压掉换行与连续空白、截到 96 字、`LENGTH_SHORT`。WebDAV/SMB 的异常 `getMessage()` 经常是整段 XML 或 HTTP 状态行，这条路封死。扫描失败改用 `CrashLog.summarize()`，输出是一行「类名：原因（方法:行号）」。

完整栈照旧在 `/data/data/com.omniplay.tv/files/omniplay-tv-crash.txt`，需要排查时仍然拿得到。

## 验证

模拟器（`omni_tv1080`）+ 假 TMDB（每次搜索拖 0.6 秒）。用 `adb shell` 造了两批文件：`Movies` 下 12 个、`Bulk` 下 240 个（每个一个子目录，`Movie N (20XX).1080p.mkv`），媒体源挂 `/storage/emulated/0`。

1. 空库启动：直接进海报墙 + 「还没有媒体源」空状态，不再卡在「正在加载媒体库」（v1.24 装机版会一直停在那儿）。
2. 抽屉里挂本地目录：3 秒后左上角 `刮削元数据和海报 (1/295)：Film` 带转圈，抽屉状态行同步显示 `扫描中 (1/1)：源名 · 已发现 297 个文件`；12 秒后走到 `(14/295)`，期间第一行的占位字母逐张换成带评分的海报（`v25-m1`、`v25-m2`）。
3. 扫描 12 个文件的场景下进度行依次是 `更新首页...` → `刮削元数据和海报 (4/12)：Film` → `(8/12)` → `刮削完成，共处理 12 个条目，全部更新。`，同一段时间里「所有影视」从 46 部涨到 58 部（`v25-top.png` 四联截图）。
4. 刮到 `(215/295)` 时点「移除」：首页立刻回到「所有影视 0 部」+ 空状态；5 秒、13 秒后仍然干净，没有幽灵卡片，横幅也没有赖在扫描中（`v25-ab1/2/3`）。
5. 工具栏截图放大检查：搜索 / 排序 / 降序 / 扫描刮削 / 媒体源 / 设置六个符号形状、亮度、无底板（`v25-icons.png`）。

顺带确认：`局域网设备（自动扫描）` 那行从「正在扫描局域网中的 WebDAV / SMB 设备...」会变成「扫描完成，发现 1 个设备，点选后登录。」，之前截图里看到它一直在扫是真还在扫（子网枚举未完成），不是显示没更新。
