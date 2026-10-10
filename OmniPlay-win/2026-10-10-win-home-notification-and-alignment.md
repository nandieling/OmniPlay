# Windows 版首页提示位置与对齐修正（2026-10-10）

## 1. 提示信息统一显示在左上角，并会自动清除

- 首页顶部的扫描/刮削状态文案、离线缓存进度条、以及右上角的全局 Toast 全部改到左上角：状态栏与缓存进度合并进同一列（`PosterWallView.axaml` 顶部 `Grid ColumnDefinitions="*,Auto"` 的 0 列），Toast 容器改为 `HorizontalAlignment=\"Left\" Margin=\"52,96,0,0\"`，与内容区 52 的左边距对齐、并落在标题行下方，不会压住状态文案。
- 状态栏文案限制两行（`MaxLines=\"2\"` + 省略号），避免长文案把顶部区域撑高。
- 新增自动清除：`PosterWallViewModel.ScheduleStatusMessageAutoClear()` 在每次 `StatusMessage` 变化时重置计时，`ClearStatusMessageAfterDelayAsync` 只在扫描未进行且文案没被新进度覆盖时清空。也就是说扫描/刮削进行中一直显示，没有新进度满 12 秒后自动收起。时长写在 `PosterWallViewModel.StatusMessageAutoClearDelay`（`internal`，测试可改）。
- 扫描进行中不会清除；扫描结束那一刻重新计时。
- 顶部右上角从此只剩工具栏按钮。

## 2. 海报墙与标题、分类封面左对齐（对齐问题的根因）

Avalonia 的 `WrapPanel` 会把每个条目在 `ItemWidth` 槽位内居中。槽位比卡片宽出半个间距（实测截图：卡片 188、槽位 204），于是整面墙相对标题右移了半个间距（8dp，1.25 倍缩放下 10 物理像素），这就是「所有影视」「继续播放」文字与首列海报左边缘对不上、分类详情人物图与下方海报对不上的原因。

- `PosterWallView.axaml.cs` 新增 `LibraryWallMargin`，在 `UpdateLibraryPosterItemWidth()` 里按 `-(itemWidth - cardWidth) / 2` 赋值。
- 海报墙 `LibraryItemsControl` 与骨架屏 `LibrarySkeletonPanel` 绑定该负左边距，首列海报回到与标题同一条左边缘；分类详情头部与人物封面同处这条线上。

## 3. 分类文件夹卡片的名称与数量居中

卡片名称 + 数量原本靠 `Button.HorizontalContentAlignment` 居中，测量链路里任何一层把内容宽度收成内容自身宽度都会让居中失效（这也是之前反复调不好、截图里数量还会掉到第二行的原因）。

现在改成外层固定为海报宽度的 `Grid Width={LibraryPosterCardWidth}`，内部横向 `StackPanel HorizontalAlignment=\"Center\"`：居中基准恒等于海报宽度，名称与数量始终同一行。

## 4. 分类详情页头部重排

- 头部从「标题 Grid 的 Auto 列里再套横向 StackPanel」改为独立的整行 `Grid ColumnDefinitions=\"Auto,*\"`。旧结构里简介处在一个 Auto 列中且带 `MaxWidth=\"1040\"`，右侧再宽也用不上，这就是简介右边大片空白的原因。
- 返回键放在人物图正上方并 `HorizontalAlignment=\"Center\"`，与图片之间留 16 的间距（原先紧贴图片左上角）。
- 六列作品年表铺满人物图右侧的全部剩余宽度，列间距 18；年表行的年份列收窄到 34 且右对齐，片名与评分紧跟其后。

## 验证

- `dotnet build src/OmniPlay.UI/OmniPlay.UI.csproj -c Release`：0 错误，警告数量与改动前持平（均为既有 nullable 告警）。
- `dotnet test tests/OmniPlay.Tests -c Release`：317 通过 / 1 失败，唯一失败是既有的 `TmpDbDumpTests.DumpLiveData`（依赖固定的 `tmp/live1.sqlite` 与硬编码的 Windows 路径，属环境问题）。
- 新增两条测试覆盖自动清除：`HomeStatusMessage_AutoClearsAfterScanIdle`、`HomeStatusMessage_KeptWhileScanRunningAndClearedAfterItEnds`（为此把 `OmniPlay.Core` 加了 `InternalsVisibleTo OmniPlay.Tests`）。
- `./package-setup.sh` 重新生成安装包与便携版，载荷完整性与 PE 架构校验通过。

## 5. 第二轮修正：继续播放对齐、单行提示、详情页不提示

- 继续播放区的卡片外格宽度是 `LibraryPosterItemWidth`，里面的卡列没有声明对齐方式，和海报墙一样被居中到单元格中央，于是整行比海报墙多了半个间距。现在卡列显式 `HorizontalAlignment="Left"`，两排卡片共用同一套列位置（首列 0，间距 = `LibraryPosterItemWidth`）。
- 左上角提示改为只占一行：状态文案 `MaxLines` 从两行改为单行 + 省略号；离线缓存进度条与两条缓存文案合并进同一横向行，不再换到第二行。
- 提示内容精简在显示层做（`PosterWallViewModel.SummarizeHomeStatus`）：丢弃「另有 N 条扫描诊断…」尾巴，多条拼接时只保留最新一段（按 。／；切分），最长 56 字。同时扫描完成与后台扫描完成不再把旧提示拼接进新提示。`StatusMessage` 本身仍保留完整信息（诊断统计测试依赖它）。
- Toast 通知加上 `IsVisible="{Binding !IsDetailOpen}"`，进入详情页后不再出现；首页状态栏本来就在首页视图内，详情页不会显示。

验证（第二轮）：UI 编译 0 错误；`dotnet test` 318 通过 / 1 失败（仍是环境性的 `TmpDbDumpTests.DumpLiveData`）；新增 `HomeStatusMessage_KeepsSingleShortLine` 覆盖单行精简逻辑；安装包与便携版重新生成、PE 校验通过。
