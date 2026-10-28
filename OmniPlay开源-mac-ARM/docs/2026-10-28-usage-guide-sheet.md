# 「使用教程」弹窗

日期：2026-10-28
影响文件：`OmniPlay/UsageGuideView.swift`、`OmniPlay/SettingsView.swift`、`OmniPlay/PosterWallView.swift`

## 为什么做

设置页里「TMDB 代理网关地址」只有一行输入框和一句说明，看不出网关要长什么样：
路径前缀谁补、图片走不走同一条域名、Key 是不是被网关顶替，全靠猜。
而国内直连 `api.themoviedb.org` / `image.tmdb.org` 不通时，症状恰恰是「海报墙一片灰、刮削一直转」，
用户最容易在这一步卡住。于是把整份教程搬进应用，并在网关一节给出一套零成本的推荐搭法。

## 入口与呈现

- 入口在偏好设置顶部：「偏好设置」标题的正下方一颗描边小按钮「使用教程」（`book.closed`），
  左缘与下方各分组对齐，右上角留给关闭按钮，两者错开一行不再挤在一起。
  无障碍标识 `settings.usageGuide`，悬停提示「查看使用教程与 TMDB 反代网关配置」。
  教程讲的就是网关这类设置项，入口跟着设置走比放在首页工具栏更顺手。
- 以 `.sheet` 从 `SettingsView` 呈现，盖在偏好设置那张 sheet 之上；这与既有的
  「开源组件详情」（`isShowingOpenSourceInfo`）是同一条路径，设置弹窗里叠 sheet 已验证可行。
- 教程固定 760 × 720，内部一条 `ScrollView`，右上角 `usageGuide.close` 关闭并回到偏好设置。
- 配色全部取调用方传入的 `AppTheme`，液态玻璃主题下底色跟着主题走。
- 首页工具栏那颗 `book.closed` 按钮已移除，按钮簇回到「加号 / 扫描 / iCloud / 齿轮」四颗。

内容顺序按真实使用路径排：三分钟跑起来 → 播放与关闭 → TMDB 反代网关（重点）→
Worker 脚本 → 配好了怎么自查 → 常见问题 → 还有这些。

## 网关一节断言的行为

教程里的说法逐条对着 `TMDBService` 核过，不是想当然：

| 教程说法 | 代码位置 |
| --- | --- |
| 只填域名，末尾 `/3` 由应用补 | `TMDBService.proxyAPIBaseURL`：空路径补 `/3`，已有前缀则追加 `/3` |
| 支持带路径前缀的网关 | 同上，`components.percentEncodedPath` 保留原前缀 |
| 图片走同域名的 `/t/p/…` | `proxyImageBaseURL` 去掉补上的 `/3`，`proxiedImageURL` 原样拼回 `/t/p/{尺寸}{路径}` |
| 网关只反代了 API 时海报不消失 | `imageURLAttempts` 先试网关、失败回落官方 `image.tmdb.org` |
| v3 `api_key` 与 v4 `Authorization: Bearer` 都要透传 | `makeRequest` / `authenticatedURL` |
| 点「验证」即探测 `/3/configuration` | `probeConnection` |

## 推荐方案

免费域名（DNSHE 的 `de5.net`）→ Cloudflare DNS（改 NS，退路是在 DNSHE 加 CNAME 指向 Worker 域名）→
一个 Worker 按路径分流反代 API 与图片服务器 → Worker 自定义域名绑定（Cloudflare 自动签证书）。
弹窗里的脚本与仓库根 README「TMDB 与反向代理网关」一节保持同一份，改一处要同步另一处。
脚本按路径分流而不是按子域，海报响应带 `cache-control: public, max-age=86400`，
让 Cloudflare CDN 承担重复的海报请求。

## 验证

- `xcodebuild`（Debug / arm64，`build/ci/DerivedData`）BUILD SUCCEEDED。
- 起应用读无障碍树：首页按钮簇只剩 `toolbar.addSource`、`toolbar.sync`、`icloud`、
  `toolbar.settings`，教程按钮不再出现在首页。
- 打开偏好设置：标题区上方是「偏好设置」标题与右上角关闭按钮（`settings.close`），
  下一行左缘是「使用教程」按钮（`settings.usageGuide`）；点开后教程 sheet 叠在偏好设置之上，七节内容全部渲染，
  脚本代码块可选中、「复制脚本」写粘贴板，长段落换行正常，无溢出与相互遮挡。
- 关教程回到偏好设置，网关输入框与「验证」按钮照常可用，两张 sheet 不互锁。

## 已知边界

- 教程文案目前只有简体中文，`Localizable.xcstrings` 未收录这些键；切到英文界面时弹窗仍是中文。
- 教程 sheet 比偏好设置宽 140 pt，叠上去时窗口宽度以教程为准，关掉后回到原尺寸。
