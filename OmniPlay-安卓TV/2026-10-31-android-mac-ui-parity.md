# 2026-10-31 安卓 UI 对齐 mac 版 + 功能补齐（1.51 / versionCode 62）

两件事：把「安卓版没 mac 版好看」这件事拆成能落地的做法并做掉第一批；把 mac 版有而安卓没有的功能补一批。

## 一、怎么把 mac 版 UI 搬过来（方案）

mac 版是 SwiftUI（`OmniPlay开源-mac-ARM/OmniPlay/*.swift`），安卓这边是纯代码搭 View（无 XML、无 Compose）。
**不建议换 Compose**：播放器、焦点网格、字幕覆盖层、扫描队列全建立在命令式 View 与 `FocusGrid` 的
nextFocus 链上，换成声明式等于把遥控器导航重写一遍，收益只是写法。要搬的是「样子」，不是「写法」，
所以按三层搬：

| mac 版（SwiftUI） | 安卓对应 | 搬什么 |
| --- | --- | --- |
| `ThemeManager.swift`（`AppTheme` / `ThemeType` / `ThemeStyle`） | `ui/Theme.java` | 色板值直接抄 mac 的 hex；派生色（边框、淡底、聚焦底）用 `blend()` 从主色推，不允许再出现写死的颜色 |
| `MovieCardView`（160x240、radius 12、评分角标、标题 2 行、年份 + 已看圈、4pt 进度条） | `PhoneMainActivity.posterCard` / TV 端 `posterCard` | 尺寸比例、圆角、角标位置与配色（TMDB `#FFD700` 系、豆瓣 `#00B51D`）、文案顺序 |
| `PosterWallView`（工具栏 `systemName`、继续观看、左上角刮削进度） | `MainActivity` 首页 + `ui/Icons` + `ui/FocusGrid` | 符号图标用 Canvas 在 24 单位网格上照 SF Symbols 重画；进度措辞对齐 |
| `MovieDetailView`（backdrop + 渐变 + 片名压在图上、meta chip、评分 pill） | `PhoneMainActivity.renderDetail` / TV 端 `renderDetail` | hero 结构、chip/pill 形态、分集行 |
| `SettingsView` | `PhoneSettingsSheet` / TV 设置弹层 | 分区标题 + 一行一项的排法 |
| `UsageGuideView` / `OpenSourceInfoView` | `ui/GuideContent`（新增） | 正文一份，手机与电视各自渲染 |

要点三条：

1. **令牌化**：颜色、圆角、间距、字号一律从 `Theme` / `Widgets` 取（`sdp`/`ssp` 还负责 4K 面板的整体放大）。
   屏幕代码里出现 `Color.parseColor("#...")` 就算漏了。
2. **组件工厂**：`Widgets`（TV）与 `PhoneUi`（手机）是仅有的两个「造控件」的地方，重复的卡片、胶囊、
   进度条只允许存在一份。这次新加的 `ratingBadge` / `progressCapsule` / `metaChip` / `personRow` 都放在
   使用它们的文件里，下一步该沉到 `Widgets`/`PhoneUi` 里再沉。
3. **焦点语言只在 TV 侧**：mac 的 hover 在电视上就是「轻微放大 + 抬起」，底色不压深、不加粗边框、不画
   对号（`Theme.FOCUS_DARKEN = 0`），这条已经全站统一；手机版则是整卡可点、无焦点态。

## 二、这次做完的（1.51）

**手机版（`PhoneMainActivity`）**

- 海报卡重画：严格 2:3、12dp 圆角；`voteAverage` / `doubanRating` 有值就在海报左下角压出金、绿两枚角标；
  标题两行居中，下一行「年份 + 已看/未看」图标（mac 版同一行）；进度 0~95% 的海报底部压一根 4dp 进度条。
- 首页加「继续观看」横向抽屉（最多 12 部），下面才排「全部 · N 部」的两列网格。
- 详情页 hero：海报铺满屏宽，底部压 `BOTTOM_TOP` 黑色渐变，片名与两枚评分角标写在海报上；
  年份 / 类型改成能横滑的小胶囊。
- 导演 / 主演一行可点，弹出这个人在本库里的其它作品（带小海报），点一条直接进详情。
- 设置里新增「使用指南」「关于与开源」两行入口。

**电视端（`MainActivity`）**

- 设置「关于与更新」里新增「使用指南」「关于与开源」两行，打开是一层可滚动面板（`Overlays`），
  遥控器焦点默认落在「关闭」。
- 详情页导演 / 主演改成可聚焦可点的一行（轻微放大 + 抬起，与全站焦点语言一致），点开人物作品面板，
  焦点落在「关闭」上，方向键能走到、返回键能退出。

**共用**：`ui/GuideContent.java` 收「使用指南」六节（添加源、扫描刮削、播放模式与字幕、遥控器与触屏、
手机输入、主题）与「关于 / 开源」（元数据来源、libmpv / jcifs-ng / ZXing / SQLite 及许可证、字幕转档配套）。

## 三、还差的 mac 版功能（下一步的排法）

按「价值 / 风险」排，P0 先做：

- **P0 刮削失败账本**（mac `ScrapeFailureTracker`）：海报、分集剧照、未匹配条目的抓取失败现在每轮扫描都从零再试，
  整库缺图时会一直空转。安卓侧对应位置在 `LocalEngine` 的刮削循环与 `SubtitleSidecarService`；做法是按目标记
  失败次数，自动连续失败满 3 次不再自动重试，用户手动点「扫描刮削」整本清零。纯逻辑，能在离线自检里加断言。
- **P1 想看 / 标记**（mac `MediaMarkStore`）：`mediaMark` 一张本地表（想看 / 已看 / 自定义评分 / 短评），
  首页多一个「想看」分区，详情页给一颗圈。要动 `LocalMediaLibrary` 的建表与迁移（库版本 +1）。
- **P1 人物页**（mac `PersonProfileStore`）：现在只有「本库同名作品」，缺 TMDB `person` 详情、头像与完整作品表；
  需要 `TmdbClient` 加 `/person/{id}` 与图片代理走同一条反代，外加头像缓存。
- **P1 分集剧照自定义**（mac `ThumbnailManager` / `EpisodeThumbnailEditModalView`）：让用户从本集多张剧照或本地
  文件里挑一张当封面。
- **P2 短剧识别**（mac `ShortDramaSupport`）：一个目录几十上百个单集两分钟的竖屏片，现在会被当成上百部独立电影，
  配额按集数烧。补目录级识别 + 纯数字集号解析 + 一张短剧旁表。
- **P2 NeoDB 同步 / 红果短剧源**（mac `NeodbService` / `HongguoService`）：都要外部账号与第三方接口，独立开关、独立灰度。
- **明确不做**：TV 端的离线缓存（用户点名去掉），mac 的 `OfflineCacheManager` 只留在手机端考虑。

## 四、验证

- `python3 build/patches/mkmkv.py` 之后 `zsh build/tvtools/harness.sh`：编译全部 Java（含新改动），
  离线自检 **字幕转档 91 项、手机字幕 13 项**全过，名称/年份与豆瓣解析两套 `ALL PASS`。
- 真机建议看这四处：手机首页的评分角标与续播进度条；手机详情页 hero 的片名可读性（深浅色各看一次）；
  电视详情页导演/主演那一行遥控器能不能走到、点开后返回是否落回原位；电视设置里「使用指南」面板能不能一路滚到底。
- 字幕转档容器（`../OmniPlay-字幕转档`）本次同步升到 1.1：文件夹视图 + 蓝光盘整盘抽字幕，`python3 selftest.py`
  **0 项没通过**，细节见那边的 `2026-10-31-文件夹视图与蓝光盘.md`。
