# 1.39 豆瓣绑上了却没海报：图床要 Referer

## 1. 现象

绑定豆瓣成功，评分、简介、年份都在，就是没海报（1.38 新加的豆瓣候选行也一样没图）。

第一反应是解析坏了：现版 `m.douban.com` 条目页已经没有 JSON-LD，`v:image` 也没了，
`og:image` 指的是 `qnmob3-sign.doubanio.com/...?sa_cv=…&imageView2/1/q/60/w/300/h/300`，
而 `isUsablePosterUrl` 明确不要 `qnmob` 那种签名缩略图。看着很像是四条海报正则全落空。

## 2. 先证明解析没问题

把真实页面（`m.douban.com/movie/subject/1395091/`，未麻的部屋）抓下来直接喂给
`DoubanClient.parse`：

```
片名=未麻的部屋 - 电影  年份=1997  评分=9.1
海报=https://img3.doubanio.com/view/photo/s_ratio_poster/public/p1351050722.jpg
```

海报字段是有值的——`<a class="sub-cover">` 里那张 `<img src>` 一直是第一条正则的形状，
而且地址是 `s_ratio_poster/public` 的正规地址，入库的也是它（`saveDouban` 在条目原本没海报时
把 `movie.posterUrl` 写成它）。所以断点不在解析，也不在库，在取图。

## 3. 真因：doubanio 图床查 Referer，回的是 418

```
不带任何头              418
只带浏览器 UA            418
带 Referer: https://m.douban.com/     200
```

豆瓣图床只认 Referer，UA 无所谓。`ImageLoader.fetch` 原来只发 `User-Agent` 和
`Cookie`（那两条是为 TMDB 与内网服务端准备的），于是每张豆瓣图都被 418 挡回来。
坑在状态码不是 403：`getInputStream()` 对非 2xx 一律抛 `IOException`，日志里只留一句
「取图失败 … 418」，看着像图没了，不像被防盗链。也正因为抛在写盘之前，失败不落缓存，
升级后同一批 URL 会直接取成功，已绑定的条目不用重绑。

改法只在 `ImageLoader` 里加一个 `refererFor(url)`：主机名含 `doubanio.com` /
`douban.com` 才发 `Referer: https://movie.douban.com/`，TMDB、本机服务端、媒体源地址
照旧不发，免得给反代添变量。

## 4. 顺带修的两处清洗

真实页面上暴露出来的，都是这一轮豆瓣改版后「没有 JSON-LD 就露馅」的那类：

- 片名退到了 `<title>`，于是绑回来叫「未麻的部屋 - 电影」。新增 `sub-title` 那条正则
  （豆瓣自己标的片名，不带栏目名），排在 `og:title` 之前；再补一条 `PAGE_SUFFIX`，
  把「- 电影 / - 电视剧 / - 综艺 …」这类尾巴剁掉。
- 原名是「Perfect Blue（1997）」，`YEAR_SUFFIX` 原来只认半角括号，全角的（）没剥。
  字符类补上，原名与片名各过一遍。

## 5. 自检为什么以前没发现

`build/harness/douban.html` 那份夹具是手写的，写的是「我以为豆瓣页面长这样」：里面有
JSON-LD、有 `rel="v:image"`。真页面上这些都没有，海报全靠 `sub-cover` 兜着。
现在加了一份真实抓的 `build/harness/douban-mobile.html`（裁到剧情简介一节），
`DoubanParseCheck` 对它验片名 / 年份 / 评分 / 原名 / 海报地址 / 摘要 / 类型 / 国家九项。
豆瓣再改版，这九项会先叫。取图那一步是 HTTP 行为，脚本里 `android.util.LruCache` 是
`Stub!`，起不了 `ImageLoader`，改完用 curl 按新请求头复验：`200 image/jpeg 34412 字节
JPEG 270x380`。

三项自检全绿，产物 `dist/OmniPlay-TV-v1.39-arm64.apk`（versionCode 50）。

## 6. 记一笔遗留

现版移动版页面根本没有导演 / 主演区块（页面上只有影评文案里的「导演」二字），
`infoNames` 拿不到东西，绑定后详情页的导演 / 主演仍然要靠 TMDB 或自定义编辑补。
要拿这两个字段得去抓桌面版 `movie.douban.com/subject/NN/`（那边有
`<span class="pl">导演:</span>` 结构），那是另一件事。
