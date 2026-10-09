# 安卓 TV 1.15：设置精简、离线缓存退场、蓝光只收主片

这一轮都是「用的人才知道别扭」的问题：设置里和工具条重复的区块、看不出状态的开关、电视上根本用不上的离线缓存，以及原盘目录被刮成一堆条目。

## 设置面板

设置里的「媒体源」卡片整块删掉。工具条上的「媒体源」按钮打开的是媒体源管理，那里已经有启用 / 重命名 / 移除 / 添加 / 重新扫描并刮削 / 清空索引；设置里再放一份「添加媒体源 / 重新扫描 / 刮削海报 / 管理媒体源」，用户只会先入为主地怀疑这两个「媒体源」是不是两件事。删掉之后设置面板从 TMDB 刮削开始，扫完还剩七个分组。

顺带做三件小事：

- TMDB 的两个输入改成标题在上、输入框在下，输入框文字左对齐。`Widgets.input()` 之前没写 gravity，在带图标的输入框里长 URL 看起来像被居中截断。
- 开关（`Widgets.SwitchRow`）从「两层 GradientDrawable」换成自绘 Drawable：凹下去的灰底 + 描边 + 滑块靠左 + 一个「关」字，开是主色底 + 白滑块靠右 + 一个「开」字。原来的关态是 `Theme.BORDER` 加一点透明度，在浅灰卡片上几乎看不见，用户反馈的「不勾选状态没有相应状态显示」就是这个。状态写进字里比Slider 位置可靠得多，遥控器上看一眼就够。
- 面板初始焦点落在第一个主题胶囊上，而不是某个输入框——设置一打开就弹软键盘很烦。

## 离线缓存退场

TV 盒子接的就是 NAS，本机那点空间既不够放一部原盘，也没有「出门前缓存」的场景。设置里的缓存卡片、详情页的「缓存本片 / 缓存本季 / 缓存本集」、状态条里的缓存进度、`cache().setListener(...)` 一起撤下。`OfflineCacheManager` 留在 `local/` 里没删：播放路径仍会先查 `cachedPathFor()`，之前缓存过的片子不会因为升级突然变成网络流。

## 蓝光盘被刮出一堆 m2ts

用户看到的是首页多出一排同名的蓝光条目。查下来问题不在文件名解析，在扫描阶段就没了 mac 那层过滤。

mac 版的做法（`MediaLibraryManager.swift`）：本地枚举器和 WebDAV 扫描器都把路径里带 `BDMV` 且下一级是 `STREAM` 的文件按「BDMV 上一级目录」分组，BDMV 里其他文件（PLAYLIST、CLIPINF）直接丢弃，非 BDMV 文件照常收录；然后每组调一次 `selectedBluRayStreamIndices`，按体积挑主片——最大的一条不小于第二名的 1.55 倍就只留它，否则按 0.72 的门槛把分卷合辑整组留下，体积全未知退回时长，再退回分卷号最小的那条。

TV 版此前是三种媒体源各自剪目录：

```java
// 旧：LocalEngine.isSkippableFolder
return lower.equals("bdmv") || lower.equals("stream") || lower.equals("lost+found")
        || lower.matches("(?i)^(disc|dvd|cd|volume|vol)\\s*\\d+$") ...
```

本地和 WebDAV 因此一盘都收不到（`BDMV` 被剪在门外，体积根本没机会参与判断）；SMB 反过来全盘照收：jcifs 的 `SmbFile.getName()` 给目录名带上结尾 `/`，`lower.equals("bdmv")` 对上的是 `"BDMV/"`，匹配失败，于是走进 `BDMV/STREAM`，每条 m2ts 都 `registerFile` 一次。花絮、宣传片、正片分段全都入库，这就是「所有 m2ts 都被刮出来」。

改法是把判断从 walker 里搬出来，交给一个纯逻辑类：

- `local/BluRayStreams.java`：`isDiscPath` / `isStreamPath` / `discFolderOf` / `selectMainFeatures`，1.55 与 0.72 两条阈值与 mac 一致。不含任何 Android 类型，桌面 JVM 直接跑。
- 三个 walker 剪掉的目录只剩系统垃圾（`lost+found`、`@eadir`、`.nomedia`、`.` / `_` 开头），`BDMV` / `STREAM` / `DISC1` 都走进去；SMB 的条目名先去掉结尾 `/` 再判断；两个网络 walker 的深度从 8 提到 12，跟本地一致——光盘结构自己就要吃掉三层。
- 三个 sink 都带上文件体积：本地 `file.length()`、SMB `entry.length()`（失败当 0，不能因为一台服务器不答就整轮扫描挂掉）、WebDAV 解析 PROPFIND 里本来就在请求却没读的 `getcontentlength`（`Entry` 新增 `sizeBytes`）。
- `LocalEngine.ScanCollector` 负责分流：普通视频文件立刻登记；`…/BDMV/…` 里在 STREAM 下的按 `discFolderOf` 攒组，不在 STREAM 下的丢弃；`finish()` 时每组只登记 `selectMainFeatures` 选中的那些，并把少掉的条数记进 `ScanReport.skippedStreams`，扫描报告写成「蓝光非正片分卷跳过 N」。

被跳过的分卷不会进 `currentFileIds`，所以 1.14 之前留下的那批「每条 m2ts 一个条目」会在下一次扫描的 `removeMissingFiles` 里自动清掉，不需要先手动清空媒体库。

`WebDavClient.groupFolderFor()` 单独留了分盘目录的跳过规则：原来靠 `isSkippableCollection` 顺带过滤 `GENERIC_DIR`，现在那层只负责系统垃圾，分盘 / 光盘结构目录必须在这里继续跳过，否则原盘剧集的组名会变成 `DISC1`。

## 验证

- `/tmp/pitest/run-bluray.sh`（`BLURAY_OK`）：分组键、`BDMV` / `STREAM` 判定、1.55 独苗、0.72 成组、恰好 1.55 判独苗、体积未知退回分卷号、非数字分卷排后、`includeExtras` 全留。
- 模拟器上造了两张盘塞进已挂载的本地源：`Dune.2021.1080p.BluRay.REMUX/BDMV/STREAM/` 4G + 400M + 200M 加一个 PLAYLIST，`Interstellar.2014.1080p.BluRay/BDMV/STREAM/` 20G + 19G。扫描报告 `扫描完成：文件 8，新增 8，新增条目 8，蓝光非正片分卷跳过 2`：五条 m2ts 只留三条，两个条目，PLAYLIST 没进库。
- 设置面板在 1080p@213dpi 上走 24 次 DOWN：0 次丢焦点、0 次出屏，行序与视觉顺序一致（主题胶囊 → 分段控件 → 开关 → 4K 播放模式 → 检查更新）。就地切「详情页显示分集简介与进度」，焦点与滚动位置都不动，滑块从「关」变「开」。
- 详情页只剩播放 / 观看状态 / 编辑与分集的「改副标题」，没有缓存字样；状态条不再出现缓存进度。媒体源管理里仍然是添加 / 重新扫描并刮削 / 清空索引三件事。
- 七个 JVM 用例全绿；`adb logcat -s AndroidRuntime:E` 全程无 FATAL。

## 已知边界

- 挑主片只按体积。mac 版在体积缺失时还能退回时长（播放期能拿到 ffprobe 结果），扫描阶段的 WebDAV / SMB 拿不到时长，所以这条分支在 TV 上永远走不到，退回的是分卷号。真机上如果碰到不报 `getcontentlength` 的 WebDAV 服务器，原盘会退化成「取 00001」，对绝大多数盘恰好是对的。
- `ScanReport.newEntities` 沿用旧口径：每登记一个文件就 +1，即使条目已存在。要看真实新增条目数得看首页计数，这里没顺手改，避免牵动 mac 版对齐过的报告文案。
- 分卷合辑会作为同一部片子的多个版本收录（详情页的「版本 / 文件」里能看到），不额外合并；与 mac 版一致。
