# 2026-10-12 · WebDAV 遍历走出指数量、扫描途中就出海报（1.28）

## 真机报回来的两个现象

用户接上 WebDAV 媒体源之后：首页很快摆出 255 张未刮削海报、`所有影视 255 部`，可是左上角「已扫描多少个文件」花几分钟涨到 **169990** 就停在那儿不动，`新增 0`，刮削一次都没开始；点某张海报进详情页再返回，那一张会立刻被刮削。再过几分钟软件**自己退出回到电视主页**，重进软件，扫描数字从头开始。

两句话就能对上代码里的两处毛病：数字虚高而新增为 0 ＝ 同一批文件被反复端上来；刮削不开始 ＝ 刮削严格排在整轮扫描之后，而这一轮永远走不完。

## 根因一：Depth:1 响应里的「目录自己」

WebDAV 遍历在 `WebDavClient.walk` 里是深度优先，唯一的收敛条件是 `maxDepth = 12`：

```java
for (Entry entry : list(url)) {
    if (!entry.isCollection) { ... sink.onVideo(...); continue; }
    walkInternal(entry.href, depth + 1, sink);   // 没有 visited，也没有跳过自己
}
```

RFC 4918 要求 Depth:1 的 PROPFIND 响应里带上**本次被请求的那个资源本身**，nginx、Apache、Nextcloud 全都这么回。于是 `/影视` 的清单里就有 `/影视/` 这一条，递归回去又拿到同一份清单，每个目录原地自套 12 层；目录清单缓存命中，所以不再走网络，只是没完没了地把同一批文件端给采集器。8 层目录就是 12^8 这个量级，`已扫描` 涨到十几万完全诚实——只是数的是重复条目。mac 版之所以没这毛病，是因为它走广度优先并且带 `visitedDirectories`，另有一道 `relativeChildPath` 要求条目路径必须真的落在本次列出的目录之下（`path != anchorPath`）。

### 改法

`WebDavClient.walk` 换成 mac 版同一走法：

- 广度优先，队列元素是 `PendingDirectory(url, depth)`；
- `visited` 按 `directoryKey(url)` 判定：主机之后的路径段、先解一层百分号、再去掉结尾斜杠。编码写法不同、结尾斜杠有无不同都算同一个目录（与 `normalizePath` 同一口径）；
- 显式跳过 `directoryKey(entry.href).equals(本目录 key)` 的那条自引用；
- `maxDepth` 留成兜底（自引用不再白吃层数，12 层是真实的 12 层）；
- 两条底线：一轮最多列 `MAX_DIRECTORIES = 20000` 个目录（超了直接报「这个媒体源的目录超过 20000 个，这一轮先停在这里」），`directoryCache` 超过 `MAX_CACHED_DIRECTORIES = 4096` 就整份清空。畸形响应最多让这一轮少扫一片，不该把电视跑死。

`SmbClient.walk` 同步改成 BFS + visited（SMB 路径不区分大小写，比对前 `toLowerCase`），共享里的环回链接是同一类坑。

## 根因二：刮削排在整轮扫描后面

`scanAndScrape` 是「`scanAll` → `scrapePending`」，WebDAV 走不完，队列里那 255 个条目就一直不动。用户点进详情页会触发那一条的单独刮削，于是看起来「只有点开的卡片会刮」。

现在扫描线程自己会挤时间刮：

```java
if (++newSincePump >= SCRAPE_PUMP_AFTER_FILES) {   // 20 条新文件
    newSincePump = 0;
    pumpScrapeDuringScan(listener);                // 借 4 秒刮一批
}
```

`scrapePending(listener)` 拆成 `scrape(listener, budgetMs, summarize)`：`budgetMs = 0` 是原来那次不限时、报总结的整轮；扫描途中传的 4 秒预算在每一条开始前检查截止时间，用完就带着剩下的条目退出（它们仍是未刮削，下一轮开头的 `requeueUnscraped()` 会把它们重新排进队列）。每个源扫完也刮一轮，与 mac 版「每个源扫完 `loadData()` 刷首页、随后刮削」同向。借出来的那几轮不报「刮削完成，共处理 N 个条目」，只留「刮削元数据和海报 (i/n)：片名」这一行进度。

## 顺带踩到的两个坑（都是本轮改动自己引入的）

1. `ScanCollector.register` 里我把 `fileIds.add(...)` 挪到判重之后，结果上一轮就入库的文件不再进「本轮还在」名单，收尾的 `removeMissingFiles` 把整面墙删了 —— 真机验证时 `所有影视` 从 201 部直接掉到 10 部才发现。记账必须走在每一条出口之前。
2. 同一处把 `report.scannedFiles++` 也压在判重之后，于是稳定态扫描写成「共扫描 0 个文件」。现在名单拆成两份：`knownPaths`（是否入库过，决定要不要重新入库）与 `seenThisRound`（本轮是否数过，决定要不要重复计数），两个数各自诚实。

## 根因三：进程自己退出

`runAsync` 早就接住 `Throwable`，但 `ImageLoader` 的解码任务是直接 `executor.execute(() -> {...})` 且只 `catch (Exception)`。遍历空转那几分钟把堆搅紧，一次 `BitmapFactory` 解码抛出的 `OutOfMemoryError` 从线程池逃出去，默认未捕获处理器写完结账就把进程带走——在电视上就是「过几分钟自己回到主页」。现在：解码任务与 `fetch` 都接 `Throwable`（一张海报换一条日志），清单缓存加上限，`AndroidManifest` 开 `android:largeHeap`。

## 验证

假 WebDAV（`/tmp/fakewdav.py`，17 个目录 / 240 个文件，每条 207 响应都带头「目录自己」那一条）+ 假 TMDB（`10.0.2.2:8099/tmdb`，每次搜索 0.6 秒）走完整挂载流程：

- 冷启动第一轮：服务端日志 `sed 's|.* /dav|/dav|' | sort | uniq -c` → 16 个目录各 **1 次**（旧版这里是指数量，且第二轮之后仍不停）；`.Trash` 从未被请求，说明垃圾目录照旧被剪。
- 扫描途中左上角就已经在写 `刮削元数据和海报 (4/35)：Movie024`，海报一张张上色，`所有影视` 41 → 190 → 210 部一路上涨（`w28-08-wall`、`w28-12-fresh`、`w28-13-scan1done`）。
- 第二遍扫描：`扫描完成：新增 0，移除 0，共扫描 239 个文件。`（`w28-19-summary2`），海报墙一张没少，说明增量与「本轮还在」名单都对。
- 冷启动 → 回桌面 → 重进：首屏仍是完整海报墙，没有出现全屏媒体源窗口，也没有半路退出。

## 留给下一轮

- 首页仍是 `GridView` + 全量重建，RecyclerView 化是流畅度的下一大步（用户已提过卡顿）。
- 遍历目前只要有一个子目录报错就整源失败（与 mac 版一致）。真机上出现「某个保护目录 403 导致整源扫不完」时，值得改成跳过该目录 + 在报告里写一行。
