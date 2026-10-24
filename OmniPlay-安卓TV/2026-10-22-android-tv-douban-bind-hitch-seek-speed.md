# 1.41 绑定那一下的「卡一下」，以及快进加速与倍速

## 1. 别人复现不了的「卡一下」是真的

反馈是：豆瓣手动匹配绑定后，有用户看见卡「正在进入详情」，还有人看见绑定成功却没海报；
本机复现不出来。两条都查了，一条是真 bug，一条是版本差 + 一个补上的洞。

`bindDoubanFromManualMatch` 成功回调那串动作是：

```java
detailCache.remove(detail.id);      // 把这一条的详情缓存丢掉
... 关掉手动匹配弹层 ...
showDetail(detail.id);              // 重新进详情页
```

`showDetail` 一进来先看缓存，缓存刚被自己删掉，于是 `showLoading("正在进入详情")` 整屏盖上来，
等 `getLibraryItemDetail`（一次 SQLite 多表查询）回来才掀开。条目 id 根本没变，却走了一遍
「进详情页」的路径。用户机器慢、库大、或者那条正好要连详情图一起查，就看得见那一下；
机器快就几乎看不见——这就是「有人有我无」的全部原因，不是玄学。

改法是原地刷新：删缓存之后调 `refreshDetail(id, true)`，它回来直接 `renderDetail`，
中间没有整屏 loading。解绑豆瓣、以及同类型的 TMDB 关联（`result.mediaType + ":" + targetId`
等于原 id）都照这个改；只有跨类型改绑（电影 ↔ 剧集，实体换表重建、id 真变）才整页重开，
那时候盖一次 loading 是应该的。

## 2. 海报那条

主因是 1.39 修的那个：豆瓣图床查 `Referer`，没带就回 418，所以库里存着正确地址、屏上却没图。
报这条的人装的是 1.38 或更早，症状就正好是「绑定成功、评分简介都在、海报没有」。

顺手补了第二个洞：页面 meta 里那张 `og:image` 现在是 `qnmob3-sign.doubanio.com` 的签名地址
（`sa_cv` / `sa_ct` 加 `imageView2/1/q/60/w/300/h/300` 那串），`isUsablePosterUrl` 一直把它判死。
图片本体在 `img3.doubanio.com` 上是同一个文件，签名只是临时门票，于是 `normalizePoster` 里
把 `qnmob*` 主机换成 `img3.doubanio.com`，剩下照旧（`large/public` 改写成 `s_ratio_poster/public`、
砍掉查询串）。这样 `sub-cover` 那行哪天没了，海报还能从 meta 兜出来。自检里加了一条专门喂
「只有签名地址」的页面。

## 3. 快进：先让它真的快

`handlePlayerHorizontalKeyDown` 的结构是：按下时挂一个长按定时器，重复事件一来就进平滑快进，
每 `PLAYER_SEEK_REPEAT_MS`(80ms) 走一步 `PLAYER_SEEK_STEP_SECONDS`(0.8 秒)——一路 10× 实时速度，
40 分钟的片子想往后跳十分钟得按住一分钟。短按不参与跳转（那是给控制条左右挪焦点用的），
所以「快进较慢」说的只能是按住不松那一段。

改成步长随按住时间爬升：0.7 秒内 10×，1.8 秒内 30×，3.5 秒内 100×，再往后 250×。
按住时间记在 `playerSeekHoldStartedAtMs`，每步重算，进度预览照旧跟着刷。

## 4. 倍速：长按松手后连点右键

按要的触发来：长按右键松手 → 开一个 900ms 的窗口；窗口内每点一下右键重新计时，
第 1 下 2 倍、第 2 下 3 倍、第 3 下及以后 4 倍。窗口过了再点右键算用户想干别的：
正在倍速就顺手退回 1 倍，这次点按照旧交回控制条的焦点逻辑（不会吃掉正常导航）。

倍速只写 mpv 的 `speed`（`MpvPlayer.setPlaybackSpeed` → `MpvVideoView.setPlaybackSpeed`），
不动进度、不重建解码链路，直出与兼容模式都吃这一条。回显有两处：控制条左角一枚
「3× 快进」角标（1 倍时隐藏），以及换档时一句 toast。换片、换播放模式会新建 mpv 实例，
`resetPlayerSpeedMultiplier()` 在两处创建点静默归位；退出播放页时字段一起清掉。

## 5. 验证

自检三项全绿（`DoubanParseCheck` 新增两条：签名地址换回正规 CDN、页面标题的栏目名被剥掉）；
`compile.sh` / `release.sh` 通过，产物 `dist/OmniPlay-TV-v1.41-arm64.apk`（versionCode 52）。

真机要看：绑定与解绑后是否还有整屏 loading 一闪；长按右键 3 秒后是否明显飞起来；
长按松手后连点三下的节奏（每下间隔 0.9 秒内）能否依次上到 2 / 3 / 4 倍；4 倍时角标是否显示；
过一会儿随便点一下右键能否回到正常速度；倍速中途换播放模式（会重建实例）是否留在 1 倍。

## 6. 同日撤下倍速（1.42）

倍速这套东西装上去之后自己用了一晚就撤了：同一次「点右键」在长按之后是倍速、过一会儿又得
还给控制条挪焦点，遥控器上手感含混，快进这件事反而变得要想着按。撤走的是
`MpvPlayer.setPlaybackSpeed` / `MpvVideoView.setPlaybackSpeed`、`handlePlayerSpeedTap`、
倍速角标与那个 0.9 秒窗口；**步长随按住时间爬升保留**，它治的是「快进较慢」这个原问题，
而且不需要用户记任何新节奏。

要倍速的话播放页本来就有：兼容模式下 mpv 吃 `speed`，回头值得做成控制条上一颗明写的按钮，
而不是藏在方向键的节奏里。
