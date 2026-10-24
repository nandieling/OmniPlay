# 1.38 不填 TMDB API Key 也要能用手动匹配

## 1. 现象与根因

详情页点「手动匹配」，没填 TMDB Key 的机器上只弹一条 toast（「先在设置里填写 TMDB API Key
或反代地址，才能手动搜索。」），弹窗根本不出现。原因在 `openManualMatchOverlay` 开头那三行：

```java
if (!api.engine().tmdbConfigured()) {
    showToast("先在设置里填写 TMDB API Key 或反代地址，才能手动搜索。");
    return;
}
```

这个门槛把整个弹层 gate 掉了，可弹层里那条豆瓣绑定路（贴链接 → `bindDouban`）压根不需要
TMDB。TMDB 只是候选来源之一，不该是弹层的准入条件。

## 2. 弹层：门槛去掉，候选拆两栏

门槛删掉，弹层无条件打开。候选区从一个 `manualMatchResults` 拆成两栏，各自带一行说明：

- `manualMatchTmdbCaption` + `manualMatchTmdbRows`
- `manualMatchDoubanCaption` + `manualMatchDoubanRows`

`runManualMatchSearch` 里两路各自 `runAsync`，互不等待、互不覆盖：

- TMDB 配置了才发，回来按匹配分摆行；没配置那一栏写「没填 API Key，跳过。直接从下面的豆瓣
  候选里选。」，不发请求也不报错。
- 豆瓣每次都发，候选行点一下就把链接回填进输入框再走 `bindDoubanFromManualMatch`，
  与手贴链接完全同一条路（同一套抓取、锁定、刷新详情页与首页）。

每回来一路就 `redeclare(FocusGrid.rowsFrom(...))` 重排一次遥控器走焦，不等另一路——
两路延迟差十几秒是常事，先回来那一栏得马上能被遥控器选中。原来那个 `manualMatchRunning`
布尔没了：两路并发时它只会互相踩。

## 3. 豆瓣关键词搜索

`DoubanClient` 原本只能按条目号抓详情页，手动匹配要求用户先知道条目号，太别扭。新增
`search(keyword, limit)`：

- 抓 `https://search.douban.com/movie/subject_search?search_text=…&cat=1002`。
- 这个页面要先有一副匿名 cookie 才吐数据：没有 cookie 时 `items` 是空数组，`count` 却报
  15，看着像「搜不到」。所以先用桌面 UA 请求一次 `https://www.douban.com/`，把
  `Set-Cookie` 收下来（`ll`、`bid` 这几个）存着复用，再带去搜索页。移动版 UA 直接给空列表。
- 结果在页面里那段 `window.__DATA__`，用大括号配对抠出来（跳过字符串内的括号与转义引号），
  再取 `items[]` 的 `id` / `title` / `cover_url` / `url` / `abstract` / `labels[].text`
  （`剧集` → `tv`）/ `rating.value` / `rating.count`。片名尾部那个 `(2026)` 用现成的
  `YEAR_SUFFIX` 抹掉，年份单独存一份；豆瓣塞在标题里的 `U+200E` 方向控制符换成普通空格。
- 搜索页被风控挡下只报一次错，不写 12 小时冷却：搜不到条目跟抓不到详情是两码事，用户换
  一条链接还能绑。抓取之间那道 12 秒节流（`waitForTurn()`，从 `fetch()` 里抽出来的）两路共用。

顺带把 `fetch()` 与 `search()` 的 HTTP 合成一个 `request(url, referer, agent, cookie,
checkGuards)`，UA / Referer / Cookie / 403 / 验证页判断只此一份。

## 4. 为什么要自己写那一段 JSON 读取器

搜索页解析本来直接用 `org.json`，代码写完跑 `zsh build/tvtools/harness.sh` 却是 0 条结果：
`android.jar` 里那份 `JSONObject` 是 `throw new RuntimeException("Stub!")` 的空壳。条目页
那一路侥幸没暴露（`findMovieJsonLd` 把异常吞了，退回正则路径），搜索页一一条都不剩。

所以搜索这条路自己读（`DoubanClient.MiniJson`，只返回 Map / List / String / Number /
Boolean，整数字面量给 `Long`，不然条目号会变成 `3.6689816E7`），条目页那一路继续用系统的
`org.json`。代价是多六十行代码，换来的是页面结构一漂移，离线自检能当场叫出来。

## 5. 自检与验证

`build/harness/douban-search.html` 是从 `subject_search?search_text=流人` 真实抓下来的页面，
裁到只剩那段 `window.__DATA__`。`DoubanParseCheck` 新增 15 项：片名 / 年份 / `tv` 判定 /
条目号 / 链接 / 评分 / 评分人数 / 海报可直连 / 摘要带国家，外加空 `items`、页面里没有数据块、
字符串里的 `}` 与转义引号不影响大括号配对、条目号不被写成科学计数法。三项自检
（名称年份、豆瓣、字幕转档）全绿。

`compile.sh` / `release.sh` 通过，产物 `dist/OmniPlay-TV-v1.38-arm64.apk`
（versionCode 49）。真机待验：不填 Key 打开弹层能否直接点豆瓣候选完成绑定；被风控时那一栏
是否只写一句话而不影响 TMDB 那栏。
