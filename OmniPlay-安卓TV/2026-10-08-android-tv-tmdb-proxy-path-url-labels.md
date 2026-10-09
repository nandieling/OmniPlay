# 安卓 TV 1.16：TMDB 反代路径、凭证跟发、WebDAV 目录名还原

报上来的三件事看着不相干，其实是两类：TMDB 检测误报（外加一个输入框图标），以及 URL 编码直接拿去显示。

## TMDB：反代填对了却「无法访问」

用户填的是 `https://tmdb.nandielinghai.de5.net/tmdb0512` 加一把 API Key，点「验证连接」得到「TMDB 无法访问，请检查 API key 或反代地址」，并要求两种 TMDB API 都要支持。这句话是兜底文案，真实原因有三层，叠在一起才让任何一把钥匙都验不过。

第一层，检测请求本身就是个坏 URL。

```java
// 旧：TmdbClient.checkConnection()
return get("/configuration").contains("images");
```

直连模式下 `get()` 会给相对路径补上官方域名，看着没问题；一旦填了反代，`apiBase()` 返回的是空串（当时的写法是 `isProxy() ? "" : ...`），`new URL("/configuration")` 抛 MalformedURLException，异常一路被 catch 吞掉，返回 false，文案就成了「无法访问」。也就是说这个功能从写下那天起，在反代模式下从来没真正发过请求。

第二层，反代地址少了版本段。直接对网关验两种写法：

```sh
curl -i https://tmdb.nandielinghai.de5.net/tmdb0512/configuration     # 404 openresty
curl -i https://tmdb.nandielinghai.de5.net/tmdb0512/3/configuration   # 401 {"status_code":7,...}
```

404 是网关自己回的，说明 `/tmdb0512/configuration` 根本没匹配上转发规则；补上 `/3` 之后才是 TMDB 在说话（`Invalid API key: You must be granted a valid key.`）。mac 版一直是把反代补成 `<base>/3` 再拼接口路径的，TV 版漏了这一步。现在 `TmdbClient.normalizeProxyBase()` 做同样的事：补 scheme、去尾斜杠、路径末尾不是 `/3` 就追加，已经带 `/3` 的不重复补（否则会变成 `/3/3`）。`apiBase()` 改成直接返回它，六处拼 URL 的地方不再各自判断。

第三层，走反代时凭证整个不发。原先六处都是同一种写法：

```java
url + (isProxy() ? "" : "&api_key=" + effectiveApiKey())
```

等于假定「反代会替我加钥匙」。这台网关只做转发，请求到 TMDB 时是裸的。改成不管直连还是反代都带凭证：

```java
public static boolean usesBearerToken(String key) { return key != null && key.trim().length() > 50; }
```

短于 50 位当 v3 API Key，拼 `api_key=`（新写了一个 `apiKeyQuery(boolean first)`，`?` / `&` 分得清，`/configuration` 这种没有 query 的 URL 之前会被拼成 `&api_key=`）；超过 50 位当 v4 Read Access Token，`getOrNull` 里改发 `Authorization: Bearer <token>`。这样两种 Key 都是粘进去就能用，跟 mac 版一致。

检测返回也拆开了。`checkConnectionDetailed()` 返回 `CheckResult{connected, message}`，按 空 Key / 2xx / 401、403 / 404 / 连不上 / 其它状态码 分别给一句能照着做的说明，设置面板直接显示这句话：

```
TMDB 拒绝了这把钥匙（HTTP 401）。反代只负责转发，API Key 仍要自己填；v4 Token 会以 Bearer 头发出。
```

模拟器上是拿一把故意写错的 32 位 Key 验的，所以停在 401；换成真 Key 就通过了。404 会提示「反代地址可能对不上——需要能转发 TMDB 的 /3 接口」，连接失败会提示检查地址与网络，不再是一句话兜底。

## 输入框：去掉图标、文字左对齐

`inputWithLeadingIcon()` 只有反代那一处再用，删掉函数和调用，反代框换成普通 `input()`。`Widgets.input()` 补上 `setGravity(Gravity.LEFT | Gravity.CENTER_VERTICAL)`——之前没写 gravity，继承来的默认值让长 URL 看起来两头都被切了。TMDB 两个字段沿用 1.15 的 `labelledRow()`：标签在上、输入框在下。

## WebDAV 目录名是一串符号

登录框、已保存连接、局域网设备行里的挂载目录显示成 `%E4%B8%AD%E6%96%87` 这类字符串。原因是 URI 的百分号编码没还原：PROPFIND 返回的 `<href>` 本来就是编码形式，保存连接时又把用户输入的路径原样存下。

新增 `local/UrlLabels.displayPath(String)`：只在 `?` 之前解码，按字节攒 UTF-8（一个汉字的 `%XX%XX%XX` 要三字节一起解），`+` 保持字面量（路径里它不是空格），非法的 `%zz` 原样保留，整串没有 `%` 就原样返回。列表项 `WebDavClient.parseMultiStatus` 也走它，`SavedConnections.Entry.detail()`、`MainActivity.displaySourcePath()`、登录框回填、局域网设备行同样走它。

之所以单独开一个类而不是复用 `WebDavClient` 里的静态方法：`SavedConnections` 被 `run-saved.sh` / `run-smb.sh` 编译，那两份源码清单里没有 `WebDavClient`，也就没有 kxml，引用过去就编不过。纯函数类没有这个负担，还能单测。

## 验证

`/tmp/pitest/run-tmdb.sh`（`TMDB_OK`）起一个本地假 TMDB：v3 Key 用例断言收到的请求行是 `GET /tmdb0512/3/configuration?api_key=…`，v4 用例断言带 `Authorization: Bearer …` 且不含 `api_key`，再分别喂 404 / 401 / 空 Key，核对三种文案。`run-webdav.sh` 加了四条 `displayPath` 断言（中文、带 query、`+`、非法 `%zz`）。八个用例全绿。

真机侧：设置面板填这台反代 + 假 Key，状态行是上面那句 401；截图确认反代框无图标、文字靠左、标签在输入框上方。第 3 条只有单测证据——模拟器上没挂带中文目录名的 WebDAV，界面上暂时演示不出来。
