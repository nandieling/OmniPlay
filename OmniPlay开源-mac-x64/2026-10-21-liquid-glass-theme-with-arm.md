# 液态玻璃主题（x64 镜像记录）

日期：2026-10-21　范围：mac x64　基准：[ARM 记录](../OmniPlay开源-mac-ARM/docs/2026-10-21-liquid-glass-theme.md)

与 ARM 同步新增第 6 套主题「液态玻璃 (Liquid Glass)」：浅色渐变底 + 系统玻璃层（同日由深色改判为浅色，见 ARM 记录第 4 节），首页顶栏、分类筛选胶囊、详情页文件行、NeoDB 弹窗与设置面板都改走统一的 `themeSurface` / `themeVibrant` / `themeGlassPlate` 落点；macOS 26 起用系统 `glassEffect`，更早系统退回 `regularMaterial` + 染色。5 套旧主题观感不变。

镜像文件（与 ARM `cmp` 一致）：

- `OmniPlay/ThemeManager.swift`
- `OmniPlay/PosterWallView.swift`
- `OmniPlay/MovieDetailView.swift`
- `OmniPlay/MovieCardView.swift`
- `OmniPlay/NeodbMarkEditorView.swift`
- `OmniPlay/ContentView.swift`
- `OmniPlay/SettingsView.swift`
- `OmniPlay/NeodbSettingsView.swift`
- `OmniPlay/OpenSourceInfoView.swift`
- `OmniPlay/MovieSearchModalView.swift`
- `OmniPlay/MovieEditModalView.swift`
- `OmniPlay/DoubanBindModalView.swift`
- `OmniPlay/OfflineCacheManager.swift`
- `OmniPlayTests/ThemeManagerTests.swift`（新增）

第二轮把光斑底、玻璃垫层、玻璃顶栏与标题栏染色一并同步；第三轮修掉装饰层吃掉点击的回归，并把顶栏玻璃收窄到按钮簇、海报玻璃收窄到海报本身，镜像文件增至上表 8 个；第四轮主题拆成「风格 × 配色」两维、设置弹窗全面跟主题后增至 10 个；第五轮为滚动流畅度改了玻璃与卡片的渲染开销，并给手动匹配、手动编辑资料、豆瓣绑定面板补上主题，加上把源文件存在性检查拆成主线程探测点 + 后台 stat 的 `OfflineCacheManager`，增至 14 个。验证：`xcodebuild build -destination 'platform=macOS,arch=x86_64'` 通过；单测与真机截图结论见 ARM 记录（同一份源码，玻璃效果由系统按渲染进程提供，与指令集无关）。

第四轮同步的模型变化：`ThemeType` 收回 5 套纯配色，新增 `ThemeStyle`（经典 / 液态玻璃），主题由 `AppThemeResolver` 按 `appThemeStyle` + `appTheme` 两个 key 解析，老版本存在 `appTheme` 里的 `liquidGlass` 自动迁移成「晶透白 + 液态玻璃」；玻璃的染色、高光、光斑与渐变底由所选配色派生，5 套配色各有一套玻璃。设置弹窗新增「外观与主题」一节（两个下拉 + 仅玻璃风格显示的色卡预览），分组标题、行底色、分隔线、状态色全部改用主题语义色。本目录改动与 ARM `cmp` 一致，x64 `xcodebuild build` 通过；单测与真机截图结论见 ARM 记录第 7 节。
第五轮的 x64 侧只有编译验证：`xcodebuild build -destination 'platform=macOS,arch=x86_64'` 通过；改动本身是 SwiftUI 渲染层与并发调度，与指令集无关，滚动相位回调按系统版本分叉（macOS 15+ 生效，14 维持原画法）。原理、验证与已知代价见 ARM 记录第 8 节。
