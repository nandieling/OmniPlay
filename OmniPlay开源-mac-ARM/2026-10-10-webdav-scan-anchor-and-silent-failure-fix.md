# 2026-10-10：WebDAV 扫描「静默零结果」，失败不回音（x64 / ARM mac 同步）

## 现象

x64 版挂好 WebDAV 再点「扫描刮削」：左上角转一会儿就没了，海报墙一部片子都不出现，也没有任何报错。

## 排查

本机起了两台真服务器（Apache 2.4 + mod_dav 做规矩的，一台自制 Python 服务器做「不规矩」的），直接跑 `MediaLibraryManager.scanLocalSourceWithResult`，绕开界面看扫描器的真实返回：

| 服务器行为 | 改之前 |
| --- | --- |
| Apache：绝对路径 href、207 | `共发现 3 个文件`，首页 3 部 |
| 把 `/电影` 规范化成 `/电影/`（301 或 307） | `success=true`，`共发现 0 个文件`，不报错 |
| 返回相对 href（`./`、`Inception.mkv`） | `success=true`，`共发现 0 个文件`，不报错 |
| 路径写错回 404 | `category=config`，只在左上角闪一行「失败」 |

扫描主链路没坏，坏的是两件事：

1. **相对路径的锚点选错了。** `WebDAVScanner` 永远拿「用户配的那条 baseUrl」当基准算相对路径（`relativePath(for:baseURL:)`）。服务器只要补个斜杠、重定向到别名路径、或者回相对 href，`entry.url.path.hasPrefix(baseURL.path)` 就不成立，于是每一个条目都被 `continue` 静默丢掉，最后报「扫描完成：新增 0，共发现 0 个文件」——成功，所以不弹任何东西，首页自然空着。同文件的 `WebDAVDirectoryBrowser` 早就为此打过补丁（`directoryURLForRelativeResolution` 先补斜杠再解析 href），扫描器没跟上，这就是「浏览能看见文件夹、扫描扫不出文件」的来历。
2. **失败没有回音。** `MediaSourceScanResult` 里的 `userMessage` / `diagnostic` 从没上过界面，`MediaSourceScanDiagnosticsFormatter.diagnosticsReport` 是死代码；单个媒体源失败只在左上角写一行「失败：源名（类别）」就随整轮扫描一起收尾。401、网络不可达、404、钥匙串读不出来，在用户眼里都是「点了没反应」。

顺带一个误导源：`authConfig` 指向的钥匙串凭据读不出来时，扫描器退化成匿名 PROPFIND，服务器回 401，界面翻译成「认证失败，请检查 WebDAV 用户名/密码」——用户会一直去改一个本来正确的密码。

## 改法

1. `listDirectory` 返回 `DirectoryListing(items:effectiveURL:)`，`effectiveURL` 取 `HTTPURLResponse.url`，即跟随重定向之后真正响应的那个地址；条目的相对路径以它的路径为锚点算（`relativeChildPath(of:under:)`）。
2. href 改为相对 `effectiveURL` 解析，并且先补斜杠（`directoryURLForRelativeResolution`，与浏览器那条路径同一个函数语义）。绝对路径 href 行为不变。
3. 待扫队列元素从裸 URL 换成 `PendingDirectory(url:relativePrefix:)`，子目录继承「相对挂载根目录的前缀」，重定向改变了服务器侧路径也不会把子树整段丢掉。
4. `resolveCredential` 改成 `throws`：凭据引用存在但钥匙串读不出来时，直接抛 `credentialUnavailable` 并归类为 `.auth`，消息写清「请在媒体源里重新登录一次」，不再发匿名请求换一句误导性的 401。
5. `PosterWallView.reportScanOutcomes`：整轮扫描跑完必须有回音。有源失败 → 弹「扫描刮削失败 / 部分媒体源扫描失败」，内容是早就写好的 `diagnosticsReport`（分类、HTTP 状态、URLError 码、端点、原始消息）；全部成功但一个文件都没扫到、且首页还是空的 → 弹「没有扫描到视频文件」，列出每个源的端点和排查提示。库里已有内容时 0 命中不再打扰。

## 验证

- 新增 3 条回归用例：`webDAVScannerReanchorsAfterServerRedirectsCollection`、`webDAVScannerResolvesRelativeHrefs`、`webDAVScannerFailsFastOnMissingKeychainCredential`。先把两处源文件退回 HEAD 跑一遍，三条全红；换回改动，三条全绿。
- 真实服务器复现：301、307、相对 href 三种行为都从 `scanned=0` 变成 `scanned=1`，规规矩矩的 Apache 仍与改前一致（3 个文件、相对路径不变）。
- `xcodebuild test -only-testing:OmniPlayTests` 39 条全绿；`xcodebuild build ARCHS=x86_64 ONLY_ACTIVE_ARCH=NO` 通过、零告警。

## 两处工程都改了

`OmniPlay/MediaLibraryManager.swift`、`OmniPlay/PosterWallView.swift`、`OmniPlayTests/WebDAVMockIntegrationTests.swift` 三个文件在 ARM 与 x64 工程里保持逐字节一致。

## 那台 Intel 机器上还需要确认的

修复覆盖了「服务器补斜杠/重定向/回相对 href」这一类静默失效。如果那台机器的原因是别的（局域网权限被拒、凭据真错了、路径指向的是 WebDAV 服务根目录），现在会直接弹一张诊断卡片，把分类、HTTP 状态、URLError 码和去掉了账号密码的端点亮出来——照着那一栏就能定位。

## 那张卡片寄回来之后：真正的原因是数据库结构没升级完

用户在新构建上复现，卡片写得很清楚：

```
[源] 电影   协议=webdav   分类=unknown   端点=http://192.168.0.150:5005/%e7%94%b5%e5%bd%b1
HTTP状态=-   URLError=-   重试次数=0
消息=SQLite error 19: FOREIGN KEY constraint failed - while executing
`INSERT INTO "videoFile" ("id", "sourceId", "relativePath", ...)`
```

没有 HTTP 状态、没有 URLError 码，说明 PROPFIND 是成功的：文件扫到了，倒在入库这一步。往下查是个升级路径的坑，跟 WebDAV 无关：

1. x64 工程 1.7 及更早的迁移阶梯是 v3=customSubtitle、v4=isEnabled/disabledAt、v5=lastPlayedAt、v6=fileSize、v7=doubanMetadata、v8=addressConfig；1.8 把同一批改动重排成 v3…v7，并把 v8 换成 `movie.directors/casts` + `categoryFolder`。
2. GRDB 的 `DatabaseMigrator` 只按「这个编号有没有被记录」决定执不执行。那台 Intel 机器的库早就记到 v8，于是新 v8 被当成已经做过，直接跳过。
3. 于是 `movie` 没有 directors/casts、`categoryFolder` 表不存在。`insertPendingMediaGroup` 里占位影片写的是 `try? dummyMovie.insert(db)`：因为「no such column」失败，错误被吞；紧接着 `videoFile.movieId` 找不到父行，抛出 19 号外键错误，整组入库回滚。每一组都回滚，海报墙自然一部都不剩。
4. ARM 那条线 v1…v7 的内容与新版完全一致（v8 是新增的），同样的库在 M 系列机器上一切正常——这就是只有 x64 版翻车的原因。

## 改法（第二轮）

1. `AppDatabase` 新增 v11 迁移 + `healSchemaGaps`：不看编号，只按代码真正依赖的列/表清单补结构（缺列 `ALTER TABLE ADD COLUMN`，缺缓存表 `CREATE TABLE IF NOT EXISTS`）；迁移跑完还无条件自愈一次，半途失败的库也救得回来。v11 会被记录，所以每台机器只补一次。
2. 迁移失败不再只 `print`：`AppDatabase.setupError` 记下问题，`PosterWallView` 启动时弹「媒体库没能升级完成」；结构仍不全时 `setup()` 抛 `AppDatabaseError.schemaIncomplete(缺什么)`。
3. 入库不再吞错：占位影片改用 `insert(db, onConflict: .ignore)`（同 id 已存在时行为不变，真出错就冒泡），`videoFile` 写入失败包成新的 `MediaSourceScanError.databaseWriteFailed`；错误分类新增 `database`，入库失败也走 `classifyScanError`，不会再一律记成 unknown。

## 验证（第二轮）

- 新增回归用例 `legacyX64DatabaseGetsHealedAndIngestsScan`：手工搭一个「x64 1.7 成品库」（grdb_migrations 记到 v8、movie 没有 directors/casts、没有 categoryFolder），跑 `AppDatabase.setup` 后断言列和表补齐、`grdb_migrations` 记下 v11、旧数据原样保留，再跑一遍真实扫描链路，断言文件成功入库。
- 把 `healSchemaGaps` 短路掉重跑，用例失败并抛 `.schemaIncomplete(["categoryFolder", "movie.directors", "movie.casts"])`，正是那台机器缺的东西。
- 两个工程 `xcodebuild test -only-testing:OmniPlayTests` 各 40 条全绿；`xcodebuild build ARCHS=x86_64 ONLY_ACTIVE_ARCH=NO` 通过、零告警。

那台 Intel 机器下次启动会自动补齐结构（控制台打印一行「🧬 媒体库结构已自动补齐：列 movie.directors、列 movie.casts、表 categoryFolder」），再点扫描刮削就能出海报，不用删库重扫。往后改迁移阶梯：已发布过的编号别再挪位置，要重排就用 GRDB 的 `registerMigration(_:merging:)`。

## 第三轮：海报出来了，但指针转圈几十秒

用户在新构建上再反馈：扫描能出海报了，可每次扫完左上角停在「检测 TMDB 连通性...」几十秒，这期间鼠标是转圈的等待指针。

在开发机上抓了一份扫描中进程的主线程采样（`sample`，5 秒），栈顶长这样：

```
DispatchQueue_1: com.apple.main-thread
  PosterWallView.triggerScanAndScrape(...)      PosterWallView.swift:3751
  MediaLibraryManager.scanLocalSourceWithResult MediaLibraryManager.swift:3662
  WebDAVScanner.scanFiles(in:)                  MediaLibraryManager.swift:3029
  WebDAVScanner.resolveCredential(...)          MediaLibraryManager.swift:3104
  WebDAVCredentialStore.loadCredential(id:)     DatabaseModels.swift:485
  SecItemCopyMatching → … → mach_msg            ← 一直等 SecurityAgent 回话
```

三件事一次说清了：

1. 整条扫描管线跑在主线程上。工程的默认隔离是 MainActor，`triggerScanAndScrape` 里那个 `Task(priority:)` 继承了主线程，于是扫描、解析、读凭据全在主线程排队，只在 `await` 处喘口气。
2. 读 WebDAV 凭据的 `SecItemCopyMatching` 带着 `LAContext`，要等 SecurityAgent，遇到 ACL 校验还会弹授权框。同步调用多久都有可能——这就是「几十秒」里最硬的一段。
3. 另一半在海报墙：`CachedPosterView` 在 `onAppear` 里同步 `Data(contentsOf:)` 读整张 JPEG 再 `NSImage(data:)`。这台机器 1926 张海报、138MB，整库刷新就是主线程连着解几百张全尺寸图；海报目录在 iCloud 容器里时更糟，没本地化的文件同步读一次要等云端下载完。

## 改法（第三轮）

1. `WebDAVCredentialStore` 整类改成 `nonisolated`，新增 `loadCredentialAsync(id:)`：读钥匙串丢到全局队列，用续接把结果送回。扫描链路（`WebDAVScanner.resolveCredential`）和源连通性预检都换用它；同步版原样留给播放器和媒体源面板，那些场景本来就有用户在等。
2. 新增 `PosterImageLoader`（`PosterManager.swift`）：后台读盘 + ImageIO 只解到 640px（卡片海报够 Retina）+ `NSCache` 记 240 张 / 160MB。`CachedPosterView` 先要内存缓存，没有就派后台任务，主线程只剩「把成品图贴上去」；文件确实在但内容不是图片时照旧删掉，让上层重下。
3. `PosterManager.downloadPoster` 的校验与落盘挪进 `Task.detached`：刮削时几十张海报连着写，主线程一张都不替它解码。
4. `TMDBService.probeConnection` 补两条：探测成功后 60 秒内直接复用上次结果（连着点两次「扫描刮削」不再白等一遍网络）；探测请求超时显式传 6 秒——原先 `makeRequest` 里写死的 10 秒会盖掉探测会话自己的 8 秒配置，最坏情况白等十秒。

## 验证（第三轮）

- 新增用例 `Poster image loader downsamples and only deletes files that are really broken`：1200×1600 的 JPEG 解出来正好 480×640、进内存缓存；写一段假字节伪装成 .jpg，断言读不出图且文件被删掉。
- 两个工程 `xcodebuild test -only-testing:OmniPlayTests` 各 41 条全绿；`xcodebuild build ARCHS=x86_64 ONLY_ACTIVE_ARCH=NO` 通过、零告警。
- 开发机实测：把钥匙串授权框留在屏幕上挡着扫描，界面照样画出来、海报照样显示，指针不再钉死——这是主线程真的腾出来的直接证据。

那台 Intel 机器要用上这一轮，得重新构建装上。装完第一次扫描「检测 TMDB 连通性...」还是会亮几秒（那是真的在探 TMDB），但指针能动、海报会一张张出来；一分钟之内再点扫描，这一步直接复用上次结果，标签一闪而过。
