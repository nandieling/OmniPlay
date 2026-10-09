# 2026-10-07 与 ARM mac 版 1.8 功能同步（x64 mac 版）

x64 工程此前停在 9 月初的代码线（1.7），本次把 ARM 版 9 月中旬到 10-07 的全部改动同步过来，除签名与打包相关配置外，两侧 `OmniPlay/` 源码现在逐字节一致。

## 同步内容

1. **首页按导演/主演分类（整块新功能）**
   - 新增 `ChineseNameStore.swift`、`PersonProfileStore.swift`；
   - `Movie` 增加 `directors/casts` 字段与 `directorList/castList`、`encodePersonList/decodePersonList/splitPersonNames`；新增 `HomeCategoryMode`、`CategoryFolder`、`PersonProfile`；
   - `AppDatabase` 补 v8（`categoryFolder`）、v9（`personProfile`）、v10（`chineseNameCache`）三个迁移，老库自动升级；
   - 扫描刮削与手动匹配两条路径都改为 `fetchCredits` 抓演职员并入库；豆瓣抓取在影片缺名单时回填；NFO 侧车文件支持导入导出 `directors/casts`；
   - 首页新增分类切换、分类文件夹卡片、随机/自定义文件夹海报、文件夹详情页与人物作品列表、编辑弹窗（含手动改中文名输入框）；影片详情页显示导演/主演，编辑弹窗新增「导演与主演」输入区。
2. **中文译名**：随第 1 项一起生效，外文导演/主演名与分类文件夹作品名自动换中文，结果与「确认无中文译名」一起缓存，启动后后台补齐历史库。
3. **分集剧照刮削引擎重写**：换成 worker 池 + `EpisodeStillFetchOutcome` + `confirmedNoStillFileIDs`/`failedFetchFileIDs` 去重，扫描结束后统一 `enqueueEpisodeStillBackfill`；「本次不刮削」也会补抓剧照，TMDB 连不上时新增「重试扫描」按钮。旧的固定 1 秒 sleep 防封 IP 换成限速队列。
4. **播放器修正**：主线程缓存 `currentTimePosition`（进度条不再卡 0/NaN）、`pendingIsPlayingOverride`（暂停按钮不再抖回）、`playOrPause` 强制主线程执行、蓝光 `bd://` 续播定位不再被文件名匹配丢弃。
5. **TMDB 网络层**：`checkConnection` 换成 `nonisolated static probeConnection`（独立 8s/10s 超时 session），全部 TMDB 请求经 `Task.detached` + `performRequest` 移出主线程，消除刮削前连通性检测期间的指针掉帧；设置页「验证」按钮同样改走后台。

另外按当前设计把海报卡片标题改为居中（`.frame(width: 160, height: 42, alignment: .top)` + 居中对齐），与 ARM 版一致。工程版本升到 1.8、build 号 1（`MARKETING_VERSION` 六个 build configuration 全部改齐，含两个测试 target）。

## 两处工程都改了的地方

`MPVPlayerManager.updatePlaybackState` 原先用 mpv `idle` 属性在播放结束时复位 `isPlaying`，ARM 版在改用 `pause` 属性判定播放/暂停时把这条丢了：播完后 mpv 回到 idle，`pause` 仍读到「未暂停」，`isPlaying` 一直为真，息屏断言不释放。现在两边都用

```swift
let playbackEnded = isIdleActive == 1 && (filename?.isEmpty ?? true)
let observedIsPlaying = isPaused == 0 && !playbackEnded
```

即只在「idle 且没有当前文件」时才当作播放结束，既保住 x64 原有的息屏断言释放，也保留 ARM 版对 `bd://` 等源播放中途误报 idle 的规避。

## 验证

- `xcodebuild -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx ARCHS=x86_64 ONLY_ACTIVE_ARCH=NO -disableAutomaticPackageResolution build` 通过，x86_64 切片零告警；产物 `CFBundleShortVersionString = 1.8`。
- `xcodebuild ... test -destination 'platform=macOS' -only-testing:OmniPlayTests` 36 项全部通过（版本号改齐后重跑一次仍全绿，产物 Info.plist 为 1.8/1），含新迁入的 `ChineseNameTranslationTests` 与剧照文件替换用例，与 ARM 版结果一致。
- 因本机当前无法访问 github.com，x64 工程首次解析 MPVKit 依赖失败，改为复用 ARM 工程 DerivedData 里的 MPVKit checkout 与 artifacts 后离线构建；下次联网时正常解析即可，工程文件未改。

## 与 ARM 版的保留差异

- `OmniPlay.entitlements` / `OmniPlay.iCloud.entitlements` 仍保留 x64 的 `allow-jit`、`allow-unsigned-executable-memory`、`disable-library-validation`（x86_64 跑 libmpv 需要）。
- `Package.resolved` 仍按版本号 `0.41.0` 锁定 MPVKit（与 ARM 同一 revision），`OTHER_LDFLAGS` 保留 `-Wl,-no_fixup_chains`、`-Wl,-unaligned_pointers,suppress`。
- 字符串目录合并了 ARM 新增的 26 个条目，x64 原有的历史条目（含已下线的 OmniPlay Docker 文案）未删。
- ARM 版另有 `README.md`、`docs/` 与若干历史变更日志，x64 未同步。
