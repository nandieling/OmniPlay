# 液态玻璃主题

日期：2026-10-21　范围：mac（ARM 基准 + x64 镜像）　关联：[2026-10-19-neodb-sync.md](2026-10-19-neodb-sync.md)

## 1. 设计取舍

主题系统原本的结构是 `ThemeType`（存 UserDefaults `appTheme`）→ `colors: AppTheme`（六个语义色），每个视图各自 `@AppStorage` 取值。加第 6 套主题有两种做法：把玻璃当成一组新颜色到处塞 if，或者把「玻璃」做成主题的一项能力。选了后者：

- `AppTheme` 新增两个带默认值的可选字段：`glass: AppGlassStyle?`（有值即为玻璃主题）与 `backdrop: [Color]`（页面渐变底，玻璃得有东西可折射）。5 套旧主题构造处不传参，行为完全不变。
- 语义化取色改成计算属性：`subtleFill`（进度条底槽）、`chipFill`（胶囊）、`placeholderFill`（缩略图占位）、`rowFill(selected:)`（列表行）、`heroDimming`（详情页海报压暗量）。视图里只写语义，不再判断主题名。
- 统一落点 `ThemeSurfaceModifier`，三种模式：`card`（卡片面板）、`vibrant`（半透明控件，非玻璃主题沿用 `ultraThinMaterial`）、`glassOnly`（自带背景的顶栏只在玻璃主题下补玻璃）。`tint` 参数供选中态染色：玻璃主题染玻璃，其他主题直接当填充色，这样替换掉原来的 `Capsule().fill(...)` 也不会改变浅色观感。
- 浅色玻璃要靠「底够花、片够透」才看得出来：`AppGlassStyle` 因此拆出 `edge`（轮廓）与 `shadow`（投影），浅色底上只给一道白色高光会整片糊掉；渐变底也在左上强调色柔光之外再压一道右下冷灰，留住明暗。
- 玻璃本体：macOS 26 起用系统 `glassEffect(_:, in:)`（折射、高光、随背景的景深都交给系统），14 / 15 退回 `regularMaterial` + 染色层。部署目标仍是 14.0，所以走 `if #available`。
- 边缘高光线用 `shape.stroke(_:lineWidth:).clipShape(shape)`：`AnyShape` 只符合 `Shape`，没有 `strokeBorder`。
- 主题自带明暗基调，补 `preferredColorScheme(theme.preferredScheme)`（玻璃主题返回 `.light`，其余 5 套仍跟随系统），否则系统切成深色时，滚动条、菜单、开关、弹层会跟浅色页面打架。

## 2. 改动

| 文件 | 改动 |
| --- | --- |
| `OmniPlay/ThemeManager.swift` | 新增 `AppGlassStyle`（染色 / 顶部高光 / 描边 / 轮廓 / 投影 / 光斑）、`AppGlassGlow`、`AppTheme` 的 `glass` / `backdrop` 与五个语义化取值加 `windowTint`、`liquidGlass` 主题（浅色底 + `0678B4` 强调色 + 三段浅色渐变底 + 五团光斑）、`backdropLayer`（`EllipticalGradient` 光斑场 + 顶部白色反光）、`ThemeSurfaceModifier` / `GlassPlateModifier`、`themeSurface` / `themeVibrant` / `themeGlassPlate` / `themeTopBarCluster` / `themeGlassPill` / `themeGlassMat` 六个修饰符、`ThemeWindowStyler` |
| `OmniPlay/PosterWallView.swift` | 两处页面底换成 `theme.backdropLayer`；右上角按钮簇改走 `themeTopBarCluster`（玻璃主题下是贴着按钮的圆角玻璃胶囊），标题行套 `themeGlassPill` 玻璃胶囊；缓存状态胶囊、局域网设备行、WebDAV 面板与浏览行改走 `themeVibrant`；首页分类筛选胶囊由 `Capsule().fill` 改为 `themeVibrant(capsule:true, tint:)`，玻璃主题下是一块真玻璃片；补 `preferredColorScheme` |
| `OmniPlay/MovieDetailView.swift` | 海报冲淡量改用 `heroDimming`（玻璃主题 0.55，比纯色主题少一点，让海报透上来）；文件行 `rowFill` + `themeGlassPlate`；已看胶囊、进度底槽、分集缩略图占位改用新的语义取值 |
| `OmniPlay/NeodbMarkEditorView.swift` | 弹窗卡片与标记按钮改走 `themeSurface`，sheet 底色用 `backdropLayer` |
| `OmniPlay/MovieCardView.swift` | 继续播放进度底槽用 `subtleFill`；海报 ZStack 加 `themeGlassMat`，玻璃主题下海报外围垫一圈不占布局的玻璃垫 |
| `OmniPlay/ContentView.swift` | 挂 `ThemeWindowStyler`，按主题的 `windowTint` 染标题栏、去掉标题栏分隔线 |
| `OmniPlay/SettingsView.swift` | 面板补 `backdropLayer` 与 `preferredColorScheme`；主题选择器下加一行说明，讲清 macOS 26 真折射、更早系统退回毛玻璃 |
| `OmniPlayTests/ThemeManagerTests.swift` | 新增 5 项：每套主题都能解析、rawValue 可往返、只有液态玻璃带 `glass`、玻璃与纯色主题的半透明取值确实不同、玻璃主题的底色与文字确实按浅色配比（按 sRGB 相对亮度判定） |

## 3. 验证

- 单测：`xcodebuild test -only-testing:OmniPlayTests -destination 'platform=macOS'` 全绿 72 项、0 失败（含主题 5 项）。
- 两平台编译：ARM（`arch=arm64`）与 x64（`arch=x86_64`）`xcodebuild build` 均通过，8 个改动/新增文件两侧 `cmp` 一致。
- 真机截图（本机 macOS 27.0.1，走的是系统 `glassEffect` 分支）：首页是彩色光斑底 + 内缩玻璃顶栏 + 玻璃页眉胶囊 + 海报玻璃砖，标题栏与页面顶色连成一片；详情页暖色海报底上 NeoDB 玻璃胶囊折射明显、深色标题清晰；设置面板（偏好设置 sheet）整体浅色，标题栏是半透明玻璃带，开关、按钮、链接文字跟随浅色控件，蓝底按钮对比足够。
- 回归：把 `appTheme` 换回 `crystal` 再截图，首页仍是纯白底黑字、顶栏无玻璃带、筛选胶囊观感不变。
- 排查记录：验证过程中 `defaults write nan.OmniPlay appTheme` 有几次写进了 `~/Library/Containers/nan.OmniPlay/Data/Library/Preferences`（历史沙盒版留下的容器目录），而应用读的是 `~/Library/Preferences/nan.OmniPlay.plist`，一度误判成「主题没生效」。改主题验证时以真实 plist 为准。
- 未覆盖：macOS 14 / 15 的 `regularMaterial` 兜底分支只做了代码复核，本机是 27 跑不到；播放器覆盖层（控制条、字幕）不参与主题，玻璃主题下仍是播放器自身的半透明黑底；分类筛选胶囊与 WebDAV 面板只做了配色推算，未逐个截图。

## 4. 同日改判：深色 → 浅色

这版先按「深色底 + `63D2FF` 冷光」落地，当天按要求改成浅色，改动只落在 `ThemeManager.swift` 与设置页那句说明里，四个视图的落点代码一行没动：

| 项 | 深色版 | 浅色版 |
| --- | --- | --- |
| 强调色 | `63D2FF`（白底对比 1.9:1，读不动） | `0678B4`（白底 4.9:1，能当文字色） |
| 底色 / 卡片 | `0D1117` / `1B222C` | `EFF4FA` / `FFFFFF` |
| 文字 | `F3F7FB` / `9EB0C4` | `0E1726` / `5C6F88` |
| 渐变底 | 三段近黑 | `F7FAFD` → `DCE8F5` → `EFF3F9`，柔光提到 0.22，另压一道右下冷灰 |
| 垫层 | `Color.white` 半透明 | `1B2B44` 半透明（0.07 / 0.055） |
| `preferredScheme` | `.dark` | `.light` |
| 玻璃片描边与投影 | 单层白色高光 + 黑 0.28 投影 | `AppGlassStyle` 新增 `edge` / `shadow`：高光 0.78 白、轮廓 0.07、投影 0.12 |
| `heroDimming` | 0.34 | 0.55 |

改完重跑：主题单测 5 项与全量 72 项单测全绿（0 失败）；ARM 与 x64 `xcodebuild build` 均通过，7 个改动文件两侧 `cmp` 一致；按 `appTheme=liquidGlass` 重新启动应用，首页与设置面板截图确认。


## 5. 第二轮：把玻璃做显眼

第一轮那版太素：底是近乎纯白的浅灰蓝，玻璃片只有一层白染色加一条淡高光，扫一眼跟普通浅色主题没差别。这轮按「玻璃必须折射出颜色才叫玻璃」重做，四个动作：

- 页面底改成彩色光斑场。新增 `AppGlassGlow`（颜色 / 中心 / 比例半径 / 强度），`backdropLayer` 用 `EllipticalGradient` 画：一团强调色铺底 + 左上青蓝、右上靛蓝、左下暖橙、右下薄荷四团，中间那层保证大窗口正中不会是一片死白。半径用容器比例而不是固定点值，窗口拉大光斑跟着铺开。
- 玻璃片配方补齐：系统 `glassEffect` 之上叠一层顶部镜面高光（`AppGlassStyle.sheen`，白色 0.75 渐隐到 34% 高度），描边给两道（白色高光 + 冷调轮廓），投影加深到 0.15 / 半径 14 / y 7。染色从 0.34 提到 0.50，偏白的区域也读得出这是一块玻璃。
- 顶栏从贴边的整宽背板改成圆角玻璃条（`themeTopBarPlate` + `topBarContentInset` 配对），图标的绝对位置跟其他主题分毫不差。这层玻璃在下一节又被收窄了一次。
- 玻璃铺到内容上：海报卡垫玻璃砖（`MovieCardView` 一行调用），`themeGlassPill` 把首页标题行做成一颗悬浮玻璃胶囊；两者都只在玻璃主题下生效，其余 5 套主题的布局与观感一个字都不变。垫层的画法在下一节改过。
- 标题栏收编：新增 `ThemeWindowStyler`（`NSViewRepresentable`），玻璃主题下把标题栏染成页面顶色并去掉分隔线，否则内容区上面永远压着一条系统白带，玻璃条看着像贴了块膏药；`ContentView` 一行 `.background()` 挂上，非玻璃主题交还系统默认。

重做后单测仍 72 项全绿（`glows.count >= 3` 是这轮新加的断言），两平台编译通过、8 个改动文件两侧 `cmp` 一致；真机截图：首页是彩色光斑底 + 玻璃顶栏 + 玻璃页眉胶囊 + 海报玻璃垫（顶栏与海报的覆盖范围在下一节收窄），详情页暖色海报底上 NeoDB 玻璃胶囊折射明显、深色标题清晰。macOS 14 / 15 的 `regularMaterial` 分支同样吃这套染色与高光，只是没有系统折射。


## 6. 第三轮：点击回归与玻璃收口

玻璃铺开后立刻暴露一个真 bug：首页右上角四个按钮和「所有影视」右边的搜索 / 排序按钮全部点不动。原因是第 5 节给玻璃片叠的装饰层里，`shape.fill(glass.sheen)` 是一整块填充的 overlay，overlay 默认参与命中测试，等于在按钮上面盖了张透明塑料膜（两道 stroke 只挡描边一圈，所以第一轮没发现）。改法是把高光与两道描边合并进同一个 overlay 并整体 `.allowsHitTesting(false)`，装饰归装饰，点击全部放行。

同时按反馈把两处玻璃收口，不再靠面积撑场面：

- 顶栏不再是横贯整宽的玻璃带。`themeTopBarPlate` 换成 `themeTopBarCluster`：右上角四个按钮单独包一个 `HStack(spacing: 16)`，玻璃只贴着按钮外沿留一圈 10pt；`AppTheme.topBarContentInset` 随之删掉，外层恢复原来的 `.padding(.horizontal, 24)`。
- 海报玻璃只贴海报。`themeGlassCard` 换成 `themeGlassMat`：玻璃垫画在一个负 padding 的透明矩形 `background` 上，等于把海报向外扩出一圈 8pt 而不占布局，片名与年份的排布、卡片固定高度全部不变；它挂在海报 ZStack 上而不是卡片根视图。

验证：点齿轮确认「偏好设置」sheet 弹出、点页眉搜索图标确认搜索框出现（都是真机点击后截图确认，不是推断）；单测 72 项全绿；两平台编译通过，改动文件两侧 `cmp` 一致。


## 7. 第四轮：风格与配色拆开

六条反馈，前四条是页面细节，后两条动到了主题模型本身。

- 首页页眉的分类下拉只留图标。`categoryMenuButton` 的 label 去掉「全部分类 / 按 XX」那段文字（文字只在弹窗里每项前面出现），图标右边直接接 `chevron.up.chevron.down`；同时删掉图标与搜索按钮之间那条 `Spacer().frame(width: 15)`，搜索 / 排序 / 排序方向三个按钮顺势左移。
- 详情页播放按钮不再描蓝边：去掉 `Capsule().stroke(theme.accent, 1.5)`，底色改 `theme.accent.opacity(0.1)`，文字色用 `theme.textPrimary`，与右边「已播 / 未播」两颗按钮同一个字色。
- NeoDB 标记按钮从片名那行挪到「开始播放 / 已播未播」这一排的末尾，label 去掉文字两侧的图标与框线，改成跟邻居同款的 `chipFill` 胶囊；未同步的脏状态并进 `.help()` 文案。
- 「版本 / 文件」列表每行第一行改为路径里的文件名（`relativePath` 取 `lastPathComponent`，空了才退回 `fileName`），第二行就是原来的播放进度，不再重复整条路径。
- 主题拆成「风格 × 配色」两维：新增 `ThemeStyle`（`classic` / `liquidGlass`），`ThemeType` 只留 5 套配色；`AppThemeResolver` 用 `appThemeStyle` + `appTheme` 两个 key 解析出 `AppTheme`，并把老版本存在 `appTheme` 里的 `liquidGlass` 值迁移成「晶透白配色 + 液态玻璃风格」（`ContentView.init` 调一次 `migrateLegacySelection()`）。玻璃本身由 `AppTheme.applyingLiquidGlass()` 从所选配色派生：染色取强调色 + 58% 白，光斑场在强调色之外再做 HSB 色相偏移（±0.06 / ±0.10 与互补的 ±0.42 / ±0.48），渐变底取该配色底色提亮后的三段，因此 5 套配色每套都有自己的玻璃，而不是一块蓝玻璃贴到底。HSB 手算而没用 `NSColorSpace.deviceHSV`（该常量不存在）。
- 设置弹窗跟满主题：新增 `外观与主题` 一节，`主题风格` 与 `主题颜色` 两个下拉（配色项带强调色小圆点），只在液态玻璃下多一行「玻璃染色与光斑」色卡预览；7 处分组标题统一走 `settingsHeader()`，sheet 标题 / 关闭按钮 / 分隔线改用主题的 `textPrimary` / `textSecondary`；Form 尾部补 `.formStyle(.grouped)` + `.scrollContentBackground(.hidden)` + `.tint()` + `.foregroundStyle()`，让页面底从分组列表背后透出来；原先硬编码的 `.secondary`、`.red`、`.textBackgroundColor`、`.controlBackgroundColor`、`.separatorColor` 全部换成语义色，顺带给 `AppTheme` 补了 `successColor` / `warningColor` / `dangerColor` 三个状态色，验证结果、更新状态、NeoDB 同步提示都改用它。

验证：单测 74 项全绿、0 失败（主题 7 项，含「每套配色都能出玻璃且仍是浅色」「经典风格不带 `glass` / `backdrop`」「旧的 `appTheme=liquidGlass` 仍能解析」）；ARM 与 x64 `xcodebuild build` 均通过，10 个改动文件两侧 `cmp` 一致；真机截图覆盖首页、两个详情页、设置弹窗。风格切换是用 AX 在弹窗里真点「主题风格」下拉完成的（`/tmp/axpick`：`AXPress` 弹层再 `AXPress` 菜单项），切到经典后截图确认首页页眉回到素面胶囊、右上角没有玻璃片、设置弹窗回到系统分组白底；切回液态玻璃后 `defaults read nan.OmniPlay` 读到 `appTheme=crystal`、`appThemeStyle=liquidGlass`。

排查补充：`screencapture -x -R…` 在屏保 / 锁屏介入时会报 `could not create image from rect`，这时改成全屏截图再 `sips -c … --cropOffset …` 裁出窗口区域；设置弹窗开着时用 `defaults write` 改主题不会反映到已启动的进程（`UserDefaults` 缓存在进程内），要在 UI 里切或者重启应用。


## 8. 第五轮：滚动帧率与两个漏网的弹窗

### 海报墙滚动

翻下来最扎手的是滚动不顺。定位到四处开销，都改在首页与卡片里：

- 玻璃按滚动相位降级。一块 `glassEffect` 都要重新采样一次它背后的内容，满屏几十张海报同时在动时这笔开销最要命。`PosterWallView` 用 `onScrollPhaseChange`（macOS 15+，更早系统维持原行为）把「正在滚动」写进 `isWallScrolling`，经 `isPosterWallScrolling` 环境值传给卡片；滚动中 `themeGlassMat` 走 `simplified`，玻璃垫换成一层带透明度的染色，手一停立刻换回真玻璃。停在原地看时观感与第 7 节完全一致。
- 玻璃垫不再投影。`ThemeSurfaceModifier` 新增 `elevated`，海报垫这种满屏重复的元件走 `false`：一张投影就是一次离屏高斯模糊，几十张同时滚时省下来的正是那几帧。顶栏按钮簇、页眉胶囊、详情页文件行的投影照旧。海报自身那道 0.1 / 8pt 阴影保留，动它等于同时改动经典主题的观感。
- hover 抖动拦掉。滚动时指针底下的卡片一直在换人，`onHover` 来回翻转会让整片卡片重绘。滚动中直接忽略 hover，并在进入滚动时清掉已有的 hover。代价是滚动停下后要等指针动一下才出现角标按钮，换来的是过程中不再有忽隐忽现的重绘。
- 主线程上的磁盘检查搬走。`refreshAvailabilityWithoutDB()` 会经 `hasMissingSource` 去 `fileExists`，而 `isCached` 还要多探两次缓存目录，以前每次 15 秒定时器与每次卡片出现都在主线程上跑几十次 stat。`OfflineCacheManager` 因此把这件事拆成两步：`missingSourceProbes(for:)` 在主线程只查内存里的缓存集合并拼出要探测的路径（顺便把 `getLocalPlaybackURL` 里重复的候选路径算法拉成 `cacheCandidateURLs`），`Self.hasMissingSource(probes:)` 在后台跑 stat，判定顺序与原来逐字一致。顺带两处收口：15 秒定时器由 `PosterWallAvailabilityTicker` 一处提供，不再每张卡各起一条；`MovieCardView` 不再挂 `@ObservedObject cacheManager`（那等于每张卡都订阅缓存器的全部 `@Published`，下载进度一跳整屏集体重绘），改成订 `objectWillChange` 且只在缓存模式的卡片上刷新。

### 两个没跟主题的弹窗

「手动匹配」`MovieSearchModalView`、「手动编辑资料」`MovieEditModalView` 以及嵌在手动匹配里的 `DoubanBindingPanel` 三个视图此前一行主题代码都没有：标题栏写死 `NSColor.windowBackgroundColor`，强调信息用 `Color.blue`，说明文字用 `.secondary` / `.gray`，分组卡片用 `NSColor.controlBackgroundColor`。按第 7 节既有做法补齐：

- 三个视图各自挂 `AppThemeResolver` 的 `appTheme` + `appThemeStyle`，跟其他落点同源。
- 根视图统一 `.background(theme.backdropLayer)` + `.tint(theme.accent)` + `.foregroundStyle(theme.textPrimary)` + `.preferredColorScheme(theme.preferredScheme)`，玻璃主题下 sheet 底下也是同一套光斑底。
- 文字与状态色改语义取值：`textPrimary` / `textSecondary` / `accent` / `dangerColor`；编辑弹窗的分组卡片底用 `theme.surface`，描边用 `textSecondary.opacity(0.2)`。
- 搜索结果列表补 `.scrollContentBackground(.hidden)`，否则 plain 列表自带一块系统底色，把主题底挡成一块白斑。

### 验证

- 单测 74 项全绿、0 失败（`WebDAVMockIntegrationTests` 里那两项源文件存在性检查会走重构后的 `hasMissingSource`）；ARM 与 x64 `xcodebuild build` 均通过；本轮 7 个改动文件（`ThemeManager`、`MovieCardView`、`PosterWallView`、`OfflineCacheManager`、`MovieSearchModalView`、`MovieEditModalView`、`DoubanBindModalView`）两侧 `cmp` 一致。
- 真机截图这轮没做成：验证期间屏幕进入锁屏，区域截图报 `could not create image from rect`、AX 也读不到窗口。滚动观感与两个弹窗的配色要在下次解锁后确认，代码层面的依据是上面那几处开销的消除。
- 兼容性：`onScrollPhaseChange` 是 macOS 15 起的 API，14 上拿不到滚动相位，卡片会一直按「没在滚动」处理，等于维持第 7 节的画法；`scrollContentBackground(.hidden)` 自 macOS 13 起可用，不影响部署目标 14.0。
