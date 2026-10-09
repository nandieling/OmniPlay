# 安卓 TV 版界面重构（v1.11 / versionCode 22）

## 症状与病因

TV 端所有界面都写在一个 8180 行的 `MainActivity` 里，同一件事被手抄了三六遍，这就是「界面混乱、bug 多」的来源：

| 重复的事实 | 抄了几遍 | 后果 |
| --- | --- | --- |
| 遮罩 + 面板 + 焦点保存恢复 | 6（设置、媒体源、登录、目录浏览器、手机输入、文本输入） | 弹层套弹层（登录框里点「手机输入」）时，只有一份的网格备份被内层踩掉，关闭后焦点不知道回哪去 |
| D-pad 路由 | 3（`applyDpadRows` / `handleDpadChain` / 首页 `handleHomeDpadKeyEvent`） | 单格互换的备份恢复不支持嵌套；空行会让按方向键时下标越界 |
| 返回键链 | 2（`onKeyDown` 与 `onBackPressed` 各一份八层判断） | 改一处漏一处 |
| 配色 / 圆角 / 焦点样式 | 每个控件现搓 `GradientDrawable` | 同一个按钮在首页与弹长得不一样的地方随处可见 |
| 整页重建 | 首页任何一次排序 / 筛选 / 搜索按键 | 海报重新加载、滚动位置与焦点手工恢复、搜索框每次按键重建，边打字边丢焦点 |

## 现在的分层

```
ui/Theme.java      配色、4K 缩放、面板形状、焦点样式（按钮 / 行 / 输入框 / 海报卡 / 播放器玻璃）
ui/Widgets.java    控件工厂与字号梯度：button / toolbarButton / optionRow / listRow / input
                   / metaPill / badge / progressBar，控件高度与字号只有一套常量
ui/FocusGrid.java  D-pad 的唯一所有者：网格栈 + 箭头路由 + 焦点滚入可视区
ui/Overlays.java   覆盖层的唯一实现：遮罩、面板、状态行、按钮行、焦点栈、关闭与返回顺序
MainActivity       只剩屏幕本身：登录页、首页、详情页、播放器，以及各自的弹层调用
```

对应 mac 版的 `ThemeManager` / SwiftUI 视图 / 弹层组件，区别只在于输入设备从鼠标键盘换成遥控器。

### FocusGrid

- 界面用 `apply(rows)` 声明自己的网格（行自上而下、行内自左而右），弹层用 `push` / `pop`。
- 栈底始终是界面自己的网格：弹层打开期间后台界面重建网格（局域网扫描边扫边插设备行就会这样）也不会破坏弹层焦点。
- `replaceTop(rows)` 给重建了自身控件的弹层用（目录浏览器换目录），不会污染栈底也不会重复压栈。
- `clean()` 丢掉空行；焦点被重建的列表挤到网格外时按当前真实焦点兜底；`link()` 对列数不同的相邻行做列号钳制。

### Overlays

- `open(Options)` 返回面板，业务只往 `body` 里 `addView`；标题、副标题、状态行（写入才显示、清空就隐藏）、底部按钮行都是现成的。
- `closeTop()` 是返回键唯一会调的关闭动作，弹层套弹层天然按后进先出关闭；`close()` 内统一做「移除视图 + 弹焦点网格 + 还原打开它的控件」，重复调用安全。
- `Options.scrollable=false` 给自带滚动列表的固定高度面板（目录浏览器、手机输入、文本输入），避免两层 ScrollView 抢 D-pad。
- `sideSheet` 让设置与媒体源像 mac 版一样贴右侧成抽屉，登录 / 手机输入 / 文本输入居中。

### 首页增量渲染

`buildHomeShell()` 只建一次（工具栏、状态横幅、滚动容器、控件区、内容区），之后按改动范围分别刷新：

- 排序 / 筛选：只重建选项行与内容区，工具栏与焦点原地不动，选完把焦点交回工具栏对应按钮；
- 输入搜索词：只重建内容区，搜索框不重建，打字过程中焦点不再丢；
- 滚动位置由仍然存在的 ScrollView 自己保持，不再手工记 `scrollY`；
- 返回键收起搜索 / 排序 / 筛选后焦点显式还给工具栏。

### 返回键

`closeTopmostLayer()` 一条链：播放器菜单 → 最上层弹层 → 播放器 → 首页搜索/排序/筛选 → 详情页 → 登录页。`onKeyDown` 与 `onBackPressed` 都只调它，`KEYCODE_ESCAPE` 与返回键等价。屏幕整体切换（登录页 / 详情 / 播放器 / 加载中 / 错误页）先 `closeAllOverlays()`，弹层不会活在自己所属视图树被拆掉之后。

## 顺带修掉的行为问题

- 设置面板改完一个开关会跳回面板顶部，遥控器要点七八下才能改下一项：`restartSettingsOverlay()` 现在记住当前聚焦的选项文字（忽略 `✓` 前缀），重画后把焦点放回原选项并滚到可见。
- 弹层状态行以前是一个常驻的空 `TextView`，弹层顶部总留着一条空隙；现在写入内容才显示、清空就隐藏。
- 媒体源面板在「启用 / 停用 / 移除 / 重命名」后要重画面板：旧代码靠「先关再开」，新实现显式重建，列表与状态行同步（状态行原本会被同一个父视图 `addView` 两次，直接抛 `IllegalStateException`）。
- 设置面板里写死的「版本 1.8」改为读包信息。
- 评分色与豆瓣分色改用 `Theme.RATING` / `Theme.RATING_DOUBAN`，与 mac 版一致。
- 播放器按钮、图标、音轨 / 字幕菜单行统一走 `Theme.stylePlayer*Focus`，玻璃层颜色只有一处定义。

## 验证

- `javac` 全量语法检查（android-35 + jcifs + zxing）通过；`ui/` 四个类没有 JVM 桩可跑，只能靠编译与真机验证。
- 引擎侧回归：`/tmp/lanprobe`（30 项）、`run-webdav.sh`（12）、`run-smb.sh`（4）、`run-saved.sh`（32）、`run-phone.sh`（24）全部通过；本次只动界面层，`local/` 未改。
- `./gradlew assembleRelease --offline` 通过；`dist/OmniPlay-TV-v1.11-arm64.apk` 经 `aapt2 dump badging` 确认 `versionCode='22' versionName='1.11'`，`dexdump` 确认 `classes.dex` 内含 `ui/Theme`、`ui/Widgets`、`ui/FocusGrid`、`ui/Overlays`。
- 未做设备端验证：本机没有连接 ADB 设备。遥控器实际手感（焦点顺序、抽屉宽度、目录浏览器行高）需真机再过一遍。

## 仍未对齐 mac 版的部分

Plex / Emby / Jellyfin 媒体服务器、豆瓣绑定与刮削、iCloud 同步、分集剧照手动替换与季 / 集覆盖编辑、演员 / 导演聚合页、主题切换、开源组件页。这些在 TV 上是「需要新做的界面」而不是「焦点错乱的旧界面」，等这版结构定型后逐个补。
