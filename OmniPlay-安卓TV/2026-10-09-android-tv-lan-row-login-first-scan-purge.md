# 1.20：局域网设备行照 mac 版、点设备先登录、扫描中移除媒体源不再留海报

## 点局域网设备是去登录，不是直接挂载

`connectDiscoveredDevice()` 里有个捷径分支：只要 `SavedConnections` 存过这台服务器上次挂载的目录，点一下就直接 `mountMediaSource()`，连登录框都不弹。用户想换共享目录、或者第一次只想看看里面有什么，根本没有机会——症状就是「点一下直接挂上了」。

现在这一行统一走 `openLoginDialog()`：地址与已存账号回填进表单，「登录并选择目录」通过后再进「选择媒体目录」，选定才挂载。和 mac 版 `PosterWallView.openDiscoveredDeviceFromManageSheet()` → `prepareWebDAVBrowserLogin(for:)` 是同一条路径。已保存的账号仍然生效，只是从「跳过登录」变成「填好等你确认」。

## 那一行的格式

标题口径改成 mac 的 `discoveredDeviceTitle()`：探到设备名（NetBIOS 名）就用设备名，否则 `WebDAV 192.168.0.150`。端口不再掺进标题——原来是 `WebDAV · 192.168.0.150 · 5005`，同一台机器开 http / https 两个服务时两行标题长而难读，而 mac 上是「标题相同、地址不同」。第二行固定显示探到的那条地址（WebDAV 过 `UrlLabels.displayPath()` 解码），不再被「上次挂载的目录」顶掉。

左边补上 mac 那行的图标位：新增 `ui/KindIcons`，用 Canvas 画，WebDAV 是地球（圆 + 经纬线 + 中央经线椭圆）、SMB 是机箱（三条横杠带指示灯），22dp，取主题强调色，`Widgets.listRow(title, detail, leading)` 用 compound drawable 挂上去，行的焦点样式与高度都不变。

## 移除媒体源后海报还在墙上

1.17 已经让 `removeSource()` 连带 `purgeSource()`，但漏了一种时机：**移除发生在扫描进行中**。`scanAll()` 开头就把源列表快照进 `enabled`，之后每个文件照样 `registerFile()` 登记回去，`removeMissingFiles()` 又把它们算作「这一轮扫到的文件」保留下来——于是海报活到下一次扫描。用户那边正是这个场景：WebDAV 刚挂上、几百分页的 PROPFIND 还在飞，顺手点了移除。

两道锁：

- `LocalEngine` 存一份 `liveSourceIds` 快照（在 `sources()` 解析时和 `persistSources()` 落盘时刷新，不额外读偏好），`registerFile()` 进门先问「这个源还在不在」，不在就直接返回，扫描报告也不再把它计入新增。
- `scanAll()` 在 `removeMissingFiles()` 之后调 `LocalMediaLibrary.purgeUnknownSources(liveSourceIds)`：从 `videoFile.id`（`local-<sourceId>:<路径>`，由 `sourceIdOf()` 解析）挑出源已经不在了的行，连同 `offlineCache`、内存里的播放进度覆盖一起删，再走 `pruneOrphanEntities()`。删除数量并回 `removed`，横幅那句「扫描完成：新增 X，移除 Y」里就包含它们。`purgeSource()` 与它共用新的 `deleteFileRows()`。

顺带把 `ImageLoader` 里写死的 `OmniPlay-Android/0.1` 换成 `Http.USER_AGENT`，取海报的 UA 不再停在 0.1。

## 模拟器上验到的

- 局域网行渲染成「机箱图标 + `SMB 10.0.2.3` + `smb://10.0.2.3`」，设置页与媒体源抽屉共用同一个 `lanDeviceButton()`。
- 点这一行弹出「登录 SMB」（用户名 / 密码 + 取消 / 登录并选择目录），媒体源列表仍是空的，没有再直接挂载。
- 选 `/storage/emulated/0` 挂载后扫出 46 条——收尾那次 `purgeUnknownSources` 没有误删还活着的文件；抽屉里点「移除」，首页立刻「0 部 / 媒体库为空」。
- 「扫描进行中移除」这个时机在模拟器上掐不准（本地目录一次扫描不到 1 秒），需要真机确认：WebDAV 挂载后立刻在抽屉里点移除，等这一轮扫描结束，卡片应当随之消失，横幅里的「移除 N」会包含这些行。

版本 1.20（versionCode 31）。
