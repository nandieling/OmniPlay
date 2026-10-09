# 2026-10-07 登录页五个问题（安卓 TV 版，versionName 1.9）

对应反馈：局域网只扫出 SMB、三类媒体源排序、局域网行按钮太矮、SMB 目录弹窗显示不对、登录成功过的连接要能直接连。

## 1. 局域网只扫出 SMB，扫不到 WebDAV

两个原因叠加，都在 `local/LanDiscovery.java`。

- 调度：旧实现每台主机一个任务，任务内**串行**试 SMB 与各 WebDAV 端口。子网里只要有一台机器对 SYN 不回包（离线设备、打印机、开了防火墙的 PC），该主机就要吃掉「端口数 × 连接超时」，整轮扫描在 30 秒总闸前就结束的往往只有 445 这一列，WebDAV 探测根本没轮到。
- 判定：PROPFIND 带请求体，服务器（尤其是要认证的）常在读完 body 之前就回 401 并关闭连接，`getOutputStream()` 抛 broken pipe，状态码连同异常一起被吞掉，结果是"这台机器没回应"。

改动：

- 每个「主机 × 端口」一条任务（`ExecutorCompletionService`，线程池 96），先完成的先上报；总时长上限 45 秒，SMB 与 WebDAV 不再互相排队；单机每轮最多上报 2 个 WebDAV 端点，避免一台 NAS 五六个端口刷屏。
- 连接超时 400ms、HTTP 超时 1200ms；HTTPS 用 trust-all 探测（只决定要不要摆给用户看，真正连接仍走原有校验），scheme 猜错时自动换 `http`/`https` 再试一次。
- 判定规则重排：OPTIONS 有 `DAV` 头 → WebDAV；专用端口上 401 / 403 → WebDAV；PROPFIND 得到 207 / 401（或专用端口的 403）→ WebDAV。80 / 443 / 8080 这类共享端口必须拿到 `DAV` 头或真 207，否则普通 Web 管理页的 401 会误报。请求体写入失败不再吞状态码。
- `UNREACHABLE` 现在只表示"两种 scheme 都没有 HTTP 应答"，只此一种情况才放弃该端口其余候选路径；此前任何 PROPFIND 失败都会放弃整列路径，`/webdav`、`/dav` 这类挂子路径的服务更容易被漏掉。
- 端口清单 5006 / 5005 / 8443 / 8010 / 8011 / 8000 / 5244 / 8080 / 443 / 80；不在默认端口上的设备在行标题里带上端口。CloudDrive（19798）等未覆盖的端口仍需手动填地址。

## 2. 媒体源类型改为 WebDAV、SMB、本地目录

`selectedKind` 默认值改为 `webdav`，`kindRow` 的 `addView` 顺序与提示文案同步；`rebuildSetupDpadGrid()` 的焦点网格按页面视觉顺序重排（类型行 → 已保存的连接 → 地址/登录/手机输入 → 重新扫描 → 局域网设备 → 底部按钮），此前局域网行排在地址输入之前，遥控器上下顺序和眼睛看到的相反。`PhoneMainActivity` 同步排序与默认值。

## 3. 局域网行按钮加高

局域网与"已保存的连接"的行统一用 `listItemParams()`（`sdp(78)`，此前是按钮自然高度约 48dp），并把第二行地址/目录塞进同一个按钮：`listRowButton(title, detail)` 用 `SpannableStringBuilder` + `RelativeSizeSpan(0.74)` + `ForegroundColorSpan(COLOR_SUBTLE)` 排两行。D-pad 焦点仍在整行上，不额外增加焦点项。

## 4. SMB 目录弹窗显示不对

根因在数据结构：目录浏览器用 `File` 同时承载"名称"和"是不是目录"，写法是 `new File(name, "d")`，而 `File#getName()` 返回的是路径最后一段，于是每行都显示成 `d`；进入下一级时又把名字按 `/` 拼，路径直接错。SMB 侧还有两处问题：jcifs-ng 列容器必须用带结尾斜杠的 URL，以及 `listShares` 把 `_` 开头的共享一起隐藏了。

改动：

- 新增 `local/BrowseEntry.java`（`name` / `url` / `directory`，目录优先、名称不区分大小写排序），`SmbClient.listShares` / `list` 返回 `List<BrowseEntry>`，URL 由服务器返回值拼出（目录补结尾斜杠，标签去掉 jcifs 附带的斜杠），管理共享改为只过滤 `.` 开头与 `IPC$` / `ADMIN$` / `PRINT$` / 短 `*$`。
- `MainActivity` 浏览器（`loadBrowserEntries` / `renderBrowserItems` / `browserNavigateInto`）改吃 `BrowseEntry`：点一行直接进入该行的 URL，不再拼字符串；WebDAV 的"列自身"条目用 `sameDirectoryUrl()` 比较（忽略结尾斜杠与百分号编码），中文目录在路径行按百分号解码显示。
- `browserParentOf` 的 WebDAV 分支按 `scheme://authority` 边界算父级并以挂载根为止，避免 `https://host:5006` 被 `lastIndexOf('/')` 切成 `https:`；返回根目录时正确禁用"返回上级"。
- 浏览器行高从 `sdp(52)` 调到 `sdp(56)`。

## 5. 登录成功过的连接自动保存

新增 `local/SavedConnections.java`：JSON 数组存在 `TvSettings.PREFS` 的 `saved_connections` 下，按「协议 + `scheme://host[:port]` + 路径无关」合并成一条记录，含账号、密码、显示名与最多 4 个挂载过的目录，最多 12 台服务器，解析失败时整块作废而不是崩。

- 登录成功即 `recordLogin`（此时即使不选目录，账号也已存下）；确认挂载时 `recordMount` 把目录插到最前，并保留之前的显示名与目录列表。
- 登录页顶部新增"已保存的连接"区块：有记录才显示；点一行 → 有已挂载目录就直接保存媒体源并扫描（跳过登录框与目录弹窗），没有目录则打开已回填账号的登录框。扫描到的设备若命中已有记录，走同一条快速路径，行内第二行显示上次挂载的目录。
- 手动填写地址时，登录框也会按主机回填已保存的账号密码，副标题提示"已保存这台服务器的账号，直接点登录即可"。
- 升级前已有的媒体源在首次进入登录页时补录进该存储；设置里"移除"媒体源只删源、保留连接记录。

## 验证

- `local/LanDiscovery` 桌面端到端：`/tmp/lanprobe/LanProbeTest.java` 用裸 `ServerSocket` 假服务器喂给真实探测代码，20 项断言通过，覆盖 OPTIONS 带 `DAV` 头、401 抢在请求体之前回（旧 bug 场景）、只在 `/webdav` 下应答的服务器（走候选路径 + URL 与端口标签）、普通 Web 服务不被误报、共享端口上的 401 不被当成 WebDAV、端口全关时 `UNREACHABLE`。注：桌面 JDK 的 `HttpURLConnection.setRequestMethod("PROPFIND")` 会抛 `Invalid HTTP method`（Android 的 OkHttp 实现允许自定义方法），因此断言只走 OPTIONS 与端口规则这两条在两端都成立的路径，PROPFIND 分支需真机复核。
- `local/SavedConnections` 桌面端到端：`/tmp/pitest/SavedConnectionsTest.java`（最小 android 桩 + 手写 `org.json` 替身）32 项断言通过，覆盖 host key 归一（带/不带 scheme、带端口、SMB 自定义端口、中文路径、空串）、免密重连记录、重登不丢目录与显示名、多目录最新在前、多服务器与协议隔离、按主机匹配 `remove`、12 台上限与淘汰最旧、损坏 JSON 降级为空并可继续写入。
- `local/PhoneInputServer` 回归：24 项断言仍全部通过。
- `assembleRelease`（JDK 17，`--offline`）BUILD SUCCESSFUL，versionCode 20 / versionName 1.9，77.57 MB。
- 未做设备端验证：真机上的 WebDAV 扫描命中与耗时、SMB 共享列表与中文目录、以及"已保存的连接"一键重连，需要在盒子上复核。
