# 2026-09-06 分集剧照刮削修复（ARM mac 版）

## 问题

- 点右上角扫描按钮后，分集剧照不生成，也没有任何提示。

## 原因

1. **提前返回跳过了剧照补抓**：扫描完成后，只要没有待刮削的 `unmatched` 文件（老库已全部刮削过），流程直接 `return` 结束，"补齐分集剧照"这一步永远不会执行。这是"扫描按钮没用"的直接原因。
2. **失败黑名单不可恢复**：旧逻辑把"TMDB 确实没有该剧照"和"网络/服务端临时失败"都写进同一个 `ThumbnailTMDBFailedFileIDs` 数组，且非强制重试时永不重试。一次网络抖动（代理网关超时等）后，分集剧照就永久丢失，机器上已累积 893 条黑名单。
3. **404 被当成临时失败**：TMDB 中不存在的季/集（本地文件名 S/E 与 TMDB 对不上）返回 404，旧代码按"抓取失败"记录，每轮扫描重复打 API 且永远拿不到。
4. **无反馈**：补抓失败、队列状态对用户完全不可见，无法定位问题。

## 修复内容

- 扫描流程即使没有待刮削内容，也会执行分集剧照补抓（`PosterWallView.enqueueEpisodeStillBackfill`）。
- `ThumbnailManager` 重写抓取引擎：
  - 3 路并发抓取，替代原来的单任务串行（原串行每集还要额外 sleep 1 秒）；
  - 区分三种结果：成功 / TMDB 确认无剧照（含 404）/ 抓取失败；
  - "确认无剧照"写入 `ThumbnailTMDBConfirmedNoStillFileIDs` 长期跳过；"抓取失败"写入 `ThumbnailTMDBFailedFetchFileIDs` 并记录原因，下一轮扫描自动重试；
  - 旧黑名单自动迁移：893 条旧记录按"抓取失败"处理，允许本轮重试一次后正确归类。
- TMDB 连接失败弹窗新增"重试扫描"按钮，"本次不刮削"也会继续补抓剧照。
- 每轮补抓入队前输出 `[OmniPlay][剧照]` 诊断日志（待处理/确认无/待重试数量），进度条显示剩余数量。

## 验证

- `xcodebuild -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx build` 通过。
- `xcodebuild ... test -destination 'platform=macOS' -skip-testing:OmniPlayUITests` 全部通过（含新增 `thumbnailManagerReplacesAndRemovesEpisodeStillFiles` 回归测试）。
- 实机验证：安装后自动扫描，剧照缓存从 30 张持续增长到 2500+ 张，失败原因计数归零，TMDB 代理网关（api 4~6s 延迟）下 404 正确归类为"确认无剧照"。
- UI 测试 target（OmniPlayUITests-Runner）在本机因系统认证问题无法启动，与本次改动无关。
